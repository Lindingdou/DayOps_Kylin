using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>工作帮坡角估算回归（点集拟合平面→坡角）。</summary>
public class SlopeEstimatorTests
{
    // z = x 平面（沿 +x 坡 45°），采一批点
    private static List<(double x, double y, double z)> PlaneZeqX()
    {
        var p = new List<(double, double, double)>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                p.Add((i, j, i));   // z=x
        return p;
    }

    [Fact]
    public void FitPlane_recovers_gradient()
    {
        var g = SlopeEstimator.FitPlane(PlaneZeqX());
        Assert.NotNull(g);
        Assert.Equal(1, g!.Value.a, 4);   // ∂z/∂x=1
        Assert.Equal(0, g!.Value.b, 4);   // ∂z/∂y=0
    }

    [Fact]
    public void Max_slope_of_z_eq_x_is_45()
    {
        Assert.Equal(45, SlopeEstimator.MaxSlopeDeg(PlaneZeqX())!.Value, 3);
    }

    [Fact]
    public void Slope_along_x_is_45_along_y_is_0()
    {
        Assert.Equal(45, SlopeEstimator.SlopeAlongDeg(PlaneZeqX(), 1, 0)!.Value, 3);
        Assert.Equal(0, SlopeEstimator.SlopeAlongDeg(PlaneZeqX(), 0, 1)!.Value, 3);
    }

    [Fact]
    public void Flat_plane_zero_slope()
    {
        var flat = new List<(double x, double y, double z)>();
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) flat.Add((i, j, 7));
        Assert.Equal(0, SlopeEstimator.MaxSlopeDeg(flat)!.Value, 4);
    }

    [Fact]
    public void Collinear_returns_null()
    {
        var line = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 1), (2, 0, 2) };
        Assert.Null(SlopeEstimator.FitPlane(line));   // 近共线 → 无唯一平面
    }
}
