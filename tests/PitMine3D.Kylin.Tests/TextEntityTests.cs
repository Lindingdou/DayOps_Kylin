using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>单笔画文字 + TextEntity 回归。</summary>
public class TextEntityTests
{
    private static float MinY(List<float> o) { float m = float.MaxValue; for (int i = 1; i < o.Count; i += 6) if (o[i] < m) m = o[i]; return m; }

    [Fact]
    public void Multiline_text_stacks_lines_downward()
    {
        var single = new List<float>(); new TextEntity { X = 0, Y = 0, Height = 1, Text = "8" }.Tessellate(single);
        var multi = new List<float>(); new TextEntity { X = 0, Y = 0, Height = 1, Text = "8\n8" }.Tessellate(multi);
        Assert.Equal(2 * single.Count, multi.Count);              // 两行 = 两倍段
        Assert.True(MinY(multi) < MinY(single) - 0.5);            // 第二行落在首行下方
    }

    [Fact]
    public void Single_line_text_unchanged_by_multiline_support()
    {
        var o = new List<float>(); new TextEntity { X = 0, Y = 0, Height = 1, Text = "8" }.Tessellate(o);
        Assert.Equal(7 * 2 * 6, o.Count);                        // 数字 8 = 7 段, 单行不受多行改动影响
    }

    [Fact]
    public void Digit_eight_has_seven_segments()
    {
        Assert.Equal(7, StrokeFont.Strokes('8').Count);   // 8 = 全 7 段
    }

    [Fact]
    public void Digit_one_has_two_segments()
    {
        Assert.Equal(2, StrokeFont.Strokes('1').Count);
    }

    [Fact]
    public void Unknown_char_empty()
    {
        Assert.Empty(StrokeFont.Strokes('中'));   // 中文暂空(记录)
        Assert.Empty(StrokeFont.Strokes(' '));    // 空格无笔画
    }

    [Fact]
    public void All_uppercase_letters_have_strokes()
    {
        for (char c = 'A'; c <= 'Z'; c++)
            Assert.True(StrokeFont.Strokes(c).Count > 0, $"字母 {c} 无字形");
    }

    [Fact]
    public void Lowercase_folds_to_uppercase()
    {
        Assert.Equal(StrokeFont.Strokes('A').Count, StrokeFont.Strokes('a').Count);
        Assert.Equal(StrokeFont.Strokes('K').Count, StrokeFont.Strokes('k').Count);
    }

    [Fact]
    public void Hole_id_zk01_all_render()
    {
        var t = new TextEntity { X = 0, Y = 0, Height = 5, Text = "ZK01" };
        var o = new List<float>();
        t.Tessellate(o);
        Assert.True(o.Count > 0);   // Z K 0 1 全有字形
    }

    [Fact]
    public void TextEntity_tessellates_digits()
    {
        var t = new TextEntity { X = 0, Y = 0, Height = 10, Text = "125" };
        var o = new List<float>();
        t.Tessellate(o);
        Assert.True(o.Count > 0);                 // "125" 有笔画
        Assert.Equal(0, o.Count % 12);            // 整段(每段 12 float)
    }

    [Fact]
    public void SceneIO_roundtrips_text()
    {
        var s = new Scene();
        s.Add(new TextEntity { X = 3, Y = 4, Height = 2.5, Text = "12.5m" });
        var s2 = SceneIO.Load(SceneIO.Save(s));
        var t = Assert.IsType<TextEntity>(s2.Entities[0]);
        Assert.Equal("12.5m", t.Text);
        Assert.Equal(2.5, t.Height, 4);
        Assert.Equal(3, t.X, 4);
    }
}
