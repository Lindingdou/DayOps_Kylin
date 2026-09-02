using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

// ─────────────────────────────────────────────────────────────────────────────
// 运输指标(TransportIndicatorsBuilder)——忠实移植原 RoadLib.Routing.TransportIndicators。
// Kylin 既有「路网运输指标」命令(RoadTransportIndicatorsCmd)只做几何部分(总里程/可达对/瓶颈);
// 此为全指标:节点类型自动源汇解析 + OD 等效运距均值/最大 + 理论运能(Σ源/Σ汇吞吐取 min) +
// 瓶颈段(介数×车道×陡坡评分) + 分期序列。纯托管、可单测; 复用 §290 RoadGraph/DijkstraPathSolver + HaulMetrics。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>瓶颈段一行。score = 介数 × 车道因子 × 陡坡因子; 禁行边单列不计分。忠实原 BottleneckEdge。</summary>
public sealed record BottleneckEdge(
    string EdgeId,
    double LengthM,
    double GradePct,
    int LaneCount,
    int Betweenness,
    string Status,
    double Score,
    string Reason);

/// <summary>分期序列一项(每期标量; 趋势图源)。忠实原 PeriodPoint。</summary>
public sealed record PeriodPoint(
    int Period,
    double TotalKm,
    int EdgeCount,
    double AvgEquivM,
    double TheoreticalCapacityTph);

/// <summary>W6 运输指标 DTO(纯数据)。忠实原 RoadLib.Routing.TransportIndicators。</summary>
public sealed class TransportIndicators
{
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    /// <summary>路网总里程 km。</summary>
    public double TotalKm { get; init; }
    public bool IsFullyConnected { get; init; }
    public int ComponentCount { get; init; }

    /// <summary>源节点 Id(Loading; 降级时=全节点截断)。</summary>
    public IReadOnlyList<string> Sources { get; init; } = new List<string>();
    /// <summary>汇节点 Id(Unloading; 降级时=全节点截断)。</summary>
    public IReadOnlyList<string> Sinks { get; init; } = new List<string>();
    /// <summary>装卸点任一为空 → 降级为全节点 OD(前 40 截断), 此标志为 true。</summary>
    public bool UsedAllNodesFallback { get; init; }

    /// <summary>OD 矩阵(源×汇, 含 Dist/Equiv/Time)。</summary>
    public ODMatrix? Od { get; init; }

    /// <summary>可达对等效运距均值 m。</summary>
    public double AvgEquivM { get; init; }
    /// <summary>可达对最大等效运距 m。</summary>
    public double MaxEquivM { get; init; }
    /// <summary>运量加权平均等效运距 m。null = 待采矿模型(吨量权未就绪)。</summary>
    public double? WeightedAvgEquivM { get; init; }

    /// <summary>Σ 源 ThroughputTph。</summary>
    public double SourceThroughputSumTph { get; init; }
    /// <summary>Σ 汇 ThroughputTph。</summary>
    public double SinkCapacitySumTph { get; init; }
    /// <summary>理论运能上界 = min(源和, 汇和)。非网络流解, 待求解器。</summary>
    public double TheoreticalCapacityTph { get; init; }

    public IReadOnlyList<BottleneckEdge> Bottlenecks { get; init; } = new List<BottleneckEdge>();
    public IReadOnlyList<PeriodPoint> PerPeriodSeries { get; init; } = new List<PeriodPoint>();
}

/// <summary>
/// 运输指标计算器(静态、可单测、零 UI 依赖)。忠实移植原 TransportIndicatorsBuilder。
/// 源汇=图中 Loading/Unloading 节点; 任一为空则降级为全节点(前 40 截断)。复用 DijkstraPathSolver/ODMatrix/HaulMetrics。
/// </summary>
public static class TransportIndicatorsBuilder
{
    private const int Cap = 40;              // 节点过多截断上限
    private const int BottleneckTopN = 10;   // 瓶颈段取前 N
    private const double GradeFreePct = 6.0; // 陡坡判据起算纵坡 %

    /// <summary>汇总会话路网 + 各期快照成运输指标 DTO。</summary>
    public static TransportIndicators Compute(RoadGraph graph, IReadOnlyList<RoadGraph> snapshots, TruckProfile truck)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        truck ??= TruckProfile.Default;

        var report = graph.Validate();
        double totalKm = TotalKm(graph);

        var (sources, sinks, fallback) = ResolveSourcesSinks(graph);

        var solver = new DijkstraPathSolver(graph);
        var query = new PathQuery { Mode = WeightMode.Distance, Truck = truck };
        ODMatrix? od = (sources.Count > 0 && sinks.Count > 0)
            ? solver.BuildMatrix(sources, sinks, query)
            : null;

        double avgEquiv = 0, maxEquiv = 0;
        if (od is not null)
        {
            double sum = 0; int pairs = 0;
            var reachable = new List<double>();
            for (int i = 0; i < sources.Count; i++)
                for (int j = 0; j < sinks.Count; j++)
                {
                    if (sources[i] == sinks[j]) continue;
                    double e = od.Equiv[i, j];
                    if (double.IsInfinity(e)) continue;
                    sum += e; pairs++;
                    reachable.Add(e);
                }
            avgEquiv = pairs > 0 ? sum / pairs : 0.0;
            maxEquiv = HaulMetrics.MaxHaulM(reachable);
        }

        double srcTph = SumThroughput(graph, sources);
        double sinkTph = SumThroughput(graph, sinks);
        double theoTph = Math.Min(srcTph, sinkTph);

        var bottlenecks = ComputeBottlenecks(graph, solver, query, sources, sinks);
        var series = ComputePerPeriodSeries(snapshots, truck);

        return new TransportIndicators
        {
            NodeCount = graph.NodeCount,
            EdgeCount = graph.EdgeCount,
            TotalKm = totalKm,
            IsFullyConnected = report.IsFullyConnected,
            ComponentCount = report.ComponentCount,
            Sources = sources,
            Sinks = sinks,
            UsedAllNodesFallback = fallback,
            Od = od,
            AvgEquivM = avgEquiv,
            MaxEquivM = maxEquiv,
            WeightedAvgEquivM = null,    // 留桩:吨量权来自采矿模型/装卸点产量, 本轮不算
            SourceThroughputSumTph = srcTph,
            SinkCapacitySumTph = sinkTph,
            TheoreticalCapacityTph = theoTph,
            Bottlenecks = bottlenecks,
            PerPeriodSeries = series,
        };
    }

    /// <summary>源汇判定:Loading×Unloading; 任一空 → 降级全节点(前 40 截断), 返回 fallback=true。</summary>
    private static (List<string> Sources, List<string> Sinks, bool Fallback) ResolveSourcesSinks(RoadGraph graph)
    {
        var loading = new List<string>();
        var unloading = new List<string>();
        foreach (var n in graph.Nodes)
        {
            if (n.Type == RoadNodeType.Loading) loading.Add(n.Id);
            else if (n.Type == RoadNodeType.Unloading) unloading.Add(n.Id);
        }
        if (loading.Count > 0 && unloading.Count > 0)
            return (loading, unloading, false);

        var all = new List<string>();
        foreach (var n in graph.Nodes) all.Add(n.Id);
        if (all.Count > Cap) all = all.GetRange(0, Cap);
        return (all, new List<string>(all), true);
    }

    /// <summary>瓶颈段:对所有源×汇最短路重建路径累计每边介数 → score 降序前 N; 禁行边单列不计分。</summary>
    private static List<BottleneckEdge> ComputeBottlenecks(
        RoadGraph graph, DijkstraPathSolver solver, PathQuery query,
        IReadOnlyList<string> sources, IReadOnlyList<string> sinks)
    {
        var betweenness = new Dictionary<string, int>();
        for (int i = 0; i < sources.Count; i++)
            for (int j = 0; j < sinks.Count; j++)
            {
                if (sources[i] == sinks[j]) continue;
                var path = solver.FindPath(sources[i], sinks[j], query);
                if (!path.Feasible) continue;
                foreach (var eid in path.EdgeIds)
                    betweenness[eid] = betweenness.GetValueOrDefault(eid) + 1;
            }

        var scored = new List<BottleneckEdge>();   // 计分边
        var banned = new List<BottleneckEdge>();    // 禁行边(单列, 不计分)
        foreach (var e in graph.Edges)
        {
            int bw = betweenness.GetValueOrDefault(e.Id);
            string status = StatusText(e.Status);
            if (e.Status != RoadEdgeStatus.Open)
            {
                banned.Add(new BottleneckEdge(e.Id, e.LengthM, e.GradePct, e.LaneCount, bw, status, 0.0, "已禁行"));
                continue;
            }
            double laneFactor = 1.0 / Math.Max(1, e.LaneCount);
            double gradeFactor = 1.0 + Math.Max(0.0, Math.Abs(e.GradePct) - GradeFreePct) / 10.0;
            double score = bw * laneFactor * gradeFactor;
            scored.Add(new BottleneckEdge(e.Id, e.LengthM, e.GradePct, e.LaneCount, bw, status, score,
                ReasonOf(e.LaneCount, e.GradePct, bw)));
        }

        var top = scored.OrderByDescending(b => b.Score).Take(BottleneckTopN).ToList();
        top.AddRange(banned);
        return top;
    }

    /// <summary>主因判定(单车道 | 陡坡 | 高介数)。</summary>
    private static string ReasonOf(int laneCount, double gradePct, int betweenness)
    {
        if (laneCount <= 1) return "单车道";
        if (Math.Abs(gradePct) > GradeFreePct) return "陡坡";
        return betweenness > 0 ? "高介数" : "—";
    }

    /// <summary>分期序列:每个 snapshot 算标量(TotalKm/EdgeCount/AvgEquivM/TheoreticalCapacityTph)。</summary>
    private static List<PeriodPoint> ComputePerPeriodSeries(IReadOnlyList<RoadGraph> snapshots, TruckProfile truck)
    {
        var series = new List<PeriodPoint>();
        if (snapshots is null) return series;
        for (int i = 0; i < snapshots.Count; i++)
        {
            var g = snapshots[i];
            if (g is null) continue;

            var (sources, sinks, _) = ResolveSourcesSinks(g);
            double avgEquiv = 0;
            if (sources.Count > 0 && sinks.Count > 0)
            {
                var od = new DijkstraPathSolver(g).BuildMatrix(sources, sinks,
                    new PathQuery { Mode = WeightMode.Distance, Truck = truck });
                double sum = 0; int pairs = 0;
                for (int a = 0; a < sources.Count; a++)
                    for (int b = 0; b < sinks.Count; b++)
                    {
                        if (sources[a] == sinks[b]) continue;
                        double e = od.Equiv[a, b];
                        if (double.IsInfinity(e)) continue;
                        sum += e; pairs++;
                    }
                avgEquiv = pairs > 0 ? sum / pairs : 0.0;
            }

            double srcTph = SumThroughput(g, sources);
            double sinkTph = SumThroughput(g, sinks);
            double theoTph = Math.Min(srcTph, sinkTph);

            series.Add(new PeriodPoint(i + 1, TotalKm(g), g.EdgeCount, avgEquiv, theoTph));
        }
        return series;
    }

    private static double SumThroughput(RoadGraph graph, IReadOnlyList<string> ids)
    {
        double sum = 0;
        foreach (var id in ids)
        {
            var n = graph.GetNode(id);
            if (n is not null) sum += n.ThroughputTph;
        }
        return sum;
    }

    private static double TotalKm(RoadGraph g)
    {
        double m = 0;
        foreach (var e in g.Edges) m += e.LengthM;
        return m / 1000.0;
    }

    private static string StatusText(RoadEdgeStatus s) => s switch
    {
        RoadEdgeStatus.Open => "开放",
        RoadEdgeStatus.Maintenance => "检修",
        _ => "关闭",
    };
}
