using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 运输图模型(RoadGraph/RoadEdge/Validate)已知值回归 —— 移植原 Tests.RoadLib/RoadNetworkTests 中
/// 覆盖 §290 所移模型的子集(中线几何自动算 / 单双向邻接 / 校验连通性与合规 / 最近节点)。
/// 抽图 RoadGraphBuilder(noding, Kylin 已由 RoadNetwork.BuildNoded §82 覆盖)/序列化/Clone 等非移植部分不在此。
/// </summary>
public class RoadGraphModelTests
{
    [Fact]
    public void RoadEdge_ComputesLengthAndGradeFromCenterline()
    {
        // 水平 100m、升 10m → 坡度 10%, 三维长 ≈100.5m
        var e = new RoadEdge("E", "a", "b", new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 10) });
        Assert.InRange(e.GradePct, 9.9, 10.1);
        Assert.InRange(e.LengthM, 100.4, 100.6);
    }

    [Fact]
    public void TwoWayEdge_TraversableBothDirections()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddEdge(new RoadEdge("E", "a", "b"));
        Assert.Single(g.EdgesFrom("a"));
        Assert.Single(g.EdgesFrom("b"));   // 双向 → b 也能出发
    }

    [Fact]
    public void OneWayEdge_OnlyForward()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddEdge(new RoadEdge("E", "a", "b") { OneWay = true });
        Assert.Single(g.EdgesFrom("a"));
        Assert.Empty(g.EdgesFrom("b"));    // 单向 → b 出不去
    }

    [Fact]
    public void Validate_DetectsDisconnectedComponents()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(10, 0, 0));
        g.AddNode("c", RoadNodeType.Junction, new Point3d(500, 0, 0));
        g.AddNode("d", RoadNodeType.Junction, new Point3d(510, 0, 0));
        g.AddEdge(new RoadEdge("E1", "a", "b"));
        g.AddEdge(new RoadEdge("E2", "c", "d"));   // 与 a-b 不连通
        var rep = g.Validate();
        Assert.False(rep.IsFullyConnected);
        Assert.Equal(2, rep.ComponentCount);
    }

    [Fact]
    public void Validate_FlagsOverGradeAndIsolatedNode()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 20)); // 20% 坡
        g.AddNode("lonely", RoadNodeType.Junction, new Point3d(9, 9, 9)); // 孤立
        g.AddEdge(new RoadEdge("E", "a", "b"));
        var rep = g.Validate(maxGradePct: 10);
        Assert.False(rep.Ok);
        Assert.Contains("lonely", rep.IsolatedNodeIds);
        Assert.Contains(rep.Issues, s => s.Contains("纵坡"));
    }

    [Fact]
    public void NearestNode_ReturnsClosest()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        Assert.Equal("b", g.NearestNode(new Point3d(95, 3, 0))!.Id);
        Assert.Null(g.NearestNode(new Point3d(95, 3, 0), maxDistM: 1.0)); // 超容差
    }
}
