using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>栅格形态学/连通域回归。</summary>
public class RasterMorphologyTests
{
    // 5×5 grid helper
    private static bool[] Grid(int nx, int ny, params (int x, int y)[] on)
    {
        var m = new bool[nx * ny];
        foreach (var (x, y) in on) m[y * nx + x] = true;
        return m;
    }

    [Fact]
    public void Dilate_grows_by_one_ring()
    {
        var m = Grid(5, 5, (2, 2));
        var d = RasterMorphology.Dilate(m, 5, 5, 1);
        Assert.Equal(9, d.Count(b => b));   // 中心 + 8 邻
    }

    [Fact]
    public void Erode_removes_thin_isolated()
    {
        var m = Grid(5, 5, (2, 2));
        var e = RasterMorphology.Erode(m, 5, 5, 1);
        Assert.Equal(0, e.Count(b => b));   // 单点被腐蚀掉
    }

    [Fact]
    public void Open_removes_speck_keeps_block()
    {
        // 3×3 实块 + 1 孤点; 开运算(r=1)保块去点
        var m = Grid(7, 7, (1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (2, 3), (3, 1), (3, 2), (3, 3), (5, 5));
        var o = RasterMorphology.Open(m, 7, 7, 1);
        Assert.True(o[2 * 7 + 2]);          // 块中心保留
        Assert.False(o[5 * 7 + 5]);         // 孤点抹掉
    }

    [Fact]
    public void FillHoles_fills_interior_gap()
    {
        // 5×5 环, 中心空 → 填洞后中心变实
        var on = new List<(int, int)>();
        for (int x = 1; x <= 3; x++) for (int y = 1; y <= 3; y++) if (!(x == 2 && y == 2)) on.Add((x, y));
        var m = Grid(5, 5, on.ToArray());
        RasterMorphology.FillHoles(m, 5, 5);
        Assert.True(m[2 * 5 + 2]);
    }

    [Fact]
    public void Components_counts_two_separate_blocks()
    {
        var m = Grid(7, 7, (0, 0), (1, 0), (5, 6), (6, 6));
        var comps = RasterMorphology.Components(m, 7, 7, 1);
        Assert.Equal(2, comps.Count);
    }

    [Fact]
    public void Components_min_cells_filters_small()
    {
        var m = Grid(7, 7, (0, 0), (1, 0), (5, 6));   // 一块 2 格, 一块 1 格
        var comps = RasterMorphology.Components(m, 7, 7, 2);
        Assert.Single(comps);   // 只有 2 格块过 minCells=2
    }

    [Fact]
    public void TraceBoundary_of_block_is_nonempty_closedish()
    {
        var on = new List<(int, int)>();
        for (int x = 1; x <= 3; x++) for (int y = 1; y <= 3; y++) on.Add((x, y));
        var m = Grid(5, 5, on.ToArray());
        var ring = RasterMorphology.TraceBoundary(m, 5, 5);
        Assert.True(ring.Count >= 4);
    }
}
