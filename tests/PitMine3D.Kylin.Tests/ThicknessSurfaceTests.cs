using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>煤厚分析等厚线(isopach)回归 —— IDW 插值 + Marching Squares 抽等厚线 + 煤厚统计。</summary>
public class ThicknessSurfaceTests
{
    // 厚度 = x 的线性场(0..10), 供确定性验证
    private static List<(double x, double y, double thickness)> LinearField() => new()
    {
        (0, 0, 0), (10, 0, 10), (0, 10, 0), (10, 10, 10), (5, 5, 5),
    };

    [Fact]
    public void Stats_reflect_thickness_values()
    {
        var r = ThicknessSurface.Isopach(LinearField());
        Assert.Equal(5, r.Stats.Count);
        Assert.Equal(0, r.Stats.Min, 6);
        Assert.Equal(10, r.Stats.Max, 6);
        Assert.Equal(5, r.Stats.Mean, 6);   // (0+10+0+10+5)/5
    }

    [Fact]
    public void Isopach_lines_at_interval_levels_within_range()
    {
        var r = ThicknessSurface.Isopach(LinearField(), gridN: 48, interval: 2);
        Assert.Equal(new[] { 0.0, 2, 4, 6, 8 }, r.Levels);       // 整数倍厚度层(min=0 恰整数倍→含, <max=10); 0 厚线=尖灭边界
        Assert.NotEmpty(r.Lines);
        // 每条等厚线的层厚都在数据范围内, 且是所列层之一
        Assert.All(r.Lines, s => Assert.Contains(s.Level, r.Levels));
        Assert.All(r.Lines, s => Assert.InRange(s.Level, 0, 10));
    }

    [Fact]
    public void Isopach_levels_track_gradient_direction()
    {
        // 厚度=x 线性: 层厚 L 的等厚线应大致落在 x≈L 处(IDW 近似)。取 level=6 的线, 平均 x 应偏大于 level=2 的
        var r = ThicknessSurface.Isopach(LinearField(), gridN: 48, interval: 2);
        double AvgX(double lvl) { var xs = r.Lines.Where(s => s.Level == lvl).SelectMany(s => new[] { s.X0, s.X1 }).ToList(); return xs.Count > 0 ? xs.Average() : double.NaN; }
        double x2 = AvgX(2), x6 = AvgX(6);
        Assert.True(x6 > x2, $"厚层等厚线应在更大 x 处：x(6)={x6} 应 > x(2)={x2}");
    }

    [Fact]
    public void Uniform_thickness_has_stats_but_no_isopach()
    {
        var pts = new List<(double x, double y, double thickness)> { (0, 0, 3), (10, 0, 3), (0, 10, 3), (10, 10, 3) };
        var r = ThicknessSurface.Isopach(pts);
        Assert.Equal(3, r.Stats.Min, 6);
        Assert.Equal(3, r.Stats.Max, 6);
        Assert.Empty(r.Lines);            // 厚度无起伏 → 无等厚线
        Assert.Empty(r.Levels);
    }

    [Fact]
    public void Too_few_points_safe()
    {
        var pts = new List<(double x, double y, double thickness)> { (0, 0, 1), (1, 1, 5) };
        var r = ThicknessSurface.Isopach(pts);
        Assert.Empty(r.Lines);
        Assert.Equal(2, r.Stats.Count);   // 统计仍算
    }
}
