using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>道路中心线提取回归。</summary>
public class RoadToolsTests
{
    [Fact]
    public void NearestOnPolyline_projects_to_segment()
    {
        var poly = new List<(double x, double y)> { (0, 2), (10, 2) };
        var np = RoadTools.NearestOnPolyline(5, 0, poly);
        Assert.Equal(5, np.x, 4);
        Assert.Equal(2, np.y, 4);
    }

    [Fact]
    public void Centerline_of_parallel_edges_is_middle()
    {
        var a = new List<(double x, double y)> { (0, 0), (10, 0) };
        var b = new List<(double x, double y)> { (0, 2), (10, 2) };
        var mid = RoadTools.Centerline(a, b);
        Assert.Equal(2, mid.Count);
        Assert.Equal(1, mid[0].y, 4);      // 中线 y=1
        Assert.Equal(1, mid[1].y, 4);
        Assert.Equal(0, mid[0].x, 4);
        Assert.Equal(10, mid[1].x, 4);
    }

    [Fact]
    public void Centerline_handles_differing_vertex_counts()
    {
        var a = new List<(double x, double y)> { (0, 0), (5, 0), (10, 0) };   // 3 点
        var b = new List<(double x, double y)> { (0, 4), (10, 4) };           // 2 点
        var mid = RoadTools.Centerline(a, b);
        Assert.Equal(3, mid.Count);        // 跟随 A 的点数
        Assert.All(mid, p => Assert.Equal(2, p.y, 4));   // 中线 y=2
    }
}
