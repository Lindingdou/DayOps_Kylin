using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using Cell = PitMine3D.Kylin.Cad.DriveSequence.Cell;

namespace PitMine3D.Kylin.Tests;

/// <summary>开采程序逐期切分回归（忠实原 TemplateDrivingEngine 距离驱动核: 块体→分期煤/岩量+累计剥采比）。</summary>
public class DriveSequenceTests
{
    // 3 列(X=5/15/25), 每列 2 煤 + 8 岩, cellVol=1000。dir=+X, 步距 10, sMin=5 → 期 0/1/2。
    private static List<Cell> ThreeColumns()
    {
        var cells = new List<Cell>();
        foreach (double cx in new[] { 5.0, 15.0, 25.0 })
        {
            for (int k = 0; k < 2; k++) cells.Add(new Cell(cx, 0, 0, 1000, true));    // 2 煤
            for (int k = 0; k < 8; k++) cells.Add(new Cell(cx, 0, 0, 1000, false));   // 8 岩
        }
        return cells;
    }

    [Fact]
    public void Distance_driven_tallies_coal_rock_and_cumulative_strip_ratio()
    {
        var r = DriveSequence.SweepByDistance(ThreeColumns(), 1, 0, advancePerPeriod: 10, coalDensity: 1.3);
        Assert.Equal(3, r.Periods.Count);                       // 3 期
        Assert.Equal(6000, r.TotalCoalVolM3, 4);                // 3×2×1000
        Assert.Equal(24000, r.TotalRockVolM3, 4);               // 3×8×1000
        Assert.Equal(24000.0 / (6000.0 * 1.3), r.OverallStripRatio, 6);
        // 每期: 煤 2000 / 岩 8000。
        Assert.All(r.Periods, p => { Assert.Equal(2000, p.CoalVolM3, 4); Assert.Equal(8000, p.RockVolM3, 4); });
        // 累计剥采比逐期恒定(均质): p0=8000/(2000·1.3), p2=24000/(6000·1.3) 相等。
        Assert.Equal(8000.0 / (2000.0 * 1.3), r.Periods[0].CumStripRatio, 6);
        Assert.Equal(24000.0 / (6000.0 * 1.3), r.Periods[2].CumStripRatio, 6);
        // 累计量递增。
        Assert.Equal(2000, r.Periods[0].CumCoalVolM3, 4);
        Assert.Equal(6000, r.Periods[2].CumCoalVolM3, 4);
    }

    [Fact]
    public void Direction_and_maxperiods_and_guards()
    {
        var cells = ThreeColumns();
        // +Y 方向: 所有格 Y=0 → 全落一期。
        var ry = DriveSequence.SweepByDistance(cells, 0, 1, 10, 1.3);
        Assert.Single(ry.Periods);
        Assert.Equal(30000, ry.Periods[0].RockVolM3 + ry.Periods[0].CoalVolM3, 4);   // 全 30 格(3列×10)×1000
        // maxPeriods=2 → 只前 2 期。
        var r2 = DriveSequence.SweepByDistance(cells, 1, 0, 10, 1.3, maxPeriods: 2);
        Assert.Equal(2, r2.Periods.Count);
        Assert.Equal(4000, r2.TotalCoalVolM3, 4);               // 只 2 期 ×2×1000
        // 守卫: 空/零步距/零密度 → 空。
        Assert.Empty(DriveSequence.SweepByDistance(new List<Cell>(), 1, 0, 10, 1.3).Periods);
        Assert.Empty(DriveSequence.SweepByDistance(cells, 1, 0, 0, 1.3).Periods);
        Assert.Empty(DriveSequence.SweepByDistance(cells, 0, 0, 10, 1.3).Periods);   // 零方向
    }

    [Fact]
    public void Volume_driven_cuts_periods_at_coal_target()
    {
        // 4 刀(X=5/15/25/35), 每刀 1 煤 + 1 岩(各 vol 1000)。sliceWidth=10。
        var cells = new List<Cell>();
        foreach (double cx in new[] { 5.0, 15.0, 25.0, 35.0 }) { cells.Add(new Cell(cx, 0, 0, 1000, true)); cells.Add(new Cell(cx, 0, 0, 1000, false)); }
        // 目标煤量 2000 → 每期 2 刀(煤 2000) → 2 期。
        var r = DriveSequence.SweepByVolume(cells, 1, 0, sliceWidth: 10, targetCoalVolM3: 2000, coalDensity: 1.3);
        Assert.Equal(2, r.Periods.Count);
        Assert.All(r.Periods, p => { Assert.Equal(2000, p.CoalVolM3, 4); Assert.Equal(2000, p.RockVolM3, 4); });
        Assert.Equal(4000, r.TotalCoalVolM3, 4);
        // 目标 1000 → 每刀一期 → 4 期各煤 1000。
        var r2 = DriveSequence.SweepByVolume(cells, 1, 0, 10, 1000, 1.3);
        Assert.Equal(4, r2.Periods.Count);
        Assert.All(r2.Periods, p => Assert.Equal(1000, p.CoalVolM3, 4));
        // 守卫: 零目标/零 sliceWidth/空 → 空。
        Assert.Empty(DriveSequence.SweepByVolume(cells, 1, 0, 10, 0, 1.3).Periods);
        Assert.Empty(DriveSequence.SweepByVolume(cells, 1, 0, 0, 2000, 1.3).Periods);
    }

    [Fact]
    public void Balance_csv_feeds_stripping_balance()
    {
        var r = DriveSequence.SweepByDistance(ThreeColumns(), 1, 0, 10, 1.3);
        var csv = DriveSequence.ToBalanceCsv(r, 1.3);
        Assert.Contains("period,coal_wan_t,waste_wan_m3", csv);
        Assert.Equal(1 + 3, csv.Trim().Split('\n').Length);     // 头 + 3 期
        // 首期 采出 = 2000·1.3/1e4 = 0.26 万t, 剥离 = 8000/1e4 = 0.8 万m³。
        Assert.Contains("1,0.26,0.8", csv);
    }

    [Fact]
    public void Bench_offset_known_values()
    {
        Assert.Equal(0, DriveSequence.BenchOffset(0, 0, 0, 45), 6);
        Assert.Equal(30, DriveSequence.BenchOffset(30, 0, 0, 45), 4);   // 单斜面 h/tan45=h
        Assert.Equal(30 / System.Math.Tan(60 * System.Math.PI / 180), DriveSequence.BenchOffset(30, 0, 0, 60), 4);
        Assert.Equal(40, DriveSequence.BenchOffset(30, 15, 5, 45), 4);  // 2 台阶: 2×(15/1+5)=40
        Assert.Equal(25, DriveSequence.BenchOffset(20, 15, 5, 45), 4);  // 1 台阶(20)+余5: (15+5)+5=25
    }

    [Fact]
    public void Setback_shifts_high_cells_to_later_periods()
    {
        // 同 XY(X=0) 两格: 底 Z=0 / 高 Z=30。
        var cells = new List<Cell> { new(0, 0, 0, 1000, true), new(0, 0, 30, 1000, true) };
        // 无退距(faceAngle=0): 两格同 X=0 → 同期 0。
        Assert.Single(DriveSequence.SweepByDistance(cells, 1, 0, 10, 1.3).Periods);
        // 有退距(45° 单斜面): 高格退距 30 → 期 3(底格期0)。
        var sb = DriveSequence.SweepByDistance(cells, 1, 0, 10, 1.3, faceAngleDeg: 45);
        Assert.Equal(4, sb.Periods.Count);                     // 期 0..3
        Assert.Equal(1000, sb.Periods[0].CoalVolM3, 4);        // 底格
        Assert.Equal(0, sb.Periods[1].CoalVolM3, 4);           // 空期(退距拉开)
        Assert.Equal(1000, sb.Periods[3].CoalVolM3, 4);        // 高格
    }
}
