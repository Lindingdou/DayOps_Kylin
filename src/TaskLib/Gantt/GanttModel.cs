// 忠实移植自原 PitMine3D Modules/TaskLib/Gantt/GanttModel.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Gantt;

// ─────────────────────────────────────────────────────────────────────────────
//  甘特图渲染视图模型（按日·三班）。渲染只认 行 × 条；条按 ProcessType 配色。
//  数据从 ProductionTask[] 投影而来（FromTasks*）——甘特、报表、任务书共用同一份任务台账。
//  每根条挂着它的 ProductionTask（Task），供点击出明细卡。
//
//  四个视图维度：
//    · 按设备类型 FromTasksByEquipType —— 电铲/卡车/钻机/推土机 各类成组排班
//    · 按设备     FromTasks            —— 逐台设备一行
//    · 按作业区   FromTasksByZone      —— 过程归到各作业区域（源侧视角）
//    · 按去向     FromTasksByDestination —— 排土场/破碎站/煤仓各成一组（汇侧视角）
//      前三个都在回答"谁在哪干活"，只有第四个回答"今天哪个排土场最忙、会不会满"，
//      并且把同一去向的【采装条】与【排土条】排进同一分组——采排配对在图上看得见。
//
//  卡车条不是影子条：按编组求解出的循环时间 T_c 与装车节拍 τ_L 展成 N 个车次小条
//  （见 TruckTripPlanner）；解不出时才回落成整段影子条。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>甘特图里的一根条：某设备在 [StartHour,EndHour) 干某工序。</summary>
public sealed class GanttBar
{
    public double StartHour { get; set; }
    public double EndHour { get; set; }
    public ProcessType Process { get; set; }
    public string Label { get; set; } = "";
    public bool Hatch { get; set; }                 // 检修/空闲 → 斜纹
    public ProductionTask? Task { get; set; }       // 背后的任务（点击出明细）

    /// <summary>去向色（同一去向的采装条与排土条共用一色，作视觉配对）。空=无去向。</summary>
    public string TintHex { get; set; } = "";
    /// <summary>悬停提示（车次条写"第 k 趟 · 去 XX · 运距 Y km"）。</summary>
    public string Tooltip { get; set; } = "";

    // ── 车次条专有 ──
    /// <summary>第几趟（1 起）；0 = 不是车次条。</summary>
    public int TripIndex { get; set; }
    /// <summary>本车本任务共几趟。</summary>
    public int TripCount { get; set; }
    /// <summary>这根条属于哪台卡车。</summary>
    public string TruckId { get; set; } = "";
    /// <summary>影子条：编组解不出时的回落形态（整段一根，不代表真实车次）。</summary>
    public bool IsShadow { get; set; }

    // ── 混采任务的车次条：这一趟走的是哪条线（单去向任务与主去向相同）──
    /// <summary>本趟拉的物料名（"煤" / "硬岩"）；空 = 不是车次条。</summary>
    public string TripMaterial { get; set; } = "";
    /// <summary>本趟的去向显示名。</summary>
    public string TripSink { get; set; } = "";
    /// <summary>本趟的运距 km（等效优先）——混采两条线不是一个数。</summary>
    public double TripHaulKm { get; set; }
    /// <summary>本趟自己那条线的循环时间 T_c（min），不是加权值。</summary>
    public double TripCycleMin { get; set; }

    public GanttBar() { }
    public GanttBar(double s, double e, ProcessType p, string label, bool hatch = false)
    { StartHour = s; EndHour = e; Process = p; Label = label; Hatch = hatch; }
}

/// <summary>甘特图里的一行：一台设备（卡车作子行缩进）。</summary>
public sealed class GanttRow
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";   // 电铲/卡车/钻机/推土机 → 行首色点
    public bool IsSub { get; set; }              // 子行（配属卡车缩进）
    public string EquipId { get; set; } = "";    // 设备编号（投影匹配用）
    public bool IsZoneHeader { get; set; }       // 分组头（按作业区 / 按设备类型 / 按去向）
    public string ZoneColorHex { get; set; } = "";
    /// <summary>分组头右侧的补充说明（按去向视图：今日入方占容 / 剩余库容）。</summary>
    public string Note { get; set; } = "";
    /// <summary>分组头的告警底色（库容/通过能力见顶时置 true）。</summary>
    public bool NoteAlert { get; set; }
    public List<GanttBar> Bars { get; set; } = new();
}

/// <summary>一天的甘特数据：行集 + 现在线 + 爆破窗口 + 当日工作量达成。</summary>
public sealed class DailyGanttModel
{
    public string DateLabel { get; set; } = "";
    public double NowHour { get; set; } = 10.5;
    public double BlastStart { get; set; } = 15.0;
    public double BlastEnd { get; set; } = 15.67;

    /// <summary>
    /// 本日<b>全部</b>爆破停产时窗。空 = 回落 <see cref="BlastStart"/>/<see cref="BlastEnd"/> 那一段。
    /// <para>
    /// 一天两三炮时只画最早那一段，图上就会出现「这个时段明明在清场，条却排得满满的」——
    /// 而装箱自 2026-08-11 起是按全部时窗扣的，图与计划必须说同一件事。
    /// </para>
    /// </summary>
    public List<Engine.BlastWindow> Blasts { get; set; } = new();

    /// <summary>
    /// 当日班制（顶部班次标签行与班次分隔线按它画）。
    /// <para>
    /// ★ 渲染器原先把「早班 00–08 / 中班 08–16 / 夜班 16–24」和 8h/16h 两条分隔线**写死**在代码里。
    /// 班次日历改成四班、或只是把交接从 8:00 挪到 7:30，甘特表头照旧那么写 —— 图在说谎，
    /// 而条的位置又是按真实时窗排的，两者对不上却毫无提示。
    /// </para>
    /// <para>空表时渲染器退回三等分兜底（脱离盘子的离线台架就是这种情形）。</para>
    /// </summary>
    public List<Engine.ShiftWindow> Shifts { get; set; } = new();

    public List<GanttRow> Rows { get; set; } = new();

    // 当日工作量（万m³，材料 = 采装 + 排土）
    public double DayPlanWanM3 { get; set; }
    public double DayActualWanM3 { get; set; }
    public double PlanToNowWanM3 { get; set; }   // 至「现在」应完成
    public double AttainmentPct { get; set; }

    /// <summary>当日计划运输功 万t·km（Σ 采装任务的 吨量×等效运距）。</summary>
    public double TransportWorkWanTKm { get; set; }
    /// <summary>吨量加权平均运距 km。</summary>
    public double WeightedAvgHaulKm { get; set; }

    public string NowText
    {
        get
        {
            int hh = (int)NowHour;
            int mm = (int)System.Math.Round((NowHour - hh) * 60);
            return $"{hh:00}:{mm:00}";
        }
    }

    // ── 去向配色（同一去向的条同色相；按首次出现顺序取色）────────────────────
    private static readonly string[] DestPalette =
    {
        "#FF0F6E56", "#FF185FA5", "#FF854F0B", "#FF534AB7",
        "#FFA32D2D", "#FF0F7B7B", "#FF7A5C0F", "#FF6B2E7A",
    };

    /// <summary>
    /// 任务的去向键：先认登记簿里的汇 Id（采装的卸点、排土场自身都归到同一个汇），
    /// 台账不可用时退回 Id/名字文本。空串 = 无去向。
    /// </summary>
    internal static string DestKeyOf(ProductionTask t)
    {
        var sink = TruckTripPlanner.SinkOf(t);
        if (sink != null) return sink.Id;
        if (!string.IsNullOrWhiteSpace(t.DestinationId)) return t.DestinationId;
        if (!string.IsNullOrWhiteSpace(t.DestinationName)) return t.DestinationName;
        if (t.Process == ProcessType.Dump && !string.IsNullOrWhiteSpace(t.WorkZone)) return t.WorkZone;
        return "";
    }

    /// <summary>需要卸点的工序：只有把料运出去的任务才谈得上"排到哪"（穿孔/检修/场内排土不要求）。</summary>
    internal static bool NeedsDestination(ProductionTask t)
        => t.Process is ProcessType.Load or ProcessType.Haul;

    // ── 混采分项去向：甘特 / 任务书 / 台账共用同一口径 ────────────────────────
    //  一台铲在同一时窗挖混采料（煤7∶岩3），煤去破碎站、岩去排土场——一条任务多个去向。
    //  拆成两条任务会撞「设备双占」，所以去向必须挂在任务的物料分项上。

    /// <summary>
    /// 任务的逐物料去向：有分项走分项，无分项则按 ResolvedMix 逐物料回落主去向。
    /// 返回的是副本，调用方改 Fraction 不会污染任务本体。
    /// </summary>
    internal static List<MaterialDestination> RoutesOf(ProductionTask t)
    {
        var list = new List<MaterialDestination>();
        foreach (var sh in t.ResolvedMix.Normalized().Shares)
        {
            if (sh.Fraction <= 1e-6) continue;
            var d = t.DestinationFor(sh.MaterialCode).Clone();
            d.Fraction = sh.Fraction;
            list.Add(d);
        }
        return list;
    }

    /// <summary>
    /// 是否**逐物料**都定了去向。混采任务缺任一物料的去向都不算定完——
    /// 只判 HasDestination 会让"煤定了破碎站、岩没人管"的任务静默过关，任务书就此签发出去。
    /// </summary>
    internal static bool AllMaterialsRouted(ProductionTask t)
    {
        var routes = RoutesOf(t);
        return routes.Count > 0 && routes.All(d => d.HasDestination);
    }

    /// <summary>尚未定去向的物料名（供告警点名）。</summary>
    internal static List<string> UnroutedMaterials(ProductionTask t)
        => RoutesOf(t).Where(d => !d.HasDestination).Select(d => d.Spec.Name).Distinct().ToList();

    /// <summary>
    /// 多去向摘要："煤 60% → 1号破碎站 · 硬岩 40% → 内排场"。
    /// 单去向（只有一条分项）返回空串——单据上写一遍就够了。
    /// </summary>
    internal static string SplitCaption(ProductionTask t)
    {
        var routes = RoutesOf(t);
        return routes.Count < 2 ? "" : string.Join(" · ", routes.Select(d => d.Caption));
    }

    /// <summary>
    /// 吨量加权运距 km —— 混采时煤走 2.6km、岩走 1.4km 是两条腿，
    /// 拿主去向的运距一刀切会把运输功算错。无分项时等同 EffectiveHaulKm。
    /// </summary>
    internal static double WeightedHaulKm(ProductionTask t)
    {
        double work = 0, ton = 0;
        foreach (var fl in t.ToFlows("", t.TargetVolumeM3))
        {
            if (fl.EffectiveHaulKm <= 1e-6) continue;
            work += fl.TransportWorkTKm; ton += fl.TonnageT;
        }
        return ton <= 1e-6 ? t.EffectiveHaulKm : work / ton;
    }

    // 分组哨兵键：前缀 U+0001 保证不会与任何真实汇 Id 撞名（与 FromTasksByDestination 内的局部常量同值）
    private const string DestNoneKey = "none";
    private const string DestOtherKey = "other";

    /// <summary>
    /// 一条任务计入某个去向的那一份。混采任务（煤7∶岩3）会产生**多份**：
    /// 煤那份计入破碎站、岩那份计入排土场，量按各自物料的份额拆，而不是整条算给主去向。
    /// 组头的入方统计因此才准——此前混采面的岩量被整份记到煤的破碎站上，排土场看着永远不忙。
    /// </summary>
    private sealed class DestPart
    {
        public string Key = "";
        public ProductionTask Task = null!;
        public MaterialDestination? Route;      // 该份对应的物料分项（无分项时 null）
        public double InSituM3;                 // 实方 m³
        public double DumpM3;                   // 占容方 m³（排土库容按它扣）
        public double TonnageT;                 // 吨量
        public string MaterialsLabel = "";      // "煤" / "硬岩、夹矸"
    }

    /// <summary>
    /// 把一条任务拆成「按去向计的若干份」。走 <see cref="ProductionTask.ToFlows(string,double)"/>——
    /// 它已按分项逐物料挂各自的去向，落到同一个汇的多种物料再合并成一份。
    /// 不外运的任务（穿孔/检修/场内排土）或无分项时整条算一份，口径与改造前一致。
    /// </summary>
    private static List<DestPart> DestPartsOf(ProductionTask t)
    {
        var parts = new List<DestPart>();

        if (NeedsDestination(t) && t.HasSplits && t.TargetVolumeM3 > 1e-6)
        {
            foreach (var g in t.ToFlows("", t.TargetVolumeM3)
                               .GroupBy(FlowKey, StringComparer.OrdinalIgnoreCase))
            {
                var flows = g.ToList();
                parts.Add(new DestPart
                {
                    Key = g.Key,
                    Task = t,
                    Route = t.DestinationFor(flows[0].MaterialCode),
                    InSituM3 = flows.Sum(f => f.InSituM3),
                    DumpM3 = flows.Sum(f => f.DumpM3),
                    TonnageT = flows.Sum(f => f.TonnageT),
                    MaterialsLabel = string.Join("、", flows.Select(f => f.Spec.Name).Distinct()),
                });
            }
            if (parts.Count > 0) return parts;
        }

        // 回落：整条算给单去向
        string k = DestKeyOf(t);
        if (k.Length == 0) k = NeedsDestination(t) ? DestNoneKey : DestOtherKey;
        parts.Add(new DestPart
        {
            Key = k, Task = t,
            InSituM3 = t.TargetVolumeM3, DumpM3 = t.TargetDumpM3, TonnageT = t.TargetTonnageT,
            MaterialsLabel = t.ResolvedMix.Caption,
        });
        return parts;
    }

    /// <summary>物料流的分组键：先归一到登记簿里的汇 Id，对不上退回 Id/名字文本；全空=未指定卸点。</summary>
    private static string FlowKey(MaterialFlow f)
    {
        var sink = SinkByRef(f.SinkId, f.SinkName);
        if (sink != null) return sink.Id;
        if (!string.IsNullOrWhiteSpace(f.SinkId)) return f.SinkId;
        if (!string.IsNullOrWhiteSpace(f.SinkName)) return f.SinkName;
        return DestNoneKey;
    }

    /// <summary>按 Id/名字查去向登记簿（分项用；台账不可用返回 null，调用方按名兜底）。</summary>
    internal static SinkNode? SinkByRef(string? id, string? name)
    {
        try
        {
            var reg = SinkRegistryLoader.Current;
            var s = reg.Find(id);
            if (s == null && !string.IsNullOrWhiteSpace(name))
                s = reg.All.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return s;
        }
        catch { return null; }
    }

    /// <summary>
    /// 去向配色表。除了各任务的主去向，还要把**混采分项的去向**一并取色——
    /// 否则煤那条腿有色、岩那条腿（去了另一个排土场）在图上就是灰的，两条线看不出区别。
    /// </summary>
    private static Dictionary<string, string> DestTints(IEnumerable<ProductionTask> tasks)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = tasks as IList<ProductionTask> ?? tasks.ToList();

        var keys = list.Select(DestKeyOf)
                       .Concat(list.SelectMany(t => RoutesOf(t).Select(RouteKeyOf)))
                       .Where(k => !string.IsNullOrEmpty(k))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        for (int i = 0; i < keys.Count; i++) map[keys[i]] = DestPalette[i % DestPalette.Length];
        return map;
    }

    /// <summary>分项去向的分组键：与 <see cref="DestKeyOf"/>/FlowKey 同一套归一化（先认登记簿 Id）。</summary>
    internal static string RouteKeyOf(MaterialDestination d)
    {
        var sink = SinkByRef(d.DestinationId, d.DestinationName);
        if (sink != null) return sink.Id;
        if (!string.IsNullOrWhiteSpace(d.DestinationId)) return d.DestinationId;
        if (!string.IsNullOrWhiteSpace(d.DestinationName)) return d.DestinationName;
        return "";
    }

    /// <summary>车次条的去向键（本趟自己那条线，不是任务的主去向）。</summary>
    private static string TripKeyOf(TruckTrip tp)
    {
        var sink = SinkByRef(tp.SinkId, tp.SinkName);
        if (sink != null) return sink.Id;
        if (!string.IsNullOrWhiteSpace(tp.SinkId)) return tp.SinkId;
        if (!string.IsNullOrWhiteSpace(tp.SinkName)) return tp.SinkName;
        return DestNoneKey;
    }

    private static string TintOf(ProductionTask t, IReadOnlyDictionary<string, string> tints)
    {
        string k = DestKeyOf(t);
        return k.Length > 0 && tints.TryGetValue(k, out var hex) ? hex : "";
    }

    private static string TintOfKey(string key, IReadOnlyDictionary<string, string> tints)
        => key.Length > 0 && tints.TryGetValue(key, out var hex) ? hex : "";

    // ── 投影 ─────────────────────────────────────────────────────────────────

    /// <summary>从任务台账投影出一天的甘特（行=设备，条=任务；卡车行由采装任务的编组展开成车次）。</summary>
    public static DailyGanttModel FromTasks(
        IList<ProductionTask> tasks, IList<RosterEntry> roster,
        string dateLabel, double nowHour, double blastStart, double blastEnd)
    {
        var m = New(dateLabel, nowHour, blastStart, blastEnd);
        var tints = DestTints(tasks);

        var rowById = new Dictionary<string, GanttRow>();
        foreach (var r in roster)
        {
            var row = new GanttRow { Name = r.Display, Category = r.Category, IsSub = r.Sub, EquipId = r.EquipId };
            m.Rows.Add(row);
            rowById[r.EquipId] = row;
        }

        foreach (var t in tasks)
        {
            // 主设备行：本任务一根条
            if (rowById.TryGetValue(t.Group.MainEquipment, out var mainRow))
                mainRow.Bars.Add(MainBar(t, tints));

            // 采装任务 → 配属卡车行各展一串车次条
            if (t.Process == ProcessType.Load)
                foreach (var truck in t.Group.Trucks)
                    if (rowById.TryGetValue(truck, out var trkRow))
                        AddTruckBars(trkRow, t, truck, tints);
        }

        FillTotals(m, tasks);
        return m;
    }

    /// <summary>按作业区域分组投影：每个作业区一条分组头，其下挂在该区作业的设备行（过程关联到区）。</summary>
    public static DailyGanttModel FromTasksByZone(
        IList<ProductionTask> tasks, IList<RosterEntry> roster,
        string dateLabel, double nowHour, double blastStart, double blastEnd)
    {
        var m = New(dateLabel, nowHour, blastStart, blastEnd);
        var tints = DestTints(tasks);

        // 作业区顺序：按区内最早开始的任务
        var zones = tasks.GroupBy(t => t.WorkZone).OrderBy(g => g.Min(t => t.StartHour)).ToList();
        foreach (var zg in zones)
        {
            // 区主色 = 区内首个非检修非运输任务的工序色
            var mainProc = zg.FirstOrDefault(t => t.Process is not ProcessType.Idle and not ProcessType.Haul)?.Process ?? ProcessType.Idle;
            m.Rows.Add(new GanttRow { Name = zg.Key, IsZoneHeader = true, ZoneColorHex = ZoneColor(mainProc) });

            AddEquipRows(m, zg.ToList(), roster, tints);
        }

        FillTotals(m, tasks);
        return m;
    }

    /// <summary>
    /// <b>按作业面 · 工序链</b>分组投影：一个面一组，组里**一道工序一行**，从上往下就是
    /// 穿孔 → 爆破 → 采装 → 运输 → 排土。
    ///
    /// <para>
    /// <b>它解决的是"衔接看不出来"</b>：按设备分组时，同一个面的五道工序散在五台设备的行里，
    /// 谁接谁全靠人对时间。摞到一起之后，前一道的条什么时候完、后一道什么时候起、
    /// 中间空了多久（等待），一眼就是一条链。
    /// </para>
    /// <para>没有这道工序的面就没有那一行 —— <b>空着就是空着</b>，不画一条空行占位。</para>
    /// </summary>
    public static DailyGanttModel FromTasksByFaceChain(
        IList<ProductionTask> tasks, IList<RosterEntry> roster,
        string dateLabel, double nowHour, double blastStart, double blastEnd)
    {
        var m = New(dateLabel, nowHour, blastStart, blastEnd);
        var tints = DestTints(tasks);

        var chain = new[] { ProcessType.Drill, ProcessType.Blast, ProcessType.Load,
                            ProcessType.Haul, ProcessType.Dump };

        // 面按"今天有多少活"排（工时降序）——最忙的面在最上面
        var byFace = tasks.Where(t => t != null && !string.IsNullOrWhiteSpace(t.WorkZone))
                          .GroupBy(t => t.WorkZone.Trim())
                          .OrderByDescending(g => g.Sum(t => Math.Max(0, t.EndHour - t.StartHour)))
                          .ThenBy(g => g.Key, StringComparer.Ordinal)
                          .ToList();

        foreach (var g in byFace)
        {
            var list = g.ToList();
            double hours = list.Sum(t => Math.Max(0, t.EndHour - t.StartHour));
            double volume = list.Where(t => t.Process == ProcessType.Load).Sum(t => t.TargetVolumeM3);

            m.Rows.Add(new GanttRow
            {
                Name = g.Key,
                IsZoneHeader = true,
                ZoneColorHex = "#FF3F6E9E",
                Note = $"工序 {list.Select(t => t.Process).Distinct().Count()}/5 道"
                     + (volume > 1e-6 ? $"　采装 {volume / 1e4:0.##} 万m³" : "")
                     + $"　占用 {hours:0.#}h",
            });

            foreach (var p in chain)
            {
                var of = list.Where(t => t.Process == p).OrderBy(t => t.StartHour).ToList();
                if (of.Count == 0) continue;                 // 没有这道工序就不占一行

                // 行名带上干这道工序的设备（同一道工序在一个面上通常就一台）
                var equips = of.Select(t => (t.Group?.MainEquipment ?? "").Trim())
                               .Where(x => x.Length > 0).Distinct().ToList();
                var row = new GanttRow
                {
                    Name = ProcessZh(p) + (equips.Count > 0 ? "　" + string.Join("/", equips.Take(2)) : ""),
                    Category = CategoryOfProcess(p),
                    EquipId = g.Key + "|" + p,               // 唯一键，不参与设备匹配
                };
                foreach (var t in of) row.Bars.Add(MainBar(t, tints));
                m.Rows.Add(row);
            }
        }

        FillTotals(m, tasks);
        return m;
    }

    private static string ProcessZh(ProcessType p) => p switch
    {
        ProcessType.Drill => "穿孔",
        ProcessType.Blast => "爆破",
        ProcessType.Load => "采装",
        ProcessType.Haul => "运输",
        ProcessType.Dump => "排土",
        _ => p.ToString(),
    };

    /// <summary>这道工序归哪一类设备（行首色点用它）。</summary>
    private static string CategoryOfProcess(ProcessType p) => p switch
    {
        ProcessType.Drill => "钻机",
        ProcessType.Blast => "爆破",
        ProcessType.Load => "电铲",
        ProcessType.Haul => "卡车",
        ProcessType.Dump => "推土机",
        _ => "",
    };

    /// <summary>按设备类型分组投影：电铲/卡车/钻机/推土机 各成一组（排班按设备类型成组处理）。</summary>
    public static DailyGanttModel FromTasksByEquipType(
        IList<ProductionTask> tasks, IList<RosterEntry> roster,
        string dateLabel, double nowHour, double blastStart, double blastEnd)
    {
        var m = New(dateLabel, nowHour, blastStart, blastEnd);
        var tints = DestTints(tasks);

        // 工序链的顺序：穿孔 → 爆破 → 采装 → 运输 → 排土。类别序照着它排，
        // 图上从上往下读就是一条工序链，而不是"设备台账的字典序"。
        string[] order = { "钻机", "爆破", "电铲", "卡车", "推土机" };
        var types = roster.Select(r => r.Category).Distinct()
            .OrderBy(c => { int i = Array.IndexOf(order, c); return i < 0 ? 99 : i; });

        foreach (var type in types)
        {
            m.Rows.Add(new GanttRow { Name = type, IsZoneHeader = true, ZoneColorHex = TypeColor(type) });
            foreach (var r in roster.Where(x => x.Category == type))
            {
                var row = new GanttRow { Name = r.Display, Category = r.Category, EquipId = r.EquipId };
                FillRowBars(row, tasks, tints);
                m.Rows.Add(row);
            }
        }

        FillTotals(m, tasks);
        return m;
    }

    /// <summary>
    /// 按去向分组投影（汇侧视角）：每个排土场/破碎站/煤仓一条分组头，
    /// 头上写【今日入方占容方 + 剩余库容】——回答"今天哪个排土场最忙、会不会满"。
    /// 同一去向的采装条与排土条排在同一分组下，采—排配对一眼可见。
    /// 无卸点的采装/运输任务单列一组（红），穿孔/检修等不外运的任务归"其它作业"。
    /// </summary>
    public static DailyGanttModel FromTasksByDestination(
        IList<ProductionTask> tasks, IList<RosterEntry> roster,
        string dateLabel, double nowHour, double blastStart, double blastEnd)
    {
        const string NoneKey = "none";
        const string OtherKey = "other";

        var m = New(dateLabel, nowHour, blastStart, blastEnd);
        var tints = DestTints(tasks);

        // 混采任务在这里被拆成多份：煤那份计入破碎站组、岩那份计入排土场组，量按各自物料份额拆，
        // 而不是整条算给主去向——组头的入方统计因此才准。
        var groups = tasks.SelectMany(DestPartsOf).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();

        // 排序：有去向的按今日入方吨量降序（最忙的排最前）→ 未指定卸点 → 其它作业
        var ordered = groups
            .OrderBy(g => g.Key == OtherKey ? 2 : g.Key == NoneKey ? 1 : 0)
            .ThenByDescending(g => g.Where(p => NeedsDestination(p.Task)).Sum(p => p.TonnageT))
            .ToList();

        foreach (var g in ordered)
        {
            var list = g.ToList();
            var groupTasks = list.Select(p => p.Task).Distinct().ToList();
            GanttRow header;

            if (g.Key == NoneKey)
            {
                // 混采任务可能只缺其中一种物料的去向——点名说清是哪一份，别让人以为整条都没定。
                // 判据是「分项 > 1 条」而非 HasSplits：单一物料面也会被写回一条分项，那不算混采。
                var partial = list.Where(p => p.Task.Splits.Count > 1 && p.MaterialsLabel.Length > 0).ToList();
                header = new GanttRow
                {
                    Name = "未指定卸点", IsZoneHeader = true, ZoneColorHex = "#FFA32D2D", NoteAlert = true,
                    Note = $"{groupTasks.Count} 项采装/运输任务无去向"
                         + (partial.Count > 0
                             ? $"（其中 {partial.Count} 项只缺混采面的部分物料：{string.Join("、", partial.Select(p => p.MaterialsLabel).Distinct())}）"
                             : "")
                         + " —— 任务书不得签发；请先在流向分配指派卸点",
                };
            }
            else if (g.Key == OtherKey)
            {
                header = new GanttRow
                {
                    Name = "其它作业（穿孔 / 爆破 / 检修）", IsZoneHeader = true, ZoneColorHex = "#FF888888",
                    Note = "不外运、不占卸点库容",
                };
            }
            else
            {
                header = DestHeader(list, tints);
            }

            m.Rows.Add(header);
            // 车次按「每趟计入它自己那条线的去向」归组：混采任务的煤趟只出现在破碎站组、
            // 岩趟只出现在排土场组，与组头的入方统计（DestPartsOf 已按分项拆量）对得上。
            AddEquipRows(m, groupTasks, roster, tints, destFilter: g.Key == OtherKey ? null : g.Key);
        }

        FillTotals(m, tasks);
        return m;
    }

    /// <summary>去向分组头：去向名 + 类型 + 今日入方（占容方/吨量）+ 剩余库容 / 通过能力。</summary>
    private static GanttRow DestHeader(List<DestPart> list, IReadOnlyDictionary<string, string> tints)
    {
        var lead = list.FirstOrDefault(p => NeedsDestination(p.Task)) ?? list[0];
        var first = lead.Task;
        var sink = lead.Route is { HasDestination: true }
            ? SinkByRef(lead.Route.DestinationId, lead.Route.DestinationName)
            : TruckTripPlanner.SinkOf(first);

        string name = sink?.Name
                      ?? (lead.Route is { HasDestination: true }
                          ? (string.IsNullOrWhiteSpace(lead.Route.DestinationName) ? lead.Route.DestinationId : lead.Route.DestinationName)
                          : (string.IsNullOrWhiteSpace(first.DestinationName) ? first.DestinationId : first.DestinationName));
        if (string.IsNullOrWhiteSpace(name)) name = first.WorkZone;
        var kind = sink?.Kind ?? lead.Route?.DestinationKind ?? first.DestinationKind;

        // 入方只统计"运进来的"（采装/运输），排土任务本身是这批料的落地作业，再计一遍就重了。
        // 量取各份自己的（混采已按物料份额拆过），不是整条任务的。
        var inbound = list.Where(p => NeedsDestination(p.Task)).ToList();
        double inSitu = inbound.Sum(p => p.InSituM3);
        double dump = inbound.Sum(p => p.DumpM3);             // 占容方：排土库容按它扣
        double ton = inbound.Sum(p => p.TonnageT);

        // 混采面只有一部分料投到这里时说清楚，免得看着像"整个面都拉过来了"。
        // 单一物料面也会被写回一条分项，那不算混采（判 Splits.Count > 1），否则每个组头都要刷一串。
        var mixedFrom = inbound.Where(p => p.Task.Splits.Count > 1 && p.MaterialsLabel.Length > 0)
                               .Select(p => $"{p.Task.WorkZone} 的{p.MaterialsLabel}")
                               .Distinct().ToList();

        string note;
        bool alert = false;
        if (kind.IsDumping())
        {
            note = $"今日入方 {dump / 1e4:0.##}万m³占容（实方 {inSitu / 1e4:0.##}万m³ · {ton / 1e4:0.##}万t）";
            if (sink is { IsCapacityLimited: true })
            {
                double rem = sink.RemainingM3;
                note += $" ｜ {sink.CapacityCaption}";
                if (rem > 1e-6) note += $" · 本日占余容 {dump / rem * 100:0.#}%";
                if (dump > rem) { note += " ⚠ 库容不足"; alert = true; }
            }
            else if (sink != null) note += " ｜ 容量不限";
        }
        else
        {
            note = $"今日进 {ton / 1e4:0.##}万t（实方 {inSitu / 1e4:0.##}万m³）";
            if (sink is { AcceptTph: > 0 })
            {
                double span = Math.Max(1.0, inbound.Count == 0 ? 1.0
                    : inbound.Max(p => p.Task.EndHour) - inbound.Min(p => p.Task.StartHour));
                double needTph = ton / span;
                note += $" ｜ 通过能力 {sink.AcceptTph:0} t/h · 本日需 {needTph:0} t/h";
                if (needTph > sink.AcceptTph) { note += " ⚠ 卸点能力不足"; alert = true; }
            }
            else note += " ｜ 通过能力不限";
        }

        if (mixedFrom.Count > 0) note += $" ｜ 含混采分项：{string.Join("、", mixedFrom)}";
        if (sink is { IsActive: false }) { note += $" ⚠ 状态 {sink.Status}"; alert = true; }

        string tint = TintOf(first, tints);
        return new GanttRow
        {
            Name = $"{name}（{kind.Label()}）",
            IsZoneHeader = true,
            ZoneColorHex = alert ? "#FFA32D2D" : (tint.Length > 0 ? tint : "#FF888888"),
            Note = note,
            NoteAlert = alert,
        };
    }

    /// <summary>
    /// 给一组任务铺设备行（主设备 + 该组采装任务的配属卡车），按 roster 顺序。
    /// <paramref name="destFilter"/> 非空时（按去向视图）只画投向该去向的车次——
    /// 混采任务的煤趟归破碎站组、岩趟归排土场组，组头统计与图上的条才对得上。
    /// </summary>
    private static void AddEquipRows(DailyGanttModel m, List<ProductionTask> group,
                                     IList<RosterEntry> roster, IReadOnlyDictionary<string, string> tints,
                                     string? destFilter = null)
    {
        foreach (var r in roster)
        {
            bool mainHere = group.Any(t => t.Group.MainEquipment == r.EquipId);
            bool truckHere = group.Any(t => t.Process == ProcessType.Load && t.Group.Trucks.Contains(r.EquipId));
            if (!mainHere && !truckHere) continue;

            var row = new GanttRow { Name = r.Display, Category = r.Category, IsSub = r.Sub, EquipId = r.EquipId };
            foreach (var t in group)
            {
                if (t.Group.MainEquipment == r.EquipId) row.Bars.Add(MainBar(t, tints));
                if (t.Process == ProcessType.Load && t.Group.Trucks.Contains(r.EquipId))
                    AddTruckBars(row, t, r.EquipId, tints, destFilter);
            }
            m.Rows.Add(row);
        }
    }

    /// <summary>给一行（按 EquipId）填条：本设备的任务 + 作为编组卡车时的车次条。</summary>
    private static void FillRowBars(GanttRow row, IList<ProductionTask> tasks, IReadOnlyDictionary<string, string> tints)
    {
        // ★ **没有执行人的工序**（爆破：按口径不指人，爆破队台账还没有）落在这一行上。
        //   甘特是按"设备行"画条的：主设备为空的任务匹配不到任何一行，条就画不出来 ——
        //   于是工序链里「穿孔→爆破→采装」中间那一环在图上永远是空的，而任务其实排了，
        //   而且 M3 正拿它挡着采装。花名册给了一行「爆破（未指定队组）」，这里把它们收进来。
        bool ownerless = row.Category == "爆破";

        foreach (var t in tasks)
        {
            if (ownerless)
            {
                if (string.IsNullOrWhiteSpace(t.Group.MainEquipment) && t.Process == ProcessType.Blast)
                    row.Bars.Add(MainBar(t, tints));
                continue;
            }

            if (t.Group.MainEquipment == row.EquipId) row.Bars.Add(MainBar(t, tints));
            if (t.Process == ProcessType.Load && t.Group.Trucks.Contains(row.EquipId))
                AddTruckBars(row, t, row.EquipId, tints);
        }
    }

    private static GanttBar MainBar(ProductionTask t, IReadOnlyDictionary<string, string> tints)
        => new(t.StartHour, t.EndHour, t.Process, BarLabel(t), t.Process == ProcessType.Idle)
        {
            Task = t,
            TintHex = TintOf(t, tints),
            Tooltip = MainTooltip(t),
        };

    /// <summary>
    /// 卡车行：把任务时段按 T_c / τ_L 展成 N 个车次小条；
    /// 编组解不出（无去向 / 运距不可解 / 台账异常）时回落成整段影子条。
    /// <para>
    /// 混采任务的 N 趟已按车次份额分摊到两条线上（见 <c>TruckTripPlanner.AllocateTripLegs</c>）：
    /// 每根条戴的是**它自己那条线**的去向色、写的是它自己那条线的物料与运距，
    /// 条长也取该线的 T_c —— 两条线节拍不同，在图上就是长短不一的条。
    /// </para>
    /// </summary>
    private static void AddTruckBars(GanttRow row, ProductionTask t, string truckId,
                                     IReadOnlyDictionary<string, string> tints, string? destFilter = null)
    {
        string tint = TintOf(t, tints);
        var trips = TruckTripPlanner.Trips(t, truckId);

        if (trips.Count == 0)
        {
            // 影子条不参与按去向的过滤：编组都没解出来，谈不上"这趟去哪"，漏画反而更误导
            row.Bars.Add(new GanttBar(t.StartHour, t.EndHour, ProcessType.Haul, "运输")
            {
                Task = t, TruckId = truckId, IsShadow = true, TintHex = tint,
                Tooltip = $"{truckId} · {ShortZone(t.WorkZone)} 运输\n{Hm(t.StartHour)}–{Hm(t.EndHour)}\n"
                        + "（编组未解出：车次展开回落为整段影子条）",
            });
            return;
        }

        var m = TruckTripPlanner.MatchOf(t);
        bool multi = m is { IsMultiLeg: true };

        // 按去向视图：只留投向本组那个汇的趟。一趟都不匹配时不做过滤——
        // 宁可多画，也不能让某台车在图上凭空消失（键对不上多半是台账没接通）。
        var shown = trips;
        if (!string.IsNullOrEmpty(destFilter))
        {
            var hit = trips.Where(tp => string.Equals(TripKeyOf(tp), destFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count > 0) shown = hit;
        }

        foreach (var tp in shown)
        {
            string dest = tp.SinkText;
            string matName = tp.MaterialName;
            // 车次条很窄，多腿时只在条上戴一个物料首字（煤/硬/表/夹/风/低），全称留给悬停
            string label = multi && matName.Length > 0 ? $"#{tp.Index}{matName[..1]}" : $"#{tp.Index}";

            row.Bars.Add(new GanttBar(tp.StartHour, tp.EndHour, ProcessType.Haul, label)
            {
                Task = t, TruckId = truckId, TripIndex = tp.Index, TripCount = trips.Count,
                TintHex = multi ? TintOfKey(TripKeyOf(tp), tints) : tint,
                TripMaterial = matName, TripSink = dest,
                TripHaulKm = tp.HaulKm, TripCycleMin = tp.CycleMin,
                Tooltip = $"第 {tp.Index} 趟 / 共 {trips.Count} 趟 · {truckId}"
                        + $"\n拉 {matName}"
                        + (string.IsNullOrWhiteSpace(dest) ? " · 去向未定" : $" → {dest} · 运距 {tp.HaulKm:0.##} km")
                        + $"\n{Hm(tp.StartHour)}–{Hm(tp.EndHour)}"
                        + (tp.Partial ? "（班末截断）" : "")
                        + (tp.CycleMin > 0 ? $"\n本趟 T_c {tp.CycleMin:0.#}min" : "")
                        + (m != null
                            ? (multi
                                ? $"（加权 T_c {m.CycleTimeMin:0.#}min · τ_L {m.LoadTaktMin:0.#}min · MF {m.MatchFactor:0.00}）"
                                : $" · 节拍 τ_L {m.LoadTaktMin:0.#}min · MF {m.MatchFactor:0.00}")
                            : ""),
            });
        }
    }

    private static string MainTooltip(ProductionTask t)
    {
        string s = $"{t.Id} · {t.Process.Label()}\n{t.LocationCaption}\n{Hm(t.StartHour)}–{Hm(t.EndHour)}";
        if (t.TargetVolumeM3 > 0) s += $"\n计划 {t.TargetVolumeM3:0} m³实方 · {t.TargetTonnageT:0} t";

        // 混采任务先把完整分项摆出来（一条任务多个去向），再给运输功——按各物料各自运距算
        string split = SplitCaption(t);
        if (split.Length > 0)
        {
            s += $"\n排到（分项）{split}";
            s += $"\n加权运距 {WeightedHaulKm(t):0.##} km · 运输功 {t.TransportWorkBySplitTKm / 1e4:0.##} 万t·km";
            var miss = UnroutedMaterials(t);
            if (miss.Count > 0) s += $"\n⚠ {string.Join("、", miss)} 未指定卸点";

            // 两条线的循环时间差多少，直接写出来——配车数就是按加权 T_c 解的
            if (t.Process == ProcessType.Load)
            {
                var fm = TruckTripPlanner.MatchOf(t);
                if (fm is { IsMultiLeg: true })
                    s += "\n" + string.Join(" ｜ ", fm.Legs.Select(l =>
                             $"{MaterialCatalog.Resolve(l.MaterialCode).Name} 车次{l.Fraction * 100:0.#}% T_c {l.CycleMin:0.#}min"))
                       + $" ⇒ 加权 T_c {fm.CycleTimeMin:0.#}min · 荐 {fm.OptimalTrucks} 车";
            }
        }
        else if (t.HasDestination) s += $"\n排到 {t.DestinationCaption} · 运输功 {t.TransportWorkTKm / 1e4:0.##} 万t·km";
        else if (NeedsDestination(t)) s += "\n⚠ 未指定卸点";
        return s;
    }

    private static DailyGanttModel New(string dateLabel, double nowHour, double blastStart, double blastEnd)
        => new()
        {
            DateLabel = dateLabel, NowHour = nowHour, BlastStart = blastStart, BlastEnd = blastEnd,
            // 班制随模型走，渲染器不再自己写死三班（见 Shifts 的注释）
            Shifts = SafeShifts(),
        };

    /// <summary>取当日班制；盘子不可用时返回空表，渲染器据此退回三等分兜底。</summary>
    private static List<Engine.ShiftWindow> SafeShifts()
    {
        try { return Engine.ShiftScope.Windows.ToList(); }
        catch { return new List<Engine.ShiftWindow>(); }
    }

    private static string TypeColor(string category) => category switch
    {
        "电铲" => "#FF185FA5",
        "卡车" => "#FF0F6E56",
        "钻机" => "#FF534AB7",
        "推土机" => "#FF854F0B",
        _ => "#FF888888",
    };

    private static string ZoneColor(ProcessType p) => p switch
    {
        ProcessType.Load => "#FF185FA5",
        ProcessType.Drill => "#FF534AB7",
        ProcessType.Dump => "#FF854F0B",
        ProcessType.Blast => "#FFA32D2D",
        _ => "#FF888888",
    };

    private static void FillTotals(DailyGanttModel m, IEnumerable<ProductionTask> tasks)
    {
        double plan = 0, actual = 0, planToNow = 0, work = 0, ton = 0;
        foreach (var t in tasks)
        {
            if (t.Process != ProcessType.Load && t.Process != ProcessType.Dump) continue;
            plan += t.TargetVolumeM3;
            actual += t.ActualVolumeM3;
            double dur = Math.Max(1e-6, t.EndHour - t.StartHour);
            double frac = Math.Clamp((Math.Min(t.EndHour, m.NowHour) - t.StartHour) / dur, 0, 1);
            planToNow += t.TargetVolumeM3 * frac;

            // 运输功只算真外运的任务（有去向且有运距）；场内排土不产生运输功。
            // 逐分项累加：混采任务煤走 2.6km、岩走 1.4km 是两条腿，
            // 用主去向的运距乘全任务吨量会把运输功算成一个不存在的数。
            if (NeedsDestination(t))
                foreach (var fl in t.ToFlows("", t.TargetVolumeM3))
                {
                    if (fl.EffectiveHaulKm <= 1e-6) continue;
                    work += fl.TransportWorkTKm;
                    ton += fl.TonnageT;
                }
        }
        m.DayPlanWanM3 = Math.Round(plan / 1e4, 2);
        m.DayActualWanM3 = Math.Round(actual / 1e4, 2);
        m.PlanToNowWanM3 = Math.Round(planToNow / 1e4, 2);
        m.AttainmentPct = planToNow > 1e-6 ? Math.Round(actual / planToNow * 100, 0) : 0;
        m.TransportWorkWanTKm = Math.Round(work / 1e4, 2);
        m.WeightedAvgHaulKm = ton > 1e-6 ? Math.Round(work / ton, 2) : 0;
    }

    private static string BarLabel(ProductionTask t) => t.Process switch
    {
        ProcessType.Load => "采装·" + ShortZone(t.WorkZone),
        ProcessType.Dump => "排土·" + ShortZone(t.WorkZone),
        ProcessType.Drill => "穿孔·" + t.Material,
        ProcessType.Haul => "运输",
        _ => t.Material,   // Idle：检修 / 空闲
    };

    private static string ShortZone(string z) => (z ?? "").Replace("面·", "").Replace("场", "");

    private static string Hm(double h)
    {
        int hh = (int)h; int mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh:00}:{mm:00}";
    }

    /// <summary>骨架样例：取共用任务台账投影（甘特 / 报表 / 任务书 同源）。</summary>
    public static DailyGanttModel Sample()
        => FromTasks(SampleTaskBoard.Day(), SampleTaskBoard.Roster(),
            SampleTaskBoard.DateLabel, SampleTaskBoard.NowHour, SampleTaskBoard.BlastStart, SampleTaskBoard.BlastEnd);
}
