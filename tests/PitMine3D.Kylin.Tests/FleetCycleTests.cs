using System;
using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>车铲循环产能求解回归（忠实 FleetMatcher §3-5 公式，验关系恒等式）。</summary>
public class FleetCycleTests
{
    [Fact]
    public void Buckets_takt_and_capacity_relationships()
    {
        var r = FleetCycle.Solve(bucketM3: 12, payloadT: 100, inSituDensity: 2.5, ks: 1.5, haulKm: 3, trucks: 6);
        // 斗数 m = 100/(12·(2.5/1.5)·0.85) ≈ 5.88
        Assert.Equal(100.0 / (12 * (2.5 / 1.5) * 0.85), r.BucketsPerTruck, 2);
        Assert.Equal(r.BucketsPerTruck * 0.53, r.LoadTaktMin, 2);       // τ_L = m·t_s
        Assert.True(r.CycleTimeMin > r.LoadTaktMin);                    // T_c > τ_L
        // 关系恒等式(公式核):
        Assert.Equal(6 * r.LoadTaktMin / r.CycleTimeMin, r.MatchFactor, 2);
        // P_sh/P_fl 关系(Result 已按未舍入值算, 此以舍入值反算 → 允 0.5% 舍入差):
        Assert.True(System.Math.Abs(60.0 * 100 / r.LoadTaktMin - r.ShovelCapTph) <= r.ShovelCapTph * 0.005);
        Assert.True(System.Math.Abs(6 * 60.0 * 100 / r.CycleTimeMin - r.FleetCapTph) <= r.FleetCapTph * 0.005);
        Assert.True(r.GroupCapM3PerH > 0);
    }

    [Fact]
    public void Group_capacity_is_bottleneck_over_density_times_eff()
    {
        var r = FleetCycle.Solve(12, 100, 2.5, 1.5, 3, trucks: 6, efficiency: 0.8);
        double p = Math.Min(r.ShovelCapTph, r.FleetCapTph);
        Assert.True(Math.Abs(p / 2.5 * 0.8 - r.GroupCapM3PerH) <= r.GroupCapM3PerH * 0.005);  // q = min(Psh,Pfl)/ρ实·η(允舍入差)
    }

    [Fact]
    public void Optimal_trucks_balance_match_factor_near_one()
    {
        var r = FleetCycle.Solve(12, 100, 2.5, 1.5, 3, trucks: 0);     // 用 n*
        Assert.Equal(r.OptimalTrucks, r.Trucks);
        Assert.InRange(r.MatchFactor, 0.75, 1.25);                     // n* 使 MF 接近 1
    }

    [Fact]
    public void Fewer_trucks_shovel_waits()
    {
        var r = FleetCycle.Solve(12, 100, 2.5, 1.5, 5, trucks: 1);     // 1 车 → 车队运力瓶颈
        Assert.True(r.MatchFactor < 0.9);
        Assert.Contains("铲待车", r.Bottleneck);
    }
}
