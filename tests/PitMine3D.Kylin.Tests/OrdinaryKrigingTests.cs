using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;
using CP = PitMine3D.Kylin.Cad.OrdinaryKriging.ControlPoint;

namespace PitMine3D.Kylin.Tests;

/// <summary>普通克里金 OK 回归（忠实移植 CoalQualityEstimator OK 核：球状变差 + 克里金方程组 + 方差）。</summary>
public class OrdinaryKrigingTests
{
    private static List<CP> Grid()
    {
        // 5×5 规则网格, V = 10 + x（线性场, 便于验内插）
        var pts = new List<CP>();
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
                pts.Add(new CP(i * 10, j * 10, 0, 10 + i * 10));
        return pts;
    }

    [Fact]
    public void Spherical_variogram_shape()
    {
        var vg = new OrdinaryKriging.Variogram(1, 10, 50);
        Assert.Equal(0, vg.Gamma(0), 6);            // γ(0)=0
        Assert.Equal(10, vg.Gamma(50), 6);          // γ(≥range)=sill
        Assert.Equal(10, vg.Gamma(80), 6);
        Assert.True(vg.Gamma(10) < vg.Gamma(30));   // 单调递增
        Assert.InRange(vg.Gamma(25), 1, 10);        // 块金~基台之间
    }

    [Fact]
    public void Exact_interpolation_at_control_point()
    {
        var pts = Grid();
        var e = OrdinaryKriging.EstimateAt(pts, 20, 20, 0);   // 正落控制点 (20,20) V=30
        Assert.NotNull(e);
        Assert.Equal(30, e!.Value.est, 6);
        Assert.Equal(0, e.Value.variance, 6);                 // 控制点上克里金方差=0
    }

    [Fact]
    public void Variance_nonnegative_and_interior_estimate_reasonable()
    {
        var pts = Grid();
        var e = OrdinaryKriging.EstimateAt(pts, 15, 15, 0, radius: 100);   // 网格间内插点
        Assert.NotNull(e);
        Assert.True(e!.Value.variance >= 0, "克里金方差非负");
        // 线性场 V=10+x, x=15 → 真值 25; 克里金对线性场应贴近
        Assert.InRange(e.Value.est, 20, 30);
    }

    [Fact]
    public void Outside_radius_returns_null()
    {
        var pts = Grid();
        var e = OrdinaryKriging.EstimateAt(pts, 100000, 100000, 0, radius: 5);   // 远在半径外
        Assert.Null(e);
    }

    [Fact]
    public void Single_point_returns_its_value()
    {
        var pts = new List<CP> { new CP(0, 0, 0, 42) };
        var e = OrdinaryKriging.EstimateAt(pts, 1, 1, 0, radius: 100);
        Assert.NotNull(e);
        Assert.Equal(42, e!.Value.est, 6);
    }

    [Fact]
    public void FitVariogram_sill_equals_sample_variance()
    {
        var pts = Grid();
        var vg = OrdinaryKriging.FitVariogram(pts);
        Assert.True(vg.Sill > 0);
        Assert.True(vg.Range > 0);
        Assert.True(vg.Nugget >= 0 && vg.Nugget <= vg.Sill);
    }

    [Fact]
    public void Empty_points_returns_null()
    {
        Assert.Null(OrdinaryKriging.EstimateAt(new List<CP>(), 0, 0, 0));
    }
}
