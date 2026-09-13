// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/EquipmentFleetProvider.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;                  // EquipmentDataContext / EquipmentCategory / EquipmentStatus
using PitMine3D.Kylin.Cad.Units;             // Machine / RateRecord / FleetPairing / EquipmentAssigner
using DbEq = PitMine3D.Kylin.Data.Entities.Equipment;
using DbEm = PitMine3D.Kylin.Data.Entities.EquipmentModel;
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  equipment / equipment_model / capacity_monthly / equipment_kpi_monthly /
//  dispatch_rule  →  EquipmentAssigner 的 POCO 输入。
//
//  为什么这一层长在 PlanLib 而不是 MineAssLib：
//    指派引擎是纯算法、脱 GUI、可离线跑判据；它不该知道有数据库这回事。
//    （MineAssLib.csproj 确实引了 GeoDataBase —— 那是台阶模板复用参数模板库留下的口子，
//      不是给排产用的。多一处引服务层，以后就多一处「谁先初始化」的运行期耦合。）
//
//  ★ 三件事必须在这一层做完，且只在这一层做一次：
//    ① 单位换算：capacity_monthly.output_m3 是【月】量，除以本月作业日 → 日台效 m³/日；
//                equipment_model.std_daily_cap_wan_m3 是【万m³/日】，×1e4 → m³/日。
//                除数 / 乘数一律写进 RecordKey，追得到。
//    ② 实测 vs 缺省的标记：capacity_monthly = 实测；equipment_model 字典 = 缺省。
//                两者混在一个 List<RateRecord> 里，靠 Measured 分层，别在别处再判一次。
//    ③ 拒收：output_m3 ≤ 0 的行不当 0 用，直接不出记录（让台效回退到下一级）。
//
//  ★ 曾经错了 12 倍的那个口径（2026-08-20 定论，已由 V050 迁移改数）：
//    源表（7.1\*设备能力分析.xlsx「2026设备能力」）的逐月列不是「当月产量」，
//    是【当月实际折算出来的**台年**能力，万m³/台·年】—— 同工作簿另一个 sheet 的同列
//    直接叫「计划台年能力」。V021 把它 ×1e4 当成了 m³/月（原表 DMH90 270.7 → 库 2,707,200，
//    逐型号逐字节相同），于是 ÷25 作业日之后一台 4100XPC 得 357,600 m³/日 ——
//    该机的物理极限（斗容 60.6、回转 0.53min、24h 不停）才 93,000。
//    V050 已把 capacity_monthly / std_daily_cap_wan_m3 / production_record 一律 ÷12。
//
//    ⇒ 本适配照旧【照单全收，不在这一层缩放】：量级是数据的事，在迁移里一次改对，
//      不在取数层偷偷乘除（两处各乘一次谁也看不出来）。临时试口径才用
//      EquipmentAssignInput.CapacityScale 那一个数。
//    ⇒ CaliberRisk + 引擎侧 AuditLoaderDayLo/Hi 的量级体检继续留着：
//      再有一批台年口径的数据进来，它们是唯一会当场喊出来的东西。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次取数的结果 —— 数据本身 + 「哪来的、丢了多少、为什么」。</summary>
public sealed class FleetResolution
{
    public List<Machine> Machines { get; } = new();
    public List<RateRecord> Rates { get; } = new();
    public List<FleetPairing> Pairings { get; } = new();

    /// <summary>面向用户的说明 —— 每一条都是适配层替用户做的决定。</summary>
    public List<string> Notes { get; } = new();

    /// <summary>数据库没就绪 / 读不出来。此时三个清单都是空的。</summary>
    public bool DatabaseReady { get; internal set; }

    /// <summary>在册台数 / 可派台数 / 有实测台效的台数。</summary>
    public int OnRoll, Dispatchable, WithMeasuredRate;

    /// <summary>拒收的产能行（≤0 / 非数）。</summary>
    public int RejectedCapacityRows;

    /// <summary>口径风险提示（空 = 没发现）。<b>不是错误，是必须有人认一次的事</b>。</summary>
    public string KnownCaliberRisk = "";

    public string Summary()
        => DatabaseReady
           ? $"在册 {OnRoll} 台 · 可派 {Dispatchable} 台 · 有实测台效 {WithMeasuredRate} 台 · "
             + $"台效记录 {Rates.Count} 条（实测 {Rates.Count(r => r.Measured)}）· 编组规则 {Pairings.Count} 条"
           : "设备库未就绪 —— 一台设备都读不出来。";
}

/// <summary>
/// 设备维取数。<b>全程 try/catch</b>：GeoDataBase 没就绪不许让排产崩，降级并留条即可。
/// </summary>
public static class EquipmentFleetProvider
{
    /// <summary>电铲的实方日产工程量级（m³/台·日）—— 只用来判「口径要不要有人认一次」，<b>不参与算量</b>。</summary>
    public const double PlausibleLoaderDayLo = 3000, PlausibleLoaderDayHi = 60000;

    /// <summary>钻机的<b>控制方量</b>日台效工程量级（m³/台·日）。
    /// <para>孔网 7×8m、台阶 15m ⇒ 每孔控制约 840m³；钻速 28m/h、孔深约 16.5m ⇒ 一天几十个孔，
    /// 量级落在万级。带取得比这个宽，只为抓「差一个数量级」。</para></summary>
    public const double PlausibleDrillDayLo = 2000, PlausibleDrillDayHi = 80000;

    /// <summary>推土机的<b>排弃占容</b>日台效工程量级（m³/台·日）。
    /// <para>现场工序定额给的是 250m³/h ⇒ 三班约 4500m³/日；带取宽。</para></summary>
    public const double PlausibleDozerDayLo = 1000, PlausibleDozerDayHi = 40000;

    /// <summary>
    /// 读一次设备维。
    /// </summary>
    /// <param name="year">目标年。</param>
    /// <param name="month">目标月（1–12）。</param>
    /// <param name="workdays">本月<b>作业日</b>数 —— 月量除以它得日台效。取自 <c>FieldParams.WorkdaysFor</c>。</param>
    /// <param name="lookbackMonths">这台设备本月没记录时，往前找几个月凑中位数。0 = 用它全部历史。</param>
    public static FleetResolution Load(int year, int month, double workdays, int lookbackMonths = 12)
    {
        var res = new FleetResolution();
        if (!(workdays > 0))
        {
            res.Notes.Add($"◆ 作业日填的是 {workdays:0.##}（须为正）—— 月量没法折成日台效，本次不取数。");
            return res;
        }

        List<DbEq> eq;
        try { eq = EquipmentDataContext.Equipment.All().ToList(); }
        catch (Exception ex)
        {
            res.Notes.Add("◆ 设备库未就绪，设备维取不到数：" + ex.Message
                        + "（先打开一次「数据库」模块，或确认 GeoDataBasePlugin 已初始化）。");
            return res;
        }
        res.DatabaseReady = true;
        res.OnRoll = eq.Count;

        // ── ① 设备清单 ─────────────────────────────────────────────────
        //    完好率不在这里补：KPI 服务只有逐台查询，518 台逐台打库会卡住界面线程。
        //    它也不参与算量，所以做成【调用方按需再补】（FillAvailability）。
        int badCat = 0, notInUse = 0;
        foreach (var e in eq)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.EquipmentId)) continue;
            if (!Enum.TryParse<EquipmentCategory>(e.Category, true, out var cat)) { badCat++; cat = EquipmentCategory.Other; }

            // 「在用」才可派。状态列存的是中文，枚举名是英文 —— 两种都认，认不出的按不可派（保守）。
            bool inUse = IsInUse(e.Status);
            if (!inUse) notInUse++;

            res.Machines.Add(new Machine
            {
                MachineId = e.EquipmentId,
                Kind = MapKind(cat),
                Model = e.Model ?? "",
                Dispatchable = inUse,
                HomeArea = e.OperatingArea ?? "",
                AvailabilityPct = -1,                  // −1 = 没读（不是 0）；要就调 FillAvailability
            });
        }
        res.Dispatchable = res.Machines.Count(m => m.Dispatchable);
        if (badCat > 0) res.Notes.Add($"⚠ {badCat} 台设备的类别列解析不出来，已按「其他」处理（这些设备不会被派）。");
        if (notInUse > 0) res.Notes.Add($"· {notInUse} 台设备状态不是「在用」（待报废/报废/租赁/退租/空），本次不可派。");
        res.Notes.Add("· 完好率本次没读（−1 = 没读，不是 0）—— 它不参与算量：实测月产量已含当月完好率，"
                    + "再乘一次是重复打折。要在报表里显示就单独调 FillAvailability。");

        // ── ② 台效：实测（capacity_monthly）────────────────────────────
        List<PitMine3D.Kylin.Data.Entities.CapacityMonthly> caps;
        try { caps = EquipmentDataContext.Production.MonthlyAll().ToList(); }
        catch (Exception ex) { caps = new(); res.Notes.Add("⚠ 月度产能表读不出来（" + ex.Message + "）—— 台效将全部走型号缺省。"); }

        var kindById = res.Machines.ToDictionary(m => m.MachineId, m => m.Kind, StringComparer.Ordinal);
        var modelById = res.Machines.ToDictionary(m => m.MachineId, m => m.Model, StringComparer.Ordinal);
        int lo = lookbackMonths > 0 ? year * 12 + month - lookbackMonths : int.MinValue;
        var measured = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in caps)
        {
            if (c == null || string.IsNullOrWhiteSpace(c.EquipmentId)) continue;
            if (!kindById.TryGetValue(c.EquipmentId, out var kind)) continue;      // 不在册的产能行，跳过
            if (c.Month is < 1 or > 12) continue;                                   // 0 = 年汇总，不是月量
            int key = c.Year * 12 + c.Month;
            if (key > year * 12 + month) continue;                                  // 未来月不当实测用
            if (key < lo) continue;
            if (!(c.OutputM3 > 0) || double.IsNaN(c.OutputM3) || double.IsInfinity(c.OutputM3))
            { res.RejectedCapacityRows++; continue; }                               // ≤0 不当 0 用，直接不出记录

            res.Rates.Add(new RateRecord
            {
                MachineId = c.EquipmentId,
                Model = modelById.TryGetValue(c.EquipmentId, out string? md) ? md : "",
                Kind = kind,
                Year = c.Year, Month = c.Month,
                M3PerDay = c.OutputM3 / workdays,
                Measured = true,
                RecordKey = $"capacity_monthly[{c.EquipmentId},{c.Year},{c.Month:00}]"
                          + $"={c.OutputM3.ToString("0", CultureInfo.InvariantCulture)}m³/月 ÷ {workdays:0.#} 作业日",
            });
            measured.Add(c.EquipmentId);
        }
        res.WithMeasuredRate = measured.Count;
        if (res.RejectedCapacityRows > 0)
            res.Notes.Add($"⚠ {res.RejectedCapacityRows} 条月度产能是 0/负/非数，已<b>不出台效记录</b>"
                        + "（让它回退到型号缺省），而不是按 0 台效排 —— 按 0 排等于让这台设备在岗却永远干不出量。");

        // ── ③ 台效：型号字典缺省（equipment_model.std_daily_cap_wan_m3）──
        List<DbEm> models;
        try { models = EquipmentDataContext.Models.All().ToList(); }
        catch { models = new(); }
        int modelWithDefault = 0;
        foreach (var m in models)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.Model)) continue;
            if (!(m.StdDailyCapWanM3 > 0)) continue;
            if (!Enum.TryParse<EquipmentCategory>(m.Category, true, out var cat)) cat = EquipmentCategory.Other;
            res.Rates.Add(new RateRecord
            {
                MachineId = "", Model = m.Model, Kind = MapKind(cat),
                Year = 0, Month = 0,
                M3PerDay = m.StdDailyCapWanM3.Value * 1e4,
                Measured = false,                                   // ★ 字典缺省，不是实测
                RecordKey = $"equipment_model[{m.Model}].std_daily_cap_wan_m3"
                          + $"={m.StdDailyCapWanM3.Value.ToString("0.####", CultureInfo.InvariantCulture)}万m³/日 ×1e4（缺省，非实测）",
            });
            modelWithDefault++;
        }
        int modelNoDefault = models.Count - modelWithDefault;
        if (modelNoDefault > 0)
            res.Notes.Add($"· 型号字典 {models.Count} 条，其中 {modelNoDefault} 条没有标准日产能"
                        + "（推土/平路/洒水那几类整类都没有）—— 这些型号的设备只能靠实测台效，实测也没有就不可派。");

        // ── ④ 编组规则（dispatch_rule）─────────────────────────────────
        try
        {
            foreach (var d in EquipmentDataContext.Dispatch.All(activeOnly: true))
            {
                if (d == null || string.IsNullOrWhiteSpace(d.ShovelModel) || string.IsNullOrWhiteSpace(d.TruckModel)) continue;
                res.Pairings.Add(new FleetPairing
                {
                    LoaderModel = d.ShovelModel, TruckModel = d.TruckModel,
                    TruckCount = d.RecommendedTruckCount,
                    CycleTimeMin = d.CycleTimeMin,
                    EfficiencyScore = d.EfficiencyScore,
                });
            }
        }
        catch (Exception ex) { res.Notes.Add("⚠ 编组规则读不出来（" + ex.Message + "）—— 卡车按缺省配比配，且任意车型都算能编上组。"); }

        // ── ⑤ 口径体检：把「必须有人认一次」的事摆出来，不替现场决定 ────────
        res.KnownCaliberRisk = CaliberRisk(res);
        if (res.KnownCaliberRisk.Length > 0) res.Notes.Add("◆ " + res.KnownCaliberRisk);

        res.Notes.Add($"· 台效口径：capacity_monthly 是【月】量，已 ÷ {workdays:0.#} 个作业日折成日台效；"
                    + "该月量本身是实测达成值（已含当月完好率/实动率），所以完好率不再乘第二遍。");
        return res;
    }

    /// <summary>把取数结果装进指派引擎的输入（其余参数由调用方填）。</summary>
    public static EquipmentAssignInput ToInput(FleetResolution res, IEnumerable<UnitAssignment> units,
                                               IEnumerable<UnitSite>? sites,
                                               int year, int month, int workdays, double shiftsPerDay = 3)
    {
        var inp = new EquipmentAssignInput
        {
            Units = units?.ToList() ?? new List<UnitAssignment>(),
            Sites = sites?.ToList() ?? new List<UnitSite>(),
            Machines = res?.Machines ?? new List<Machine>(),
            Rates = res?.Rates ?? new List<RateRecord>(),
            Pairings = res?.Pairings ?? new List<FleetPairing>(),
            Year = year, Month = month,
            WorkdayCount = Math.Max(1, workdays),
            ShiftsPerDay = shiftsPerDay > 0 ? shiftsPerDay : 3,
            AuditLoaderDayLo = PlausibleLoaderDayLo,
            AuditLoaderDayHi = PlausibleLoaderDayHi,
        };
        return inp;
    }

    /// <summary>
    /// 打开穿孔 / 排土两个工序（缺省两个都关，见 <see cref="EquipmentAssignInput"/> 的说明）。
    ///
    /// <para><b>为什么单独一个方法而不是 ToInput 的参数</b>：这两个开关会整体改变采装的量
    /// （穿爆超前把开工日往后顶），打开它是一次<b>口径决定</b>，该在调用处看得见 ——
    /// 藏在一串默认参数里，下一个人不会知道自己打开了什么。</para>
    ///
    /// <para><b>本方法不造台效</b>：钻机与推土机的台效仍然只走 <c>capacity_monthly</c> 实测 →
    /// <c>equipment_model</c> 字典这条既有回退链。推土机在型号字典里整类没有标准日产能，
    /// 所以库里没有它的实测台效时它就是不可派 —— 引擎会如实报，<b>不拿工序定额里那个
    /// 250 m³/h 的样例值顶上</b>（那张表现在还是写死的样例、"持久化待接"，
    /// 拿它当台效就是编数）。</para>
    /// </summary>
    /// <param name="noBlastUnitIds">免爆单元（表土 / 风化层）。null 或空 = 所有岩单元都按需爆破算（偏保守）。</param>
    /// <param name="drillsPerUnit">一个单元最多摆几台钻机。</param>
    /// <param name="dozersPerSink">一个排土位置同一天最多摆几台推土机。</param>
    public static void EnableDrillingAndDozing(EquipmentAssignInput inp, FleetResolution res,
                                               bool drilling, bool dozing,
                                               int blastLeadDays = 2,
                                               IEnumerable<string>? noBlastUnitIds = null,
                                               int drillsPerUnit = 1, int dozersPerSink = 2)
    {
        if (inp == null) return;
        inp.ScheduleDrilling = drilling;
        inp.ScheduleDozing = dozing;
        inp.BlastLeadDays = Math.Max(0, blastLeadDays);
        inp.MaxDrillsPerUnit = Math.Max(1, drillsPerUnit);
        inp.MaxDozersPerSink = Math.Max(1, dozersPerSink);
        inp.NoBlastUnitIds = noBlastUnitIds == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(noBlastUnitIds.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.Ordinal);

        // 量级体检带：与挖装那条同源 —— 一个量一个来源，都从本类的常量出，
        // 别让引擎自己的缺省和这里的常量各活各的（那样改一处不生效，且没人报错）。
        inp.AuditDrillDayLo = PlausibleDrillDayLo; inp.AuditDrillDayHi = PlausibleDrillDayHi;
        inp.AuditDozerDayLo = PlausibleDozerDayLo; inp.AuditDozerDayHi = PlausibleDozerDayHi;

        // 打开之前先说清楚这一轮到底有没有设备可派 —— 开关开着而一台都没有，
        // 结果会是"全线欠产"，那不是算法保守，是设备维真的没数。
        if (res == null || !res.DatabaseReady) return;
        if (drilling)
        {
            int n = res.Machines.Count(m => m.Kind == MachineKind.Drill && m.Dispatchable);
            res.Notes.Add(n > 0
                ? $"· 已打开穿孔排产：在册可派钻机 {n} 台。"
                : "◆ 已打开穿孔排产，但<b>在册可派钻机 0 台</b> —— 所有需爆破的岩单元都会开不了挖。");
        }
        if (dozing)
        {
            int n = res.Machines.Count(m => m.Kind == MachineKind.Dozer && m.Dispatchable);
            int withRate = res.Rates.Count(x => x.Kind == MachineKind.Dozer && x.M3PerDay > 0);
            res.Notes.Add(n > 0 && withRate > 0
                ? $"· 已打开排土排产：在册可派推土机 {n} 台，有台效记录 {withRate} 条。"
                : $"◆ 已打开排土排产，但推土机可派 {n} 台 / 台效记录 {withRate} 条 —— "
                  + "推土机在 equipment_model 里整类没有标准日产能，只能靠 capacity_monthly 的实测；"
                  + "两头都没有就不可派，排土这一侧会全线欠产。");
        }
    }

    /// <summary>
    /// 把「确定开采程序」的<b>面级型号配置</b>摊到单元上，喂给指派引擎当硬约束。
    ///
    /// <para><b>单元归哪个面，必须由调用方给</b>（<paramref name="unitToFace"/>：单元号 → 作业面名）。
    /// 这一层<b>不猜</b> —— 按标高猜、按带号猜都能写出来，猜出来的归属每一项校核都会是 ✓，
    /// 而它对应不上现场任何一个面。归属错了的后果是"这个面的铲跑到别的面去了"，
    /// 在报表上完全看不出来。</para>
    ///
    /// <para>⚠ <b>这条链目前缺最后一环</b>：<c>UnitAssignment</c> 上没有作业面归属字段，
    /// 上游「采掘单元清单」也还没有产出这份对应关系。在它补上之前，
    /// 调用方给不出 <paramref name="unitToFace"/> ⇒ 本方法返回 0 条约束并如实留条，
    /// 而不是悄悄按某种规则凑一份出来。</para>
    /// </summary>
    /// <returns>写进 <c>inp.Pins</c> 的条数。</returns>
    public static int ApplyFacePins(EquipmentAssignInput inp,
                                    IEnumerable<WorkingFace>? faces,
                                    IReadOnlyDictionary<string, string>? unitToFace,
                                    List<string>? notes = null)
    {
        if (inp == null) return 0;
        inp.Pins = new Dictionary<string, UnitFacePin>(StringComparer.Ordinal);

        var faceList = faces?.Where(f => f != null).ToList() ?? new List<WorkingFace>();
        var configured = faceList.Where(f => f.EquipConfiguredCount > 0 || f.TrucksPerLoader > 0).ToList();
        if (configured.Count == 0)
        {
            notes?.Add("· 面级设备型号：一个面都没配 ⇒ 排产在全矿在册设备里挑（这是缺省，不是缺陷）。");
            return 0;
        }

        if (unitToFace == null || unitToFace.Count == 0)
        {
            notes?.Add($"◆ 有 {configured.Count} 个作业面钉了设备型号，但<b>没有单元→作业面的对应关系</b>，"
                     + "这批约束这一轮<b>一条都没生效</b>。"
                     + "（UnitAssignment 上没有作业面归属字段，上游「采掘单元清单」也还没产出这份对应。）"
                     + "本层<b>不按标高/带号猜归属</b> —— 猜错了的后果是这个面的设备跑到别的面去，"
                     + "而报表上完全看不出来。");
            return 0;
        }

        var byName = new Dictionary<string, WorkingFace>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in faceList) byName[f.Name ?? ""] = f;

        int n = 0, miss = 0;
        foreach (var kv in unitToFace)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            if (!byName.TryGetValue(kv.Value ?? "", out var f)) { miss++; continue; }
            var pin = new UnitFacePin
            {
                FaceName = f.Name ?? "",
                DrillModel = (f.DrillModel ?? "").Trim(),
                LoaderModel = (f.LoaderModel ?? "").Trim(),
                TruckModel = (f.TruckModel ?? "").Trim(),
                DozerModel = (f.DozerModel ?? "").Trim(),
                TrucksPerLoader = Math.Max(0, f.TrucksPerLoader),
                // 工艺维走同一份归属 —— 拆开摊会出现"型号按 A 面、超前期按 B 面"，两边各自都对。
                BlastLeadDays = Math.Max(0, f.Process?.BlastLeadDays ?? 0),
            };
            if (!pin.Any) continue;
            inp.Pins[kv.Key] = pin;
            n++;
        }

        notes?.Add($"· 面级设备型号：{configured.Count} 个面配了型号，摊到 {n} 个单元上当硬约束。"
                 + (miss > 0 ? $" ◆ 另有 {miss} 个单元的归属面在作业面清单里查不到，这些单元不受约束。" : ""));
        return n;
    }

    /// <summary>
    /// 一次把「确定开采程序」的面级配置<b>整套</b>接到排产输入上：
    /// 归属解算（<see cref="FaceUnitResolver"/>）→ 型号约束 → 免爆单元清单 → 打开穿孔/排土。
    ///
    /// <para><b>为什么合成一个入口</b>：这四件事必须用<b>同一份归属</b>。
    /// 分开调用的话，型号约束按一份归属摊、免爆清单按另一份算，
    /// 两份都各自"正确"，合起来就是"这个面按免爆算、它的铲却按需爆破的面配的"——
    /// 而两边都不会报错。</para>
    /// </summary>
    /// <param name="units">本月单元（要 ZLo/ZHi/Kind 做归属，所以吃 MineUnit）。</param>
    /// <param name="faces">作业面清单（<c>ShortTermSchemeStore.Base.Faces</c>）。</param>
    /// <param name="manualFaceOf">手工归属覆盖：单元号 → 作业面名（FA1）。可空。</param>
    /// <returns>归属解算结果 —— <b>调用方必须把它的 Notes 显示出来</b>，匹配率低是事实不是错误，但要看得见。</returns>
    public static FaceUnitResolution ApplyMiningProgram(EquipmentAssignInput inp,
                                                        FleetResolution res,
                                                        IEnumerable<MineUnit>? units,
                                                        IEnumerable<WorkingFace>? faces,
                                                        bool drilling, bool dozing,
                                                        int blastLeadDays = 2,
                                                        IReadOnlyDictionary<string, string>? manualFaceOf = null,
                                                        int drillsPerUnit = 1, int dozersPerSink = 2)
    {
        var fu = FaceUnitResolver.Resolve(units, faces, manualFaceOf);
        if (inp == null) return fu;

        // ① 型号约束（用这份归属）
        ApplyFacePins(inp, faces, fu.UnitToFace, res?.Notes);

        // ② 开工序 + 免爆清单（用【同一份】归属，FA7）
        EnableDrillingAndDozing(inp, res, drilling, dozing, blastLeadDays,
                                fu.NoBlastUnits, drillsPerUnit, dozersPerSink);

        // ③ 归属本身的结论也要进 Notes —— 匹配率 60% 和 100% 排出来的班表差别很大，
        //    而在结果界面上长得一模一样。
        if (res != null) res.Notes.AddRange(fu.Notes);

        if (drilling && fu.NoBlastUnits.Count == 0 && fu.MatchedCount > 0)
            res?.Notes.Add("· 免爆单元 0 个：配上面的单元里，没有哪个面的物料是「不需爆破」的"
                         + "（表土/风化层面还没建，或都按岩/煤填了物料）。"
                         + "所有岩单元都会按需穿爆算，钻机需求偏高。");
        return fu;
    }

    /// <summary>从排产用的 <see cref="MineUnit"/> 取质心，喂给转场距离。<b>一个量一个来源</b>：
    /// 单元位置只从这里出，别在窗口里再拼一份。</summary>
    public static List<UnitSite> SitesFrom(IEnumerable<MineUnit>? units)
        => (units ?? Enumerable.Empty<MineUnit>())
           .Where(u => u != null && u.UnitId.Length > 0)
           .Select(u => new UnitSite { UnitId = u.UnitId, Cx = u.Cx, Cy = u.Cy, Cz = u.Cz })
           .ToList();

    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 补完好率（<c>equipment_kpi_monthly.availability</c>）。<b>只为显示</b>，不参与算量。
    /// <para>单独一个方法而不是在 <see cref="Load"/> 里顺手做：KPI 服务只有逐台查询，
    /// 518 台逐台打库在窗口线程上会卡；要不要补由调用方决定。</para>
    /// </summary>
    public static void FillAvailability(FleetResolution res, int year, int month)
    {
        if (res == null || !res.DatabaseReady) return;
        int hit = 0;
        foreach (var m in res.Machines)
        {
            try
            {
                var k = EquipmentDataContext.Kpi.Get(m.MachineId, year, month);
                if (k != null && k.Availability > 0) { m.AvailabilityPct = k.Availability * 100.0; hit++; }
            }
            catch { /* 单台读不到不影响其余 */ }
        }
        res.Notes.Add($"· 完好率补了 {hit}/{res.Machines.Count} 台（{year}-{month:00}）—— 只做显示，不参与算量。");
    }

    private static string CaliberRisk(FleetResolution res)
    {
        var loaderRates = res.Rates
            .Where(r => r.Measured && (r.Kind == MachineKind.Shovel || r.Kind == MachineKind.Loader))
            .Select(r => r.M3PerDay).Where(v => v > 0).OrderBy(v => v).ToList();
        if (loaderRates.Count < 3) return "";
        double med = loaderRates[loaderRates.Count / 2];
        if (med >= PlausibleLoaderDayLo && med <= PlausibleLoaderDayHi) return "";
        double mid = (PlausibleLoaderDayLo + PlausibleLoaderDayHi) * 0.5;
        return $"挖装设备实测日台效中位数 {med:0} m³/台·日，是工程合理量级（{PlausibleLoaderDayLo:0}~{PlausibleLoaderDayHi:0}）的 {med / mid:0.#} 倍。"
             + "capacity_monthly 与 equipment_model 两张表内部自洽，所以不是某一张录错，是**整张表一个口径**。"
             + $"这一条在 2026-08 出现过一次，根因是把源表的【台年能力 万m³/台·年】当成了月产量（差 12 倍），"
             + "已由迁移 V050 一次改数修掉。现在又报出来，先去核源表那一列的表头写的是"
             + "「当月产量」还是「台年能力」——<b>别在取数层缩放</b>，量级要在迁移里改对。"
             + "临时试口径才用 EquipmentAssignInput.CapacityScale，改完在指派结果里核对「欠产」还报不报得出来。";
    }

    private static bool IsInUse(string? status)
    {
        string s = (status ?? "").Trim();
        if (s.Length == 0) return false;                            // 空 = 不知道 ⇒ 不派（保守）
        if (string.Equals(s, EquipmentStatus.InUse.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
        return s is "在用" or "在役" or "正常";
    }

    private static MachineKind MapKind(EquipmentCategory c) => c switch
    {
        EquipmentCategory.Shovel => MachineKind.Shovel,
        EquipmentCategory.Truck => MachineKind.Truck,
        EquipmentCategory.Drill => MachineKind.Drill,
        EquipmentCategory.Loader => MachineKind.Loader,
        EquipmentCategory.Dozer => MachineKind.Dozer,
        EquipmentCategory.Grader => MachineKind.Grader,
        EquipmentCategory.WaterTruck => MachineKind.WaterTruck,
        _ => MachineKind.Other,
    };
}
