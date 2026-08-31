using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>煤质深度分析回归（忠实移植 CoalQualityAnalytics 商品煤符合性核）。</summary>
public class CoalAnalyticsTests
{
    private static CoalSample S(long id, string seam, double? ad, double? st, double? qgr, double? vdaf = 30)
        => new(id, "H" + id, seam, 0, 0, 0, ad, null, st, null, qgr, null, vdaf, null);

    private static ComplianceLimits Lim(double adMax = 30, double stMax = 1.0, double qMin = 21)
        => new(UseClean: false, AshOn: true, AshMax: adMax, SulfurOn: true, SulfurMax: stMax,
               CalorificOn: true, CalorificMin: qMin, Calorific: CalorificKind.Qgr, VdafOn: false, VdafMin: 0, VdafMax: 0);

    [Fact]
    public void Pass_and_fail_by_each_limit()
    {
        var rows = new List<CoalSample>
        {
            S(1, "5", 25, 0.8, 23),   // 全达标 → pass
            S(2, "5", 35, 0.8, 23),   // 灰超 → fail
            S(3, "5", 25, 1.5, 23),   // 硫超 → fail
            S(4, "5", 25, 0.8, 18),   // 热不足 → fail
        };
        var r = CoalAnalytics.Evaluate(rows, Lim());
        Assert.Equal(4, r.Evaluated);
        Assert.Equal(1, r.Pass);
        Assert.Equal(25.0, r.PassPct, 3);
        Assert.Contains(r.Samples, e => e.Id == 2 && e.Fails.Contains("Ad"));
        Assert.Contains(r.Samples, e => e.Id == 3 && e.Fails.Contains("St"));
        Assert.Contains(r.Samples, e => e.Id == 4 && e.Fails.Contains("Q"));
    }

    [Fact]
    public void Insufficient_data_skipped_not_counted()
    {
        var rows = new List<CoalSample>
        {
            S(1, "5", 25, 0.8, 23),      // 完整 → 达标
            S(2, "5", null, 0.8, 23),    // 缺灰(灰限值启用) → 数据不足, 跳过
        };
        var r = CoalAnalytics.Evaluate(rows, Lim());
        Assert.Equal(1, r.Evaluated);        // 只 1 段可判
        Assert.Equal(1, r.Insufficient);     // 1 段不足
        Assert.Equal(1, r.Pass);
        Assert.Equal(100.0, r.PassPct, 3);   // 达标率只对可判段算
    }

    [Fact]
    public void By_seam_aggregation()
    {
        var rows = new List<CoalSample>
        {
            S(1, "5", 25, 0.8, 23),   // 煤层5 达标
            S(2, "5", 35, 0.8, 23),   // 煤层5 超标
            S(3, "9", 25, 0.8, 23),   // 煤层9 达标
        };
        var r = CoalAnalytics.Evaluate(rows, Lim());
        var s5 = r.BySeam.First(b => b.SeamCode == "5");
        Assert.Equal(2, s5.Evaluated); Assert.Equal(1, s5.Pass); Assert.Equal(50.0, s5.PassPct, 3);
        var s9 = r.BySeam.First(b => b.SeamCode == "9");
        Assert.Equal(1, s9.Pass); Assert.Equal(100.0, s9.PassPct, 3);
    }

    [Fact]
    public void Compliance_from_seed_runs()
    {
        using var db = GeoDatabase.OpenSeeded();
        var samples = GeoDataQueries.GetCoalSamples(db.Connection);
        Assert.NotEmpty(samples);
        var r = CoalAnalytics.Evaluate(samples, Lim());
        Assert.InRange(r.PassPct, 0, 100);
        Assert.Equal(r.Evaluated, r.Samples.Count(e => e.Evaluated));
    }

    // 带厚度/密度的样本(GradeTonnage/ByElevation 用)
    private static CoalSample ST(long id, string seam, double ad, double z, double th, double dens)
        => new(id, "H" + id, seam, 0, 0, z, ad, null, null, null, null, null, null, null, th, dens);

    [Fact]
    public void GradeTonnage_cumulative_monotone_below_cutoff()
    {
        var rows = new List<CoalSample>
        {
            ST(1, "5", 10, 100, 2, 1.4), ST(2, "5", 20, 90, 2, 1.4), ST(3, "5", 30, 80, 2, 1.4), ST(4, "5", 40, 70, 2, 1.4),
        };
        var r = CoalAnalytics.GradeTonnage(rows, "ad", useClean: false, steps: 10);
        Assert.True(r.BelowCutoff);                     // 灰分低者优 → 累计≤
        Assert.Equal(4, r.N);
        Assert.True(r.TotalMass > 0);
        // 累计"≤限值"质量应随限值递增(单调不减)
        for (int i = 1; i < r.Curve.Count; i++) Assert.True(r.Curve[i].CumMass >= r.Curve[i - 1].CumMass - 1e-9);
        Assert.Equal(100.0, r.Curve[^1].CumMassPct, 1);  // 最高限值含全部质量
    }

    [Fact]
    public void ByElevation_thickness_weighted_bands()
    {
        var rows = new List<CoalSample>
        {
            ST(1, "5", 10, 100, 3, 1.4), ST(2, "5", 20, 105, 1, 1.4),   // 带[100~120]: 加权=(10×3+20×1)/(3+1)... 但密度同→(10×3+20×1)/4=12.5
            ST(3, "5", 30, 130, 2, 1.4),                                 // 带[120~140]
        };
        var bands = CoalAnalytics.ByElevation(rows, "ad", useClean: false, band: 20);
        Assert.NotEmpty(bands);
        var b0 = bands[0];
        Assert.Equal(2, b0.N);
        Assert.Equal(12.5, b0.WeightedMean, 3);          // 厚度加权 (10×3+20×1)/4
        Assert.True(b0.Min <= b0.WeightedMean && b0.WeightedMean <= b0.Max);
    }

    [Fact]
    public void DetectOutliers_iqr_flags_extreme()
    {
        var rows = new List<CoalSample>();
        for (int i = 0; i < 10; i++) rows.Add(ST(i + 1, "5", 20 + i * 0.5, 0, 1, 1)); // 20~24.5 紧凑
        rows.Add(ST(100, "5", 90, 0, 1, 1));   // 明显偏高离群
        var r = CoalAnalytics.DetectOutliers(rows, "ad", useClean: false);
        Assert.True(r.N >= 11);
        Assert.Contains(r.Outliers, o => o.Id == 100 && o.Kind == "偏高");
        Assert.True(r.Upper > r.Q3);           // 上栅栏 > Q3
        Assert.True(r.Outliers[0].Severity > 0);
    }

    [Fact]
    public void DetectOutliers_small_sample_no_result()
    {
        var rows = new List<CoalSample> { ST(1, "5", 20, 0, 1, 1), ST(2, "5", 21, 0, 1, 1) };  // <5
        var r = CoalAnalytics.DetectOutliers(rows, "ad", useClean: false);
        Assert.Empty(r.Outliers);
    }
}
