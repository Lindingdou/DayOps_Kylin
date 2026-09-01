using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 路网拓扑分类 回归 —— 忠实移植原 RoadLib.Network.RoadTopology 的 R-T1/R-T2/R-T3 已知值验证:
/// 节点 5 类(度数)/ 碎边压成路段 / 路段 3 类(干线-支线-孤立段) / 连通片 / 拓扑增量。
/// </summary>
public class RoadTopologyTests
{
    // 无向图: n 节点(位置无关) + 边(a,b,权)。adj 双向填。
    static (List<(double x, double y)>, List<List<(int to, double w)>>) G(int n, params (int a, int b, double w)[] es)
    {
        var nodes = Enumerable.Range(0, n).Select(_ => (0.0, 0.0)).ToList();
        var adj = Enumerable.Range(0, n).Select(_ => new List<(int, double)>()).ToList();
        foreach (var (a, b, w) in es) { adj[a].Add((b, w)); adj[b].Add((a, w)); }
        return (nodes, adj);
    }

    [Fact]
    public void Simple_path_is_one_isolated_segment()
    {
        var (nodes, adj) = G(4, (0, 1, 10), (1, 2, 10), (2, 3, 10));
        var r = RoadTopology.Analyze(nodes, adj);
        Assert.Single(r.Segments);
        Assert.Equal(30, r.Segments[0].LengthM, 6);
        Assert.Equal(RoadSegmentClass.Isolated, r.Segments[0].Class);   // 两端悬挂
        Assert.Equal(2, r.RealNodeCount);                              // 两端点
        Assert.Equal(2, r.SeamCount);                                  // 中间两接缝
        Assert.Equal(1, r.ComponentCount);
        Assert.Equal(0, r.JunctionCount);
        Assert.Equal(2, r.DangleCount);
    }

    [Fact]
    public void Y_junction_three_spurs()
    {
        var (nodes, adj) = G(4, (0, 1, 10), (0, 2, 10), (0, 3, 10));   // 0 度3
        var r = RoadTopology.Analyze(nodes, adj);
        Assert.Equal(3, r.Segments.Count);
        Assert.All(r.Segments, s => Assert.Equal(RoadSegmentClass.Spur, s.Class));
        Assert.Equal(1, r.NodeCountByClass[(int)RoadNodeClass.Tee]);
        Assert.Equal(3, r.NodeCountByClass[(int)RoadNodeClass.Endpoint]);
        Assert.Equal(1, r.JunctionCount);
        Assert.Equal(3, r.SegmentCountByClass[(int)RoadSegmentClass.Spur]);
    }

    [Fact]
    public void Cross_node_is_multi()
    {
        var (nodes, adj) = G(5, (0, 1, 10), (0, 2, 10), (0, 3, 10), (0, 4, 10));   // 0 度4
        var r = RoadTopology.Analyze(nodes, adj);
        Assert.Equal(1, r.NodeCountByClass[(int)RoadNodeClass.Multi]);
        Assert.Equal(1, r.JunctionCount);
        Assert.Equal(4, r.Segments.Count);
    }

    [Fact]
    public void Trunk_between_two_junctions()
    {
        // 0(度3) —接缝1—接缝2— 3(度3), 两端各挂 2 支线。
        var (nodes, adj) = G(8,
            (0, 1, 10), (1, 2, 10), (2, 3, 10),   // 干线链
            (0, 4, 5), (0, 5, 5),                 // 0 的支线 → 0 度3
            (3, 6, 5), (3, 7, 5));                // 3 的支线 → 3 度3
        var r = RoadTopology.Analyze(nodes, adj);
        Assert.Equal(1, r.SegmentCountByClass[(int)RoadSegmentClass.Trunk]);
        Assert.Equal(4, r.SegmentCountByClass[(int)RoadSegmentClass.Spur]);
        var trunk = r.Segments.Single(s => s.Class == RoadSegmentClass.Trunk);
        Assert.Equal(30, trunk.LengthM, 6);                 // 干线 3×10
        Assert.Equal(new[] { 0, 1, 2, 3 }, trunk.NodePath.ToArray());
    }

    [Fact]
    public void All_seam_triangle_is_isolated_loop()
    {
        var (nodes, adj) = G(3, (0, 1, 10), (1, 2, 10), (2, 0, 10));   // 全度2
        var r = RoadTopology.Analyze(nodes, adj);
        Assert.Single(r.Segments);
        Assert.True(r.Segments[0].IsLoop);
        Assert.Equal(RoadSegmentClass.Isolated, r.Segments[0].Class);
        Assert.Equal(3, r.SeamCount);
        Assert.Equal(0, r.RealNodeCount);
        Assert.Equal(30, r.Segments[0].LengthM, 6);
    }

    [Fact]
    public void Components_counted()
    {
        var (nodes, adj) = G(4, (0, 1, 10), (2, 3, 10));   // 两条独立段
        var r = RoadTopology.Analyze(nodes, adj);
        Assert.Equal(2, r.ComponentCount);
        Assert.Equal(2, r.Segments.Count);
    }

    [Fact]
    public void DescribeDelta_lists_changes()
    {
        var path = RoadTopology.Analyze(G(4, (0, 1, 10), (1, 2, 10), (2, 3, 10)).Item1,
                                        G(4, (0, 1, 10), (1, 2, 10), (2, 3, 10)).Item2);
        var y = RoadTopology.Analyze(G(4, (0, 1, 10), (0, 2, 10), (0, 3, 10)).Item1,
                                     G(4, (0, 1, 10), (0, 2, 10), (0, 3, 10)).Item2);
        Assert.Equal("", RoadTopology.DescribeDelta(path, path));       // 无变化
        var delta = RoadTopology.DescribeDelta(path, y);
        Assert.NotEqual("", delta);
        Assert.Contains("路口 0→1", delta);
    }

    [Fact]
    public void Empty_is_safe()
    {
        var r = RoadTopology.Analyze(new List<(double, double)>(), new List<List<(int, double)>>());
        Assert.Empty(r.Segments);
        Assert.Equal(0, r.ComponentCount);
    }
}
