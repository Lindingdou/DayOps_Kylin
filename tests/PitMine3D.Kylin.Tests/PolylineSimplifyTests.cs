using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>Douglas-Peucker 简化回归。</summary>
public class PolylineSimplifyTests
{
    [Fact]
    public void Collinear_reduces_to_endpoints()
    {
        var pts = new List<(double x, double y)> { (0, 0), (1, 0), (2, 0), (3, 0), (10, 0) };
        var s = PolylineSimplify.DouglasPeucker(pts, 0.01);
        Assert.Equal(2, s.Count);              // 共线 → 只剩两端
        Assert.Equal((0, 0), s[0]);
        Assert.Equal((10, 0), s[^1]);
    }

    [Fact]
    public void Sharp_corner_kept()
    {
        var pts = new List<(double x, double y)> { (0, 0), (5, 5), (10, 0) };   // 尖角
        var s = PolylineSimplify.DouglasPeucker(pts, 0.5);
        Assert.Equal(3, s.Count);              // 角点保留
    }

    [Fact]
    public void Large_tolerance_flattens()
    {
        var pts = new List<(double x, double y)> { (0, 0), (5, 0.1), (10, 0) };   // 微凸
        var s = PolylineSimplify.DouglasPeucker(pts, 1.0);
        Assert.Equal(2, s.Count);              // 容差大 → 中点删
    }
}
