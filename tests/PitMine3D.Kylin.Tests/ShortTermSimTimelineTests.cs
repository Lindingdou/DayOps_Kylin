using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 短期推演的月粒度时间轴（§三四八）—— §三四七 年轨的月度孪生。
///
/// 头号判据是**两个尺度的口径同源**：同一个矿在年图上"库容够排"、在月图上"排不下"，
/// 那种不一致最难查，因为两边都觉得自己没错。故这里逐条对着年轨的判据验一遍。
/// 另一条本层独有：**内排率在没有排弃量时是"—"不是 0%**。
/// </summary>
public class ShortTermSimTimelineTests
{
    private static ShortTermPlan Plan(params (double coal, double strip, LongTermDumpMode dump)[] rows)
    {
        var p = new ShortTermPlan { RatioCeiling = 12, PlanYear = 2027 };
        int m = 1;
        foreach (var (coal, strip, dump) in rows)
        {
            p.Months.Add(new MonthPeriod
            {
                Label = $"2027-{m:00}", Month = m,
                CoalWanT = coal, StripWanM3 = strip,
                Ratio = coal > 1e-9 ? strip / coal : 0,
                Dump = dump, Workdays = 25, EquipUtilPct = 80,
            });
            m++;
        }
        return p;
    }

    private static SinkRegistry Sinks(params (string id, string name, SinkKind kind, double capWanM3)[] rows)
    {
        var r = new SinkRegistry();
        foreach (var (id, name, kind, cap) in rows)
            r.Put(new SinkNode
            { Id = id, Name = name, Kind = kind, DesignCapacityM3 = cap * 1e4, FilledM3 = 0, RefEntityId = id });
        return r;
    }

    // ── 两个尺度口径同源 ────────────────────────────────────
    [Fact]
    public void 同源_松散系数与年轨是同一个()
        => Assert.Equal(LongTermSimTimeline.RockSwell, ShortTermSimTimeline.RockSwell, 9);

    [Fact]
    public void 同源_同一组量在年月两轨算出同样的占容与溢出()
    {
        // ★ 本文件的头号判据：口径一致 ⇒ 同样的输入必须得到同样的结论
        double kr = ShortTermSimTimeline.RockSwell;
        var sinks1 = Sinks(("D1", "北排", SinkKind.ExternalDump, 100 * kr));
        var sinks2 = Sinks(("D1", "北排", SinkKind.ExternalDump, 100 * kr));

        var lt = new LongTermPlan { EconomicStripRatioMax = 12 };
        lt.Periods.Add(new PlanPeriod { Label = "2027", CoalWanT = 50, StripWanM3 = 100, Dump = LongTermDumpMode.External });
        lt.Periods.Add(new PlanPeriod { Label = "2028", CoalWanT = 50, StripWanM3 = 100, Dump = LongTermDumpMode.External });
        var st = Plan((50, 100, LongTermDumpMode.External), (50, 100, LongTermDumpMode.External));

        var fy = LongTermSimTimeline.Build(lt, sinks1);
        var fm = ShortTermSimTimeline.Build(st, sinks2);
        Assert.Equal(fy[0].DumpWanM3, fm[0].DumpWanM3, 6);
        Assert.Equal(fy[1].SpilledWanM3, fm[1].SpilledWanM3, 6);
        Assert.Equal(LongTermSimTimeline.FirstSpillIndex(fy), ShortTermSimTimeline.FirstSpillIndex(fm));
    }

    [Fact]
    public void 换算_扣的是占容方不是实方()
    {
        var f = ShortTermSimTimeline.Build(Plan((10, 100, LongTermDumpMode.External))).Single();
        Assert.Equal(100 * ShortTermSimTimeline.RockSwell, f.DumpWanM3, 6);
        Assert.True(f.DumpWanM3 > f.StripWanM3);
    }

    // ── 库容 ────────────────────────────────────────────────
    [Fact]
    public void 库容_排不下的量从哪个月起算得出来()
    {
        double kr = ShortTermSimTimeline.RockSwell;
        var p = Plan((10, 100, LongTermDumpMode.External),
                     (10, 100, LongTermDumpMode.External),
                     (10, 100, LongTermDumpMode.External));
        var frames = ShortTermSimTimeline.Build(p, Sinks(("D1", "北排", SinkKind.ExternalDump, 100 * kr * 2)));
        Assert.Equal(0, frames[0].SpilledWanM3, 6);
        Assert.Equal(100 * kr, frames[2].SpilledWanM3, 3);
        Assert.Equal(2, ShortTermSimTimeline.FirstSpillIndex(frames));
    }

    [Fact]
    public void 库容_没给去向时只出量不做校核()
    {
        // ★「没配台账」与「库容真的不够」必须分得开（同 §三四七 修过的那条）
        foreach (var sinks in new[] { (SinkRegistry?)null, new SinkRegistry() })
        {
            var frames = ShortTermSimTimeline.Build(Plan((10, 100, LongTermDumpMode.External)), sinks);
            Assert.Equal(0, frames[0].SpilledWanM3, 9);
            Assert.Empty(frames[0].RemainWanM3);
            Assert.Equal(-1, ShortTermSimTimeline.FirstSpillIndex(frames));
        }
    }

    [Fact]
    public void 库容_推演不改动台账真实已填量()
    {
        var sinks = Sinks(("D1", "北排", SinkKind.ExternalDump, 10));
        double before = sinks.Find("D1")!.FilledM3;
        ShortTermSimTimeline.Build(Plan((10, 500, LongTermDumpMode.External)), sinks);
        Assert.Equal(before, sinks.Find("D1")!.FilledM3, 9);
    }

    [Fact]
    public void 库容_内排优先扣()
    {
        var sinks = Sinks(("EX", "外排", SinkKind.ExternalDump, 10000),
                          ("IN", "内排", SinkKind.InternalDump, 10000));
        var f = ShortTermSimTimeline.Build(Plan((10, 100, LongTermDumpMode.Internal)), sinks).Single();
        Assert.True(f.RemainWanM3["IN"] < f.RemainWanM3["EX"]);
    }

    [Fact]
    public void 库容_通过型去向不进剩余表()
    {
        var sinks = Sinks(("D1", "北排", SinkKind.ExternalDump, 1000));
        sinks.Put(new SinkNode { Id = "LUP-1", Name = "破碎站", Kind = SinkKind.Crusher, DesignCapacityM3 = 0 });
        var f = ShortTermSimTimeline.Build(Plan((10, 100, LongTermDumpMode.External)), sinks).Single();
        Assert.True(f.RemainWanM3.ContainsKey("D1"));
        Assert.False(f.RemainWanM3.ContainsKey("LUP-1"));
    }

    // ── 内排率 ──────────────────────────────────────────────
    [Fact]
    public void 内排率_按排弃占容加权()
    {
        var p = Plan((10, 100, LongTermDumpMode.Internal), (10, 300, LongTermDumpMode.External));
        Assert.Equal(25.0, ShortTermSimTimeline.InnerDumpPct(ShortTermSimTimeline.Build(p))!.Value, 6);
    }

    [Fact]
    public void 内排率_没有排弃量时是判不了而不是零()
    {
        // ★ "没有排弃量"与"内排率 0%"是两回事
        var p = Plan((10, 0, LongTermDumpMode.External));
        Assert.Null(ShortTermSimTimeline.InnerDumpPct(ShortTermSimTimeline.Build(p)));
        Assert.Contains("内排率 —", ShortTermSimTimeline.Summary(p, ShortTermSimTimeline.Build(p)));
    }

    [Fact]
    public void 内排率_全内排是一百()
    {
        var p = Plan((10, 100, LongTermDumpMode.Internal), (10, 100, LongTermDumpMode.Internal));
        Assert.Equal(100.0, ShortTermSimTimeline.InnerDumpPct(ShortTermSimTimeline.Build(p))!.Value, 6);
    }

    // ── 峰月与标记 ──────────────────────────────────────────
    [Fact]
    public void 峰月_取采出最大的那个月()
    {
        var p = Plan((10, 100, LongTermDumpMode.External),
                     (30, 100, LongTermDumpMode.External),
                     (20, 100, LongTermDumpMode.External));
        Assert.Equal(1, ShortTermSimTimeline.PeakIndex(ShortTermSimTimeline.Build(p)));
    }

    [Fact]
    public void 峰月_空表返回负一()
        => Assert.Equal(-1, ShortTermSimTimeline.PeakIndex(Array.Empty<ShortTermSimFrame>()));

    [Fact]
    public void 提示_检修月与峰月标出来()
    {
        var p = Plan((10, 100, LongTermDumpMode.External), (10, 100, LongTermDumpMode.External));
        p.Months[0].IsMaintenance = true;
        p.Months[1].IsPeak = true;
        var frames = ShortTermSimTimeline.Build(p);
        Assert.Contains(frames[0].Notes, n => n.Contains("检修月"));
        Assert.Contains(frames[1].Notes, n => n.Contains("峰月"));
    }

    [Fact]
    public void 提示_设备利用超百分百要点出来()
    {
        // ★ 公式与原版一致, >100% 不是算错; 但图上那条线会被钳在顶端,
        //   不点出来就会被当成"一直满负荷"
        var p = Plan((10, 100, LongTermDumpMode.External));
        p.Months[0].EquipUtilPct = 756;
        var f = ShortTermSimTimeline.Build(p).Single();
        Assert.Contains(f.Notes, n => n.Contains("超 100%") && n.Contains("干不完"));
    }

    [Fact]
    public void 提示_设备利用不超时不报()
    {
        var p = Plan((10, 100, LongTermDumpMode.External));
        p.Months[0].EquipUtilPct = 88;
        Assert.DoesNotContain(ShortTermSimTimeline.Build(p).Single().Notes, n => n.Contains("超 100%"));
    }

    [Fact]
    public void 提示_剥采比超上限报出来()
    {
        var p = Plan((10, 200, LongTermDumpMode.External));   // 比 20 > 上限 12
        Assert.Contains(ShortTermSimTimeline.Build(p).Single().Notes, n => n.Contains("超上限"));
    }

    [Fact]
    public void 提示_上限为零时不报()
    {
        var p = Plan((10, 2000, LongTermDumpMode.External));
        p.RatioCeiling = 0;   // 0 = 不限
        Assert.DoesNotContain(ShortTermSimTimeline.Build(p).Single().Notes, n => n.Contains("超上限"));
    }

    // ── 字段搬运 ────────────────────────────────────────────
    [Fact]
    public void 搬运_作业日设备利用完成率推进距离都带过来()
    {
        var p = Plan((10, 100, LongTermDumpMode.External));
        p.Months[0].Workdays = 22.5;
        p.Months[0].EquipUtilPct = 91;
        p.Months[0].CompletionPct = 103.5;
        p.Months[0].AdvanceM = 47;
        p.Months[0].ActiveFace = "北一采";
        var f = ShortTermSimTimeline.Build(p).Single();
        Assert.Equal(22.5, f.Workdays, 6);
        Assert.Equal(91, f.EquipUtilPct, 6);
        Assert.Equal(103.5, f.CompletionPct, 6);
        Assert.Equal(47, f.AdvanceM, 6);
        Assert.Equal("北一采", f.ActiveFace);
        Assert.Equal(1, f.Month);
    }

    [Fact]
    public void 累计_逐月累加()
    {
        double kr = ShortTermSimTimeline.RockSwell;
        var frames = ShortTermSimTimeline.Build(Plan((10, 100, LongTermDumpMode.External), (20, 300, LongTermDumpMode.External)));
        Assert.Equal(30, frames[1].CumCoalWanT, 6);
        Assert.Equal(400 * kr, frames[1].CumDumpWanM3, 6);
    }

    // ── 兜底与文案 ──────────────────────────────────────────
    [Fact]
    public void 兜底_空方案不抛()
    {
        Assert.Empty(ShortTermSimTimeline.Build(null));
        Assert.Empty(ShortTermSimTimeline.Build(new ShortTermPlan()));
        Assert.Contains("先排产", ShortTermSimTimeline.Summary(null, Array.Empty<ShortTermSimFrame>()));
    }

    [Fact]
    public void 文案_没接台账时说未校核而不是够排()
    {
        var p = Plan((10, 100, LongTermDumpMode.External));
        string s = ShortTermSimTimeline.Summary(p, ShortTermSimTimeline.Build(p, null));
        Assert.Contains("未做库容校核", s);
        Assert.DoesNotContain("库容够排", s);
    }

    [Fact]
    public void 文案_顶栏给月数累计内排率与峰月()
    {
        var p = Plan((10, 100, LongTermDumpMode.Internal), (30, 100, LongTermDumpMode.External));
        string s = ShortTermSimTimeline.Summary(p, ShortTermSimTimeline.Build(p));
        Assert.Contains("2 个月", s);
        Assert.Contains("累计采出", s);
        Assert.Contains("内排率", s);
        Assert.Contains("峰月", s);
    }

    // ── 与真排产联动 ────────────────────────────────────────
    [Fact]
    public void 联动_真排产出来的月计划推得出完整时间轴()
    {
        var p = new ShortTermPlan();
        ShortTermScheduler.Schedule(p);
        Assert.NotEmpty(p.Months);
        var frames = ShortTermSimTimeline.Build(p, Sinks(("D1", "北排", SinkKind.ExternalDump, 100000)));
        Assert.Equal(p.Months.Count, frames.Count);
        Assert.All(frames, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
        Assert.True(frames[^1].CumCoalWanT > 0);
        Assert.True(ShortTermSimTimeline.PeakIndex(frames) >= 0);
    }

    [Fact]
    public void 联动_库容给小了一定报排不下()
    {
        var p = new ShortTermPlan();
        ShortTermScheduler.Schedule(p);
        var frames = ShortTermSimTimeline.Build(p, Sinks(("D1", "小场", SinkKind.ExternalDump, 1)));
        Assert.True(ShortTermSimTimeline.FirstSpillIndex(frames) >= 0);
    }
}
