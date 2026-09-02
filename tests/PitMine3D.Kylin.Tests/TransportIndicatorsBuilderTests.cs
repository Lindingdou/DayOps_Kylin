using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 运输指标计算器(TransportIndicatorsBuilder)已知值回归 —— 逐字移植原 Tests.RoadLib/TransportIndicatorsTests
/// (等价性由构造保证)。源汇=Loading×Unloading; 一条共享干线 L1—J1—U1 / L1—J1—U2(e1 介数 2)。
/// </summary>
public class TransportIndicatorsBuilderTests
{
    private static RoadGraph BuildGraph()
    {
        var g = new RoadGraph();
        g.AddNode(new RoadNode("L1", RoadNodeType.Loading, new Point3d(0, 0, 0)) { ThroughputTph = 1000 });
        g.AddNode(new RoadNode("J1", RoadNodeType.Junction, new Point3d(100, 0, 0)));
        g.AddNode(new RoadNode("U1", RoadNodeType.Unloading, new Point3d(200, 0, 0)) { ThroughputTph = 500 });
        g.AddNode(new RoadNode("U2", RoadNodeType.Unloading, new Point3d(100, 100, 0)) { ThroughputTph = 300 });
        g.AddEdge(new RoadEdge("e1", "L1", "J1"));
        g.AddEdge(new RoadEdge("e2", "J1", "U1"));
        g.AddEdge(new RoadEdge("e3", "J1", "U2"));
        return g;
    }

    [Fact]
    public void Compute_ResolvesLoadingUnloadingSourcesSinks()
    {
        var ind = TransportIndicatorsBuilder.Compute(BuildGraph(), new List<RoadGraph>(), TruckProfile.Default);
        Assert.False(ind.UsedAllNodesFallback);
        Assert.Equal(new[] { "L1" }, ind.Sources);
        Assert.Equal(2, ind.Sinks.Count);
        Assert.Contains("U1", ind.Sinks);
        Assert.Contains("U2", ind.Sinks);
        Assert.NotNull(ind.Od);
    }

    [Fact]
    public void Compute_NoLoadUnload_FallsBackToAllNodes()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddEdge(new RoadEdge("e", "a", "b"));
        var ind = TransportIndicatorsBuilder.Compute(g, new List<RoadGraph>(), TruckProfile.Default);
        Assert.True(ind.UsedAllNodesFallback);
        Assert.Equal(2, ind.Sources.Count);   // 降级=全节点
        Assert.Equal(2, ind.Sinks.Count);
    }

    [Fact]
    public void Compute_TheoreticalCapacity_IsMinOfSourceAndSinkThroughput()
    {
        var ind = TransportIndicatorsBuilder.Compute(BuildGraph(), new List<RoadGraph>(), TruckProfile.Default);
        Assert.Equal(1000, ind.SourceThroughputSumTph, 3);   // Σ源 = L1
        Assert.Equal(800, ind.SinkCapacitySumTph, 3);        // Σ汇 = U1+U2
        Assert.Equal(800, ind.TheoreticalCapacityTph, 3);    // min
    }

    [Fact]
    public void Compute_HaulDistances_FlatEquivEqualsLength()
    {
        var ind = TransportIndicatorsBuilder.Compute(BuildGraph(), new List<RoadGraph>(), TruckProfile.Default);
        // 平路等效=实距:L1→U1 与 L1→U2 各 200m → 平均/最大均 ≈ 200
        Assert.InRange(ind.AvgEquivM, 199, 201);
        Assert.InRange(ind.MaxEquivM, 199, 201);
    }

    [Fact]
    public void Compute_Bottleneck_SharedTrunkHasHighestBetweenness()
    {
        var ind = TransportIndicatorsBuilder.Compute(BuildGraph(), new List<RoadGraph>(), TruckProfile.Default);
        Assert.NotEmpty(ind.Bottlenecks);
        // e1(L1—J1)被两条最短路共用 → 介数 2、瓶颈分最高, 排第一
        Assert.Equal("e1", ind.Bottlenecks[0].EdgeId);
        Assert.Equal(2, ind.Bottlenecks[0].Betweenness);
        var e2 = ind.Bottlenecks.First(b => b.EdgeId == "e2");
        Assert.Equal(1, e2.Betweenness);
    }

    [Fact]
    public void Compute_WeightedAvg_IsStubbed()
    {
        var ind = TransportIndicatorsBuilder.Compute(BuildGraph(), new List<RoadGraph>(), TruckProfile.Default);
        Assert.Null(ind.WeightedAvgEquivM);   // 吨量加权留桩, 待采矿模型
    }

    [Fact]
    public void Compute_PerPeriodSeries_OnePerSnapshot()
    {
        var snaps = new List<RoadGraph> { BuildGraph(), BuildGraph() };
        var ind = TransportIndicatorsBuilder.Compute(BuildGraph(), snaps, TruckProfile.Default);
        Assert.Equal(2, ind.PerPeriodSeries.Count);
        Assert.Equal(3, ind.PerPeriodSeries[0].EdgeCount);   // 每期 3 条边
    }
}
