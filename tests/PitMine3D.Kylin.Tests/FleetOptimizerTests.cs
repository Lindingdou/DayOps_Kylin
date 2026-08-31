using System.Collections.Generic;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>设备智能编组优化回归（忠实移植 FleetOptimizer：物理产能+M/M/c排队+DP最小卡车数）。</summary>
public class FleetOptimizerTests
{
    private static FleetDispatchRule Rule() => new()
    {
        ShovelModel = "WK-35", TruckModel = "TR100", CycleTimeMin = 25, RecommendedTruckCount = 5,
        TruckPayloadT = 100, BucketLoadsPerTruck = 6, ShovelBucketM3 = 20, EfficiencyScore = 80,
    };

    [Fact]
    public void ErlangC_boundary_and_monotone()
    {
        Assert.Equal(1.0, FleetOptimizer.ErlangC(3, 1.0), 6);        // ρ≥1 → 必排队
        Assert.Equal(0.5, FleetOptimizer.ErlangC(1, 0.5), 3);        // M/M/1 排队概率 = ρ
        Assert.True(FleetOptimizer.ErlangC(2, 0.8) > FleetOptimizer.ErlangC(2, 0.4)); // ρ 越大越易排队
        Assert.InRange(FleetOptimizer.ErlangC(3, 0.6), 0, 1);
    }

    [Fact]
    public void Optimize_meets_target_when_unconstrained()
    {
        var inp = new FleetOptInput { DailyTargetM3 = 50000, Rules = new List<FleetDispatchRule> { Rule() } };
        var r = FleetOptimizer.Optimize(inp);
        Assert.NotEmpty(r.Groups);
        Assert.True(r.TargetMet, $"无在籍约束应达标, 实达 {r.TotalDailyM3}");
        Assert.True(r.TotalDailyM3 >= 50000 - 1e-6);
        Assert.True(r.TotalShovels > 0 && r.TotalTrucks == r.TotalShovels * 5);   // 每铲配 5 车
        var g = r.Groups[0];
        Assert.InRange(g.MatchFactor, 0.5, 0.8);                     // 5×3.18/25≈0.64
        Assert.True(g.GroupDailyM3 > 0);
    }

    [Fact]
    public void Optimize_respects_inventory_limit()
    {
        var inp = new FleetOptInput
        {
            DailyTargetM3 = 500000,   // 高目标, 无约束需很多台
            Rules = new List<FleetDispatchRule> { Rule() },
            ShovelInventory = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase) { ["WK-35"] = 3 },
        };
        var r = FleetOptimizer.Optimize(inp);
        Assert.All(r.Groups, g => Assert.True(g.ShovelCount <= 3, $"在籍3台不应超, 实 {g.ShovelCount}"));
        Assert.False(r.TargetMet);                                   // 3 台不够 → 缺口
        Assert.Contains(r.Notes, n => n.Contains("不足") || n.Contains("在籍"));
    }

    [Fact]
    public void Optimize_empty_rules_notes()
    {
        var r = FleetOptimizer.Optimize(new FleetOptInput { DailyTargetM3 = 10000 });
        Assert.Empty(r.Groups);
        Assert.NotEmpty(r.Notes);
    }

    [Fact]
    public void Optimize_zero_target_notes()
    {
        var r = FleetOptimizer.Optimize(new FleetOptInput { DailyTargetM3 = 0, Rules = new List<FleetDispatchRule> { Rule() } });
        Assert.Empty(r.Groups);
    }

    [Fact]
    public void Fleet_dispatch_rules_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rules = GeoDataQueries.GetFleetDispatchRules(db.Connection);
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.TruckPayloadT > 0 && r.CycleTimeMin > 0);   // join 到载重
        // 用种子规则跑优化不崩 + 达标或给缺口说明
        var r0 = FleetOptimizer.Optimize(new FleetOptInput { DailyTargetM3 = 100000, Rules = rules });
        Assert.True(r0.Groups.Count > 0 || r0.Notes.Count > 0);
    }
}
