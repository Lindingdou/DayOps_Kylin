using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>标注样式设置面板背后的读写/校验/持久化（DimStyleStore）回归。</summary>
public class DimStyleStoreTests
{
    [Fact]
    public void Clone_and_CopyTo_cover_every_field()
    {
        var s = new DimStyle { TextHeight = 2.5, DecimalPlaces = 3, TickRatio = 0.1, ArrowRatio = 0.7, ArrowWidthRatio = 0.3, TextOffsetRatio = 0.9, ExtLineOffsetRatio = 0.25, ExtLineExtensionRatio = 0.55 };
        var c = DimStyleStore.Clone(s);
        Assert.NotSame(s, c);
        Assert.Equal(DimStyleStore.ToJson(s), DimStyleStore.ToJson(c));
        var d = new DimStyle();
        DimStyleStore.CopyTo(s, d);
        Assert.Equal(2.5, d.TextHeight); Assert.Equal(3, d.DecimalPlaces); Assert.Equal(0.55, d.ExtLineExtensionRatio);
    }

    [Fact]
    public void TrySet_validates_and_writes()
    {
        var s = new DimStyle();
        Assert.Null(DimStyleStore.TrySet(s, "TextHeight", " 3.5 "));
        Assert.Equal(3.5, s.TextHeight);
        Assert.Null(DimStyleStore.TrySet(s, "DecimalPlaces", "4"));
        Assert.Equal(4, s.DecimalPlaces);
        Assert.NotNull(DimStyleStore.TrySet(s, "DecimalPlaces", "9"));      // 越界不写
        Assert.Equal(4, s.DecimalPlaces);
        Assert.NotNull(DimStyleStore.TrySet(s, "DecimalPlaces", "2.5"));    // 非整数
        Assert.NotNull(DimStyleStore.TrySet(s, "ArrowRatio", "abc"));
        Assert.Equal(new DimStyle().ArrowRatio, s.ArrowRatio);
        Assert.NotNull(DimStyleStore.TrySet(s, "TextHeight", "-1"));
        Assert.Null(DimStyleStore.TrySet(s, "TextHeight", "0"));            // 0 = 自动, 合法
        Assert.Equal(0, s.TextHeight);
        Assert.NotNull(DimStyleStore.TrySet(s, "Nope", "1"));
    }

    [Fact]
    public void Json_round_trip_and_bad_values_fall_back()
    {
        var s = new DimStyle { TextHeight = 1.25, DecimalPlaces = 1, ArrowRatio = 0.8 };
        var back = DimStyleStore.FromJson(DimStyleStore.ToJson(s))!;
        Assert.Equal(1.25, back.TextHeight); Assert.Equal(1, back.DecimalPlaces); Assert.Equal(0.8, back.ArrowRatio);
        Assert.Equal("0.#", back.NumberFormat);
        Assert.Null(DimStyleStore.FromJson("not json"));
        var bad = DimStyleStore.FromJson("{\"TextHeight\":-5,\"DecimalPlaces\":42,\"ArrowRatio\":999}")!;
        var d = new DimStyle();
        Assert.Equal(d.TextHeight, bad.TextHeight); Assert.Equal(d.DecimalPlaces, bad.DecimalPlaces); Assert.Equal(d.ArrowRatio, bad.ArrowRatio);
        var partial = DimStyleStore.FromJson("{\"DecimalPlaces\":0}")!;   // 缺项按默认
        Assert.Equal(0, partial.DecimalPlaces); Assert.Equal(d.ArrowRatio, partial.ArrowRatio);
        Assert.Equal("0", partial.NumberFormat);
    }

    [Fact]
    public void Describe_reports_auto_height()
    {
        Assert.Contains("自动", DimStyleStore.Describe(new DimStyle()));
        Assert.Contains("文字高 2.5", DimStyleStore.Describe(new DimStyle { TextHeight = 2.5 }));
    }
}
