using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>快速估值（网格配色）回归。</summary>
public class EstimationTests
{
    [Fact]
    public void Range_of_grid()
    {
        var g = new double[2, 2] { { 1, 3 }, { 2, 9 } };
        var (min, max) = Estimation.Range(g);
        Assert.Equal(1, min, 4);
        Assert.Equal(9, max, 4);
    }

    [Fact]
    public void BuildCells_one_rect_per_grid_node()
    {
        var g = new double[3, 3];
        var cells = Estimation.BuildCells(g, 0, 0, 1, 1, 0, 1);
        Assert.Equal(9, cells.Count);
        Assert.All(cells, c => Assert.IsType<PitMine3D.Kylin.Cad.Draw.RectEntity>(c));
    }

    [Fact]
    public void BuildCells_skips_NaN_unsupported_cells()
    {
        // 半径外(无数据支撑)单元置 NaN → 不渲染: 9 格里 3 个 NaN → 只出 6 方块
        var g = new double[3, 3];
        g[0, 0] = double.NaN; g[1, 1] = double.NaN; g[2, 2] = double.NaN;
        var cells = Estimation.BuildCells(g, 0, 0, 1, 1, 0, 1);
        Assert.Equal(6, cells.Count);
    }

    [Fact]
    public void Range_and_CountValid_ignore_NaN()
    {
        var g = new double[2, 2] { { 1, double.NaN }, { 2, 9 } };
        var (min, max) = Estimation.Range(g);
        Assert.Equal(1, min, 4);
        Assert.Equal(9, max, 4);            // NaN 不参与
        Assert.Equal(3, Estimation.CountValid(g));
    }

    [Fact]
    public void Range_all_NaN_is_zero_zero()
    {
        var g = new double[2, 2] { { double.NaN, double.NaN }, { double.NaN, double.NaN } };
        var (min, max) = Estimation.Range(g);
        Assert.Equal(0, min, 4);
        Assert.Equal(0, max, 4);
        Assert.Equal(0, Estimation.CountValid(g));
    }
}
