// 忠实移植自原 PitMine3D Modules/RoadLib/Routing/TransportIndicators.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// W6 运输指标 DTO（纯数据，零 UI 依赖）：把会话路网 + 各期快照一次性汇总成「几何类运输指标」。
/// 由 <see cref="TransportIndicatorsBuilder.Compute"/> 算出，供报表窗展示/导出，及第八条生产计划只读查询。
/// 凡需吨量/真网络流/经济单价的指标（加权平均运距的权、真运能、成本、压矿）均留桩标「待采矿模型/求解器」，不假装算出。
/// </summary>
public sealed class TransportIndicators
{
    // ── 概要（来自 graph + Validate）──
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    /// <summary>路网总里程 km。</summary>
    public double TotalKm { get; init; }
    public bool IsFullyConnected { get; init; }
    public int ComponentCount { get; init; }

    // ── 口径（每个数字都得能被追问"这是按什么算的"）──
    /// <summary>本次计算的口径摘要（一行，打在报表横幅上；含 hash，可比对两次是不是同一口径）。</summary>
    public string CaliperSummary { get; init; } = "";
    /// <summary>择路口径中文名（"平均等效运距是沿哪种路测的"这句话的主语）。</summary>
    public string ModeText { get; init; } = "";
    /// <summary>口径旁注（派生值 / 缺省 / 越界警告）。</summary>
    public IReadOnlyList<string> CaliperNotes { get; init; } = new List<string>();

    // ── 源 / 汇 ──
    /// <summary>源节点 Id（Loading；降级时=全节点截断）。</summary>
    public IReadOnlyList<string> Sources { get; init; } = new List<string>();
    /// <summary>汇节点 Id（Unloading；降级时=全节点截断）。</summary>
    public IReadOnlyList<string> Sinks { get; init; } = new List<string>();
    /// <summary>装卸点任一为空 → 降级为全节点 OD（前 40 截断），此标志为 true（窗内黄字提示）。</summary>
    public bool UsedAllNodesFallback { get; init; }
    /// <summary>发生规模截断时的说明（"已截断 78→60，缺 18 个源"）；无截断为 null。静默截断会让人以为算全了。</summary>
    public string? TruncationNote { get; init; }

    // ── OD 矩阵（源×汇，含 Dist/Equiv/Time 三口径，三视图共用一张）──
    public ODMatrix? Od { get; init; }

    // ── 运距指标（基于 OD Equiv 的可达对）──
    /// <summary>可达对等效运距均值 m。</summary>
    public double AvgEquivM { get; init; }
    /// <summary>可达对最大等效运距 m。</summary>
    public double MaxEquivM { get; init; }
    /// <summary>运量加权平均等效运距 m。null = 待采矿模型（吨量权未就绪），接口已就绪仅缺权。</summary>
    public double? WeightedAvgEquivM { get; init; }
    /// <summary>
    /// 按**源吞吐能力**加权的平均等效运距 m（权 = 源节点 <c>ThroughputTph</c>，真数据、现在就有）。
    /// 它不是运量加权（那要吨量，待采矿模型），但比算术平均接近实际：产量大的采区拉长了平均运距。
    /// 所有源吞吐均为 0 时为 null。
    /// </summary>
    public double? ThroughputWeightedAvgEquivM { get; init; }

    // ── 路网理论运能（无网络流，给上界 + 诚实标注）──
    /// <summary>Σ 源 ThroughputTph。</summary>
    public double SourceThroughputSumTph { get; init; }
    /// <summary>Σ 汇 ThroughputTph（卸载侧接收能力，v1 用 ThroughputTph）。</summary>
    public double SinkCapacitySumTph { get; init; }
    /// <summary>理论运能上界 = min(源和, 汇和)。非网络流解，待求解器。</summary>
    public double TheoreticalCapacityTph { get; init; }

    // ── 瓶颈段（几何 + 拓扑代理，无吨量）──
    public IReadOnlyList<BottleneckEdge> Bottlenecks { get; init; } = new List<BottleneckEdge>();

    // ── 分期序列（每期一项，趋势图源；只算标量不存矩阵）──
    public IReadOnlyList<PeriodPoint> PerPeriodSeries { get; init; } = new List<PeriodPoint>();
}

/// <summary>瓶颈段一行（设计 §1.3.5）。score = 介数 × 车道因子 × 陡坡因子；禁行边单列不计分。</summary>
public sealed record BottleneckEdge(
    string EdgeId,
    double LengthM,
    double GradePct,
    /// <summary>最大分段绝对纵坡 %（陡坡判据用它，不用整段平均）。</summary>
    double MaxAbsSegGradePct,
    int LaneCount,
    /// <summary>被所有源×汇最短路经过次数（介数代理）。</summary>
    int Betweenness,
    /// <summary>状态文本（开放/检修/关闭）。</summary>
    string Status,
    /// <summary>瓶颈分（禁行边为 0，单列）。</summary>
    double Score,
    /// <summary>主因（单车道 | 陡坡 | 高介数 | 已禁行）。</summary>
    string Reason);

/// <summary>分期序列一项（每期标量；趋势图双 Y 轴源）。</summary>
public sealed record PeriodPoint(
    int Period,
    double TotalKm,
    int EdgeCount,
    double AvgEquivM,
    double TheoreticalCapacityTph);

/// <summary>
/// W6 运输指标计算器（静态、可单测、零 UI 依赖）。
/// 源汇=图中 Loading/Unloading 节点；任一为空则降级为全节点（前 40 截断）。
/// 复用 DijkstraPathSolver / ODMatrix / HaulMetrics，节点过多按 40 截断。
/// </summary>
public static class TransportIndicatorsBuilder
{
    /// <summary>降级为全节点时的节点数上限。</summary>
    private const int Cap = 40;

    /// <summary>源×汇对数上限：超了按 Id Ordinal 序截断并**写明截断量**（不静默）。</summary>
    private const int MaxPairs = 2000;

    /// <summary>瓶颈段取前 N。</summary>
    private const int BottleneckTopN = 10;

    /// <summary>汇总会话路网 + 各期快照成运输指标 DTO。</summary>
    /// <param name="graph">会话路网图（必填）。</param>
    /// <param name="snapshots">各期快照（可空/可少于 2 期，趋势页据此降级）。</param>
    /// <param name="caliper">
    /// 口径（择路权重 / 限坡 / 车型），来自共享运输约束。
    /// 原先这里硬编码 <c>WeightMode.Distance</c> + <see cref="TruckProfile.Default"/> 且不给限坡，
    /// 与另外两颗钮各说各话 —— 同一张图算出来的"平均运距"和用户在寻径窗里读到的对不上。
    /// </param>
    public static TransportIndicators Compute(RoadGraph graph, IReadOnlyList<RoadGraph> snapshots, HaulCaliper caliper)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        caliper ??= HaulCaliper.From(null);

        // 概要（校验的限坡口径与寻径同源，否则"校验说合规、寻径说走不通"）
        var report = graph.Validate(caliper.MaxGradePct > 0 ? caliper.MaxGradePct : 10.0);
        double totalKm = TotalKm(graph);

        // 源 / 汇：优先 Loading×Unloading；任一为空降级为全节点（Ordinal 序前 40）
        var (sources, sinks, fallback, truncNote) = ResolveSourcesSinks(graph);

        // OD：一次 BuildMatrix 出 Dist/Equiv/Time，口径 = 横幅所示（默认时间最短）
        var solver = new DijkstraPathSolver(graph);
        var query = caliper.Query(loaded: true);
        ODMatrix? od = (sources.Count > 0 && sinks.Count > 0)
            ? solver.BuildMatrix(sources, sinks, query)
            : null;

        // 运距指标：基于 OD Equiv 的可达对（跳过 ∞，跳过自身对）
        double avgEquiv = 0, maxEquiv = 0;
        double? tphWeighted = null;
        if (od is not null)
        {
            double sum = 0; int pairs = 0;
            var reachable = new List<double>();
            double wNum = 0, wDen = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                double srcTphI = graph.GetNode(sources[i])?.ThroughputTph ?? 0.0;
                for (int j = 0; j < sinks.Count; j++)
                {
                    if (sources[i] == sinks[j]) continue;          // 同节点（全节点降级时源汇同集合）
                    double e = od.Equiv[i, j];
                    if (double.IsInfinity(e)) continue;
                    sum += e; pairs++;
                    reachable.Add(e);
                    wNum += e * srcTphI; wDen += srcTphI;
                }
            }
            avgEquiv = pairs > 0 ? sum / pairs : 0.0;
            maxEquiv = HaulMetrics.MaxHaulM(reachable);
            tphWeighted = wDen > 1e-9 ? wNum / wDen : null;        // 全 0 吞吐 → null，不退化成算术平均冒充"加权"
        }

        // 理论运能：Σ源 / Σ汇 的 ThroughputTph，取 min（非网络流，待求解器）
        double srcTph = SumThroughput(graph, sources);
        double sinkTph = SumThroughput(graph, sinks);
        double theoTph = Math.Min(srcTph, sinkTph);

        // 瓶颈段：源×汇最短路累计每边介数，按 score 降序前 N（同分按 EdgeId Ordinal 兜底）
        var bottlenecks = ComputeBottlenecks(graph, solver, query, sources, sinks, caliper);

        // 分期序列：每个 snapshot 算标量（不存矩阵）
        var series = ComputePerPeriodSeries(snapshots, caliper);

        return new TransportIndicators
        {
            NodeCount = graph.NodeCount,
            EdgeCount = graph.EdgeCount,
            TotalKm = totalKm,
            IsFullyConnected = report.IsFullyConnected,
            ComponentCount = report.ComponentCount,
            CaliperSummary = caliper.SummaryLine(),
            ModeText = caliper.ModeText,
            CaliperNotes = caliper.Notes,
            Sources = sources,
            Sinks = sinks,
            UsedAllNodesFallback = fallback,
            TruncationNote = truncNote,
            Od = od,
            AvgEquivM = avgEquiv,
            MaxEquivM = maxEquiv,
            WeightedAvgEquivM = null,    // 留桩：吨量权来自采矿模型/装卸点产量，本轮不算
            ThroughputWeightedAvgEquivM = tphWeighted,
            SourceThroughputSumTph = srcTph,
            SinkCapacitySumTph = sinkTph,
            TheoreticalCapacityTph = theoTph,
            Bottlenecks = bottlenecks,
            PerPeriodSeries = series,
        };
    }

    /// <summary>
    /// 源汇判定：Loading×Unloading；任一空 → 降级全节点（<c>Ordinal</c> 序前 40）。
    /// **排序在截断之前**：不排序的话截到的子集取决于 Dictionary 枚举序，同一张图两次算出不同的表。
    /// 对数超上限时同样按 Ordinal 序截断，并回一句说明 —— 静默截断会被读成"算全了"。
    /// </summary>
    private static (List<string> Sources, List<string> Sinks, bool Fallback, string? TruncNote) ResolveSourcesSinks(RoadGraph graph)
    {
        var loading = new List<string>();
        var unloading = new List<string>();
        foreach (var n in graph.Nodes)
        {
            if (n.Type == RoadNodeType.Loading) loading.Add(n.Id);
            else if (n.Type == RoadNodeType.Unloading) unloading.Add(n.Id);
        }
        loading.Sort(StringComparer.Ordinal);
        unloading.Sort(StringComparer.Ordinal);

        bool fallback = loading.Count == 0 || unloading.Count == 0;
        if (fallback)
        {
            var all = graph.Nodes.Select(n => n.Id).OrderBy(s => s, StringComparer.Ordinal).ToList();
            int total = all.Count;
            if (all.Count > Cap) all = all.GetRange(0, Cap);
            string? note = total > Cap ? $"未设装卸点，降级为全节点 OD 并按 Id 序截断：{total} → {Cap} 个节点（缺 {total - Cap} 个）" : null;
            return (all, new List<string>(all), true, note);
        }

        // 规模保护：对数超上限时砍源侧（汇通常远少于源），并写明砍了多少。
        long pairs = (long)loading.Count * unloading.Count;
        if (pairs > MaxPairs)
        {
            int keep = Math.Max(1, MaxPairs / Math.Max(1, unloading.Count));
            if (keep < loading.Count)
            {
                int total = loading.Count;
                loading = loading.GetRange(0, keep);
                return (loading, unloading, false,
                    $"源×汇 {total}×{unloading.Count} = {pairs} 对超上限 {MaxPairs}，已按 Id 序截断源：{total} → {keep}（缺 {total - keep} 个源）");
            }
        }
        return (loading, unloading, false, null);
    }

    /// <summary>
    /// 瓶颈段：对所有源×汇最短路重建路径累计每边介数 → score 降序前 N；禁行边单列不计分。
    /// 陡坡判据读口径的限坡（原先硬编码 6%，与约束的 8% 各说各话 —— 同一条 7% 的边
    /// "寻径说合规、瓶颈表说陡坡"），并且判的是分段最大坡而不是整段平均。
    /// </summary>
    private static List<BottleneckEdge> ComputeBottlenecks(
        RoadGraph graph, DijkstraPathSolver solver, PathQuery query,
        IReadOnlyList<string> sources, IReadOnlyList<string> sinks, HaulCaliper caliper)
    {
        // 累计介数：每条边被源×汇最短路经过的次数
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

        double gradeFree = caliper.MaxGradePct > 0 ? caliper.MaxGradePct : 6.0;

        // 留一法要的全网中位数（车道 / 陡坡因子 / 介数各一条）
        var open = graph.Edges.Where(e => e.Status == RoadEdgeStatus.Open).ToList();
        double medLane = Median(open.Select(e => LaneFactor(e.LaneCount)));
        double medGrade = Median(open.Select(e => GradeFactor(e.MaxAbsSegGradePct, gradeFree)));
        double medBw = Median(open.Select(e => (double)betweenness.GetValueOrDefault(e.Id)));

        var scored = new List<BottleneckEdge>();   // 计分边
        var banned = new List<BottleneckEdge>();    // 禁行边（单列，不计分）
        foreach (var e in graph.Edges)
        {
            int bw = betweenness.GetValueOrDefault(e.Id);
            string status = HaulTextReport.StatusText(e.Status);
            if (e.Status != RoadEdgeStatus.Open)
            {
                banned.Add(new BottleneckEdge(e.Id, e.LengthM, e.GradePct, e.MaxAbsSegGradePct,
                    e.LaneCount, bw, status, 0.0, "已禁行"));
                continue;
            }
            double lane = LaneFactor(e.LaneCount);
            double grade = GradeFactor(e.MaxAbsSegGradePct, gradeFree);
            double score = bw * lane * grade;
            scored.Add(new BottleneckEdge(e.Id, e.LengthM, e.GradePct, e.MaxAbsSegGradePct,
                e.LaneCount, bw, status, score,
                ReasonOf(bw, lane, grade, medBw, medLane, medGrade)));
        }

        // 计分边按 score 降序取前 N；同分按 EdgeId Ordinal 兜底（OrderByDescending 本身不稳定，
        // 不兜底的话同分行的先后随枚举序变，两次算出两张表）。禁行边附在后面单列。
        var top = scored
            .OrderByDescending(b => b.Score)
            .ThenBy(b => b.EdgeId, StringComparer.Ordinal)
            .Take(BottleneckTopN).ToList();
        top.AddRange(banned.OrderBy(b => b.EdgeId, StringComparer.Ordinal));
        return top;
    }

    /// <summary>单车道更瓶颈。</summary>
    private static double LaneFactor(int laneCount) => 1.0 / Math.Max(1, laneCount);

    /// <summary>超出限坡的部分计入陡坡因子（判的是分段最大坡）。</summary>
    private static double GradeFactor(double maxAbsSegGradePct, double gradeFreePct)
        => 1.0 + Math.Max(0.0, maxAbsSegGradePct - gradeFreePct) / 10.0;

    /// <summary>
    /// 主因判定用**留一法**：把三个因子各自换成全网中位数，瓶颈分掉得最多的那个即主因。
    /// 原先是 <c>if (laneCount &lt;= 1) return "单车道";</c> 的硬顺序 —— 配上"车道数普遍为 1"的现状，
    /// 全网主因恒为"单车道"，整列零信息量。留一法在所有边车道相同时自动不会把车道选成主因，
    /// 是自适应的；且纯算术、无随机，同图必得同结论。
    /// </summary>
    private static string ReasonOf(int bw, double lane, double grade, double medBw, double medLane, double medGrade)
    {
        double baseScore = bw * lane * grade;
        if (baseScore <= 1e-9) return "—";
        double dropBw = baseScore - medBw * lane * grade;
        double dropLane = baseScore - bw * medLane * grade;
        double dropGrade = baseScore - bw * lane * medGrade;
        double best = Math.Max(dropBw, Math.Max(dropLane, dropGrade));
        if (best <= 1e-9) return "无突出主因";
        // 并列时固定顺序：介数 → 车道 → 陡坡（保证可复现）
        if (Math.Abs(dropBw - best) < 1e-9) return "高介数";
        if (Math.Abs(dropLane - best) < 1e-9) return "单车道";
        return "陡坡";
    }

    /// <summary>中位数（空集为 0）。</summary>
    private static double Median(IEnumerable<double> values)
    {
        var v = values.ToList();
        if (v.Count == 0) return 0.0;
        v.Sort();
        int m = v.Count / 2;
        return v.Count % 2 == 1 ? v[m] : (v[m - 1] + v[m]) / 2.0;
    }

    /// <summary>分期序列：每个 snapshot 算标量（TotalKm/EdgeCount/AvgEquivM/TheoreticalCapacityTph），不存矩阵。</summary>
    private static List<PeriodPoint> ComputePerPeriodSeries(IReadOnlyList<RoadGraph> snapshots, HaulCaliper caliper)
    {
        var series = new List<PeriodPoint>();
        if (snapshots is null) return series;
        for (int i = 0; i < snapshots.Count; i++)
        {
            var g = snapshots[i];
            if (g is null) continue;

            var (sources, sinks, _, _) = ResolveSourcesSinks(g);
            double avgEquiv = 0;
            if (sources.Count > 0 && sinks.Count > 0)
            {
                // 各期与当期同口径，否则趋势线的起伏里混着口径变化，读不出真实趋势。
                var od = new DijkstraPathSolver(g).BuildMatrix(sources, sinks, caliper.Query(loaded: true));
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

    /// <summary>Σ 指定节点的 ThroughputTph。</summary>
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

    /// <summary>路网总里程 km。</summary>
    private static double TotalKm(RoadGraph g)
    {
        double m = 0;
        foreach (var e in g.Edges) m += e.LengthM;
        return m / 1000.0;
    }

}
