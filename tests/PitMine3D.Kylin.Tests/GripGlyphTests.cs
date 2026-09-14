using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>夹点方块（AutoCAD 样式：实心 + 三态配色）的画法回归。</summary>
public class GripGlyphTests
{
    /// <summary>把交错 P3_C3 缓冲拆成「线段」列表。</summary>
    private static List<(double x0, double y0, double x1, double y1, float r, float g, float b)> Segs(List<float> o)
    {
        var list = new List<(double, double, double, double, float, float, float)>();
        for (int i = 0; i + 11 < o.Count; i += 12)
            list.Add((o[i], o[i + 1], o[i + 6], o[i + 7], o[i + 3], o[i + 4], o[i + 5]));
        return list;
    }

    [Fact]
    public void Grip_is_filled_not_just_an_outline()
    {
        var o = new List<float>();
        GripGlyph.Append(o, 0, 0, 1, 0.1f, 0.45f, 0.95f);
        var segs = Segs(o);

        Assert.Equal(GripGlyph.SegmentsPerGrip, segs.Count);

        // 填充段 = 横贯方块的水平线；只有描边的话这类线段只会有上下两条
        var horizontalSpans = segs.Count(s => s.y0 == s.y1 && s.x0 == -1 && s.x1 == 1);
        Assert.True(horizontalSpans >= GripGlyph.FillLines,
            $"实心方块该有 ≥{GripGlyph.FillLines} 条横向填充线, 实际 {horizontalSpans} —— 退回描边就说明填充丢了");
    }

    [Fact]
    public void Fill_lines_span_the_whole_square_evenly()
    {
        var o = new List<float>();
        GripGlyph.Append(o, 5, 7, 2, 1, 1, 1);
        // 只取填充色那批：上下两条描边也是横贯方块的水平段，混进来会算出 0 间距
        var ys = Segs(o).Where(s => s.y0 == s.y1 && s.x0 == 3 && s.x1 == 7 && s.r == 1f)
                        .Select(s => s.y0).OrderBy(v => v).ToList();
        Assert.Equal(GripGlyph.FillLines + 1, ys.Count);

        Assert.Equal(5.0, ys.First(), 6);       // 底边 cy-h
        Assert.Equal(9.0, ys.Last(), 6);        // 顶边 cy+h
        for (int i = 1; i < ys.Count; i++)      // 等距, 不留缝（缓冲是 float，比到 5 位就够）
            Assert.Equal(4.0 / GripGlyph.FillLines, ys[i] - ys[i - 1], 5);
    }

    [Fact]
    public void Square_bounds_are_centre_plus_minus_half_size()
    {
        var o = new List<float>();
        GripGlyph.Append(o, -3, 4, 0.5, 1, 1, 1);
        var segs = Segs(o);
        Assert.Equal(-3.5, segs.Min(s => System.Math.Min(s.x0, s.x1)), 6);
        Assert.Equal(-2.5, segs.Max(s => System.Math.Max(s.x0, s.x1)), 6);
        Assert.Equal(3.5, segs.Min(s => System.Math.Min(s.y0, s.y1)), 6);
        Assert.Equal(4.5, segs.Max(s => System.Math.Max(s.y0, s.y1)), 6);
    }

    /// <summary>三态必须是 AutoCAD 那三个颜色，且互不相同（分不清状态等于没做）。</summary>
    [Fact]
    public void Three_states_use_blue_green_red_and_differ()
    {
        var cold = GripGlyph.Color(selected: false, hovered: false);
        var warm = GripGlyph.Color(selected: false, hovered: true);
        var hot = GripGlyph.Color(selected: true, hovered: false);

        Assert.True(cold.b > cold.r && cold.b > cold.g, "冷态该以蓝为主");
        Assert.True(warm.g > warm.r && warm.g > warm.b, "暖态(悬停)该以绿为主");
        Assert.True(hot.r > hot.g && hot.r > hot.b, "热态(已选)该以红为主");

        Assert.NotEqual(cold, warm);
        Assert.NotEqual(cold, hot);
        Assert.NotEqual(warm, hot);
    }

    /// <summary>已选优先于悬停：拖着一个已选夹点时不该因为光标压着就变绿。</summary>
    [Fact]
    public void Selected_beats_hovered()
        => Assert.Equal(GripGlyph.Color(selected: true, hovered: false),
                        GripGlyph.Color(selected: true, hovered: true));

    [Fact]
    public void Edge_is_a_darker_shade_of_the_fill()
    {
        var o = new List<float>();
        GripGlyph.Append(o, 0, 0, 1, 0.10f, 0.45f, 0.95f);
        var segs = Segs(o);

        var fill = segs.First(s => s.y0 == s.y1 && s.x0 == -1 && s.x1 == 1 && s.y0 > -1 && s.y0 < 1);
        var edge = segs.First(s => s.x0 == -1 && s.x1 == -1);      // 左边框(竖直段)
        Assert.True(edge.r < fill.r + 1e-6 && edge.g < fill.g && edge.b < fill.b, "描边该比填充深");
        Assert.Equal(fill.g * GripGlyph.EdgeShade, edge.g, 5);
    }

    [Fact]
    public void Degenerate_size_does_not_produce_nonsense()
    {
        var o = new List<float>();
        GripGlyph.Append(o, 1, 1, 0, 1, 1, 1);                     // 半边长 0
        var segs = Segs(o);
        Assert.Equal(GripGlyph.SegmentsPerGrip, segs.Count);
        Assert.All(segs, s => Assert.Equal(1.0, s.x0, 6));
    }
}
