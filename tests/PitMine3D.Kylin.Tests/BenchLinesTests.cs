using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>境界台阶线生成回归。</summary>
public class BenchLinesTests
{
    private static List<(double x, double y)> Square(double s) =>
        new() { (0, 0), (s, 0), (s, s), (0, s) };   // CCW

    [Fact]
    public void OffsetClosed_square_shrinks_uniformly()
    {
        var r = BenchLines.OffsetClosed(Square(10), 1)!;
        Assert.Equal(4, r.Count);
        Assert.Equal(1, r[0].x, 4); Assert.Equal(1, r[0].y, 4);
        Assert.Equal(9, r[1].x, 4); Assert.Equal(1, r[1].y, 4);
        Assert.Equal(9, r[2].x, 4); Assert.Equal(9, r[2].y, 4);
        Assert.Equal(1, r[3].x, 4); Assert.Equal(9, r[3].y, 4);
    }

    [Fact]
    public void OffsetClosed_handles_clockwise_winding()
    {
        // CW 方形，内偏移应同样收缩(不外扩)
        var cw = new List<(double x, double y)> { (0, 0), (0, 10), (10, 10), (10, 0) };
        var r = BenchLines.OffsetClosed(cw, 1)!;
        // 各点应落在 [1,9]×[1,9] 内
        foreach (var p in r)
        {
            Assert.InRange(p.x, 0.999, 9.001);
            Assert.InRange(p.y, 0.999, 9.001);
        }
    }

    [Fact]
    public void Generate_stops_when_area_no_longer_shrinks()
    {
        // 20×20 方形，台阶距 2 → 每圈缩 2，最多到中心约 5 圈后退化停
        var rings = BenchLines.Generate(Square(20), 2, 20);
        Assert.True(rings.Count >= 3);
        Assert.True(rings.Count < 20);                  // 未跑满 → 自交保护生效
        // 面积严格递减
        for (int i = 1; i < rings.Count; i++)
        {
            double prev = System.Math.Abs(BenchLines.SignedArea(rings[i - 1]));
            double cur = System.Math.Abs(BenchLines.SignedArea(rings[i]));
            Assert.True(cur < prev);
        }
    }
}
