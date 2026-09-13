// 忠实移植自原 PitMine3D Modules/TaskLib/Gantt/TruckTripPlanner.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Gantt;

// ─────────────────────────────────────────────────────────────────────────────
//  车次展开器 —— 把甘特里的卡车「影子条」升级成真运输条。
//
//  影子条的问题：直接把采装任务的起止时间复制到卡车行上，标签固定"运输"。
//  它画出来的是"这台车这段时间归这个铲用"，而不是"这台车这段时间跑了几趟"——
//  调度真正要看的是后者：车次数决定了运量能不能兑现，也决定了卸点会不会拥堵。
//
//  ── 展开公式（符号沿用 FleetMatcher / 理论报告 §3）──
//    T_c  循环时间 min = t_装 + 重车行驶 + t_卸 + 空车行驶 + t_调
//    τ_L  装车节拍 min/车 = m·t_s（m = 每车斗数，由物料松方密度反推）
//    第 i 台车（i 从 1 起）第 k 趟的发车时刻：
//        s(i,k) = 起始 + (i−1)·τ_L + Σ_{j<k} T_c(第 j 趟那条线)
//        N_i    = 跑到时窗末为止
//    (i−1)·τ_L 这个错峰项不能省：n 台车同时压到铲下就得排队，
//    现场是按装车节拍依次进场的，错峰后画出来才和 MF=n·τ_L/T_c 的物理含义自洽。
//
//  ── 混采面：一趟一条线 ────────────────────────────────────────────────────
//    一台铲同一时窗挖「煤7∶岩3」，煤去破碎站、岩去内排场——车队跑的是两条运距不同的线。
//    展开时按【车次份额】把 N 趟分摊到各条线上（见 AllocateTripLegs），
//    每趟的条长用**它自己那条线**的 T_c(m)，不是加权值：这样甘特上两条线的节拍差一眼可见。
//    错峰项仍用加权 τ_L（铲下依次进场是混着来的，与哪条线无关）。
//
//  ── 回落（本类的第一原则：宁可退回影子条，也不许甘特画不出来）──
//    去向解不出 / 运距解不出 / 编组求解抛异常 / T_c 非正 / 车次数离谱，
//    一律返回空车次表，调用方画回原来的整段影子条。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一个车次（一趟：装 → 重车 → 卸 → 空车返）。</summary>
public sealed class TruckTrip
{
    /// <summary>第几趟（1 起）。</summary>
    public int Index { get; set; }
    public double StartHour { get; set; }
    public double EndHour { get; set; }
    /// <summary>被班末截断的尾趟（画到时段边界为止）。</summary>
    public bool Partial { get; set; }

    // ── 本趟走的是哪条线（混采面才有区别；单去向面全部相同）──
    /// <summary>本趟在 <see cref="TruckTripPlanner.LegPlanOf"/> 里的线序号。</summary>
    public int LegIndex { get; set; }
    public string MaterialCode { get; set; } = "";
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    public SinkKind SinkKind { get; set; } = SinkKind.ExternalDump;
    /// <summary>本趟运距 km（等效优先）。</summary>
    public double HaulKm { get; set; }
    /// <summary>本趟自己那条线的循环时间 T_c（min），不是加权值。</summary>
    public double CycleMin { get; set; }

    public string MaterialName => MaterialCatalog.Resolve(MaterialCode).Name;
    public string SinkText => string.IsNullOrWhiteSpace(SinkName) ? SinkId : SinkName;
}

/// <summary>
/// 一条运输线的车次口径 —— 甘特与派车单**共用同一份**，两边不许各算各的。
/// 混采面每种物料一条；单去向面只有一条。
/// </summary>
public sealed class TripLeg
{
    public string MaterialCode { get; set; } = "";
    /// <summary>车次份额（=吨量份额）：本线该占 N 趟里的多少趟。</summary>
    public double TripShare { get; set; }
    /// <summary>实方体积份额（与任务 Splits 对账用）。</summary>
    public double VolumeShare { get; set; }

    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    public SinkKind SinkKind { get; set; } = SinkKind.ExternalDump;

    /// <summary>本线等效运距 km。</summary>
    public double HaulKm { get; set; }
    /// <summary>本线的循环时间 T_c（min）。</summary>
    public double CycleMin { get; set; }
    /// <summary>本线的装车节拍 τ_L（min/车）。</summary>
    public double TaktMin { get; set; }
    /// <summary>本线重车单程行驶时间 min（派车单算卸车时刻用）。</summary>
    public double LoadedMin { get; set; }

    public MaterialSpec Spec => MaterialCatalog.Resolve(MaterialCode);
    public string SinkText => string.IsNullOrWhiteSpace(SinkName) ? SinkId : SinkName;
    public bool HasDestination => !string.IsNullOrWhiteSpace(SinkId) || !string.IsNullOrWhiteSpace(SinkName);

    public string Caption => $"{Spec.Name} {TripShare * 100:0.#}%车次 → {(SinkText.Length > 0 ? SinkText : "（未定）")}";
}

/// <summary>
/// 车次展开器。对同一条任务的编组求解结果做缓存——FleetMatcher 每次调用都要读
/// dispatch_rule 台账，一天几十条任务 × 三个视图来回切，不缓存会把窗口卡住。
/// </summary>
internal static class TruckTripPlanner
{
    /// <summary>单车单任务车次上限。超过说明 T_c 异常小（台账脏数据），画出来只是噪声 → 回落影子条。</summary>
    private const int MaxTripsPerTruck = 40;

    // 键 = 任务的"编组求解相关字段指纹"，不是 Id：滚动重排后同 Id 任务的时段/去向会变。
    private static readonly Dictionary<string, FleetMatchResult?> _matchCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HaulLeg?> _legCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReadOnlyList<(MaterialDestination Dest, HaulLeg Leg)>> _legsCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReadOnlyList<TripLeg>> _planCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SinkNode?> _sinkCache = new(StringComparer.Ordinal);

    /// <summary>换了一天 / 载入新快照后丢缓存。</summary>
    public static void Invalidate()
    {
        _matchCache.Clear();
        _legCache.Clear();
        _legsCache.Clear();
        _planCache.Clear();
        _sinkCache.Clear();
    }

    /// <summary>
    /// 缓存键 = 决定 T_c / τ_L 的那几件事：物料、去向、运距、编组。
    /// <para>
    /// 混采任务必须把**各分项的「物料|去向|运距」有序拼进键**：只写主去向的话，
    /// 「煤→破碎站 2.6km ＋ 岩→内排场 1.26km」与「煤→破碎站 2.6km ＋ 岩→外排场 3.2km」
    /// 会撞成同一个键，第二条任务直接拿第一条的加权 T_c 画图，两条线的差别在缓存这一层就被抹掉了。
    /// </para>
    /// 刻意**不含**时段与工作量——同一个面裂到三个班的三条任务，循环时间与装车节拍是同一个，
    /// 把班次编进键会让 FleetMatcher（每次都要读 dispatch_rule 台账）多跑两倍。
    /// </summary>
    private static string KeyOf(ProductionTask t)
        => $"{t.Process}|{t.WorkZone}|{t.EngineeringPositionId}|{t.ResolvedMix.Caption}"
         + $"|{t.DestinationId}|{t.DestinationName}|{t.HaulDistanceKm:0.###}|{t.EquivHaulKm:0.###}"
         + $"|{t.Group.MainEquipment}|{string.Join(",", t.Group.Trucks)}"
         + "|" + SplitFingerprint(t);

    /// <summary>分项指纹：按物料码排序后拼「物料&gt;去向|实距|等效|份额」，保证同一组分项永远同键。</summary>
    private static string SplitFingerprint(ProductionTask t)
        => t.Splits.Count == 0
            ? "-"
            : string.Join(";", t.Splits
                .OrderBy(s => s.MaterialCode, StringComparer.OrdinalIgnoreCase)
                .Select(s => $"{s.MaterialCode}>{s.DestinationId}|{s.DestinationName}"
                           + $"|{s.HaulKm:0.###}|{s.EquivHaulKm:0.###}|{s.Fraction:0.####}"));

    // ── 去向 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 任务的去向节点：先按 DestinationId 查登记簿，再按 DestinationName 反查；
    /// 排土任务本身没有"去向"（它就在排土场上作业），故按 WorkZone 名字反查一次——
    /// 这样"采装→内排场"与"内排场上的排土作业"能落到同一个汇，采排配对才在图上看得见。
    /// 登记簿读不到（DB 未接通）时返回 null，调用方按名字兜底。
    /// <para>
    /// 注意这里给的是**主去向**。混采任务的完整分项去向见 <see cref="LegPlanOf"/>。
    /// </para>
    /// </summary>
    public static SinkNode? SinkOf(ProductionTask t)
    {
        string key = $"{t.DestinationId}|{t.DestinationName}|{t.Process}|{t.WorkZone}";
        if (_sinkCache.TryGetValue(key, out var cached)) return cached;

        SinkNode? sink = null;
        try
        {
            var reg = SinkRegistryLoader.Current;
            sink = reg.Find(t.DestinationId);
            if (sink == null && !string.IsNullOrWhiteSpace(t.DestinationName))
                sink = reg.All.FirstOrDefault(s => string.Equals(s.Name, t.DestinationName, StringComparison.OrdinalIgnoreCase));
            if (sink == null && t.Process == ProcessType.Dump && !string.IsNullOrWhiteSpace(t.WorkZone))
                sink = reg.All.FirstOrDefault(s => string.Equals(s.Name, t.WorkZone, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            sink = null;   // 去向台账不可用 → 交回调用方按名字兜底
        }

        _sinkCache[key] = sink;
        return sink;
    }

    // ── 运距 / 编组 ──────────────────────────────────────────────────────────

    /// <summary>这条任务【主去向】那条腿的运距解（路网 → 手填 → 兜底三层，由 HaulResolver 负责）。</summary>
    public static HaulLeg? LegOf(ProductionTask t)
    {
        string key = KeyOf(t);
        if (_legCache.TryGetValue(key, out var cached)) return cached;

        HaulLeg? leg = null;
        try
        {
            leg = HaulResolver.Resolve(FaceOf(t), SinkOf(t));
            if (leg is { Feasible: false }) leg = null;
        }
        catch { leg = null; }

        _legCache[key] = leg;
        return leg;
    }

    /// <summary>
    /// 这条任务【逐物料分项】的运距解：混采面 N 种物料 ⇒ N 条腿，各挂各自的汇与运距。
    /// 解不出返回空表。
    /// </summary>
    public static IReadOnlyList<(MaterialDestination Dest, HaulLeg Leg)> LegsOf(ProductionTask t)
    {
        string key = KeyOf(t);
        if (_legsCache.TryGetValue(key, out var cached)) return cached;

        IReadOnlyList<(MaterialDestination, HaulLeg)> legs = Array.Empty<(MaterialDestination, HaulLeg)>();
        try
        {
            SinkRegistry? reg = null;
            try { reg = SinkRegistryLoader.Current; } catch { reg = null; }
            legs = HaulResolver.ResolveAll(FaceOf(t), reg);
        }
        catch { legs = Array.Empty<(MaterialDestination, HaulLeg)>(); }

        _legsCache[key] = legs;
        return legs;
    }

    /// <summary>
    /// 这条任务的编组求解结果（拿 T_c / τ_L / MF）。解不出返回 null。
    /// 有分项的任务走【多腿加权】口径，单去向任务口径完全不变。
    /// </summary>
    public static FleetMatchResult? MatchOf(ProductionTask t)
    {
        string key = KeyOf(t);
        if (_matchCache.TryGetValue(key, out var cached)) return cached;

        FleetMatchResult? r = null;
        try
        {
            var legs = t.HasSplits ? LegsOf(t) : Array.Empty<(MaterialDestination Dest, HaulLeg Leg)>();
            if (legs.Count > 0 && legs.Any(x => x.Leg is { Feasible: true }))
            {
                r = FleetMatcher.Match(FaceOf(t), legs);
            }
            else
            {
                var leg = LegOf(t);
                if (leg != null) r = FleetMatcher.Match(FaceOf(t), leg);
            }
            if (r is { CycleTimeMin: <= 0 }) r = null;
        }
        catch { r = null; }

        _matchCache[key] = r;
        return r;
    }

    /// <summary>
    /// 这条任务的【运输线台账】：每条线的去向、运距、T_c、τ_L 与车次份额。
    /// 甘特画车次条、派车引擎签车次指令都从这里取，两边永不分家。解不出返回空表。
    /// </summary>
    public static IReadOnlyList<TripLeg> LegPlanOf(ProductionTask t)
    {
        string key = KeyOf(t);
        if (_planCache.TryGetValue(key, out var cached)) return cached;

        var plan = new List<TripLeg>();
        try
        {
            var m = MatchOf(t);
            if (m != null && m.Legs.Count > 0)
            {
                var legs = LegsOf(t);
                var mix = t.ResolvedMix.Normalized();
                var mainLeg = LegOf(t);

                foreach (var (code, share, km, cyc, takt) in m.Legs)
                {
                    string mat = string.IsNullOrWhiteSpace(code) ? mix.PrimaryCode : code;

                    // 该物料那条腿的去向与重车行驶时间；对不上（单去向口径）就回落主去向
                    MaterialDestination? d = null;
                    HaulLeg? hl = null;
                    for (int j = 0; j < legs.Count; j++)
                    {
                        if (!string.Equals(legs[j].Dest.MaterialCode, mat, StringComparison.OrdinalIgnoreCase)) continue;
                        d = legs[j].Dest;
                        hl = legs[j].Leg;
                        break;
                    }
                    d ??= t.DestinationFor(mat);
                    hl ??= mainLeg;

                    plan.Add(new TripLeg
                    {
                        MaterialCode = mat,
                        TripShare = share,
                        VolumeShare = mix.FractionOf(mat),
                        SinkId = d.DestinationId,
                        SinkName = d.DestinationName,
                        SinkKind = d.DestinationKind,
                        HaulKm = km > 1e-6 ? km : d.EffectiveHaulKm,
                        CycleMin = cyc,
                        TaktMin = takt,
                        LoadedMin = hl is { LoadedMin: > 0 } ? hl.LoadedMin : Math.Max(0, (cyc - takt) / 2),
                    });
                }
            }
        }
        catch { plan.Clear(); }

        _planCache[key] = plan;
        return plan;
    }

    // ── ★ 车次分摊（甘特与派车单**必须**调这一个函数）───────────────────────

    /// <summary>
    /// 把 <paramref name="tripCount"/> 趟按各线的【车次份额】交错分摊到各条线上。
    ///
    /// <para>
    /// 判据是「顺序最大亏空法」：第 k 趟（k 从 0 起）派给亏得最多的那条线，
    /// 亏空 = w_j·(k+1) − 已派给 j 的趟数。份额 6∶4 时排出来是 A B A B A A B A B A ——
    /// 而不是"前 6 趟全去 A、后 4 趟全去 B"：现场是混着跑的，后者画出来就是假的。
    /// </para>
    /// <para>
    /// ★ 该序列具有<b>前缀稳定性</b>：算 10 趟得到的前 7 项，与直接算 7 趟完全一致。
    /// 甘特（画图口径，剩得下半趟就画）与派车单（单据口径，能把料卸掉才算一趟）车次数不同，
    /// 靠这条性质才能保证"同一台车的第 3 趟"在两边指向同一条线。
    /// </para>
    /// </summary>
    /// <returns>长度 = tripCount 的线序号数组（对应 <see cref="LegPlanOf"/> 的下标）。</returns>
    public static int[] AllocateTripLegs(IReadOnlyList<TripLeg> legs, int tripCount)
    {
        int n = Math.Max(0, tripCount);
        var res = new int[n];
        if (legs == null || legs.Count <= 1 || n == 0) return res;   // 单线：全 0

        // 份额归一；全空（份额没解出来）时按等分，至少别把所有趟都堆到第一条线上
        var w = new double[legs.Count];
        double sum = legs.Sum(l => Math.Max(0, l.TripShare));
        for (int j = 0; j < legs.Count; j++)
            w[j] = sum > 1e-9 ? Math.Max(0, legs[j].TripShare) / sum : 1.0 / legs.Count;

        var got = new double[legs.Count];
        for (int k = 0; k < n; k++)
        {
            int best = 0;
            double bestDeficit = double.NegativeInfinity;
            for (int j = 0; j < w.Length; j++)
            {
                double deficit = w[j] * (k + 1) - got[j];
                if (deficit > bestDeficit + 1e-12) { bestDeficit = deficit; best = j; }
            }
            res[k] = best;
            got[best] += 1;
        }
        return res;
    }

    /// <summary>任务 → 编组求解的输入面（编组只读不写，仍拷一份编组，杜绝求解器改到台账上）。</summary>
    private static FaceInput FaceOf(ProductionTask t) => new()
    {
        Zone = t.WorkZone,
        BenchElevationM = t.BenchElevationM,
        EngineeringPositionId = t.EngineeringPositionId,
        Material = t.Material,
        MaterialCode = t.MaterialCode,
        Mix = t.Mix,
        DestinationId = t.DestinationId,
        DestinationName = t.DestinationName,
        DestinationKind = t.DestinationKind,
        HaulDistanceKm = t.HaulDistanceKm,
        EquivHaulKm = t.EquivHaulKm,

        // 分项去向必须带上（且深拷贝）：多腿求解全靠它，漏了就退化成单去向口径
        Splits = t.Splits.Select(s => s.Clone()).ToList(),

        DayTargetM3 = t.TargetVolumeM3,
        Quality = t.QualityTarget,
        Process = t.Process,
        Group = new EquipmentGroup
        {
            MainEquipment = t.Group.MainEquipment,
            Trucks = new List<string>(t.Group.Trucks),
            Aux = new List<string>(t.Group.Aux),
            RecommendedTrucks = t.Group.RecommendedTrucks,
            GroupCapacityM3PerH = t.Group.GroupCapacityM3PerH,
        },
    };

    // ── 车次展开 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 [StartHour,EndHour) 展成第 <paramref name="truckId"/> 台车的车次表。
    /// 混采任务按份额把各趟分摊到各条线，每趟的长度取它自己那条线的 T_c。
    /// 解不出编组（或车次数离谱）返回空表 ⇒ 调用方回落影子条。
    /// </summary>
    public static IReadOnlyList<TruckTrip> Trips(ProductionTask t, string truckId)
    {
        var none = Array.Empty<TruckTrip>();

        double dur = t.EndHour - t.StartHour;
        if (dur <= 1e-6) return none;

        var m = MatchOf(t);
        if (m == null) return none;

        double tc = m.CycleTimeMin / 60.0;                    // T_c(加权) → h（只作合理性校验）
        double takt = Math.Max(0, m.LoadTaktMin) / 60.0;      // τ_L(加权) → h（错峰进场用）
        if (tc <= 1e-4 || double.IsNaN(tc) || double.IsInfinity(tc)) return none;

        var plan = LegPlanOf(t);
        if (plan.Count == 0) return none;

        int i = t.Group.Trucks.IndexOf(truckId);              // 第 i 台车（0 起）
        if (i < 0) i = 0;

        // 多分配两格：下面的车次数护栏在 Add 之后才生效，索引会比上限多走一步
        var alloc = AllocateTripLegs(plan, MaxTripsPerTruck + 2);

        var list = new List<TruckTrip>();
        double s = t.StartHour + i * takt;                    // 错峰进场
        for (int k = 1; k <= alloc.Length; k++)
        {
            var leg = plan[alloc[k - 1]];
            double tck = Math.Max(1e-4, leg.CycleMin) / 60.0; // 本趟走的那条线的 T_c
            if (double.IsNaN(tck) || double.IsInfinity(tck)) return none;

            if (s + tck * 0.5 > t.EndHour) break;             // 剩不下半趟 → 本班不再发车
            double e = Math.Min(t.EndHour, s + tck);
            list.Add(new TruckTrip
            {
                Index = k, StartHour = s, EndHour = e, Partial = e < s + tck - 1e-6,
                LegIndex = alloc[k - 1],
                MaterialCode = leg.MaterialCode,
                SinkId = leg.SinkId, SinkName = leg.SinkName, SinkKind = leg.SinkKind,
                HaulKm = leg.HaulKm, CycleMin = leg.CycleMin,
            });
            s += tck;
            if (list.Count > MaxTripsPerTruck) return none;   // T_c 异常小 → 回落影子条
        }
        return list.Count == 0 ? none : list;
    }
}
