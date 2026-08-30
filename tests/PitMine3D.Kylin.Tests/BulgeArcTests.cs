using System;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>LwPolyline 凸度→圆弧插值回归。</summary>
public class BulgeArcTests
{
    [Fact]
    public void Semicircle_interior_points_lie_on_circle_and_bulge_left()
    {
        // b=1 → 半圆；弦(0,0)-(2,0) 向左(+y)鼓 → 圆心(1,0) r=1
        var pts = BulgeArc.Interior(0, 0, 2, 0, 1.0, 8);
        Assert.Equal(7, pts.Count);                       // n=8 → i=1..7
        foreach (var p in pts)
        {
            double d = Math.Sqrt((p.x - 1) * (p.x - 1) + p.y * p.y);
            Assert.Equal(1.0, d, 3);                      // 落在圆上
            Assert.True(p.y > 0, "b>0 应向 +y 鼓");
        }
    }

    [Fact]
    public void Zero_bulge_is_straight_no_points()
    {
        Assert.Empty(BulgeArc.Interior(0, 0, 10, 0, 0, 8));
    }

    [Fact]
    public void Negative_bulge_arcs_to_other_side()
    {
        var pts = BulgeArc.Interior(0, 0, 2, 0, -1.0, 8);
        Assert.All(pts, p => Assert.True(p.y < 0, "b<0 应向 -y 鼓"));
    }

    [Fact]
    public void Midpoint_of_semicircle_is_apex()
    {
        var pts = BulgeArc.Interior(0, 0, 2, 0, 1.0, 8);
        var mid = pts[3];                                 // i=4 → t=0.5 → 弧顶
        Assert.Equal(1.0, mid.x, 3);
        Assert.Equal(1.0, mid.y, 3);
    }
}
