using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>产量达成分析回归（移植自 TaskLib.AttainmentAnalyzer 的 gap 分解核）。</summary>
public class AttainmentAnalyzerTests
{
    [Fact]
    public void Hour_gap_attributed_to_fault()
    {
        // 计划1000/10h(班产100), 实际800/8h(班产100) → 全是少干2h的工时缺口, 归因故障2h
        var b = AttainmentAnalyzer.Of(1000, 800, 10, 8, faultHours: 2);
        Assert.Equal(200, b.HourGapM3, 4);
        Assert.Equal(0, b.RateGapM3, 4);
        Assert.Equal(80, b.AttainPct, 4);
        var it = Assert.Single(b.Items);
        Assert.Equal("突发停机（非计划故障）", it.Cause);
        Assert.Equal(200, it.VolumeM3, 4);
        Assert.Equal(100, b.ExplainedPct, 4);
    }

    [Fact]
    public void Rate_gap_attributed_to_efficiency()
    {
        // 工时足额(10h) 但实际班产 70<100 → 效率缺口 300
        var b = AttainmentAnalyzer.Of(1000, 700, 10, 10);
        Assert.Equal(0, b.HourGapM3, 4);
        Assert.Equal(300, b.RateGapM3, 4);
        var it = Assert.Single(b.Items);
        Assert.Equal("执行效率", it.Cause);
        Assert.Equal(300, it.VolumeM3, 4);
    }

    [Fact]
    public void Overachieve_no_items()
    {
        var b = AttainmentAnalyzer.Of(1000, 1100, 10, 10);
        Assert.Empty(b.Items);
        Assert.Equal(110, b.AttainPct, 4);
    }

    [Fact]
    public void Combine_sums_and_merges_causes()
    {
        var a = AttainmentAnalyzer.Of(1000, 800, 10, 8, faultHours: 2);   // 故障 200
        var c = AttainmentAnalyzer.Of(1000, 800, 10, 8, faultHours: 2);   // 故障 200
        var m = AttainmentAnalyzer.Combine(new[] { a, c });
        Assert.Equal(2000, m.PlanM3, 4);
        Assert.Equal(1600, m.ActualM3, 4);
        var it = Assert.Single(m.Items);
        Assert.Equal("突发停机（非计划故障）", it.Cause);
        Assert.Equal(400, it.VolumeM3, 4);   // 合并
    }
}
