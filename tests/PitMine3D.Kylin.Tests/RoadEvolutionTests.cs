using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>路网演化对比回归（构造两期已知关系：保持/移位/延拓/截短/废除/新建）。</summary>
public class RoadEvolutionTests
{
    private static EvoLine Line(string id, params (double x, double y)[] pts)
    {
        var e = new EvoLine { Id = id };
        foreach (var (x, y) in pts) e.Centerline.Add(new Pt3(x, y, 0));
        return e;
    }

    // 一条沿 X 轴、每 2m 打点的直线(足够长, 覆盖判定稳)
    private static EvoLine StraightX(string id, double x0, double x1, double y)
    {
        var e = new EvoLine { Id = id };
        for (double x = x0; x <= x1 + 1e-9; x += 2) e.Centerline.Add(new Pt3(x, y, 0));
        return e;
    }

    [Fact]
    public void Identical_line_is_kept()
    {
        var prev = new List<EvoLine> { StraightX("A", 0, 200, 0) };
        var curr = new List<EvoLine> { StraightX("A", 0, 200, 0) };
        var res = RoadEvolutionAnalyzer.Analyze(prev, curr);
        Assert.True(res.KeepCount >= 1);
        Assert.Equal(0, res.AbolishCount);
        Assert.Equal(0, res.ExtendCount);
        Assert.True(res.Ledger.IsBalanced);
    }

    [Fact]
    public void Laterally_shifted_line_is_shift()
    {
        // 整体横移 6m (> 移位阈值 3, < 匹配容差 8) → 移位
        var prev = new List<EvoLine> { StraightX("A", 0, 200, 0) };
        var curr = new List<EvoLine> { StraightX("A", 0, 200, 6) };
        var res = RoadEvolutionAnalyzer.Analyze(prev, curr);
        Assert.True(res.ShiftCount >= 1);
        Assert.Equal(0, res.AbolishCount);
    }

    [Fact]
    public void Extended_line_reports_extend()
    {
        // 本期比上期长 100m → 延拓段出现
        var prev = new List<EvoLine> { StraightX("A", 0, 200, 0) };
        var curr = new List<EvoLine> { StraightX("A", 0, 300, 0) };
        var res = RoadEvolutionAnalyzer.Analyze(prev, curr);
        Assert.True(res.ExtendCount >= 1);
        Assert.True(res.LenOf(RoadEvolutionClass.Extend) > 50);   // ~100m 延拓
        Assert.True(res.KeepCount >= 1);                           // 前 200m 保持
    }

    [Fact]
    public void Shortened_line_reports_shorten()
    {
        // 本期比上期短 100m → 上期侧截短段
        var prev = new List<EvoLine> { StraightX("A", 0, 300, 0) };
        var curr = new List<EvoLine> { StraightX("A", 0, 200, 0) };
        var res = RoadEvolutionAnalyzer.Analyze(prev, curr);
        Assert.True(res.ShortenCount >= 1);
        Assert.True(res.LenOf(RoadEvolutionClass.Shorten) > 50);
    }

    [Fact]
    public void Removed_line_is_abolished_new_line_is_new()
    {
        // 上期有 A、本期无; 本期有 B(远处新路)、上期无
        var prev = new List<EvoLine> { StraightX("A", 0, 200, 0) };
        var curr = new List<EvoLine> { StraightX("B", 0, 200, 1000) };
        var res = RoadEvolutionAnalyzer.Analyze(prev, curr);
        Assert.True(res.AbolishCount >= 1);     // A 废除
        Assert.True(res.NewRoadCount >= 1);     // B 新建入网
    }

    [Fact]
    public void Ledger_balances()
    {
        var prev = new List<EvoLine> { StraightX("A", 0, 300, 0), StraightX("C", 0, 100, 500) };
        var curr = new List<EvoLine> { StraightX("A", 0, 200, 0), StraightX("D", 0, 100, 900) };
        var res = RoadEvolutionAnalyzer.Analyze(prev, curr);
        Assert.True(res.Ledger.IsBalanced);   // RE8 不重不漏自证
    }

    [Fact]
    public void Empty_inputs_no_throw()
    {
        var res = RoadEvolutionAnalyzer.Analyze(new List<EvoLine>(), new List<EvoLine>());
        Assert.Empty(res.Routes);
        Assert.True(res.Ledger.IsBalanced);
    }
}
