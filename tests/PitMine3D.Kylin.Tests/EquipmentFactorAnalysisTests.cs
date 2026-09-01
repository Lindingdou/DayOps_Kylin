using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;
using FR = PitMine3D.Kylin.Data.EquipmentFactorAnalysis.FactorRow;

namespace PitMine3D.Kylin.Tests;

/// <summary>设备主控因素分析（因素-产能 Pearson 相关排名）回归。</summary>
public class EquipmentFactorAnalysisTests
{
    [Fact]
    public void Pearson_perfect_positive_and_negative()
    {
        Assert.Equal(1.0, EquipmentFactorAnalysis.Pearson(new[] { 1.0, 2, 3 }, new[] { 2.0, 4, 6 })!.Value, 9);
        Assert.Equal(-1.0, EquipmentFactorAnalysis.Pearson(new[] { 1.0, 2, 3 }, new[] { 6.0, 4, 2 })!.Value, 9);
        Assert.Null(EquipmentFactorAnalysis.Pearson(new[] { 5.0, 5, 5 }, new[] { 1.0, 2, 3 }));   // x 无方差
        Assert.Null(EquipmentFactorAnalysis.Pearson(new[] { 1.0 }, new[] { 2.0 }));               // 样本 < 2
    }

    [Fact]
    public void Correlate_ranks_main_driver_with_direction_and_strength()
    {
        // 可用率与产能完全正相关(强正); 内部故障率与产能完全负相关(强负); 利用率恒定(略去)
        var rows = new List<FR>
        {
            //          avail runRate util  inFault exFault  output
            new FR(0.5,  0.6,  0.7, 0.10, 0.02, 100),
            new FR(0.6,  0.5,  0.7, 0.08, 0.03, 120),
            new FR(0.7,  0.4,  0.7, 0.06, 0.02, 140),
            new FR(0.8,  0.7,  0.7, 0.04, 0.03, 160),
        };
        var cc = EquipmentFactorAnalysis.Correlate(rows);
        // 可用率(0.5..0.8 单调↑ 同 output↑) → 强正 r=1
        var avail = cc.Single(c => c.Factor == "可用率");
        Assert.Equal(1.0, avail.R, 6);
        Assert.Equal("正", avail.Direction); Assert.Equal("强", avail.Strength);
        // 内部故障率(0.10..0.04 单调↓ 同 output↑) → 强负 r=-1
        var inf = cc.Single(c => c.Factor == "内部故障率");
        Assert.Equal(-1.0, inf.R, 6);
        Assert.Equal("负", inf.Direction);
        // 利用率恒 0.7 无方差 → 略去
        Assert.DoesNotContain(cc, c => c.Factor == "利用率");
        // 按 |r| 降序: 首位 |r|=1
        Assert.Equal(1.0, System.Math.Abs(cc[0].R), 6);
    }

    [Fact]
    public void Correlate_too_few_rows_returns_empty()
    {
        Assert.Empty(EquipmentFactorAnalysis.Correlate(new List<FR> { new FR(0.5, 0.6, 0.7, 0.1, 0.02, 100) }));
    }
}
