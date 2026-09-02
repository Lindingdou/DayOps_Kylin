using System;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using RC = PitMine3D.Kylin.Cad.RoadLayoutSolver.RampCand;

namespace PitMine3D.Kylin.Tests;

/// <summary>运输道路布局求解回归（忠实原 RoadLayoutSolver 方案构建核: 运量定车道→拆线→可行/成本）。</summary>
public class RoadLayoutSolverTests
{
    [Fact]
    public void Small_demand_single_lane_all_feasible_known_values()
    {
        var r = RoadLayoutSolver.Solve(new[] { new RC(100, 50, 500, true, "") }, demandTons: 800, perLaneTons: 1000, haulUnitCost: 2);
        Assert.True(r.Success);
        Assert.Equal(3, r.Schemes.Count);
        Assert.All(r.Schemes, s => Assert.True(s.Feasible));         // 需求 800 < 单车道 1000 → 皆可行
        Assert.NotNull(r.Recommended);
        Assert.True(r.Recommended!.Feasible);
        var compact = r.Schemes[0];
        Assert.Equal(1, compact.TotalLanes);                          // ⌈800/1000⌉=1
        Assert.Single(compact.Lines);
        Assert.Equal(0.8, compact.Lines[0].Utilization, 6);           // 800/1000
        Assert.Equal(800, compact.TotalHaulCostYuan, 4);              // 800×(500/1000)×2
    }

    [Fact]
    public void Large_demand_splits_lanes_and_respects_per_road_limit()
    {
        // 需求 3500, 单车道 1000 → 总车道 4。
        var r = RoadLayoutSolver.Solve(new[] { new RC(100, 0, 800, true, "") }, 3500, 1000, 2);
        var compact = r.Schemes[0];                                   // 紧凑 每线≤2 → 2 线×2 车道
        Assert.Equal(4, compact.TotalLanes);
        Assert.Equal(2, compact.Lines.Count);
        Assert.All(compact.Lines, l => Assert.Equal(2, l.LaneCount));
        Assert.True(compact.Feasible);                               // 容 4000≥3500, 每线 2≤4
        var single = r.Schemes[2];                                   // 单线 4 车道
        Assert.Single(single.Lines);
        Assert.Equal(4, single.Lines[0].LaneCount);
        Assert.True(single.Feasible);                               // 4≤上限 4
    }

    [Fact]
    public void Score_weighted_by_objective_and_gates_infeasible()
    {
        // 单候选(展线500,可行) 需求800/单车道1000 → 紧凑方案 1线1车道·利用率0.8·capex500m。
        var r = RoadLayoutSolver.Solve(new[] { new RC(100, 50, 500, true, "") }, 800, 1000, 2);
        var s = r.Schemes[0];
        // 默认权重(0.8,0.2): capexKm=0.5 → capexScore=100/1.5=66.67; utilScore=0.8·100=80。Score=0.8·66.67+0.2·80=69.33。
        Assert.Equal(0.8 * (100.0 / 1.5) + 0.2 * 80.0, s.Score, 4);
        // 均衡目标(0.6,0.4)权重不同 → 分不同。
        var bal = RoadLayoutSolver.Solve(new[] { new RC(100, 50, 500, true, "") }, 800, 1000, 2, objective: "均衡");
        Assert.Equal(0.6 * (100.0 / 1.5) + 0.4 * 80.0, bal.Schemes[0].Score, 4);
        // 不可行方案 Score=0。
        var bad = RoadLayoutSolver.Solve(new[] { new RC(100, 0, 500, false, "坡度超限") }, 800, 1000, 2);
        Assert.All(bad.Schemes, sc => Assert.Equal(0, sc.Score, 6));
        // 推荐 = 最高分可行方案。
        Assert.NotNull(r.Recommended);
        Assert.Equal(r.Schemes.Where(x => x.Feasible).Max(x => x.Score), r.Recommended!.Score, 6);
    }

    [Fact]
    public void Infeasible_geometry_and_lane_overflow_and_empty()
    {
        // 几何不可行候选 → 全方案不可行, 违规带原因。
        var bad = RoadLayoutSolver.Solve(new[] { new RC(100, 0, 500, false, "坡度超限") }, 800, 1000, 2);
        Assert.All(bad.Schemes, s => Assert.False(s.Feasible));
        Assert.All(bad.Schemes, s => Assert.Contains(s.Violations, v => v.Contains("坡度超限")));
        // 单线超车道上限: 需求 6000 → 总车道 6 > 上限 4。
        var big = RoadLayoutSolver.Solve(new[] { new RC(100, 0, 500, true, "") }, 6000, 1000, 2);
        Assert.False(big.Schemes[2].Feasible);                       // 单线 6 车道 > 4
        Assert.Contains(big.Schemes[2].Violations, v => v.Contains("车道"));
        Assert.True(big.Schemes[0].Feasible);                        // 紧凑 3 线×2 可行
        // 空候选 → 不成功。
        Assert.False(RoadLayoutSolver.Solve(Array.Empty<RC>(), 800, 1000, 2).Success);
    }

    // ── 移植原版 Tests.MineAssLib/RoadLayoutSolverTests 的 6 已知值(等价性由构造保证) ──
    // 原用输入式 API(RoadLayoutInput+RampCandidate链, 2 候选×500=1km); Kylin 用 RampCand+参数式,
    // 原 r.Schemes[0](排序后最优)映射 Kylin r.Recommended(=最高分可行)。链长聚合 chainLenM=Σ RequiredLengthM。
    private static RC[] Chain2() => new[] { new RC(100, 85, 500, true, ""), new RC(85, 70, 500, true, "") };

    [Fact]
    public void Orig_small_demand_single_lane_cost_1600()
    {
        // 需求 800 < 单车道 1000 → 1 线 1 道; 成本 = 800 × (1km) × 2 = 1600(2 候选×500=1km 验链长聚合)。
        var r = RoadLayoutSolver.Solve(Chain2(), 800, 1000, 2);
        Assert.True(r.Success);
        Assert.NotNull(r.Recommended);
        Assert.True(r.Recommended!.Feasible);
        Assert.Single(r.Recommended.Lines);
        Assert.Equal(1, r.Recommended.Lines[0].LaneCount);
        Assert.Equal(1600, r.Recommended.TotalHaulCostYuan, 3);   // 原版同值(链长 1km 非 500m)
    }

    [Fact]
    public void Orig_large_demand_splits_into_9_lanes_single_infeasible()
    {
        // 需求 9000 / 单车道 1000 = 9 总车道 > 单线上限 4 → 拆并行; 单线基线判不可行。
        var r = RoadLayoutSolver.Solve(Chain2(), 9000, 1000, 0);
        Assert.True(r.Success);
        Assert.Contains(r.Schemes, s => s.Feasible);
        var best = r.Recommended!;
        Assert.True(best.Feasible);
        Assert.All(best.Lines, l => Assert.True(l.LaneCount <= RoadLayoutSolver.MaxLanesPerRoad));
        Assert.Equal(9, best.Lines.Sum(l => l.LaneCount));                 // 总车道守恒 9
        Assert.True(best.Lines.Sum(l => l.CapacityTons) + 1e-6 >= 9000);   // 运力够
        Assert.Contains(r.Schemes, s => s.Lines.Count == 1 && !s.Feasible); // 单线 9 道不可行
    }

    [Fact]
    public void Orig_lanes_proportional_utilization_never_exceeds_one()
    {
        var r = RoadLayoutSolver.Solve(Chain2(), 9000, 1000, 1);
        foreach (var s in r.Schemes.Where(s => s.Feasible))
            Assert.All(s.Lines, l => Assert.True(l.Utilization <= 1.0 + 1e-6, $"util={l.Utilization}"));
    }

    [Fact]
    public void Orig_haul_cost_zero_when_no_unit_price()
    {
        var r = RoadLayoutSolver.Solve(Chain2(), 5000, 1000, 0);
        Assert.All(r.Schemes, s => Assert.Equal(0, s.TotalHaulCostYuan, 6));
    }

    [Fact]
    public void Orig_no_candidates_fails()
    {
        var r = RoadLayoutSolver.Solve(Array.Empty<RC>(), 1000, 1000, 1);
        Assert.False(r.Success);
    }

    [Fact]
    public void Orig_recommended_scheme_ranks_feasible_first()
    {
        var r = RoadLayoutSolver.Solve(Chain2(), 9000, 1000, 1);
        Assert.NotNull(r.Recommended);
        Assert.True(r.Recommended!.Feasible);
        Assert.True(r.Recommended.Score > 0);
    }
}
