// 忠实移植自原 PitMine3D Modules/RoadLib/Routing/HaulSolveKernel.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>不可达主因（逐级放宽第一次恢复可达的那一级）。</summary>
public enum HaulBlockCause
{
    /// <summary>可达，无阻因。</summary>
    None,
    /// <summary>限坡挡住。</summary>
    Grade,
    /// <summary>限载挡住。</summary>
    Load,
    /// <summary>检修 / 关闭段挡住。</summary>
    Status,
    /// <summary>单向路挡住（需要逆行才通）。</summary>
    OneWay,
    /// <summary>全放开仍不通：物理不连通。</summary>
    Disconnected,
    /// <summary>源或汇节点不在图里。</summary>
    MissingNode,
}

/// <summary>一条腿的不可达诊断结论。<see cref="EdgeIds"/> = 拦路的具体边（可直接拿去高亮/改属性）。</summary>
public sealed record HaulDiagnosis(HaulBlockCause Cause, string Text, IReadOnlyList<string> EdgeIds)
{
    public static HaulDiagnosis Ok { get; } = new(HaulBlockCause.None, "", Array.Empty<string>());
}

/// <summary>往返对里的一条腿（去程重车 / 回程空车）。</summary>
public sealed class HaulLeg
{
    public required string FromId { get; init; }
    public required string ToId { get; init; }
    /// <summary>true=重车（判限载、用重车坡阻/车速）；false=空车。</summary>
    public required bool Loaded { get; init; }
    public required PathResult Path { get; init; }
    /// <summary>不可达时的诊断；可达时为 null。</summary>
    public HaulDiagnosis? Diagnosis { get; init; }

    public bool Feasible => Path.Feasible;
    public double? LengthM => Path.Feasible ? Path.LengthM : null;
    public double? EquivM => Path.Feasible ? Path.EquivM : null;
    public double? TimeMin => Path.Feasible ? Path.TimeMin : null;
}

/// <summary>
/// 一个源汇对的完整往返结果。**逐腿判定，绝不整对丢弃** —— 去程通、回程断是个真实且常见的状态
/// （单行环线、某段临时封闭），把它折叠成"不可达"会让人查不出到底哪头出了事。
/// </summary>
public sealed class HaulPairResult
{
    public required string SrcId { get; init; }
    public required string DstId { get; init; }
    public required HaulLeg Outbound { get; init; }   // 重车 src → dst
    public required HaulLeg Return { get; init; }     // 空车 dst → src

    /// <summary>双向都通才算这对可行。</summary>
    public bool Feasible => Outbound.Feasible && Return.Feasible;

    /// <summary>往返等效运距 m（去 + 回）；任一腿断即 null，**不用单腿值冒充**。</summary>
    public double? RoundTripEquivM => Feasible ? Outbound.Path.EquivM + Return.Path.EquivM : null;

    /// <summary>循环时间 min = 重车运 + 空车返 + 装车 + 卸车调车；任一腿断即 null。</summary>
    public double? CycleTimeMin { get; init; }

    /// <summary>单趟成本 元；未设运输单价即 null（显示「—」而不是 0 元/趟）。</summary>
    public double? CostPerTripYuan { get; init; }

    /// <summary>四态文本：双向通 / 回程不可达 / 去程不可达 / 双向不可达 / 源汇缺失。</summary>
    public string StatusText
    {
        get
        {
            if (Outbound.Diagnosis?.Cause == HaulBlockCause.MissingNode
                || Return.Diagnosis?.Cause == HaulBlockCause.MissingNode) return "源/汇节点缺失";
            if (Feasible) return "双向通";
            if (!Outbound.Feasible && !Return.Feasible) return "双向不可达";
            // 只写态，不写原因 —— 原因由 PrimaryDiagnosis 那一列/那一行负责，两处都写会互相截断。
            return Outbound.Feasible ? "回程不可达" : "去程不可达";
        }
    }

    /// <summary>主因：优先报去程的，去程通才报回程的。</summary>
    public HaulDiagnosis? PrimaryDiagnosis
        => !Outbound.Feasible ? Outbound.Diagnosis : (!Return.Feasible ? Return.Diagnosis : null);
}

/// <summary>
/// 往返对解算内核：三颗钮（点对点寻径 / 等效运距 / 运输指标报表）**共用同一份解算**。
///
/// 为什么必须共用：原先三处各建各的 <see cref="PathQuery"/> —— 点对点按里程、等效运距按成本、报表按里程，
/// 限坡三处都没给值，车型三处都是硬编码缺省。同一对源汇能给出三个不同的循环时间，用户必然发现并失去信任。
///
/// 两条腿是**两次独立求解**，不是"把去程反过来"：
///   · 限载只在重车腿判（<c>query.Loaded &amp;&amp; e.MaxLoadT &gt; 0</c>），空车本就不受限载约束；
///   · 单向边只在非 OneWay 时才挂反向邻接（<see cref="RoadGraph.EdgesFrom"/>）。
/// 所以空车**合法地可以走另一条路** —— 矿山的单行环线正是这样。把去程取反得到的回程运距是错的。
/// </summary>
public static class HaulSolveKernel
{
    /// <summary>解一个源汇对的往返。<paramref name="diagnose"/>=false 时跳过逐级放宽（批量场景省 5 次求解）。</summary>
    public static HaulPairResult Solve(RoadGraph graph, DijkstraPathSolver solver,
        string srcId, string dstId, HaulCaliper caliper, bool diagnose = true)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        if (solver is null) throw new ArgumentNullException(nameof(solver));
        if (caliper is null) throw new ArgumentNullException(nameof(caliper));

        // 去程 src→dst 重车、回程 dst→src 空车。**回程不是把去程反过来** —— 见类注释。
        var outbound = SolveLeg(graph, solver, srcId, dstId, caliper, loaded: true, diagnose);
        var back = SolveLeg(graph, solver, dstId, srcId, caliper, loaded: false, diagnose);

        double? cycle = null;
        double? cost = null;
        if (outbound.Feasible && back.Feasible)
        {
            cycle = HaulMetrics.CycleTimeMin(outbound.Path.TimeMin, back.Path.TimeMin,
                caliper.SpotLoadMin, caliper.ManeuverDumpMin);
            if (caliper.UnitHaulCostPerTonKm is { } unit)
                cost = outbound.Path.EquivM / 1000.0 * caliper.Truck.PayloadT * unit;
        }

        return new HaulPairResult
        {
            SrcId = srcId,
            DstId = dstId,
            Outbound = outbound,
            Return = back,
            CycleTimeMin = cycle,
            CostPerTripYuan = cost,
        };
    }

    /// <summary>解一条腿（可达则无诊断，不可达则逐级放宽定主因）。</summary>
    public static HaulLeg SolveLeg(RoadGraph graph, DijkstraPathSolver solver,
        string fromId, string toId, HaulCaliper caliper, bool loaded, bool diagnose = true)
    {
        var path = graph.GetNode(fromId) is null || graph.GetNode(toId) is null
            ? PathResult.Unreachable
            : solver.FindPath(fromId, toId, caliper.Query(loaded));

        HaulDiagnosis? diag = null;
        if (!path.Feasible)
            diag = diagnose
                ? Diagnose(graph, solver, fromId, toId, caliper, loaded)
                : new HaulDiagnosis(HaulBlockCause.None, "不可达（未跑诊断）", Array.Empty<string>());

        return new HaulLeg { FromId = fromId, ToId = toId, Loaded = loaded, Path = path, Diagnosis = diag };
    }

    /// <summary>
    /// 不可达诊断：**同一张图、同一个求解器**，按固定顺序逐级放宽最多 5 次，第一次恢复可达的那一级即主因。
    /// 纯函数、无随机 → 同图同口径必得同结论。
    ///
    /// 顺序不能改：限坡 → 限载 → 检修封闭 → 单向 → 物理不连通。前四级都是"改个属性/参数就能解决"，
    /// 第五级才是"得去补一条真路"。第 4 级尤其不能省 —— 单向路造成的不可达在求解器眼里与物理不连通
    /// 长得完全一样，合并了就会把一个改属性能解决的问题误报成"这两块地根本没连上"。
    /// </summary>
    public static HaulDiagnosis Diagnose(RoadGraph graph, DijkstraPathSolver solver,
        string fromId, string toId, HaulCaliper caliper, bool loaded)
    {
        if (graph.GetNode(fromId) is null || graph.GetNode(toId) is null)
            return new HaulDiagnosis(HaulBlockCause.MissingNode,
                $"源/汇节点不在路网里（{fromId} / {toId}）—— 路网重建过？装卸点改过名？",
                Array.Empty<string>());

        var baseQuery = caliper.Query(loaded);

        // ① 去限坡
        if (caliper.MaxGradePct > 0)
        {
            var r = solver.FindPath(fromId, toId, baseQuery.With(PathRelax.IgnoreGrade));
            if (r.Feasible)
            {
                var culprits = EdgesOn(graph, r).Where(e => e.MaxAbsSegGradePct > caliper.MaxGradePct)
                    .OrderByDescending(e => e.MaxAbsSegGradePct).ToList();
                double steepest = culprits.Count > 0 ? culprits[0].MaxAbsSegGradePct : 0;
                return new HaulDiagnosis(HaulBlockCause.Grade,
                    $"限坡 {caliper.MaxGradePct:F1}% 挡住：绕不开的最陡段要 {steepest:F1}%"
                    + $"（{DescribeEdges(culprits, e => $"{e.MaxAbsSegGradePct:F1}%")}）。放宽限坡或改缓这几段即通。",
                    culprits.Select(e => e.Id).ToList());
            }
        }

        // ② 再去限载
        var relax = PathRelax.IgnoreGrade | PathRelax.IgnoreLoad;
        if (loaded)
        {
            var r = solver.FindPath(fromId, toId, baseQuery.With(relax));
            if (r.Feasible)
            {
                var culprits = EdgesOn(graph, r)
                    .Where(e => e.MaxLoadT > 0 && e.MaxLoadT < caliper.Truck.PayloadT)
                    .OrderBy(e => e.MaxLoadT).ToList();
                return new HaulDiagnosis(HaulBlockCause.Load,
                    $"限载挡住：车载 {caliper.Truck.PayloadT:F0}t 过不去"
                    + $"（{DescribeEdges(culprits, e => $"限 {e.MaxLoadT:F0}t")}）。换小车或提高该段限载即通。",
                    culprits.Select(e => e.Id).ToList());
            }
        }

        // ③ 再放行检修 / 关闭
        relax |= PathRelax.IgnoreStatus;
        {
            var r = solver.FindPath(fromId, toId, baseQuery.With(relax));
            if (r.Feasible)
            {
                var culprits = EdgesOn(graph, r).Where(e => e.Status != RoadEdgeStatus.Open).ToList();
                return new HaulDiagnosis(HaulBlockCause.Status,
                    $"禁行段挡住（{DescribeEdges(culprits, e => e.Status == RoadEdgeStatus.Maintenance ? "检修" : "关闭")}）。"
                    + "「边状态」改回开放即通。",
                    culprits.Select(e => e.Id).ToList());
            }
        }

        // ④ 再把单向边当双向
        relax |= PathRelax.IgnoreOneWay;
        {
            var r = solver.FindPath(fromId, toId, baseQuery.With(relax));
            if (r.Feasible)
            {
                var culprits = ReversedOneWayEdgesOn(graph, r);
                return new HaulDiagnosis(HaulBlockCause.OneWay,
                    $"单向路挡住：要逆行才通（{DescribeEdges(culprits, e => $"仅 {e.FromId}→{e.ToId}")}）。"
                    + "把这几段改双向，或补一条反向路。",
                    culprits.Select(e => e.Id).ToList());
            }
        }

        // ⑤ 全放开仍不通：物理不连通
        var comp = graph.BuildComponentMap();
        int cs = comp.GetValueOrDefault(fromId, 0), cd = comp.GetValueOrDefault(toId, 0);
        return new HaulDiagnosis(HaulBlockCause.Disconnected,
            $"物理不连通：{fromId} 在连通片 #{cs}、{toId} 在连通片 #{cd}，两片之间没有任何路。"
            + "用「手动标定线路」补一条连接，或检查中线断点。",
            Array.Empty<string>());
    }

    // ── 小工具 ──

    /// <summary>路径上的边对象（跳过图里查不到的）。</summary>
    private static List<RoadEdge> EdgesOn(RoadGraph graph, PathResult path)
    {
        var list = new List<RoadEdge>();
        foreach (var id in path.EdgeIds)
            if (graph.GetEdge(id) is { } e) list.Add(e);
        return list;
    }

    /// <summary>路径上"被逆向走过且本身是单向"的边 —— 第 4 级放宽的实际获益者。</summary>
    private static List<RoadEdge> ReversedOneWayEdgesOn(RoadGraph graph, PathResult path)
    {
        var list = new List<RoadEdge>();
        for (int i = 0; i < path.EdgeIds.Count && i + 1 < path.NodeIds.Count; i++)
        {
            var e = graph.GetEdge(path.EdgeIds[i]);
            if (e is null || !e.OneWay) continue;
            // edges[i] 把 nodes[i] 接到 nodes[i+1]；若终点恰是该边的 From，说明是逆着 From→To 走的。
            if (e.FromId == path.NodeIds[i + 1]) list.Add(e);
        }
        return list;
    }

    /// <summary>把涉事边压成一行（最多列 3 条，其余折成「等 N 段」）。</summary>
    private static string DescribeEdges(IReadOnlyList<RoadEdge> edges, Func<RoadEdge, string> detail)
    {
        if (edges.Count == 0) return "未定位到具体边";
        var head = edges.Take(3).Select(e => $"{e.Id} {detail(e)}");
        string s = string.Join("、", head);
        return edges.Count > 3 ? $"{s} 等 {edges.Count} 段" : s;
    }
}
