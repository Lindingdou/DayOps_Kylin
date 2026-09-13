using System;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>剥采比均衡 VP 窗口的取数/计算层（原 VpCurveWindow.Recompute 抽成 <see cref="VpBalanceSession"/>）：投产/达产/阶段标记/分段/偏离/指标/校核。</summary>
public class VpBalanceSessionTests
{
    [Fact]
    public void 模拟数据_投产达产与阶段标记与基建剥离()
    {
        var s = new VpBalanceSession();
        s.LoadSample();
        Assert.Equal(11, s.Periods.Count);
        Assert.Equal(2, s.CommIdx);                       // 2025 首个 P≥⅓·1000
        Assert.Equal(4800, s.BaseStrip, 6);               // 2023+2024 基建剥离
        Assert.Equal(4, s.CapIdx);                        // 2027 首达 1000
        Assert.Equal(new[] { "基建", "基建", "过渡", "过渡", "稳产", "稳产", "稳产", "稳产", "稳产", "稳产", "减产" }, s.Periods.Select(p => p.Phase).ToArray());
        Assert.Equal(3.6, s.Periods[2].Ratio, 9);
        Assert.Equal(8050, s.TotalCoal, 6);
        Assert.Equal(49700, s.TotalStrip, 6);
        Assert.Equal(49700.0 / 8050, s.AvgRatio, 9);
        Assert.Equal((49700.0 - 4800) / 8050, s.ProdRatio, 9);
        // 产出曲线顶点：锚点 + 投产起 9 期
        Assert.Equal(10, s.Xs.Count);
        Assert.Equal(0, s.Xs[0], 9); Assert.Equal(4800, s.Ys[0], 9);
        Assert.Equal(8050, s.Xs[^1], 9); Assert.Equal(49700, s.Ys[^1], 9);
    }

    [Fact]
    public void 分段_落在曲线上方_偏离非负_阶段剥采比递增_校核通过()
    {
        var s = new VpBalanceSession();
        s.LoadSample();
        Assert.True(s.UsedK >= 1 && s.UsedK <= VpBalanceSolver.MaxStages);
        Assert.Equal(s.UsedK, s.Stages.Count);
        Assert.Equal(0, s.Breakpoints[0]); Assert.Equal(s.Xs.Count - 1, s.Breakpoints[^1]);
        Assert.All(s.Periods.Skip(s.CommIdx), p => Assert.True(p.Deviation >= -1e-6, $"{p.Label} 欠剥 {p.Deviation}"));
        for (int i = 1; i < s.Stages.Count; i++) Assert.True(s.Stages[i].Ratio + 1e-9 >= s.Stages[i - 1].Ratio);
        Assert.Equal("2025", s.Stages[0].FromLabel);
        Assert.Equal("2033", s.Stages[^1].ToLabel);
        Assert.Equal(s.Xs.Count - 1, s.Stages.Sum(st => st.Years));
        Assert.True(s.CheckOk, string.Join("·", s.Issues));
        Assert.True(s.PeakAdvance >= 0);
        Assert.Equal(s.Periods.Max(p => p.Deviation), s.PeakAdvance, 9);
        // 阶梯图：基建期 null，产出期都有所属阶段均衡比
        var sr = s.StageRatioPerPeriod();
        Assert.Null(sr[0]); Assert.Null(sr[1]);
        Assert.All(sr.Skip(2), v => Assert.NotNull(v));
    }

    [Fact]
    public void 指定期数与经济比上限_超经济比报警_非递增报警()
    {
        var s = new VpBalanceSession { StageCount = 2 };
        s.LoadSample();
        Assert.Equal(2, s.UsedK);
        s.EcoRatio = 5.0; s.Recompute();
        Assert.True(s.Stages.Any(st => st.OverEco));
        Assert.Contains("超经济比", s.Issues);
        s.EcoRatio = 100; s.Recompute();
        Assert.DoesNotContain("超经济比", s.Issues);
        // 期数超上限被钳
        s.StageCount = 99; s.Recompute();
        Assert.True(s.UsedK <= VpBalanceSolver.MaxStages);
    }

    [Fact]
    public void 无设计能力_退化为首个出煤期投产_不判达产()
    {
        var s = new VpBalanceSession { DesignCapacity = null };
        s.Load(new[] { ("1", 0.0, 100.0), ("2", 10.0, 50.0), ("3", 20.0, 80.0) });
        Assert.Equal(1, s.CommIdx);
        Assert.Equal(-1, s.CapIdx);
        Assert.Equal(100, s.BaseStrip, 9);
        Assert.Equal("基建", s.Periods[0].Phase);
        Assert.Equal("过渡", s.Periods[1].Phase);
    }

    [Fact]
    public void 增加期_期号顺延量沿用末期_无采出时全基建()
    {
        var s = new VpBalanceSession();
        s.LoadSample();
        var p = s.AddPeriod();
        Assert.Equal("2034", p.Label); Assert.Equal(800, p.Coal); Assert.Equal(5600, p.Strip);
        Assert.Equal(12, s.Periods.Count);
        var e = new VpBalanceSession();
        e.Load(new[] { ("a", 0.0, 10.0), ("b", 0.0, 20.0) });
        Assert.Equal(-1, e.CommIdx); Assert.Equal(30, e.BaseStrip, 9); Assert.Equal(0, e.UsedK);
        Assert.All(e.Periods, q => Assert.Equal("基建", q.Phase));
    }
}
