using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三维折线纵坡分析回归（GradeProfile：逐段坡度% + 超限汇总）。</summary>
public class GradeProfileTests
{
    [Fact]
    public void Computes_grade_percent_per_segment()
    {
        // (0,0,0)→(100,0,8): 水平100, 升8 → 坡8%; (100,0,8)→(100,0,8): 竖直段坡记0
        var line = new List<(double x, double y, double z)> { (0, 0, 0), (100, 0, 8), (200, 0, 4) };
        var segs = GradeProfile.Compute(line);
        Assert.Equal(2, segs.Count);
        Assert.Equal(8.0, segs[0].GradePct, 6);        // 上坡 +8%
        Assert.Equal(-4.0, segs[1].GradePct, 6);       // 下坡 -4%（升-4/水平100）
        Assert.Equal(100.0, segs[0].HorizLen, 6);
    }

    [Fact]
    public void Summary_flags_over_limit_segments()
    {
        var line = new List<(double x, double y, double z)> { (0, 0, 0), (100, 0, 12), (200, 0, 15), (300, 0, 15) };
        // 段坡: +12%, +3%, 0%
        var segs = GradeProfile.Compute(line);
        var (maxAbs, over, avg) = GradeProfile.Summary(segs, maxPct: 8);
        Assert.Equal(12.0, maxAbs, 6);                 // 最大绝对纵坡
        Assert.Equal(1, over);                          // 仅第一段超 8%
        Assert.True(avg > 4 && avg < 6, $"加权平均绝对坡 ≈5, 实 {avg:0.##}");   // (12+3+0)/3=5
    }

    [Fact]
    public void Degenerate_inputs()
    {
        Assert.Empty(GradeProfile.Compute(new List<(double x, double y, double z)> { (0, 0, 0) }));
        var (maxAbs, over, avg) = GradeProfile.Summary(new List<GradeProfile.Seg>(), 8);
        Assert.Equal(0, maxAbs); Assert.Equal(0, over); Assert.Equal(0, avg);
    }
}
