using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>等高线 Marching Squares 回归。</summary>
public class ContourTests
{
    [Fact]
    public void Tilted_plane_gives_vertical_contour()
    {
        // z = x（grid[ix,iy]=ix），level=1.5 → 等值线是过 x=1.5 的竖直段
        int n = 4;
        var g = new double[n, n];
        for (int ix = 0; ix < n; ix++)
            for (int iy = 0; iy < n; iy++)
                g[ix, iy] = ix;
        var segs = Contour.MarchingSquares(g, 0, 0, 1, 1, 1.5);
        Assert.NotEmpty(segs);
        Assert.All(segs, s =>
        {
            Assert.Equal(1.5, s.x0, 3);   // 两端 x 都 =1.5
            Assert.Equal(1.5, s.x1, 3);
        });
    }

    [Fact]
    public void Level_above_all_gives_no_contour()
    {
        var g = new double[3, 3];   // 全 0
        var segs = Contour.MarchingSquares(g, 0, 0, 1, 1, 5.0);
        Assert.Empty(segs);
    }

    [Fact]
    public void Level_crosses_produces_segments()
    {
        // 中心高四周低的锥形 → level 中间值应产生闭合环(多段)
        var g = new double[3, 3]
        {
            { 0, 0, 0 },
            { 0, 2, 0 },
            { 0, 0, 0 },
        };
        var segs = Contour.MarchingSquares(g, 0, 0, 1, 1, 1.0);
        Assert.True(segs.Count >= 4, $"segs={segs.Count}");   // 环绕中心的若干段
    }

    [Fact]
    public void IdwAt_returns_exact_on_point_and_blends_between()
    {
        var pts = new System.Collections.Generic.List<(double x, double y, double z)> { (0, 0, 10), (10, 0, 20) };
        Assert.Equal(10, Contour.IdwAt(pts, 0, 0), 4);        // 落在点上 → 精确
        double mid = Contour.IdwAt(pts, 5, 0);
        Assert.Equal(15, mid, 4);                              // 中点等权 → 15
    }

    [Fact]
    public void GridFromPoints_reproduces_samples_at_corner_nodes()
    {
        // 2x2 网格节点恰为 4 个采样点 → IDW 在点上返回精确 z
        var pts = new System.Collections.Generic.List<(double x, double y, double z)>
        {
            (0, 0, 10), (10, 0, 20), (0, 10, 30), (10, 10, 40)
        };
        var g = Contour.GridFromPoints(pts, 2, 2, out double x0, out double y0, out double dx, out double dy);
        Assert.Equal(0, x0, 4); Assert.Equal(10, dx, 4); Assert.Equal(10, dy, 4);
        Assert.Equal(10, g[0, 0], 3); Assert.Equal(20, g[1, 0], 3);
        Assert.Equal(30, g[0, 1], 3); Assert.Equal(40, g[1, 1], 3);
    }

    // ── NN(最近邻) / MA(移动平均) 估值网格 ──
    [Fact]
    public void Nearest_grid_takes_closest_sample_value()
    {
        // 两点: (0,0,v=5), (10,0,v=9)。格 x0=0 dx=5, 3 列: 0(近5), 5(等距→先者5), 10(近9)
        var pts = new List<(double x, double y, double z)> { (0, 0, 5), (10, 0, 9) };
        var g = Contour.GridNearest(pts, 3, 1, 0, 0, 5, 1);
        Assert.Equal(5, g[0, 0], 6);          // x=0 → 近 (0,0)=5
        Assert.Equal(9, g[2, 0], 6);          // x=10 → 近 (10,0)=9
        // 块状：非样本处直接取最近值(不插值)，g[0]≠g[2] 且都是原始样本值
        Assert.Contains(g[1, 0], new[] { 5.0, 9.0 });
    }

    [Fact]
    public void MovingAverage_grid_averages_within_radius_else_nearest()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 10), (2, 0, 20), (100, 0, 99) };
        // 格点 (1,0) 半径 5 内含 (0,0)=10 与 (2,0)=20 → 均值 15；不含远点 99
        var g = Contour.GridMovingAverage(pts, 1, 1, 1, 0, 1, 1, radius: 5);
        Assert.Equal(15.0, g[0, 0], 6);
        // 半径内无点 → 最近邻兜底
        var g2 = Contour.GridMovingAverage(pts, 1, 1, 50, 0, 1, 1, radius: 5);
        Assert.Equal(20.0, g2[0, 0], 6);      // (50,0) 最近是 (2,0)=20
    }

    // ── 等高线高程层表(等高距 vs auto) ──
    [Fact]
    public void Levels_interval_gives_round_elevations()
    {
        // z∈[2,23], 等高距 5 → 整数倍处 5/10/15/20 (23 不含, <zmax)
        var lv = Contour.Levels(2, 23, interval: 5);
        Assert.Equal(new[] { 5.0, 10, 15, 20 }, lv);
    }

    [Fact]
    public void Levels_interval_first_at_or_above_zmin()
    {
        // z∈[100,118], 等高距 5 → 100/105/110/115 (100 恰整数倍, 含)
        var lv = Contour.Levels(100, 118, interval: 5);
        Assert.Equal(new[] { 100.0, 105, 110, 115 }, lv);
    }

    [Fact]
    public void Levels_auto_ten_when_no_interval()
    {
        var lv = Contour.Levels(0, 11, interval: 0);   // auto → 10 层, zmin+step·k
        Assert.Equal(10, lv.Count);
        Assert.All(lv, v => Assert.InRange(v, 0, 11));
        for (int i = 1; i < lv.Count; i++) Assert.True(lv[i] > lv[i - 1]);   // 升序
    }

    [Fact]
    public void Levels_caps_and_degenerate_safe()
    {
        Assert.Empty(Contour.Levels(5, 5, 1));                    // 无起伏 → 空
        Assert.True(Contour.Levels(0, 1e6, interval: 0.001, maxLevels: 300).Count <= 300);   // 间距过小被封顶
    }
}
