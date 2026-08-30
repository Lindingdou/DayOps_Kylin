using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>线性标注回归。</summary>
public class DimToolsTests
{
    [Fact]
    public void Builds_line_ticks_and_distance_text()
    {
        var dim = DimTools.Build(0, 0, 10, 0, 1);
        // 尺寸线 + 2 端刻度 + 文字 = 4 实体
        Assert.Equal(4, dim.Count);
        var tx = Assert.IsType<TextEntity>(dim[3]);
        Assert.Equal("10", tx.Text);            // 距离文字
    }

    [Fact]
    public void Distance_text_formats_decimals()
    {
        var dim = DimTools.Build(0, 0, 3, 4, 1);   // 距离 5
        var tx = (TextEntity)dim[^1];
        Assert.Equal("5", tx.Text);
    }

    [Fact]
    public void Zero_length_only_line()
    {
        var dim = DimTools.Build(2, 2, 2, 2, 1);
        Assert.Single(dim);                        // 退化 → 只线
    }
}
