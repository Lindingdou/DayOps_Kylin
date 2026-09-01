using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>散点图（点标记 + 拟合线 + 离群高亮 + 轴）回归。</summary>
public class ScatterPlotTests
{
    [Fact]
    public void Build_scatters_points_with_fit_line_in_frame()
    {
        // 完美线 y = 2x, x∈[0,10] → 拟合端点(0,0)/(10,20) 恰为 Y 量程端
        var pts = new List<(double, double)> { (0, 0), (5, 10), (10, 20) };
        var ents = ScatterPlot.Build(pts, 0, 0, 40, 20, 1, "Ad", "Q", (2.0, 0.0));

        var dots = ents.OfType<PointEntity>().ToList();
        Assert.Equal(3, dots.Count);                          // 每样本一点
        Assert.All(dots, d => Assert.Equal(3, d.Style));      // 叉标记
        // 点 (5,10)→图区中心 (20,10)
        Assert.Contains(dots, d => Math.Abs(d.X - 20) < 1e-9 && Math.Abs(d.Y - 10) < 1e-9);

        var lines = ents.OfType<LineEntity>().ToList();
        // 拟合线端点 (0,0)→(40,20)（映射到图区两角, 因 y=2x 端点即 Y 量程端）
        Assert.Contains(lines, l => Math.Abs(l.X0 - 0) < 1e-9 && Math.Abs(l.Y0 - 0) < 1e-9
                                 && Math.Abs(l.X1 - 40) < 1e-9 && Math.Abs(l.Y1 - 20) < 1e-9);

        var texts = ents.OfType<TextEntity>().Select(t => t.Text).ToList();
        Assert.Contains("Ad", texts); Assert.Contains("Q", texts);   // 轴名
        Assert.Contains("0", texts); Assert.Contains("10", texts);   // X min/max
        Assert.Contains("20", texts);                                // Y max
    }

    [Fact]
    public void Build_highlights_outliers_larger_and_distinct_color()
    {
        var pts = new List<(double, double)> { (0, 0), (1, 1), (2, 8) };
        var ents = ScatterPlot.Build(pts, 0, 0, 20, 20, 1, "x", "y",
            highlight: new List<(double, double)> { (2, 8) });
        var dots = ents.OfType<PointEntity>().ToList();
        var hot = dots.Single(d => d.Cr > 0.9f && d.Cg < 0.5f);      // 红叉离群
        var normal = dots.First(d => !(d.Cr > 0.9f && d.Cg < 0.5f));
        Assert.True(hot.Size > normal.Size);                          // 离群点更大
    }

    [Fact]
    public void Build_fit_line_endpoints_extend_y_range_to_stay_in_box()
    {
        // 点 y 全为 5, 但拟合线 y=x 在 x=10 处到 10 → Y 量程须含 10, 线不出框
        var pts = new List<(double, double)> { (0, 5), (5, 5), (10, 5) };
        var ents = ScatterPlot.Build(pts, 0, 0, 30, 30, 1, "x", "y", (1.0, 0.0));
        var lines = ents.OfType<LineEntity>().ToList();
        Assert.All(lines, l => Assert.True(l.Y0 >= -1e-9 && l.Y0 <= 30 + 1e-9
                                        && l.Y1 >= -1e-9 && l.Y1 <= 30 + 1e-9));
    }

    [Fact]
    public void Build_empty_or_zero_size_returns_empty()
    {
        Assert.Empty(ScatterPlot.Build(new List<(double, double)>(), 0, 0, 40, 20, 1, "x", "y"));
        Assert.Empty(ScatterPlot.Build(new List<(double, double)> { (1, 1) }, 0, 0, 0, 20, 1, "x", "y"));
    }
}
