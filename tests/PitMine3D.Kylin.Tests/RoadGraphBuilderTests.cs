using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 属性图抽取(RoadGraphBuilder.FromPolylines)已知值回归 —— 逐字移植原 Tests.RoadLib/RoadNetworkTests 的
/// 抽图 / noding(X十字/T丁字/立交Z闸门/共线去重)/ 缺口桥接 部分(等价性由构造)。Z 感知 noding 为 2D §80/§82 所无。
/// </summary>
public class RoadGraphBuilderTests
{
    private static IReadOnlyList<Point3d>[] Lines(params IReadOnlyList<Point3d>[] ls) => ls;

    [Fact]
    public void Builder_SharedEndpoints_MergeToOneNode()
    {
        var plines = Lines(
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 5) },
            new[] { new Point3d(100, 0, 5), new Point3d(200, 50, 12) }); // 共享 (100,0,5)
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(3, g.NodeCount);   // 不是 4:共享端点并成一个
        Assert.Equal(2, g.EdgeCount);
    }

    [Fact]
    public void Builder_StackedSameXyDifferentZ_NotMerged()
    {
        var plines = Lines(
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 0) },
            new[] { new Point3d(100, 0, 0), new Point3d(100, 0, 30) }); // 同 XY、Z 差 30 → 不并
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(3, g.NodeCount);
    }

    [Fact]
    public void Builder_ProducesTraversableGraph()
    {
        var plines = Lines(
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 5) },
            new[] { new Point3d(100, 0, 5), new Point3d(200, 50, 12) });
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        var start = g.NearestNode(new Point3d(0, 0, 0))!;
        var end = g.NearestNode(new Point3d(200, 50, 12))!;
        var r = new DijkstraPathSolver(g).FindPath(start.Id, end.Id, PathQuery.Default);
        Assert.True(r.Feasible);
        Assert.Equal(2, r.EdgeIds.Count);
    }

    [Fact]
    public void Noding_CrossIntersection_SplitsIntoFourEdges()
    {
        // 十字交叉、同标高 → 两线各打断成 2 段, 交点并成 1 个共享节点。
        var plines = Lines(
            new[] { new Point3d(-50, 0, 10), new Point3d(50, 0, 10) },
            new[] { new Point3d(0, -50, 10), new Point3d(0, 50, 10) });
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(4, g.EdgeCount);
        Assert.Equal(5, g.NodeCount);                  // 4 端点 + 1 交点
        Assert.Equal(1, g.Validate().ComponentCount);  // 连通
    }

    [Fact]
    public void Noding_TJunction_SplitsThroughLine()
    {
        var plines = Lines(
            new[] { new Point3d(-50, 0, 10), new Point3d(50, 0, 10) },
            new[] { new Point3d(0, 0, 10), new Point3d(0, 50, 10) });
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(3, g.EdgeCount);
        Assert.Equal(4, g.NodeCount);
        Assert.Equal(1, g.Validate().ComponentCount);
    }

    [Fact]
    public void Noding_Overpass_NotSplit()
    {
        // 立交:XY 相交但标高差 30m → 不打断, 保持两条不连通的边。
        var plines = Lines(
            new[] { new Point3d(-50, 0, 0), new Point3d(50, 0, 0) },
            new[] { new Point3d(0, -50, 30), new Point3d(0, 50, 30) });
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(2, g.EdgeCount);
        Assert.Equal(4, g.NodeCount);
        Assert.Equal(2, g.Validate().ComponentCount);  // 上下层不连通
    }

    [Fact]
    public void Noding_CollinearOverlap_DedupsDuplicateEdge()
    {
        var plines = Lines(
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 0) },
            new[] { new Point3d(30, 0, 0), new Point3d(70, 0, 0) });
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 2.0);
        Assert.Equal(1, rep.DuplicateEdgesRemoved);
        Assert.Equal(3, g.EdgeCount);                  // 0-30 / 30-70 / 70-100, 无重复
        Assert.Equal(4, g.NodeCount);
        Assert.Equal(1, g.Validate().ComponentCount);
    }

    [Fact]
    public void Bridge_ConnectsNearGapBetweenComponents()
    {
        var plines = Lines(
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 0) },
            new[] { new Point3d(115, 0, 0), new Point3d(200, 0, 0) });   // 缺口 15m
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(2, rep.ComponentsBeforeBridge);
        Assert.Equal(1, rep.BridgesAdded);
        Assert.Equal(1, g.Validate().ComponentCount);   // 桥接后全连通
    }

    [Fact]
    public void Bridge_SkipsGapAcrossElevation()
    {
        var plines = Lines(
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 0) },
            new[] { new Point3d(115, 0, 30), new Point3d(200, 0, 30) });
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(0, rep.BridgesAdded);
        Assert.Equal(2, g.Validate().ComponentCount);
    }

    [Fact]
    public void Bridge_RespectsGapLimit()
    {
        var plines = Lines(
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 0) },
            new[] { new Point3d(140, 0, 0), new Point3d(200, 0, 0) });   // 缺口 40m > 25
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(0, rep.BridgesAdded);
        Assert.Equal(2, g.Validate().ComponentCount);
    }

    [Fact]
    public void Bridge_SpurToTrunkMiddle_SplitsAndConnects()
    {
        var plines = Lines(
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 0) },   // 干线
            new[] { new Point3d(50, 12, 0), new Point3d(50, 60, 0) });  // 支线, 端点离干线 12m
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(1, rep.BridgesAdded);
        Assert.Equal(1, g.Validate().ComponentCount);
        Assert.Equal(4, g.EdgeCount);   // 干线断成 2 + 支线 1 + 桥接 1
    }

    [Fact]
    public void NodingReport_TalliesSplits()
    {
        var plines = Lines(
            new[] { new Point3d(-50, 0, 10), new Point3d(50, 0, 10) },
            new[] { new Point3d(0, -50, 10), new Point3d(0, 50, 10) });
        RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 2.0);
        Assert.Equal(2, rep.InputLines);
        Assert.Equal(1, rep.CrossSplits);              // 一个十字交点
        Assert.Equal(4, rep.OutputSegments);
    }
}
