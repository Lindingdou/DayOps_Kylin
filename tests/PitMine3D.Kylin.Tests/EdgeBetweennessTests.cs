using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>路网边介数（最短路中心性，瓶颈段核）回归。</summary>
public class EdgeBetweennessTests
{
    // 无向邻接: 每边两向登记
    private static List<List<(int to, double w)>> Graph(int n, params (int u, int v, double w)[] edges)
    {
        var adj = new List<List<(int, double)>>();
        for (int i = 0; i < n; i++) adj.Add(new List<(int, double)>());
        foreach (var (u, v, w) in edges) { adj[u].Add((v, w)); adj[v].Add((u, w)); }
        return adj;
    }

    [Fact]
    public void Path_graph_middle_edge_has_highest_betweenness()
    {
        // 链 0-1-2-3(各边长1)。全端点(0,3)为源汇 → 路径 0→3 与 3→0 各经 3 条边。
        var adj = Graph(4, (0, 1, 1), (1, 2, 1), (2, 3, 1));
        var ends = RoadNetwork.DanglingEndpoints(adj);
        Assert.Equal(new[] { 0, 3 }, ends.OrderBy(x => x).ToArray());   // 度1端点
        var bw = RoadNetwork.EdgeBetweenness(adj, ends, ends);
        // 三条边都被 0↔3 两向经过 → 各介数 2
        Assert.All(bw, e => Assert.Equal(2, e.Betweenness));
        Assert.Equal(3, bw.Count);
    }

    [Fact]
    public void Y_junction_shared_stem_edge_is_bottleneck()
    {
        // Y: 0-1, 1-2, 1-3(茎 0-1 共享)。端点 0/2/3。所有端点对最短路必经…看茎 0-1。
        //   源汇=端点{0,2,3}: 对 (0,2)(0,3)(2,0)(3,0) 经边(0,1); (2,3)(3,2) 不经(0,1)。→ 边(0,1)介数=4(最高)
        var adj = Graph(4, (0, 1, 1), (1, 2, 1), (1, 3, 1));
        var ends = RoadNetwork.DanglingEndpoints(adj);   // {0,2,3}
        Assert.Equal(3, ends.Count);
        var bw = RoadNetwork.EdgeBetweenness(adj, ends, ends);
        var stem = bw.Single(e => (e.U == 0 && e.V == 1) || (e.U == 1 && e.V == 0));
        Assert.Equal(4, stem.Betweenness);            // 茎最忙
        Assert.Equal(stem.Betweenness, bw[0].Betweenness);   // 降序 → 茎在首位
    }

    [Fact]
    public void NetworkIndicators_mileage_and_reachable_distances()
    {
        // 链 0-1-2-3, 边长 3/4/5 → 总里程 12。端点 {0,3} 源汇: 0→3=12, 3→0=12 → 2 对, 均值/最大 12。
        var adj = Graph(4, (0, 1, 3), (1, 2, 4), (2, 3, 5));
        var ends = RoadNetwork.DanglingEndpoints(adj);
        var s = RoadNetwork.NetworkIndicators(adj, ends, ends);
        Assert.Equal(12, s.TotalMileageM, 6);
        Assert.Equal(2, s.ReachablePairs);           // (0,3) 与 (3,0)
        Assert.Equal(12, s.MeanDistM, 6);
        Assert.Equal(12, s.MaxDistM, 6);
    }

    [Fact]
    public void NetworkIndicators_unreachable_pairs_excluded()
    {
        // 两分量 0-1(长2) 与 2-3(长3): 总里程 5; 全节点源汇 → 仅同分量对可达
        var adj = Graph(4, (0, 1, 2), (2, 3, 3));
        var s = RoadNetwork.NetworkIndicators(adj, new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 3 });
        Assert.Equal(5, s.TotalMileageM, 6);
        Assert.Equal(4, s.ReachablePairs);           // (0,1)(1,0)(2,3)(3,2), 跨分量 ∞ 不计
        Assert.Equal(2.5, s.MeanDistM, 6);           // (2+2+3+3)/4
        Assert.Equal(3, s.MaxDistM, 6);
    }

    [Fact]
    public void Unreachable_pairs_skipped_no_throw()
    {
        // 两不连通分量 0-1 与 2-3
        var adj = Graph(4, (0, 1, 1), (2, 3, 1));
        var bw = RoadNetwork.EdgeBetweenness(adj, new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 3 });
        Assert.Equal(2, bw.Count);
        Assert.All(bw, e => Assert.Equal(2, e.Betweenness));   // 各分量内 2 向
    }
}
