using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>纵断面竖曲线平滑回归（GBJ22-87 阶段③抛物线）—— 端点保持/抛物线公式/半径 clamp/触发阈值。</summary>
public class RoadVerticalCurveTests
{
    static readonly double[] S3 = { 0, 100, 200 };

    [Fact]
    public void Fewer_than_three_points_passes_through()
    {
        var r = RoadVerticalCurve.Smooth(new double[] { 0, 100 }, new double[] { 0, 10 }, 1.0, 500);
        Assert.Equal(0, r.Count);
        Assert.Equal(2, r.Profile.Count);
        Assert.Equal((0.0, 0.0), r.Profile[0]);
        Assert.Equal((100.0, 10.0), r.Profile[1]);
    }

    [Fact]
    public void Constant_grade_inserts_no_curve()
    {
        // 恒定 10% 上坡 → 无变坡 → 不设竖曲线，剖面原样返回
        var r = RoadVerticalCurve.Smooth(S3, new double[] { 0, 10, 20 }, 1.0, 500);
        Assert.Equal(0, r.Count);
        Assert.Equal(0, r.Violations);
        Assert.Equal(3, r.Profile.Count);
        Assert.Equal((100.0, 10.0), r.Profile[1]);
    }

    [Fact]
    public void Grade_break_below_trigger_inserts_no_curve()
    {
        // 变坡 0.5%（0.105 vs 0.10）< trigger 1% → 不设竖曲线
        var r = RoadVerticalCurve.Smooth(S3, new double[] { 0, 10, 20.5 }, 1.0, 500);
        Assert.Equal(0, r.Count);
        Assert.Equal(3, r.Profile.Count);
    }

    [Fact]
    public void Crest_break_inserts_one_parabolic_curve()
    {
        // 对称凸曲线：+10% 接 −10%，dg=0.20；trigger 1% 触发；rV=500 → Lv=100
        var r = RoadVerticalCurve.Smooth(S3, new double[] { 0, 10, 0 }, 1.0, 500);
        Assert.Equal(1, r.Count);
        // half = min(50, 0.49*100)=49 → rAch = 2*49/0.20 = 490 < 500 → 1 处不达标
        Assert.Equal(1, r.Violations);
        Assert.Equal(490.0, r.MinRadiusM, 6);
    }

    [Fact]
    public void Endpoints_are_preserved_through_smoothing()
    {
        var z = new double[] { 0, 10, 0 };
        var r = RoadVerticalCurve.Smooth(S3, z, 1.0, 500);
        Assert.Equal((0.0, 0.0), r.Profile[0]);
        var last = r.Profile[^1];
        Assert.Equal(200.0, last.S, 9);
        Assert.Equal(0.0, last.Z, 9);
    }

    [Fact]
    public void Bvc_sits_on_incoming_straight_grade()
    {
        // BVC 在变坡点前 half=49 处，沿进坡 g1=+0.10 回退：z = 10 − 0.10*49 = 5.1
        var r = RoadVerticalCurve.Smooth(S3, new double[] { 0, 10, 0 }, 1.0, 500);
        // 首点之后第一个插入点即 BVC
        var bvc = r.Profile[1];
        Assert.Equal(51.0, bvc.S, 6);
        Assert.Equal(5.1, bvc.Z, 6);
    }

    [Fact]
    public void Evc_rejoins_outgoing_straight_grade()
    {
        // EVC 在 s=149，z=5.1；沿出坡 g2=−0.10 到 s=200 应回到 z=0（端点）
        var r = RoadVerticalCurve.Smooth(S3, new double[] { 0, 10, 0 }, 1.0, 500);
        // EVC = 最后一个竖曲线采样点（末端点之前）
        var evc = r.Profile[^2];
        Assert.Equal(149.0, evc.S, 6);
        Assert.Equal(5.1, evc.Z, 6);
        // 出坡直线外推到 200：5.1 + (−0.10)*(200−149) = 0
        double zEnd = evc.Z + (-0.10) * (200 - evc.S);
        Assert.Equal(0.0, zEnd, 6);
    }

    [Fact]
    public void Parabola_apex_rounds_off_the_crest_vertex()
    {
        // 抛物线顶点在 x=half=49（dz/dx=0）：z = 5.1 + 0.10*49 + (−0.20)/(2*98)*49² = 7.55
        var r = RoadVerticalCurve.Smooth(S3, new double[] { 0, 10, 0 }, 1.0, 500);
        double apex = r.Profile.Max(p => p.Z);
        Assert.Equal(7.55, apex, 4);
        // 顶点低于原折点 10（竖曲线削平尖角）
        Assert.True(apex < 10.0);
    }

    [Fact]
    public void Parabola_samples_follow_the_quadratic_formula()
    {
        // 逐点核对 z = zb + g1·x + (g2−g1)/(2·span)·x²，zb=5.1, g1=0.10, g2=−0.10, span=98
        var r = RoadVerticalCurve.Smooth(S3, new double[] { 0, 10, 0 }, 1.0, 500, samplesPerVc: 4);
        double sb = 51.0, zb = 5.1, g1 = 0.10, span = 98.0, dgc = -0.20;
        foreach (var (S, Z) in r.Profile.Where(p => p.S > sb + 1e-9 && p.S < sb + span - 1e-9))
        {
            double x = S - sb;
            double expect = zb + g1 * x + dgc / (2.0 * span) * x * x;
            Assert.Equal(expect, Z, 6);
        }
    }

    [Fact]
    public void Large_radius_within_segment_reports_no_violation()
    {
        // rV=100 小半径：Lv=100*0.20=20，half=min(10,49)=10 → rAch=2*10/0.20=100 = rV → 不计不达标
        var r = RoadVerticalCurve.Smooth(S3, new double[] { 0, 10, 0 }, 1.0, 100);
        Assert.Equal(1, r.Count);
        Assert.Equal(0, r.Violations);
        Assert.Equal(100.0, r.MinRadiusM, 6);
    }

    // ── 移植原 Tests.PitMineApp/ProfileSmootherTests 已知值(等价性由构造; 原 ProfileSmoother.VerticalCurves = Kylin Smooth) ──
    [Fact]
    public void Orig_Sag_InsertsOneVerticalCurve_PreservesEndpoints()
    {
        // 凹型变坡(−8% → +8%, Δ16%) → 插 1 条竖曲线, R=Lv/ΔG=48/0.16=300, 端点保持, 凹顶抬圆。
        var s = new double[] { 0, 100, 200 };
        var z = new double[] { 0, -8, 0 };
        var vc = RoadVerticalCurve.Smooth(s, z, triggerPct: 2, rV: 300);
        Assert.Equal(1, vc.Count);
        Assert.Equal(300.0, vc.MinRadiusM, 0);
        Assert.Equal(0, vc.Violations);
        Assert.Equal((0.0, 0.0), vc.Profile[0]);
        Assert.Equal(200.0, vc.Profile[^1].S, 3);
        Assert.Equal(0.0, vc.Profile[^1].Z, 3);
        Assert.True(vc.Profile.Count > 3);
        double zAt100 = double.NaN;
        foreach (var p in vc.Profile) if (Math.Abs(p.S - 100) < 1e-6) zAt100 = p.Z;
        Assert.True(zAt100 > -8.0 + 0.1, "竖曲线应把凹顶抬圆");
    }

    [Fact]
    public void Orig_ShortSegments_ClampRadius_FlagsViolation()
    {
        // 短段放不下 Lv → clamp, R < R_v, 计 violation。
        var vc = RoadVerticalCurve.Smooth(new double[] { 0, 10, 20 }, new double[] { 0, -0.8, 0 }, triggerPct: 2, rV: 300);
        Assert.Equal(1, vc.Count);
        Assert.Equal(1, vc.Violations);
        Assert.True(vc.MinRadiusM < 300.0);
    }

    [Fact]
    public void Orig_NoGradeChange_NoVerticalCurve()
    {
        var vc = RoadVerticalCurve.Smooth(new double[] { 0, 100, 200 }, new double[] { 0, -5, -10 }, 2, 300);
        Assert.Equal(0, vc.Count);
        Assert.Equal(3, vc.Profile.Count);
    }

    [Fact]
    public void Orig_SmallGradeChange_BelowTrigger_NoVerticalCurve()
    {
        var vc = RoadVerticalCurve.Smooth(new double[] { 0, 100, 200 }, new double[] { 0, -5, -9 }, 2, 300);
        Assert.Equal(0, vc.Count);
    }
}
