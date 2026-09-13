using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Data.Entities;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 【排土场按量推进】的判据（DA1–DA8）。纯算，不碰库、不碰图 —— 合成条带直接喂进去。
///
/// 【为什么合成而不是接真库】这里要验的是"量→带"这条一维账：部分填、推进距离、排满、
/// 多期滚账。真库的带宽/走向长参差不齐，反而看不出这些账对不对。真库那侧由界面的
/// 「排土场按量推进」窗口对着形态看（见 [[dump-strip-offline-harness]] 的教训：
/// 数值全绿也可能是盲区，形态还是得出图看）。
/// </summary>
public class DumpAdvanceByVolumeTests
{
    /// <summary>造一批规整条带：levels 级 × panels 幅 × steps 带，每带库容 = strike×W×H。</summary>
    private static List<DumpStrip> Grid(int levels = 2, int panels = 2, int steps = 3,
        double strike = 100, double w = 20, double h = 10, string category = "external_dump")
    {
        var list = new List<DumpStrip>();
        for (int lv = 1; lv <= levels; lv++)
            for (int pn = 1; pn <= panels; pn++)
                for (int st = 1; st <= steps; st++)
                    list.Add(new DumpStrip
                    {
                        Id = list.Count + 1,
                        RegionId = 1,
                        RegionName = "外排1",
                        Category = category,
                        Code = $"外排1-L{lv}-P{pn:00}-S{st:00}",
                        LevelIndex = lv, PanelIndex = pn, PanelCount = panels,
                        StepIndex = st, SubIndex = 1, SubCount = 1,
                        BenchHeightM = h, StrikeLenM = strike, StripWidthM = w,
                        CapacityM3 = strike * w * h,
                    });
        return list;
    }

    // ── DA1 量→带：最后一带按比例部分填，不整带进位 ──

    [Fact]
    public void DA1_LastCell_IsPartiallyFilled_NotRoundedUpToWholeCell()
    {
        var strips = Grid(levels: 1, panels: 1, steps: 5);   // 每带 100×20×10 = 20000 m³
        var r = DumpAdvanceByVolume.Advance(strips, volumeM3: 50000, swellKr: 1.0);
        var s = r.Slices.Single();

        Assert.True(r.Ok);
        Assert.Equal(50000, s.PlacedM3, 3);                  // 一方不多一方不少
        Assert.Equal(3, s.Fills.Count);                       // 2 整带 + 半带
        Assert.False(s.Fills[0].IsPartial);
        Assert.False(s.Fills[1].IsPartial);
        Assert.True(s.Fills[2].IsPartial);
        Assert.Equal(0.5, s.Fills[2].Fraction, 3);
    }

    // ── DA2 口径：实方 ×Kr → 占容方，只换这一次 ──

    [Fact]
    public void DA2_SolidVolume_IsConvertedByKr_Once()
    {
        var strips = Grid(levels: 1, panels: 1, steps: 10);
        var r = DumpAdvanceByVolume.Advance(strips, volumeM3: 20000, swellKr: 1.25);
        var s = r.Slices.Single();

        Assert.Equal(25000, s.RequestedM3, 3);               // 20000 实方 → 25000 占容方
        Assert.Equal(25000, s.PlacedM3, 3);
    }

    // ── DA3 推进距离 = Σ已填带宽（部分填按比例），不是 V/(L×h) 那个平均数 ──

    [Fact]
    public void DA3_AdvanceDistance_SumsStripWidths_PartialProrated()
    {
        var strips = Grid(levels: 1, panels: 1, steps: 5, strike: 100, w: 20, h: 10);
        var r = DumpAdvanceByVolume.Advance(strips, volumeM3: 50000, swellKr: 1.0);
        var s = r.Slices.Single();

        // 2 整带 + 半带 = 20 + 20 + 10 = 50 m
        Assert.Equal(50, s.AdvanceMByLevel[1], 3);
        Assert.Equal(50, s.MaxAdvanceM, 3);
        // 一级一幅等宽这种最规整的情形下，对照值才该与逐级值相符
        Assert.Equal(50, s.EquivalentAdvanceM, 3);
    }

    [Fact]
    public void DA3_MultiPanel_EquivalentAdvance_DivergesFromRealAdvance()
    {
        // 两幅并肩推进：真推进只有 1 带宽 = 20 m，而 V/(L中位×h) 会按单幅长度算成 40 m。
        // 这正是"平均数只在一级一幅时才等价"的那条 —— 判据要能把它们分开。
        var strips = Grid(levels: 1, panels: 2, steps: 3, strike: 100, w: 20, h: 10);
        var r = DumpAdvanceByVolume.Advance(strips, volumeM3: 40000, swellKr: 1.0);
        var s = r.Slices.Single();

        Assert.Equal(20, s.AdvanceMByLevel[1], 3);           // 两幅各推 1 带 → 推进 1 带宽
        Assert.Equal(40, s.EquivalentAdvanceM, 3);           // 对照值翻了一倍
    }

    // ── DA4 推进次序 ──

    [Fact]
    public void DA4_StepAbreast_FillsSameStepAcrossPanelsAndLevels_First()
    {
        var strips = Grid(levels: 2, panels: 2, steps: 3);
        var seq = DumpAdvanceByVolume.Sort(strips, DumpAdvanceByVolume.Order.StepAbreast);

        // 前 4 个位置必须都是第 1 带（2 级 × 2 幅），而不是把一幅推到底
        Assert.All(seq.Take(4), c => Assert.Equal(1, c.StepIndex));
        Assert.Equal(4, seq.Take(4).Select(c => (c.LevelIndex, c.PanelIndex)).Distinct().Count());
    }

    [Fact]
    public void DA4_LevelByLevel_FinishesOneLevelBeforeNext_BottomUp()
    {
        var strips = Grid(levels: 2, panels: 2, steps: 3);
        var seq = DumpAdvanceByVolume.Sort(strips, DumpAdvanceByVolume.Order.LevelByLevel);

        // 级序 1 = 最上一级 → 自下而上是先排 L2
        Assert.All(seq.Take(6), c => Assert.Equal(2, c.LevelIndex));
        Assert.All(seq.Skip(6), c => Assert.Equal(1, c.LevelIndex));
    }

    // ── DA5 排满：余量必须报出来，不静默截断 ──

    [Fact]
    public void DA5_Overflow_IsReported_NotSilentlyTruncated()
    {
        var strips = Grid(levels: 1, panels: 1, steps: 2);   // 总库容 40000
        var r = DumpAdvanceByVolume.Advance(strips, volumeM3: 60000, swellKr: 1.0);
        var s = r.Slices.Single();

        Assert.Equal(40000, s.PlacedM3, 3);
        Assert.Equal(20000, s.OverflowM3, 3);
        Assert.True(s.SiteFull);
        Assert.Contains("排不下", r.Message);
        Assert.Equal(0, r.RemainingM3, 3);
    }

    // ── DA8 多期：逐期接着上一期的水位往下吃 ──

    [Fact]
    public void DA8_Series_ContinuesFromPreviousWaterline()
    {
        var strips = Grid(levels: 1, panels: 1, steps: 5);   // 每带 20000
        var r = DumpAdvanceByVolume.AdvanceSeries(strips,
            new[] { ("2027", 30000.0), ("2028", 30000.0) }, swellKr: 1.0);

        Assert.True(r.Ok);
        var y1 = r.Slices[0];
        var y2 = r.Slices[1];

        Assert.Equal(30000, y1.PlacedM3, 3);
        Assert.Equal(30000, y2.PlacedM3, 3);
        Assert.Equal(60000, r.PlacedM3, 3);

        // 第 2 年必须从第 2 带的一半接着填 —— 从头再来的话它会重填第 1 带
        Assert.Equal(2, y2.Fills[0].Strip.StepIndex);
        Assert.Equal(0.5, y2.Fills[0].Fraction, 3);
        Assert.Equal(3, y2.TopStep);
    }

    [Fact]
    public void DA8_AlreadyFilled_SkipsConsumedCells()
    {
        var strips = Grid(levels: 1, panels: 1, steps: 5);
        var r = DumpAdvanceByVolume.Advance(strips, volumeM3: 10000, swellKr: 1.0, alreadyFilledM3: 40000);
        var s = r.Slices.Single();

        Assert.Equal(3, s.Fills.Single().Strip.StepIndex);   // 前两带已填满，从第 3 带起
        Assert.Equal(50000, r.PlacedM3, 3);                  // 起始 40000 + 本期 10000
    }

    // ── 空输入：不许"看上去正常地"返回 0 ──

    [Fact]
    public void NoStrips_FailsLoudly_WithActionableMessage()
    {
        var r = DumpAdvanceByVolume.Advance(new List<DumpStrip>(), volumeM3: 10000);
        Assert.False(r.Ok);
        Assert.Contains("排土条带", r.Message);
    }
}
