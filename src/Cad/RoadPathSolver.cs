using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

// ─────────────────────────────────────────────────────────────────────────────
// 约束感知运输寻径(忠实移植原 RoadLib.Network.RoadGraph + RoadLib.Routing.PathSolver)。
// Kylin 既有 RoadNetwork 是无属性通用图(几何最短路/介数); 此为带 纵坡/限载/状态 属性的
// 运输图 + Dijkstra 约束寻径(限坡逼折返、超载拒行、闭边绕行、等效运距/时间/成本)。纯托管、可单测。
// 复用 Kylin 既有 HaulMetrics/TruckProfile(等效运距/行车时间)。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>轻量三维点(double 精度)。忠实原 RoadLib.Network.Point3d。</summary>
public readonly record struct Point3d(double X, double Y, double Z)
{
    /// <summary>三维距离。</summary>
    public double DistanceTo(in Point3d o)
    {
        double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>平面(水平)距离, 用于算坡度。</summary>
    public double HorizontalDistanceTo(in Point3d o)
    {
        double dx = X - o.X, dy = Y - o.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>节点类型(6 类)。忠实原 RoadNodeType。</summary>
public enum RoadNodeType { Loading, Unloading, Junction, Portal, Entry, Waypoint }

/// <summary>边状态(3 态)。忠实原 RoadEdgeStatus。</summary>
public enum RoadEdgeStatus { Open, Maintenance, Closed }

/// <summary>路网节点。忠实原 RoadNode。</summary>
public sealed class RoadNode
{
    public string Id { get; }
    public RoadNodeType Type { get; set; }
    public Point3d Position { get; set; }
    /// <summary>源 / 汇的吞吐能力 t/h(仅 Loading/Unloading 有意义)。</summary>
    public double ThroughputTph { get; set; }
    public string? RefId { get; set; }

    public RoadNode(string id, RoadNodeType type, Point3d position)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Type = type;
        Position = position;
    }
}

/// <summary>路网边(一段路)。几何走中线, 拓扑 + 属性在此。忠实原 RoadEdge。</summary>
public sealed class RoadEdge
{
    public string Id { get; }
    public string FromId { get; }
    public string ToId { get; }

    /// <summary>中线(至少含起讫两点)。</summary>
    public IReadOnlyList<Point3d> Centerline { get; private set; }

    /// <summary>三维里程 m(构造时按中线自动算, 可后续覆盖)。</summary>
    public double LengthM { get; set; }
    /// <summary>纵坡 %, 带符号, 沿 From→To 方向。</summary>
    public double GradePct { get; set; }

    public int LaneCount { get; set; } = 1;
    public bool OneWay { get; set; }
    public double MaxLoadT { get; set; }
    public double SpeedLimitKph { get; set; }
    public string? Pavement { get; set; }
    public RoadEdgeStatus Status { get; set; } = RoadEdgeStatus.Open;
    public bool IsTemporary { get; set; }

    public RoadEdge(string id, string fromId, string toId, IReadOnlyList<Point3d>? centerline = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        FromId = fromId ?? throw new ArgumentNullException(nameof(fromId));
        ToId = toId ?? throw new ArgumentNullException(nameof(toId));
        Centerline = centerline ?? Array.Empty<Point3d>();
        RecomputeGeometry();
    }

    /// <summary>替换中线并重算里程 / 纵坡。</summary>
    public void SetCenterline(IReadOnlyList<Point3d> centerline)
    {
        Centerline = centerline ?? Array.Empty<Point3d>();
        RecomputeGeometry();
    }

    /// <summary>由中线算三维里程与纵坡(中线点数 &lt; 2 则保持现值, 留给调用方/AddEdge 显式填)。</summary>
    public void RecomputeGeometry()
    {
        var c = Centerline;
        if (c.Count < 2) return;
        double len = 0, horiz = 0;
        for (int i = 1; i < c.Count; i++)
        {
            len += c[i].DistanceTo(c[i - 1]);
            horiz += c[i].HorizontalDistanceTo(c[i - 1]);
        }
        LengthM = len;
        double rise = c[^1].Z - c[0].Z;
        GradePct = horiz > 1e-9 ? rise / horiz * 100.0 : 0.0;
    }
}

/// <summary>有向可达连接:从某节点经 Edge 抵达 ToId; Reversed=逆 From→To 行驶(坡度取反)。忠实原 RoadLink。</summary>
public readonly record struct RoadLink(RoadEdge Edge, string ToId, bool Reversed);

/// <summary>路网校验报告。忠实原 RoadGraph.ValidationReport。</summary>
public sealed class ValidationReport
{
    public bool IsFullyConnected { get; init; }
    public int ComponentCount { get; init; }
    public IReadOnlyList<string> IsolatedNodeIds { get; init; } = new List<string>();
    public IReadOnlyList<string> Issues { get; init; } = new List<string>();
    public bool Ok => IsFullyConnected && Issues.Count == 0;
}

/// <summary>路网图:节点 + 边 + 邻接。忠实原 RoadGraph(移植寻径所需核; 打断/序列化等非寻径部分不在此)。</summary>
public sealed class RoadGraph
{
    private readonly Dictionary<string, RoadNode> _nodes = new();
    private readonly Dictionary<string, RoadEdge> _edges = new();
    private readonly Dictionary<string, List<RoadLink>> _adj = new();   // nodeId -> 出向连接

    public IReadOnlyCollection<RoadNode> Nodes => _nodes.Values;
    public IReadOnlyCollection<RoadEdge> Edges => _edges.Values;
    public int NodeCount => _nodes.Count;
    public int EdgeCount => _edges.Count;

    public RoadNode? GetNode(string id) => _nodes.GetValueOrDefault(id);
    public RoadEdge? GetEdge(string id) => _edges.GetValueOrDefault(id);

    public RoadNode AddNode(RoadNode node)
    {
        _nodes[node.Id] = node;
        return node;
    }

    public RoadNode AddNode(string id, RoadNodeType type, Point3d position)
        => AddNode(new RoadNode(id, type, position));

    /// <summary>加边。要求两端节点已存在。若边里程为 0, 按两端节点直线兜底(算里程 + 纵坡)。</summary>
    public RoadEdge AddEdge(RoadEdge edge)
    {
        if (!_nodes.ContainsKey(edge.FromId))
            throw new InvalidOperationException($"边 {edge.Id} 的起点 {edge.FromId} 不在图中。");
        if (!_nodes.ContainsKey(edge.ToId))
            throw new InvalidOperationException($"边 {edge.Id} 的终点 {edge.ToId} 不在图中。");
        if (edge.LengthM <= 0)
        {
            var a = _nodes[edge.FromId].Position;
            var b = _nodes[edge.ToId].Position;
            edge.LengthM = a.DistanceTo(b);
            double h = a.HorizontalDistanceTo(b);
            edge.GradePct = h > 1e-9 ? (b.Z - a.Z) / h * 100.0 : 0.0;
        }
        _edges[edge.Id] = edge;
        Link(edge);
        return edge;
    }

    /// <summary>删边 + 重建邻接。</summary>
    public bool RemoveEdge(string edgeId)
    {
        if (!_edges.Remove(edgeId)) return false;
        RebuildAdjacency();
        return true;
    }

    /// <summary>改边属性:替换同 Id 边并重建邻接(坡度 / 车道 / 状态变更走这里)。</summary>
    public void UpdateEdge(RoadEdge edge)
    {
        _edges[edge.Id] = edge;
        RebuildAdjacency();
    }

    /// <summary>离给定点最近的节点(线性扫描)。超 maxDistM 返回 null。</summary>
    public RoadNode? NearestNode(Point3d p, double maxDistM = double.MaxValue)
    {
        RoadNode? best = null;
        double bestD = maxDistM;
        foreach (var n in _nodes.Values)
        {
            double d = n.Position.DistanceTo(p);
            if (d <= bestD) { bestD = d; best = n; }
        }
        return best;
    }

    public IReadOnlyList<RoadLink> EdgesFrom(string nodeId)
        => _adj.TryGetValue(nodeId, out var list) ? list : (IReadOnlyList<RoadLink>)Array.Empty<RoadLink>();

    private void Link(RoadEdge e)
    {
        if (!_adj.TryGetValue(e.FromId, out var fwd)) { fwd = new(); _adj[e.FromId] = fwd; }
        fwd.Add(new RoadLink(e, e.ToId, Reversed: false));
        if (!e.OneWay)
        {
            if (!_adj.TryGetValue(e.ToId, out var rev)) { rev = new(); _adj[e.ToId] = rev; }
            rev.Add(new RoadLink(e, e.FromId, Reversed: true));
        }
    }

    private void RebuildAdjacency()
    {
        _adj.Clear();
        foreach (var e in _edges.Values) Link(e);
    }

    /// <summary>校验:并查集数连通分量 + 孤立节点 + 逐边合规(纵坡/车道/里程)。忠实原 RoadGraph.Validate。</summary>
    public ValidationReport Validate(double maxGradePct = 10.0)
    {
        var issues = new List<string>();

        // 物理连通性(无向, 忽略单双向):并查集数连通分量。
        var parent = new Dictionary<string, string>();
        string Find(string x)
        {
            string r = x;
            while (parent[r] != r) r = parent[r];
            while (parent[x] != r) { var n = parent[x]; parent[x] = r; x = n; }
            return r;
        }
        foreach (var id in _nodes.Keys) parent[id] = id;
        foreach (var e in _edges.Values)
        {
            var ra = Find(e.FromId); var rb = Find(e.ToId);
            if (ra != rb) parent[ra] = rb;
        }
        int components = _nodes.Count == 0 ? 0 : _nodes.Keys.Select(Find).Distinct().Count();

        // 孤立节点:没有任何边引用。
        var referenced = new HashSet<string>();
        foreach (var e in _edges.Values) { referenced.Add(e.FromId); referenced.Add(e.ToId); }
        var isolated = _nodes.Keys.Where(id => !referenced.Contains(id)).ToList();
        foreach (var id in isolated) issues.Add($"孤立节点:{id}(无边连接)");

        // 逐边合规。
        foreach (var e in _edges.Values)
        {
            if (Math.Abs(e.GradePct) > maxGradePct)
                issues.Add($"边 {e.Id} 纵坡 {e.GradePct:F1}% 超限(>{maxGradePct:F1}%)");
            if (e.LaneCount < 1)
                issues.Add($"边 {e.Id} 车道数 {e.LaneCount} 非法(<1)");
            if (e.LengthM <= 0)
                issues.Add($"边 {e.Id} 里程为 0(中线缺失且两端重合?)");
        }

        return new ValidationReport
        {
            IsFullyConnected = components <= 1,
            ComponentCount = components,
            IsolatedNodeIds = isolated,
            Issues = issues,
        };
    }
}

/// <summary>寻径权重口径。忠实原 WeightMode。</summary>
public enum WeightMode { Distance, Time, Fuel, Cost }

/// <summary>一次寻径查询的口径。忠实原 PathQuery。</summary>
public sealed class PathQuery
{
    public WeightMode Mode { get; set; } = WeightMode.Distance;
    public TruckProfile Truck { get; set; } = TruckProfile.Default;
    /// <summary>重车方向(true)按载重判限载、用重车坡阻; false=空车回程。</summary>
    public bool Loaded { get; set; } = true;
    /// <summary>限坡硬约束 %:超此纵坡的边不可行驶, 0=不限。真实矿山靠它逼出折返/绕行。</summary>
    public double MaxGradePct { get; set; } = 0.0;

    public static PathQuery Default => new();
}

/// <summary>寻径结果(路径 + 里程 + 等效运距 + 时间 + 成本)。忠实原 PathResult。</summary>
public sealed class PathResult
{
    public bool Feasible { get; init; }
    public IReadOnlyList<string> NodeIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EdgeIds { get; init; } = Array.Empty<string>();
    public double LengthM { get; init; }       // 实际三维里程
    public double EquivM { get; init; }         // 等效运距(坡度折算)
    public double TimeMin { get; init; }        // 单程行车时间
    public double Cost { get; init; }           // 单趟运输成本(等效运距×载重×单价)

    public static PathResult Unreachable { get; } = new() { Feasible = false };
}

/// <summary>OD 运距 / 运能矩阵。行=源、列=汇; 不可达填 +∞。忠实原 ODMatrix。</summary>
public sealed class ODMatrix
{
    public required IReadOnlyList<string> Sources { get; init; }
    public required IReadOnlyList<string> Sinks { get; init; }
    public required double[,] Dist { get; init; }
    public required double[,] Equiv { get; init; }
    public required double[,] Time { get; init; }
}

/// <summary>寻径求解器接口。忠实原 IPathSolver。</summary>
public interface IPathSolver
{
    PathResult FindPath(string fromId, string toId, PathQuery query);
    ODMatrix BuildMatrix(IReadOnlyList<string> sources, IReadOnlyList<string> sinks, PathQuery query);
    /// <summary>K 最短路(Yen 算法, 按里程升序)。返回 1~K 条不重复路径。</summary>
    List<PathResult> FindKShortest(string fromId, string toId, PathQuery query, int k);
    /// <summary>路网变化后失效缓存(整体清)。</summary>
    void Invalidate(IEnumerable<string>? changedEdgeIds = null);
}

/// <summary>
/// Dijkstra 约束寻径(零依赖, 二叉堆用 .NET PriorityQueue)。边权按口径 + 坡度算, 运距是寻径副产物。
/// 忠实移植原 RoadLib.Routing.DijkstraPathSolver:限坡/限载/闭边硬约束门控 + 缓存 key 含全约束。
/// </summary>
public sealed class DijkstraPathSolver : IPathSolver
{
    private readonly RoadGraph _graph;
    // 单源结果缓存:key 含所有影响最短路的口径(源/权重模式/重空/限坡/车型), 否则不同约束查询会错误命中同缓存。
    private readonly Dictionary<(string Src, WeightMode Mode, bool Loaded, double MaxGradePct, TruckProfile Truck),
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

    // ── Yen K 最短路(备选路径) ──
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

                // 已有路径中共享同一 root 的, 禁掉其第 i 条边(逼出不同分叉)
                var exclEdges = new HashSet<string>();
                foreach (var p in A)
                    if (SameRoot(p.NodeIds, rootNodes, i) && i < p.EdgeIds.Count)
                        exclEdges.Add(p.EdgeIds[i]);
                // 禁掉 root 中间节点(除 spur)避免绕回
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

    // 带排除集的 Dijkstra(无缓存, Yen 用)。
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
            foreach (var link in _graph.EdgesFrom(u))
            {
                var e = link.Edge;
                if (e.Status != RoadEdgeStatus.Open) continue;
                if (exclEdges.Contains(e.Id)) continue;
                if (exclNodes.Contains(link.ToId)) continue;
                if (query.MaxGradePct > 0 && Math.Abs(e.GradePct) > query.MaxGradePct) continue;
                if (query.Loaded && e.MaxLoadT > 0 && query.Truck.PayloadT > e.MaxLoadT) continue;
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
            bool reversed = e.FromId == nodeIds[i + 1];   // 到 nodeIds[i+1]:若它是 From 则逆向
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

    // ── 单源最短路(带缓存) ──
    private Dictionary<string, (double Dist, string? PrevNode, string? PrevEdge)> SingleSource(
        string src, PathQuery query)
    {
        var key = (src, query.Mode, query.Loaded, query.MaxGradePct, query.Truck);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var best = new Dictionary<string, (double, string?, string?)>();
        var pq = new PriorityQueue<string, double>();
        best[src] = (0.0, null, null);
        pq.Enqueue(src, 0.0);

        while (pq.TryDequeue(out var u, out var du))
        {
            if (du > best[u].Item1) continue;   // 过期堆项
            foreach (var link in _graph.EdgesFrom(u))
            {
                var e = link.Edge;
                if (e.Status != RoadEdgeStatus.Open) continue;                       // 禁行(检修/关闭)
                if (query.MaxGradePct > 0 && Math.Abs(e.GradePct) > query.MaxGradePct) continue;   // 超限坡
                if (query.Loaded && e.MaxLoadT > 0 && query.Truck.PayloadT > e.MaxLoadT) continue; // 超限载
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
            bool reversed = e.FromId == cur;                     // 抵达 cur:若 cur 是 From 则逆向行驶
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
