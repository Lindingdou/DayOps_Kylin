// 忠实移植自原 PitMine3D Modules/RoadLib/Routing/PathSolver.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>寻径权重口径（设计 §4 的 B3）。</summary>
public enum WeightMode { Distance, Time, Fuel, Cost }

/// <summary>
/// 逐级放宽开关：**只给「不可达诊断」用**，正常寻径一律 <see cref="None"/>。
/// 逐项关掉过滤再解一次，第一次恢复可达的那一级就是拦路主因 —— 这是"为什么走不通"的唯一可证答案。
/// </summary>
[Flags]
public enum PathRelax
{
    None = 0,
    /// <summary>不判限坡。</summary>
    IgnoreGrade = 1,
    /// <summary>不判限载。</summary>
    IgnoreLoad = 2,
    /// <summary>检修 / 关闭段当开放走。</summary>
    IgnoreStatus = 4,
    /// <summary>单向边当双向走（车会逆行，只用来定性）。</summary>
    IgnoreOneWay = 8,
}

/// <summary>一次寻径查询的口径。</summary>
public sealed class PathQuery
{
    public WeightMode Mode { get; set; } = WeightMode.Distance;
    public TruckProfile Truck { get; set; } = TruckProfile.Default;
    /// <summary>重车方向（true）按载重判限载、用重车坡阻；false=空车回程。</summary>
    public bool Loaded { get; set; } = true;
    /// <summary>
    /// 限坡硬约束 %（B4）：边的**最大分段纵坡**（<see cref="Network.RoadEdge.MaxAbsSegGradePct"/>）超此值即不可行驶，0=不限。
    /// 真实矿山靠它逼出折返/绕行。**库层默认 0（不限）是有意的**——它是"没人给口径时不要偷偷卡人"的中立值；
    /// 业务调用方必须显式从共享运输约束灌值（见 <see cref="HaulCaliper"/>），否则限坡等于没写。
    /// </summary>
    public double MaxGradePct { get; set; } = 0.0;
    /// <summary>逐级放宽（诊断用，默认不放宽）。</summary>
    public PathRelax Relax { get; set; } = PathRelax.None;

    public static PathQuery Default => new();

    /// <summary>复制一份并叠加放宽位（诊断逐级放宽用；原查询不动）。</summary>
    public PathQuery With(PathRelax relax) => new()
    {
        Mode = Mode, Truck = Truck, Loaded = Loaded, MaxGradePct = MaxGradePct, Relax = Relax | relax,
    };
}

/// <summary>寻径结果（B/C 一次到位：路径 + 里程 + 等效运距 + 时间 + 成本）。</summary>
public sealed class PathResult
{
    public bool Feasible { get; init; }
    public IReadOnlyList<string> NodeIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EdgeIds { get; init; } = Array.Empty<string>();
    public double LengthM { get; init; }       // 实际三维里程
    public double EquivM { get; init; }         // 等效运距（坡度折算）
    public double TimeMin { get; init; }        // 单程行车时间
    public double Cost { get; init; }           // 单趟运输成本（等效运距×载重×单价）

    public static PathResult Unreachable { get; } = new() { Feasible = false };
}

/// <summary>OD 运距 / 运能矩阵（B2）。行=源、列=汇；不可达填 +∞。</summary>
public sealed class ODMatrix
{
    public required IReadOnlyList<string> Sources { get; init; }
    public required IReadOnlyList<string> Sinks { get; init; }
    public required double[,] Dist { get; init; }
    public required double[,] Equiv { get; init; }
    public required double[,] Time { get; init; }
}

/// <summary>寻径求解器接口（设计 §4）。</summary>
public interface IPathSolver
{
    PathResult FindPath(string fromId, string toId, PathQuery query);
    ODMatrix BuildMatrix(IReadOnlyList<string> sources, IReadOnlyList<string> sinks, PathQuery query);
    /// <summary>K 最短路（B6 备选路径，Yen 算法，按里程升序）。返回 1~K 条不重复路径。</summary>
    List<PathResult> FindKShortest(string fromId, string toId, PathQuery query, int k);
    /// <summary>路网变化后失效缓存（B5；v1 整体清，后续按受影响 OD 精化）。</summary>
    void Invalidate(IEnumerable<string>? changedEdgeIds = null);
}

/// <summary>Dijkstra 寻径（零依赖，二叉堆用 .NET PriorityQueue）。边权按口径 + 坡度算，运距是寻径副产物。</summary>
public sealed class DijkstraPathSolver : IPathSolver
{
    private readonly RoadGraph _graph;
    // 单源结果缓存：key 必须含所有影响最短路的口径（源/权重模式/重空/限坡/放宽位/车型），
    // 否则不同约束的查询会错误命中同一缓存。车型按引用区分（同一 query 复用即命中，BuildMatrix 内每源只跑一次）。
    // Relax 必须在键里：诊断会拿同一个 solver 连解 5 次，漏了它第 2 次起全部命中第 1 次的缓存，诊断恒定返回同一结论。
    private readonly Dictionary<(string Src, WeightMode Mode, bool Loaded, double MaxGradePct, PathRelax Relax, TruckProfile Truck),
        Dictionary<string, (double Dist, string? PrevNode, string? PrevEdge)>> _cache = new();

    public DijkstraPathSolver(RoadGraph graph) => _graph = graph;

    public void Invalidate(IEnumerable<string>? changedEdgeIds = null) => _cache.Clear();

    public PathResult FindPath(string fromId, string toId, PathQuery query)
    {
        if (_graph.GetNode(fromId) is null || _graph.GetNode(toId) is null) return PathResult.Unreachable;
        var sssp = SingleSource(fromId, query);
        if (!sssp.TryGetValue(toId, out var hit) || double.IsPositiveInfinity(hit.Dist))
            return PathResult.Unreachable;
        return Reconstruct(fromId, toId, sssp, query);
    }

    public ODMatrix BuildMatrix(IReadOnlyList<string> sources, IReadOnlyList<string> sinks, PathQuery query)
    {
        int ns = sources.Count, nk = sinks.Count;
        var dist = new double[ns, nk];
        var equiv = new double[ns, nk];
        var time = new double[ns, nk];
        for (int i = 0; i < ns; i++)
        {
            // 每个源只跑一次 Dijkstra，对所有汇复用。
            for (int j = 0; j < nk; j++)
            {
                var r = FindPath(sources[i], sinks[j], query);
                dist[i, j] = r.Feasible ? r.LengthM : double.PositiveInfinity;
                equiv[i, j] = r.Feasible ? r.EquivM : double.PositiveInfinity;
                time[i, j] = r.Feasible ? r.TimeMin : double.PositiveInfinity;
            }
        }
        return new ODMatrix { Sources = sources, Sinks = sinks, Dist = dist, Equiv = equiv, Time = time };
    }

    // ── Yen K 最短路（B6 备选路径） ──
    public List<PathResult> FindKShortest(string fromId, string toId, PathQuery query, int k)
    {
        var A = new List<PathResult>();
        var first = FindPath(fromId, toId, query);
        if (!first.Feasible) return A;
        A.Add(first);
        if (k <= 1) return A;

        var seen = new HashSet<string> { Sig(first) };
        var B = new List<PathResult>();

        for (int kk = 1; kk < k; kk++)
        {
            var prev = A[kk - 1];
            for (int i = 0; i < prev.NodeIds.Count - 1; i++)
            {
                string spur = prev.NodeIds[i];
                var rootNodes = prev.NodeIds.Take(i + 1).ToList();   // 0..i
                var rootEdges = prev.EdgeIds.Take(i).ToList();       // 0..i-1

                // 已有路径中共享同一 root 的，禁掉其第 i 条边（逼出不同分叉）
                var exclEdges = new HashSet<string>();
                foreach (var p in A)
                    if (SameRoot(p.NodeIds, rootNodes, i) && i < p.EdgeIds.Count)
                        exclEdges.Add(p.EdgeIds[i]);
                // 禁掉 root 中间节点（除 spur）避免绕回
                var exclNodes = new HashSet<string>();
                for (int j = 0; j < i; j++) exclNodes.Add(rootNodes[j]);

                var spurPath = DijkstraExcluding(spur, toId, query, exclNodes, exclEdges);
                if (spurPath is null) continue;

                var nodes = new List<string>(rootNodes);
                nodes.AddRange(spurPath.NodeIds.Skip(1));
                var edges = new List<string>(rootEdges);
                edges.AddRange(spurPath.EdgeIds);
                var cand = MeasurePath(nodes, edges, query);
                string sig = Sig(cand);
                if (seen.Contains(sig)) continue;
                if (B.Exists(b => Sig(b) == sig)) continue;
                B.Add(cand);
            }
            if (B.Count == 0) break;
            B.Sort((a, b) => a.LengthM.CompareTo(b.LengthM));
            var best = B[0]; B.RemoveAt(0);
            A.Add(best);
            seen.Add(Sig(best));
        }
        return A;
    }

    private static string Sig(PathResult p) => string.Join(",", p.EdgeIds);

    private static bool SameRoot(IReadOnlyList<string> path, List<string> root, int i)
    {
        if (path.Count <= i) return false;
        for (int j = 0; j <= i; j++) if (path[j] != root[j]) return false;
        return true;
    }

    // 带排除集的 Dijkstra（无缓存，Yen 用）。
    private PathResult? DijkstraExcluding(string src, string dst, PathQuery query,
        HashSet<string> exclNodes, HashSet<string> exclEdges)
    {
        if (exclNodes.Contains(src)) return null;
        var best = new Dictionary<string, (double Dist, string? PrevNode, string? PrevEdge)>();
        var pq = new PriorityQueue<string, double>();
        best[src] = (0.0, null, null);
        pq.Enqueue(src, 0.0);
        while (pq.TryDequeue(out var u, out var du))
        {
            if (du > best[u].Dist) continue;
            if (u == dst) break;
            foreach (var link in Adjacency(u, query))
            {
                var e = link.Edge;
                if (exclEdges.Contains(e.Id)) continue;
                if (exclNodes.Contains(link.ToId)) continue;
                if (!Passable(e, query)) continue;
                double w = EdgeWeight(e, link.Reversed, query);
                double nd = du + w;
                var v = link.ToId;
                if (!best.TryGetValue(v, out var cur) || nd < cur.Dist)
                {
                    best[v] = (nd, u, e.Id);
                    pq.Enqueue(v, nd);
                }
            }
        }
        return best.ContainsKey(dst) ? Reconstruct(src, dst, best, query) : null;
    }

    // 由 节点+边 序列重算路径度量。
    private PathResult MeasurePath(List<string> nodeIds, List<string> edgeIds, PathQuery query)
    {
        double length = 0, equiv = 0, time = 0;
        for (int i = 0; i < edgeIds.Count; i++)
        {
            var e = _graph.GetEdge(edgeIds[i]);
            if (e is null) continue;
            bool reversed = e.FromId == nodeIds[i + 1];   // 到 nodeIds[i+1]：若它是 From 则逆向
            double grade = reversed ? -e.GradePct : e.GradePct;
            double len = EdgeLength(e);
            length += len;
            equiv += HaulMetrics.EquivalentLengthM(len, grade, query.Truck, query.Loaded);
            time += HaulMetrics.TravelTimeMin(len, grade, query.Truck, query.Loaded);
        }
        double cost = equiv / 1000.0 * query.Truck.PayloadT * query.Truck.UnitHaulCostPerTonKm;
        return new PathResult
        {
            Feasible = true,
            NodeIds = nodeIds,
            EdgeIds = edgeIds,
            LengthM = length,
            EquivM = equiv,
            TimeMin = time,
            Cost = cost,
        };
    }

    // ── 单源最短路（带缓存） ──
    private Dictionary<string, (double Dist, string? PrevNode, string? PrevEdge)> SingleSource(
        string src, PathQuery query)
    {
        var key = (src, query.Mode, query.Loaded, query.MaxGradePct, query.Relax, query.Truck);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var best = new Dictionary<string, (double, string?, string?)>();
        var pq = new PriorityQueue<string, double>();
        best[src] = (0.0, null, null);
        pq.Enqueue(src, 0.0);

        while (pq.TryDequeue(out var u, out var du))
        {
            if (du > best[u].Item1) continue;   // 过期堆项
            foreach (var link in Adjacency(u, query))
            {
                var e = link.Edge;
                if (!Passable(e, query)) continue;
                double w = EdgeWeight(e, link.Reversed, query);
                double nd = du + w;
                var v = link.ToId;
                if (!best.TryGetValue(v, out var cur) || nd < cur.Item1)
                {
                    best[v] = (nd, u, e.Id);
                    pq.Enqueue(v, nd);
                }
            }
        }
        _cache[key] = best;
        return best;
    }

    /// <summary>
    /// 单条边能不能走（三道硬过滤，两处 Dijkstra 共用一份实现 —— 原先各写一份，改一处漏一处）。
    /// 限坡判的是 <see cref="RoadEdge.MaxAbsSegGradePct"/>（分段最大）而不是 <see cref="RoadEdge.GradePct"/>（整段平均）：
    /// 平均坡会把长边里的陡段平掉，按它判限坡等于没判。
    /// </summary>
    private static bool Passable(RoadEdge e, PathQuery query)
    {
        if (e.Status != RoadEdgeStatus.Open && !query.Relax.HasFlag(PathRelax.IgnoreStatus)) return false;   // 检修/关闭禁行
        if (query.MaxGradePct > 0 && !query.Relax.HasFlag(PathRelax.IgnoreGrade)
            && e.MaxAbsSegGradePct > query.MaxGradePct) return false;                                        // 超限坡
        if (query.Loaded && e.MaxLoadT > 0 && !query.Relax.HasFlag(PathRelax.IgnoreLoad)
            && query.Truck.PayloadT > e.MaxLoadT) return false;                                              // 超限载（空车腿本就不受限载）
        return true;
    }

    /// <summary>取出向连接；诊断放宽 <see cref="PathRelax.IgnoreOneWay"/> 时改用把单向边也当双向的那份邻接。</summary>
    private IReadOnlyList<RoadLink> Adjacency(string nodeId, PathQuery query)
        => query.Relax.HasFlag(PathRelax.IgnoreOneWay)
            ? _graph.EdgesFromIgnoringOneWay(nodeId)
            : _graph.EdgesFrom(nodeId);

    private double EdgeWeight(RoadEdge e, bool reversed, PathQuery query)
    {
        double len = EdgeLength(e);
        double grade = reversed ? -e.GradePct : e.GradePct;   // 逆向行驶坡度取反
        return query.Mode switch
        {
            WeightMode.Distance => len,
            WeightMode.Time => HaulMetrics.TravelTimeMin(len, grade, query.Truck, query.Loaded),
            _ => HaulMetrics.EquivalentLengthM(len, grade, query.Truck, query.Loaded), // Fuel/Cost 用等效运距代理
        };
    }

    private double EdgeLength(RoadEdge e)
    {
        if (e.LengthM > 0) return e.LengthM;
        var a = _graph.GetNode(e.FromId); var b = _graph.GetNode(e.ToId);
        return a is not null && b is not null ? a.Position.DistanceTo(b.Position) : 0.0;
    }

    private PathResult Reconstruct(string src, string dst,
        Dictionary<string, (double Dist, string? PrevNode, string? PrevEdge)> sssp, PathQuery query)
    {
        var nodes = new List<string>();
        var edges = new List<string>();
        double length = 0, equiv = 0, time = 0;

        string cur = dst;
        while (true)
        {
            nodes.Add(cur);
            var (_, prevNode, prevEdge) = sssp[cur];
            if (prevNode is null || prevEdge is null) break;     // 到源
            var e = _graph.GetEdge(prevEdge)!;
            bool reversed = e.FromId == cur;                     // 抵达 cur：若 cur 是 From 则逆向行驶
            double grade = reversed ? -e.GradePct : e.GradePct;
            double len = EdgeLength(e);
            length += len;
            equiv += HaulMetrics.EquivalentLengthM(len, grade, query.Truck, query.Loaded);
            time += HaulMetrics.TravelTimeMin(len, grade, query.Truck, query.Loaded);
            edges.Add(e.Id);
            cur = prevNode;
        }
        nodes.Reverse();
        edges.Reverse();

        double cost = equiv / 1000.0 * query.Truck.PayloadT * query.Truck.UnitHaulCostPerTonKm;
        return new PathResult
        {
            Feasible = true,
            NodeIds = nodes,
            EdgeIds = edges,
            LengthM = length,
            EquivM = equiv,
            TimeMin = time,
            Cost = cost,
        };
    }
}
