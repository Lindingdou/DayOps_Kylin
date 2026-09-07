using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>箱线图（Q1–Q3 箱 + 中位线 + 须端帽 + 共享量程）回归。</summary>
[Collection("TextGeometry")]
public class BoxPlotTests
{
    [Fact]
    public void Build_boxes_share_value_range_with_median_and_whiskers()
    {
        // 两箱, 全局 min0 max20; 图区 0..40 × 0..20 → SY 恒等
        var boxes = new List<(string, double, double, double, double, double)>
        {
            ("A", 0, 5, 10, 15, 20),
            ("B", 4, 6, 8, 12, 16),
        };
        var ents = BoxPlot.Build(boxes, 0, 0, 40, 20, 1, "Ad");
        var lines = ents.OfType<LineEntity>().ToList();
        // A 的须: 竖线 min0→max20; 存在一条 x≈10(第一槽中心 = 0.5*20) 的竖线从 y0 到 y20
        Assert.Contains(lines, l => Math.Abs(l.X0 - 10) < 1e-6 && Math.Abs(l.X1 - 10) < 1e-6
            && Math.Abs(Math.Min(l.Y0, l.Y1) - 0) < 1e-6 && Math.Abs(Math.Max(l.Y0, l.Y1) - 20) < 1e-6);
        // 中位线(橙 Med 色 Cr≈0.95) 存在, A 中位10→y10
        Assert.Contains(lines, l => l.Cr > 0.9f && Math.Abs(l.Y0 - 10) < 1e-6 && Math.Abs(l.Y1 - 10) < 1e-6);
        var texts = ents.OfType<TextEntity>().Select(t => t.Text).ToList();
        Assert.Contains("A", texts); Assert.Contains("B", texts); Assert.Contains("Ad", texts);
        Assert.Contains("0", texts); Assert.Contains("20", texts);   // 值轴端
    }

    [Fact]
    public void Build_empty_or_zero_size_returns_empty()
    {
        Assert.Empty(BoxPlot.Build(new List<(string, double, double, double, double, double)>(), 0, 0, 40, 20, 1, "v"));
        Assert.Empty(BoxPlot.Build(new List<(string, double, double, double, double, double)> { ("A", 0, 1, 2, 3, 4) }, 0, 0, 0, 20, 1, "v"));
    }
}
