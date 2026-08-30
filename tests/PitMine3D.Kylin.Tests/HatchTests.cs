using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>平行填充（排土条带）回归。</summary>
public class HatchTests
{
    [Fact]
    public void Square_horizontal_strips_span_full_width()
    {
        // 10×10 方形, 间距 2.5, 水平 → y=2.5/5/7.5(0与10为边界半开排除) 各横跨 x 0..10
        var poly = new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        var segs = Hatch.ParallelFill(poly, 2.5, 0);
        Assert.True(segs.Count >= 3, $"segs={segs.Count}");
        foreach (var s in segs)
        {
            Assert.Equal(0, System.Math.Min(s.x0, s.x1), 3);
            Assert.Equal(10, System.Math.Max(s.x0, s.x1), 3);
            Assert.Equal(s.y0, s.y1, 6);   // 水平段
        }
    }

    [Fact]
    public void Too_small_or_open_returns_empty()
    {
        Assert.Empty(Hatch.ParallelFill(new List<(double x, double y)> { (0, 0), (1, 1) }, 1, 0)); // 点太少
        var sq = new List<(double x, double y)> { (0, 0), (1, 0), (1, 1), (0, 1) };
        Assert.Empty(Hatch.ParallelFill(sq, 0, 0));   // 间距 0
    }

    [Fact]
    public void Triangle_strips_narrow_toward_apex()
    {
        // 底宽顶尖三角: 低处条带长, 高处短
        var tri = new List<(double x, double y)> { (0, 0), (10, 0), (5, 10) };
        var segs = Hatch.ParallelFill(tri, 2, 0);
        Assert.NotEmpty(segs);
        // 最低条带比最高条带长
        double loLen = System.Math.Abs(segs[0].x1 - segs[0].x0);
        double hiLen = System.Math.Abs(segs[^1].x1 - segs[^1].x0);
        Assert.True(loLen > hiLen, $"lo={loLen} hi={hiLen}");
    }
}
