using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;
using CR = PitMine3D.Kylin.Data.CoalTypeInference.ClassRange;

namespace PitMine3D.Kylin.Tests;

/// <summary>煤质数据审核规则回归（忠实原 CoalQualityService.RunAudit 可支撑的 5 类规则）。</summary>
public class CoalAuditTests
{
    // 位序: Id,Hole,Seam,X,Y,Z, AdRaw,AdClean,StdRaw,StdClean, QgrD,QnetAd,VdafRaw,VdafClean,
    //       SampleThickness,ApparentDensity,CleanCoalYield,CakingG,PlasticYMm,CoalType
    private static CoalSample Mk(long id, string seam = "4-1", double? adRaw = 20, double? adClean = 10,
        double? std = 0.5, double? qnet = 25, double? vdaf = 32, double? yield = 70, double? g = null, double? y = null, string? type = null)
        => new(id, "H" + id, seam, id, id, 100, adRaw, adClean, std, null, null, qnet, vdaf, null,
            null, null, yield, g, y, type);

    [Fact]
    public void Rule2_physical_ranges_flag_out_of_bounds()
    {
        var s = new List<CoalSample>
        {
            Mk(1, std: 15),           // St 15 > 10 → 越界
            Mk(2, adRaw: 70),         // Ad 70 > 60 → 越界(注: adClean 默认10 < 70, 不触发规则③)
            Mk(3, qnet: 60),          // Qnet 60 > 50 → 越界
            Mk(4),                    // 全合规
        };
        var a = CoalAudit.Run(s);
        Assert.Equal(3, a.Items.Count(x => x.Category == "物理范围"));
        Assert.All(a.Items.Where(x => x.Category == "物理范围"), x => Assert.Equal(CoalAudit.Severity.Error, x.Severity));
        Assert.Empty(a.Items.Where(x => x.SampleId == 4));   // 合规样无 finding
    }

    [Fact]
    public void Rule3_clean_ash_above_raw_is_impossible()
    {
        var a = CoalAudit.Run(new List<CoalSample> { Mk(1, adRaw: 15, adClean: 22) });   // 浮煤灰 22 > 原煤灰 15
        var hit = Assert.Single(a.Items.Where(x => x.Category == "原煤vs浮煤"));
        Assert.Equal(CoalAudit.Severity.Error, hit.Severity);
    }

    [Fact]
    public void Rule4_coal_type_mismatch_warns()
    {
        var ranges = new List<CR> { new("QM", 28, 37, null, null, null, null), new("CY", 37, null, null, null, null, null) };
        var s = new List<CoalSample>
        {
            Mk(1, vdaf: 32, type: "QM"),   // 反推 QM == 标注 → 无 finding
            Mk(2, vdaf: 40, type: "QM"),   // 反推 CY ≠ 标注 QM → 警告
        };
        var a = CoalAudit.Run(s, ranges);
        var mism = Assert.Single(a.Items.Where(x => x.Category == "煤类反推"));
        Assert.Equal(2, mism.SampleId);
        Assert.Equal(CoalAudit.Severity.Warning, mism.Severity);
    }

    [Fact]
    public void Rule4_skipped_when_no_ranges()
    {
        var a = CoalAudit.Run(new List<CoalSample> { Mk(1, vdaf: 40, type: "QM") }, ranges: null);
        Assert.Empty(a.Items.Where(x => x.Category == "煤类反推"));
    }

    [Fact]
    public void Rule7_clean_coal_yield_range()
    {
        var a = CoalAudit.Run(new List<CoalSample> { Mk(1, yield: 120), Mk(2, yield: -5), Mk(3, yield: 70) });
        Assert.Equal(2, a.Items.Count(x => x.Category == "浮煤回收率"));
        Assert.All(a.Items.Where(x => x.Category == "浮煤回收率"), x => Assert.Equal(CoalAudit.Severity.Error, x.Severity));
    }

    [Fact]
    public void Rule5_same_seam_3sigma_outlier_needs_min_count()
    {
        // 34 个正常 Ad≈20 + 1 个离群 Ad=200 于同层; 需层样本≥30 才统计, 3σ 命中离群。
        var s = new List<CoalSample>();
        for (int i = 0; i < 34; i++) s.Add(Mk(i + 1, adRaw: 20 + (i % 2 == 0 ? 0.5 : -0.5)));   // 紧簇 ~20
        s.Add(Mk(999, adRaw: 200));                                                              // 极端离群
        var a = CoalAudit.Run(s);
        var outliers = a.Items.Where(x => x.Category == "同层离群").ToList();
        Assert.Contains(outliers, x => x.SampleId == 999);
        Assert.All(outliers, x => Assert.Equal(CoalAudit.Severity.Warning, x.Severity));

        // 同数据但阈值提到 40 → 层样本 35 < 40 不统计, 无离群 finding。
        var a2 = CoalAudit.Run(s, minSeamForOutlier: 40);
        Assert.Empty(a2.Items.Where(x => x.Category == "同层离群"));
    }

    [Fact]
    public void Summary_counts_by_category_and_severity()
    {
        var s = new List<CoalSample>
        {
            Mk(1, std: 15),                 // 物理范围 Error
            Mk(2, adRaw: 15, adClean: 22),  // 原煤vs浮煤 Error
            Mk(3, yield: 120),              // 浮煤回收率 Error
            Mk(4),                          // 干净
        };
        var a = CoalAudit.Run(s);
        Assert.Equal(4, a.Samples);
        Assert.Equal(3, a.Findings);
        Assert.Equal(3, a.Errors);
        Assert.Equal(0, a.Warnings);
        Assert.Equal(3, a.ByCategory.Sum(c => c.Count));
        // CSV 头 + 每条一行。
        var csv = CoalAudit.ToCsv(a);
        Assert.Contains("sample_id,hole_id,seam,category,severity,message", csv);
        Assert.Equal(1 + 3, csv.Trim().Split('\n').Length);
    }
}
