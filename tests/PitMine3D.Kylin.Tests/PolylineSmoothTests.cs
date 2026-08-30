using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>Chaikin 曲线平滑回归。</summary>
public class PolylineSmoothTests
{
    [Fact]
    public void Open_preserves_endpoints_and_adds_points()
    {
        var pl = new List<(double x, double y)> { (0, 0), (10, 0), (10, 10) };
        var sm = PolylineSmooth.Chaikin(pl, 1, false);
        Assert.True(sm.Count > pl.Count);
        Assert.Equal((0, 0), sm[0]);        // 首端保留
        Assert.Equal((10, 10), sm[^1]);     // 末端保留
    }

    [Fact]
    public void Closed_wraps_no_endpoint_pin()
    {
        var sq = new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        var sm = PolylineSmooth.Chaikin(sq, 1, true);
        Assert.Equal(8, sm.Count);          // 闭合每段生 2 点 → 4*2
    }

    [Fact]
    public void Iterations_increase_density()
    {
        var pl = new List<(double x, double y)> { (0, 0), (10, 0), (10, 10) };
        int c1 = PolylineSmooth.Chaikin(pl, 1, false).Count;
        int c3 = PolylineSmooth.Chaikin(pl, 3, false).Count;
        Assert.True(c3 > c1);
    }
}
