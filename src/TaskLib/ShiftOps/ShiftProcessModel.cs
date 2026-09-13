// 忠实移植自原 PitMine3D Modules/TaskLib/ShiftOps/ShiftProcessModel.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;      // ShiftWindow / FaceInput（排产侧的输入契约）
using PitMine3D.Kylin.TaskLib.Simulation;  // SimPoint / SimRegion（作业区域几何）

namespace PitMine3D.Kylin.TaskLib.ShiftOps;

/// <summary>工艺链上的一环。顺序即工艺顺序，不是显示顺序。</summary>
public enum ChainStage
{
    Drill = 0,      // 穿孔：为下一次爆破备孔
    Blast = 1,      // 爆破：把实体岩变成可装的爆堆
    Load = 2,       // 采装：铲把爆堆装上车
    Haul = 3,       // 运输：车把料拉到去向
    Dump = 4,       // 排土：推土机把料摊平压实
}

/// <summary>系统此刻卡在哪。</summary>
public enum Bottleneck
{
    None,
    ShovelWaitsTruck,   // 铲等车：运力 < 铲装能力
    TruckWaitsShovel,   // 车等铲：铲装能力 < 运力
    DumpLimited,        // 排土受限：排土场承接不下
    NoFace,             // 没有可装的面（爆堆没备好 / 该面这个班没排）
}

public static class ShiftOpsLabels
{
    public static string Label(this ChainStage s) => s switch
    {
        ChainStage.Drill => "穿孔",
        ChainStage.Blast => "爆破",
        ChainStage.Load => "采装",
        ChainStage.Haul => "运输",
        _ => "排土",
    };

    public static string Label(this Bottleneck b) => b switch
    {
        Bottleneck.ShovelWaitsTruck => "铲等车（运力不足）",
        Bottleneck.TruckWaitsShovel => "车等铲（铲装能力不足）",
        Bottleneck.DumpLimited => "排土受限（排土场承接不下）",
        Bottleneck.NoFace => "无可装作业面",
        _ => "无明显瓶颈",
    };
}

/// <summary>一条工艺线：一个采装面 + 它的车队 + 它的去向，构成一个可以独立卡住的子系统。</summary>
public sealed class ProcessLine
{
    public string Zone { get; init; } = "";
    public string Shovel { get; init; } = "";
    public List<string> Trucks { get; init; } = new();
    public int RecommendedTrucks { get; init; }
    public string Destination { get; init; } = "";
    public string Material { get; init; } = "";

    public double StartHour { get; init; }
    public double EndHour { get; init; }
    public double TargetM3 { get; init; }

    /// <summary>编组班产 = min(铲装能力, 车队运力)，由排产给出（m³/h 实方）。</summary>
    public double GroupCapacityM3PerH { get; init; }

    /// <summary>单程运距（km）。等效优先（含坡度折算），没有才回落实距。</summary>
    public double HaulKm { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public bool HasPosition { get; init; }

    /// <summary>去向的坐标（排土场 / 卸载点）。没有就不画运料方向 —— 画一条指向不明的线更误导。</summary>
    public double DestX { get; init; }
    public double DestY { get; init; }
    public double DestZ { get; init; }
    public bool HasDestPosition { get; init; }

    /// <summary>
    /// 这条线干在哪块地上（作业区域的环，世界坐标）。空 = 没配到区域，画面退回打点。
    /// <para>点答不了"这活的范围有多大、跟隔壁面挨着没有"——而那正是作业区域这三个字的全部内容。</para>
    /// </summary>
    public IReadOnlyList<SimPoint> Ring { get; init; } = Array.Empty<SimPoint>();

    /// <summary>环的代表高程 m。</summary>
    public double RingZ { get; init; }

    /// <summary>环的来源：「工序区」/「面级」/「无」。<b>必须显示</b>——两者画面上分不出，说的却是两件事。</summary>
    public string RingSource { get; init; } = ShiftZoneGeometry.SrcNone;

    public bool HasRing => Ring.Count >= 3;

    public double DurationH => Math.Max(0, EndHour - StartHour);

    /// <summary>这条线在时刻 t 干着没有。</summary>
    public bool ActiveAt(double t) => t >= StartHour && t < EndHour;

    /// <summary>到时刻 t 为止这条线已完成的比例（按时长线性推）。</summary>
    public double ProgressAt(double t)
        => DurationH < 1e-9 ? (t >= EndHour ? 1 : 0)
                            : Math.Clamp((t - StartHour) / DurationH, 0, 1);

    // ── 车流：Little 定律 ────────────────────────────────────────────────────
    //  在制车数 n = λ × W。λ = 出车率（车/h），W = 一个往返的周转时间（h）。
    //  ⚠ 不许对 n 取 mod：n < 1 时"每隔几分钟才有一台车在途"是真实状态，
    //    取 mod 会把它夸大成 1/n 倍。见 [车流密度是 Little 定律]。

    /// <summary>单车斗容折算（m³ 实方/车）。排产没给车数时按经验值兜底并如实标注。</summary>
    public double PerTruckM3 { get; init; }

    /// <summary>往返周转时间（h）：装 + 重车行 + 卸 + 空车行。</summary>
    public double CycleH { get; init; }

    /// <summary>出车率 λ（车/h）= 班产 ÷ 单车装载量。</summary>
    public double TripsPerHour => PerTruckM3 > 1e-9 ? GroupCapacityM3PerH / PerTruckM3 : 0;

    /// <summary>在途车数 n = λ × W（不取整、不取 mod）。</summary>
    public double TrucksInTransit => TripsPerHour * CycleH;

    /// <summary>
    /// 这条线此刻卡在哪。
    ///
    /// <para><b>口径</b>：排产已经把 <c>GroupCapacityM3PerH</c> 算成 min(铲装能力, 车队运力)，
    /// 但没说是哪一侧小。这里用「配车 vs 荐车」反推 ——
    /// 配得比推荐少 ⇒ 运力是短板（铲等车）；配得比推荐多 ⇒ 铲是短板（车等铲）。
    /// 推荐数缺失时不猜，返回 <see cref="Bottleneck.None"/> 并在 <see cref="BottleneckWhy"/> 里说明。</para>
    /// </summary>
    public Bottleneck BottleneckAt(double t)
    {
        if (!ActiveAt(t)) return Bottleneck.None;
        if (RecommendedTrucks <= 0) return Bottleneck.None;
        if (Trucks.Count < RecommendedTrucks) return Bottleneck.ShovelWaitsTruck;
        if (Trucks.Count > RecommendedTrucks) return Bottleneck.TruckWaitsShovel;
        return Bottleneck.None;
    }

    public string BottleneckWhy => RecommendedTrucks <= 0
        ? "编组规则没给推荐车数 ⇒ 判不出是铲等车还是车等铲（不猜）"
        : Trucks.Count < RecommendedTrucks
            ? $"配 {Trucks.Count} 车 < 荐 {RecommendedTrucks} 车，缺 {RecommendedTrucks - Trucks.Count} 台 ⇒ 铲将待车"
            : Trucks.Count > RecommendedTrucks
                ? $"配 {Trucks.Count} 车 > 荐 {RecommendedTrucks} 车，多 {Trucks.Count - RecommendedTrucks} 台 ⇒ 车将排队待装"
                : $"配 {Trucks.Count} 车 = 荐 {RecommendedTrucks} 车，供需匹配";
}

/// <summary>不搬料但占工序位的那几道（穿孔 / 爆破 / 检修·空闲）。</summary>
public sealed class ProcessStep
{
    public ChainStage Stage { get; init; }
    public string Zone { get; init; } = "";
    public string Equipment { get; init; } = "";
    public double StartHour { get; init; }
    public double EndHour { get; init; }
    public bool IsIdle { get; init; }
    public string IdleWhy { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public bool HasPosition { get; init; }

    /// <summary>这道工序干在哪块地上。穿孔/爆破/排土各有各的地，配不到时为空。</summary>
    public IReadOnlyList<SimPoint> Ring { get; init; } = Array.Empty<SimPoint>();
    public double RingZ { get; init; }
    public string RingSource { get; init; } = ShiftZoneGeometry.SrcNone;
    public bool HasRing => Ring.Count >= 3;

    public bool ActiveAt(double t) => t >= StartHour && t < EndHour;
}

/// <summary>一个班的工艺·工序系统。</summary>
public sealed class ShiftProcessSystem
{
    public string ShiftName { get; init; } = "";
    public double StartHour { get; init; }
    public double EndHour { get; init; }

    public List<ProcessLine> Lines { get; } = new();      // 采装—运输—排土 的成套线
    public List<ProcessStep> Steps { get; } = new();      // 穿孔 / 爆破 / 检修·空闲
    public List<string> Notes { get; } = new();

    public double DurationH => Math.Max(0, EndHour - StartHour);

    /// <summary>班内相对时钟 → 绝对小时。</summary>
    public double ClockAt(double phase) => StartHour + DurationH * Math.Clamp(phase, 0, 1);

    /// <summary>本班排土承接总量（占容方）—— 由排土工序的作业量给出。</summary>
    public double DumpAcceptM3 { get; set; }

    /// <summary>本班采装出方合计（实方）。</summary>
    public double LoadedM3 => Lines.Sum(l => l.TargetM3);

    /// <summary>此刻在干的工艺线。</summary>
    public IEnumerable<ProcessLine> ActiveLines(double t) => Lines.Where(l => l.ActiveAt(t));

    /// <summary>此刻的系统瓶颈（取占比最大的那一类；都没有则 None）。</summary>
    public Bottleneck BottleneckAt(double t)
    {
        var act = ActiveLines(t).ToList();
        if (act.Count == 0)
            return Steps.Any(s => s.ActiveAt(t) && !s.IsIdle) ? Bottleneck.None : Bottleneck.NoFace;

        var byKind = act.Select(l => l.BottleneckAt(t))
                        .Where(b => b != Bottleneck.None)
                        .GroupBy(b => b)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault();
        return byKind?.Key ?? Bottleneck.None;
    }
}

/// <summary>
/// 一天三个班的工艺·工序系统。
///
/// <para><b>这一层答的是"一个班里工序系统怎么跑"</b>：
/// 穿孔备孔 → 爆破成堆 → 铲装上车 → 车拉到去向 → 推土摊平，
/// 每一环的能力、互锁、以及此刻卡在哪。它和月度那套（逐月采剥接续、采排体积配对、
/// 推进反算）不是同一个尺度，也不共用任何口径。</para>
/// </summary>
public sealed class DayProcessPlan
{
    public DateTime Date { get; init; }
    public List<ShiftProcessSystem> Shifts { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>一天的时间跨度（取三个班的并集，缺省 0–24）。</summary>
    public (double Lo, double Hi) DayRange => Shifts.Count == 0
        ? (0, 24)
        : (Math.Min(0, Shifts.Min(s => s.StartHour)), Math.Max(24, Shifts.Max(s => s.EndHour)));

    /// <summary>某个绝对时刻落在哪个班（找不到返回 null —— 班次之间的空档是真的存在）。</summary>
    public ShiftProcessSystem? ShiftAt(double hour)
        => Shifts.FirstOrDefault(s => hour >= s.StartHour && hour < s.EndHour);

    /// <summary>
    /// 从当日任务盘子建。
    ///
    /// <para><b>成线口径</b>：一条工艺线 = 一个**采装**任务（它带着铲、车队、去向、运距）。
    /// 排土任务不单独成线 —— 它是线的下游，量已经在采装侧记过一次，
    /// 单列会把同一批料记两遍（见 <see cref="ShiftProcessSystem.DumpAcceptM3"/> 只做承接口径）。
    /// 穿孔 / 爆破 / 检修进 <see cref="ShiftProcessSystem.Steps"/>：它们占工序位但不搬料。</para>
    /// </summary>
    /// <param name="zones">
    /// 作业区域几何（工序区 → 面级 → 无）。null = 不配地，退回只带点的老行为。
    /// </param>
    public static DayProcessPlan Build(IReadOnlyList<ProductionTask>? tasks,
                                       IReadOnlyList<ShiftWindow>? shifts,
                                       IReadOnlyList<FaceInput>? faces = null,
                                       DateTime date = default,
                                       SinkRegistry? sinks = null,
                                       ShiftZoneGeometry? zones = null)
    {
        var plan = new DayProcessPlan { Date = date };
        if (tasks == null || tasks.Count == 0)
        {
            plan.Notes.Add("当日任务盘子为空 —— 没有任务就没有工序系统可演（不是漏算）。");
            return plan;
        }

        // 班次窗口：优先用排班给的；没有就按任务的班次名归拢出时窗
        var wins = (shifts != null && shifts.Count > 0)
            ? shifts.Select(s => (Name: s.Name, Lo: s.Start, Hi: s.End)).ToList()
            : tasks.Where(t => !string.IsNullOrWhiteSpace(t.Shift))
                   .GroupBy(t => t.Shift!, StringComparer.Ordinal)
                   .Select(g => (Name: g.Key, Lo: g.Min(t => t.StartHour), Hi: g.Max(t => t.EndHour)))
                   .OrderBy(x => x.Lo).ToList();

        if (wins.Count == 0)
        {
            plan.Notes.Add("任务上没有班次名，也没有班次日历 ⇒ 分不出班，工序系统无从按班演。");
            return plan;
        }

        foreach (var w in wins)
        {
            var sys = new ShiftProcessSystem { ShiftName = w.Name, StartHour = w.Lo, EndHour = w.Hi };
            var mine = tasks.Where(t => string.Equals(t.Shift, w.Name, StringComparison.Ordinal)).ToList();

            foreach (var t in mine)
            {
                var face = Locate(faces, t);
                var zone = zones?.Resolve(t.Process, t.WorkZone) ?? default;
                if (t.Process == ProcessType.Load)
                {
                    double perTruck = PerTruckM3(t);
                    var dest = LocateSink(sinks, t);
                    sys.Lines.Add(new ProcessLine
                    {
                        Zone = t.WorkZone ?? "",
                        Shovel = t.Group?.MainEquipment ?? "",
                        Trucks = new List<string>(t.Group?.Trucks ?? new List<string>()),
                        RecommendedTrucks = t.Group?.RecommendedTrucks ?? 0,
                        Destination = t.DestinationName ?? "",
                        Material = t.Material ?? "",
                        StartHour = t.StartHour, EndHour = t.EndHour,
                        TargetM3 = t.TargetVolumeM3,
                        GroupCapacityM3PerH = t.Group?.GroupCapacityM3PerH ?? 0,
                        HaulKm = t.EquivHaulKm > 1e-9 ? t.EquivHaulKm : t.HaulDistanceKm,
                        PerTruckM3 = perTruck,
                        CycleH = CycleHours(t),
                        X = face.X, Y = face.Y, Z = face.Z, HasPosition = face.Has,
                        DestX = dest.X, DestY = dest.Y, DestZ = dest.Z, HasDestPosition = dest.Has,
                        Ring = zone.Has ? zone.Region!.Ring : Array.Empty<SimPoint>(),
                        RingZ = RingZOf(zone, face.Z),
                        RingSource = zone.Source,
                    });
                }
                else if (t.Process == ProcessType.Dump)
                {
                    sys.DumpAcceptM3 += t.TargetVolumeM3;
                    sys.Steps.Add(Step(ChainStage.Dump, t, face, zone));
                }
                else
                {
                    var stage = t.Process switch
                    {
                        ProcessType.Drill => ChainStage.Drill,
                        ProcessType.Blast => ChainStage.Blast,
                        ProcessType.Haul => ChainStage.Haul,
                        _ => ChainStage.Load,          // 检修/空闲挂在它占着的那个工序位上
                    };
                    sys.Steps.Add(Step(stage, t, face, zone));
                }
            }

            if (sys.Lines.Count == 0)
                sys.Notes.Add(sys.Steps.All(s => s.IsIdle)
                    ? "本班**没有采装线** —— 全班检修/空闲。工序系统这一层没有料在流，"
                    + "画面上的静止是真的，不是没读到数据。"
                    : "本班没有采装任务，只有不搬料的工序（穿孔/爆破/检修）。");

            plan.Shifts.Add(sys);
        }

        // 采排承接：采装出方 × Kr 应当被排土承接下来。这里只报差，不改任何一侧的量。
        foreach (var s in plan.Shifts.Where(s => s.Lines.Count > 0 && s.DumpAcceptM3 > 1e-6))
        {
            double need = s.LoadedM3 * 1.129;   // Kr 残余膨胀，与月度档同一个常数
            double gap = s.DumpAcceptM3 - need;
            if (Math.Abs(gap) / Math.Max(1, need) > 0.05)
                s.Notes.Add($"采排承接：本班采装 {s.LoadedM3 / 1e4:0.##}万m³实方 × Kr1.129 = 应排 {need / 1e4:0.##}万m³占容，"
                          + $"实排 {s.DumpAcceptM3 / 1e4:0.##}万m³，差 {gap / 1e4:+0.##;-0.##}万m³。"
                          + "**班内不同步是常态**（挖了先堆在采场边、下一班才拉走）——"
                          + "这里只记，不判对错；要判去看全天合计。");
        }

        return plan;
    }

    private static ProcessStep Step(ChainStage stage, ProductionTask t, (double X, double Y, double Z, bool Has) f,
                                    ZoneHit zone = default)
        => new()
        {
            Ring = zone.Has ? zone.Region!.Ring : Array.Empty<SimPoint>(),
            RingZ = RingZOf(zone, f.Z),
            RingSource = zone.Source.Length > 0 ? zone.Source : ShiftZoneGeometry.SrcNone,
            Stage = stage,
            Zone = t.WorkZone ?? "",
            Equipment = t.Group?.MainEquipment ?? "",
            StartHour = t.StartHour, EndHour = t.EndHour,
            IsIdle = t.Process == ProcessType.Idle,
            IdleWhy = t.Reasons.Count == 0 ? "" : string.Join("、", t.Reasons.Select(r => r.Label()).Distinct()),
            X = f.X, Y = f.Y, Z = f.Z, HasPosition = f.Has,
        };

    /// <summary>
    /// 环的代表高程：区域台账自带的优先；台账没给（NaN）时退回作业面那个点的 Z。
    /// <para>两个都没有就是 0 —— 俯视图上看不出差别，轴测图上会贴地。这一层不猜高程，
    /// 真要绝对高程得看区域的 <c>ZSource</c>（三层降级在那儿已经记过账了）。</para>
    /// </summary>
    private static double RingZOf(ZoneHit zone, double faceZ)
    {
        double z = zone.Region?.Z ?? double.NaN;
        if (!double.IsNaN(z)) return z;
        return double.IsNaN(faceZ) ? 0 : faceZ;
    }

    /// <summary>
    /// 去向坐标：先按去向号对，再按去向名对。没有就是没有 —— 不拿别的点凑。
    /// <para>工艺线要画的是「面 → 去向」的真方向；去向没坐标时窗口那边**不画线**，
    /// 而不是编一个方向出来（指向不明的线比没有线更误导）。</para>
    /// </summary>
    private static (double X, double Y, double Z, bool Has) LocateSink(SinkRegistry? sinks, ProductionTask t)
    {
        var all = sinks?.All;
        if (all == null || all.Count == 0) return (0, 0, 0, false);
        var s = all.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Id)
                    && string.Equals(x.Id, t.DestinationId, StringComparison.OrdinalIgnoreCase))
             ?? all.FirstOrDefault(x => string.Equals(x.Name, t.DestinationName, StringComparison.OrdinalIgnoreCase));
        if (s == null) return (0, 0, 0, false);
        bool has = Math.Abs(s.X) > 1e-6 || Math.Abs(s.Y) > 1e-6;
        return (s.X, s.Y, s.Z, has);
    }

    /// <summary>作业面坐标：先按工程位置号对，再按面名对。两条都不中就没坐标（不猜）。</summary>
    private static (double X, double Y, double Z, bool Has) Locate(IReadOnlyList<FaceInput>? faces, ProductionTask t)
    {
        if (faces == null || faces.Count == 0) return (0, 0, 0, false);
        var f = faces.FirstOrDefault(x => x.EngineeringPositionId.Length > 0
                    && string.Equals(x.EngineeringPositionId, t.EngineeringPositionId, StringComparison.OrdinalIgnoreCase))
             ?? faces.FirstOrDefault(x => string.Equals(x.Zone, t.WorkZone, StringComparison.OrdinalIgnoreCase));
        if (f == null || !f.HasSourcePosition) return (0, 0, 0, false);
        return (f.SourceX, f.SourceY,
                Math.Abs(f.SourceZ) > 1e-9 ? f.SourceZ : t.BenchElevationM, true);
    }

    /// <summary>
    /// 单车装载量（m³ 实方/车）。
    /// <para>排产没把斗容折算下沉到任务上，这里按「班产 ÷ (车数 × 每小时趟数)」是循环定义，
    /// 所以直接用一个**明标出来的**经验值：35 m³/车（常见 100t 级矿卡装松方 ~44 m³，
    /// 除以 Ks1.25 得实方 ~35）。它只影响"在途几台车"这个示意量，不进任何账。</para>
    /// </summary>
    internal static double PerTruckM3(ProductionTask t) => 35.0;

    /// <summary>
    /// 往返周转时间（h）：装 + 重车行 + 卸 + 空车行。
    /// <para>车速取重车 22 km/h、空车 30 km/h（露天矿常见值），装 3 min、卸 1.5 min。
    /// 这几个数是**示意口径**，用来把"在途几台车"算成一个有量纲的数，不参与任何结算。</para>
    /// </summary>
    internal static double CycleHours(ProductionTask t)
    {
        double km = t.EquivHaulKm > 1e-9 ? t.EquivHaulKm : t.HaulDistanceKm;
        if (km <= 1e-9) return 0;
        return km / 22.0 + km / 30.0 + (3.0 + 1.5) / 60.0;
    }
}
