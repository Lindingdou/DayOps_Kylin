using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 煤质深度分析补全 回归 —— 忠实移植原 CoalQualityAnalytics 的两处纯 C# 引擎方法:
/// AshCalorificRegression(灰分-发热量 OLS 回归 + 残差 z-score 离群) + OverallConclusions(七类综合结论)。
/// </summary>
public class CoalQualityAnalyticsTests
{
    // 位置记录: (Id,Hole,Seam, X,Y,Z, AdRaw,AdClean, StdRaw,StdClean, QgrD,QnetAd, VdafRaw,VdafClean, ...CakingG,...CoalType)
    static CoalSample CS(long id, string hole, string seam, double ad, double st, double qgr, double vdaf, string? type = null, double? g = null)
        => new(id, hole, seam, 0, 0, 0, ad, null, st, null, qgr, null, vdaf, null, null, null, null, g, null, type);

    // ── 灰分-发热量回归: Cal = 30 − 0.5·Ad 精确线性 → 斜率 −0.5 / 截距 30 / r²=1 ──
    [Fact]
    public void Regression_recovers_known_line()
    {
        var all = new List<CoalSample>();
        long id = 1;
        foreach (double ad in new[] { 10.0, 15, 20, 25, 30, 35 })
            all.Add(CS(id, "H" + id++, "3", ad, 0.5, 30 - 0.5 * ad, 30));
        var r = CoalAnalytics.AshCalorificRegression(all, CalorificKind.Qgr);
        Assert.Equal(6, r.N);
        Assert.Equal(-0.5, r.Slope, 6);
        Assert.Equal(30, r.Intercept, 6);
        Assert.Equal(1.0, r.R2, 6);
        Assert.Equal(10, r.XMin, 6);
        Assert.Equal(35, r.XMax, 6);
        Assert.Empty(r.Suspects);          // 全在线上 → 无离群
        Assert.Equal("Qgr,d", r.YName);
    }

    [Fact]
    public void Regression_flags_residual_outlier()
    {
        var all = new List<CoalSample>();
        long id = 1;
        for (double ad = 10; ad <= 28; ad += 2)   // 10 点精确在线
            all.Add(CS(id, "H" + id++, "3", ad, 0.5, 30 - 0.5 * ad, 30));
        all.Add(CS(999, "OUT", "3", 20, 0.5, 5, 30));   // Ad=20 处 Cal 应=20, 实=5 → 大残差
        var r = CoalAnalytics.AshCalorificRegression(all);
        Assert.NotEmpty(r.Suspects);
        Assert.Contains(r.Suspects, s => s.HoleId == "OUT");
        Assert.True(System.Math.Abs(r.Suspects[0].ZScore) >= 2.5);
    }

    [Fact]
    public void Regression_too_few_samples_returns_empty()
    {
        var all = new List<CoalSample> { CS(1, "H1", "3", 10, 0.5, 25, 30), CS(2, "H2", "3", 20, 0.5, 20, 30) };
        var r = CoalAnalytics.AshCalorificRegression(all);
        Assert.Equal(2, r.N);
        Assert.Equal(0, r.Slope, 6);
        Assert.Equal(0, r.R2, 6);
        Assert.Empty(r.Points);
    }

    // ── 综合结论: 2 煤层已知煤质 ──
    static List<CoalSample> TwoSeamSamples()
    {
        var all = new List<CoalSample>();
        long id = 1;
        // 3 煤: 低-中灰(15~17), 特低硫(0.5), 发热量随灰降(负相关)
        foreach (double ad in new[] { 15.0, 16, 17 }) all.Add(CS(id, "H" + id++, "3", ad, 0.5, 30 - 0.3 * ad, 31, "1/3焦煤"));
        // 5 煤: 中灰(25~27)
        foreach (double ad in new[] { 25.0, 26, 27 }) all.Add(CS(id, "H" + id++, "5", ad, 0.5, 30 - 0.3 * ad, 31, "1/3焦煤"));
        return all;
    }

    [Fact]
    public void Conclusions_cover_expected_categories()
    {
        var cs = CoalAnalytics.OverallConclusions(TwoSeamSamples());
        var cats = cs.Select(c => c.Category).ToHashSet();
        Assert.Contains("煤质表征", cats);
        Assert.Contains("煤层对比", cats);       // 2 煤层
        Assert.Contains("均匀性", cats);
        Assert.Contains("数据质量", cats);
        Assert.Contains("用途建议", cats);
    }

    [Fact]
    public void Conclusions_characterize_ash_and_seam_order()
    {
        var cs = CoalAnalytics.OverallConclusions(TwoSeamSamples());
        var repr = cs.First(c => c.Category == "煤质表征").Text;
        Assert.Contains("中灰", repr);           // 均值 Ad=21 → 中灰(16<21≤29)
        var cmp = cs.First(c => c.Category == "煤层对比").Text;
        Assert.Contains("3煤灰分最低", cmp);      // 3 煤(16) < 5 煤(26)
        Assert.Contains("5煤最高", cmp);
    }

    [Fact]
    public void Conclusions_detect_negative_ash_calorific_correlation()
    {
        var cs = CoalAnalytics.OverallConclusions(TwoSeamSamples());
        var corr = cs.FirstOrDefault(c => c.Category == "相关性");
        Assert.NotNull(corr);
        Assert.Contains("负相关", corr!.Text);   // 灰分↑ 发热量↓
    }

    [Fact]
    public void Conclusions_empty_input_is_safe()
    {
        var cs = CoalAnalytics.OverallConclusions(new List<CoalSample>());
        Assert.Single(cs);
        Assert.Equal(CoalAnalytics.Verdict.Warn, cs[0].Level);
    }

    [Fact]
    public void ConclusionsToCsv_has_header()
    {
        var csv = CoalAnalytics.ConclusionsToCsv(CoalAnalytics.OverallConclusions(TwoSeamSamples()));
        Assert.Contains("类别,结论,评级", csv);
        Assert.Contains("煤质表征", csv);
    }
}
