using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>两连通片之间最窄缺口：各自最近的一对点 + 间距。</summary>
public readonly record struct RoadGap(
    int FromComp, int ToComp, (double x, double y) From, (double x, double y) To, double GapM);

/// <summary>
/// 路网连通性诊断（忠实移植原 <c>RoadLib.Network.RoadConnectivity</c> 的 2D 版, 去 Z）——
/// 不可达时回答「断在哪、每处多宽」。口径：把边中线按 step 加密成点、按连通片分组,
/// 网格哈希(格边=maxGap, 3×3 邻域)求两两片间最近点对(≤maxGap 才收), 按宽度升序。纯函数、可单测。
/// 作用于 <see cref="RoadNetwork"/> 的索引图(nodes + adj)。
/// </summary>
public static class RoadConnectivity
{
    public const double SampleStepM = 10.0;

    /// <summary>连通分量标号(0-based, 每节点一个)+分量数。孤立节点自成一片。</summary>
    public static int[] Components(List<List<(int to, double w)>> adj, out int count)
    {
        int n = adj.Count;
        var label = new int[n];
        for (int i = 0; i < n; i++) label[i] = -1;
        count = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            if (label[s] != -1) continue;
            int c = count++;
            stack.Push(s); label[s] = c;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                foreach (var (v, _) in adj[u]) if (label[v] == -1) { label[v] = c; stack.Push(v); }
            }
        }
        return label;
    }

    /// <summary>全图片与片之间最近缺口(≤maxGap 的才收), 按宽度升序。</summary>
    public static List<RoadGap> AllGaps(List<(double x, double y)> nodes, List<List<(int to, double w)>> adj,
        double maxGapM = 300.0, double stepM = SampleStepM)
    {
        var label = Components(adj, out _);
        double cell = Math.Max(1.0, maxGapM);
        var grid = new Dictionary<(long, long), List<(int comp, double x, double y)>>();
        void Add(int comp, double x, double y)
        {
            var key = ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));
            if (!grid.TryGetValue(key, out var b)) { b = new(); grid[key] = b; }
            b.Add((comp, x, y));
        }
        // 边中线加密成点(去重无向边 u<v)
        int n = adj.Count;
        for (int u = 0; u < n; u++)
            foreach (var (v, _) in adj[u])
            {
                if (v <= u) continue;
                var a = nodes[u]; var b = nodes[v];
                Add(label[u], a.x, a.y);
                double len = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
                double step = Math.Max(1.0, stepM);
                int m = (int)(len / step);
                for (int k = 1; k <= m; k++)
                {
                    double t = k * step / len;
                    if (t >= 1.0) break;
                    Add(label[u], a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
                }
                Add(label[v], b.x, b.y);
            }

        var best = new Dictionary<(int, int), RoadGap>();
        foreach (var (key, bucket) in grid)
        {
            var near = new List<(int comp, double x, double y)>();
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy), out var nb)) near.AddRange(nb);
            foreach (var (ca, ax, ay) in bucket)
                foreach (var (cb, bx, by) in near)
                {
                    if (ca == cb) continue;
                    double d = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
                    if (d > maxGapM) continue;
                    var k = ca < cb ? (ca, cb) : (cb, ca);
                    if (best.TryGetValue(k, out var cur) && cur.GapM <= d) continue;
                    var (fc, fx, fy, tc, tx, ty) = ca < cb ? (ca, ax, ay, cb, bx, by) : (cb, bx, by, ca, ax, ay);
                    best[k] = new RoadGap(fc, tc, (fx, fy), (tx, ty), d);
                }
        }
        return best.Values.OrderBy(x => x.GapM).ToList();
    }
}
