using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 生产任务动态调整（§三五九）。
///
/// 两条头号判据：
///   ① <b>实绩没录 ≠ 实绩为 0</b>。没录时达成度与欠量一律判不了；
///      按 0 算的话，没录实绩的面会全部变成"欠产 100%"，重排就把整天的量再排一遍。
///   ② <b>原因码决定动作</b>。同样是"没干够"，故障要顶设备、缺车要补车、缺料要切面、
///      天气要全盘降效回摊 —— <b>动作选错了，重排出来的计划照样排得满满的，而现场还是干不动</b>。
/// </summary>
public class AdjustModelTests
{
    private static ShiftTask T(string zone = "北一采", double plan = 3000, double actual = 0,
                               string equip = "E1", ProcessType p = ProcessType.Load,
                               string shift = "早班", double start = 0, double end = 8, int trucks = 3)
    {
        var t = new ShiftTask
        {
            Id = "D-" + zone, Process = p, Shift = shift, WorkZone = zone, TargetVolumeM3 = plan,
            ActualVolumeM3 = actual, StartHour = start, EndHour = end, PlannedHours = end - start,
            Group = new EquipmentGroup { MainEquipment = equip, RecommendedTrucks = 5, GroupCapacityM3PerH = 300 },
        };
        for (int i = 0; i < trucks; i++) t.Group.Trucks.Add("T" + i);
        return t;
    }

    private static ExploderConfig Cfg(params (string Zone, double Target)[] faces)
    {
        var c = new ExploderConfig { IdPrefix = "D", Shifts = { new ShiftWindow("早班", 0, 8) } };
        foreach (var (z, tg) in faces)
            c.Faces.Add(new FaceInput
            {
                Zone = z, Process = ProcessType.Load, DayTargetM3 = tg,
                Group = new EquipmentGroup { MainEquipment = "E-" + z, RecommendedTrucks = 5, GroupCapacityM3PerH = 300, Trucks = { "T1", "T2" } },
            });
        return c;
    }

    // ── 实绩没录 ≠ 0 ────────────────────────────────────────
    [Fact]
    public void 实绩_没录时达成度与欠量都判不了()
    {
        // ★ 按 0 算会让没录的面全成"欠产 100%"
        var r = AdjustModel.BuildRows(new[] { T(actual: 0) }, "早班").Single();
        Assert.Null(r.ActualM3);
        Assert.Null(r.AttainmentPct);
        Assert.Null(r.ShortfallM3);
        Assert.Equal("—", r.Actual);
        Assert.Equal("—", r.Attainment);
    }

    [Fact]
    public void 实绩_录了就算得出达成度与欠量()
    {
        var r = AdjustModel.BuildRows(new[] { T(plan: 3000, actual: 1200) }, "早班").Single();
        Assert.Equal(1200, r.ActualM3);
        Assert.Equal(40, r.AttainmentPct!.Value, 6);
        Assert.Equal(1800, r.ShortfallM3!.Value, 6);
    }

    [Fact]
    public void 实绩_超额时欠量是零不是负数()
    {
        var r = AdjustModel.BuildRows(new[] { T(plan: 1000, actual: 1500) }, "早班").Single();
        Assert.Equal(0, r.ShortfallM3!.Value, 9);
        Assert.Equal(150, r.AttainmentPct!.Value, 6);
    }

    [Fact]
    public void 实绩_计划为零时达成度判不了不是无穷()
    {
        var r = AdjustModel.BuildRows(new[] { T(plan: 0, actual: 100) }, "早班").Single();
        Assert.Null(r.AttainmentPct);
        Assert.Equal("—", r.Plan);
    }

    // ── 原因码 → 动作 ──────────────────────────────────────
    [Theory]
    [InlineData(SchedReason.Fault, AdjustStrategy.ReassignBackup)]
    [InlineData(SchedReason.TruckShortage, AdjustStrategy.AddTrucks)]
    [InlineData(SchedReason.OreShortage, AdjustStrategy.SwitchFace)]
    [InlineData(SchedReason.Weather, AdjustStrategy.ReduceCapacity)]
    [InlineData(SchedReason.Absence, AdjustStrategy.ReduceCapacity)]
    [InlineData(SchedReason.OverPlanned, AdjustStrategy.ReduceTarget)]
    [InlineData(SchedReason.ProcessWait, AdjustStrategy.RollForward)]
    public void 原因码_判了就把动作换成对应那一个(SchedReason reason, AdjustStrategy want)
    {
        var row = AdjustModel.BuildRows(new[] { T() }, "早班").Single();
        AdjustModel.ApplyReason(row, reason);
        Assert.Equal(want, row.Strategy);
        Assert.Equal(reason, row.Reason);
    }

    [Fact]
    public void 原因码_每一个都有一句动作建议不留空()
    {
        foreach (var r in AdjustModel.Reasons)
        {
            Assert.False(string.IsNullOrWhiteSpace(AdjustModel.AdviceOf(r)), r.ToString());
            Assert.NotEqual("—", AdjustModel.AdviceOf(r));
            Assert.False(string.IsNullOrWhiteSpace(AdjustModel.ReasonZh(r)), r.ToString());
        }
    }

    [Fact]
    public void 原因码_没判时不给动作建议而是提醒先判()
    {
        var row = AdjustModel.BuildRows(new[] { T() }, "早班").Single();
        Assert.Null(row.Reason);
        Assert.Equal("—", row.ReasonText);
        Assert.Contains("先判原因码", row.Advice);
    }

    [Fact]
    public void 动作_每一个都有中文名()
        => Assert.All(AdjustModel.Strategies, s => Assert.False(string.IsNullOrWhiteSpace(AdjustModel.StrategyZh(s))));

    // ── 建行 ────────────────────────────────────────────────
    [Fact]
    public void 建行_空闲笔不列且只出本班()
    {
        var rows = AdjustModel.BuildRows(new[]
        {
            T(zone: "A", shift: "早班"),
            T(zone: "B", shift: "中班"),
            T(zone: "C", shift: "早班", p: ProcessType.Idle),
        }, "早班");
        Assert.Single(rows);
        Assert.Equal("A", rows[0].Zone);
    }

    [Fact]
    public void 建行_按开始时刻排序()
    {
        var rows = AdjustModel.BuildRows(new[]
        { T(zone: "晚", start: 4, end: 8), T(zone: "早", start: 0, end: 4) }, "早班");
        Assert.Equal(new[] { "早", "晚" }, rows.Select(r => r.Zone));
    }

    // ── 重排 ────────────────────────────────────────────────
    [Fact]
    public void 重排_没有盘子时说清楚而不是静默什么都不做()
    {
        var res = AdjustModel.Run(null, new List<AdjustRow>(), 0);
        Assert.False(res.Ran);
        Assert.Contains("先把当日作业面装出来", res.Blocked);
        Assert.Equal(res.Blocked, res.Summary);
    }

    [Fact]
    public void 重排_本班没有任务时说清楚()
    {
        var res = AdjustModel.Run(Cfg(("A", 3000)), new List<AdjustRow>(), 0);
        Assert.False(res.Ran);
        Assert.Contains("没有任务", res.Blocked);
    }

    [Fact]
    public void 重排_原盘子不动调整结果只在返回的计划里()
    {
        // ★ 拿原盘子再装一次箱得到的还是调整前那一版，而它算得出来、也不报错
        var cfg = Cfg(("A", 3000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 1200) }, "早班");
        var res = AdjustModel.Run(cfg, rows, 0);
        Assert.Equal(3000, cfg.Faces[0].DayTargetM3, 6);
        Assert.NotNull(res.Plan);
    }

    [Fact]
    public void 重排_把实绩扣掉之后的剩余量回摊()
    {
        var cfg = Cfg(("A", 3000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 1200) }, "早班");
        var res = AdjustModel.Run(cfg, rows, 0);
        Assert.True(res.Ran);
        Assert.Equal(1800, res.RolledShortfallM3, 0);       // 3000 − 1200
        Assert.Contains("回摊剩余", res.Summary);
    }

    [Fact]
    public void 重排_实绩没录时按一点没干算不是按全干完算()
    {
        // 没录 ⇒ 回摊整条 —— 与"达成度判不了"并不矛盾：重排要的是"还剩多少没干"，
        // 而没有实绩的最保守假设就是一点没干（按干完算会把活漏掉）
        var cfg = Cfg(("A", 3000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 0) }, "早班");
        var res = AdjustModel.Run(cfg, rows, 0);
        Assert.Equal(3000, res.RolledShortfallM3, 0);
    }

    [Fact]
    public void 重排_缺车判定走补车动作()
    {
        var cfg = Cfg(("A", 3000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 500) }, "早班");
        AdjustModel.ApplyReason(rows[0], SchedReason.TruckShortage);
        var res = AdjustModel.Run(cfg, rows, 0);
        Assert.Contains("补", string.Join("|", res.Notes));
        Assert.Contains("卡车", string.Join("|", res.Notes));
    }

    [Fact]
    public void 重排_故障判定走备机顶替()
    {
        var cfg = Cfg(("A", 3000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 500) }, "早班");
        AdjustModel.ApplyReason(rows[0], SchedReason.Fault);
        var res = AdjustModel.Run(cfg, rows, 0);
        Assert.Contains("顶替", string.Join("|", res.Notes));
    }

    [Fact]
    public void 重排_缺料判定把剩余量切到别的面()
    {
        var cfg = Cfg(("A", 3000), ("B", 1000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 500) }, "早班");
        AdjustModel.ApplyReason(rows[0], SchedReason.OreShortage);
        var res = AdjustModel.Run(cfg, rows, 0);
        Assert.Contains("切至", string.Join("|", res.Notes));
        // ★ 重排走的是盘子的一份克隆 —— 原盘子不动，调整结果在 res.Plan 里
        Assert.Equal(3000, cfg.Faces.Single(f => f.Zone == "A").DayTargetM3, 6);
        Assert.NotNull(res.Plan);
        Assert.DoesNotContain(res.Plan!.Tasks,
            x => x.WorkZone == "A" && x.Process == ProcessType.Load && x.TargetVolumeM3 > 1);
    }

    [Fact]
    public void 重排_没判原因码的面按顺延走()
    {
        var cfg = Cfg(("A", 3000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 500) }, "早班");
        var res = AdjustModel.Run(cfg, rows, 0);
        Assert.Contains("顺延", string.Join("|", res.Notes));
    }

    [Fact]
    public void 重排_一个面出现多行时以先判出原因码的那一行为准()
    {
        // 一个面同时"缺车"又"缺料"，两种动作会互相抵消 —— 须由人明确一个
        var cfg = Cfg(("A", 3000), ("B", 1000));
        var rows = AdjustModel.BuildRows(new[]
        {
            T(zone: "A", plan: 2000, actual: 100, start: 0, end: 4),
            T(zone: "A", plan: 1000, actual: 0, equip: "E2", start: 4, end: 8),
        }, "早班");
        AdjustModel.ApplyReason(rows[0], SchedReason.OreShortage);
        AdjustModel.ApplyReason(rows[1], SchedReason.TruckShortage);
        var res = AdjustModel.Run(cfg, rows, 0);
        // 走的是第一行判出来的「切面」，不是第二行的「补车」
        Assert.Contains("切至", string.Join("|", res.Notes));
        Assert.DoesNotContain("补 ", string.Join("|", res.Notes));
    }

    [Fact]
    public void 重排_什么都不欠时也给一句话不留空白()
    {
        var cfg = Cfg(("A", 3000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 3000) }, "早班");
        var res = AdjustModel.Run(cfg, rows, 0);
        Assert.True(res.Ran);
        Assert.NotEmpty(res.Notes);
    }

    [Fact]
    public void 重排_摘要里带起点时刻与回摊量()
    {
        var cfg = Cfg(("A", 3000));
        var rows = AdjustModel.BuildRows(new[] { T(zone: "A", plan: 3000, actual: 1000) }, "早班");
        var res = AdjustModel.Run(cfg, rows, 9.5);
        Assert.Contains("自 09:30 起重排", res.Summary);
        Assert.Contains("2,000 m³", res.Summary);
    }

    [Fact]
    public void 摘要_没跑过时不摆一堆零()
        => Assert.Equal("还没重排。", new AdjustResult().Summary);
}
