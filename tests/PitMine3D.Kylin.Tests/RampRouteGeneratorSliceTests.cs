using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 坑线选线器(RampRouteGenerator.Generate)已知值回归。忠实移植原 MineAssLib.RoadLayout.RampRouteGenerator
/// (Layer-1 选线, 纯几何可单测)——逐对相邻非工作帮台阶按 "可用帮长 vs ΔH/i" 判 斜坡道/转弯坡道/螺旋。
/// 默认约束: i=8% → needLen(ΔH=10)=125; 回头需平盘 2·R=20; 单车道年运力 (3600/30)·0.8·5000·90=43,200,000t。
/// </summary>
public class RampRouteGeneratorSliceTests
{
    // 标高 level、坡顶线长 crestLen(水平)、平盘宽 berm 的非工作帮台阶。
    private static RampBenchLine Bench(double level, double crestLen, double berm, bool working = false) => new()
    {
        Level = level,
        BermWidth = berm,
        IsWorkingWall = working,
        Crest = new List<(double X, double Y, double Z)> { (0, 0, level), (crestLen, 0, level) },
    };

    [Fact]
    public void Straight_when_available_length_meets_required_run()
    {
        // 上帮坡顶线长 200 ≥ 需 125(ΔH=10, i=8%) → 斜坡道, 回头半径 0。
        var benches = new List<RampBenchLine> { Bench(100, 200, 30), Bench(90, 200, 30) };
        var c = RampRouteGenerator.Generate(benches);
        var r = Assert.Single(c);
        Assert.Equal(RampForm.Straight, r.Form);
        Assert.Equal(125.0, r.RequiredLengthM, 6);
        Assert.Equal(200.0, r.AvailableLengthM, 6);
        Assert.Equal(0.0, r.TurnRadius, 6);
        Assert.True(r.GeomFeasible);
        Assert.Equal(100, r.FromLevel, 6);
        Assert.Equal(90, r.ToLevel, 6);
    }

    [Fact]
    public void Switchback_when_short_leg_but_berm_fits_turn()
    {
        // 坡顶线长 100 < 需 125, 但平盘 25 ≥ 回头需 20 → 转弯坡道(回头), 半径=卡车转弯半径 10。
        var benches = new List<RampBenchLine> { Bench(100, 100, 25), Bench(90, 100, 25) };
        var r = Assert.Single(RampRouteGenerator.Generate(benches));
        Assert.Equal(RampForm.Switchback, r.Form);
        Assert.Equal(10.0, r.TurnRadius, 6);
        Assert.True(r.GeomFeasible);
    }

    [Fact]
    public void Spiral_when_neither_leg_nor_berm_fits()
    {
        // 坡顶线长 100 < 需 125, 平盘 5 < 回头需 20 → 螺旋兜底。
        var benches = new List<RampBenchLine> { Bench(100, 100, 5), Bench(90, 100, 5) };
        var r = Assert.Single(RampRouteGenerator.Generate(benches));
        Assert.Equal(RampForm.Spiral, r.Form);
        Assert.Equal(10.0, r.TurnRadius, 6);
    }

    [Fact]
    public void Per_lane_capacity_is_known_value_from_defaults()
    {
        // (3600/30)·(80/100)·5000·90 = 43,200,000 t/期·车道。
        var benches = new List<RampBenchLine> { Bench(100, 200, 30), Bench(90, 200, 30) };
        var r = Assert.Single(RampRouteGenerator.Generate(benches));
        Assert.Equal(43_200_000.0, r.PerLaneCapacityTons, 3);
        Assert.Equal((0, 0, 100), r.PortalStart);   // 起坡点 = 上帮坡顶线首点
    }

    [Fact]
    public void Working_walls_excluded_and_levels_sorted_high_to_low()
    {
        // 乱序 + 一工作帮: 只在非工作帮相邻对间选线, 按标高降序配对。
        var benches = new List<RampBenchLine>
        {
            Bench(90, 200, 30), Bench(100, 200, 30), Bench(80, 200, 30, working: true), Bench(70, 200, 30)
        };
        var c = RampRouteGenerator.Generate(benches);
        // 非工作帮 {100,90,70} 降序配对 → (100→90),(90→70)
        Assert.Equal(2, c.Count);
        Assert.Equal(100, c[0].FromLevel, 6);
        Assert.Equal(90, c[0].ToLevel, 6);
        Assert.Equal(90, c[1].FromLevel, 6);
        Assert.Equal(70, c[1].ToLevel, 6);
    }

    [Fact]
    public void Tally_counts_forms_and_infeasible()
    {
        var benches = new List<RampBenchLine>
        {
            Bench(100, 200, 30),   // →90 斜坡道
            Bench(90, 100, 25),    // →80 转弯
            Bench(80, 100, 5),     // →70 螺旋
            Bench(70, 200, 30),
        };
        var c = RampRouteGenerator.Generate(benches);
        var (s, sb, sp, bad) = RampRouteGenerator.Tally(c);
        Assert.Equal(1, s);
        Assert.Equal(1, sb);
        Assert.Equal(1, sp);
        Assert.Equal(0, bad);
    }

    [Fact]
    public void Zero_grade_marks_infeasible()
    {
        var benches = new List<RampBenchLine> { Bench(100, 200, 30), Bench(90, 200, 30) };
        var c = RampRouteGenerator.Generate(benches, new RampRouteConstraints { MaxGradePct = 0 });
        var r = Assert.Single(c);
        Assert.False(r.GeomFeasible);   // needLen=+∞ & i≤0 → 不可行
    }

    [Fact]
    public void Generated_candidates_feed_layout_solver_end_to_end()
    {
        // Layer-1 选线 → 映射为 Layer-2 RoadLayoutSolver 候选 → 求解出方案(证两层贯通)。
        var benches = new List<RampBenchLine>
        {
            Bench(100, 200, 30), Bench(90, 200, 30), Bench(80, 200, 30)
        };
        var gen = RampRouteGenerator.Generate(benches);
        Assert.Equal(2, gen.Count);
        var cands = gen.Select(g => new RoadLayoutSolver.RampCand(
            g.FromLevel, g.ToLevel, g.RequiredLengthM, g.GeomFeasible, g.Note)).ToList();
        var res = RoadLayoutSolver.Solve(cands, demandTons: 1_000_000, perLaneTons: 500_000, haulUnitCost: 2);
        Assert.True(res.Success);
        Assert.NotNull(res.Recommended);
        Assert.Equal(3, res.Schemes.Count);
    }
}
