using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云去噪 SOR/ROR 回归。</summary>
public class PointDenoiseTests
{
    // 紧凑簇 (间距 1) + 一个远飞点
    private static List<(double x, double y, double z)> ClusterPlusOutlier()
    {
        var p = new List<(double, double, double)>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                p.Add((i, j, 0));       // 25 点紧凑网格
        p.Add((1000, 1000, 1000));      // 远飞点
        return p;
    }

    [Fact]
    public void Sor_removes_far_outlier()
    {
        var kept = PointDenoise.Sor(ClusterPlusOutlier(), k: 8, stdMul: 1.0);
        Assert.DoesNotContain((1000.0, 1000.0, 1000.0), kept);   // 飞点被剔
        Assert.True(kept.Count >= 24);                           // 簇基本保留
    }

    [Fact]
    public void Ror_removes_isolated_point()
    {
        var kept = PointDenoise.Ror(ClusterPlusOutlier(), radius: 1.5, minNeighbors: 3);
        Assert.DoesNotContain((1000.0, 1000.0, 1000.0), kept);   // 孤点半径内无邻 → 剔
        Assert.True(kept.Count >= 20);
    }

    [Fact]
    public void Sor_too_few_points_keeps_all()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0) };
        Assert.Equal(2, PointDenoise.Sor(pts, 8, 1.0).Count);    // 点 ≤ k+1 不滤
    }

    [Fact]
    public void Empty_returns_empty()
    {
        Assert.Empty(PointDenoise.Sor(new List<(double, double, double)>(), 8, 1.0));
        Assert.Empty(PointDenoise.Ror(new List<(double, double, double)>(), 1.0, 3));
    }
}
