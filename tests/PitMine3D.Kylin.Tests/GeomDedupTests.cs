using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>去重核回归（点集容差去重 / 折线同一几何判定）。</summary>
public class GeomDedupTests
{
    [Fact]
    public void KeepAfterDedup_removes_coincident()
    {
        var pts = new List<(double, double)> { (0, 0), (0.0005, 0), (1, 1), (1, 1) };
        var keep = GeomDedup.KeepAfterDedup(pts, 1e-3);
        Assert.Equal(2, keep.Count);   // (0,0)吸收(0.0005,0); (1,1)吸收重复
        Assert.Equal(0, keep[0]);
        Assert.Equal(2, keep[1]);
    }

    [Fact]
    public void KeepAfterDedup_keeps_distinct()
    {
        var pts = new List<(double, double)> { (0, 0), (1, 0), (2, 0) };
        Assert.Equal(3, GeomDedup.KeepAfterDedup(pts, 1e-3).Count);
    }

    [Fact]
    public void SamePolyline_forward_match()
    {
        var a = new List<(double, double)> { (0, 0), (1, 0), (1, 1) };
        var b = new List<(double, double)> { (0, 0), (1, 0), (1, 1) };
        Assert.True(GeomDedup.SamePolyline(a, false, b, false, 1e-3));
    }

    [Fact]
    public void SamePolyline_reversed_match()
    {
        var a = new List<(double, double)> { (0, 0), (1, 0), (1, 1) };
        var b = new List<(double, double)> { (1, 1), (1, 0), (0, 0) };   // 反向
        Assert.True(GeomDedup.SamePolyline(a, false, b, false, 1e-3));
    }

    [Fact]
    public void SamePolyline_differs_on_closed_flag()
    {
        var a = new List<(double, double)> { (0, 0), (1, 0), (1, 1) };
        var b = new List<(double, double)> { (0, 0), (1, 0), (1, 1) };
        Assert.False(GeomDedup.SamePolyline(a, false, b, true, 1e-3));
    }

    [Fact]
    public void SamePolyline_differs_on_count_and_geometry()
    {
        var a = new List<(double, double)> { (0, 0), (1, 0), (1, 1) };
        var b = new List<(double, double)> { (0, 0), (1, 0) };
        Assert.False(GeomDedup.SamePolyline(a, false, b, false, 1e-3));
        var c = new List<(double, double)> { (0, 0), (1, 0), (5, 5) };
        Assert.False(GeomDedup.SamePolyline(a, false, c, false, 1e-3));
    }
}
