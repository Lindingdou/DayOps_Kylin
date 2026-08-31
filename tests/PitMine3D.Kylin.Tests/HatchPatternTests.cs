using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>图案填充（用户定义线剖面）回归。</summary>
public class HatchPatternTests
{
    private static List<(double x, double y)> Square(double s) =>
        new() { (0, 0), (s, 0), (s, s), (0, s) };

    [Fact]
    public void Square_horizontal_hatch_line_count_and_bounds()
    {
        // 10×10 方, 0° 间距 2 → 扫描线 y=2,4,6,8 (跳边界) = 4 条; 各从 x=0 到 x=10。
        var lines = HatchPattern.Generate(Square(10), 0, 2);
        Assert.Equal(4, lines.Count);
        foreach (var l in lines)
        {
            Assert.Equal(0, Math.Min(l.x1, l.x2), 6);
            Assert.Equal(10, Math.Max(l.x1, l.x2), 6);
            Assert.Equal(l.y1, l.y2, 6);              // 水平
            Assert.InRange(l.y1, 1.9, 8.1);
        }
    }

    [Fact]
    public void Vertical_hatch_at_90_degrees()
    {
        // 90° → 竖线, y 跨度全高, x 为扫描位置
        var lines = HatchPattern.Generate(Square(10), 90, 2);
        Assert.Equal(4, lines.Count);
        foreach (var l in lines)
        {
            Assert.Equal(l.x1, l.x2, 6);              // 竖直
            Assert.Equal(0, Math.Min(l.y1, l.y2), 6);
            Assert.Equal(10, Math.Max(l.y1, l.y2), 6);
        }
    }

    [Fact]
    public void Cross_hatch_doubles_direction()
    {
        var single = HatchPattern.Generate(Square(10), 0, 2, cross: false);
        var crossed = HatchPattern.Generate(Square(10), 0, 2, cross: true);
        Assert.Equal(single.Count * 2, crossed.Count);   // 加一组正交
    }

    [Fact]
    public void Lines_stay_inside_convex_boundary()
    {
        var tri = new List<(double x, double y)> { (0, 0), (10, 0), (0, 10) };  // 直角三角
        var lines = HatchPattern.Generate(tri, 0, 1);
        // 每条水平线 y=k, 内部 x∈[0, 10-k]; 右端点应 ≈ 10-y
        foreach (var l in lines)
        {
            double y = l.y1, xmax = Math.Max(l.x1, l.x2), xmin = Math.Min(l.x1, l.x2);
            Assert.True(xmin >= -1e-6);
            Assert.True(xmax <= 10 - y + 1e-6, $"y={y} xmax={xmax}");
        }
    }

    [Fact]
    public void Concave_boundary_pairs_intervals()
    {
        // C 形凹多边形(缺口在右中): 扫描线穿过缺口应切成两段
        var c = new List<(double x, double y)>
        {
            (0, 0), (10, 0), (10, 3), (4, 3), (4, 7), (10, 7), (10, 10), (0, 10)
        };
        var lines = HatchPattern.Generate(c, 0, 1);
        // y=5 那条穿缺口 → 只覆盖 x∈[0,4]（缺口 4..10 无料）
        var mid = lines.FindAll(l => Math.Abs(l.y1 - 5) < 1e-6);
        Assert.Single(mid);
        Assert.Equal(0, Math.Min(mid[0].x1, mid[0].x2), 6);
        Assert.Equal(4, Math.Max(mid[0].x1, mid[0].x2), 6);
    }

    [Fact]
    public void Degenerate_inputs_empty()
    {
        Assert.Empty(HatchPattern.Generate(new List<(double, double)> { (0, 0), (1, 1) }, 0, 1));  // <3 点
        Assert.Empty(HatchPattern.Generate(Square(10), 0, 0));      // 间距 0
        Assert.Empty(HatchPattern.Generate(null!, 0, 1));
    }
}
