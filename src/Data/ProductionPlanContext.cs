using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;

namespace PitMine3D.Kylin.Data;

/// <summary>装配链上某一段的状态。</summary>
public enum ChainState
{
    /// <summary>台账里有，用的就是它。</summary>
    Real = 0,
    /// <summary>用了<b>工程缺省参数</b>（不携带这个矿特有的信息），不是台账排的。</summary>
    Partial = 1,
    /// <summary>台账里没有，这一段是空的。</summary>
    Missing = 2,
    /// <summary>这一段在 Kylin 侧还没接（未移植）。</summary>
    NotPorted = 3,
}

/// <summary>装配一段的结果（段名 + 状态 + 一句给人看的来源）。</summary>
public sealed class ChainSegment
{
    public string Name { get; set; } = "";
    public ChainState State { get; set; }
    public string Label { get; set; } = "";
    public string StateZh => State switch
    {
        ChainState.Real => "台账",
        ChainState.Partial => "工程缺省",
        ChainState.NotPorted => "未移植",
        _ => "没有",
    };
}

/// <summary>装配一份当日盘子的结果。</summary>
public sealed class PlanAssembly
{
    public ExploderConfig Config { get; set; } = new();
    public DateTime Date { get; set; }

    /// <summary>逐段状态（顺序即装配顺序）。</summary>
    public List<ChainSegment> Chain { get; set; } = new();

    /// <summary>装配过程里要人知道的事（不构成校核违规，但不能只写在某个状态栏上）。</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>作业面来自台账。<b>false = 一个面都没有</b>（Kylin 不编样例面，见类文档）。</summary>
    public bool FacesFromLedger { get; set; }

    /// <summary>一个作业面都没有 —— 这一盘计划排不出来。</summary>
    public bool NoFaces => Config.Faces.Count == 0;

    /// <summary>能不能拿这份盘子去排。</summary>
    public bool Usable => !NoFaces;

    public ChainSegment? Seg(string name)
        => Chain.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>盘子来源总文案（各段用「·」串起来）。</summary>
    public string SourceLabel => string.Join("　·　", Chain.Select(c => $"{c.Name}[{c.StateZh}]"));
}

/// <summary>
/// 当日盘子装配层（移植原 <c>TaskLib.Engine.ProductionPlanContext.Config()</c> 的装配链）。
///
/// <para>
/// 在此之前 Kylin 的「生产任务编制」跑的是一份<b>写死在代码里的示例</b>（「4煤南 / WK-35A / T1..T5」），
/// 状态栏还自己写着"真数据需接台账/钻孔计划"。而各张台账其实已经一张张接通了 ——
/// 班次日历(§三三二)、检修档期(§三三三)、去向台账(§三三七)、爆破与穿孔(§三五二)、编制锚点(§三五三)。
/// 本类把它们装成一份盘子。
/// </para>
///
/// <b>装配顺序</b>（照原版）：
/// <list type="number">
///   <item><b>①.5 人工锚点</b> —— 排在最前：配煤标准要在装箱前生效，降效要在所有面的能力上生效。</item>
///   <item><b>② 班次</b> ← <c>shift_calendar</c>；缺 → <b>工程缺省三班</b>（唯一允许的兜底，理由见下）。</item>
///   <item><b>③ 爆破停产时窗</b> ← <c>blast_event</c> 逐炮（不是只取最早一炮）。</item>
///   <item><b>③.2 穿孔作业计划</b> ← <c>drill_plan</c>。</item>
///   <item><b>③.5 检修档期</b> ← <c>maintenance_window</c>。有效时窗 = 班起 − 检修 − 爆破清场 − 交接损失，
///     三个减项里检修此前恒为 0：一台上午定修的电铲，计划里从 0 点起就在满负荷干活。</item>
///   <item><b>④ 作业面 + 编组</b> ← <c>working_face_routing</c>，班产按
///     <c>fleet_dispatch_rule</c> 的物理产能子模型算（与「编组优化」<b>同一份口径</b>）。</item>
///   <item><b>⑤ 去向登记簿</b> ← 去向台账；逐受矿点入仓标准的锚点在这一步之后才盖得上。</item>
/// </list>
///
/// <para>
/// <b>★ 样例不兜底</b>（照搬原版 2026-08-18 的那条决定）：编一份看着正常的假盘子
/// —— 有面、有量、有编组，每个数都自洽 —— 比空着更危险，人分不出自己看的是这个矿还是示例矿。
/// 唯一的例外是<b>班制</b>：早/中/夜 00–08/08–16/16–24 是工程缺省参数，不携带任何这个矿特有的信息
/// （跟交接班坡道 0.5h 同类），不兜它反而挡住整条链。但必须标明<b>是缺省不是日历排的</b>，
/// 状态记 <see cref="ChainState.Partial"/> 而不是 Real。
/// 判别标准就一条：<b>这个兜底会不会让人误以为看到的是真数据</b>。
/// </para>
///
/// ── 未移植的几段（登记）──
/// <list type="bullet">
///   <item><b>本期工序作业区派生面</b>（<c>process_zone</c>，原版 ④ 的第二层）—— Kylin 尚无那张表的消费者。</item>
///   <item><b>去向自动重建</b>（原版 ⓪，从采掘单元台账按场名分组建场）—— 依赖未移植的采掘单元台账。</item>
///   <item><b>月计划 → 日目标分解</b>：日目标直接取 <c>working_face_routing.day_target_m3</c>
///     （那张表存的正是"当日怎么干"）。按月计划摊到各天那条路见 <c>ShortTermLink</c>，本层不代劳。</item>
///   <item><b>煤质</b>：作业面台账没有灰/热/硫列，故 <see cref="FaceInput.Quality"/> 一律为 null ——
///     配煤约束因此不绑（<b>不拿默认煤质冒充</b>，那会让「配煤达标」变成一句假话）。</item>
///   <item><b>单据回灌 / 采掘单元对号 / 主设备可用性校核</b>：依赖未移植的那几张表。</item>
/// </list>
/// </summary>
public static class ProductionPlanContext
{
    /// <summary>工程缺省三班（早/中/夜）。<b>不是台账</b>，用它时状态记 Partial。</summary>
    public static List<ShiftWindow> DefaultThreeShifts() => new()
    {
        new ShiftWindow("早班", 0, 8), new ShiftWindow("中班", 8, 16), new ShiftWindow("夜班", 16, 24),
    };

    /// <summary>
    /// 台账口径的班次时窗 → 引擎口径的那一个。
    /// <b>两个 <c>ShiftWindow</c> 是有意分开的</b>：<see cref="ShiftWindow"/> 是台账那侧的只读值
    /// （班次日历推出来的 [起,止)），<c>Cad.Tasks.Scheduling.ShiftWindow</c> 是装箱盘子里的一项。
    /// 合成一个类型的话，台账层就得引用调度域 —— 这里只在装配这一处换一次。
    /// </summary>
    internal static Cad.Tasks.Scheduling.ShiftWindow ToEngine(ShiftWindow w) => new(w.Name, w.Start, w.End);

    /// <summary>
    /// 装配一份当日盘子。<b>每次调用都重新装配</b>（不缓存）：台账随时可能被别的窗口改，
    /// 缓存会让"改完看不见"重新长出来。
    /// </summary>
    /// <param name="nowHour">此刻几点（0..24）。判据注入固定值。</param>
    public static PlanAssembly Assemble(DbConnection? conn, DateTime date, double nowHour)
    {
        var asm = new PlanAssembly { Date = date.Date };
        var cfg = asm.Config;
        cfg.DateLabel = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        cfg.IdPrefix = "D" + date.ToString("MMdd", CultureInfo.InvariantCulture);
        cfg.NowHour = nowHour;

        // ── ①.5 人工锚点（配煤标准 / 降效 / 交接 / 备采下限）──
        // 排在最前：配煤标准要在装箱前生效，降效要在所有面的能力上生效。没设过返回空串。
        string anchor = "";
        try { anchor = CompileOverrides.ApplyTo(cfg); } catch { anchor = ""; }
        asm.Chain.Add(new ChainSegment
        {
            Name = "编制锚点",
            State = anchor.Length > 0 ? ChainState.Real : ChainState.Missing,
            Label = anchor.Length > 0 ? anchor : "没设过编制锚点，全部按引擎缺省",
        });

        // ── ② 班次 ──
        var shifts = MaintenanceWindows.ShiftWindowsOf(WorkCalendar.Day(conn!, date, out string shiftErr));
        if (shifts.Count > 0)
        {
            cfg.Shifts.AddRange(shifts.Select(ToEngine));
            asm.Chain.Add(new ChainSegment
            {
                Name = "班次", State = ChainState.Real,
                Label = $"班次日历 {shifts.Count} 班（" + string.Join("、", shifts.Select(s => $"{s.Name} {Hm(s.Start)}–{Hm(s.End)}")) + "）",
            });
        }
        else
        {
            cfg.Shifts.AddRange(DefaultThreeShifts().Select(ToEngine));
            asm.Chain.Add(new ChainSegment
            {
                Name = "班次", State = ChainState.Partial,
                Label = "班次日历本日没有，按工程缺省三班（早 00–08 / 中 08–16 / 夜 16–24）",
            });
            asm.Notes.Add("· 班次日历里本日一条都没有，已按**工程缺省三班**排。"
                        + "这不是日历里排的班 —— 实际班制不是三班八小时、或有爆破班/停产班时，"
                        + "算出来的有效时窗与班产都会偏。补法：到「班次日历」把本月的班排出来。"
                        + (shiftErr.Length > 0 ? $"（读表报：{shiftErr}）" : ""));
        }

        // ── ③ 爆破停产时窗（逐炮，不是只取最早一炮）──
        var shots = BlastPlanLink.ShotsByDate(conn, date, out string blastErr);
        var blasts = BlastPlanLink.BlastWindowsOf(shots, out int noTime);
        if (blasts.Count > 0)
        {
            cfg.SetBlasts(blasts);
            asm.Chain.Add(new ChainSegment
            {
                Name = "爆破时窗", State = ChainState.Real,
                Label = $"爆破台账 {shots.Count} 炮 → {blasts.Count} 段停产（"
                      + string.Join("、", blasts.Select(w => $"{Hm(w.Start)}–{Hm(w.End)}"))
                      + $"；清场按缺省 {BlastPlanLink.ClearanceH * 60:0} 分钟）",
            });
            if (noTime > 0)
                asm.Notes.Add($"· {noTime} 炮没记爆破时刻，排不进任何时窗（在爆破台账补 blast_time）。");
        }
        else
        {
            asm.Chain.Add(new ChainSegment
            {
                Name = "爆破时窗", State = ChainState.Missing,
                Label = blastErr.Length > 0 ? $"爆破台账读不通（{blastErr}）" : "本日无爆破台账记录，未扣停产时段",
            });
        }

        // ── ③.2 穿孔作业计划 ──
        var drillLoad = DrillPlanStore.ToDrillInputs(DrillPlanStore.ByDate(conn, date, out string drillErr), date);
        if (drillLoad.Drills.Count > 0) cfg.Drills.AddRange(drillLoad.Drills);
        asm.Chain.Add(new ChainSegment
        {
            Name = "穿孔计划",
            State = drillLoad.Drills.Count > 0 ? ChainState.Real : ChainState.Missing,
            Label = drillErr.Length > 0 ? $"穿孔计划表读不通（{drillErr}）" : drillLoad.Label,
        });
        if (drillLoad.Bad > 0 || drillLoad.Cancelled > 0) asm.Notes.Add("· " + drillLoad.Label);

        // ── ③.5 检修档期 ──
        var maintRows = MaintenanceWindows.ByDate(conn!, date, out string maintErr);
        var maints = WorkWindowCalc.HourWindowsOf(maintRows);
        foreach (var m in maints)
            cfg.Maintenance.Add(new MaintenanceWindow { EquipId = m.EquipId, Start = m.Start, End = m.End, Label = m.Label });
        asm.Chain.Add(new ChainSegment
        {
            Name = "检修档期",
            State = maints.Count > 0 ? ChainState.Real : ChainState.Missing,
            Label = maintErr.Length > 0 ? $"检修档期读不通（{maintErr}）"
                  : maints.Count > 0 ? $"检修档期本日 {maints.Count} 条"
                  : "检修档期本日没有（若真有设备在修，有效时窗会算多）",
        });
        if (maintRows.Count > maints.Count)
            asm.Notes.Add($"· 检修档期有 {maintRows.Count - maints.Count} 条起止时刻非法已丢弃（不按 0 点算）——"
                        + "界面上看得见、计划里没有，到「检修档期」改成 HH:mm。");

        // ── ④ 作业面 + 编组 ──
        AssembleFaces(conn, asm);

        // ── ⑤ 去向登记簿 ──
        SinkRegistry? sinks = null;
        try { sinks = SinkRegistryLoader.Load(conn); } catch { sinks = null; }
        if (sinks != null && sinks.All.Count > 0)
        {
            string sinkAnchor = "";
            try { sinkAnchor = CompileOverrides.ApplySinkBlend(sinks); } catch { }
            asm.Chain.Add(new ChainSegment
            {
                Name = "去向", State = ChainState.Real,
                Label = $"去向台账 {sinks.All.Count} 个受矿点" + (sinkAnchor.Length > 0 ? "；" + sinkAnchor : ""),
            });
            if (sinkAnchor.Contains("找不到")) asm.Notes.Add("· " + sinkAnchor);
        }
        else
        {
            asm.Chain.Add(new ChainSegment
            {
                Name = "去向", State = ChainState.Missing,
                Label = "去向台账里一个受矿点都没有 —— 排弃量没有去处，采排守恒校核判不了",
            });
        }

        // 未移植的那几段照实登记，不假装装配过
        asm.Chain.Add(new ChainSegment
        {
            Name = "月计划分解", State = ChainState.NotPorted,
            Label = "日目标直接取作业面台账的 day_target_m3；按月计划摊到各天那条路在「短期生产计划」里",
        });

        return asm;
    }

    /// <summary>
    /// ④ 作业面：<c>working_face_routing</c> → <see cref="FaceInput"/>，班产按编组规则的物理产能算。
    /// <b>一个面都没有就是没有</b>，不编样例面。
    /// </summary>
    private static void AssembleFaces(DbConnection? conn, PlanAssembly asm)
    {
        var cfg = asm.Config;
        var faces = BlastPlanLink.FacesOfDay(conn, out string faceErr);
        // 日目标为 0 的面排不出任何任务，但它在台账里是"配了没量"，与"没有这个面"不是一回事
        var usable = faces.Where(f => f.Zone.Length > 0).ToList();

        if (usable.Count == 0)
        {
            asm.Chain.Add(new ChainSegment
            {
                Name = "作业面", State = ChainState.Missing,
                Label = faceErr.Length > 0 ? $"作业面台账读不通（{faceErr}）" : "作业面台账里一个面都没有",
            });
            asm.Notes.Add("· 一个作业面都没有 —— 这一盘计划排不出来。"
                        + "**这里不编样例面**：编一份看着正常的假盘子，人分不出自己看的是这个矿还是示例矿。"
                        + "补法：到「作业面台账」把本期的面与当日目标录进去。");
            return;
        }

        var rules = new List<FleetDispatchRule>();
        if (conn != null)
        {
            try { rules = GeoDataQueries.GetFleetDispatchRules(conn); } catch { rules = new List<FleetDispatchRule>(); }
        }
        var models = ModelOfEquipment(conn);

        int solved = 0, noTarget = 0;
        foreach (var f in usable)
        {
            AttachGroup(f, rules, models);
            if (f.Group.GroupCapacityM3PerH > 1e-9) solved++;
            if (f.DayTargetM3 <= 1e-9) noTarget++;
            cfg.Faces.Add(f);
        }

        asm.FacesFromLedger = true;
        asm.Chain.Add(new ChainSegment
        {
            Name = "作业面", State = ChainState.Real,
            Label = $"作业面台账 {usable.Count} 个面（采装 {usable.Count(f => f.Process == ProcessType.Load)}"
                  + $" / 排土 {usable.Count(f => f.Process == ProcessType.Dump)}）",
        });
        asm.Chain.Add(new ChainSegment
        {
            Name = "编组班产",
            State = solved == usable.Count ? ChainState.Real : solved > 0 ? ChainState.Partial : ChainState.Missing,
            Label = rules.Count == 0
                ? "编组规则表里没有在役规则 —— 各面班产算不出来，装箱排不出任务"
                : $"按编组规则算出 {solved}/{usable.Count} 个面的班产（与「编组优化」同一份物理口径）",
        });

        if (solved < usable.Count)
            asm.Notes.Add($"· {usable.Count - solved} 个面对不上编组规则（主设备没录、或它的机型不在规则表里）——"
                        + "那些面班产为 0，装箱排不出任务。补法：在「作业面台账」填主设备，"
                        + "并确认该机型在设备编组规则里有一条在役规则。");
        if (noTarget > 0)
            asm.Notes.Add($"· {noTarget} 个面当日目标为 0（配了没量）—— 装箱会把它们排成空闲，不是漏排。");
    }

    /// <summary>设备编号 → 设备类别（电铲/钻机/推土机…）。持证校核按类别匹配，不能按设备号猜。</summary>
    public static Dictionary<string, string> CategoryOfEquipment(DbConnection? conn)
        => ColumnOfEquipment(conn, "category");

    /// <summary>设备编号 → 机型（编组规则按机型对）。读不到就返回空表，逐面退回按机型名直接对。</summary>
    private static Dictionary<string, string> ModelOfEquipment(DbConnection? conn)
        => ColumnOfEquipment(conn, "model");

    private static Dictionary<string, string> ColumnOfEquipment(DbConnection? conn, string column)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (conn == null) return map;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT equipment_id, {column} FROM equipment WHERE {column} IS NOT NULL";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                string id = rd.IsDBNull(0) ? "" : rd.GetValue(0)?.ToString() ?? "";
                string v = rd.IsDBNull(1) ? "" : rd.GetValue(1)?.ToString() ?? "";
                if (id.Length > 0 && v.Length > 0) map[id] = v;
            }
        }
        catch { /* 台账读不到就退回按机型名直接对 */ }
        return map;
    }

    /// <summary>
    /// 给一个面配上编组：主设备 → 机型 → 编组规则 → 班产/荐车数/周期分解。
    /// <b>对不上就留空</b>（班产 0），由调用方点名报出来 —— 不拿一个"典型班产"顶上去。
    /// </summary>
    internal static void AttachGroup(FaceInput face, IReadOnlyList<FleetDispatchRule> rules,
                                     IReadOnlyDictionary<string, string> models)
    {
        if (face == null) return;
        string equip = face.Group.MainEquipment;
        if (equip.Length == 0) return;

        string model = models.TryGetValue(equip, out var m) ? m : equip;
        // 一个铲型往往配着好几种车型（真库里 2800XP 就有 8 条）。取**效率评分最高**的那条 ——
        // 与「编组优化」排序用的是同一个键；随手取第一条会让班产随表的插入顺序变，而且不报错。
        var rule = Best(rules.Where(r => Eq(r.ShovelModel, model)))
                ?? Best(rules.Where(r => Contains(model, r.ShovelModel) || Contains(r.ShovelModel, model)));
        if (rule == null) return;

        // ★ 与「编组优化」共用同一份物理口径（FleetOptimizer.Capacity）——
        //   各写一份的话，那边说得出的班产与这边排出来的量会是两个数，而两边各自都自洽。
        var cap = FleetOptimizer.Capacity(rule, new FleetOptInput());
        double hoursPerDay = Math.Max(0.1, new FleetOptInput().WorkHoursPerDay);

        face.Group.RecommendedTrucks = rule.RecommendedTruckCount;
        face.Group.GroupCapacityM3PerH = cap.DailyM3 / hoursPerDay;
        face.Group.LoadTaktMin = cap.LoadMin;
        face.Group.CycleTimeMin = cap.CycleMin;
        face.Group.MatchFactor = cap.MatchFactor;
        face.Group.TruckPayloadT = rule.TruckPayloadT;      // 派车单按吨排，吨是守恒量
        // 车号台账里没有「这台铲今天配哪几辆车」这张表，故按荐车数占位：
        // 运力校核判的是**配车数 vs 荐车数**，占位与荐车数相等 ⇒ 不会误报运力不足。
        // 真车号要等派车单那一步（未移植）。
        if (face.Group.Trucks.Count == 0)
            for (int i = 0; i < rule.RecommendedTruckCount; i++)
                face.Group.Trucks.Add($"{rule.TruckModel}#{i + 1}");
    }

    private static FleetDispatchRule? Best(IEnumerable<FleetDispatchRule> rs)
        => rs.OrderByDescending(r => r.EfficiencyScore)
             .ThenBy(r => r.TruckModel, StringComparer.Ordinal)   // 评分并列时按车型名定序，别让结果随表序漂
             .FirstOrDefault();

    private static bool Eq(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? outer, string? inner)
        => !string.IsNullOrWhiteSpace(outer) && !string.IsNullOrWhiteSpace(inner)
        && outer.Trim().Contains(inner.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Hm(double hh)
    {
        int h = (int)hh;
        int m = (int)Math.Round((hh - h) * 60);
        if (m == 60) { h++; m = 0; }
        return $"{h:00}:{m:00}";
    }
}
