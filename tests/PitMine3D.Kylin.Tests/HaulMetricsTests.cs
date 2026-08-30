using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>运距指标回归（等效运距/行程时间/循环时间/加权平均）。对运输公式校核。</summary>
public class HaulMetricsTests
{
    private static TruckProfile T => TruckProfile.Default;   // 90t, 25/40kph, k=6/2

    [Fact]
    public void EquivalentLength_flat_is_identity()
    {
        Assert.Equal(1000.0, HaulMetrics.EquivalentLengthM(1000, 0, T, true), 6);
    }

    [Fact]
    public void EquivalentLength_uphill_loaded()
    {
        // factor = 1 + 6·0.08 = 1.48; 1000·1.48 = 1480
        Assert.Equal(1480.0, HaulMetrics.EquivalentLengthM(1000, 8, T, loaded: true), 6);
    }

    [Fact]
    public void EquivalentLength_uphill_empty_smaller_penalty()
    {
        // 空车 k = 6·0.4 = 2.4; factor = 1 + 2.4·0.08 = 1.192
        Assert.Equal(1192.0, HaulMetrics.EquivalentLengthM(1000, 8, T, loaded: false), 6);
    }

    [Fact]
    public void EquivalentLength_downhill_reduces_with_floor()
    {
        // factor = max(0.5, 1 − 2·0.10) = 0.8
        Assert.Equal(800.0, HaulMetrics.EquivalentLengthM(1000, -10, T, true), 6);
        // 陡下坡触底 0.5
        Assert.Equal(500.0, HaulMetrics.EquivalentLengthM(1000, -50, T, true), 6);
    }

    [Fact]
    public void TravelTime_flat_loaded()
    {
        // 1000m 平路重车 25km/h → (1/25)·60 = 2.4 min
        Assert.Equal(2.4, HaulMetrics.TravelTimeMin(1000, 0, T, true), 6);
    }

    [Fact]
    public void TravelTime_uphill_slower_than_flat()
    {
        double flat = HaulMetrics.TravelTimeMin(1000, 0, T, true);
        double up = HaulMetrics.TravelTimeMin(1000, 8, T, true);
        Assert.True(up > flat);
    }

    [Fact]
    public void CycleTime_sums_components()
    {
        Assert.Equal(2.4 + 1.5 + 3.0 + 1.5, HaulMetrics.CycleTimeMin(2.4, 1.5), 6);
    }

    [Fact]
    public void WeightedAverageHaul_and_max()
    {
        var s = new List<(double, double)> { (1000, 100), (2000, 300) };
        // (1000·100 + 2000·300)/400 = 700000/400 = 1750
        Assert.Equal(1750.0, HaulMetrics.WeightedAverageHaulM(s), 6);
        Assert.Equal(2000.0, HaulMetrics.MaxHaulM(new[] { 1000.0, 2000.0, 500.0 }), 6);
    }

    [Fact]
    public void WeightedAverage_empty_is_zero()
    {
        Assert.Equal(0.0, HaulMetrics.WeightedAverageHaulM(new List<(double, double)>()), 9);
        Assert.Equal(0.0, HaulMetrics.MaxHaulM(new double[0]), 9);
    }
}
