using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云质量统计回归（托管重算 PointCloudQualityStats 指标）。</summary>
public class PointCloudStatsTests
{
    [Fact]
    public void Bounds_area_density_meanz_stdz()
    {
        var pts = new List<(double x, double y, double z)>
        {
            (0, 0, 0), (10, 0, 0), (0, 10, 0), (10, 10, 10),
        };
        var s = PointCloudStats.Compute(pts);
        Assert.Equal(4, s.Count);
        Assert.Equal(0, s.MinX, 6); Assert.Equal(10, s.MaxX, 6);
        Assert.Equal(0, s.MinZ, 6); Assert.Equal(10, s.MaxZ, 6);
        Assert.Equal(100, s.AreaXY, 6);          // 10×10 包围盒
        Assert.Equal(0.04, s.DensityXY, 6);      // 4/100
        Assert.Equal(2.5, s.MeanZ, 6);           // (0+0+0+10)/4
        Assert.Equal(Math.Sqrt(18.75), s.StdZ, 6);   // 总体标准差 sqrt(75/4)
    }

    [Fact]
    public void Empty_all_zero()
    {
        var s = PointCloudStats.Compute(new List<(double, double, double)>());
        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.DensityXY, 6);
    }

    [Fact]
    public void Flat_cloud_zero_stdz()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 5), (1, 0, 5), (0, 1, 5), (1, 1, 5) };
        var s = PointCloudStats.Compute(pts);
        Assert.Equal(5, s.MeanZ, 6);
        Assert.Equal(0, s.StdZ, 6);
    }
}
