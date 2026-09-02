using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>道路横断面回归（弯道加宽 / 超高 / 三点曲率 / 沿线汇总）。对公路几何公式校核。</summary>
public class RoadCrossSectionTests
{
    [Fact]
    public void Widening_formula_and_threshold()
    {
        // ε = laneCount·L²/(2R); L=6, R=50, lane=2 → 2·36/100 = 0.72
        Assert.Equal(0.72, RoadCrossSection.WideningM(50, widenThresholdM: 100, laneCount: 2, wheelbaseM: 6), 6);
        // R 超阈值不加宽
        Assert.Equal(0.0, RoadCrossSection.WideningM(200, 100, 2, 6), 9);
        // 直线(R=∞)不加宽
        Assert.Equal(0.0, RoadCrossSection.WideningM(double.PositiveInfinity, 100, 2, 6), 9);
    }

    [Fact]
    public void Superelevation_formula_and_clamp()
    {
        // e = V²/(127R) − μ; V=40, R=50 → 1600/6350 − 0.15 = 0.25197 − 0.15 = 0.10197 → 10.197%
        Assert.Equal(10.197, RoadCrossSection.SuperelevationPct(50, 40, maxSuperPct: 12, frictionMu: 0.15), 2);
        // 大 R → 需求为负 → clamp 到 0
        Assert.Equal(0.0, RoadCrossSection.SuperelevationPct(10000, 40, 12), 6);
        // 小 R → 超过上限 → clamp 到 max
        Assert.Equal(8.0, RoadCrossSection.SuperelevationPct(20, 60, maxSuperPct: 8), 6);
    }

    [Fact]
    public void Radius3_of_unit_circle_points()
    {
        // 单位圆上三点 → 半径 1
        var a = (1.0, 0.0, 0.0);
        var b = (0.0, 1.0, 0.0);
        var c = (-1.0, 0.0, 0.0);
        Assert.Equal(1.0, RoadCrossSection.Radius3(a, b, c), 6);
    }

    [Fact]
    public void Radius3_collinear_is_infinite()
    {
        Assert.True(double.IsPositiveInfinity(
            RoadCrossSection.Radius3((0, 0, 0), (1, 0, 0), (2, 0, 0))));
    }

    [Fact]
    public void ComputeAlong_straight_line_no_widen()
    {
        var pts = new List<(double, double, double)> { (0, 0, 0), (10, 0, 0), (20, 0, 0), (30, 0, 0) };
        var cs = RoadCrossSection.ComputeAlong(pts, baseWidthM: 15, widenThresholdM: 100, laneCount: 2, wheelbaseM: 6, designSpeedKmh: 30, maxSuperPct: 8);
        Assert.Equal(0.0, cs.MaxWideningM, 9);
        Assert.Equal(0.0, cs.WidenedLengthM, 9);
        foreach (var w in cs.WidthM) Assert.Equal(15.0, w, 9);
    }

    [Fact]
    public void ComputeAlong_curve_widens()
    {
        // 四点近直角转弯 → 中间站有小曲率半径 → 加宽 > 0
        var pts = new List<(double, double, double)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (20, 10, 0) };
        var cs = RoadCrossSection.ComputeAlong(pts, 15, 100, 2, 6, 30, 8);
        Assert.True(cs.MaxWideningM > 0);
        Assert.True(cs.WidenedLengthM > 0);
    }

    [Fact]
    public void MinCurveRadius_by_speed_known_value_and_super_inverse()
    {
        // R=v²/(127(μ+e_max)). v=25,μ=0.15,e_max=6% → 25²/(127·0.21)=625/26.67=23.44m。
        double R = RoadCrossSection.MinCurveRadiusBySpeed(25, 6);
        Assert.Equal(625.0 / (127.0 * 0.21), R, 4);
        // 逆一致: 在 R_min 处, 超高应恰饱和到 e_max(SuperelevationPct 的逆)。
        Assert.Equal(6.0, RoadCrossSection.SuperelevationPct(R, 25, 6), 3);
        // 车速越高 R_min 越大; 超高越大 R_min 越小。
        Assert.True(RoadCrossSection.MinCurveRadiusBySpeed(40, 6) > RoadCrossSection.MinCurveRadiusBySpeed(25, 6));
        Assert.True(RoadCrossSection.MinCurveRadiusBySpeed(25, 8) < RoadCrossSection.MinCurveRadiusBySpeed(25, 4));
    }

    [Fact]
    public void Development_length_is_rise_over_grade()
    {
        // 降 45m @ 9% 纵坡 → 展线长 45/0.09=500m。
        Assert.Equal(500.0, RoadCrossSection.DevelopmentLengthM(45, 9), 6);
        Assert.Equal(0, RoadCrossSection.DevelopmentLengthM(45, 0), 6);   // 零纵坡 → 0(不外推)
        // 纵坡越缓展线越长。
        Assert.True(RoadCrossSection.DevelopmentLengthM(45, 6) > RoadCrossSection.DevelopmentLengthM(45, 12));
    }

    // ── 移植原 Tests.PitMineApp/RoadCrossSectionTests 已知值(等价性由构造; §289 补深度核) ──
    [Fact]
    public void Orig_Widening_OnlyOnTightCurves()
    {
        Assert.Equal(2.4, RoadCrossSection.WideningM(15, 200, laneCount: 2, wheelbaseM: 6), 2);  // 2·36/30
        Assert.Equal(0.0, RoadCrossSection.WideningM(300, 200, 2, 6), 6);                        // R>阈值
        Assert.Equal(0.0, RoadCrossSection.WideningM(double.PositiveInfinity, 200, 2, 6), 6);    // 直线
    }

    [Fact]
    public void Orig_Superelevation_ClampedAndZeroOnFlat()
    {
        Assert.Equal(6.0, RoadCrossSection.SuperelevationPct(15, designSpeedKmh: 25, maxSuperPct: 6), 3);  // 17.8%→clamp 6
        Assert.Equal(0.0, RoadCrossSection.SuperelevationPct(300, 25, 6), 3);                              // 需求<0
    }

    [Fact]
    public void Orig_ComputeAlong_WidensAtCorner()
    {
        var pts = new (double, double, double)[] { (0, 0, 0), (10, 0, 0), (10, 10, 0) };
        var cs = RoadCrossSection.ComputeAlong(pts, baseWidthM: 10, widenThresholdM: 200,
            laneCount: 2, wheelbaseM: 6, designSpeedKmh: 25, maxSuperPct: 6);
        Assert.Equal(10.0, cs.WidthM[0], 3);          // 端点直线 → 基宽
        Assert.True(cs.WidthM[1] > 10.0);             // 角点加宽
        Assert.Equal(5.09, cs.MaxWideningM, 1);       // 2·36/(2·7.07)
        Assert.True(cs.MaxSuperelevationPct > 0);     // 紧弯有超高
    }

    [Fact]
    public void Orig_ComputeAlong_NoWideningWhenThresholdSmall()
    {
        var pts = new (double, double, double)[] { (0, 0, 0), (10, 0, 0), (10, 10, 0) };
        var cs = RoadCrossSection.ComputeAlong(pts, 10, widenThresholdM: 5,
            laneCount: 2, wheelbaseM: 6, designSpeedKmh: 25, maxSuperPct: 6);
        Assert.Equal(0.0, cs.MaxWideningM, 6);
        Assert.Equal(10.0, cs.WidthM[1], 6);
    }
}
