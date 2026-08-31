using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>道路路面路带生成回归（RoadSurface.Strip：中线双侧外扩）。</summary>
public class RoadSurfaceTests
{
    private static double PolyArea(List<(double x, double y)> poly)
    {
        double s = 0;
        for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; s += a.x * b.y - b.x * a.y; }
        return Math.Abs(s) / 2;
    }

    [Fact]
    public void Straight_centerline_makes_rectangle_of_length_times_width()
    {
        // 水平直线中线 长100, 路宽 20 → 矩形面积 100×20=2000
        var center = new List<(double x, double y)> { (0, 0), (100, 0) };
        var strip = RoadSurface.Strip(center, 20);
        Assert.Equal(4, strip.Count);                          // 2 点中线 → 4 顶点路带
        Assert.Equal(2000.0, PolyArea(strip), 3);             // 面积 = 长×宽
        // 左右缘各偏 ±10
        Assert.Contains(strip, p => Math.Abs(p.y - 10) < 1e-6);
        Assert.Contains(strip, p => Math.Abs(p.y + 10) < 1e-6);
    }

    [Fact]
    public void Multi_segment_centerline_area_at_least_length_times_width()
    {
        // 折线中线：路带面积 ≥ 各段长×宽之和的近似(转角处略有出入)，且为正、闭合 ≥6 顶点
        var center = new List<(double x, double y)> { (0, 0), (50, 0), (50, 50) };
        var strip = RoadSurface.Strip(center, 10);
        Assert.Equal(6, strip.Count);                          // 3 中线点 → 6 路带顶点
        double area = PolyArea(strip);
        Assert.True(area > 900 && area < 1200, $"两段各 50 × 宽 10 ≈ 1000, 实 {area:0}");
    }

    [Fact]
    public void Degenerate_inputs_return_empty()
    {
        Assert.Empty(RoadSurface.Strip(new List<(double x, double y)> { (0, 0) }, 10));   // 单点
        Assert.Empty(RoadSurface.Strip(new List<(double x, double y)> { (0, 0), (1, 0) }, 0));  // 零宽
    }
}
