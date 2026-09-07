using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>直方图柱状图（竖条+轴）回归。</summary>
[Collection("TextGeometry")]
public class HistogramPlotTests
{
    [Fact]
    public void Build_bars_scale_to_max_count_with_axes()
    {
        // 数据集中在低值 → 首桶计数最大
        var vals = new List<double> { 1, 1, 1, 1, 2, 3, 10 };
        var s = Statistics.Describe(vals, 4);            // 4 桶
        var ents = HistogramPlot.Build(s, 0, 0, 40, 20, 1);

        var texts = ents.OfType<TextEntity>().ToList();
        Assert.Contains(texts, t => t.Text == "值");      // X 轴名
        Assert.Contains(texts, t => t.Text == "频数");    // Y 轴名
        Assert.Contains(texts, t => t.Text == "1");       // min 标签
        Assert.Contains(texts, t => t.Text == "10");      // max 标签

        var lines = ents.OfType<LineEntity>().ToList();
        // 最高桶顶到达图区顶(y=20); 存在一条 y≈20 的顶线
        Assert.Contains(lines, l => System.Math.Abs(l.Y0 - 20) < 1e-6 && System.Math.Abs(l.Y1 - 20) < 1e-6);
    }

    [Fact]
    public void Build_empty_or_zero_size_returns_empty()
    {
        var s = Statistics.Describe(new List<double> { 1, 2, 3 }, 4);
        Assert.Empty(HistogramPlot.Build(s, 0, 0, 0, 20, 1));         // 零宽
        Assert.Empty(HistogramPlot.Build(default, 0, 0, 40, 20, 1));  // 空直方图
    }
}
