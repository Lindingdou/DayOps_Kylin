// 忠实移植自原 PitMine3D Modules/TaskLib/ShiftOps/ProcessDetailModel.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.ShiftOps;

/// <summary>工艺线内部的一个动作。这是**工序之下**那一层：一个装车循环里的各段。</summary>
public enum CycleAct
{
    Spot,        // 就位（车倒到铲下）
    LoadTruck,   // 装车（铲装满一台）
    HaulFull,    // 重车行
    Queue,       // 卸点排队
    Unload,      // 卸载
    HaulEmpty,   // 空车行
    ShovelWait,  // 铲等车（铲空转）
}

public static class CycleActLabels
{
    public static string Label(this CycleAct a) => a switch
    {
        CycleAct.Spot => "就位",
        CycleAct.LoadTruck => "装车",
        CycleAct.HaulFull => "重车行",
        CycleAct.Queue => "卸点排队",
        CycleAct.Unload => "卸载",
        CycleAct.HaulEmpty => "空车行",
        _ => "铲等车",
    };

    /// <summary>这一段是不是**有效作业**（不是等待）。</summary>
    public static bool IsProductive(this CycleAct a)
        => a is CycleAct.LoadTruck or CycleAct.HaulFull or CycleAct.Unload or CycleAct.HaulEmpty;
}

/// <summary>循环里的一段。属性都是**算出来的预测值**，不是实测。</summary>
public sealed class CycleSegment
{
    public CycleAct Act { get; init; }
    public int TripIndex { get; init; }          // 第几趟（从 1 起）
    public string Truck { get; init; } = "";
    public double StartH { get; init; }
    public double EndH { get; init; }
    public double DurationH => Math.Max(0, EndH - StartH);

    /// <summary>这一段搬的量（m³ 实方）。只有装车段有量，别处为 0 —— 一趟料只记一次。</summary>
    public double VolumeM3 { get; init; }

    /// <summary>这一段的行进距离（km）。只有行车段有。</summary>
    public double DistanceKm { get; init; }

    /// <summary>行进速度（km/h）。只有行车段有。</summary>
    public double SpeedKmh { get; init; }

    public string Caption => $"#{TripIndex} {Act.Label()}"
                           + (Truck.Length > 0 ? $"　{Truck}" : "")
                           + $"　{Hm(StartH)}–{Hm(EndH)}　{DurationH * 60:0.#} min"
                           + (VolumeM3 > 1e-9 ? $"　{VolumeM3:0.#} m³" : "")
                           + (DistanceKm > 1e-9 ? $"　{DistanceKm:0.##} km @ {SpeedKmh:0} km/h" : "");

    private static string Hm(double h)
    {
        if (double.IsNaN(h)) return "—";
        int hh = (int)Math.Floor(h), mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh % 24:00}:{mm:00}";
    }
}

/// <summary>
/// 一条工艺线的**细衔接预测**：把"配 2 车 &lt; 荐 6 车 ⇒ 铲将待车"这句定性话，
/// 算成每一趟的节拍、每一段的时长、以及铲/车各自等多久。
///
/// <para><b>为什么能算</b>：装车循环是确定性的排队问题 ——
/// 铲的节拍是"装满一台车要多久"（t_load），车的节拍是"每隔多久回来一台"（W / n）。
/// 两者谁慢谁定速：</para>
/// <list type="bullet">
///   <item>W/n &gt; t_load ⇒ <b>铲等车</b>，每趟空转 (W/n − t_load)；</item>
///   <item>W/n &lt; t_load ⇒ <b>车排队</b>，每趟排 (t_load − W/n)；</item>
/// </list>
/// <para>这不是"模拟"出来的噪声，是解析解 —— 同样的输入永远给同样的数，可以下判据。</para>
///
/// <para><b>口径边界</b>：所有速度/装卸时长都是**示意参数**（见 <see cref="Params"/>），
/// 用来把节拍算成有量纲的数，<b>不进任何账</b>。真值要接设备台账的实测循环时间。</para>
/// </summary>
public sealed class LineDetail
{
    /// <summary>示意参数。改这里等于改整条预测链，所以集中放一处并逐条标出依据。</summary>
    public sealed class Parameters
    {
        /// <summary>单车装载量（m³ 实方）。100t 级矿卡装松方约 44 m³，÷Ks1.25 ≈ 35。</summary>
        public double TruckM3 { get; set; } = 35.0;
        /// <summary>装满一台车（min）。铲斗 ~12 m³、3 斗一车、每斗 35 s。</summary>
        public double LoadMin { get; set; } = 3.0;
        /// <summary>就位（min）：车倒到铲下对位。</summary>
        public double SpotMin { get; set; } = 0.6;
        /// <summary>卸载（min）：举斗 + 落斗。</summary>
        public double UnloadMin { get; set; } = 1.5;
        /// <summary>重车速度（km/h）。</summary>
        public double FullKmh { get; set; } = 22.0;
        /// <summary>空车速度（km/h）。</summary>
        public double EmptyKmh { get; set; } = 30.0;
    }

    public Parameters Params { get; } = new();

    public ProcessLine Line { get; }
    public List<CycleSegment> Segments { get; } = new();
    public List<string> Notes { get; } = new();

    // ── 预测出来的节拍量 ─────────────────────────────────────────────────────

    /// <summary>铲的节拍：装满一台车的时间（h）＝ 就位 + 装车。</summary>
    public double ShovelTactH { get; private set; }
    /// <summary>车的节拍：每隔多久回来一台（h）＝ 周转 W ÷ 车数 n。</summary>
    public double TruckTactH { get; private set; }
    /// <summary>一个完整往返（h）：就位+装+重车行+卸+空车行。</summary>
    public double CycleH { get; private set; }
    /// <summary>本班这条线预测能跑几趟。</summary>
    public int Trips { get; private set; }
    /// <summary>预测出方（m³ 实方）＝ 趟数 × 单车装载量。</summary>
    public double PredictedM3 => Trips * Params.TruckM3;
    /// <summary>铲空转合计（h）。</summary>
    public double ShovelWaitH { get; private set; }
    /// <summary>车排队合计（h）。</summary>
    public double TruckQueueH { get; private set; }
    /// <summary>铲的作业率＝(总时长−空转)/总时长。</summary>
    public double ShovelUtil { get; private set; }

    public Bottleneck Kind { get; private set; }

    public LineDetail(ProcessLine line) => Line = line;

    /// <summary>
    /// 算一遍。<paramref name="maxTrips"/> 是**画图用的上限**（一个班几百趟画不下），
    /// 但趟数/出方/等待这些**总量按整班算**，不受它影响 —— 截断的是展示，不是账。
    /// </summary>
    public LineDetail Predict(int maxTrips = 40)
    {
        Segments.Clear(); Notes.Clear();
        var p = Params;
        int n = Math.Max(0, Line.Trucks.Count);
        double km = Line.HaulKm;

        double spot = p.SpotMin / 60.0, load = p.LoadMin / 60.0, unload = p.UnloadMin / 60.0;
        double full = km > 1e-9 ? km / p.FullKmh : 0;
        double empty = km > 1e-9 ? km / p.EmptyKmh : 0;

        ShovelTactH = spot + load;
        CycleH = spot + load + full + unload + empty;
        TruckTactH = n > 0 ? CycleH / n : double.PositiveInfinity;

        if (n == 0)
        {
            Notes.Add("这条线**一台车都没配** ⇒ 铲装不出去，出方为 0。这不是预测保守，是编组缺车。");
            Kind = Bottleneck.ShovelWaitsTruck;
            ShovelUtil = 0;
            return this;
        }
        if (km <= 1e-9)
            Notes.Add("运距为 0（台账没填 / 路网没问到）⇒ 行车段时长为 0，周转只剩装卸。"
                    + "预测的趟数会**偏高**，别拿它当计划量。");

        // 谁慢谁定速
        double tact = Math.Max(ShovelTactH, TruckTactH);
        Kind = TruckTactH > ShovelTactH + 1e-9 ? Bottleneck.ShovelWaitsTruck
             : ShovelTactH > TruckTactH + 1e-9 ? Bottleneck.TruckWaitsShovel
             : Bottleneck.None;

        double span = Line.DurationH;
        Trips = tact > 1e-9 ? (int)Math.Floor(span / tact) : 0;

        double waitPerTrip = Math.Max(0, TruckTactH - ShovelTactH);
        double queuePerTrip = Math.Max(0, ShovelTactH - TruckTactH);
        ShovelWaitH = Trips * waitPerTrip;
        TruckQueueH = Trips * queuePerTrip;
        ShovelUtil = span > 1e-9 ? Math.Clamp((span - ShovelWaitH) / span, 0, 1) : 0;

        // ── 逐趟展开（只展前 maxTrips 趟，够看清衔接形态）──
        int show = Math.Min(Trips, Math.Max(1, maxTrips));
        for (int i = 0; i < show; i++)
        {
            double t = Line.StartHour + i * tact;
            string truck = Line.Trucks.Count > 0 ? Line.Trucks[i % Line.Trucks.Count] : "";

            if (waitPerTrip > 1e-9)
            {
                Segments.Add(new CycleSegment
                { Act = CycleAct.ShovelWait, TripIndex = i + 1, StartH = t, EndH = t + waitPerTrip });
                t += waitPerTrip;
            }
            else if (queuePerTrip > 1e-9)
            {
                Segments.Add(new CycleSegment
                { Act = CycleAct.Queue, TripIndex = i + 1, Truck = truck, StartH = t, EndH = t + queuePerTrip });
                t += queuePerTrip;
            }

            Segments.Add(new CycleSegment
            { Act = CycleAct.Spot, TripIndex = i + 1, Truck = truck, StartH = t, EndH = t + spot });
            t += spot;

            Segments.Add(new CycleSegment
            {
                Act = CycleAct.LoadTruck, TripIndex = i + 1, Truck = truck,
                StartH = t, EndH = t + load, VolumeM3 = p.TruckM3,
            });
            t += load;

            if (full > 1e-9)
            {
                Segments.Add(new CycleSegment
                {
                    Act = CycleAct.HaulFull, TripIndex = i + 1, Truck = truck,
                    StartH = t, EndH = t + full, DistanceKm = km, SpeedKmh = p.FullKmh,
                });
                t += full;
            }

            Segments.Add(new CycleSegment
            { Act = CycleAct.Unload, TripIndex = i + 1, Truck = truck, StartH = t, EndH = t + unload });
            t += unload;

            if (empty > 1e-9)
                Segments.Add(new CycleSegment
                {
                    Act = CycleAct.HaulEmpty, TripIndex = i + 1, Truck = truck,
                    StartH = t, EndH = t + empty, DistanceKm = km, SpeedKmh = p.EmptyKmh,
                });
        }
        if (Trips > show)
            Notes.Add($"共预测 {Trips} 趟，图上只展开前 {show} 趟（再多画不下）。"
                    + "趟数 / 出方 / 等待这些**总量是按整班算的**，不受展开条数影响。");

        // 与排产给的班产对一次：两条独立算法算同一件事，差太多必有一边错
        if (Line.GroupCapacityM3PerH > 1e-9 && span > 1e-9)
        {
            double byPlan = Line.GroupCapacityM3PerH * span;
            double diff = PredictedM3 - byPlan;
            if (Math.Abs(diff) / Math.Max(1, byPlan) > 0.25)
                Notes.Add($"⚠ 与排产口径差得多：本模型按节拍预测 {PredictedM3 / 1e4:0.###}万m³，"
                        + $"排产按编组班产给 {byPlan / 1e4:0.###}万m³，差 {diff / 1e4:+0.###;-0.###}万m³"
                        + $"（{diff / Math.Max(1, byPlan):+0%;-0%}）。"
                        + "两条算法各自独立（这边是装车节拍，那边是编组班产台账），"
                        + "差得多说明**至少一边的参数不对** —— 多半是这里的示意参数没接设备台账实测值。");
        }

        return this;
    }

    /// <summary>
    /// 某时刻**在飞的所有段**。
    ///
    /// <para><b>为什么必须是复数</b>：n 台车同时在跑，一个时刻当然有好几段同时在进行 ——
    /// 一台在装、一台在重车行、一台在卸。第一版给的是"返回第一条"，
    /// 判据 P11 立刻抓到：不同趟的段本来就重叠，"第一条"是哪条取决于列表顺序，
    /// 语义根本不成立。多车并行正是配车的意义所在，模型不能把它压成单条。</para>
    /// </summary>
    public IEnumerable<CycleSegment> ActiveAt(double hour)
        => Segments.Where(s => hour >= s.StartH && hour < s.EndH);

    /// <summary>
    /// 某时刻**铲**在做什么。铲一次只能干一件事（等车 / 就位 / 装车），所以这个是单数。
    /// 行车段与卸载段不属于铲 —— 那是车在干。
    /// </summary>
    public CycleSegment? ShovelActAt(double hour)
        => Segments.FirstOrDefault(s => hour >= s.StartH && hour < s.EndH
                                     && s.Act is CycleAct.ShovelWait or CycleAct.Spot or CycleAct.LoadTruck);

    /// <summary>某时刻某台车在做什么（一台车一次也只能干一件事）。</summary>
    public CycleSegment? TruckActAt(string truck, double hour)
        => Segments.FirstOrDefault(s => hour >= s.StartH && hour < s.EndH
                                     && string.Equals(s.Truck, truck, StringComparison.Ordinal));

    /// <summary>某时刻**在途**的车数（重车行 + 空车行 + 排队 + 卸载都算离开了铲）。</summary>
    public int TrucksAwayAt(double hour)
        => Segments.Count(s => hour >= s.StartH && hour < s.EndH
                            && s.Act is CycleAct.HaulFull or CycleAct.HaulEmpty
                                      or CycleAct.Unload or CycleAct.Queue);

    /// <summary>各动作的时长汇总（用于"时间都花在哪儿了"）。</summary>
    public IEnumerable<(CycleAct Act, double Hours, double Share)> Breakdown()
    {
        double total = Segments.Sum(s => s.DurationH);
        if (total < 1e-9) yield break;
        foreach (var g in Segments.GroupBy(s => s.Act).OrderByDescending(g => g.Sum(x => x.DurationH)))
            yield return (g.Key, g.Sum(x => x.DurationH), g.Sum(x => x.DurationH) / total);
    }
}
