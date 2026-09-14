using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 进度计划出图的取数与标度（§三四六）。
///
/// 盯三处：**达产线/n经线必须落在轴内**（不然"差多少"看不出来）、
/// **累计 NPV 下限要含 0**（不然"哪年转正"看不见）、
/// **削峰与达产是越小越好**（雷达上归一方向反了，结论就整个反过来）。
/// </summary>
public class LongTermChartMathTests
{
    private static LongTermPlan Plan(string name, double cap, double nEco, params (double coal, double ratio, double npv)[] rows)
    {
        var p = new LongTermPlan { Name = name, DesignCapacityWanTa = cap, EconomicStripRatioMax = nEco };
        int y = 2027;
        foreach (var (coal, ratio, npv) in rows)
            p.Periods.Add(new PlanPeriod
            { Label = (y++).ToString(), CoalWanT = coal, Ratio = ratio, NpvWan = npv });
        return p;
    }

    // ── 累计 NPV ────────────────────────────────────────────
    [Fact]
    public void 累计_逐期把现金流累加()
    {
        var p = Plan("甲", 1000, 12, (0, 0, -500), (300, 8, -200), (900, 6, 400), (1000, 5, 600));
        Assert.Equal(new[] { -500.0, -700.0, -300.0, 300.0 }, LongTermChartMath.Accumulate(p));
    }

    [Fact]
    public void 累计_空方案给空表不抛()
    {
        Assert.Empty(LongTermChartMath.Accumulate(null));
        Assert.Empty(LongTermChartMath.Accumulate(new LongTermPlan()));
    }

    [Fact]
    public void 范围_累计NPV下限一定含零()
    {
        // ★ 全程为正的方案，下限也要按 0 起 —— 否则零线画不出来，"哪年转正"就看不见了
        var p = Plan("全正", 1000, 12, (900, 6, 100), (1000, 5, 200));
        var (min, max) = LongTermChartMath.NpvRange(new[] { p });
        Assert.Equal(0, min, 9);
        Assert.Equal(300, max, 9);
    }

    [Fact]
    public void 范围_基建期为负时下限取到最负那一点()
    {
        var p = Plan("甲", 1000, 12, (0, 0, -800), (0, 0, -400), (900, 6, 500));
        var (min, max) = LongTermChartMath.NpvRange(new[] { p });
        Assert.Equal(-1200, min, 9);
        Assert.True(max >= 1);
    }

    [Fact]
    public void 范围_没有方案时不炸且区间可用()
    {
        var (min, max) = LongTermChartMath.NpvRange(Array.Empty<LongTermPlan>());
        Assert.True(max > min);
    }

    // ── 纵轴上限 ────────────────────────────────────────────
    [Fact]
    public void 轴_产量上限要把达产线包进去()
    {
        // ★ 方案压根没达产：只按实际产量定上限的话，达产线会跑到画布外
        var p = Plan("欠产", cap: 1000, nEco: 12, (200, 8, 0), (300, 7, 0));
        double max = LongTermChartMath.OutputAxisMax(new[] { p });
        Assert.True(max > 1000, $"上限 {max:0} 应当高于达产线 1000");
        Assert.Equal(1000 * LongTermChartMath.HeadroomCurve, max, 6);
    }

    [Fact]
    public void 轴_产量超达产时按产量定上限()
    {
        var p = Plan("超产", cap: 1000, nEco: 12, (1400, 8, 0));
        Assert.Equal(1400 * LongTermChartMath.HeadroomCurve, LongTermChartMath.OutputAxisMax(new[] { p }), 6);
    }

    [Fact]
    public void 轴_剥采比上限要把n经线包进去()
    {
        var p = Plan("低剥", cap: 1000, nEco: 12, (900, 3, 0), (1000, 4, 0));
        double max = LongTermChartMath.RatioAxisMax(new[] { p });
        Assert.True(max > 12);
        Assert.Equal(12 * LongTermChartMath.HeadroomCurve, max, 6);
    }

    [Fact]
    public void 轴_多方案取各自最大里的最大()
    {
        var a = Plan("甲", 800, 10, (700, 5, 0));
        var b = Plan("乙", 1200, 15, (1100, 9, 0));
        Assert.Equal(1200 * LongTermChartMath.HeadroomCurve, LongTermChartMath.OutputAxisMax(new[] { a, b }), 6);
        Assert.Equal(15 * LongTermChartMath.HeadroomCurve, LongTermChartMath.RatioAxisMax(new[] { a, b }), 6);
    }

    [Fact]
    public void 轴_空方案表不返回零上限()
    {
        // 上限为 0 会让后面的除法炸成 NaN/∞
        Assert.True(LongTermChartMath.OutputAxisMax(Array.Empty<LongTermPlan>()) > 0);
        Assert.True(LongTermChartMath.RatioAxisMax(Array.Empty<LongTermPlan>()) > 0);
    }

    [Fact]
    public void 轴_期数取各方案最长且至少为一()
    {
        var a = Plan("甲", 1000, 12, (1, 1, 0), (2, 1, 0));
        var b = Plan("乙", 1000, 12, (1, 1, 0), (2, 1, 0), (3, 1, 0), (4, 1, 0));
        Assert.Equal(4, LongTermChartMath.MaxPeriodCount(new[] { a, b }));
        Assert.Equal(1, LongTermChartMath.MaxPeriodCount(Array.Empty<LongTermPlan>()));
        Assert.Equal(1, LongTermChartMath.MaxPeriodCount(null));
        Assert.Equal(1, LongTermChartMath.MaxPeriodCount(new[] { new LongTermPlan() }));
    }

    // ── 雷达归一 ────────────────────────────────────────────
    private static LongTermPlan WithResult(string name, double peak, double ttc, double inner,
                                           double bal, double npv, double plateau)
    {
        var p = new LongTermPlan { Name = name };
        p.Result = new LongTermResult(0, ttc, "", plateau, peak, 0, inner, 0, npv, 0, 0, 0, bal, true);
        return p;
    }

    [Fact]
    public void 雷达_削峰与达产是越小越好()
    {
        // ★ 归一方向反了，结论就整个反过来：峰值剥采比低的方案该得高分
        var lowPeak = WithResult("低峰", peak: 8, ttc: 3, inner: 50, bal: 1, npv: 100, plateau: 10);
        var highPeak = WithResult("高峰", peak: 20, ttc: 9, inner: 50, bal: 1, npv: 100, plateau: 10);
        var list = new[] { lowPeak, highPeak };

        var vLow = LongTermChartMath.RadarValues(list, 0);
        var vHigh = LongTermChartMath.RadarValues(list, 1);
        Assert.Equal(1.0, vLow[0], 9);    // 削峰：峰值最低 → 满分
        Assert.Equal(0.0, vHigh[0], 9);
        Assert.Equal(1.0, vLow[1], 9);    // 达产：用时最短 → 满分
        Assert.Equal(0.0, vHigh[1], 9);
    }

    [Fact]
    public void 雷达_内排NPV稳产均衡是越大越好()
    {
        var weak = WithResult("弱", 10, 5, inner: 10, bal: 0.5, npv: 100, plateau: 3);
        var strong = WithResult("强", 10, 5, inner: 90, bal: 0.9, npv: 900, plateau: 15);
        var list = new[] { weak, strong };
        var vStrong = LongTermChartMath.RadarValues(list, 1);
        Assert.Equal(1.0, vStrong[2], 9);   // 内排
        Assert.Equal(1.0, vStrong[3], 9);   // 均衡
        Assert.Equal(1.0, vStrong[4], 9);   // NPV
        Assert.Equal(1.0, vStrong[5], 9);   // 稳产
    }

    [Fact]
    public void 雷达_只有一个方案时六项都给中值()
    {
        // 只有一个方案时归一化没有意义；给 0 或 1 都会让人误以为"特别差/特别好"
        var only = WithResult("独苗", 10, 5, 50, 1, 100, 8);
        var v = LongTermChartMath.RadarValues(new[] { only }, 0);
        Assert.Equal(6, v.Length);
        Assert.All(v, x => Assert.Equal(0.5, x, 9));
    }

    [Fact]
    public void 雷达_全相等时也给中值()
    {
        var a = WithResult("甲", 10, 5, 50, 1, 100, 8);
        var b = WithResult("乙", 10, 5, 50, 1, 100, 8);
        Assert.All(LongTermChartMath.RadarValues(new[] { a, b }, 0), x => Assert.Equal(0.5, x, 9));
    }

    [Fact]
    public void 雷达_六个轴名与顺序固定()
    {
        Assert.Equal(new[] { "削峰", "达产", "内排", "均衡", "NPV", "稳产" }, LongTermChartMath.RadarAxes);
        Assert.Equal(LongTermChartMath.RadarAxes.Length,
                     LongTermChartMath.RadarValues(new[] { WithResult("甲", 1, 1, 1, 1, 1, 1) }, 0).Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void 归一_下标越界给中值而不是炸(int i)
    {
        Assert.Equal(0.5, LongTermChartMath.NormHigh(new[] { 1.0, 2.0 }, i), 9);
        Assert.Equal(0.5, LongTermChartMath.NormLow(new[] { 1.0, 2.0 }, i), 9);
    }

    [Fact]
    public void 归一_空数组给中值()
    {
        Assert.Equal(0.5, LongTermChartMath.NormHigh(Array.Empty<double>(), 0), 9);
        Assert.Equal(0.5, LongTermChartMath.NormLow(Array.Empty<double>(), 0), 9);
    }

    // ── 标签与配色 ──────────────────────────────────────────
    [Theory]
    [InlineData(1, 1)]
    [InlineData(12, 1)]
    [InlineData(24, 2)]
    [InlineData(60, 5)]
    [InlineData(0, 1)]
    public void 标签_年标稀疏步长(int n, int step)
        => Assert.Equal(step, LongTermChartMath.LabelStep(n));

    [Theory]
    [InlineData("基准", "基准")]
    [InlineData("一个特别长的方案名字", "一个特别长的方案…")]
    [InlineData(null, "")]
    public void 标签_方案名截短(string? name, string expect)
        => Assert.Equal(expect, LongTermChartMath.ShortName(name));

    [Fact]
    public void 配色_四个时相各不相同()
    {
        var all = Enum.GetValues<PlanPhase>().Select(LongTermChartMath.PhaseRgb).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void 配色_方案色按六色循环且下标为负也不炸()
    {
        Assert.Equal(LongTermChartMath.SchemeRgb(0), LongTermChartMath.SchemeRgb(6));
        Assert.Equal(LongTermChartMath.Palette[1], LongTermChartMath.SchemeRgb(7));
        Assert.Equal(LongTermChartMath.SchemeRgb(5), LongTermChartMath.SchemeRgb(-1));
    }

    [Fact]
    public void 配色_时相中文名齐全()
    {
        foreach (PlanPhase ph in Enum.GetValues<PlanPhase>())
            Assert.False(string.IsNullOrWhiteSpace(LongTermChartMath.PhaseLabel(ph)));
    }

    // ── 与真排产联动 ────────────────────────────────────────
    [Fact]
    public void 联动_真排出来的方案六张图的取数都算得出()
    {
        var p = new LongTermPlan();
        LongTermScheduler.Schedule(p);
        Assert.NotNull(p.Result);
        Assert.NotEmpty(p.Periods);

        var list = new[] { p };
        Assert.True(LongTermChartMath.OutputAxisMax(list) > 0);
        Assert.True(LongTermChartMath.RatioAxisMax(list) > 0);
        Assert.Equal(p.Periods.Count, LongTermChartMath.Accumulate(p).Count);
        var (min, max) = LongTermChartMath.NpvRange(list);
        Assert.True(max > min);
        Assert.Equal(6, LongTermChartMath.RadarValues(list, 0).Length);
    }

    [Fact]
    public void 联动_基建期不参与剥采比曲线()
    {
        // 基建期没有采出, 剥采比无定义 —— 连上去会拉出一条假尖峰
        var p = new LongTermPlan();
        LongTermScheduler.Schedule(p);
        var basic = p.Periods.Where(z => z.CoalWanT <= 0).ToList();
        if (basic.Count > 0)
            Assert.All(basic, z => Assert.Equal(PlanPhase.Basic, z.Phase));
    }
}
