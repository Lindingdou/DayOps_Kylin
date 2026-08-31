using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>高程截断回归（Z 百分位波段裁剪）。</summary>
public class PointZClipTests
{
    private static List<(double x, double y, double z)> ZSeq(params double[] zs)
    {
        var l = new List<(double, double, double)>();
        foreach (var z in zs) l.Add((0, 0, z));
        return l;
    }

    [Fact]
    public void Clips_extreme_low_and_high()
    {
        // z: 0,1,2,...,100 均匀; [p2,p98] ≈ [2,98] → 剔除两端极值
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i <= 100; i++) pts.Add((i, 0, i));
        var (kept, zLo, zHi, removed) = PointZClip.Clip(pts, 0.02, 0.98);
        Assert.Equal(2.0, zLo, 6);
        Assert.Equal(98.0, zHi, 6);
        Assert.True(removed >= 4);                 // 两端各 ~2 个被剔
        Assert.All(kept, p => Assert.InRange(p.z, 2.0, 98.0));
    }

    [Fact]
    public void Full_range_keeps_all()
    {
        var pts = ZSeq(1, 5, 9, 20, 3);
        var (kept, _, _, removed) = PointZClip.Clip(pts, 0.0, 1.0);
        Assert.Equal(5, kept.Count);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void Empty_input()
    {
        var (kept, _, _, removed) = PointZClip.Clip(new List<(double x, double y, double z)>(), 0.02, 0.98);
        Assert.Empty(kept);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void Removes_single_outlier()
    {
        // 一堆 z≈10 + 一个 z=1000 飞点; 高百分位剪掉飞点
        var pts = ZSeq(9, 10, 10, 11, 10, 9, 10, 11, 10, 1000);
        var (kept, _, zHi, removed) = PointZClip.Clip(pts, 0.0, 0.9);
        Assert.True(zHi < 1000);          // 上界不含飞点
        Assert.DoesNotContain(kept, p => p.z > 900);
        Assert.True(removed >= 1);
    }
}
