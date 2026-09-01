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
}
