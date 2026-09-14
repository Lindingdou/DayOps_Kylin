using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 刀量切割引擎（§三七〇，忠实原 TemplateDrivingEngine）：每隔 Δ 驱动一刀，逐刀分煤/岩累成累积量表；
/// 推进坐标 s = 垂距 + 台阶退距(块高−煤底板)；期按距离 / 按量切；切到最后一刀；量约束双前界；几何预览。
/// </summary>
public class TemplateDrivingEngineTests
{
    /// <summary>基线沿 y（x=0），往 +x 推进。</summary>
    private static WorkLineSamples WorkLine() =>
        WorkLineSamples.FromWorkLine(new[] { (0.0, 0.0), (0.0, 100.0) }, 1000, fan: false, new[] { (50.0, 0.0), (50.0, 100.0) }, null);

    /// <summary>x∈[0,100) 每 10m 一列、y∈[0,100) 10 列、z 三层(1000/1010/1020)：最下层煤，上两层岩。陡帮 α→89° 时退距≈0。</summary>
    private static List<CellBox> Slab(bool topCoalToo = false)
    {
        var cells = new List<CellBox>();
        for (int i = 0; i < 10; i++) for (int j = 0; j < 10; j++) for (int k = 0; k < 3; k++)
            cells.Add(new CellBox { Cx = i * 10 + 5, Cy = j * 10 + 5, Cz = 1000 + k * 10 + 5, Sx = 10, Sy = 10, Sz = 10, IsCoal = k == 0 || (topCoalToo && k == 2), IsRock = !(k == 0 || (topCoalToo && k == 2)), SourceIndex = cells.Count });
        return cells;
    }

    [Fact]
    public void 台阶退距_单斜面与分台阶()
    {
        double tan45 = Math.Tan(Math.PI / 4);
        Assert.Equal(0, TemplateDrivingEngine.BenchOffset(0, 0, 0, tan45), 9);
        Assert.Equal(10, TemplateDrivingEngine.BenchOffset(10, 0, 0, tan45), 9);                    // 单斜面 h/tanα
        Assert.Equal(10 + 5 + 3, TemplateDrivingEngine.BenchOffset(13, 10, 5, tan45), 9);           // 一个台阶(10/tan + 平盘5) + 余高 3
        Assert.Equal(2 * (10 + 5), TemplateDrivingEngine.BenchOffset(20, 10, 5, tan45), 9);
    }

    [Fact]
    public void 按距离切期_陡帮_逐刀煤岩量与工程位置()
    {
        var o = TemplateDrivingEngine.Run(Slab(), WorkLine(), volumeDriven: false, advancePerPeriod: 20, targetCoalM3PerPeriod: 0, periods: 3, alphaDeg: 89, coalDensity: 1.3, sliceWidth: 10);
        Assert.True(o.Success, o.Error);
        Assert.Equal(6, o.Cuts.Count);                                    // 60m / 10m
        Assert.Equal(3, o.Periods.Count);
        // 每列 1 煤 + 2 岩，每刀 10 列：煤 10×1000=1万m³，岩 2万m³（α=89° 退距 ≈ 0.17m/10m，顶层岩块高 20m ⇒ 退 0.35m，不跨刀）
        Assert.Equal(10000, o.Cuts[0].CoalVolM3, 1);
        Assert.Equal(20000, o.Cuts[0].RockVolM3, 1);
        Assert.Equal(20000, o.Periods[0].CoalVolM3, 1);
        Assert.Equal(40000, o.Periods[0].RockVolM3, 1);
        Assert.Equal(40000 / (20000 * 1.3), o.Periods[0].StripRatio, 6);
        Assert.Equal(60, o.TotalAdvance, 6);
        Assert.Equal(60000, o.TotalCoalVolM3, 1);
        // 工程位置线 = 基线沿推进方向推 aTo
        Assert.Equal(20, o.Periods[0].PositionLine[0].X, 6); Assert.Equal(60, o.Periods[2].PositionLine[1].X, 6);
        Assert.Equal(1000, o.Periods[0].PositionLine[0].Z, 6);
        Assert.Equal(10, o.Cuts[0].PositionLine[0].X, 6);
        Assert.False(o.RanOutOfCoal);
    }

    [Fact]
    public void 按量切期_每期目标煤量_煤不足时标记()
    {
        var o = TemplateDrivingEngine.Run(Slab(), WorkLine(), volumeDriven: true, advancePerPeriod: 0, targetCoalM3PerPeriod: 25000, periods: 2, alphaDeg: 89, coalDensity: 1.3, sliceWidth: 10);
        Assert.True(o.Success, o.Error);
        Assert.Equal(2, o.Periods.Count);
        Assert.Equal(30, o.Periods[0].AdvanceTo, 6);                       // 累积煤 ≥ 2.5万 首次在第 3 刀
        Assert.Equal(50, o.Periods[1].AdvanceTo, 6);                       // 累积 ≥ 5万 在第 5 刀
        Assert.True(o.Periods[0].CoalVolM3 >= 25000 - 1e-6);
        var far = TemplateDrivingEngine.Run(Slab(), WorkLine(), true, 0, 200000, 2, 89, 1.3, 10);
        Assert.True(far.Success); Assert.True(far.RanOutOfCoal);
    }

    [Fact]
    public void 台阶退距把上层岩推后几刀()
    {
        // α=45°：退距按块中心高出煤底板算 —— 煤块中心高 5m ⇒ 退 5m（s=5+5=10 ⇒ 第 2 刀）；中层岩高 15 ⇒ s=20（第 3 刀）；顶层岩高 25 ⇒ s=30（第 4 刀）
        var o = TemplateDrivingEngine.Run(Slab(), WorkLine(), false, 10, 0, 14, 45, 1.3, 10);
        Assert.True(o.Success, o.Error);
        Assert.Equal(0, o.Cuts[0].CoalVolM3, 1); Assert.Equal(0, o.Cuts[0].RockVolM3, 1);
        Assert.Equal(10000, o.Cuts[1].CoalVolM3, 1); Assert.Equal(0, o.Cuts[1].RockVolM3, 1);
        Assert.Equal(10000, o.Cuts[2].CoalVolM3, 1); Assert.Equal(10000, o.Cuts[2].RockVolM3, 1);   // 第 2 列煤 + 第 1 列中层岩
        Assert.Equal(20000, o.Cuts[3].RockVolM3, 1);                                                 // 第 2 列中层 + 第 1 列顶层
        Assert.Equal(200000, o.Cuts[13].CumRockVolM3, 1);                                            // 14 刀累积岩 = 全部 20 万
        Assert.Equal(100000, o.Cuts[13].CumCoalVolM3, 1);
    }

    [Fact]
    public void 切到最后一刀_驱动到物料尽头_不切期()
    {
        var o = TemplateDrivingEngine.Run(Slab(), WorkLine(), false, 10, 0, 1, 89, 1.3, 10, cutToEnd: true, benchStepHeight: 0, minBermWidth: 0);
        Assert.True(o.Success, o.Error);
        Assert.Empty(o.Periods);
        Assert.Equal(100, o.TotalAdvance, 6);                              // 10 列 × 10m
        Assert.Equal(10, o.Cuts.Count);
        Assert.Equal(100000, o.TotalCoalVolM3, 1);
        Assert.Equal(200000, o.TotalRockVolM3, 1);
        Assert.Equal(200000 / (100000 * 1.3), o.OverallStripRatio, 6);
        Assert.Equal(1000, o.MinZ, 6); Assert.Equal(1030, o.MaxZ, 6);
        // 卡阶段：第 2~3 刀之间的块
        var sub = TemplateDrivingEngine.CollectCellsBetween(Slab(), WorkLine(), 89, 0, 0, 10, 30, true, true, out double cv, out double rv);
        Assert.Equal(60, sub.Count);
        Assert.Equal(20000, cv, 1); Assert.Equal(40000, rv, 1);
        Assert.Equal(20, TemplateDrivingEngine.CollectCellsBetween(Slab(), WorkLine(), 89, 0, 0, 10, 30, true, false, out _, out _).Count);
    }

    [Fact]
    public void 量约束双前界_采煤前界与剥离超前位置()
    {
        // 上层也是煤(topCoalToo)：每刀 煤 2万 / 岩 1万。Q=4万 ⇒ 采煤前界 20m；n=1、ρ=1 ⇒ 岩目标 4万 ⇒ 剥离前界 40m ⇒ 超前 20m
        var s = TemplateDrivingEngine.SolveQuantityRatio(Slab(topCoalToo: true), WorkLine(), 40000, 1.0, 1.0, 89, 10);
        Assert.True(s.Success, s.Error);
        Assert.Equal(20, s.CoalFrontAdvance, 6);
        Assert.Equal(40, s.RockFrontAdvance, 6);
        Assert.Equal(20, s.LeadDistance, 6);
        Assert.True(s.RockLeadsCoal); Assert.True(s.CoalReachable); Assert.True(s.RockReachable);
        Assert.Equal(40000, s.ActualCoalVolM3, 1); Assert.Equal(40000, s.ActualRockVolM3, 1);
        Assert.Equal(2, s.CoalFrontLine.Count); Assert.Equal(20, s.CoalFrontLine[0].X, 6); Assert.Equal(40, s.RockFrontLine[0].X, 6);
        // 不可达：岩量目标超过总岩
        var far = TemplateDrivingEngine.SolveQuantityRatio(Slab(true), WorkLine(), 40000, 10, 1.0, 89, 10);
        Assert.True(far.Success); Assert.False(far.RockReachable);
        Assert.False(TemplateDrivingEngine.SolveQuantityRatio(Slab(true), WorkLine(), 0, 1, 1, 89, 10).Success);
    }

    [Fact]
    public void 几何预览_无块体只画刀线和期()
    {
        var o = TemplateDrivingEngine.PreviewGeometry(WorkLine(), 25, 2, 10);
        Assert.True(o.Success);
        Assert.Equal(5, o.Cuts.Count);                                     // 50/10
        Assert.Equal(2, o.Periods.Count);
        Assert.Equal(50, o.Cuts[4].AdvanceTo, 6);
        Assert.Equal(25, o.Periods[0].PositionLine[0].X, 6);
        Assert.Equal(0, o.TotalCoalVolM3, 9);
        Assert.False(TemplateDrivingEngine.Run(new List<CellBox>(), WorkLine(), false, 10, 0, 1, 45, 1.3, 10).Success);
        Assert.Contains("扫掠不到", TemplateDrivingEngine.Run(Slab(), WorkLineSamples.FromWorkLine(new[] { (500.0, 0.0), (500.0, 100.0) }, 1000, false, new[] { (600.0, 0.0), (600.0, 100.0) }, null), false, 10, 0, 1, 45, 1.3, 10).Error);
    }

    [Fact]
    public void 台阶面剖面与三角网_形态扩展逐级退距()
    {
        var prof = TemplateDrivingEngine.BenchProfile(1000, 1030, 45, 10, 5);
        // 底 → (10,1010) 坡顶 → (15,1010) 平盘 → (25,1020) → (30,1020) → (40,1030)
        Assert.Equal(6, prof.Count);
        Assert.Equal((40, 1030), (Math.Round(prof[^1].off, 6), prof[^1].z));
        var (v, t) = TemplateDrivingEngine.FaceMesh(WorkLine(), 50, prof);
        Assert.Equal(2 * prof.Count, v.Count);
        Assert.Equal(2 * (prof.Count - 1), t.Count);
        Assert.Equal(50, v[0].x, 6); Assert.Equal(1000, v[0].z, 6);            // 趾点在推进 50
        Assert.Equal(10, v[prof.Count - 1].x, 6); Assert.Equal(1030, v[prof.Count - 1].z, 6);   // 顶退 40

        var fe = BenchFormExpander.ExpandOnLevels(WorkLine(), new[] { 1030.0, 1020.0, 1010.0 }, 45, 5, 1.0);
        Assert.True(fe.Success, fe.Error);
        Assert.Equal(3, fe.LevelCount);
        Assert.Equal(0, fe.Levels[0][0].X, 6);
        Assert.Equal(15, fe.Levels[1][0].X, 6);                               // Δz 10 / tan45 + 平盘 5
        Assert.Equal(30, fe.Levels[2][0].X, 6);
        Assert.Equal(30, fe.MaxRetreat, 6);
        Assert.Equal(15, BenchFormExpander.ExpandOnLevels(WorkLine(), new[] { 1030.0, 1020.0, 1010.0 }, 45, 5, 0.5).MaxRetreat, 6);
    }
}
