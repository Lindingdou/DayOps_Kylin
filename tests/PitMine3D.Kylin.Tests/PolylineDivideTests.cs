using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>定数/定距等分回归。</summary>
public class PolylineDivideTests
{
    private static List<(double x, double y)> Line10 => new() { (0, 0), (10, 0) };

    [Fact]
    public void Divide_into_five_gives_four_points()
    {
        var m = PolylineDivide.Divide(Line10, 5);
        Assert.Equal(4, m.Count);
        Assert.Equal(2, m[0].x, 4);
        Assert.Equal(8, m[3].x, 4);
    }

    [Fact]
    public void Measure_every_three()
    {
        var m = PolylineDivide.Measure(Line10, 3);
        Assert.Equal(3, m.Count);              // 3,6,9
        Assert.Equal(3, m[0].x, 4);
        Assert.Equal(9, m[2].x, 4);
    }

    [Fact]
    public void Divide_follows_arclength_across_segments()
    {
        // L 形 (0,0)-(4,0)-(4,4)，总长 8，等分 2 → 中点在 (4,0)
        var poly = new List<(double x, double y)> { (0, 0), (4, 0), (4, 4) };
        var m = PolylineDivide.Divide(poly, 2);
        Assert.Single(m);
        Assert.Equal(4, m[0].x, 4); Assert.Equal(0, m[0].y, 4);
    }
}
