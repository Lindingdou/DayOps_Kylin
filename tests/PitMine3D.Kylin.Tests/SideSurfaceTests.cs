using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>侧面三角网放样回归（弧长拉链缝合）。</summary>
public class SideSurfaceTests
{
    [Fact]
    public void Loft_two_parallel_lines_makes_strip()
    {
        // 顶线 z=10、底线 z=0, 各 3 点等距 → 拉链缝成 4 三角(2×2 段)
        var top = new List<(double, double, double)> { (0, 0, 10), (10, 0, 10), (20, 0, 10) };
        var bot = new List<(double, double, double)> { (0, 0, 0), (10, 0, 0), (20, 0, 0) };
        var (verts, tris) = SideSurface.Loft(top, bot, closed: false, flip: false);
        Assert.Equal(6, verts.Count);
        Assert.Equal(4, tris.Count);   // 3+3 点拉链 → (3-1)+(3-1)=4 三角
    }

    [Fact]
    public void Loft_area_matches_rectangle()
    {
        // 两条长 20 的线, 垂直间距(Z)10 → 直纹面总面积 = 20×10 = 200
        var top = new List<(double, double, double)> { (0, 0, 10), (20, 0, 10) };
        var bot = new List<(double, double, double)> { (0, 0, 0), (20, 0, 0) };
        var (verts, tris) = SideSurface.Loft(top, bot, false, false);
        var m = MeshMetrics.Compute(verts, tris);
        Assert.Equal(200.0, m.SurfaceArea, 4);
    }

    [Fact]
    public void Loft_aligns_reversed_bottom()
    {
        // 底线方向相反 → Align 应翻转, 不产生自交(面积仍 = 200)
        var top = new List<(double, double, double)> { (0, 0, 10), (20, 0, 10) };
        var bot = new List<(double, double, double)> { (20, 0, 0), (0, 0, 0) };   // 反向
        var (verts, tris) = SideSurface.Loft(top, bot, false, false);
        var m = MeshMetrics.Compute(verts, tris);
        Assert.Equal(200.0, m.SurfaceArea, 4);
    }

    [Fact]
    public void Loft_closed_rings_makes_tube()
    {
        // 顶/底方形环(z=10/0), 闭合 → 侧壁管(8 三角)
        var top = new List<(double, double, double)> { (0, 0, 10), (10, 0, 10), (10, 10, 10), (0, 10, 10) };
        var bot = new List<(double, double, double)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0) };
        var (_, tris) = SideSurface.Loft(top, bot, closed: true, flip: false);
        Assert.Equal(8, tris.Count);   // 4 边 × 2 三角
    }

    [Fact]
    public void Loft_too_few_points_empty()
    {
        var top = new List<(double, double, double)> { (0, 0, 0) };
        var bot = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0) };
        var (_, tris) = SideSurface.Loft(top, bot, false, false);
        Assert.Empty(tris);
    }
}
