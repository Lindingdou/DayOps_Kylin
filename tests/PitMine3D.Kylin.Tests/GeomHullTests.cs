using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>凸包（境界圈定）回归。</summary>
public class GeomHullTests
{
    [Fact]
    public void Square_with_interior_point_hull_is_four_corners()
    {
        var pts = new List<(double x, double y)>
        {
            (0, 0), (4, 0), (4, 4), (0, 4), (2, 2)   // (2,2) 在内部
        };
        var hull = GeomHull.ConvexHull(pts);
        Assert.Equal(4, hull.Count);
        Assert.DoesNotContain((2, 2), hull);
    }

    [Fact]
    public void Collinear_excluded()
    {
        // 一条边上的中点不应进凸包
        var pts = new List<(double x, double y)>
        {
            (0, 0), (2, 0), (4, 0), (4, 4), (0, 4)
        };
        var hull = GeomHull.ConvexHull(pts);
        Assert.DoesNotContain((2, 0), hull);   // 共线中点被排除
        Assert.Equal(4, hull.Count);
    }

    [Fact]
    public void Too_few_points_returned_asis()
    {
        var pts = new List<(double x, double y)> { (0, 0), (1, 1) };
        Assert.Equal(2, GeomHull.ConvexHull(pts).Count);
    }
}
