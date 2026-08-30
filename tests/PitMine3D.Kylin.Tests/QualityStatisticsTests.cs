using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>煤质指标统计回归（移植自 CoalQualityService.StatsBySeam 纯算）。</summary>
public class QualityStatisticsTests
{
    [Fact]
    public void Basic_stats_on_1to5()
    {
        var s = QualityStatistics.Compute(new List<double> { 5, 1, 3, 2, 4 });   // 乱序输入
        Assert.Equal(5, s.Count);
        Assert.Equal(3, s.Mean, 6);
        Assert.Equal(1, s.Min, 6);
        Assert.Equal(5, s.Max, 6);
        Assert.Equal(3, s.P50, 6);
        Assert.Equal(2, s.P25, 6);      // pos=(5-1)*.25=1 → sorted[1]=2
        Assert.Equal(4, s.P75, 6);
        Assert.Equal(Math.Sqrt(2.5), s.Std, 6);   // 样本标准差(n−1): sqrt(10/4)
    }

    [Fact]
    public void Percentile_interpolates()
    {
        var sorted = new List<double> { 0, 10 };
        Assert.Equal(2.5, QualityStatistics.Percentile(sorted, 0.25), 6);   // 线性插值
        Assert.Equal(5.0, QualityStatistics.Percentile(sorted, 0.50), 6);
    }

    [Fact]
    public void Single_value_zero_std()
    {
        var s = QualityStatistics.Compute(new List<double> { 42 });
        Assert.Equal(1, s.Count);
        Assert.Equal(42, s.Mean, 6);
        Assert.Equal(0, s.Std, 6);
        Assert.Equal(42, s.P50, 6);
    }

    [Fact]
    public void Empty_is_all_zero()
    {
        var s = QualityStatistics.Compute(new List<double>());
        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.Mean, 6);
    }
}
