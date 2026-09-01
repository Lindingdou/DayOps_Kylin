using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>Weibull 失效分布拟合(中位秩回归)回归。</summary>
public class ReliabilityTests
{
    [Fact]
    public void WeibullFit_recovers_known_params_from_quantile_data()
    {
        // 由已知 β/η 的 Weibull 分位数(在中位秩处)生成间隔 → 数据恰落在 Weibull 线上 → 拟合精确还原
        double beta = 2.5, eta = 120; int n = 10;
        var t = new List<double>();
        for (int i = 0; i < n; i++)
        {
            double F = (i + 1 - 0.3) / (n + 0.4);
            t.Add(eta * Math.Pow(-Math.Log(1 - F), 1.0 / beta));
        }
        var (b, e, ok) = Reliability.WeibullFit(t);
        Assert.True(ok);
        Assert.Equal(beta, b, 3);
        Assert.Equal(eta, e, 1);
    }

    [Fact]
    public void WeibullFit_insufficient_or_degenerate_not_ok()
    {
        Assert.False(Reliability.WeibullFit(new List<double> { 10, 20 }).ok);          // n<3
        Assert.False(Reliability.WeibullFit(new List<double> { 5, 5, 5, 5 }).ok);      // 全相等 → 退化
        Assert.False(Reliability.WeibullFit(new List<double>()).ok);                   // 空
    }

    [Fact]
    public void Weibull_phase_bands_match_original_thresholds()
    {
        Assert.Equal("早期失效期(磨合/质量)", Reliability.Phase(0.6));   // β<0.85
        Assert.Equal("随机失效期(偶发)", Reliability.Phase(1.0));        // 0.85≤β≤1.15
        Assert.Equal("损耗失效期(老化)", Reliability.Phase(2.0));        // β>1.15
    }

    [Fact]
    public void PooledIntervals_per_equipment_gaps_only()
    {
        var d1 = new List<DateTime> { new(2025, 1, 1), new(2025, 1, 11), new(2025, 1, 21) };  // 间隔 10,10
        var d2 = new List<DateTime> { new(2025, 2, 1), new(2025, 2, 6) };                     // 间隔 5
        var iv = Reliability.PooledIntervalsDays(new[] { d1, d2 });
        Assert.Equal(3, iv.Count);              // 跨设备不串(2+1 个间隔)
        Assert.Contains(10.0, iv);
        Assert.Contains(5.0, iv);
    }
}
