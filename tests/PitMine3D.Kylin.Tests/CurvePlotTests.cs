using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>通用 XY 折线图（自动量程归一化 + 外框 + 四角刻度 + 轴名）回归。</summary>
public class CurvePlotTests
{
    [Fact]
    public void Build_normalizes_points_to_box_with_frame_and_labels()
    {
        // 三点 X∈[0,10]、Y∈[20,100]; 图区 [0,0]..[40,20]
        var pts = new List<(double x, double y)> { (0, 20), (5, 60), (10, 100) };
        var ents = CurvePlot.Build(pts, 0, 0, 40, 20, 1, "限值", "累计%");

        var lines = ents.OfType<LineEntity>().ToList();
        // 首段起点 = 左下角(0,0); 末段终点 = 右上角(40,20)（量程两端映射到图区角）
        var seg0 = lines[0];
        Assert.True(Math.Abs(seg0.X0 - 0) < 1e-9 && Math.Abs(seg0.Y0 - 0) < 1e-9);
        var seg1 = lines[1];
        Assert.True(Math.Abs(seg1.X1 - 40) < 1e-9 && Math.Abs(seg1.Y1 - 20) < 1e-9);
        // 中点 x=5→图区 X=20, y=60→图区 Y=(60-20)/80*20=10
        Assert.True(Math.Abs(seg0.X1 - 20) < 1e-9 && Math.Abs(seg0.Y1 - 10) < 1e-9);

        var texts = ents.OfType<TextEntity>().ToList();
        Assert.Contains(texts, t => t.Text == "限值");    // X 轴名
        Assert.Contains(texts, t => t.Text == "累计%");   // Y 轴名
        Assert.Contains(texts, t => t.Text == "0");        // X min
        Assert.Contains(texts, t => t.Text == "10");       // X max
        Assert.Contains(texts, t => t.Text == "20");       // Y min
        Assert.Contains(texts, t => t.Text == "100");      // Y max
        // 折线 2 段 + 外框 4 段 = 6 条线
        Assert.Equal(6, lines.Count);
    }

    [Fact]
    public void Build_degenerate_y_range_stays_finite()
    {
        // 所有 Y 相等 → ry 退化保护, 折线贴底、不产生 NaN/Inf
        var pts = new List<(double x, double y)> { (0, 5), (1, 5), (2, 5) };
        var ents = CurvePlot.Build(pts, 0, 0, 30, 10, 1, "x", "y");
        var lines = ents.OfType<LineEntity>().ToList();
        Assert.All(lines, l => Assert.True(double.IsFinite(l.X0) && double.IsFinite(l.Y0)
                                        && double.IsFinite(l.X1) && double.IsFinite(l.Y1)));
    }

    [Fact]
    public void Build_empty_or_zero_size_returns_empty()
    {
        Assert.Empty(CurvePlot.Build(new List<(double, double)>(), 0, 0, 40, 20, 1, "x", "y"));
        Assert.Empty(CurvePlot.Build(new List<(double, double)> { (1, 1) }, 0, 0, 0, 20, 1, "x", "y")); // 零宽
    }
}
