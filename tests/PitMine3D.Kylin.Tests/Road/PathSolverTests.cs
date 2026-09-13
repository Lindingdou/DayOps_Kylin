// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/PathSolverTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// Dijkstra 寻径行为测试。核心场景：坑底→地表两条路——
///   折返路 A(A1..A4，每段 ≤8%) vs 直连路 B(13.3%，超 10% 限坡)。
/// </summary>
public class PathSolverTests
{
    private static RoadGraph PitDumpGraph()
    {
        var g = new RoadGraph();
        g.AddNode("pit", RoadNodeType.Loading, new Point3d(0, 0, -60));
        g.AddNode("N1", RoadNodeType.Junction, new Point3d(200, 0, -45));
        g.AddNode("N2", RoadNodeType.Junction, new Point3d(200, 250, -25));
        g.AddNode("N3", RoadNodeType.Junction, new Point3d(0, 250, -10));
        g.AddNode("dump", RoadNodeType.Unloading, new Point3d(0, 450, 0));
        g.AddEdge(new RoadEdge("A1", "pit", "N1"));   // 7.5%
        g.AddEdge(new RoadEdge("A2", "N1", "N2"));    // 8.0%
        g.AddEdge(new RoadEdge("A3", "N2", "N3"));    // 7.5%
        g.AddEdge(new RoadEdge("A4", "N3", "dump"));  // 5.0%
        g.AddEdge(new RoadEdge("B1", "pit", "dump")); // 13.3%（超 10%）
        return g;
    }

    [Fact]
    public void ShortestDistance_NoGradeLimit_PicksDirectRoute()
    {
        var solver = new DijkstraPathSolver(PitDumpGraph());
        var r = solver.FindPath("pit", "dump", new PathQuery { Mode = WeightMode.Distance });
        Assert.True(r.Feasible);
        Assert.Equal(new[] { "B1" }, r.EdgeIds);   // 直连最短
    }

    [Fact]
    public void GradeLimit_ForcesSwitchbackRoute()
    {
        var solver = new DijkstraPathSolver(PitDumpGraph());
        var r = solver.FindPath("pit", "dump",
            new PathQuery { Mode = WeightMode.Distance, MaxGradePct = 10 });
        Assert.True(r.Feasible);
        Assert.Equal(new[] { "A1", "A2", "A3", "A4" }, r.EdgeIds); // 直连被限坡拒 → 折返
    }

    // 回归：缓存 key 必须含 MaxGradePct，否则限坡查询会错误命中不限坡的旧缓存。
    [Fact]
    public void CacheKey_DifferentGradeLimit_GivesDifferentResult()
    {
        var solver = new DijkstraPathSolver(PitDumpGraph());
        var noLimit = solver.FindPath("pit", "dump", new PathQuery { Mode = WeightMode.Distance });
        var limited = solver.FindPath("pit", "dump",
            new PathQuery { Mode = WeightMode.Distance, MaxGradePct = 10 });
        Assert.Equal(new[] { "B1" }, noLimit.EdgeIds);
        Assert.Equal(new[] { "A1", "A2", "A3", "A4" }, limited.EdgeIds); // 不能是缓存的 B1
    }

    [Fact]
    public void ClosedEdge_AfterInvalidate_MakesUnreachable()
    {
        var g = PitDumpGraph();
        var solver = new DijkstraPathSolver(g);
        // 关闭折返路一段；直连又超坡 → 无路可走
        var a2 = g.GetEdge("A2")!;
        a2.Status = RoadEdgeStatus.Closed;
        g.UpdateEdge(a2);
        solver.Invalidate();
        var r = solver.FindPath("pit", "dump",
            new PathQuery { Mode = WeightMode.Distance, MaxGradePct = 10 });
        Assert.False(r.Feasible);
    }

    [Fact]
    public void OverloadEdge_RejectedWhenLoaded()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Loading, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Unloading, new Point3d(100, 0, 0));
        g.AddEdge(new RoadEdge("E", "a", "b") { MaxLoadT = 50 }); // 限载 50t
        var solver = new DijkstraPathSolver(g);
        var truck = new TruckProfile { PayloadT = 90 };           // 90t 重车超限
        Assert.False(solver.FindPath("a", "b", new PathQuery { Truck = truck, Loaded = true }).Feasible);
        Assert.True(solver.FindPath("a", "b", new PathQuery { Truck = truck, Loaded = false }).Feasible); // 空车可过
    }

    [Fact]
    public void OdMatrix_ComputesAllPairs()
    {
        var solver = new DijkstraPathSolver(PitDumpGraph());
        var od = solver.BuildMatrix(new[] { "pit" }, new[] { "dump" },
            new PathQuery { Mode = WeightMode.Cost, MaxGradePct = 10 });
        Assert.Single(od.Sources);
        Assert.True(od.Equiv[0, 0] > 0 && !double.IsInfinity(od.Equiv[0, 0]));
    }

    [Fact]
    public void PathMetrics_EquivExceedsActualLength_OnGradedRoute()
    {
        var solver = new DijkstraPathSolver(PitDumpGraph());
        var r = solver.FindPath("pit", "dump",
            new PathQuery { Mode = WeightMode.Distance, MaxGradePct = 10, Loaded = true });
        Assert.True(r.EquivM > r.LengthM);   // 重车上坡 → 等效运距 > 实际里程
        Assert.True(r.TimeMin > 0 && r.Cost > 0);
    }

    // Yen K 最短路：pit→dump 只有 2 条简单路（直连 B1 / 折返 A1-A4），按里程升序。
    [Fact]
    public void KShortest_FindsTwoDistinctRoutesByLength()
    {
        var solver = new DijkstraPathSolver(PitDumpGraph());
        var paths = solver.FindKShortest("pit", "dump", new PathQuery { Mode = WeightMode.Distance }, 3);

        Assert.Equal(2, paths.Count);                                       // 只有 2 条简单路
        Assert.Equal(new[] { "B1" }, paths[0].EdgeIds);                     // 最短=直连
        Assert.Equal(new[] { "A1", "A2", "A3", "A4" }, paths[1].EdgeIds);   // 备选=折返
        Assert.True(paths[0].LengthM < paths[1].LengthM);
    }
}
