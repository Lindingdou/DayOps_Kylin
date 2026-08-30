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
