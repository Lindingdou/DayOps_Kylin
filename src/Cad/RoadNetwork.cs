using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 路网寻径（RoadLib 托管切片）—— 多段线集合建无向加权图（合并重合顶点=交点），
/// Dijkstra 求两点间最短路。纯逻辑、可单测。
/// </summary>
public static class RoadNetwork
{
    /// <summary>多段线集合 → 图：节点(合并 tol 内重合点) + 邻接(权=段长)。</summary>
    public static (List<(double x, double y)> nodes, List<List<(int to, double w)>> adj) Build(
        IEnumerable<IReadOnlyList<(double x, double y)>> polys, double tol)
    {
        var nodes = new List<(double x, double y)>();
        var adj = new List<List<(int to, double w)>>();
        double tol2 = tol * tol;
        int NodeIdx((double x, double y) p)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                double dx = nodes[i].x - p.x, dy = nodes[i].y - p.y;
                if (dx * dx + dy * dy <= tol2) return i;
            }
            nodes.Add(p); adj.Add(new List<(int, double)>());
            return nodes.Count - 1;
        }
        foreach (var poly in polys)
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                int u = NodeIdx(poly[i]), v = NodeIdx(poly[i + 1]);
                if (u == v) continue;
                double dx = nodes[u].x - nodes[v].x, dy = nodes[u].y - nodes[v].y;
                double w = Math.Sqrt(dx * dx + dy * dy);
                adj[u].Add((v, w)); adj[v].Add((u, w));
            }
        return (nodes, adj);
    }

    /// <summary>
    /// 交叉口打断（noding）——忠实原 RoadGraphBuilder.NodePolylines：把两两折线段的<b>内部</b>交点作为断点，
    /// 在各自折线上插点并打断成子段（X 十字：两线各断；T 丁字：被搭线在交点处断，搭线端点靠顶点吸附并入）。
    /// 交点落段端点(容差内)不打断，交给建图时的顶点合并。<b>2D 场景</b>：无逐点 Z，平面相交一律打断
    /// （原有 Z 闸门区分平交/立交；Kylin 2D 无法区分立体交叉，已记录）。
    /// </summary>
    public static List<IReadOnlyList<(double x, double y)>> NodePolylines(
        IEnumerable<IReadOnlyList<(double x, double y)>> polys, double tol)
    {
        var lines = polys.Where(p => p != null && p.Count >= 2).ToList();
        int n = lines.Count;
        double tol2 = Math.Max(tol, 1e-9) * Math.Max(tol, 1e-9);
        var breaks = new List<(int seg, double x, double y)>[n];
        for (int i = 0; i < n; i++) breaks[i] = new List<(int, double, double)>();

        void AddBreak(int li, int seg, (double x, double y) p)
        {
            var a = lines[li][seg]; var b = lines[li][seg + 1];
            if (D2(p, a) <= tol2 || D2(p, b) <= tol2) return;              // 落端点 → 交顶点合并处理
            foreach (var br in breaks[li]) if (br.seg == seg && D2((br.x, br.y), p) <= tol2) return; // 去重
            breaks[li].Add((seg, p.x, p.y));
        }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                for (int si = 0; si + 1 < lines[i].Count; si++)
                    for (int sj = 0; sj + 1 < lines[j].Count; sj++)
                    {
                        var hit = PolylineIntersect.SegSeg(lines[i][si], lines[i][si + 1], lines[j][sj], lines[j][sj + 1]);
                        if (hit is { } p) { AddBreak(i, si, p); AddBreak(j, sj, p); }
                    }

        var result = new List<IReadOnlyList<(double x, double y)>>();
        for (int i = 0; i < n; i++)
        {
            if (breaks[i].Count == 0) { result.Add(lines[i]); continue; }
            result.AddRange(SplitPolyline(lines[i], breaks[i], tol2));
        }
        return result;
    }

    private static List<IReadOnlyList<(double x, double y)>> SplitPolyline(
        IReadOnlyList<(double x, double y)> line, List<(int seg, double x, double y)> brk, double tol2)
    {
        // 组装插入断点后的顶点序列（带"是否断点"标记），断点处切分。
        var verts = new List<(double x, double y)>();
        var isCut = new List<bool>();
        void Push((double x, double y) p, bool cut)
        {
            if (verts.Count > 0 && D2(verts[verts.Count - 1], p) <= tol2) { if (cut) isCut[isCut.Count - 1] = true; return; }
            verts.Add(p); isCut.Add(cut);
        }
        Push(line[0], false);
        for (int s = 0; s + 1 < line.Count; s++)
        {
            var a = line[s]; var b = line[s + 1];
            double dx = b.x - a.x, dy = b.y - a.y, L2 = dx * dx + dy * dy;
            var pts = brk.Where(br => br.seg == s).Select(br => (br.x, br.y)).ToList();
            pts.Sort((p, q) =>
            {
                double tp = L2 < 1e-12 ? 0 : ((p.x - a.x) * dx + (p.y - a.y) * dy);
                double tq = L2 < 1e-12 ? 0 : ((q.x - a.x) * dx + (q.y - a.y) * dy);
                return tp.CompareTo(tq);
            });
            foreach (var p in pts) Push(p, true);
            Push(b, false);
        }
        var outp = new List<IReadOnlyList<(double x, double y)>>();
        var cur = new List<(double x, double y)> { verts[0] };
        for (int k = 1; k < verts.Count; k++)
        {
            cur.Add(verts[k]);
            if (isCut[k] && k < verts.Count - 1) { outp.Add(cur); cur = new List<(double x, double y)> { verts[k] }; }
        }
        if (cur.Count >= 2) outp.Add(cur);
        return outp;
    }

    /// <summary>先 noding(交叉打断) 再建图 —— 路网寻径/拓扑的正确入口：使 X/T 交叉真正连通(否则跨段交叉不连)。</summary>
    public static (List<(double x, double y)> nodes, List<List<(int to, double w)>> adj) BuildNoded(
        IEnumerable<IReadOnlyList<(double x, double y)>> polys, double tol)
        => Build(NodePolylines(polys, tol), tol);

    private static double D2((double x, double y) a, (double x, double y) b)
    { double dx = a.x - b.x, dy = a.y - b.y; return dx * dx + dy * dy; }

    /// <summary>Dijkstra 最短路，返回节点索引路径；不可达返回空。</summary>
    public static List<int> Dijkstra(List<List<(int to, double w)>> adj, int start, int goal)
    {
        int n = adj.Count;
        var path = new List<int>();
        if (start < 0 || goal < 0 || start >= n || goal >= n) return path;
        var dist = new double[n]; var prev = new int[n]; var done = new bool[n];
        for (int i = 0; i < n; i++) { dist[i] = double.MaxValue; prev[i] = -1; }
        dist[start] = 0;
        for (int it = 0; it < n; it++)
        {
            int u = -1; double best = double.MaxValue;
            for (int i = 0; i < n; i++) if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
            if (u < 0) break;
            done[u] = true;
            if (u == goal) break;
            foreach (var (v, w) in adj[u])
                if (dist[u] + w < dist[v]) { dist[v] = dist[u] + w; prev[v] = u; }
        }
        if (dist[goal] >= double.MaxValue) return path;   // 不可达
        for (int c = goal; c >= 0; c = prev[c]) path.Add(c);
        path.Reverse();
        return path;
    }

    /// <summary>单源最短距：从 start 到每个节点的最短距离(不可达为 +∞)。供 OD 运距矩阵。</summary>
    public static double[] DijkstraDistances(List<List<(int to, double w)>> adj, int start)
    {
        int n = adj.Count;
        var dist = new double[n]; var done = new bool[n];
        for (int i = 0; i < n; i++) dist[i] = double.PositiveInfinity;
        if (start < 0 || start >= n) return dist;
        dist[start] = 0;
        for (int it = 0; it < n; it++)
        {
            int u = -1; double best = double.PositiveInfinity;
            for (int i = 0; i < n; i++) if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
            if (u < 0) break;
            done[u] = true;
            foreach (var (v, w) in adj[u])
                if (dist[u] + w < dist[v]) dist[v] = dist[u] + w;
        }
        return dist;
    }

    /// <summary>邻接中 u→v 的边权(无则 +∞)。</summary>
    private static double EdgeWeight(List<List<(int to, double w)>> adj, int u, int v)
    {
        if (u < 0 || u >= adj.Count) return double.PositiveInfinity;
        double best = double.PositiveInfinity;
        foreach (var (t, w) in adj[u]) if (t == v && w < best) best = w;
        return best;
    }

    /// <summary>路径(节点索引序列)的边权总和。</summary>
    public static double PathWeight(List<List<(int to, double w)>> adj, List<int> path)
    {
        double d = 0;
        for (int i = 1; i < path.Count; i++) d += EdgeWeight(adj, path[i - 1], path[i]);
        return d;
    }

    /// <summary>带排除集的 Dijkstra 最短路(供 Yen K 短路)：排除给定节点与无向边。不可达返回空。</summary>
    public static List<int> DijkstraExcluding(List<List<(int to, double w)>> adj, int start, int goal,
        HashSet<int> exclNodes, HashSet<(int, int)> exclEdges)
    {
        int n = adj.Count;
        var path = new List<int>();
        if (start < 0 || goal < 0 || start >= n || goal >= n) return path;
        if (exclNodes.Contains(start)) return path;
        var dist = new double[n]; var prev = new int[n]; var done = new bool[n];
        for (int i = 0; i < n; i++) { dist[i] = double.PositiveInfinity; prev[i] = -1; }
        dist[start] = 0;
        for (int it = 0; it < n; it++)
        {
            int u = -1; double best = double.PositiveInfinity;
            for (int i = 0; i < n; i++) if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
            if (u < 0) break;
            done[u] = true;
            if (u == goal) break;
            foreach (var (v, w) in adj[u])
            {
                if (exclNodes.Contains(v)) continue;
                var key = u < v ? (u, v) : (v, u);
                if (exclEdges.Contains(key)) continue;
                if (dist[u] + w < dist[v]) { dist[v] = dist[u] + w; prev[v] = u; }
            }
        }
        if (double.IsPositiveInfinity(dist[goal])) return path;
        for (int c = goal; c >= 0; c = prev[c]) path.Add(c);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Yen K 最短路(备选路径, 按里程升序)：返回 1~K 条不重复简单路径(节点索引序列)。
    /// 忠实移植原 DijkstraPathSolver.FindKShortest 结构(spur/root 分解、禁同 root 的第 i 条边、禁 root 中间节点)。
    /// </summary>
    public static List<List<int>> KShortestPaths(List<List<(int to, double w)>> adj, int start, int goal, int k)
    {
        var A = new List<List<int>>();
        var noNodes = new HashSet<int>(); var noEdges = new HashSet<(int, int)>();
        var first = DijkstraExcluding(adj, start, goal, noNodes, noEdges);
        if (first.Count < 2) return A;
        A.Add(first);
        if (k <= 1) return A;

        var seen = new HashSet<string> { Sig(first) };
        var B = new List<List<int>>();
        for (int kk = 1; kk < k; kk++)
        {
            var prev = A[kk - 1];
            for (int i = 0; i < prev.Count - 1; i++)
            {
                int spur = prev[i];
                var rootNodes = prev.GetRange(0, i + 1);
                var exclEdges = new HashSet<(int, int)>();
                foreach (var p in A)
                    if (SameRoot(p, rootNodes, i) && i + 1 < p.Count)
                    {
                        int u = p[i], v = p[i + 1];
                        exclEdges.Add(u < v ? (u, v) : (v, u));
                    }
                var exclNodes = new HashSet<int>();
                for (int j = 0; j < i; j++) exclNodes.Add(rootNodes[j]);   // 禁 root 中间节点(除 spur)

                var spurPath = DijkstraExcluding(adj, spur, goal, exclNodes, exclEdges);
                if (spurPath.Count < 2) continue;

                var cand = new List<int>(rootNodes);
                cand.AddRange(spurPath.GetRange(1, spurPath.Count - 1));   // 拼接(去 spur 重复)
                string sig = Sig(cand);
                if (seen.Contains(sig)) continue;
                if (B.Exists(b => Sig(b) == sig)) continue;
                B.Add(cand);
            }
            if (B.Count == 0) break;
            B.Sort((a, b) => PathWeight(adj, a).CompareTo(PathWeight(adj, b)));
            var bestPath = B[0]; B.RemoveAt(0);
            A.Add(bestPath);
            seen.Add(Sig(bestPath));
        }
        return A;
    }

    private static string Sig(List<int> path) => string.Join(",", path);

    private static bool SameRoot(List<int> path, List<int> root, int i)
    {
        if (path.Count <= i) return false;
        for (int j = 0; j <= i; j++) if (path[j] != root[j]) return false;
        return true;
    }

    /// <summary>路径(节点索引序列)的总长度。</summary>
    public static double PathLength(List<(double x, double y)> nodes, List<int> path)
    {
        double d = 0;
        for (int i = 1; i < path.Count; i++)
        {
            var a = nodes[path[i - 1]]; var b = nodes[path[i]];
            d += Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
        }
        return d;
    }

    /// <summary>最近节点索引。</summary>
    public static int NearestNode(List<(double x, double y)> nodes, double px, double py)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            double dx = nodes[i].x - px, dy = nodes[i].y - py, d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>一条边的介数结果(端点/长/被最短路经过次数)，按介数降序。</summary>
    public readonly record struct EdgeBw(int U, int V, double LengthM, int Betweenness);

    /// <summary>
    /// 边介数(shortest-path centrality) —— 忠实原 TransportIndicators 瓶颈段的介数核: 对所有 源×汇 有序对
    /// 跑 Dijkstra, 累计每条边被最短路经过次数, 按介数降序。识别路网关键(高流量)段。
    /// (原另乘 车道因子/陡坡因子; Kylin 路网为中线几何最小模型无 车道/坡度/状态, 该加权记录待边属性模型。)
    /// 无向边规范 (min,max); s==t 跳; 不可达跳。纯图论、可单测。
    /// </summary>
    public static List<EdgeBw> EdgeBetweenness(List<List<(int to, double w)>> adj,
        IReadOnlyList<int> sources, IReadOnlyList<int> sinks)
    {
        var len = new Dictionary<(int, int), double>();
        for (int u = 0; u < adj.Count; u++)
            foreach (var (v, w) in adj[u]) { var e = (System.Math.Min(u, v), System.Math.Max(u, v)); if (!len.ContainsKey(e)) len[e] = w; }
        var bw = new Dictionary<(int, int), int>();
        foreach (var s in sources)
            foreach (var t in sinks)
            {
                if (s == t) continue;
                var path = Dijkstra(adj, s, t);
                for (int i = 1; i < path.Count; i++)
                { var e = (System.Math.Min(path[i - 1], path[i]), System.Math.Max(path[i - 1], path[i])); bw[e] = bw.GetValueOrDefault(e) + 1; }
            }
        var outp = new List<EdgeBw>();
        foreach (var kv in len)
            outp.Add(new EdgeBw(kv.Key.Item1, kv.Key.Item2, kv.Value, bw.GetValueOrDefault(kv.Key)));
        outp.Sort((a, b) => b.Betweenness.CompareTo(a.Betweenness));
        return outp;
    }

    /// <summary>路网几何运输指标(忠实原 TransportIndicators 几何部分): 总里程 + 源×汇可达对 等运距 均值/最大。</summary>
    public readonly record struct NetworkStats(double TotalMileageM, int ReachablePairs, double MeanDistM, double MaxDistM);

    /// <summary>
    /// 从路网算几何运输指标: 总里程(去重边长和) + 源×汇有序对最短路 可达对数/均值/最大 运距。
    /// (原另有运量加权均值/成本, 需吨量与采矿模型, 记录; 此为纯几何可达指标。)不可达对(∞)不计。纯图论、可单测。
    /// </summary>
    public static NetworkStats NetworkIndicators(List<List<(int to, double w)>> adj, IReadOnlyList<int> sources, IReadOnlyList<int> sinks)
    {
        double totalMileage = 0;
        var seen = new HashSet<(int, int)>();
        for (int u = 0; u < adj.Count; u++)
            foreach (var (v, w) in adj[u]) { var e = (System.Math.Min(u, v), System.Math.Max(u, v)); if (seen.Add(e)) totalMileage += w; }
        double sum = 0, max = 0; int cnt = 0;
        foreach (var s in sources)
        {
            var dist = DijkstraDistances(adj, s);
            foreach (var t in sinks)
            {
                if (s == t) continue;
                double d = dist[t];
                if (!double.IsInfinity(d)) { sum += d; if (d > max) max = d; cnt++; }
            }
        }
        return new NetworkStats(totalMileage, cnt, cnt > 0 ? sum / cnt : 0, max);
    }

    /// <summary>度为 1 的悬挂端点(路网端, 天然装卸/出入口候选)。</summary>
    public static List<int> DanglingEndpoints(List<List<(int to, double w)>> adj)
    {
        var ends = new List<int>();
        for (int i = 0; i < adj.Count; i++)
        {
            var uniq = new HashSet<int>(); foreach (var (v, _) in adj[i]) uniq.Add(v);
            if (uniq.Count <= 1) ends.Add(i);
        }
        return ends;
    }
}
