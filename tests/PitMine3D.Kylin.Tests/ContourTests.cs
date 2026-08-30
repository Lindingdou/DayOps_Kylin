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
}
