using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>路网寻径回归。</summary>
public class RoadNetworkTests
{
    private static List<(double x, double y)> Poly(params (double x, double y)[] p) => new(p);

    [Fact]
    public void Path_along_single_polyline()
    {
        var (nodes, adj) = RoadNetwork.Build(new[] { Poly((0, 0), (1, 0), (2, 0)) }, 1e-6);
        Assert.Equal(3, nodes.Count);
        var path = RoadNetwork.Dijkstra(adj, 0, 2);
        Assert.Equal(new List<int> { 0, 1, 2 }, path);
    }

    [Fact]
    public void Junction_merges_and_connects()
    {
        // 两段在 (1,0) 相接 → 合并为交点，(0,0)→(1,1) 连通
        var (nodes, adj) = RoadNetwork.Build(new[]
        {
            Poly((0, 0), (1, 0)),
            Poly((1, 0), (1, 1)),
        }, 1e-6);
        Assert.Equal(3, nodes.Count);        // (0,0)(1,0)(1,1)
        int s = RoadNetwork.NearestNode(nodes, 0, 0);
        int g = RoadNetwork.NearestNode(nodes, 1, 1);
        var path = RoadNetwork.Dijkstra(adj, s, g);
        Assert.Equal(3, path.Count);
    }

    [Fact]
    public void Disconnected_is_unreachable()
    {
        var (nodes, adj) = RoadNetwork.Build(new[]
        {
            Poly((0, 0), (1, 0)),
            Poly((10, 10), (11, 10)),
        }, 1e-6);
        var path = RoadNetwork.Dijkstra(adj, 0, RoadNetwork.NearestNode(nodes, 10, 10));
        Assert.Empty(path);
    }

    [Fact]
    public void PathLength_sums_segments()
    {
        var (nodes, adj) = RoadNetwork.Build(new[] { Poly((0, 0), (3, 0), (3, 4)) }, 1e-6);
        var path = RoadNetwork.Dijkstra(adj, 0, 2);
        Assert.Equal(7, RoadNetwork.PathLength(nodes, path), 4);   // 3 + 4
    }

    [Fact]
    public void Chooses_shorter_of_two_routes()
    {
        // 菱形: 0→1→3 (长) vs 0→2→3 (短)
        var (nodes, adj) = RoadNetwork.Build(new[]
        {
            Poly((0, 0), (1, 5), (2, 0)),   // 上路(远)
            Poly((0, 0), (1, 0), (2, 0)),   // 下路(近, 直线)
        }, 1e-6);
        int s = RoadNetwork.NearestNode(nodes, 0, 0);
        int g = RoadNetwork.NearestNode(nodes, 2, 0);
        var path = RoadNetwork.Dijkstra(adj, s, g);
        // 应走下路(经 (1,0))
        Assert.Contains(RoadNetwork.NearestNode(nodes, 1, 0), path);
    }
}
