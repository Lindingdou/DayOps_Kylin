using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>路网连通性诊断回归（连通分量 + 片间最窄缺口）。</summary>
public class RoadConnectivityTests
{
    [Fact]
    public void Components_single_connected()
    {
        var poly = new List<(double x, double y)> { (0, 0), (10, 0), (20, 0) };
        var (_, adj) = RoadNetwork.Build(new[] { (IReadOnlyList<(double x, double y)>)poly }, 1e-6);
        RoadConnectivity.Components(adj, out int c);
        Assert.Equal(1, c);
    }

    [Fact]
    public void Components_two_separate()
    {
        var p1 = new List<(double x, double y)> { (0, 0), (10, 0) };
        var p2 = new List<(double x, double y)> { (0, 100), (10, 100) };
        var (_, adj) = RoadNetwork.Build(new[]
        {
            (IReadOnlyList<(double x, double y)>)p1, (IReadOnlyList<(double x, double y)>)p2
        }, 1e-6);
        RoadConnectivity.Components(adj, out int c);
        Assert.Equal(2, c);
    }

    [Fact]
    public void AllGaps_finds_gap_between_two_lines()
    {
        // 两条水平线, 端点间隔 5m（在 maxGap 内）
        var p1 = new List<(double x, double y)> { (0, 0), (10, 0) };
        var p2 = new List<(double x, double y)> { (15, 0), (25, 0) };
        var (nodes, adj) = RoadNetwork.Build(new[]
        {
            (IReadOnlyList<(double x, double y)>)p1, (IReadOnlyList<(double x, double y)>)p2
        }, 1e-6);
        var gaps = RoadConnectivity.AllGaps(nodes, adj, maxGapM: 50, stepM: 10);
        Assert.NotEmpty(gaps);
        Assert.Equal(5.0, gaps[0].GapM, 6);            // (10,0)→(15,0) 最近, 距 5
    }

    [Fact]
    public void AllGaps_empty_when_beyond_maxgap()
    {
        var p1 = new List<(double x, double y)> { (0, 0), (10, 0) };
        var p2 = new List<(double x, double y)> { (1000, 0), (1010, 0) };
        var (nodes, adj) = RoadNetwork.Build(new[]
        {
            (IReadOnlyList<(double x, double y)>)p1, (IReadOnlyList<(double x, double y)>)p2
        }, 1e-6);
        Assert.Empty(RoadConnectivity.AllGaps(nodes, adj, maxGapM: 50, stepM: 10));
    }

    [Fact]
    public void AllGaps_sorted_ascending()
    {
        // 三条线：A 与 B 隔 5, B 与 C 隔 8 → 缺口升序 5,8
        var a = new List<(double x, double y)> { (0, 0), (10, 0) };
        var b = new List<(double x, double y)> { (15, 0), (25, 0) };
        var c = new List<(double x, double y)> { (33, 0), (43, 0) };
        var (nodes, adj) = RoadNetwork.Build(new[]
        {
            (IReadOnlyList<(double x, double y)>)a, (IReadOnlyList<(double x, double y)>)b, (IReadOnlyList<(double x, double y)>)c
        }, 1e-6);
        var gaps = RoadConnectivity.AllGaps(nodes, adj, maxGapM: 50, stepM: 10);
        Assert.True(gaps.Count >= 2);
        for (int i = 1; i < gaps.Count; i++) Assert.True(gaps[i - 1].GapM <= gaps[i].GapM);
    }
}
