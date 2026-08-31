using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>属性统计/直方图回归（移植自 BlockReportGenerator：min/max/mean/std + 等宽桶）。</summary>
public class StatisticsTests
{
    [Fact]
    public void Basic_moments_correct()
    {
        var s = Statistics.Describe(new List<double> { 1, 2, 3, 4, 5 });
        Assert.Equal(5, s.Count);
        Assert.Equal(1, s.Min, 6);
        Assert.Equal(5, s.Max, 6);
        Assert.Equal(3, s.Mean, 6);
        Assert.Equal(3, s.Median, 6);
        // 总体方差 = ((2²+1²+0+1²+2²)/5)=2 → std=√2
        Assert.Equal(System.Math.Sqrt(2), s.Std, 6);
    }

    [Fact]
    public void Median_even_count_averages_middle_pair()
    {
        var s = Statistics.Describe(new List<double> { 10, 20, 30, 40 });
        Assert.Equal(25, s.Median, 6);   // (20+30)/2
    }

    [Fact]
    public void Histogram_counts_sum_to_n_and_uniform_is_flat()
    {
        // 0..99 均匀, 10 桶 → 每桶恰 10
        var data = Enumerable.Range(0, 100).Select(i => (double)i).ToList();
        var s = Statistics.Describe(data, buckets: 10);
        Assert.Equal(100, s.Histogram.Sum());          // 频数和 == N
        foreach (var c in s.Histogram)
            Assert.Equal(10, c);                         // 均匀 → 每桶 10
    }

    [Fact]
    public void All_equal_collapses_into_first_bucket()
    {
        var s = Statistics.Describe(new List<double> { 7, 7, 7, 7 }, buckets: 5);
        Assert.Equal(4, s.Histogram[0]);
        Assert.Equal(0, s.Std, 6);
        for (int i = 1; i < s.Histogram.Length; i++) Assert.Equal(0, s.Histogram[i]);
    }

    [Fact]
    public void Max_value_lands_in_last_bucket_not_overflow()
    {
        // 边界值 max 应落末桶(而非越界)
        var s = Statistics.Describe(new List<double> { 0, 5, 10 }, buckets: 2);
        Assert.Equal(3, s.Histogram.Sum());
        Assert.Equal(2, s.Histogram.Length);
        Assert.True(s.Histogram[1] >= 1);   // 10 落末桶
    }

    [Fact]
    public void Histogram_csv_header_rows_and_edges()
    {
        var s = Statistics.Describe(new List<double> { 0, 10 }, buckets: 4);
        var csv = Statistics.HistogramCsv(s);
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("bucket,low,high,count", lines[0]);
        Assert.Equal(5, lines.Length);                  // 表头 + 4 桶
        Assert.StartsWith("0,0,2.5,", lines[1]);        // 首桶 [0,2.5)
        Assert.EndsWith(",10,1", lines[4]);             // 末桶 high=Max=10, 含末值
    }

    [Fact]
    public void Empty_is_safe()
    {
        var s = Statistics.Describe(new List<double>());
        Assert.Equal(0, s.Count);
        Assert.Empty(s.Histogram);
        Assert.Equal("bucket,low,high,count\n", Statistics.HistogramCsv(s));
    }

    // ── 分位数 / 箱线(忠实原「均值/std/分位数...箱线」) ──
    [Fact]
    public void Quartiles_on_odd_series()
    {
        var s = Statistics.Describe(new List<double> { 1, 2, 3, 4, 5 });
        Assert.Equal(2, s.Q1, 6);        // 25th: rank=0.25·4=1 → sorted[1]=2
        Assert.Equal(3, s.Median, 6);
        Assert.Equal(4, s.Q3, 6);        // 75th: rank=0.75·4=3 → sorted[3]=4
    }

    [Fact]
    public void Percentile_linear_interpolation_between_order_stats()
    {
        var data = new List<double> { 10, 20, 30, 40 };   // 已升序
        Assert.Equal(17.5, Statistics.Percentile(data, 25), 6);   // rank=0.75 → 10+10·0.75
        Assert.Equal(32.5, Statistics.Percentile(data, 75), 6);   // rank=2.25 → 30+10·0.25
        Assert.Equal(10, Statistics.Percentile(data, 0), 6);      // 端点夹取
        Assert.Equal(40, Statistics.Percentile(data, 100), 6);
    }

    [Fact]
    public void Quartiles_on_uniform_0_to_100()
    {
        var data = Enumerable.Range(0, 101).Select(i => (double)i).ToList();
        var s = Statistics.Describe(data);
        Assert.Equal(25, s.Q1, 6);
        Assert.Equal(50, s.Median, 6);
        Assert.Equal(75, s.Q3, 6);
    }

    [Fact]
    public void Boxplot_csv_is_five_number_summary()
    {
        var s = Statistics.Describe(new List<double> { 1, 2, 3, 4, 5 });
        var lines = Statistics.BoxplotCsv(s).TrimEnd('\n').Split('\n');
        Assert.Equal("min,q1,median,q3,max", lines[0]);
        Assert.Equal("1,2,3,4,5", lines[1]);
    }

    [Fact]
    public void Q1_le_median_le_Q3_invariant()
    {
        var s = Statistics.Describe(new List<double> { 5, 1, 9, 3, 7, 2, 8 });
        Assert.True(s.Q1 <= s.Median && s.Median <= s.Q3, "Q1≤中位≤Q3");
        Assert.True(s.Min <= s.Q1 && s.Q3 <= s.Max);
    }
}
