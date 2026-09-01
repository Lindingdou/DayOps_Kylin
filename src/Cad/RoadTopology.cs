using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 路网拓扑分类核 —— 忠实移植原 RoadLib.Network.RoadTopology 的 R-T1/R-T2/R-T3。
/// 把离散中线打断出来的碎边还原成「真节点—真节点」的**路段**, 并按度数给节点/路段定类:
///   R-T1 节点按度数分 5 类: 孤立(0)/端点(1)/接缝(2)/丁字(3)/多岔(≥4)——只有度≠2 才是真节点(接缝不是路口)。
///   R-T2 路段 = 两真节点间串起的一串边(接缝处接续), 统计以路段为单位而非碎边。
///   R-T3 路段分 3 类(全由端点度数推): 干线(两端都通)/支线(一端悬挂)/孤立段(两端悬挂)。悬挂=度≤1。
/// 另 DescribeDelta 出两次拓扑的变化(增删边/边状态回显用)。纯图论、确定性、可单测。
///
/// 区别于 Kylin 既有 RoadNetworkReportCmd(只数 度0/1/≥3 的**碎边级**计数): 本类做**路段级**分类
/// (碎边压成路段) + 节点 5 类 + 拓扑增量。构建于 Kylin `RoadNetwork.Build` 的 (nodes, adj) 之上。
/// **已记录(需更富图模型, Kylin 邻接表无)**: 装卸点类型(源汇作天然端点)、人工改判(R-T7)、可通行过滤(passableOnly)。
/// </summary>
public enum RoadNodeClass { Isolated, Endpoint, Seam, Tee, Multi }
public enum RoadSegmentClass { Trunk, Spur, Isolated }

/// <summary>一条路段: 依次经过的节点(含首末真节点 + 中间接缝) + 里程 + 类别。</summary>
public sealed class RoadTopoSegment
{
    public IReadOnlyList<int> NodePath { get; init; } = Array.Empty<int>();
    public int FromNode { get; init; }
    public int ToNode { get; init; }
    public double LengthM { get; init; }
    public RoadSegmentClass Class { get; init; }
    public bool IsLoop => FromNode == ToNode;
}

public sealed class RoadTopologyReport
{
    public IReadOnlyList<int> NodeCountByClass { get; init; } = Array.Empty<int>();       // 下标=RoadNodeClass
    public IReadOnlyList<int> SegmentCountByClass { get; init; } = Array.Empty<int>();    // 下标=RoadSegmentClass
    public IReadOnlyList<double> SegmentLengthByClass { get; init; } = Array.Empty<double>();
    public IReadOnlyList<RoadTopoSegment> Segments { get; init; } = Array.Empty<RoadTopoSegment>();
    public int ComponentCount { get; init; }
    public int RealNodeCount { get; init; }
    public int SeamCount { get; init; }
    public double TotalLengthM { get; init; }

    public int JunctionCount => NodeCountByClass[(int)RoadNodeClass.Tee] + NodeCountByClass[(int)RoadNodeClass.Multi];
    public int DangleCount => NodeCountByClass[(int)RoadNodeClass.Endpoint] + NodeCountByClass[(int)RoadNodeClass.Isolated];

    public string Summary =>
        $"路段 {Segments.Count}（" + string.Join(" / ", Enum.GetValues<RoadSegmentClass>()
            .Select(c => $"{RoadTopology.TextOf(c)} {SegmentCountByClass[(int)c]}")) + "）"
        + $" · 路口 {JunctionCount} · 悬挂端点 {DangleCount} · 接缝 {SeamCount} · 连通片 {ComponentCount} · 总长 {TotalLengthM:0.#}m";
}

public static class RoadTopology
{
    /// <summary>度数 → 节点类别(R-T1)。</summary>
    public static RoadNodeClass ClassOfDegree(int degree) => degree switch
    {
        <= 0 => RoadNodeClass.Isolated,
        1 => RoadNodeClass.Endpoint,
        2 => RoadNodeClass.Seam,
        3 => RoadNodeClass.Tee,
        _ => RoadNodeClass.Multi,
    };

    public static string TextOf(RoadNodeClass c) => c switch
    {
        RoadNodeClass.Isolated => "孤立点", RoadNodeClass.Endpoint => "端点",
        RoadNodeClass.Seam => "接缝", RoadNodeClass.Tee => "丁字", _ => "多岔",
    };

    public static string TextOf(RoadSegmentClass c) => c switch
    {
        RoadSegmentClass.Trunk => "干线", RoadSegmentClass.Spur => "支线", _ => "孤立段",
    };

    private struct Edge { public int A, B; public double W; }

    /// <summary>分析拓扑: 度数 → 节点类别 → 碎边压成路段 → 路段类别 + 连通片。nodes/adj 来自 RoadNetwork.Build。</summary>
    public static RoadTopologyReport Analyze(
        IReadOnlyList<(double x, double y)> nodes,
        IReadOnlyList<List<(int to, double w)>> adj)
    {
        int n = nodes?.Count ?? 0;
        var nodeCount = new int[Enum.GetValues<RoadNodeClass>().Length];
        var segCount = new int[Enum.GetValues<RoadSegmentClass>().Length];
        var segLen = new double[segCount.Length];
        if (n == 0 || adj == null)
            return new RoadTopologyReport { NodeCountByClass = nodeCount, SegmentCountByClass = segCount, SegmentLengthByClass = segLen };

        // 度数 = 邻接条目数(与原 adj[u].Count 同口径)。
        var degree = new int[n];
        for (int u = 0; u < n; u++) degree[u] = u < adj.Count && adj[u] != null ? adj[u].Count : 0;

        // R-T1 节点类别计数。
        for (int u = 0; u < n; u++) nodeCount[(int)ClassOfDegree(degree[u])]++;

        // 无向边表(每边一次, u<v) + 逐节点关联边索引。
        var edges = new List<Edge>();
        var incident = new List<List<int>>(n);
        for (int u = 0; u < n; u++) incident.Add(new List<int>());
        for (int u = 0; u < n; u++)
        {
            if (u >= adj.Count || adj[u] == null) continue;
            foreach (var (v, w) in adj[u])
                if (v > u && v < n) { int ei = edges.Count; edges.Add(new Edge { A = u, B = v, W = w }); incident[u].Add(ei); incident[v].Add(ei); }
        }

        bool IsBreak(int id) => degree[id] != 2;   // 真节点=度≠2(无装卸点概念, 记录); 悬挂=度≤1 在 BuildSegment 内联
        int Other(int ei, int at) => edges[ei].A == at ? edges[ei].B : edges[ei].A;

        var used = new bool[edges.Count];
        var segments = new List<RoadTopoSegment>();

        // ② 从每个真节点出发, 顺接缝走到下一个真节点。
        for (int start = 0; start < n; start++)
        {
            if (!IsBreak(start)) continue;
            foreach (int e0 in incident[start])
            {
                if (used[e0]) continue;
                used[e0] = true;
                var chain = new List<int> { e0 };
                int cur = Other(e0, start);
                int last = e0;
                while (!IsBreak(cur))
                {
                    int next = -1;
                    foreach (int ei in incident[cur]) if (ei != last && !used[ei]) { next = ei; break; }
                    if (next < 0) break;
                    used[next] = true;
                    chain.Add(next);
                    cur = Other(next, cur);
                    last = next;
                }
                segments.Add(BuildSegment(chain, start, cur, edges, degree));
            }
        }

        // ③ 剩下的是「整环都是接缝」的孤立环(无真节点作起点)。
        for (int e0 = 0; e0 < edges.Count; e0++)
        {
            if (used[e0]) continue;
            used[e0] = true;
            var chain = new List<int> { e0 };
            int start = edges[e0].A, cur = edges[e0].B, last = e0;
            while (cur != start)
            {
                int next = -1;
                foreach (int ei in incident[cur]) if (ei != last && !used[ei]) { next = ei; break; }
                if (next < 0) break;
                used[next] = true;
                chain.Add(next);
                cur = Other(next, cur);
                last = next;
            }
            segments.Add(BuildSegment(chain, start, cur, edges, degree));
        }

        double total = 0;
        foreach (var s in segments) { segCount[(int)s.Class]++; segLen[(int)s.Class] += s.LengthM; total += s.LengthM; }
        int seams = 0;
        for (int u = 0; u < n; u++) if (!IsBreak(u)) seams++;

        return new RoadTopologyReport
        {
            NodeCountByClass = nodeCount,
            SegmentCountByClass = segCount,
            SegmentLengthByClass = segLen,
            Segments = segments,
            ComponentCount = CountComponents(n, edges),
            RealNodeCount = n - seams,
            SeamCount = seams,
            TotalLengthM = total,
        };
    }

    private static RoadTopoSegment BuildSegment(List<int> chain, int startId, int endId, List<Edge> edges, int[] degree)
    {
        // 节点序: 从 startId 依链走到 endId。
        var path = new List<int> { startId };
        double len = 0;
        int cur = startId;
        foreach (int ei in chain)
        {
            int nxt = edges[ei].A == cur ? edges[ei].B : edges[ei].A;
            path.Add(nxt);
            len += edges[ei].W;
            cur = nxt;
        }

        bool loop = startId == endId;
        RoadSegmentClass cls;
        if (loop)
            cls = degree[startId] >= 3 ? RoadSegmentClass.Trunk : RoadSegmentClass.Isolated;
        else
        {
            int open = (degree[startId] <= 1 ? 1 : 0) + (degree[endId] <= 1 ? 1 : 0);
            cls = open switch { 0 => RoadSegmentClass.Trunk, 1 => RoadSegmentClass.Spur, _ => RoadSegmentClass.Isolated };
        }
        return new RoadTopoSegment { NodePath = path, FromNode = startId, ToNode = endId, LengthM = len, Class = cls };
    }

    private static int CountComponents(int n, List<Edge> edges)
    {
        if (n == 0) return 0;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var e in edges) { int ra = Find(e.A), rb = Find(e.B); if (ra != rb) parent[ra] = rb; }
        var roots = new HashSet<int>();
        for (int i = 0; i < n; i++) roots.Add(Find(i));
        return roots.Count;
    }

    /// <summary>两次分析之间的变化(只列真动了的项; 没动返回空串)。</summary>
    public static string DescribeDelta(RoadTopologyReport before, RoadTopologyReport after)
    {
        var parts = new List<string>();
        foreach (var c in Enum.GetValues<RoadSegmentClass>())
        {
            int a = before.SegmentCountByClass[(int)c], b = after.SegmentCountByClass[(int)c];
            if (a != b) parts.Add($"{TextOf(c)} {a}→{b}");
        }
        if (before.JunctionCount != after.JunctionCount) parts.Add($"路口 {before.JunctionCount}→{after.JunctionCount}");
        if (before.DangleCount != after.DangleCount) parts.Add($"悬挂端点 {before.DangleCount}→{after.DangleCount}");
        if (before.ComponentCount != after.ComponentCount) parts.Add($"连通片 {before.ComponentCount}→{after.ComponentCount}");
        return string.Join(" · ", parts);
    }
}
