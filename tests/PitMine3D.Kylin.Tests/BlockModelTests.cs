using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>块体模型 CSV 解析 + 品位配色回归。</summary>
public class BlockModelTests
{
    [Fact]
    public void Parses_blocks_and_grade_stats()
    {
        const string csv =
            "x,y,z,size,grade\n" +          // 表头跳过
            "0,0,0,2,1.0\n" +
            "2,0,0,2,3.0\n";
        var r = BlockModel.Parse(csv);
        Assert.True(r.Success, r.Error);
        Assert.Equal(2, r.Blocks.Count);
        Assert.Equal(2, r.Blocks[0].Size, 4);
        Assert.Equal(1.0, r.GradeMin, 4);
        Assert.Equal(3.0, r.GradeMax, 4);
        Assert.Equal(2.0, r.GradeMean, 4);
    }

    [Fact]
    public void GradeColor_low_blue_high_red()
    {
        var low = BlockModel.GradeColor(0, 0, 10);
        var high = BlockModel.GradeColor(10, 0, 10);
        Assert.True(low.b > low.r);      // 低品位=蓝主导
        Assert.True(high.r > high.b);    // 高品位=红主导
    }

    [Fact]
    public void BuildCells_one_rect_per_block()
    {
        var r = BlockModel.Parse("0,0,0,2,1\n5,5,0,2,2\n");
        var cells = BlockModel.BuildCells(r.Blocks, r.GradeMin, r.GradeMax);
        Assert.Equal(2, cells.Count);
        Assert.All(cells, c => Assert.IsType<PitMine3D.Kylin.Cad.Draw.RectEntity>(c));
    }

    [Fact]
    public void Empty_fails()
    {
        Assert.False(BlockModel.Parse("x,y,z\n说明,甲,乙\n").Success);
    }
}
