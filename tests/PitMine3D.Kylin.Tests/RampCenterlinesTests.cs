using System;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>参数化坑线中线回归（螺旋/折返几何校核）。</summary>
public class RampCenterlinesTests
{
    [Fact]
    public void Spiral_points_lie_on_circle_of_radius()
    {
        var pts = RampCenterlines.Spiral(0, 0, 100, radius: 50, startAngleDeg: 0, turns: 2, ccw: true, gradePct: 8);
        Assert.True(pts.Count > 8);
        foreach (var (x, y, _) in pts)
            Assert.Equal(50.0, Math.Sqrt(x * x + y * y), 6);   // 定半径
    }

    [Fact]
    public void Spiral_descends_by_grade_over_arc()
    {
        // 下降量 = 纵坡 × 总弧长 = 0.08 × (2·2π·50)
        var pts = RampCenterlines.Spiral(0, 0, 100, 50, 0, 2, true, 8);
        double expectDrop = 0.08 * (2 * 2 * Math.PI * 50);
        Assert.Equal(100.0, pts[0].Z, 6);
        Assert.Equal(100.0 - expectDrop, pts[^1].Z, 4);
    }

    [Fact]
    public void Spiral_ccw_vs_cw_opposite_first_step()
    {
        var ccw = RampCenterlines.Spiral(0, 0, 0, 50, 0, 1, true, 0);
        var cw = RampCenterlines.Spiral(0, 0, 0, 50, 0, 1, false, 0);
        // 起点相同(角0), 第二点 y 符号相反(逆时针 +y, 顺时针 −y)
        Assert.True(ccw[1].Y > 0);
        Assert.True(cw[1].Y < 0);
    }

    [Fact]
    public void Spiral_invalid_returns_empty()
    {
        Assert.Empty(RampCenterlines.Spiral(0, 0, 0, radius: 0, startAngleDeg: 0, turns: 2, ccw: true, gradePct: 8));
        Assert.Empty(RampCenterlines.Spiral(0, 0, 0, 50, 0, turns: 0, ccw: true, gradePct: 8));
    }

    [Fact]
    public void Switchback_leg_direction_reverses_each_leg()
    {
        // 方位 0(沿 +X), 3 腿 → 首腿朝 +X, 次腿朝 −X
        var pts = RampCenterlines.Switchback(0, 0, 100, azimuthDeg: 0, turnSide: +1, legs: 3,
            legLength: 100, gradePct: 8, curveGradePct: 4, radius: 20);
        Assert.True(pts.Count > 3);
        // 首腿末端 x≈100(沿 +X 走完一腿)
        Assert.True(pts[^1].Z < 100);   // 全程下降
    }

    [Fact]
    public void Switchback_invalid_returns_empty()
    {
        Assert.Empty(RampCenterlines.Switchback(0, 0, 0, 0, +1, legs: 1, legLength: 100, gradePct: 8, curveGradePct: 4, radius: 20));
        Assert.Empty(RampCenterlines.Switchback(0, 0, 0, 0, +1, legs: 3, legLength: 0, gradePct: 8, curveGradePct: 4, radius: 20));
    }
}
