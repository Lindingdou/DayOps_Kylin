using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 路网交叉口打断（noding）回归 —— 忠实原 RoadGraphBuilder.NodePolylines：X 十字/T 丁字 跨段交叉打断插节点，
/// 使路网真正连通。对比：未 noding 的 Build 跨段交叉不连（此前 Kylin 退化行为）。
/// </summary>
public class RoadNodingTests
{
    private static IReadOnlyList<(double x, double y)> P(params (double x, double y)[] pts) => pts;

    [Fact]
    public void Cross_X_splits_into_four_and_connects()
    {
        var a = P((0, 0), (10, 0));      // 水平
        var b = P((5, -5), (5, 5));      // 竖直，交于 (5,0) 内部
        var noded = RoadNetwork.NodePolylines(new[] { a, b }, 1e-6);
        Assert.Equal(4, noded.Count);    // 各断成 2 段

        // noding 后：(0,0)→(5,5) 经交点连通
        var (nodes, adj) = RoadNetwork.BuildNoded(new[] { a, b }, 1e-6);
        int s = RoadNetwork.NearestNode(nodes, 0, 0), g = RoadNetwork.NearestNode(nodes, 5, 5);
        Assert.NotEmpty(RoadNetwork.Dijkstra(adj, s, g));
    }

    [Fact]
    public void Without_noding_cross_is_disconnected()
    {
        // 证退化：未 noding 的 Build，X 交叉不连（交点非任何折线顶点）。
        var a = P((0, 0), (10, 0));
        var b = P((5, -5), (5, 5));
        var (nodes, adj) = RoadNetwork.Build(new[] { a, b }, 1e-6);
        int s = RoadNetwork.NearestNode(nodes, 0, 0), g = RoadNetwork.NearestNode(nodes, 5, 5);
        Assert.Empty(RoadNetwork.Dijkstra(adj, s, g));    // 不连通
    }

    [Fact]
    public void Tee_junction_splits_through_line_and_connects()
    {
        var through = P((0, 0), (10, 0));   // 干线
        var branch = P((5, 0), (5, 5));     // 支线，端点 (5,0) 落干线中段
        var noded = RoadNetwork.NodePolylines(new[] { through, branch }, 1e-6);
        Assert.Equal(3, noded.Count);       // 干线断 2 + 支线 1（支线端点在交点，不断）

        var (nodes, adj) = RoadNetwork.BuildNoded(new[] { through, branch }, 1e-6);
        int s = RoadNetwork.NearestNode(nodes, 0, 0), g = RoadNetwork.NearestNode(nodes, 5, 5);
        Assert.NotEmpty(RoadNetwork.Dijkstra(adj, s, g));
    }

    [Fact]
    public void Parallel_lines_stay_separate()
    {
        var a = P((0, 0), (10, 0));
        var b = P((0, 5), (10, 5));         // 平行不交
        var noded = RoadNetwork.NodePolylines(new[] { a, b }, 1e-6);
        Assert.Equal(2, noded.Count);       // 无交点 → 不打断
        var (nodes, adj) = RoadNetwork.BuildNoded(new[] { a, b }, 1e-6);
        int s = RoadNetwork.NearestNode(nodes, 0, 0), g = RoadNetwork.NearestNode(nodes, 10, 5);
        Assert.Empty(RoadNetwork.Dijkstra(adj, s, g));   // 正确不连通
    }

    [Fact]
    public void Endpoint_touch_not_double_split()
    {
        // 两线已在端点相接 (10,0)：交点落端点 → 不额外打断，顶点合并即连通。
        var a = P((0, 0), (10, 0));
        var b = P((10, 0), (10, 10));
        var noded = RoadNetwork.NodePolylines(new[] { a, b }, 1e-6);
        Assert.Equal(2, noded.Count);       // 不打断
        var (nodes, adj) = RoadNetwork.BuildNoded(new[] { a, b }, 1e-6);
        int s = RoadNetwork.NearestNode(nodes, 0, 0), g = RoadNetwork.NearestNode(nodes, 10, 10);
        Assert.NotEmpty(RoadNetwork.Dijkstra(adj, s, g));
    }
}
