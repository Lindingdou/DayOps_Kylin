using System;
using System.Collections.Generic;

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
}
