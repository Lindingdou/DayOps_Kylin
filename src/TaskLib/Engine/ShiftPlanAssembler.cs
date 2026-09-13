// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ShiftPlanAssembler.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;                 // MiningUnitLedger / MonthlyUnitLedgerStore / UnitRailFile
using PitMine3D.Kylin.Data;                   // EquipmentDataContext
using PitMine3D.Kylin.TaskLib.Domain;                        // ProductionTask / MaterialCatalog
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 消歧 System.Threading.Tasks.TaskStatus

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  装配 + 转换：把月度台账喂给 <see cref="MonthlyShiftDecomposer"/>，再把结果转成
//  <see cref="ProductionTask"/> —— 甘特 / 任务书 / 派工 / 达成度整条下游**一行不改**就能吃。
//
//  ══ 台效只有一个可信来源 ══
//  `production_record.output_m3` **不可信**：四类设备各记一遍、量级差七倍（已判定）。
//  `equipment_kpi_monthly` 只有工时与作业率，没有 m³/日。
//  所以日台效一律取 **`FleetMatcher` 解出来的编组班产**（m³/h，已在装配链里跑过）× 日工作小时。
//  **不另解一套** —— 两套解会让同一个月出两份机号分配，而两边各自都自洽。
//  解不出班产的面 ⇒ 那台设备**不可派**，不是"能力无限"（后者会让它一班吃下整个单元）。
//
//  ══ 转换时保住的三样 ══
//  · **稳定键**：`TaskKey.Compose(日期, 班次, 主设备, 工序, 作业区)` —— 实绩回灌靠它对号；
//  · **量口径**：只有采装笔的量进 `TargetVolumeM3`（那是原位实方）；
//  · **位置**：任务区域的环写进 `WorkZone` 之外的字段拿不下，先落在 `Note` 里 ——
//    契约里没有几何列，硬塞会让别处按字符串解析（那是下一个坑）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>月度台账 → 班组计划 → ProductionTask。永不抛。</summary>
public static class ShiftPlanAssembler
{
    /// <summary>缺省日工作小时（把编组班产 m³/h 折成日台效）。</summary>
    public const double DefaultWorkHoursPerDay = 24.0;

    /// <summary>
    /// 折日台效时的作业日兜底数。
    /// <para>只在调用方没给作业日时用。<b>它会让钻机的日台效偏</b>（月量 ÷ 作业日），
    /// 所以给了真实作业日就一定用真实的 —— 这个常数只是别让钻机整批消失。</para>
    /// </summary>
    public const double DefaultWorkdaysFallback = 25.0;

    /// <summary>装配结果（带上转换后的任务，便于一次拿全）。</summary>
    public sealed class Assembled
    {
        /// <summary>整月班组计划（逐日逐班逐机的行）。<b>永远是整月</b> —— 穿孔计划下发按期写，靠它。</summary>
        public ShiftPlanResult Plan = new();

        /// <summary>
        /// 交给当日盘子的任务。<b>给了 <c>onlyDate</c> 就只有那一天</b>。
        ///
        /// <para><b>为什么必须按日切</b>：<see cref="ProductionTask"/> 是**单日契约** ——
        /// 它只有 <c>Shift</c> 与 <c>StartHour/EndHour</c>(0..24)，**没有日期列**。
        /// 把整月的行原样塞进去，甘特就把一个月的条全叠在同一根 24 小时轴上：
        /// 一台钻机 26 个作业日的早班条重合成一条，看着像"这台机早班有活"，
        /// 其实是 26 天摞在一起。达成度、工序进度、派车单同理 —— 每个数都自洽，
        /// 而它们说的是一个月。<b>没有一处会报错</b>。</para>
        /// </summary>
        public List<ProductionTask> Tasks = new();

        /// <summary>整月任务（不按日切）。要整月视图的调用方取它，别去动 <see cref="Tasks"/>。</summary>
        public List<ProductionTask> MonthTasks = new();

        public List<string> Notes = new();
        public string Headline = "";

        /// <summary>
        /// 分解是否成立。<b>看的是整月计划有没有行，不是当日有没有任务</b>。
        ///
        /// <para>口径改过（原来是 <c>Tasks.Count &gt; 0</c>）：按日切之后，"今天不是作业日"
        /// 会让当日任务为 0，而那时 <c>Ok=false</c> 就会让调用方**退回日目标装箱** ——
        /// 装箱不认作业日历，会照着日目标给一个本不上班的日子排满三个班。
        /// 分解成立与否是**月**的属性，当日空是当日的事实，两者不能共用一个标志。</para>
        /// </summary>
        public bool Ok => Plan.Rows.Count > 0;

        /// <summary>分解成立，但**当日**一条任务都没有（多半是这天不在作业日历里）。</summary>
        public bool EmptyDay => Ok && Tasks.Count == 0;
    }

    /// <summary>
    /// 按期装配并分解。
    /// </summary>
    /// <param name="period">期次 yyyy-MM。</param>
    /// <param name="cfg">已装配好的当日盘子 —— <b>编组班产从它取</b>（不另解）。</param>
    /// <param name="ledgerRoot">台账根目录；null = 默认。</param>
    /// <summary>
    /// 钻机从哪来 —— <b>由 PlanLib 在插件初始化时注入</b>（参数：年、月、作业日数）。
    ///
    /// <para><b>为什么钻机不按面取、而电铲按面取</b>：电铲驻在一个台阶上作业，
    /// 「哪台铲在哪个面」是台账里的事实（<c>working_face.equipment_id</c>）；
    /// <b>钻机在台阶之间转移</b>，绑到某个面上不符合现场 —— 它是按矿调的。</para>
    ///
    /// <para><b>为什么走钩子而不是直接读库</b>：把设备台账读成 <c>Machine</c>/<c>RateRecord</c>、
    /// 再按六级回退解台效（实测 → 同型号 → 型号字典 → 类别兜底）那一整套在
    /// <c>PlanLib.ShortTerm.EquipmentFleetProvider</c> + <c>MineAssLib.RateBook</c> 里。
    /// 在这儿另写一份就是**第二个台效口径** —— 两份各自正确也证明不了一致，
    /// 加一级回退时只会改到其中一份。TaskLib 不引 PlanLib（约定如此），所以由 PlanLib 反向注入。</para>
    ///
    /// <para><b>没注入就是没有钻机</b>：那时需爆破的单元排不出采装（M3 是硬约束），
    /// 并在 Notes 里点名 —— <b>不给兜底钻机</b>，兜底出来的穿孔进度是编的。</para>
    /// </summary>
    public static Func<int, int, double, IReadOnlyList<PlanMachine>>? DrillFleetHook { get; set; }

    /// <param name="onlyDate">
    /// 非 null 时 <see cref="Assembled.Tasks"/> 只留这一天（<see cref="Assembled.Plan"/> 与
    /// <see cref="Assembled.MonthTasks"/> 仍是整月）。当日盘子必须给它 —— 理由见
    /// <see cref="Assembled.Tasks"/>。
    /// </param>
    public static Assembled Build(string period, ExploderConfig? cfg, string? ledgerRoot = null,
                                  DateTime? onlyDate = null)
    {
        var res = new Assembled();

        // ── ① 单元：月度台账 + 真轨 ──
        var units = LoadUnits(period, ledgerRoot, res.Notes);
        if (units.Count == 0)
        {
            res.Headline = $"{period} 没有排到本期的采掘单元 —— 分解不出班组计划。"
                         + "先到「采掘单元清单」排一期。";
            return res;
        }

        // ── ② 时间：作业日 + 班 ──
        //  ★ 排在设备之前：钻机的日台效 = 月量 ÷ **作业日**，取数时要用它（2026-08-19）。
        var days = Workdays(period, res.Notes);
        if (days.Count == 0)
        {
            res.Headline = $"{period} 的班次日历里一个作业日都没有 —— **拒绝分解**"
                         + "（不拿自然日顺延顶替：顺延出来的日期看着完全正常，"
                         + "而设备会被排到本来不上班的那天）。";
            return res;
        }

        // ── ③ 设备：电铲从盘子里的编组取（按面绑）· 钻机按矿调（DrillFleetHook）──
        var machines = LoadMachines(cfg, res.Notes, period, days.Count);

        // ★ 闸门按**工序**判，不按总台数判（2026-08-19 补）。
        //
        //   原来这里是 `machines.Count == 0`。而这张清单是两段拼出来的：
        //   电铲**按面取**（没有面 ⇒ 0 台），钻机**按矿调**（DrillFleetHook，与面无关 ⇒ 照样十几台）。
        //   于是 0 台电铲 + 11 台钻机 = 11 > 0，闸**放行** ——
        //   分解器排出一整月的穿孔，采装/运输/排土一条没有；
        //   量只落在采装笔上（见 ToTasks），所以计划量恒 0、达成度恒 0%。
        //   而顶栏还写着「没有作业面·排不出计划」：一句话说排不出来，下面排了 800 条。
        //
        //   **钻机不能替电铲开闸**：穿孔是为采装做准备的工序，没有采装面的穿孔
        //   排给谁用说不出来。判据也不能只判总数 —— 总数把两个来源混成了一个数。
        int loadMachines = machines.Count(m => m != null && m.Process == ProcessType.Load);
        if (loadMachines == 0)
        {
            // 「没有面」「有面但设备不可派」「只剩钻机」三种补法完全不同，结论必须分开说
            int nLoadFaces = (cfg?.Faces ?? new List<FaceInput>())
                             .Count(f => f != null && f.Process == ProcessType.Load);
            int drillsOnly = machines.Count - loadMachines;
            res.Headline = nLoadFaces == 0
                ? "**盘子里一个采装作业面都没有** —— 拒绝分解。"
                  + "设备是按面取的，没有面就取不到设备（与设备库里有多少台无关）。"
                  + "先到「作业区划分」把本期工序作业区生成并入库（面就会派生出来），"
                  + "或直接在「作业面台账」建档。"
                : "**一台可派的采装设备都没有** —— 拒绝分解。"
                  + "编组班产解不出来时那台设备是不可派，不是能力无限。";
            if (drillsOnly > 0)
                res.Headline += $"　（此时钻机有 {drillsOnly} 台可派，但**不单独成盘**："
                              + "只有穿孔、没有采装的计划，量恒为 0，看着像排了一天的活。）";
            return res;
        }


        var slotsByDate = SlotsByDate(period);
        res.Plan = MonthlyShiftDecomposer.Decompose(
            period, units, machines, days,
            d => slotsByDate.TryGetValue(d.Date, out var v) ? v : Array.Empty<PlanShift>());

        res.MonthTasks = ToTasks(res.Plan, cfg);
        res.Tasks = onlyDate.HasValue
            ? ToTasks(res.Plan, cfg, onlyDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : res.MonthTasks;
        res.Notes.AddRange(res.Plan.Notes);
        res.Headline = res.Plan.Headline
                     + (res.MonthTasks.Count > 0 ? $"　→ 转成 {res.MonthTasks.Count} 条任务" : "");

        if (onlyDate.HasValue)
        {
            string dk = onlyDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            res.Headline += $"　（当日 {dk}：{res.Tasks.Count} 条）";
            if (res.Tasks.Count == 0)
                res.Notes.Add($"◆ 本期分解出 {res.MonthTasks.Count} 条任务，但 **{dk} 一条都没有** —— "
                            + "这天多半不在班次日历的作业日里（分解器只排作业日，不拿自然日顺延）。"
                            + "当日盘子于是是空的，这是如实呈现，不是排产失败。"
                            + "要改就去「班次日历」把这天排上班。");
        }
        return res;
    }

    // ── 转换 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 班组任务 → <see cref="ProductionTask"/>。
    /// </summary>
    /// <param name="onlyDate">
    /// <c>yyyy-MM-dd</c>；非空时只转这一天的行。
    /// <para><b>为什么筛在这一层而不是筛出来的任务上</b>：<see cref="ProductionTask"/> 上**没有日期列**，
    /// 转完就只剩 <c>Id</c> 里那截日期标签了 —— 那时再筛就得去解析 Id，
    /// 而 Id 的格式是给"稳定键"用的，不是给查询用的（<see cref="TaskKey"/> 换个写法这里就静默筛空）。
    /// <c>ShiftTaskRow.Date</c> 才是这个日期的**正主**。</para>
    /// </param>
    public static List<ProductionTask> ToTasks(ShiftPlanResult? plan, ExploderConfig? cfg,
                                               string? onlyDate = null)
    {
        var list = new List<ProductionTask>();
        if (plan == null) return list;
        string want = (onlyDate ?? "").Trim();

        // 面上的去向/运距/煤质原样带过来 —— 那些是装配链解出来的，这里不重解
        var faceOf = new Dictionary<string, FaceInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in cfg?.Faces ?? new List<FaceInput>())
            if (!string.IsNullOrWhiteSpace(f.Zone)) faceOf[f.Zone.Trim()] = f;

        // 穿孔计划台账（drill_plan）：孔数 / 单孔延米。按「设备+面」对号，对不上再退到「只按设备」。
        // 这一份是**计划量**，与下面那笔控制方量是两个口径（孔数是钻机的活，控制方量是这一炮管住的岩）。
        var drillOf = new Dictionary<string, DrillInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in cfg?.Drills ?? new List<DrillInput>())
        {
            if (d == null) continue;
            if (d.HoleCount is not > 0 && d.HoleLengthM is not > 0) continue;   // 没量的不建索引：建了也只会顶替掉有量的
            string byBoth = (d.EquipId ?? "").Trim() + "|" + (d.Zone ?? "").Trim();
            if (!drillOf.ContainsKey(byBoth)) drillOf[byBoth] = d;
            string byEquip = (d.EquipId ?? "").Trim() + "|";
            if (!drillOf.ContainsKey(byEquip)) drillOf[byEquip] = d;
        }

        foreach (var r in plan.Rows)
        {
            if (want.Length > 0 && !string.Equals((r.Date ?? "").Trim(), want, StringComparison.Ordinal)) continue;

            faceOf.TryGetValue(r.FaceName, out var face);
            string dateLabel = r.Date;

            var t = new ProductionTask
            {
                Id = TaskKey.Compose(dateLabel, r.Shift, r.MachineId, r.Process.ToString(), r.FaceName),
                Process = r.Process,
                WorkZone = r.FaceName,
                UnitId = r.UnitId,
                Shift = r.Shift,
                StartHour = r.StartHour,
                EndHour = r.EndHour,
                MaterialCode = r.MaterialCode,
                Material = MaterialCatalog.Resolve(r.MaterialCode).Name,
                Status = TaskStatus.Planned,
            };

            // 量口径：只有采装那一笔是原位实方 —— 穿孔的控制方量、排土的排弃占容
            // 混进同一列，谁一 SUM 就得到一个不对应任何真实量的数。
            //
            // ★ 但**不许因此把量丢掉**（2026-08-20 修）：穿孔行的 `VolumeM3` 是分解器算出来的
            //   **控制方量**（`MonthlyShiftDecomposer.BasisOf` 明写着这个口径），此前这一行
            //   只判 Load、其余一律不接 ⇒ 控制方量在这一步整段蒸发，于是正式单据上
            //   穿孔那一行的量永远是「—」，而分解器那头明明算得清清楚楚。
            //   分开一列接住它，口径就没混，账也没丢（同「整段丢弃必须记账」那条纪律）。
            if (r.Process == ProcessType.Load) t.TargetVolumeM3 = r.VolumeM3;
            else if (r.Process == ProcessType.Drill)
            {
                t.ControlVolumeM3 = r.VolumeM3;

                // 孔数/延米走 drill_plan，缺就留 null —— **不拿控制方量反推孔数**
                //（那要孔网参数，反推出来的数在单据上看着完全正常）。
                if (drillOf.TryGetValue((r.MachineId ?? "").Trim() + "|" + (r.FaceName ?? "").Trim(), out var dp)
                 || drillOf.TryGetValue((r.MachineId ?? "").Trim() + "|", out dp))
                    t.Drill = new DrillQuantity { PlanHoles = dp.HoleCount, PlanMeters = dp.HoleLengthM };
            }

            t.Group.MainEquipment = r.MachineId;
            if (face != null)
            {
                t.BenchElevationM = face.BenchElevationM;
                t.EngineeringPositionId = face.EngineeringPositionId;
                t.DestinationId = face.DestinationId;
                t.DestinationName = face.DestinationName;
                t.DestinationKind = face.DestinationKind;
                t.HaulDistanceKm = face.HaulDistanceKm;
                t.EquivHaulKm = face.EquivHaulKm;
                t.QualityTarget = face.Quality;
                t.Group.Trucks = new List<string>(face.Group.Trucks);
                t.Group.RecommendedTrucks = face.Group.RecommendedTrucks;
                t.Group.GroupCapacityM3PerH = face.Group.GroupCapacityM3PerH;
                t.PlannedHours = face.Group.GroupCapacityM3PerH > 1e-9
                    ? r.VolumeM3 / face.Group.GroupCapacityM3PerH : 0;
            }

            list.Add(t);
        }
        return list;
    }

    // ── 装配件 ────────────────────────────────────────────────────────

    private static List<PlanUnit> LoadUnits(string period, string? ledgerRoot, List<string> notes)
    {
        var list = new List<PlanUnit>();
        List<MiningUnitLedger.Row> rows;
        try
        {
            var store = new MonthlyUnitLedgerStore(ledgerRoot);
            if (!store.Exists(period)) { notes.Add($"◆ {period} 没有月度台账。"); return list; }
            store.TryLoad(period, out rows, out _);
        }
        catch (Exception ex) { notes.Add($"◆ 月度台账读取失败（{ex.GetType().Name}）。"); return list; }

        var rails = new Dictionary<string, UnitRailFile.RailRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (UnitRailFile.TryRead(UnitRailFile.PathIn(ledgerRoot), out var map, out _, out _))
                foreach (var kv in map) rails[kv.Key] = kv.Value;
        }
        catch { }

        int noRail = 0, usedFlow = 0, usedDone = 0;
        foreach (var r in rows ?? new List<MiningUnitLedger.Row>())
        {
            if (r == null || string.IsNullOrWhiteSpace(r.UnitId)) continue;
            if (!string.Equals((r.Period ?? "").Trim(), period, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Kind == LedgerKind.Dump) continue;                 // 排土不在采掘队列上推进

            double vol = r.Kind == LedgerKind.Coal ? r.CoalM3 ?? 0 : r.NetRockM3 ?? 0;

            // ★ 本期要采多少 = **排产写进 Flows 的量**，不是 `量 ×（1 − 完成度）`（2026-08-19 修）。
            //
            //   `完成度` 这一列上有一次**口径对撞**：
            //     · 写方（排产写回 `MiningUnitPlanWindow`）填的是 `DoneAfter` ——
            //       **计划执行完之后**的累计完成度。本期计划采完的单元于是写成「已采 / 100%」。
            //     · 读方（这里）原来把它当**已经完成了多少**，算 `量 ×（1 − 完成度）`。
            //   两个口径撞在一起的后果：排完产，本期 106 个单元里 104 个是「已采 100%」，
            //   剩余量全归零 ⇒ **逐日逐班一条都切不出来**，而台账上每个数看着都对。
            //   （实测：只剩 2 个「在采」的单元有量，切出 0 条班组任务。）
            //
            //   正确的量就在旁边：`Flows[].InSituM3` 是排产给这个单元定的**本期原位实方**。
            //   没有流的单元才退回 `量 ×（1 − 完成度）`，并把用了哪个口径报出来。
            double flowM3 = (r.Flows ?? new List<MiningUnitLedger.Flow>())
                            .Where(f => f != null).Sum(f => Math.Max(0, f.InSituM3));
            double remain;
            if (flowM3 > 1e-6) { remain = flowM3; usedFlow++; }
            else { remain = Math.Max(0, vol * (1 - Math.Clamp(r.Done, 0, 1))); usedDone++; }
            if (!(remain > 1e-6)) continue;

            string mat = r.Kind == LedgerKind.Coal ? MaterialCatalog.Coal : MaterialCatalog.Rock;
            bool blast = MaterialCatalog.Resolve(mat).NeedsBlasting;
            rails.TryGetValue(r.UnitId.Trim(), out var rail);
            if (rail == null) noRail++;

            list.Add(new PlanUnit
            {
                UnitId = r.UnitId.Trim(),
                FaceName = string.IsNullOrWhiteSpace(r.Region) ? "采场" : r.Region.Trim()
                         + (string.IsNullOrWhiteSpace(r.Seam) ? "" : "·" + r.Seam.Trim()),
                MaterialCode = mat,
                Seq = r.Seq,
                RemainM3 = remain,
                NeedsBlasting = blast,
                // 控制方量：本仓库拿不到孔网参数，按原位实方同量记 —— 口径写在量的名字上，不混列
                DrillM3 = blast ? remain : 0,
                Rail = rail,
                TowardCrest = true,
            });
        }

        // 用了哪个口径必须报出来 —— 两条路的量差一个数量级都不奇怪
        if (usedFlow > 0)
            notes.Add($"· {usedFlow} 个单元的本期量取自**排产写的流**（Flows 的原位实方）—— 这是本期要采的量。");
        if (usedDone > 0)
            notes.Add($"◆ {usedDone} 个单元**没有流**，本期量退回「量 ×（1 − 完成度）」。"
                    + "⚠ 这一列由排产写的是**计划执行完之后**的完成度，"
                    + "所以计划采完的单元在这里会算出剩余量 0 —— 这些单元排不进班表。"
                    + "补法：重排一次让流落到单元上，或核对完成度这一列。");

        if (noRail > 0)
            notes.Add($"◆ {noRail}/{list.Count} 个单元**没有真轨** —— 任务照排，但切不出任务区域，"
                    + "派工点开定位不到图上。补法：跑一次采矿模型，再从模型取一次台账。");
        return list;
    }

    /// <summary>
    /// 从盘子里取可派设备。<b>台效 = 编组班产 × 日工作小时</b>，解不出的面那台设备不可派。
    /// </summary>
    private static List<PlanMachine> LoadMachines(ExploderConfig? cfg, List<string> notes,
                                                 string period, double days)
    {
        var list = new List<PlanMachine>();
        int noRate = 0, noEquip = 0;
        foreach (var f in cfg?.Faces ?? new List<FaceInput>())
        {
            if (f == null || f.Process != ProcessType.Load) continue;
            string id = (f.Group?.MainEquipment ?? "").Trim();
            if (id.Length == 0) { noEquip++; continue; }
            double perH = f.Group?.GroupCapacityM3PerH ?? 0;
            if (!(perH > 1e-9)) { noRate++; continue; }

            if (list.Any(m => string.Equals(m.MachineId, id, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new PlanMachine(id, f.ShovelModelPref ?? "", ProcessType.Load,
                                     perH * DefaultWorkHoursPerDay,
                                     new[] { f.MaterialCode }));
        }
        // ★ 第三档：**盘子里压根没有采装作业面**（2026-08-19 补）。
        //   原来只报了"有面但没设备"和"有面但没班产"两档 —— 而面为空时循环体一次都不进，
        //   两个计数器都是 0，于是**一条 note 都不出**，界面上只剩
        //   「一台可派设备都没有」这句结论，读起来像设备库空了。
        //   实测：设备库里 518 台设备、50 个型号，而 cfg.Faces 里采装面是 0 个 —— 缺的根本不是设备。
        int loadFaces = (cfg?.Faces ?? new List<FaceInput>()).Count(f => f != null && f.Process == ProcessType.Load);
        if (loadFaces == 0)
            notes.Add("◆ **盘子里一个采装作业面都没有** —— 设备是按「面」取的（每个面的主设备 + 编组班产），"
                    + "没有面就取不到设备，这跟设备库里有多少台**没有关系**。"
                    + "补法：到「确定开采程序」建面（或点「按本期单元派生作业面」），"
                    + "再回「设备指派」跑一次让编组落到面上。");

        if (noEquip > 0)
            notes.Add($"◆ {noEquip} 个面**没有主设备** —— 那些面这一期排不上。"
                    + "主电铲是**作业面台账（working_face.equipment_id）**里的一列，"
                    + "**派生不出来**：从工序作业区派生的面只有位置与量，配哪台铲是人定的。"
                    + "`FleetMatcher` 只把这一列**透传**（它算编组班产、不挑设备），"
                    + "所以台账空着的话这里永远是 0 台可派 —— 而设备库里有多少台无关。"
                    + "补法：到「作业面台账」给这些面建档并填主电铲，"
                    + "或在「设备指派 → 归属覆盖」里手工指定。");
        // ── 钻机：按矿调，不按面绑（见 DrillFleetHook 的说明）──────────
        int drills = 0;
        bool needBlast = true;      // 是否有需爆破的单元，由调用方的 Notes 侧已经报过；这里只管钻机
        if (DrillFleetHook != null &&
            DateTime.TryParseExact((period ?? "").Trim() + "-01", "yyyy-MM-dd",
                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var pd))
        {
            try
            {
                foreach (var m in DrillFleetHook(pd.Year, pd.Month, days > 0 ? days : DefaultWorkdaysFallback) ?? Array.Empty<PlanMachine>())
                {
                    if (m == null || string.IsNullOrWhiteSpace(m.MachineId)) continue;
                    if (list.Any(x => string.Equals(x.MachineId, m.MachineId, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(m); drills++;
                }
                notes.Add(drills > 0
                    ? $"· 钻机 {drills} 台**按矿调取**（不按面绑：钻机在台阶之间转移，绑到面上不符合现场）。"
                    : "◆ 钻机注入器返回 0 台 —— 需爆破的单元排不出采装（M3 穿爆超前是硬约束）。"
                      + "先看设备库里钻机的状态与台效：解不出台效的钻机是**不可派**，不是台效为 0。");
            }
            catch (Exception ex)
            { notes.Add($"◆ 钻机注入器抛了（{ex.GetType().Name}）—— 本期没有钻机，需爆破的单元排不出采装。"); }
        }
        else if (needBlast)
            notes.Add("◆ **一台钻机都没有**（钻机注入器没装上）—— 需爆破的单元**排不出采装**："
                    + "M3「穿爆超前」是硬约束，没穿完孔就开不了挖。"
                    + "这不是保守，是这一期真的排不了那些岩单元。"
                    + "钻机按矿调、不按面绑，由 PlanLib 在插件初始化时注入 `DrillFleetHook`。");


        if (noRate > 0)
            notes.Add($"◆ {noRate} 个面**解不出编组班产** —— 那台设备**不可派**（不是能力无限："
                    + "当成无限会让它一班吃下整个单元）。补法：在「设备编组」里补 dispatch_rule。");
        return list;
    }

    private static List<DateTime> Workdays(string period, List<string> notes)
    {
        var list = new List<DateTime>();
        if (!DateTime.TryParseExact((period ?? "").Trim() + "-01", "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        { notes.Add($"◆ 期次「{period}」不是 yyyy-MM。"); return list; }
        try
        {
            var last = first.AddMonths(1).AddDays(-1);
            foreach (var g in EquipmentDataContext.ShiftCalendar.InRange(first, last)
                                                  .Where(s => s != null)
                                                  .GroupBy(s => s.Date.Date).OrderBy(g => g.Key))
                list.Add(g.Key);
        }
        catch (Exception ex) { notes.Add($"◆ 班次日历读不到（{ex.GetType().Name}）。"); }
        return list;
    }

    private static Dictionary<DateTime, IReadOnlyList<PlanShift>> SlotsByDate(string period)
    {
        var map = new Dictionary<DateTime, IReadOnlyList<PlanShift>>();
        if (!DateTime.TryParseExact((period ?? "").Trim() + "-01", "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            return map;
        try
        {
            var last = first.AddMonths(1).AddDays(-1);
            foreach (var g in EquipmentDataContext.ShiftCalendar.InRange(first, last)
                                                  .Where(s => s != null).GroupBy(s => s.Date.Date))
            {
                // ★ 班次名必须走 WorkCalendar.ShiftName（2026-08-20 修）。
                //
                //   这里原来取的是**台账里的原始码**（真库里是 A/B/C），而全项目其余地方
                //   —— 盘子的 cfg.Shifts、下拉框的取值域 ShiftScope.Names、班次日历窗口、事实宽表 ——
                //   一律是「早班/中班/夜班」。于是分解器排出来的任务 Shift='A'，
                //   甘特上选「早班」按 t.Shift == "早班" 一条都对不上：**切哪个班都是空的**，
                //   而「全部」时又都在。没有任何一处报错。
                //   同一个口径不许有两份取名法。
                var starts = g.Select(x => (Name: WorkCalendar.ShiftName(x.Shift),
                                            H: ParseHour(x.StartTime), x.IsBlastShift))
                              .Where(x => x.H >= 0).OrderBy(x => x.H).ToList();
                if (starts.Count == 0) continue;

                // 收班时刻是推出来的：shift_calendar 只有开班时刻（与 DrillPlanWriter 同一条）
                double span = starts.Count >= 2
                    ? Median(Enumerable.Range(1, starts.Count - 1)
                                       .Select(i => starts[i].H - starts[i - 1].H).ToList())
                    : 24.0;
                var slots = new List<PlanShift>();
                for (int i = 0; i < starts.Count; i++)
                {
                    double end = i + 1 < starts.Count ? starts[i + 1].H
                                                      : Math.Min(24.0, starts[i].H + span);
                    slots.Add(new PlanShift(starts[i].Name, starts[i].H, end, starts[i].IsBlastShift));
                }
                map[g.Key] = slots;
            }
        }
        catch { }
        return map;
    }

    private static double ParseHour(string? hhmm)
    {
        string t = (hhmm ?? "").Trim();
        if (t.Length == 0) return -1;
        var parts = t.Split(':');
        if (!int.TryParse(parts[0], out int h) || h < 0 || h > 23) return -1;
        int m = 0;
        if (parts.Length >= 2) int.TryParse(parts[1], out m);
        return h + Math.Clamp(m, 0, 59) / 60.0;
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var v = xs.OrderBy(x => x).ToList();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) * 0.5;
    }
}
