// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/TripAnimator.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Road;          // Point3d：给 SimRoadGraph 传查询坐标（路网求解本体已搬走）
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Simulation;
using HaulLeg = PitMine3D.Kylin.TaskLib.Engine.HaulLeg;   // Kylin 的 Cad.Road 把原 RoadLib.Network/Routing 合成一个命名空间，HaulSolveKernel.HaulLeg 会串进来

// ─────────────────────────────────────────────────────────────────────────────
//  L4 车次动画 —— 用「铲—车排队」把编组是否合理演出来。
//
//  为什么车次层值得单独做：匹配系数 MF = n·τ_L / T_c 是个抽象数，
//  但「铲空转等车」和「五台车在铲下排队」是看得见的。把 T_c 节拍展开成事件序列，
//  MF<1 / MF≈1 / MF>1 三种编组的差别一眼可辨——这是编组评审最强的证据。
//
//  ── 时间参数的取法（不自造常量）──
//  循环时间 T_c、装车节拍 τ_L 来自 FleetMatcher（理论报告 §3.1/§3.3），
//  重车/空车行车时间来自 HaulResolver 的 HaulLeg。剩下的那段
//      T_turn = T_c − τ_L − t_重 − t_空
//  就是「调车 + 卸载」，直接当作卸车段时长用 —— 这样四段之和恒等于 T_c，
//  既不重复计时，也不需要在本文件里再写一套调车/卸载常量。
//
//  ── 混采面：一趟一条线（多腿口径）──
//  一台铲同一时窗挖「煤7∶岩3」，煤去破碎站、岩去内排场 —— 车队跑的是两条运距不同的线。
//  本动画走**多腿**：
//    · 运距   HaulResolver.ResolveAll(face, sinks) → 每种物料一条 HaulLeg；
//    · 编组   FleetMatcher.Match(face, legs)       → FleetMatchResult.Legs 给出各线自己的
//             T_c(m) / τ_L(m) / 运距，MatchFactor 等总量仍是车次份额加权值；
//    · 归线   TruckTripPlanner.LegPlanOf(task) 给**规范线表**（下标即线序号），
//             再由 TruckTripPlanner.AllocateTripLegs(plan, N) 决定第 k 趟走哪条线。
//  ★ 分摊必须复用 AllocateTripLegs：甘特、派车单、本动画三处共用同一个序列，
//    否则「同一台车的第 3 趟」在三个界面上会指向不同去向 —— 那是最难查的一类不一致。
//  每趟的装车/行车/卸车段一律取**它自己那条线**的 T_c(m)、τ_L(m)、t_重(m)、t_空(m)，
//  不用加权值：两条线的节拍差正是混采面要看的东西。
//  注意 FleetMatchResult.Legs 的 Fraction 是【车次份额（吨量口径）】，不是实方份额；
//  实方份额在任务的 Splits / ResolvedMix 上（本文件里叫 VolumeShare），两者不可混用。
//
//  ── 排队模型 ──
//  电铲 = 单服务台（一次只能装一台车）；卸点 = **每条线各一个**单服务台
//  （两个去向本就是两个卸点，共用一个服务台会凭空造出排队）。
//  按「谁先回到铲下谁先装」（FIFO）推进事件：
//      装车开始 = max(回到铲下时刻, 铲空闲时刻)   → 差额 = 车在铲下排队
//      铲空闲   = 上一台车装完                     → 空档 = 铲等车
//  MF<1 时铲空档累积、MF>1 时车队排队累积，两个累计量直接显示在界面上。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一台车在某一时刻处于循环的哪一段。</summary>
public enum TripPhase
{
    /// <summary>在铲下排队等装（MF&gt;1 的典型现象）。</summary>
    QueueLoad,
    Loading,
    /// <summary>重车在途。</summary>
    Hauling,
    /// <summary>在卸点排队。</summary>
    QueueDump,
    /// <summary>调车 + 卸载。</summary>
    Dumping,
    /// <summary>空车返程。</summary>
    Returning,
    /// <summary>本时段已无任务（时段结束/未开始）。</summary>
    Off,
}

public static class TripPhaseLabels
{
    public static string Label(this TripPhase p) => p switch
    {
        TripPhase.QueueLoad => "铲下排队",
        TripPhase.Loading => "装车",
        TripPhase.Hauling => "重车在途",
        TripPhase.QueueDump => "卸点排队",
        TripPhase.Dumping => "卸车",
        TripPhase.Returning => "空车返程",
        _ => "未上线",
    };
}

/// <summary>
/// 一条运输线在动画侧的口径（下标与 <see cref="TaskLib.Gantt.TruckTripPlanner.LegPlanOf"/>
/// 的线表**严格对齐** —— 甘特/派车单/动画靠这个下标说同一件事）。
/// <para>
/// 与 <see cref="TaskLib.Gantt.TripLeg"/> 的差别：那边只给到 <c>LoadedMin</c>（派车单算卸车时刻够用），
/// 动画还要把循环拆成四段，所以这里补上 <see cref="EmptyMin"/> 与 <see cref="TurnaroundMin"/>，
/// 数据源仍是 <see cref="HaulResolver.ResolveAll"/> 给的同一条 HaulLeg。
/// </para>
/// </summary>
public sealed class TripLine
{
    /// <summary>线序号（= LegPlanOf 的下标 = TruckTrip.LegIndex）。</summary>
    public int Index { get; set; }
    public string MaterialCode { get; set; } = "";
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    public SinkKind SinkKind { get; set; } = SinkKind.ExternalDump;

    /// <summary>【车次份额】（吨量口径）：本线该占 N 趟里的多少趟。</summary>
    public double TripShare { get; set; }
    /// <summary>【实方份额】（体积口径，来自 ResolvedMix / Splits）。与 <see cref="TripShare"/> 不是一回事。</summary>
    public double VolumeShare { get; set; }

    /// <summary>本线等效运距 km。</summary>
    public double HaulKm { get; set; }
    /// <summary>本线循环时间 T_c(m) min（不是加权值）。</summary>
    public double CycleMin { get; set; }
    /// <summary>本线装车节拍 τ_L(m) min/车。</summary>
    public double TaktMin { get; set; }
    /// <summary>本线重车单程 min。</summary>
    public double LoadedMin { get; set; }
    /// <summary>本线空车返程 min。</summary>
    public double EmptyMin { get; set; }
    /// <summary>本线调车+卸载 min = T_c − τ_L − t_重 − t_空。</summary>
    public double TurnaroundMin { get; set; }

    /// <summary>行车段是按「T_c − τ_L」对半拆的（运距层没给重/空车时间）。</summary>
    public bool TravelSplitSimplified { get; set; }

    public MaterialSpec Spec => MaterialCatalog.Resolve(MaterialCode);
    public string MaterialName => Spec.Name;
    public string SinkText => string.IsNullOrWhiteSpace(SinkName) ? SinkId : SinkName;

    public string Caption =>
        $"{MaterialName} → {(SinkText.Length > 0 ? SinkText : "（去向未定）")}　"
      + $"车次 {TripShare * 100:0.#}% · 实方 {VolumeShare * 100:0.#}% · {HaulKm:0.00}km · T_c {CycleMin:0.#}min";
}

/// <summary>一趟车次的完整事件时刻（分钟，自时段起点算）。</summary>
public sealed class TripLeg
{
    public int TruckIndex { get; set; }
    public string TruckId { get; set; } = "";
    /// <summary>本车第几趟（1 起）。</summary>
    public int TripIndex { get; set; }
    /// <summary>
    /// 本趟走哪条线（<see cref="TripScene.Lines"/> 的下标）。
    /// 由 <see cref="TaskLib.Gantt.TruckTripPlanner.AllocateTripLegs"/> 分摊得出，
    /// 与甘特、派车单上「同一台车的第 N 趟」指向同一条线。
    /// </summary>
    public int LineIndex { get; set; }

    public double ArriveShovelMin { get; set; }
    public double LoadStartMin { get; set; }
    public double LoadEndMin { get; set; }
    public double ArriveSinkMin { get; set; }
    public double DumpStartMin { get; set; }
    public double DumpEndMin { get; set; }
    public double BackAtMin { get; set; }

    /// <summary>本趟在铲下排队时长（MF&gt;1 的直接证据）。</summary>
    public double QueueLoadMin => Math.Max(0, LoadStartMin - ArriveShovelMin);
    /// <summary>本趟在卸点排队时长。</summary>
    public double QueueDumpMin => Math.Max(0, DumpStartMin - ArriveSinkMin);
    /// <summary>本趟实际循环时长。</summary>
    public double CycleMin => Math.Max(0, BackAtMin - ArriveShovelMin);
    /// <summary>本趟是否在时段内跑完。</summary>
    public bool Completed { get; set; }
}

/// <summary>某一时刻一台车的状态。</summary>
public sealed class TripTruckState
{
    public string TruckId { get; set; } = "";
    public TripPhase Phase { get; set; } = TripPhase.Off;
    /// <summary>沿路线的位置 0=铲位，1=卸点。</summary>
    public double RouteS { get; set; }
    public SimPoint Position { get; set; }
    public int TripIndex { get; set; }
    /// <summary>本趟走的是哪条线（<see cref="TripScene.Lines"/> 下标）。</summary>
    public int LineIndex { get; set; }
    /// <summary>本趟去哪（混采面两趟之间会变）。</summary>
    public string SinkText { get; set; } = "";
    public string MaterialCode { get; set; } = "";
    /// <summary>本趟已排队时长（在排队相里才 &gt;0）。</summary>
    public double WaitedMin { get; set; }
    public bool IsQueueing => Phase is TripPhase.QueueLoad or TripPhase.QueueDump;
}

/// <summary>某一时刻的全场快照。</summary>
public sealed class TripSnapshot
{
    public double Minute { get; set; }
    public List<TripTruckState> Trucks { get; set; } = new();

    public int LoadQueue => Trucks.Count(t => t.Phase == TripPhase.QueueLoad);
    public int DumpQueue => Trucks.Count(t => t.Phase == TripPhase.QueueDump);
    public int InTransit => Trucks.Count(t => t.Phase is TripPhase.Hauling or TripPhase.Returning);
    public int LoadingNow => Trucks.Count(t => t.Phase == TripPhase.Loading);
    public int DumpingNow => Trucks.Count(t => t.Phase == TripPhase.Dumping);

    /// <summary>某条线的卸点此刻排了几台（混采面各卸点分开看，合起来看会掩盖真正堵的那个点）。</summary>
    public int DumpQueueOn(int lineIndex) => Trucks.Count(t => t.Phase == TripPhase.QueueDump && t.LineIndex == lineIndex);

    /// <summary>电铲是否正在装车（否则就是在等车）。</summary>
    public bool ShovelBusy { get; set; }
    /// <summary>至此刻电铲累计空等时长 min（MF&lt;1 的直接证据）。</summary>
    public double ShovelIdleCumMin { get; set; }
    /// <summary>至此刻卡车累计排队时长 min（MF&gt;1 的直接证据）。</summary>
    public double TruckQueueCumMin { get; set; }
    /// <summary>至此刻完成的车次数。</summary>
    public int CompletedTrips { get; set; }
    /// <summary>至此刻已运实方 m³（按车次均摊）。</summary>
    public double HauledInSituM3 { get; set; }

    public string Verdict { get; set; } = "";
}

/// <summary>
/// 一个采装任务的车次动画场景。<see cref="Available"/>=false 时界面只显示
/// <see cref="Status"/>，不画动画、不显示任何推算数。
/// </summary>
public sealed class TripScene
{
    // ── 身份 ──
    public string TaskId { get; set; } = "";
    public string FaceName { get; set; } = "";
    public string SinkName { get; set; } = "";
    public string MaterialCaption { get; set; } = "";
    public string ShovelId { get; set; } = "";
    public List<string> TruckIds { get; set; } = new();

    // ── 时段 ──
    public double StartHour { get; set; }
    public double EndHour { get; set; }
    public double HorizonMin => Math.Max(0, (EndHour - StartHour) * 60);

    // ── 编组解（这几个是**车次份额加权值**，只用于表头与 MF；逐趟一律走各自那条线）──
    public double CycleMin { get; set; }
    public double TaktMin { get; set; }
    public double LoadedMin { get; set; }
    public double EmptyMin { get; set; }
    /// <summary>调车 + 卸载 = T_c − τ_L − t_重 − t_空（加权）。</summary>
    public double TurnaroundMin { get; set; }

    // ── 多腿（混采面一趟一条线）──
    /// <summary>本面的运输线表。下标与甘特/派车单的线序号一致。单去向面只有一条。</summary>
    public List<TripLine> Lines { get; set; } = new();
    /// <summary>混采面（不止一条线）。</summary>
    public bool IsMultiLeg => Lines.Count > 1;
    /// <summary>各线的路线折线（与 <see cref="Lines"/> 同序）。起点都是铲位。</summary>
    public List<List<SimPoint>> Routes { get; set; } = new();

    public TripLine? LineAt(int i) => i >= 0 && i < Lines.Count ? Lines[i] : Lines.FirstOrDefault();
    public double MatchFactor { get; set; }
    public int OptimalTrucks { get; set; }
    public int TruckCount => TruckIds.Count;
    public string Bottleneck { get; set; } = "";
    public string FleetExplain { get; set; } = "";
    public string HaulCaption { get; set; } = "";

    // ── 工程量（按车次均摊，派生量）──
    public double TaskInSituM3 { get; set; }
    /// <summary>每车次实方 m³ = 任务量 ÷ 本时段车次总数（派生，不是台账载重）。</summary>
    public double PerTripInSituM3 { get; set; }
    public double PerTripLooseM3 { get; set; }
    public double PerTripTonnageT { get; set; }

    // ── 路线 ──
    /// <summary>主线路线（= <see cref="Routes"/>[0]）。旧调用点的兼容入口。</summary>
    public List<SimPoint> Route => Routes.Count > 0 ? Routes[0] : _emptyRoute;
    private static readonly List<SimPoint> _emptyRoute = new();
    /// <summary>路线来自真实路网（false = 源汇两点直线插值的简化）。</summary>
    public bool RouteIsReal { get; set; }
    public string RouteSource { get; set; } = "";

    // ── 事件 ──
    public List<TripLeg> Legs { get; set; } = new();

    public bool Available { get; set; }
    public string Status { get; set; } = "";
    public List<string> Notes { get; set; } = new();

    /// <summary>全时段完成车次数。</summary>
    public int TotalTrips => Legs.Count(l => l.Completed);
    /// <summary>全时段铲空等合计 min。</summary>
    public double ShovelIdleMin { get; set; }
    /// <summary>全时段卡车排队合计 min。</summary>
    public double TruckQueueMin => Legs.Sum(l => l.QueueLoadMin + l.QueueDumpMin);

    // ── 时刻查询 ───────────────────────────────────────────────────────────

    /// <summary>取某一时刻（分钟，自时段起点算）的全场快照。</summary>
    public TripSnapshot At(double minute)
    {
        var snap = new TripSnapshot { Minute = minute };
        if (!Available) { snap.Verdict = Status; return snap; }

        for (int i = 0; i < TruckIds.Count; i++)
        {
            var st = new TripTruckState { TruckId = TruckIds[i], Phase = TripPhase.Off };
            var leg = Legs.FirstOrDefault(l => l.TruckIndex == i && minute >= l.ArriveShovelMin && minute < l.BackAtMin);
            if (leg != null)
            {
                st.TripIndex = leg.TripIndex;
                st.LineIndex = leg.LineIndex;
                var ln = LineAt(leg.LineIndex);
                st.SinkText = ln?.SinkText ?? SinkName;
                st.MaterialCode = ln?.MaterialCode ?? "";
                if (minute < leg.LoadStartMin) { st.Phase = TripPhase.QueueLoad; st.RouteS = 0; st.WaitedMin = minute - leg.ArriveShovelMin; }
                else if (minute < leg.LoadEndMin) { st.Phase = TripPhase.Loading; st.RouteS = 0; }
                else if (minute < leg.ArriveSinkMin)
                {
                    st.Phase = TripPhase.Hauling;
                    st.RouteS = Frac(minute, leg.LoadEndMin, leg.ArriveSinkMin);
                }
                else if (minute < leg.DumpStartMin) { st.Phase = TripPhase.QueueDump; st.RouteS = 1; st.WaitedMin = minute - leg.ArriveSinkMin; }
                else if (minute < leg.DumpEndMin) { st.Phase = TripPhase.Dumping; st.RouteS = 1; }
                else { st.Phase = TripPhase.Returning; st.RouteS = 1 - Frac(minute, leg.DumpEndMin, leg.BackAtMin); }
            }
            st.Position = PointOnLine(st.LineIndex, st.RouteS);
            snap.Trucks.Add(st);
        }

        // 累计量：逐 leg 累加到 minute 为止
        double idle = 0, prevLoadEnd = 0;
        foreach (var l in Legs.OrderBy(l => l.LoadStartMin))
        {
            if (l.LoadStartMin >= minute) break;
            idle += Math.Max(0, l.LoadStartMin - prevLoadEnd);
            prevLoadEnd = l.LoadEndMin;
        }
        // 若此刻铲空着，把「上一车装完到现在」这段也算进空等
        bool busy = Legs.Any(l => minute >= l.LoadStartMin && minute < l.LoadEndMin);
        if (!busy && minute > prevLoadEnd) idle += minute - prevLoadEnd;

        snap.ShovelBusy = busy;
        snap.ShovelIdleCumMin = idle;
        snap.TruckQueueCumMin = Legs.Where(l => l.ArriveShovelMin < minute)
                                    .Sum(l => Math.Min(l.QueueLoadMin, Math.Max(0, minute - l.ArriveShovelMin))
                                            + (l.ArriveSinkMin < minute ? Math.Min(l.QueueDumpMin, Math.Max(0, minute - l.ArriveSinkMin)) : 0));
        snap.CompletedTrips = Legs.Count(l => l.Completed && l.BackAtMin <= minute);
        snap.HauledInSituM3 = snap.CompletedTrips * PerTripInSituM3;
        snap.Verdict = VerdictAt(snap);
        return snap;
    }

    private string VerdictAt(TripSnapshot s)
    {
        string mf = $"MF {MatchFactor:0.00}（配 {TruckCount} 车 · 荐 {OptimalTrucks} 车"
                  + (IsMultiLeg ? $" · {Lines.Count} 条线" : "") + "）";
        if (MatchFactor < 0.9)
            return $"{mf} → 运力不足·铲等车：此刻铲{(s.ShovelBusy ? "在装车" : "空等")}，累计空等 {s.ShovelIdleCumMin:0.#} min。加车或缩短运距可提产。";
        if (MatchFactor > 1.1)
            return $"{mf} → 采装瓶颈·车排队：此刻铲下排队 {s.LoadQueue} 台、卸点排队 {s.DumpQueue} 台，累计排队 {s.TruckQueueCumMin:0.#} min。减车或加铲才划算。";
        return $"{mf} → 配置均衡：铲累计空等 {s.ShovelIdleCumMin:0.#} min、车累计排队 {s.TruckQueueCumMin:0.#} min，两侧都不明显。";
    }

    private static double Frac(double t, double a, double b) => b - a <= 1e-9 ? 1 : Math.Clamp((t - a) / (b - a), 0, 1);

    /// <summary>某条线的路线折线（越界回落主线）。</summary>
    public IReadOnlyList<SimPoint> RouteOf(int lineIndex)
        => lineIndex >= 0 && lineIndex < Routes.Count ? Routes[lineIndex] : Route;

    /// <summary>沿**某条线**的路线按弧长比例取点（s：0=铲位，1=该线卸点）。</summary>
    public SimPoint PointOnLine(int lineIndex, double s) => Along(RouteOf(lineIndex), s);

    /// <summary>沿主线路线按弧长比例取点。</summary>
    public SimPoint PointAt(double s) => Along(Route, s);

    private static SimPoint Along(IReadOnlyList<SimPoint> route, double s)
    {
        if (route.Count == 0) return new SimPoint(0, 0);
        if (route.Count == 1) return route[0];
        double total = 0;
        for (int i = 0; i + 1 < route.Count; i++) total += (route[i + 1] - route[i]).Length;
        if (total <= 1e-9) return route[0];
        double target = Math.Clamp(s, 0, 1) * total, acc = 0;
        for (int i = 0; i + 1 < route.Count; i++)
        {
            double seg = (route[i + 1] - route[i]).Length;
            if (acc + seg >= target || i + 2 == route.Count)
            {
                double k = seg <= 1e-9 ? 0 : (target - acc) / seg;
                return route[i] + (route[i + 1] - route[i]) * Math.Clamp(k, 0, 1);
            }
            acc += seg;
        }
        return route[^1];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  场景构建
// ─────────────────────────────────────────────────────────────────────────────

public static class TripAnimator
{
    /// <summary>单场景车次数上限（防台账脏数据把 T_c 算成 0 而造出百万条腿）。</summary>
    private const int MaxLegs = 4000;

    // ★ 路网不在本文件里管：图 / 解算器 / 吸附半径 / 命中率全在 SimRoadGraph 单例上。
    //   本文件曾自带 _graph/_solver/_graphTried + RealRoute/Polyline/Solver/FindNode 一整套，
    //   外部拿不到 ⇒ 想画卡车的人只能自己 new 第二份图。已整体搬到 SimRoadGraph，这里只当调用方。

    /// <summary>丢弃路网缓存（存了新路网后调用）。转发给 <see cref="SimRoadGraph"/> 单例。</summary>
    public static void Invalidate() => SimRoadGraph.Invalidate();

    /// <summary>本时段可做车次动画的采装任务（有主设备、有配车、有工作量）。</summary>
    public static List<ProductionTask> Candidates(IEnumerable<ProductionTask> tasks)
        => tasks.Where(t => t.Process == ProcessType.Load
                         && t.TargetVolumeM3 > 1e-6
                         && t.EndHour > t.StartHour)
                .OrderBy(t => t.StartHour).ThenBy(t => t.WorkZone)
                .ToList();

    /// <summary>
    /// 为一条采装任务展开车次动画场景。任何一环缺失都降级：
    /// 解不出编组 → <see cref="TripScene.Available"/>=false（不画、不猜）；
    /// 解不出路网 → 退源汇两点直线插值，并在 <see cref="TripScene.RouteSource"/> 标注为简化。
    /// </summary>
    public static TripScene Build(ProductionTask task, SimRegionSet? regions, SinkRegistry? sinks, bool actual = false)
    {
        var scene = new TripScene
        {
            TaskId = task.Id,
            FaceName = task.WorkZone,
            SinkName = string.IsNullOrWhiteSpace(task.DestinationName) ? task.DestinationId : task.DestinationName,
            MaterialCaption = task.ResolvedMix.Caption,
            ShovelId = task.Group.MainEquipment,
            TruckIds = new List<string>(task.Group.Trucks),
            StartHour = task.StartHour,
            EndHour = task.EndHour,
            TaskInSituM3 = actual ? task.ActualVolumeM3 : task.TargetVolumeM3,
        };

        if (scene.TruckIds.Count == 0)
        {
            scene.Status = $"{task.WorkZone}：本任务未配卡车（编组只有 {task.Group.MainEquipment}），无车次可展开。";
            return scene;
        }
        if (scene.HorizonMin <= 1)
        {
            scene.Status = $"{task.WorkZone}：任务时段不足 1 分钟，无车次可展开。";
            return scene;
        }

        // ── 运距 + 编组：**多腿**（混采面每种物料一条线），复用既有求解器，不另起炉灶 ──
        SinkNode? sink = FindSink(sinks, task);
        var face = FaceOf(task);
        HaulLeg mainLeg;
        IReadOnlyList<(MaterialDestination Dest, HaulLeg Leg)> resolved;
        FleetMatchResult match;
        try
        {
            mainLeg = HaulResolver.Resolve(face, sink);
            resolved = HaulResolver.ResolveAll(face, sinks);
            // 多腿解不出（无分项 / 全被过滤）就退单腿口径——降级，不崩
            match = resolved.Count > 0 ? FleetMatcher.Match(face, resolved) : FleetMatcher.Match(face, mainLeg);
        }
        catch (Exception ex)
        {
            scene.Status = $"{task.WorkZone}：编组/运距求解失败（{Short(ex)}），车次动画不可用。";
            return scene;
        }

        scene.CycleMin = match.CycleTimeMin;
        scene.TaktMin = match.LoadTaktMin;
        scene.MatchFactor = match.MatchFactor;
        scene.OptimalTrucks = match.OptimalTrucks;
        scene.Bottleneck = match.Bottleneck;
        scene.FleetExplain = match.Explain;
        scene.HaulCaption = mainLeg.Caption;

        if (scene.CycleMin <= 1e-6 || scene.TaktMin <= 1e-6)
        {
            scene.Status = $"{task.WorkZone}：循环时间/装车节拍解为 0（编组台账异常），车次动画不可用。";
            return scene;
        }

        // 加权四段（只用于表头；逐趟一律走各自那条线）
        (scene.LoadedMin, scene.EmptyMin, scene.TurnaroundMin, bool wSimplified) =
            SplitCycle(scene.CycleMin, scene.TaktMin, mainLeg.LoadedMin, mainLeg.EmptyMin);

        // ── 线表：**必须**取 TruckTripPlanner 的规范线表，下标要和甘特/派车单对齐 ──
        BuildLines(scene, task, match, resolved, mainLeg);
        if (scene.Lines.Count == 0)
        {
            scene.Status = $"{task.WorkZone}：解不出任何运输线（去向/运距全空），车次动画不可用。";
            return scene;
        }
        if (scene.Lines.Any(l => l.TravelSplitSimplified) || wSimplified)
            scene.Notes.Add("部分线的运距层未给出重车/空车行车时间，行车段按「T_c − τ_L」对半拆分（简化），卸车段并入行车。");

        if (scene.IsMultiLeg)
        {
            string joined = string.Join(" / ", scene.Lines.Select(l => l.SinkText).Where(s => s.Length > 0).Distinct());
            if (joined.Length > 0) scene.SinkName = joined;
            scene.Notes.Add($"混采面多腿：{scene.Lines.Count} 条运输线，每趟按【车次份额】分摊（"
                          + string.Join("；", scene.Lines.Select(l => l.Caption)) + "）。"
                          + "车次归线复用 TruckTripPlanner.AllocateTripLegs —— 与设备甘特、派车单是同一套分摊，"
                          + "「同一台车的第 N 趟」三处指向同一个去向。");
            scene.Notes.Add("注意口径：【车次份额】按吨量分（一车拉多少吨），【实方份额】按体积分（挖了多少方），"
                          + "两者在密度不同的物料间必然不等，不可互相替用。");
        }

        // ── 排队推进（电铲单服务台 + 每条线各一个卸点服务台）──
        BuildLegs(scene);
        if (scene.Legs.Count == 0)
        {
            scene.Status = $"{task.WorkZone}：本时段跑不完一趟（T_c {scene.CycleMin:0.#}min > 时段 {scene.HorizonMin:0.#}min），无完整车次。";
            return scene;
        }

        // 每车次工程量：任务量 ÷ 本时段车次数（派生量，不是台账载重）
        // 一趟都没跑完时用「已开工车次数」当分母，免得把整条任务的量塞进一车。
        int trips = scene.TotalTrips > 0 ? scene.TotalTrips : Math.Max(1, scene.Legs.Count);
        scene.PerTripInSituM3 = scene.TaskInSituM3 / trips;
        var mix = task.ResolvedMix;
        scene.PerTripLooseM3 = mix.ToLooseM3(scene.PerTripInSituM3);
        scene.PerTripTonnageT = mix.ToTonnage(scene.PerTripInSituM3);
        scene.Notes.Add($"每车次量按「任务量 ÷ 本时段车次数」均摊得 {scene.PerTripInSituM3:0.#} m³实方 " +
                        $"（松方 {scene.PerTripLooseM3:0.#} m³ · {scene.PerTripTonnageT:0.#} t）——是派生量，非台账载重。");

        // ── 路线：一条线一条路 ──
        ResolveRoutes(scene, task, sink, sinks, regions);

        scene.Available = true;
        scene.Status = $"{task.WorkZone} → {scene.SinkName}　T_c {scene.CycleMin:0.#}min · τ_L {scene.TaktMin:0.##}min · "
                     + $"配 {scene.TruckCount} 车（荐 {scene.OptimalTrucks}）· {scene.Bottleneck}"
                     + (scene.IsMultiLeg ? $"　| 多腿：{scene.Lines.Count} 条线（T_c 加权，逐趟按各线自算）" : "");
        return scene;
    }

    /// <summary>
    /// 装配运输线表。
    /// <para>
    /// 线的**身份与顺序**取 <see cref="TaskLib.Gantt.TruckTripPlanner.LegPlanOf"/> —— 这是甘特与派车单
    /// 用的同一份规范线表，下标即线序号；动画自己另排一套顺序的话，
    /// <see cref="TaskLib.Gantt.TruckTripPlanner.AllocateTripLegs"/> 分出来的趟就会指到别的去向上。
    /// </para>
    /// <para>
    /// 线的**行车分段**（重车/空车）另从 <see cref="HaulResolver.ResolveAll"/> 的 HaulLeg 按物料取，
    /// 因为规范线表只带 LoadedMin（派车单够用），动画要把循环拆成四段。
    /// </para>
    /// <para>规范线表拿不到时退化成单线（用加权解 + 主去向），动画照跑，并在 Notes 里说明。</para>
    /// </summary>
    private static void BuildLines(TripScene scene, ProductionTask task, FleetMatchResult match,
                                   IReadOnlyList<(MaterialDestination Dest, HaulLeg Leg)> resolved, HaulLeg mainLeg)
    {
        IReadOnlyList<TaskLib.Gantt.TripLeg> plan = Array.Empty<TaskLib.Gantt.TripLeg>();
        try { plan = TaskLib.Gantt.TruckTripPlanner.LegPlanOf(task); }
        catch { plan = Array.Empty<TaskLib.Gantt.TripLeg>(); }

        HaulLeg? HaulFor(string code)
        {
            foreach (var (d, l) in resolved)
                if (string.Equals(d.MaterialCode, code, StringComparison.OrdinalIgnoreCase)) return l;
            return null;
        }

        if (plan.Count > 0)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var p = plan[i];
                var hl = HaulFor(p.MaterialCode) ?? mainLeg;
                double takt = p.TaktMin > 1e-6 ? p.TaktMin : scene.TaktMin;
                double tc = p.CycleMin > 1e-6 ? p.CycleMin : scene.CycleMin;
                // T_c 比 τ_L 还短是台账脏数据（行车段成了负的）：退回加权解，别让某条线把趟数刷爆
                if (tc < takt) { tc = scene.CycleMin; takt = scene.TaktMin; }
                var (ld, mt, tn, simp) = SplitCycle(tc, takt, hl.LoadedMin, hl.EmptyMin);
                scene.Lines.Add(new TripLine
                {
                    Index = i,
                    MaterialCode = p.MaterialCode,
                    SinkId = p.SinkId, SinkName = p.SinkName, SinkKind = p.SinkKind,
                    TripShare = p.TripShare,          // 车次份额（吨量口径）
                    VolumeShare = p.VolumeShare,      // 实方份额（体积口径）
                    HaulKm = p.HaulKm,
                    CycleMin = tc, TaktMin = takt,
                    LoadedMin = ld, EmptyMin = mt, TurnaroundMin = tn,
                    TravelSplitSimplified = simp,
                });
            }
            return;
        }

        // 降级：单线。份额置 1，去向取任务主去向。
        {
            var (ld, mt, tn, simp) = SplitCycle(scene.CycleMin, scene.TaktMin, mainLeg.LoadedMin, mainLeg.EmptyMin);
            string code = task.ResolvedMix.Normalized().PrimaryCode;
            scene.Lines.Add(new TripLine
            {
                Index = 0,
                MaterialCode = string.IsNullOrWhiteSpace(code) ? task.MaterialCode : code,
                SinkId = task.DestinationId, SinkName = task.DestinationName, SinkKind = task.DestinationKind,
                TripShare = 1, VolumeShare = 1,
                HaulKm = mainLeg.EquivKm > 1e-6 ? mainLeg.EquivKm : mainLeg.Km,
                CycleMin = scene.CycleMin, TaktMin = scene.TaktMin,
                LoadedMin = ld, EmptyMin = mt, TurnaroundMin = tn,
                TravelSplitSimplified = simp,
            });
            if (match.IsMultiLeg)
                scene.Notes.Add("编组解出了多条线，但规范线表（TruckTripPlanner.LegPlanOf）没取到 —— "
                              + "动画退化为单线加权口径，去向按任务主去向画。此时不要拿动画的去向去对派车单。");
        }
    }

    /// <summary>
    /// 把 T_c 拆成 τ_L + t_重 + T_调卸 + t_空 —— 四段之和恒等于 T_c，
    /// 既不重复计时，也不在本文件里另造调车/卸载常量。
    /// 运距层没给重/空车时间（或拆出负的调卸段）时按「T_c − τ_L」对半拆，并回报 simplified=true。
    /// </summary>
    private static (double Loaded, double Empty, double Turnaround, bool Simplified) SplitCycle(
        double cycleMin, double taktMin, double loadedMin, double emptyMin)
    {
        double turn = cycleMin - taktMin - loadedMin - emptyMin;
        if (loadedMin > 1e-6 && emptyMin > 1e-6 && turn >= 0) return (loadedMin, emptyMin, turn, false);
        double travel = Math.Max(0, cycleMin - taktMin);
        return (travel * 0.5, travel * 0.5, 0, true);
    }

    /// <summary>
    /// 事件推进：谁先回到铲下谁先装；铲是单服务台，**每条线的卸点各是一个单服务台**。
    /// <para>
    /// 每趟走哪条线由 <see cref="TaskLib.Gantt.TruckTripPlanner.AllocateTripLegs"/> 按【车次份额】分摊，
    /// 索引口径 = 「第 i 台车的第 k 趟 → alloc[k−1]」，与甘特 <c>TruckTripPlanner.Trips</c> 完全一致；
    /// 该序列具前缀稳定性，所以两边车次数不同也不影响「同一趟指向同一条线」。
    /// </para>
    /// <para>各段时长一律取**本趟那条线**的 τ_L(m)/t_重(m)/T_调卸(m)/t_空(m)，不是加权值。</para>
    /// </summary>
    private static void BuildLegs(TripScene scene)
    {
        int n = scene.TruckCount;
        int lineCount = Math.Max(1, scene.Lines.Count);
        double horizon = scene.HorizonMin;

        // 单车车次上限：给分摊序列留够长度（前缀稳定，多算不影响前面的项）
        int allocLen = Math.Min(MaxLegs, Math.Max(8, MaxLegs / Math.Max(1, n))) + 2;
        int[] alloc;
        try
        {
            var planLegs = scene.Lines.Select(l => new TaskLib.Gantt.TripLeg
            {
                MaterialCode = l.MaterialCode, TripShare = l.TripShare, VolumeShare = l.VolumeShare,
                SinkId = l.SinkId, SinkName = l.SinkName, SinkKind = l.SinkKind,
                HaulKm = l.HaulKm, CycleMin = l.CycleMin, TaktMin = l.TaktMin, LoadedMin = l.LoadedMin,
            }).ToList();
            alloc = TaskLib.Gantt.TruckTripPlanner.AllocateTripLegs(planLegs, allocLen);
        }
        catch { alloc = new int[allocLen]; }        // 分摊失败 → 全走 0 号线，动画照跑

        var ready = new double[n];                 // 各车回到铲下的时刻
        var tripNo = new int[n];
        var sinkFree = new double[lineCount];      // 各线卸点的空闲时刻
        double shovelFree = 0, prevLoadEnd = 0, idle = 0;

        while (scene.Legs.Count < MaxLegs)
        {
            int i = 0;
            for (int k = 1; k < n; k++) if (ready[k] < ready[i]) i = k;

            double arrive = ready[i];
            if (arrive >= horizon) break;

            double loadStart = Math.Max(arrive, shovelFree);
            if (loadStart >= horizon) break;

            int trip = tripNo[i] + 1;                            // 本车第几趟（1 起）
            int li = alloc.Length > 0 ? alloc[Math.Min(trip - 1, alloc.Length - 1)] : 0;
            if (li < 0 || li >= lineCount) li = 0;
            var line = scene.Lines[li];

            idle += Math.Max(0, loadStart - prevLoadEnd);

            double loadEnd = loadStart + Math.Max(1e-6, line.TaktMin);
            shovelFree = loadEnd; prevLoadEnd = loadEnd;

            double arriveSink = loadEnd + line.LoadedMin;
            double dumpStart = Math.Max(arriveSink, sinkFree[li]);
            double dumpEnd = dumpStart + line.TurnaroundMin;
            sinkFree[li] = dumpEnd;

            double back = dumpEnd + line.EmptyMin;

            tripNo[i] = trip;
            scene.Legs.Add(new TripLeg
            {
                TruckIndex = i, TruckId = scene.TruckIds[i], TripIndex = trip, LineIndex = li,
                ArriveShovelMin = arrive, LoadStartMin = loadStart, LoadEndMin = loadEnd,
                ArriveSinkMin = arriveSink, DumpStartMin = dumpStart, DumpEndMin = dumpEnd,
                BackAtMin = back,
                Completed = back <= horizon,
            });

            ready[i] = back;
        }

        scene.ShovelIdleMin = idle;
    }

    // ── 路线求解 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 逐线求路：混采面两条线去两个卸点，画一条路会把「煤车往排土场跑」这种错觉画出来。
    /// 每条线独立走「路网 → 区域质心直线」两层，全部失败时给一条示意线，动画照跑。
    /// </summary>
    private static void ResolveRoutes(TripScene scene, ProductionTask task, SinkNode? mainSink,
                                      SinkRegistry? sinks, SimRegionSet? regions)
    {
        var srcPit = regions != null && !regions.IsEmpty
            ? (regions.Pits.FirstOrDefault(r => Hit(r.Name, task.WorkZone)) ?? regions.Pits.FirstOrDefault())
            : null;
        SimPoint a = srcPit?.Centroid ?? new SimPoint(0, 0);

        int real = 0, plain = 0;
        double fan = 0;                    // 简化线的扇形张角，免得两条线画成一条
        foreach (var line in scene.Lines)
        {
            var sk = FindSinkFor(sinks, line) ?? (scene.Lines.Count == 1 ? mainSink : null);
            var pts = RealRoute(task, sk);
            if (pts != null) { scene.Routes.Add(pts); real++; continue; }

            // 简化：源 → 该线卸点。卸点位置优先区域轮廓质心 → 汇台账坐标 → 扇形示意
            SimPoint b;
            var dmp = regions == null || regions.IsEmpty ? null
                : regions.Dumps.FirstOrDefault(r => Hit(r.Name, line.SinkText)
                                                 || (sk != null && string.Equals(r.SinkId, sk.Id, StringComparison.OrdinalIgnoreCase)));
            if (dmp != null) b = dmp.Centroid;
            else if (sk != null && (Math.Abs(sk.X) > 1e-6 || Math.Abs(sk.Y) > 1e-6)) b = new SimPoint(sk.X, sk.Y);
            else
            {
                double ang = fan; fan += 0.55;
                double len = Math.Max(300, line.HaulKm * 1000);
                b = new SimPoint(a.X + len * Math.Cos(ang), a.Y + len * Math.Sin(ang));
            }
            if ((b - a).Length <= 1e-6) b = new SimPoint(a.X + Math.Max(300, line.HaulKm * 1000), a.Y);

            scene.Routes.Add(new List<SimPoint> { a, b });
            plain++;
        }

        scene.RouteIsReal = real > 0 && plain == 0;
        scene.RouteSource = real == scene.Lines.Count
            ? $"路线：{SimRoadGraph.Label}（{real} 条线全走真实路网）"
            : real > 0
                ? $"路线：{real}/{scene.Lines.Count} 条线走真实路网（{SimRoadGraph.Label}），其余为源汇直线简化"
                : regions is { IsEmpty: false }
                    ? (regions.AllSynthetic
                        ? "简化：源汇两点直线插值（两端取示意区域质心；无可用路网路径）"
                        : "简化：源汇两点直线插值（两端取真实区域质心；无可用路网路径，未走真实路线）")
                    : "简化：源汇两点直线插值（无路网存档，位置为示意）";

        if (plain > 0)
            scene.Notes.Add("有线路未走真实路网：卡车位置只表达「循环到第几段」，不代表真实行驶轨迹。" +
                            "在「开拓运输 · 保存路网」存一期路网后自动切到真实路径。");
    }

    /// <summary>
    /// 路网求一条「作业面 → 该汇」的真实路径；任何一步不成立返回 null（路网层永不阻塞）。
    /// <para>
    /// 求解本体在 <see cref="SimRoadGraph"/>（进程内单例）。本方法只做「任务/汇 → 查询键」的映射。
    /// ★ 汇点 Z 传 0 是**改造前就有的写法**（<c>SinkNode.Z</c> 其实有值），
    ///   连同 500 m 三维吸附一起原样保留以保证行为不变；存疑记录见 <c>SimRoadGraph.RouteByKeys</c>。
    /// </para>
    /// </summary>
    private static List<SimPoint>? RealRoute(ProductionTask task, SinkNode? sink)
    {
        if (sink == null) return null;
        var r = SimRoadGraph.RouteByKeys(
            new[] { task.EngineeringPositionId, task.WorkZone }, new Point3d(0, 0, 0),
            new[] { sink.RefEntityId, sink.Id }, new Point3d(sink.X, sink.Y, 0));
        return r.Hit ? r.ToPolyline() : null;
    }

    /// <summary>按线上的去向 Id/名字找汇。</summary>
    private static SinkNode? FindSinkFor(SinkRegistry? reg, TripLine line)
    {
        if (reg == null) return null;
        try
        {
            var s = reg.Find(line.SinkId);
            if (s != null) return s;
            if (!string.IsNullOrWhiteSpace(line.SinkName))
                return reg.All.FirstOrDefault(x => string.Equals(x.Name, line.SinkName, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return null;
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    /// <summary>任务 → 编组求解的输入面（只读求解，拷一份编组，杜绝求解器改到任务上）。</summary>
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

        // 分项去向必须带上（且深拷贝）：多腿求解全靠它，漏了就静默退化成单去向口径，
        // 混采面会只按主去向那条线跑动画。与 TruckTripPlanner.FaceOf 保持一致。
        Splits = t.Splits.Select(s => s.Clone()).ToList(),

        DayTargetM3 = t.TargetVolumeM3,
        Process = ProcessType.Load,
        Group = new EquipmentGroup
        {
            MainEquipment = t.Group.MainEquipment,
            Trucks = new List<string>(t.Group.Trucks),
            Aux = new List<string>(t.Group.Aux),
            RecommendedTrucks = t.Group.RecommendedTrucks,
            GroupCapacityM3PerH = t.Group.GroupCapacityM3PerH,
        },
    };

    private static SinkNode? FindSink(SinkRegistry? reg, ProductionTask t)
    {
        if (reg == null) return null;
        var s = reg.Find(t.DestinationId);
        if (s != null) return s;
        foreach (var cand in new[] { t.DestinationName, t.WorkZone })
        {
            if (string.IsNullOrWhiteSpace(cand)) continue;
            var hit = reg.All.FirstOrDefault(x => string.Equals(x.Name, cand, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    private static bool Hit(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase));

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
