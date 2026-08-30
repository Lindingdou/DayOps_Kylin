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
}
