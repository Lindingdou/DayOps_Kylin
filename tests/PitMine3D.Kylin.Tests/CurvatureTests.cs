using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>地表曲率回归。</summary>
public class CurvatureTests
{
    [Fact]
    public void Flat_zero_curvature()
    {
        var g = new double[4, 4];
        var c = Curvature.Compute(g, 1);
        foreach (var v in c) Assert.Equal(0, v, 6);
    }

    [Fact]
    public void Pit_center_positive_curvature()
    {
        // 中心低(0)四邻高(1) → 拉普拉斯 = 4 → 正曲率(凹)
        var g = new double[3, 3]
        {
            { 1, 1, 1 },
            { 1, 0, 1 },
            { 1, 1, 1 },
        };
        var c = Curvature.Compute(g, 1);
        Assert.Equal(4, c[1, 1], 4);
    }

    [Fact]
    public void Peak_center_negative_curvature()
    {
        var g = new double[3, 3]
        {
            { 0, 0, 0 },
            { 0, 2, 0 },
            { 0, 0, 0 },
        };
        var c = Curvature.Compute(g, 1);
        Assert.Equal(-8, c[1, 1], 4);   // 0+0+0+0 - 4*2 = -8
    }
}
