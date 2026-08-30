using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>OD 运距矩阵基石回归（单源全网最短距）。</summary>
public class OdMatrixTests
{
    // 一条链 0-1-2-3, 每段长 10（沿 x 轴）
    private static (List<(double x, double y)> nodes, List<List<(int to, double w)>> adj) Chain()
    {
        var poly = new List<(double x, double y)> { (0, 0), (10, 0), (20, 0), (30, 0) };
        return RoadNetwork.Build(new[] { (IReadOnlyList<(double x, double y)>)poly }, 1e-6);
    }

    [Fact]
    public void DijkstraDistances_along_chain()
    {
        var (nodes, adj) = Chain();
        int s = RoadNetwork.NearestNode(nodes, 0, 0);
        var d = RoadNetwork.DijkstraDistances(adj, s);
        Assert.Equal(0.0, d[RoadNetwork.NearestNode(nodes, 0, 0)], 6);
        Assert.Equal(10.0, d[RoadNetwork.NearestNode(nodes, 10, 0)], 6);
        Assert.Equal(30.0, d[RoadNetwork.NearestNode(nodes, 30, 0)], 6);
    }

    [Fact]
    public void DijkstraDistances_unreachable_is_infinity()
    {
        // 两段互不相连的路
        var p1 = new List<(double x, double y)> { (0, 0), (10, 0) };
        var p2 = new List<(double x, double y)> { (100, 100), (110, 100) };
        var (nodes, adj) = RoadNetwork.Build(new[]
        {
            (IReadOnlyList<(double x, double y)>)p1, (IReadOnlyList<(double x, double y)>)p2
        }, 1e-6);
        int s = RoadNetwork.NearestNode(nodes, 0, 0);
        var d = RoadNetwork.DijkstraDistances(adj, s);
        int far = RoadNetwork.NearestNode(nodes, 110, 100);
        Assert.True(double.IsInfinity(d[far]));
    }

    [Fact]
    public void DijkstraDistances_symmetric_matrix()
    {
        var (nodes, adj) = Chain();
        int a = RoadNetwork.NearestNode(nodes, 0, 0);
        int b = RoadNetwork.NearestNode(nodes, 30, 0);
        var da = RoadNetwork.DijkstraDistances(adj, a);
        var db = RoadNetwork.DijkstraDistances(adj, b);
        Assert.Equal(da[b], db[a], 6);   // 无向图对称
        Assert.Equal(30.0, da[b], 6);
    }

    [Fact]
    public void DijkstraDistances_bad_start_all_infinity()
    {
        var (_, adj) = Chain();
        var d = RoadNetwork.DijkstraDistances(adj, -1);
        foreach (var v in d) Assert.True(double.IsInfinity(v));
    }
}
