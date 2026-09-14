using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>工艺链上的一环。<b>顺序即工艺顺序</b>，不是显示顺序。</summary>
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

/// <summary>一条工艺线：一个采装面 + 它的车队 + 它的去向，构成一个<b>可以独立卡住</b>的子系统。</summary>
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

    /// <summary>单程运距 km。等效优先（含坡度折算）。</summary>
    public double HaulKm { get; init; }

    public double DurationH => Math.Max(0, EndHour - StartHour);
    public bool ActiveAt(double t) => t >= StartHour && t < EndHour;

    /// <summary>此刻这条线干了几成（0..1）。</summary>
    public double ProgressAt(double t)
        => DurationH <= 1e-9 ? (t >= EndHour ? 1 : 0) : Math.Clamp((t - StartHour) / DurationH, 0, 1);

    // ── 车流：Little 定律 ────────────────────────────────────────────────────
    //  在制车数 n = λ × W。λ = 出车率（车/h），W = 一个往返的周转时间（h）。
    //  ⚠ <b>不许对 n 取整或取 mod</b>：n < 1 时"每隔几分钟才有一台车在途"是真实状态，
    //    取整会把它夸大成 1/n 倍，看着像车队一直满负荷。

    /// <summary>单车装载量 m³ 实方。0 = 台账没解出载重（此时车流判不了）。</summary>
    public double PerTruckM3 { get; init; }

    /// <summary>往返周转时间 h：装 + 重车行 + 卸 + 空车行。</summary>
    public double CycleH { get; init; }

    /// <summary>出车率 λ（车/h）= 班产 ÷ 单车装载量。判不了返回 null。</summary>
    public double? TripsPerHour => PerTruckM3 > 1e-9 ? GroupCapacityM3PerH / PerTruckM3 : null;

    /// <summary>在途车数 n = λ × W（<b>不取整、不取 mod</b>）。判不了返回 null。</summary>
    public double? TrucksInTransit => TripsPerHour is { } l && CycleH > 1e-9 ? l * CycleH : null;

    /// <summary>
    /// 这条线此刻卡在哪。
    /// <para>
    /// <b>口径</b>：排产已经把 <see cref="GroupCapacityM3PerH"/> 算成 min(铲装能力, 车队运力)，
    /// 但没说是哪一侧小。这里用「配车 vs 荐车」反推 —— 配得比推荐少 ⇒ 运力是短板（铲等车）；
    /// 配得比推荐多 ⇒ 铲是短板（车等铲）。<b>推荐数缺失时不猜</b>，返回 None 并在
    /// <see cref="BottleneckWhy"/> 里说明。
    /// </para>
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

    /// <summary>班内相对时钟（0..1）→ 绝对小时。</summary>
    public double ClockAt(double phase) => StartHour + DurationH * Math.Clamp(phase, 0, 1);

    /// <summary>本班排土承接总量（占容方 m³）—— 由排土工序的作业量给出。</summary>
    public double DumpAcceptM3 { get; set; }

    /// <summary>本班采装出方合计（实方 m³）。</summary>
    public double LoadedM3 => Lines.Sum(l => l.TargetM3);

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

    /// <summary>此刻卡在哪 + 为什么（逐线摊开，给人看的）。</summary>
    public string BottleneckCaption(double t)
    {
        var b = BottleneckAt(t);
        var act = ActiveLines(t).ToList();
        if (act.Count == 0)
            return b == Bottleneck.NoFace
                ? "此刻无可装作业面 —— 采装面这个班没排，或爆堆还没备好。"
                : "此刻没有采装线在跑（穿孔/爆破在进行）。";
        string why = string.Join("；", act.Select(l => $"{l.Zone}：{l.BottleneckWhy}"));
        return $"{b.Label()}　|　{why}";
    }
}

/// <summary>
/// 一天各班的工艺·工序系统（移植原 <c>TaskLib.ShiftOps.ShiftProcessModel</c> 的判定部分）。
///
/// <para>
/// <b>这一层答的是"一个班里工序系统怎么跑"</b>：穿孔备孔 → 爆破成堆 → 铲装上车 → 车拉到去向 →
/// 推土摊平，每一环的能力、互锁、以及此刻卡在哪。
/// </para>
///
/// <para>
/// <b>★ 为什么必须与月度那套分开，而不是加一个粒度开关</b>（原版注释，照搬）：合在一起之后
/// 所有量都要"按期长换算"，而两边的判据<b>互相不成立</b> ——
/// 采排体积配对在一个班上必然报不守恒（挖了先堆在采场边、下一班才拉走）；
/// 推进反算的分母是月度工作线长（拿一个班的量去除算出"推进几厘米"没有调度意义）；
/// 剥采比/内排率这类期内配比在班尺度上发散。分开之后每一层只留它成立的那些判据。
/// </para>
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item><b>画面三层未移</b>（正射影像做地 / 工艺线与工序标记跑在上面 / 班内时钟拖到哪一刻
///     三层同时就是那一刻）—— 依赖未移植的 <c>Sim*</c> overlay 与影像底图。本轮出的是同一套判定的
///     <b>表格形态</b>：时钟拖到哪一刻，表里给的就是那一刻在跑的线与此刻的瓶颈。</item>
///   <item>工艺线/工序的<b>几何</b>（作业区环、坐标）未带 —— 没有几何就不画，
///     也就不存在"拿缺省值伪造推进"的问题。</item>
/// </list>
/// </summary>
public sealed class DayProcessPlan
{
    public DateTime Date { get; init; }
    public List<ShiftProcessSystem> Shifts { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>一天的时间跨度（取各班的并集，缺省 0–24）。</summary>
    public (double Lo, double Hi) DayRange => Shifts.Count == 0
        ? (0, 24)
        : (Math.Min(0, Shifts.Min(s => s.StartHour)), Math.Max(24, Shifts.Max(s => s.EndHour)));

    /// <summary>某个绝对时刻落在哪个班（<b>找不到返回 null</b> —— 班次之间的空档是真的存在）。</summary>
    public ShiftProcessSystem? ShiftAt(double hour)
        => Shifts.FirstOrDefault(s => hour >= s.StartHour && hour < s.EndHour);

    /// <summary>
    /// 由当日盘子与装箱结果建一天的工艺·工序系统。
    /// 采装笔成线（带它的车队与去向），穿孔/爆破/空闲成工序位。
    /// </summary>
    public static DayProcessPlan Build(ExploderConfig? cfg, ExploderResult? plan, DateTime date)
    {
        var day = new DayProcessPlan { Date = date.Date };
        if (cfg == null || plan == null)
        {
            day.Notes.Add("没有当日盘子 —— 工序系统推不出来。");
            return day;
        }

        foreach (var sh in cfg.Shifts)
        {
            var sys = new ShiftProcessSystem { ShiftName = sh.Name, StartHour = sh.Start, EndHour = sh.End };
            var mine = plan.Tasks.Where(t => string.Equals(t.Shift, sh.Name, StringComparison.Ordinal)).ToList();

            foreach (var t in mine.Where(t => t.Process == ProcessType.Load))
            {
                double perTruck = t.Group.TruckPayloadT > 1e-9
                    ? t.Group.TruckPayloadT / Math.Max(0.1, MaterialCatalog.Resolve(MaterialCatalog.CodeFromText(t.Material)).InSituDensityTPerM3)
                    : 0;
                sys.Lines.Add(new ProcessLine
                {
                    Zone = t.WorkZone, Shovel = t.Group.MainEquipment,
                    Trucks = new List<string>(t.Group.Trucks),
                    RecommendedTrucks = t.Group.RecommendedTrucks,
                    Destination = t.DestinationName.Length > 0 ? t.DestinationName : t.DestinationId,
                    Material = t.Material,
                    StartHour = t.StartHour, EndHour = t.EndHour, TargetM3 = t.TargetVolumeM3,
                    GroupCapacityM3PerH = t.Group.GroupCapacityM3PerH,
                    HaulKm = t.EquivHaulKm,
                    PerTruckM3 = perTruck,
                    CycleH = t.Group.CycleTimeMin > 1e-9 ? t.Group.CycleTimeMin / 60.0 : 0,
                });
            }

            foreach (var t in mine.Where(t => t.Process is ProcessType.Drill or ProcessType.Blast or ProcessType.Dump or ProcessType.Idle))
                sys.Steps.Add(new ProcessStep
                {
                    Stage = t.Process switch
                    {
                        ProcessType.Drill => ChainStage.Drill,
                        ProcessType.Blast => ChainStage.Blast,
                        ProcessType.Dump => ChainStage.Dump,
                        _ => ChainStage.Load,
                    },
                    Zone = t.WorkZone, Equipment = t.Group.MainEquipment,
                    StartHour = t.StartHour, EndHour = t.EndHour,
                    IsIdle = t.Process == ProcessType.Idle,
                    IdleWhy = t.Process == ProcessType.Idle && t.Reasons.Count > 0
                        ? string.Join("、", t.Reasons.Select(AdjustModel.ReasonZh)) : "",
                });

            sys.DumpAcceptM3 = mine.Where(t => t.Process == ProcessType.Dump).Sum(t => t.TargetVolumeM3);

            // ★ 排土承接不下是**系统级**的卡点，不是某一条线的 —— 逐线判不出来它
            if (sys.LoadedM3 > 1e-6 && sys.DumpAcceptM3 > 1e-6 && sys.DumpAcceptM3 < sys.LoadedM3 * 0.5)
                sys.Notes.Add($"{sh.Name}：本班采装出方 {sys.LoadedM3:N0} m³，排土只承接 {sys.DumpAcceptM3:N0} m³ —— "
                            + "料会堆在采场边，下一班要连本带利拉走。");
            if (sys.Lines.Count == 0 && sys.Steps.Count == 0)
                sys.Notes.Add($"{sh.Name}：本班一条任务都没排。");

            day.Shifts.Add(sys);
        }

        if (day.Shifts.Count == 0) day.Notes.Add("盘子里一个班次都没有。");
        return day;
    }

    internal static string Hm(double hh)
    {
        int h = (int)hh;
        int m = (int)Math.Round((hh - h) * 60);
        if (m == 60) { h++; m = 0; }
        return $"{h:00}:{m:00}";
    }
}
