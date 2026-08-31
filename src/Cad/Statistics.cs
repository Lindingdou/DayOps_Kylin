using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 属性统计摘要 —— 移植自原 BlockModelLib.Reporting 的「min/max/mean/std/count + 等宽桶直方图」。
/// 供块体品位 / 点云高程 / 任意数值属性的分布分析。纯逻辑、可单测。
/// </summary>
public static class Statistics
{
    public readonly record struct Summary(
        int Count, double Min, double Max, double Mean, double Std,
        double Median, double Q1, double Q3, long[] Histogram);

    /// <summary>百分位(p∈[0,100])——升序数据上的线性插值(order statistics)。空→0, 单点→该点。</summary>
    public static double Percentile(IReadOnlyList<double> sortedAsc, double p)
    {
        if (sortedAsc == null || sortedAsc.Count == 0) return 0;
        if (sortedAsc.Count == 1) return sortedAsc[0];
        if (p <= 0) return sortedAsc[0];
        if (p >= 100) return sortedAsc[^1];
        double rank = p / 100.0 * (sortedAsc.Count - 1);   // 0-based 秩
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        if (hi >= sortedAsc.Count) hi = sortedAsc.Count - 1;
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (rank - lo);
    }

    /// <summary>算 min/max/mean/std(总体,÷N)/median/Q1/Q3 + buckets 桶等宽直方图([Min,Max] 等分)。</summary>
    public static Summary Describe(IReadOnlyList<double> values, int buckets = 20)
    {
        if (values == null || values.Count == 0)
            return new Summary(0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<long>());
        int n = values.Count;
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (var v in values) { if (v < min) min = v; if (v > max) max = v; sum += v; }
        double mean = sum / n;
        double ss = 0;
        foreach (var v in values) { double d = v - mean; ss += d * d; }
        double std = Math.Sqrt(ss / n);           // 总体标准差(÷N)

        var sorted = new List<double>(values);
        sorted.Sort();
        double median = Percentile(sorted, 50);   // 与线性插值百分位一致(奇偶皆同旧中位)
        double q1 = Percentile(sorted, 25), q3 = Percentile(sorted, 75);

        int nb = Math.Max(1, buckets);
        var hist = new long[nb];
        double range = max - min;
        if (range < 1e-12) hist[0] = n;           // 全相等 → 全落首桶
        else
            foreach (var v in values)
            {
                int b = (int)((v - min) / range * nb);
                if (b < 0) b = 0; if (b >= nb) b = nb - 1;
                hist[b]++;
            }
        return new Summary(n, min, max, mean, std, median, q1, q3, hist);
    }

    /// <summary>直方图 → CSV(bucket,low,high,count)。桶边界由 Summary 的 Min/Max 等分推出。</summary>
    public static string HistogramCsv(Summary s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("bucket,low,high,count\n");
        int nb = s.Histogram.Length;
        double w = nb > 0 ? (s.Max - s.Min) / nb : 0;
        for (int i = 0; i < nb; i++)
        {
            double lo = s.Min + i * w, hi = (i == nb - 1) ? s.Max : s.Min + (i + 1) * w;
            sb.Append(i).Append(',').Append(lo.ToString("R", inv)).Append(',')
              .Append(hi.ToString("R", inv)).Append(',').Append(s.Histogram[i]).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>一行摘要文本。</summary>
    public static string SummaryLine(Summary s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"n={s.Count} min={s.Min.ToString("0.###", inv)} max={s.Max.ToString("0.###", inv)} " +
               $"mean={s.Mean.ToString("0.###", inv)} std={s.Std.ToString("0.###", inv)} " +
               $"Q1={s.Q1.ToString("0.###", inv)} median={s.Median.ToString("0.###", inv)} Q3={s.Q3.ToString("0.###", inv)}";
    }

    /// <summary>箱线图五数概括 → CSV(min,q1,median,q3,max)。IQR=Q3−Q1, 须要时可外推须。</summary>
    public static string BoxplotCsv(Summary s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return "min,q1,median,q3,max\n" +
               $"{s.Min.ToString("R", inv)},{s.Q1.ToString("R", inv)},{s.Median.ToString("R", inv)}," +
               $"{s.Q3.ToString("R", inv)},{s.Max.ToString("R", inv)}\n";
    }
}
