using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>LAS 分类统计(pc_quality 强度分类)回归 —— 逐分类码计数降序 + ASPRS 名 + CSV。</summary>
public class LasQualityReportTests
{
    [Fact]
    public void ClassBreakdown_counts_per_code_descending()
    {
        var cls = new List<byte> { 2, 2, 2, 6, 6, 3 };   // 地面×3, 建筑×2, 低植被×1
        var b = LasQualityReport.ClassBreakdown(cls);
        Assert.Equal(3, b.Count);                         // 3 个类别
        Assert.Equal((byte)2, b[0].Code); Assert.Equal(3, b[0].Count);   // 降序: 地面最多
        Assert.Equal("地面", b[0].Name);
        Assert.Equal((byte)6, b[1].Code); Assert.Equal(2, b[1].Count);
        Assert.Equal((byte)3, b[2].Code); Assert.Equal(1, b[2].Count);
        // 计数和 == 总点数
        Assert.Equal(cls.Count, b.Sum(x => x.Count));
    }

    [Fact]
    public void ClassName_maps_common_asprs_codes()
    {
        Assert.Equal("地面", LasQualityReport.ClassName(2));
        Assert.Equal("高植被", LasQualityReport.ClassName(5));
        Assert.Equal("建筑", LasQualityReport.ClassName(6));
        Assert.Equal("水", LasQualityReport.ClassName(9));
        Assert.Equal("类99", LasQualityReport.ClassName(99));   // 未知码兜底
    }

    [Fact]
    public void Csv_has_header_and_percentages()
    {
        var csv = LasQualityReport.ClassBreakdownCsv(LasQualityReport.ClassBreakdown(new List<byte> { 2, 2, 6, 6 }));
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("code,name,count,pct", lines[0]);
        Assert.Equal(3, lines.Length);                    // 表头 + 2 类
        Assert.Contains("50", lines[1]);                  // 各占 50%
    }

    [Fact]
    public void Empty_safe()
    {
        Assert.Empty(LasQualityReport.ClassBreakdown(new List<byte>()));
        Assert.Equal("code,name,count,pct\n", LasQualityReport.ClassBreakdownCsv(new List<LasQualityReport.ClassCount>()));
    }
}
