using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 直线坑线自动布线（StraightRampAutoRouter.Route）已知值回归。忠实移植原
/// MineAssLib.RoadLayout.StraightRampAutoRouter —— 纯几何、可单测。
/// 用同心方环(周长 P=8·半边)在解析可算的高差/限坡下合成已知值:
///   逐级需展线 L=ΔH/i；可行判据 P≥L；缓坡段展线 L=ΔH/g+n·lEase·(1−ge/g)；折返兜底腿数。
/// </summary>
public class StraightRampAutoRouterTests
{
    // 半边 h、标高 z 的方环(4 角, 隐式闭合 → 周长 8h)。角序使 X 最大角 =(h,h)=默认起坡口。
    private static RampBenchLine Square(double h, double z, double berm = 0) => new()
    {
        Level = z,
        BermWidth = berm,
        Crest = new List<(double X, double Y, double Z)> { (h, h, z), (-h, h, z), (-h, -h, z), (h, -h, z) },
    };

    [Fact]
    public void Straight_leg_placed_when_perimeter_exceeds_required_run()
    {
        // 顶环 Z=10、底环 Z=0，两方环半边 100(周长 800)。i=8% → L=ΔH/i=10/0.08=125；800≥125 → 斜坡道。
        var benches = new List<RampBenchLine> { Square(100, 10), Square(100, 0) };
        var r = StraightRampAutoRouter.Route(benches, new StraightRampRouteOptions { GradePct = 8 });
        Assert.True(r.Success);
        Assert.Equal(1, r.StraightLegs);
        Assert.Equal(0, r.SwitchbackLegs);
        Assert.True(r.ReachedBottom);
        Assert.Equal(0, r.ReachedZ, 6);            // 落到底环标高
        Assert.Single(r.Levels);
        Assert.True(r.Levels[0].Feasible);
        Assert.Equal(RampForm.Straight, r.Levels[0].Form);
        Assert.Equal(125.0, r.Levels[0].RequiredRunM, 6);   // L = ΔH/i 已知值
        Assert.Equal(800.0, r.Levels[0].LowerPerimeterM, 6); // 底环周长 8·100
    }

    [Fact]
    public void Report_and_stop_when_ring_too_small_and_no_switchback()
    {
        // 底环半边 5(周长 40) 但需 L=100/0.08=1250 ≫ 40；未启折返 → 报告并止步(首级即不可行)。
        var benches = new List<RampBenchLine> { Square(100, 100), Square(5, 0) };
        var r = StraightRampAutoRouter.Route(benches, new StraightRampRouteOptions { GradePct = 8 });
        Assert.False(r.Success);
        Assert.Equal(0, r.StraightLegs);
        Assert.Equal(0, r.SwitchbackLegs);
        Assert.Single(r.Levels);
        Assert.False(r.Levels[0].Feasible);
        Assert.Equal(1250.0, r.Levels[0].RequiredRunM, 6);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Switchback_fallback_folds_run_into_legs_when_enabled()
    {
        // 同上不可行几何, 但启折返兜底(R=3, 占位 2R=6 ≤ 周长 40) → 折返可行。
        // 每腿沿帮跨度 s=min(P·0.45,L)=min(18,1250)=18；腿数 legs=max(2,⌈1250/18⌉)=70。
        var benches = new List<RampBenchLine> { Square(100, 100), Square(5, 0) };
        var opt = new StraightRampRouteOptions { GradePct = 8, AllowSwitchbackFallback = true, MinTurnRadius = 3 };
        var r = StraightRampAutoRouter.Route(benches, opt);
        Assert.True(r.Success);
        Assert.Equal(0, r.StraightLegs);
        Assert.Equal(1, r.SwitchbackLegs);
        Assert.Equal(RampForm.Switchback, r.Levels[0].Form);
        Assert.True(r.Levels[0].Feasible);
        Assert.Equal(3.0, r.Levels[0].TurnRadius, 6);
        Assert.Equal(70, r.Levels[0].Legs);        // ⌈L/s⌉ 已知值
    }

    [Fact]
    public void Multi_level_chain_connects_every_bench_to_bottom()
    {
        // 三同心方环 Z=20/10/0(半边 100)。每级 ΔH=10 → L=125 ≤ 800 → 全程斜坡道贯通到底。
        var benches = new List<RampBenchLine> { Square(100, 20), Square(100, 10), Square(100, 0) };
        var r = StraightRampAutoRouter.Route(benches, new StraightRampRouteOptions { GradePct = 8 });
        Assert.True(r.Success);
        Assert.Equal(2, r.LevelsTotal);            // 环数-1
        Assert.Equal(2, r.LevelsConnected);
        Assert.Equal(2, r.StraightLegs);
        Assert.True(r.ReachedBottom);
        Assert.Equal(0, r.ReachedZ, 6);
        Assert.Equal(3, r.Centerline.Count);       // 起坡口 + 2 落点
        // 中线 Z 单调下降 20 → 10 → 0
        Assert.Equal(20, r.Centerline[0].Z, 6);
        Assert.Equal(10, r.Centerline[1].Z, 6);
        Assert.Equal(0, r.Centerline[2].Z, 6);
    }

    [Fact]
    public void Ease_sections_add_run_per_continuous_drop_formula()
    {
        // 大环半边 500(周长 4000, 充裕)。ΔH=100, i=10% → g=0.10, L_base=1000。
        // 缓坡段: hSeg=20 → 累计 100/20=5 段; ge=2%; lEase=50。
        // L = 1000 + 5·50·(1 − 0.02/0.10) = 1000 + 250·0.8 = 1200；EaseSections=5。
        var benches = new List<RampBenchLine> { Square(500, 100), Square(500, 0) };
        var opt = new StraightRampRouteOptions
        {
            GradePct = 10,
            MaxContinuousDropM = 20,
            EaseGradePct = 2,
            EaseMinLengthM = 50,
        };
        var r = StraightRampAutoRouter.Route(benches, opt);
        Assert.True(r.Success);
        Assert.Equal(5, r.Levels[0].EaseSections);
        Assert.Equal(1200.0, r.Levels[0].RequiredRunM, 6);  // 缓坡展线公式已知值
        Assert.Equal(5, r.TotalEaseSections);
    }

    [Fact]
    public void Default_portal_is_max_x_vertex_of_top_ring()
    {
        // 无种子 → 默认起坡口 = 最高环 X 最大顶点 =(100,100)。
        var benches = new List<RampBenchLine> { Square(100, 10), Square(100, 0) };
        var r = StraightRampAutoRouter.Route(benches, new StraightRampRouteOptions { GradePct = 8 });
        Assert.Equal(100, r.Centerline[0].X, 6);
        Assert.Equal(100, r.Centerline[0].Y, 6);
        Assert.Equal(10, r.Centerline[0].Z, 6);
    }

    [Fact]
    public void Guards_reject_insufficient_benches_and_bad_grade()
    {
        var one = new List<RampBenchLine> { Square(100, 10) };
        var rNone = StraightRampAutoRouter.Route(one, new StraightRampRouteOptions { GradePct = 8 });
        Assert.False(rNone.Success);
        Assert.NotNull(rNone.Error);               // 台阶不足

        var two = new List<RampBenchLine> { Square(100, 10), Square(100, 0) };
        var rBadGrade = StraightRampAutoRouter.Route(two, new StraightRampRouteOptions { GradePct = 0 });
        Assert.False(rBadGrade.Success);
        Assert.NotNull(rBadGrade.Error);           // 限坡无效
    }
}
