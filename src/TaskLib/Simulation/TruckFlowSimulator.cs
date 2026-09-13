// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/TruckFlowSimulator.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  车流离散事件仿真（DE 组，2026-08-20）—— 解析式闭环之后的第二阶段。
//
//  ══ 为什么解析解不够 ══
//  编组那一层给的是**稳态解**：τ_L、T_c、最优配车 n*、匹配系数 MF、Erlang-C 排队概率。
//  它成立的前提是「这条线独立、到达平稳」。而现场是**多条线共用一个卸点、共用一段路**：
//  卸点通过能力不够时，队会**反压**回铲上 —— 铲装完一车没车可装，只能等。
//  这件事解析式算不出来，它只能算出"超了多少"（那正是可行性回填在做的事）。
//  仿真算得出来：**排队时间**、**真实吞吐**、**谁在等谁**。
//
//  ══ 模型（四个事件，一条循环）══
//      装车(铲，单服务台) → 重车行驶 → 卸车(卸点，单服务台) → 空返 → 回到铲前排队
//  · 服务时间**全部取自解析解的分解**，不引入新数字：
//      t_装 = τ_L（编组解出来的装车节拍）
//      t_卸 = 卸点的通过能力换算（AcceptTph → 每车 60·W_t/AcceptTph 分钟）；没录能力就用 t_卸 常数
//      单程行驶 = (T_c − τ_L − t_卸常数 − t_调) / 2
//    ⚠ 这是**同一份 T_c 的分解**，不是另一套参数。两边用两套参数，仿真"验证"解析解就没有意义了。
//  · **确定性**：服务时间取均值、不掷随机数。排队完全来自**资源争用**（多条线抢同一个卸点），
//    这正是要看的那件事；掷随机数会让同一份计划每次跑出不同结论，判据也就钉不住。
//
//  ══ 四条口径 ══
//   DE1 **一班一跑**：班是排产的最小时间盒，跨班的车次不结转（交接班要换人换车）。
//   DE2 **定车制**：这一班跟这台铲的就是那 n 台车，不跨面调 —— 与编组求解同一条口径。
//   DE3 **只统计跑完整趟的车次**：班末没跑完的那一趟不计吨（它没卸下去），
//       但它占用的时间照记 —— 否则利用率会虚高。
//   DE4 **仿真不改计划**：它只出一份"照这个计划跑，实际能跑出多少"的报告。
//       要削量是可行性回填那一步的事（那一步有优先级规则），仿真只负责说实话。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一条线（一台铲 → 一个卸点）在一个班里的仿真结果。</summary>
public sealed class SimLaneResult
{
    public string Shift = "";
    public string Face = "";
    public string ShovelId = "";
    public string SinkName = "";

    /// <summary>这一班跟这台铲的车数。</summary>
    public int Trucks;

    /// <summary>跑完整趟的车次数。</summary>
    public int TripsDone;

    /// <summary>拉下去的吨（只算跑完整趟的）。</summary>
    public double TonnageT;

    /// <summary>铲**等车**的分钟数（运力不足的直接证据）。</summary>
    public double ShovelIdleMin;

    /// <summary>车在**铲前**排队的分钟数合计（采装是瓶颈时它会涨）。</summary>
    public double QueueAtShovelMin;

    /// <summary>车在**卸点**排队的分钟数合计（卸点是瓶颈时它会涨 —— 这就是反压）。</summary>
    public double QueueAtSinkMin;

    /// <summary>铲的利用率（装车时间 ÷ 班有效时长）。</summary>
    public double ShovelUtil;

    /// <summary>实测匹配系数：车队供给 ÷ 铲的需求。</summary>
    public double MatchFactorSim;
}

/// <summary>一次车流仿真的汇总（名字带 TruckFlow 前缀：SimFlowResult 已被物料流那一层占了）。</summary>
public sealed class TruckFlowResult
{
    public List<SimLaneResult> Lanes = new();
    public List<string> Notes = new();

    public double TonnageT => Lanes.Sum(x => x.TonnageT);
    public double QueueAtSinkMin => Lanes.Sum(x => x.QueueAtSinkMin);
    public double ShovelIdleMin => Lanes.Sum(x => x.ShovelIdleMin);

    /// <summary>解析侧承运吨（同一盘计划）。</summary>
    public double AnalyticTonnageT;

    /// <summary>仿真 ÷ 解析。&lt;1 = 现场跑不出计划的量。</summary>
    public double Attainment => AnalyticTonnageT > 1e-6 ? TonnageT / AnalyticTonnageT : 0;
}

/// <summary>车流离散事件仿真。纯计算，不碰库、不掷随机数、不改计划。永不抛。</summary>
public static class TruckFlowSimulator
{
    /// <summary>卸载时间常数 t_卸（min）—— 与 <c>FleetMatcher</c> 同一个数。</summary>
    private const double DumpMin = 1.5;

    /// <summary>调车对位 t_调（min）—— 与 <c>FleetMatcher</c> 同一个数。</summary>
    private const double SpotMin = 1.0;

    /// <summary>
    /// 按当日盘子跑一遍车流。
    /// </summary>
    /// <param name="tasks">当日任务（读**运输**笔：它带着源采装笔、去向、吨量）。</param>
    /// <param name="cfg">盘子（要面上的编组解与班次时窗、去向的通过能力）。</param>
    public static TruckFlowResult Run(IReadOnlyList<ProductionTask>? tasks, ExploderConfig? cfg)
    {
        var res = new TruckFlowResult();
        var hauls = (tasks ?? Array.Empty<ProductionTask>())
                    .Where(t => t != null && t.Process == ProcessType.Haul && t.HaulTonnageT > 1e-6).ToList();
        if (hauls.Count == 0 || cfg == null)
        {
            res.Notes.Add("· 本盘没有运输笔 —— 车流没得跑（多半是去向未定：没有去向就算不出运距与配车）。");
            return res;
        }
        res.AnalyticTonnageT = hauls.Sum(t => t.HaulTonnageT);

        var faceOf = new Dictionary<string, FaceInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in cfg.Faces ?? new List<FaceInput>())
            if (f != null && f.Process == ProcessType.Load && !string.IsNullOrWhiteSpace(f.Zone))
                faceOf[f.Zone.Trim()] = f;      // 采装面与排土面会重名，只取采装面

        var sinkOf = new Dictionary<string, SinkNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var sk in cfg.Sinks?.All ?? (IReadOnlyCollection<SinkNode>)Array.Empty<SinkNode>())
            if (sk != null && !string.IsNullOrWhiteSpace(sk.Name)) sinkOf[sk.Name.Trim()] = sk;

        // 一个班一跑（DE1）
        foreach (var byShift in hauls.GroupBy(t => (t.Shift ?? "").Trim()))
        {
            var win = cfg.Shifts?.FirstOrDefault(w => string.Equals(w.Name, byShift.Key, StringComparison.Ordinal));
            double shiftMin = win != null ? Math.Max(0, (win.End - win.Start) * 60) : 8 * 60;
            if (shiftMin <= 1e-6) continue;

            RunShift(byShift.Key, byShift.ToList(), faceOf, sinkOf, shiftMin, res);
        }

        Summarize(res);
        return res;
    }

    // ── 一个班 ────────────────────────────────────────────────────────

    private sealed class Lane
    {
        public SimLaneResult Out = new();
        public double LoadMin, TravelLoadedMin, TravelEmptyMin, PayloadT;
        public double ShovelFreeAt;                 // 铲什么时候空出来
        public double[] TruckFreeAt = Array.Empty<double>();  // 每台车什么时候回到铲前
        public string SinkKey = "";
        public double RemainTonnage;                // 这一班还剩多少吨要拉（拉完就停）
    }

    private static void RunShift(string shift, List<ProductionTask> hauls,
                                 Dictionary<string, FaceInput> faceOf,
                                 Dictionary<string, SinkNode> sinkOf,
                                 double shiftMin, TruckFlowResult res)
    {
        var lanes = new List<Lane>();
        foreach (var h in hauls)
        {
            faceOf.TryGetValue((h.WorkZone ?? "").Trim(), out var face);
            var lane = BuildLane(shift, h, face, shiftMin);
            if (lane != null) lanes.Add(lane);
        }
        if (lanes.Count == 0) return;

        // 卸点：一个服务台，服务时间由通过能力换算（没录能力就用 t_卸 常数）
        var sinkFreeAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sinkService = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // ★ **通过能力没录的卸点不当瓶颈**（DE7）：
        //   把它按"单卸点、每车占 t_卸"来排队，51 条线抢一个服务台会排出上千小时的队 ——
        //   那是模型造出来的，不是现场的（一个排土场有好几个卸点，能力是台账里的一列，现在没人录）。
        //   判不了就不判：不知道能力时这个卸点不构成约束，并在结论里说明这一条没算。
        var uncapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in lanes)
        {
            if (sinkService.ContainsKey(lane.SinkKey) || uncapped.Contains(lane.SinkKey)) continue;
            if (sinkOf.TryGetValue(lane.SinkKey, out var sk) && sk.AcceptTph > 1e-6 && lane.PayloadT > 1e-6)
            {
                sinkService[lane.SinkKey] = Math.Max(DumpMin, 60.0 * lane.PayloadT / sk.AcceptTph);
                sinkFreeAt[lane.SinkKey] = 0;
            }
            else uncapped.Add(lane.SinkKey);
        }
        // 一班一跑 ⇒ 同一句提示会重复三遍，去重（同一件事说三次，读的人会以为是三个场）
        if (uncapped.Count > 0 && !res.Notes.Any(x => x.Contains("没录通过能力")))
            res.Notes.Add($"· {uncapped.Count} 个去向**没录通过能力**（{string.Join("、", uncapped.Take(3))}）—— "
                        + "它们这一轮**不当瓶颈算**（不知道能力就算不出排队；按「单卸点」硬算会排出上千小时的假队）。"
                        + "要把卸点算进来，去「去向台账」补上通过能力那一列。");

        // ── 事件推进：每次挑"下一个能开始装车的车"，一趟一趟往前滚 ──
        //   单服务台的铲 + 单服务台的卸点，服务时间确定 ⇒ 事件序列唯一（DE：确定性）
        while (true)
        {
            Lane? pick = null;
            int pickTruck = -1;
            double startLoad = double.MaxValue;

            foreach (var lane in lanes)
            {
                if (lane.RemainTonnage <= 1e-6) continue;
                for (int i = 0; i < lane.TruckFreeAt.Length; i++)
                {
                    double t = Math.Max(lane.TruckFreeAt[i], lane.ShovelFreeAt);
                    if (t < startLoad) { startLoad = t; pick = lane; pickTruck = i; }
                }
            }
            if (pick == null || startLoad >= shiftMin) break;

            // 铲等车：铲空着等这台车到（供给不足的直接证据）
            if (pick.TruckFreeAt[pickTruck] > pick.ShovelFreeAt)
                pick.Out.ShovelIdleMin += pick.TruckFreeAt[pickTruck] - pick.ShovelFreeAt;
            else
                pick.Out.QueueAtShovelMin += pick.ShovelFreeAt - pick.TruckFreeAt[pickTruck];

            double loadDone = startLoad + pick.LoadMin;
            pick.ShovelFreeAt = loadDone;

            double arrive = loadDone + pick.TravelLoadedMin;
            double unloadDone;
            if (sinkService.TryGetValue(pick.SinkKey, out double svc))
            {
                double startUnload = Math.Max(arrive, sinkFreeAt[pick.SinkKey]);
                pick.Out.QueueAtSinkMin += startUnload - arrive;         // 反压就记在这里
                unloadDone = startUnload + svc;
                sinkFreeAt[pick.SinkKey] = unloadDone;
            }
            else
            {
                unloadDone = arrive + DumpMin;                            // 能力没录：卸车照花时间，但不排队
            }

            double backAtShovel = unloadDone + pick.TravelEmptyMin + SpotMin;
            pick.TruckFreeAt[pickTruck] = backAtShovel;

            // DE3：卸完了才算数；班末没卸下去的那一趟不计吨
            if (unloadDone <= shiftMin)
            {
                pick.Out.TripsDone++;
                double t = Math.Min(pick.PayloadT, pick.RemainTonnage);
                pick.Out.TonnageT += t;
                pick.RemainTonnage -= t;
            }
            else
            {
                pick.RemainTonnage = 0;      // 这一班到点了，这条线收工
            }
        }

        foreach (var lane in lanes)
        {
            double busy = lane.Out.TripsDone * lane.LoadMin;
            lane.Out.ShovelUtil = shiftMin > 1e-6 ? Math.Round(Math.Min(1, busy / shiftMin), 3) : 0;
            // 实测匹配系数：n·τ_L / T_c（与解析同一个定义，只是用仿真跑出来的量回算）
            double tc = lane.LoadMin + lane.TravelLoadedMin + DumpMin + lane.TravelEmptyMin + SpotMin;
            lane.Out.MatchFactorSim = tc > 1e-6 ? Math.Round(lane.Out.Trucks * lane.LoadMin / tc, 2) : 0;
            res.Lanes.Add(lane.Out);
        }
    }

    private static Lane? BuildLane(string shift, ProductionTask h, FaceInput? face, double shiftMin)
    {
        double payload = face?.Group.TruckPayloadT ?? 0;
        int trucks = face?.Group.Trucks.Count > 0 ? face.Group.Trucks.Count
                   : Math.Max(0, face?.Group.RecommendedTrucks ?? 0);
        double takt = face?.Group.LoadTaktMin ?? 0;
        double tc = face?.Group.CycleTimeMin ?? 0;

        // 四样缺一样就跑不了 —— **不猜**：猜出来的排队时间比没有更坏
        if (payload <= 1e-6 || trucks <= 0 || takt <= 1e-6 || tc <= takt + DumpMin) return null;

        // 单程行驶 = (T_c − t_装 − t_卸 − t_调) / 2 —— 同一份 T_c 的分解，不引入新数字
        double travel = Math.Max(0, tc - takt - DumpMin - SpotMin) / 2.0;

        return new Lane
        {
            Out = new SimLaneResult
            {
                Shift = shift,
                Face = h.WorkZone ?? "",
                ShovelId = face?.Group.MainEquipment ?? "",
                SinkName = (h.DestinationName ?? "").Trim(),
                Trucks = trucks,
            },
            LoadMin = takt,
            TravelLoadedMin = travel,
            TravelEmptyMin = travel,
            PayloadT = payload,
            TruckFreeAt = new double[trucks],       // 班初都在铲前
            ShovelFreeAt = 0,
            SinkKey = (h.DestinationName ?? "").Trim(),
            RemainTonnage = h.HaulTonnageT,
        };
    }

    private static void Summarize(TruckFlowResult res)
    {
        if (res.Lanes.Count == 0)
        {
            res.Notes.Add("◆ 一条线都跑不起来 —— 编组没解出载重/节拍/循环时间，或这一班没有配车。"
                        + "缺一样就不跑：猜出来的排队时间比没有更坏。");
            return;
        }

        res.Notes.Add($"· 仿真跑了 {res.Lanes.Count} 条线：拉下去 {res.TonnageT / 1e4:0.##} 万t"
                    + $"（解析 {res.AnalyticTonnageT / 1e4:0.##} 万t，达成 {res.Attainment * 100:0.#}%）"
                    + $"　铲等车合计 {res.ShovelIdleMin / 60:0.#} h　卸点排队合计 {res.QueueAtSinkMin / 60:0.#} h");

        if (res.Attainment < 0.98 && res.AnalyticTonnageT > 1e-6)
            res.Notes.Add($"◆ **仿真跑不出计划的量**（{res.Attainment * 100:0.#}%）—— "
                        + "解析解按稳态公式算，没算多条线抢同一个卸点。"
                        + "看下面哪一项大：铲等车 = 车不够；卸点排队 = 卸点不够（反压回铲上）。");

        var worstSink = res.Lanes.GroupBy(x => x.SinkName)
                        .Select(g => (Sink: g.Key, Q: g.Sum(x => x.QueueAtSinkMin)))
                        .OrderByDescending(x => x.Q).FirstOrDefault();
        if (worstSink.Q > 60)
            res.Notes.Add($"◆ **{worstSink.Sink} 卸点排队 {worstSink.Q / 60:0.#} h** —— "
                        + "队会反压回铲上：铲装完一车没车可装只能等。"
                        + "补法：加卸点/延长开放时窗/把一部分量分流到别的场（可行性回填那一步会按优先级削）。");

        var starved = res.Lanes.Where(x => x.ShovelIdleMin > 60).OrderByDescending(x => x.ShovelIdleMin).Take(3).ToList();
        foreach (var s in starved)
            res.Notes.Add($"· {s.ShovelId} @ {s.Face} 等车 {s.ShovelIdleMin / 60:0.#} h"
                        + $"（配车 {s.Trucks} 台，实测匹配系数 {s.MatchFactorSim:0.00}）—— "
                        + "匹配系数 <1 就是运力不足，加车或缩运距。");
    }
}
