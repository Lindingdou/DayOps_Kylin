using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>一组指标值的统计（煤质指标按煤层统计的口径）。</summary>
public readonly record struct QualityStats(int Count, double Mean, double Std, double Min, double Max, double P25, double P50, double P75);

/// <summary>
/// 煤质指标统计核 —— 移植自原 `GeoDataBase.CoalQualityService.StatsBySeam` 的纯算部分
/// (样本标准差 n−1、线性插值百分位)。DB 取数是阻塞部分, 此处只保留可托管、可单测的统计数学。
/// </summary>
public static class QualityStatistics
{
    /// <summary>对一组值算 计数/均值/样本标准差/最小/最大/P25/P50/P75。空集返回全 0。</summary>
    public static QualityStats Compute(IReadOnlyList<double> values)
    {
        if (values == null || values.Count == 0) return new QualityStats(0, 0, 0, 0, 0, 0, 0, 0);
        var sorted = values.OrderBy(v => v).ToList();
        double mean = sorted.Average();
        double std = sorted.Count > 1
            ? Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / (sorted.Count - 1))   // 样本标准差(n−1)
            : 0;
        return new QualityStats(
            sorted.Count, mean, std, sorted[0], sorted[^1],
            Percentile(sorted, 0.25), Percentile(sorted, 0.50), Percentile(sorted, 0.75));
    }

    /// <summary>已排序序列的线性插值百分位（移植自原逻辑）。</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double pos = (sorted.Count - 1) * p;
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];
        double frac = pos - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}
