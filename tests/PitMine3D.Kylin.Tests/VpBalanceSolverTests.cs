using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>剥采比均衡 DP 求解回归（移植自 PlanLib.VpBalanceSolver）。</summary>
public class VpBalanceSolverTests
{
    [Fact]
    public void Linear_curve_one_segment_zero_area()
    {
        // 恒定剥采比 2 → 单段即精确，超前剥离面积 0
        var xs = new List<double> { 0, 1, 2, 3 };
        var ys = new List<double> { 0, 2, 4, 6 };
        var r = VpBalanceSolver.Solve(xs, ys, 1);
        Assert.True(r.Ok);
        Assert.Single(r.Segments);
        Assert.Equal(2, r.Segments[0].RatioM3PerT, 6);
        Assert.Equal(0, r.TotalLeadArea, 6);
    }

    [Fact]
    public void Convex_curve_two_segments_fit_exactly()
    {
        // 剥采比 1→3 的凸曲线：K=1 有超前面积，K=2 过全部顶点 → 面积 0
        var xs = new List<double> { 0, 1, 2 };
        var ys = new List<double> { 0, 1, 4 };
        Assert.Equal(1.0, VpBalanceSolver.FitK(xs, ys, 1).area, 6);
        Assert.Equal(0.0, VpBalanceSolver.FitK(xs, ys, 2).area, 6);

        var r = VpBalanceSolver.Solve(xs, ys, 2);
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal(1, r.Segments[0].RatioM3PerT, 6);   // 段1 斜率 1
        Assert.Equal(3, r.Segments[1].RatioM3PerT, 6);   // 段2 斜率 3
    }

    [Fact]
    public void FitK_area_nonincreasing_in_K()
    {
        var xs = new List<double> { 0, 1, 2, 3, 4 };
        var ys = new List<double> { 0, 1, 3, 6, 10 };   // 剥采比递增
        double a1 = VpBalanceSolver.FitK(xs, ys, 1).area;
        double a2 = VpBalanceSolver.FitK(xs, ys, 2).area;
        double a3 = VpBalanceSolver.FitK(xs, ys, 3).area;
        Assert.True(a2 <= a1 + 1e-9);
        Assert.True(a3 <= a2 + 1e-9);
    }

    [Fact]
    public void BalanceY_interpolates_between_breakpoints()
    {
        var xs = new List<double> { 0, 2, 4 };
        var ys = new List<double> { 0, 1, 4 };
        var bps = new List<int> { 0, 2 };                // 单段：弦 (0,0)-(4,4)
        Assert.Equal(2, VpBalanceSolver.BalanceY(xs, ys, bps, 2), 6);   // 弦在 x=2 → y=2
        Assert.Equal(4, VpBalanceSolver.BalanceY(xs, ys, bps, 4), 6);
    }
}
