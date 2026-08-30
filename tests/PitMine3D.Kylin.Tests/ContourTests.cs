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
}
