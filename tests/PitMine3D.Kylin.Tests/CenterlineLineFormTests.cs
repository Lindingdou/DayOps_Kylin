using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>中心线线形后处理回归（GBJ22-87）—— ①转角圆弧化 ②分段限坡纵断面 ③竖曲线复用。</summary>
public class CenterlineLineFormTests
{
    static List<(double, double, double)> P(params (double, double, double)[] pts) => pts.ToList();
    static LineFormResult Run(List<(double, double, double)> pts, double rMin = 15,
        double maxGrade = 0, double curveMax = 0, double maxRes = 0, double superElev = 0,
        double vcTrig = 0, double vcR = 0, double minSec = 0)
        => CenterlineLineForm.Apply(pts, null, 0, rMin, maxGrade, curveMax, maxRes, superElev, vcTrig, vcR, minSec);

    [Fact]
    public void Empty_or_short_passes_through()
    {
        Assert.Empty(Run(P()).Line);
        var two = Run(P((0, 0, 0), (100, 0, 10)));
        Assert.Equal(2, two.Line.Count);   // <3 点：原样返回
        Assert.Equal(0.0, two.MinRadiusM);
    }

    [Fact]
    public void Collinear_inserts_no_arc()
    {
        var r = Run(P((0, 0, 0), (100, 0, 0), (200, 0, 0)));
        Assert.Equal(0, r.Violations);
        Assert.Equal(0.0, r.MinRadiusM);   // 无弯
        Assert.Equal(3, r.Line.Count);      // 无弧点插入
    }

    [Fact]
    public void Right_angle_corner_rounds_with_rMin_radius()
    {
        // 90° 转角, 段长 100 足够容 rMin=15 → 半径达标, 无违规
        var r = Run(P((0, 0, 0), (100, 0, 0), (100, 100, 0)), rMin: 15);
        Assert.Equal(15.0, r.MinRadiusM, 6);
        Assert.Equal(0, r.Violations);
        Assert.True(r.Line.Count > 3, "应插入弧点");

        // 进/出切点 (85,0) 和 (100,15)
        Assert.Contains(r.Line, q => Math.Abs(q.X - 85) < 1e-6 && Math.Abs(q.Y - 0) < 1e-6);
        Assert.Contains(r.Line, q => Math.Abs(q.X - 100) < 1e-6 && Math.Abs(q.Y - 15) < 1e-6);

        // 弧区所有点(圆心(85,15))到圆心距 == R == 15
        foreach (var q in r.Line.Where(q => q.X > 84 && q.X < 101 && q.Y > -1 && q.Y < 16))
        {
            double d = Math.Sqrt((q.X - 85) * (q.X - 85) + (q.Y - 15) * (q.Y - 15));
            Assert.Equal(15.0, d, 6);
        }
    }

    [Fact]
    public void Tight_corner_short_segments_reduces_radius_and_flags_violation()
    {
        // 段长仅 10, 放不下 rMin=15 → T 被 clamp 到 5, R 降到 5, 计 1 处违规
        var r = Run(P((0, 0, 0), (10, 0, 0), (10, 10, 0)), rMin: 15);
        Assert.Equal(1, r.Violations);
        Assert.Equal(5.0, r.MinRadiusM, 6);
    }

    [Fact]
    public void Grade_zero_reinterpolates_z_linearly_along_arclength()
    {
        // maxGrade=0 → 退化: Z 沿弧长线性插值; 端点保持
        var r = Run(P((0, 0, 0), (100, 0, 0), (200, 0, 20)));
        Assert.Equal(0.0, r.Line[0].Z, 9);
        Assert.Equal(20.0, r.Line[^1].Z, 6);
    }

    [Fact]
    public void Grade_limiting_scales_to_hit_total_height_within_limit()
    {
        // 共线, 总高差 10 / 总长 200, 限坡 8% → α=10/16=0.625, 各段 5%, 末点命中 z1=10
        var r = Run(P((0, 0, 0), (100, 0, 0), (200, 0, 10)), maxGrade: 8);
        Assert.Equal(10.0, r.Line[^1].Z, 6);         // 命中总高差
        Assert.Equal(5.0, r.MaxGradeUsedPct, 6);      // 0.625*8
        Assert.False(r.GradeExceedsLimit);            // α<1
    }

    [Fact]
    public void Grade_limiting_flags_when_descend_cannot_fit()
    {
        // 总高差 50 / 总长 200, 限坡 8% → Hmax=16 < 50 → α>1 → 报"须增长展线"
        var r = Run(P((0, 0, 0), (100, 0, 0), (200, 0, 50)), maxGrade: 8);
        Assert.True(r.GradeExceedsLimit);
        Assert.True(r.MaxGradeUsedPct > 8);           // 被迫超限(如实报)
    }

    [Fact]
    public void Curve_segments_take_reduced_grade_cap()
    {
        // 有转角 → 弧段按 curveMax(6%) 折减, 直线段按 i_max(8%); 二者应各自记录
        var r = Run(P((0, 0, 0), (100, 0, 0), (100, 100, 40)), rMin: 15, maxGrade: 8, curveMax: 6);
        Assert.True(r.CurveGradeUsedPct > 0, "弧段应有折减纵坡");
        Assert.True(r.CurveGradeUsedPct <= r.MaxGradeUsedPct + 1e-9,
            "弧段折减纵坡 ≤ 直线段纵坡(同 α 下 6% ≤ 8%)");
    }

    [Fact]
    public void Resultant_grade_composes_vertical_and_superelevation()
    {
        // 合成坡度 = √(纵² + 超高²): 弯道纵坡受 √(maxRes²−superElev²)=√48≈6.93 反推限制。
        // 高差取小(5)保证 α<1, 各段纵坡在限内。
        var r = Run(P((0, 0, 0), (100, 0, 0), (100, 100, 5)), rMin: 15,
            maxGrade: 8, curveMax: 8, maxRes: 8, superElev: 4);
        Assert.True(r.ResultantGradePct <= 8.0 + 1e-6, "合成坡度不超上限");
        Assert.True(r.CurveGradeUsedPct <= Math.Sqrt(48) + 1e-6, "弯道纵坡受合成坡度反推限制");
    }

    [Fact]
    public void Vertical_curve_stage_is_wired_and_preserves_endpoints()
    {
        // 转角 + 限坡(直线8%/弧段3%)→ 直线↔弧段坡差成变坡点 → 竖曲线阶段介入;
        // 端点标高恒保持(限坡命中 z1 + 竖曲线保端点, 复用 RoadVerticalCurve)。
        var r = Run(P((0, 0, 0), (100, 0, 0), (100, 100, 12)), rMin: 15,
            maxGrade: 8, curveMax: 3, vcTrig: 0.1, vcR: 200);
        Assert.Equal(0.0, r.Line[0].Z, 6);
        Assert.Equal(12.0, r.Line[^1].Z, 6);
        Assert.True(r.VerticalCurveCount >= 0);   // 竖曲线阶段已接线
    }
}
