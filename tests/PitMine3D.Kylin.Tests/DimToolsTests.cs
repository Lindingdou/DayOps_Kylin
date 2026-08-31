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

    [Fact]
    public void Radial_line_reaches_circumference_with_R_text()
    {
        // 圆心(0,0) 半径5，方向 +X → 径向线终点在 (5,0)，文字 "R5"
        var dim = DimTools.BuildRadial(0, 0, 5, 1, 0, 1);
        var line = Assert.IsType<LineEntity>(dim[0]);
        Assert.Equal(0, line.X0, 4); Assert.Equal(0, line.Y0, 4);
        Assert.Equal(5, line.X1, 4); Assert.Equal(0, line.Y1, 4);
        var tx = Assert.IsType<TextEntity>(dim[^1]);
        Assert.Equal("R5", tx.Text);
    }

    [Fact]
    public void Radial_has_arrowhead_and_text()
    {
        // 径向线 + 2 箭头线 + 文字 = 4 实体
        var dim = DimTools.BuildRadial(1, 1, 2, 0, 1, 0.5);
        Assert.Equal(4, dim.Count);
        Assert.Equal("R2", ((TextEntity)dim[^1]).Text);
    }

    // ── 标注样式(DIM 变量) ──
    [Fact]
    public void DimStyle_decimal_places_control_text_format()
    {
        var s0 = new DimStyle { DecimalPlaces = 0 };
        Assert.Equal("12", ((TextEntity)DimTools.Build(0, 0, 12.34, 0, 1, s0)[^1]).Text);   // 0 位=整数
        var s3 = new DimStyle { DecimalPlaces = 3 };
        Assert.Equal("12.34", ((TextEntity)DimTools.Build(0, 0, 12.34, 0, 1, s3)[^1]).Text); // 3 位(末尾零裁剪)
        var s1 = new DimStyle { DecimalPlaces = 1 };
        Assert.Equal("12.3", ((TextEntity)DimTools.Build(0, 0, 12.34, 0, 1, s1)[^1]).Text);  // 1 位
    }

    [Fact]
    public void DimStyle_fixed_text_height_overrides_passed_h()
    {
        var s = new DimStyle { TextHeight = 7.0 };
        var tx = (TextEntity)DimTools.Build(0, 0, 10, 0, 1.0, s)[^1];   // 传入 h=1 但固定高 7
        Assert.Equal(7.0, tx.Height, 6);
        // TextHeight=0 → 用传入 h
        var tx2 = (TextEntity)DimTools.Build(0, 0, 10, 0, 3.0, new DimStyle { TextHeight = 0 })[^1];
        Assert.Equal(3.0, tx2.Height, 6);
    }

    [Fact]
    public void DimStyle_arrow_ratio_scales_radial_arrow()
    {
        // 箭头比越大 → 箭头线越长(端点到箭底距离)。比对两种比例的箭头线长。
        double LenOf(DimStyle st)
        {
            var dim = DimTools.BuildRadial(0, 0, 10, 1, 0, 5, st);   // H=5
            var a = (LineEntity)dim[1];                               // 第一条箭头线
            double dx = a.X1 - a.X0, dy = a.Y1 - a.Y0;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }
        Assert.True(LenOf(new DimStyle { ArrowRatio = 1.0 }) > LenOf(new DimStyle { ArrowRatio = 0.3 }), "箭头比大→箭头长");
    }

    [Fact]
    public void DimStyle_default_preserves_legacy_behavior()
    {
        // 默认样式与旧行为一致(2 位裁剪 → "0.##")
        Assert.Equal(DimStyle.Default.DecimalPlaces, 2);
        Assert.Equal("2.5", ((TextEntity)DimTools.Build(0, 0, 2.5, 0, 1)[^1]).Text);
        Assert.Equal("10", ((TextEntity)DimTools.Build(0, 0, 10, 0, 1)[^1]).Text);
    }
}
