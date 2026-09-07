using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>类别柱状图（竖条 ∝ 值/最大值 + 类别标签 + 值轴）回归。</summary>
[Collection("TextGeometry")]
public class BarChartPlotTests
{
    [Fact]
    public void Build_bars_scale_to_max_value_with_labels()
    {
        var items = new List<(string, double)> { ("QM", 40), ("CY", 20), ("SM", 10) };
        var ents = BarChartPlot.Build(items, 0, 0, 30, 20, 1, "样本数");

        var lines = ents.OfType<LineEntity>().ToList();
        // 最高条(QM=40=max) 顶到达图区顶 y=20 → 存在 y≈20 的顶线
        Assert.Contains(lines, l => Math.Abs(l.Y0 - 20) < 1e-6 && Math.Abs(l.Y1 - 20) < 1e-6);
        // 次条 CY=20 → 高 = 20/40*20 = 10
        Assert.Contains(lines, l => Math.Abs(l.Y0 - 10) < 1e-6 && Math.Abs(l.Y1 - 10) < 1e-6);

        var texts = ents.OfType<TextEntity>().Select(t => t.Text).ToList();
        Assert.Contains("QM", texts);          // 类别标签
        Assert.Contains("CY", texts);
        Assert.Contains("SM", texts);
        Assert.Contains("样本数", texts);        // 值轴名
        Assert.Contains("40", texts);          // max 刻度 + QM 数值
    }

    [Fact]
    public void Build_all_zero_values_stays_finite_no_div0()
    {
        var items = new List<(string, double)> { ("A", 0), ("B", 0) };
        var ents = BarChartPlot.Build(items, 0, 0, 20, 10, 1, "v");
        var lines = ents.OfType<LineEntity>().ToList();
        Assert.All(lines, l => Assert.True(double.IsFinite(l.Y0) && double.IsFinite(l.Y1)));
    }

    [Fact]
    public void Build_empty_or_zero_size_returns_empty()
    {
        Assert.Empty(BarChartPlot.Build(new List<(string, double)>(), 0, 0, 30, 20, 1, "v"));
        Assert.Empty(BarChartPlot.Build(new List<(string, double)> { ("A", 1) }, 0, 0, 0, 20, 1, "v"));
    }
}
