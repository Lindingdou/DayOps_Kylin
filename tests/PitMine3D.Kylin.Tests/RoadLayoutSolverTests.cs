using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Transport;
using PitMine3D.Kylin.Cad.RoadLayout;
using Xunit;

namespace PitMine3D.Kylin.Tests.RoadLayout;

/// <summary>「运量驱动布线」求解器(波4 多线拆分 + 成本 + 比选)纯逻辑测试。</summary>
public class RoadLayoutSolverTests
{
    /// <summary>直接喂候选链(绕开台阶线派生),确定性。chainLen = candCount×reqLenEach。</summary>
    private static RoadLayoutInput MakeInput(double demandTons, double perLane, double haulUnitCost,
        int candCount = 2, double reqLenEach = 500)
    {
        var cands = new List<RampCandidate>();
        double top = 100;
        for (int i = 0; i < candCount; i++)
            cands.Add(new RampCandidate
            {
                FromLevel = top - i * 15,
                ToLevel = top - (i + 1) * 15,
                Form = RampForm.Straight,
                GradePct = 8,
                RequiredLengthM = reqLenEach,
                AvailableLengthM = reqLenEach,
                PerLaneCapacityTons = perLane,
                GeomFeasible = true,
                PortalStart = (0, 0, top - i * 15),
            });
        return new RoadLayoutInput
        {
            Sources = new List<LoadingPoint> { new() { Id = "S1", OreTons = demandTons } },
            Sinks = new List<UnloadingPoint> { new() { Id = "U1", Kind = UnloadKind.Crusher } },
            Constraints = new TransportConstraintSettings { HaulUnitCost = haulUnitCost, LaneCount = 2 },
            Candidates = cands,
        };
    }

    [Fact]
    public void SmallDemand_SingleLaneSingleLine_Feasible()
    {
        // 需求 800 < 单车道运力 1000 → 1 线 1 道;成本 = 800 × (1km) × 2 = 1600。
        var r = new RoadLayoutSolver().Solve(MakeInput(demandTons: 800, perLane: 1000, haulUnitCost: 2));
        Assert.True(r.Success);
        var best = r.Schemes[0];
        Assert.True(best.Feasible);
        Assert.Single(best.Lines);
        Assert.Equal(1, best.Lines[0].LaneCount);
        Assert.Equal(1600, best.TotalHaulCost, 3);
    }

    [Fact]
    public void LargeDemand_SplitsIntoParallelLines_SingleLineFlaggedInfeasible()
    {
        // 需求 9000 / 单车道 1000 = 9 总车道 > 单线上限 4 → 须拆并行坑线。
        var r = new RoadLayoutSolver().Solve(MakeInput(demandTons: 9000, perLane: 1000, haulUnitCost: 0));
        Assert.True(r.Success);

        // 至少一套可行,推荐方案可行,且各线车道 ≤ 上限、总车道守恒为 9。
        Assert.Contains(r.Schemes, s => s.Feasible);
        var best = r.Schemes[0];
        Assert.True(best.Feasible);
        Assert.All(best.Lines, l => Assert.True(l.LaneCount <= RoadLayoutSolver.MaxLanesPerRoad));
        Assert.Equal(9, best.Lines.Sum(l => l.LaneCount));
        Assert.True(best.Lines.Sum(l => l.CapacityTons) + 1e-6 >= 9000);   // 运力够

        // 单线基线(1 线 9 道)应判不可行。
        Assert.Contains(r.Schemes, s => s.Lines.Count == 1 && !s.Feasible);
    }

    [Fact]
    public void LanesProportionalTonnage_UtilizationNeverExceedsOne()
    {
        var r = new RoadLayoutSolver().Solve(MakeInput(demandTons: 9000, perLane: 1000, haulUnitCost: 1));
        foreach (var s in r.Schemes.Where(s => s.Feasible))
            Assert.All(s.Lines, l => Assert.True(l.Utilization <= 1.0 + 1e-6, $"util={l.Utilization}"));
    }

    [Fact]
    public void HaulCost_ZeroWhenNoUnitPrice()
    {
        var r = new RoadLayoutSolver().Solve(MakeInput(demandTons: 5000, perLane: 1000, haulUnitCost: 0));
        Assert.All(r.Schemes, s => Assert.Equal(0, s.TotalHaulCost, 6));
    }

    [Fact]
    public void NoCandidates_Fails()
    {
        var input = MakeInput(1000, 1000, 1);
        input.Candidates = new List<RampCandidate>();   // 空候选
        input.Benches = new List<BenchLine>();          // 也无台阶线可派生
        var r = new RoadLayoutSolver().Solve(input);
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void RecommendedSchemeRanksFeasibleFirst()
    {
        var r = new RoadLayoutSolver().Solve(MakeInput(demandTons: 9000, perLane: 1000, haulUnitCost: 1));
        // 排序后首个必可行(只要存在可行方案)。
        Assert.True(r.Schemes[0].Feasible);
        Assert.True(r.Schemes[0].TotalScore > 0);
    }

    [Fact]
    public void MultiCandidatePerLevel_ChainPicksOnePerLevel_LengthNotInflated()
    {
        // 选线器现在每级产出多个**只差起坡位置**的候选;求解器须每级择一串成链。
        // 若整表直接串起来,展线长/运距/基建量会按每级候选数成倍放大(回归防线)。
        const int perLevel = 3;
        var cands = new List<RampCandidate>();
        for (int lv = 0; lv < 2; lv++)                    // 2 级
            for (int j = 0; j < perLevel; j++)            // 每级 3 个候选,仅起坡点不同
                cands.Add(new RampCandidate
                {
                    FromLevel = 100 - lv * 15,
                    ToLevel = 100 - (lv + 1) * 15,
                    Form = RampForm.Straight,
                    GradePct = 8,
                    RequiredLengthM = 500,
                    AvailableLengthM = 500,
                    PerLaneCapacityTons = 1000,
                    GeomFeasible = true,
                    PortalStart = (j * 10, 0, 100 - lv * 15),
                });

        var input = MakeInput(demandTons: 800, perLane: 1000, haulUnitCost: 2);
        input.Candidates = cands;
        var r = new RoadLayoutSolver().Solve(input);

        Assert.True(r.Success);
        var best = r.Schemes[0];
        // 链 = 2 段(每级一条),非 6 段;长度 1000m 而非 3000m。
        Assert.Equal(2, best.Lines[0].Segments.Count);
        Assert.Equal(1000, best.Lines[0].LengthM, 3);
        // 运营成本随之不被放大:800t × 1km × 2 = 1600。
        Assert.Equal(1600, best.TotalHaulCost, 3);
    }
}
