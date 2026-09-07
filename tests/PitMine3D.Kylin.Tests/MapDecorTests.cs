using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>制图装饰（指北针/比例尺）回归。</summary>
[Collection("TextGeometry")]
public class MapDecorTests
{
    [Fact]
    public void NorthArrow_points_up_with_N_label()
    {
        var ents = MapDecor.NorthArrow(0, 0, 10);
        var txt = ents.OfType<TextEntity>().Single();
        Assert.Equal("N", txt.Text);
        var lines = ents.OfType<LineEntity>().ToList();
        Assert.Equal(3, lines.Count);                       // 杆 + 两翼
        var shaft = lines[0];
        Assert.True(shaft.Y1 > shaft.Y0);                   // 箭杆指上(北)
        Assert.True(txt.Y > shaft.Y1);                      // "N" 在箭头上方
    }

    [Fact]
    public void ScaleBar_has_bar_ticks_and_length_label()
    {
        var ents = MapDecor.ScaleBar(0, 0, 100, 5);
        var lines = ents.OfType<LineEntity>().ToList();
        var texts = ents.OfType<TextEntity>().ToList();
        Assert.Equal(4, lines.Count);                       // 主条 + 左/右/中刻度
        Assert.Contains(texts, t => t.Text == "0");
        Assert.Contains(texts, t => t.Text == "100");       // 长度标签
        var bar = lines[0];
        Assert.Equal(100, System.Math.Abs(bar.X1 - bar.X0), 6);   // 条长=100
    }

    [Fact]
    public void TitleBlock_has_frame_title_and_field_labels()
    {
        var ents = MapDecor.TitleBlock(0, 0, 100, 30, 3, "平朔露天矿", "1:1000");
        var texts = ents.OfType<TextEntity>().ToList();
        Assert.Contains(texts, t => t.Text == "平朔露天矿");          // 标题
        Assert.Contains(texts, t => t.Text.Contains("比例") && t.Text.Contains("1:1000"));
        Assert.Contains(texts, t => t.Text == "制图");
        Assert.Contains(texts, t => t.Text == "日期");
        var lines = ents.OfType<LineEntity>().ToList();
        Assert.True(lines.Count >= 7);                               // 4 外框 + 2 横分隔 + 1 竖分隔
    }

    [Fact]
    public void TitleBlock_empty_title_uses_placeholder()
    {
        var ents = MapDecor.TitleBlock(0, 0, 100, 30, 3, "", "");
        Assert.Contains(ents.OfType<TextEntity>(), t => t.Text == "标题");   // 占位
        Assert.Empty(MapDecor.TitleBlock(0, 0, 0, 30, 3, "x", ""));          // 零宽返空
    }

    [Theory]
    [InlineData(87, 50)]     // 8.7 → 5×10(向下取整以放进目标宽度)
    [InlineData(43, 20)]     // 4.3 → 2×10
    [InlineData(23, 20)]     // 2.3 → 2×10
    [InlineData(1234, 1000)] // 1.234 → 1×1000
    [InlineData(6.5, 5)]     // 6.5 → 5×1
    public void NiceLength_rounds_down_to_1_2_5_x_10n(double target, double expected)
    {
        Assert.Equal(expected, MapDecor.NiceLength(target), 6);
    }
}
