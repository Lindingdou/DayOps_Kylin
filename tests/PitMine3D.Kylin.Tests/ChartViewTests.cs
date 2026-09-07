using System.Linq;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>自绘图表控件的纯计算部分(刻度/箱线/色带) —— 数据库页面全部图表依赖它。</summary>
public class ChartViewTests
{
    [Fact]
    public void NiceTicks_CoversRangeWithRoundSteps()
    {
        var t = ChartView.NiceTicks(0, 97, 6);
        Assert.NotEmpty(t);
        Assert.Equal(0, t.First());
        Assert.True(t.Last() <= 97 && t.Last() >= 80);
        var step = t[1] - t[0];
        Assert.Contains(step, new[] { 10.0, 20.0 });
        Assert.All(t.Zip(t.Skip(1), (a, b) => b - a), d => Assert.Equal(step, d, 6));
    }

    [Fact]
    public void NiceTicks_NegativeRangeAndTinyRange()
    {
        var t = ChartView.NiceTicks(-3.2, 2.9, 5);
        Assert.Contains(0.0, t);
        Assert.True(t.First() >= -3.2 && t.Last() <= 2.9);
        Assert.Empty(ChartView.NiceTicks(5, 5, 5));
    }

    [Fact]
    public void BoxOf_FiveNumberSummary()
    {
        var b = ChartView.BoxOf(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })!;
        Assert.Equal(1, b.Min); Assert.Equal(9, b.Max);
        Assert.Equal(5, b.Median);
        Assert.Equal(3, b.Q1); Assert.Equal(7, b.Q3);
        Assert.Equal(5, b.Mean!.Value, 9);
        Assert.Null(ChartView.BoxOf(System.Array.Empty<double>()));
    }

    [Fact]
    public void Lerp3_EndpointsAndMid()
    {
        var lo = Color.Parse("#2166AC"); var mid = Color.Parse("#F7F7F7"); var hi = Color.Parse("#B2182B");
        Assert.Equal(lo, ChartView.Lerp3(lo, mid, hi, 0));
        Assert.Equal(hi, ChartView.Lerp3(lo, mid, hi, 1));
        Assert.Equal(mid, ChartView.Lerp3(lo, mid, hi, 0.5));
        Assert.Equal(ChartView.PaletteAt(0), ChartView.PaletteAt(ChartView.Palette.Length));
    }
}
