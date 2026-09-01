using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>分煤层煤质五数概括（忠实原 CoalQualityStatsWindow 每煤层箱线）回归。</summary>
public class CoalStatsBySeamTests
{
    // 位序同 CoalSample: Id,Hole,Seam,X,Y,Z, AdRaw,...
    private static CoalSample Mk(long id, string seam, double ad)
        => new(id, "H" + id, seam, 0, 0, 0, ad, null, null, null, null, null, null, null);

    [Fact]
    public void StatsBySeam_groups_and_five_number_summary()
    {
        // 煤层 A: Ad {10,20,30,40,50} → min10 max50 中位30 均值30; 煤层 B: {5,15}
        var s = new List<CoalSample>
        {
            Mk(1, "A", 10), Mk(2, "A", 20), Mk(3, "A", 30), Mk(4, "A", 40), Mk(5, "A", 50),
            Mk(6, "B", 5), Mk(7, "B", 15),
        };
        var rows = CoalAnalytics.StatsBySeam(s, "ad");
        Assert.Equal(2, rows.Count);
        var a = rows.Single(r => r.SeamCode == "A");
        Assert.Equal(5, a.N);
        Assert.Equal(10, a.Min, 6); Assert.Equal(50, a.Max, 6);
        Assert.Equal(30, a.Median, 6); Assert.Equal(30, a.Mean, 6);
        var b = rows.Single(r => r.SeamCode == "B");
        Assert.Equal(2, b.N);
        Assert.Equal(5, b.Min, 6); Assert.Equal(15, b.Max, 6); Assert.Equal(10, b.Mean, 6);
        Assert.Equal("不足", a.SampleLevel);   // 5 样 < 20 → 不足
        // CSV 头(含评级) + 每层一行
        var csv = CoalAnalytics.StatsBySeamToCsv(rows);
        Assert.Contains("煤层,样本,均值,标准差,Min,P25,P50,P75,Max,评级", csv);
        Assert.Equal(1 + 2, csv.Trim().Split('\n').Length);
    }

    [Fact]
    public void SampleAdequacy_thresholds_50_20()
    {
        Assert.Equal("充分", CoalAnalytics.SampleAdequacy(50));
        Assert.Equal("充分", CoalAnalytics.SampleAdequacy(80));
        Assert.Equal("紧张", CoalAnalytics.SampleAdequacy(20));
        Assert.Equal("紧张", CoalAnalytics.SampleAdequacy(49));
        Assert.Equal("不足", CoalAnalytics.SampleAdequacy(19));
        Assert.Equal("不足", CoalAnalytics.SampleAdequacy(0));
    }

    [Fact]
    public void StatsBySeam_skips_samples_missing_indicator()
    {
        var s = new List<CoalSample>
        {
            Mk(1, "A", 10), Mk(2, "A", 20),
            new(3, "H3", "A", 0, 0, 0, null, null, null, null, null, null, null, null),   // 缺 Ad → 跳
        };
        var rows = CoalAnalytics.StatsBySeam(s, "ad");
        Assert.Single(rows);
        Assert.Equal(2, rows[0].N);   // 只 2 个有效样
    }
}
