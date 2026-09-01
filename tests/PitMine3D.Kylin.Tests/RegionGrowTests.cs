using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云区域生长分割回归 —— 平面单区、陡折棱分两区(法向突变)、缓弯仍一区(平滑阈内)。</summary>
public class RegionGrowTests
{
    // 21×11 规则网格, z 由 zf(x,y) 给
    static List<(double x, double y, double z)> Grid(Func<double, double, double> zf)
    {
        var pts = new List<(double, double, double)>();
        for (int i = 0; i <= 20; i++)
            for (int j = 0; j <= 10; j++)
                pts.Add((i, j, zf(i, j)));
        return pts;
    }
    static int Idx(int i, int j) => i * 11 + j;

    [Fact]
    public void Flat_plane_is_a_single_region()
    {
        var pts = Grid((x, y) => 0.0);
        var r = RegionGrow.Segment(pts, k: 12, smoothnessDeg: 15, curvatureThreshold: 0.1, minSize: 10);
        Assert.Equal(1, r.RegionCount);
    }

    [Fact]
    public void Sharp_ridge_splits_into_two_surfaces()
    {
        // 帐篷形(陡)：x≤10 面 z=1.5x, x≥10 面 z=1.5(20−x); 二面夹角≈113°, 逐邻步法向跳变 >15° → 折棱处不并
        var pts = Grid((x, y) => x <= 10 ? 1.5 * x : 1.5 * (20 - x));
        var r = RegionGrow.Segment(pts, k: 12, smoothnessDeg: 15, curvatureThreshold: 0.1, minSize: 10);
        Assert.True(r.RegionCount >= 2, $"陡折棱应至少分两面, 实得 {r.RegionCount}");
        // A 面深处(2,5) 与 B 面深处(18,5) 属不同区(折棱确实把两面分开)
        int a = r.Label[Idx(2, 5)], b = r.Label[Idx(18, 5)];
        Assert.True(a >= 0 && b >= 0, "两面深处应各归一区");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Shallow_bend_below_smoothness_threshold_stays_one_region()
    {
        // 极缓弯：二面夹角≈4° < 平滑阈 15° → 视为同一光滑面, 不分
        var pts = Grid((x, y) => x <= 10 ? 0.05 * x : 1 - 0.05 * x);
        var r = RegionGrow.Segment(pts, k: 12, smoothnessDeg: 15, curvatureThreshold: 0.1, minSize: 10);
        Assert.Equal(1, r.RegionCount);
    }

    [Fact]
    public void Too_few_points_yields_no_regions()
    {
        var r = RegionGrow.Segment(new List<(double, double, double)> { (0, 0, 0), (1, 0, 0) });
        Assert.Equal(0, r.RegionCount);
    }
}
