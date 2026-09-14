using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 中长远推演的年粒度时间轴（§三四七）。
///
/// 这一层比"看计划表"多出来的东西只有一样：**逐年扣库容之后排不下的量**。
/// 故头号判据是它 —— 库容够时不报、不够时从哪一年开始报、报多少。
/// 另两条：**扣的是占容方不是实方**（拿实方扣会把排土场算小一大截），
/// **推演不能改动台账上的真实已填量**。
/// </summary>
public class LongTermSimTimelineTests
{
    private static LongTermPlan Plan(params (double coal, double strip, LongTermDumpMode dump)[] rows)
    {
        var p = new LongTermPlan { EconomicStripRatioMax = 12, StartYear = 2027 };
        int y = 2027;
        foreach (var (coal, strip, dump) in rows)
            p.Periods.Add(new PlanPeriod
            {
                Label = (y++).ToString(),
                CoalWanT = coal, StripWanM3 = strip,
                Ratio = coal > 1e-9 ? strip / coal : 0,
                Dump = dump,
                Phase = coal > 0 ? PlanPhase.Stable : PlanPhase.Basic,
            });
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

    // ── 占容方换算 ──────────────────────────────────────────
    [Fact]
    public void 换算_扣的是占容方不是实方()
    {
        // ★ 拿实方扣会把排土场算小一大截（Kr > 1）
        var p = Plan((100, 1000, LongTermDumpMode.External));
        var f = LongTermSimTimeline.Build(p).Single();
        Assert.Equal(1000 * LongTermSimTimeline.RockSwell, f.DumpWanM3, 6);
        Assert.True(f.DumpWanM3 > f.StripWanM3, "占容方一定大于实方");
    }

    [Fact]
    public void 换算_松散系数取自物料目录不另立一份()
        => Assert.Equal(MaterialCatalog.Resolve(MaterialCatalog.Rock).ResidualSwellFactor,
                        LongTermSimTimeline.RockSwell, 9);

    // ── 库容扣减 ────────────────────────────────────────────
    [Fact]
    public void 库容_够排时一年都不报()
    {
        var p = Plan((100, 100, LongTermDumpMode.External), (100, 100, LongTermDumpMode.External));
        var frames = LongTermSimTimeline.Build(p, Sinks(("D1", "北排", SinkKind.ExternalDump, 100000)));
        Assert.All(frames, f => Assert.Equal(0, f.SpilledWanM3, 9));
        Assert.Equal(-1, LongTermSimTimeline.FirstSpillIndex(frames));
        Assert.Contains("库容够排", LongTermSimTimeline.Summary(p, frames));
    }

    [Fact]
    public void 库容_排满那一年点名说是哪个场()
    {
        // 容量 100 万 m³，一年就排 100 万实方(占容更大) ⇒ 第一年就满
        var p = Plan((100, 100, LongTermDumpMode.External), (100, 100, LongTermDumpMode.External));
        var frames = LongTermSimTimeline.Build(p, Sinks(("D1", "北排土场", SinkKind.ExternalDump, 100)));
        Assert.Contains(frames[0].Notes, n => n.Contains("北排土场") && n.Contains("排满"));
    }

    [Fact]
    public void 库容_排不下的量从哪年起算得出来()
    {
        // ★ 这是本层存在的理由：计划表里看不见这件事
        var p = Plan((100, 100, LongTermDumpMode.External),
                     (100, 100, LongTermDumpMode.External),
                     (100, 100, LongTermDumpMode.External));
        double kr = LongTermSimTimeline.RockSwell;
        // 库容只够头两年多一点
        var frames = LongTermSimTimeline.Build(p, Sinks(("D1", "北排", SinkKind.ExternalDump, 100 * kr * 2)));
        Assert.Equal(0, frames[0].SpilledWanM3, 6);
        Assert.Equal(0, frames[1].SpilledWanM3, 6);
        Assert.Equal(100 * kr, frames[2].SpilledWanM3, 3);
        Assert.Equal(2, LongTermSimTimeline.FirstSpillIndex(frames));
        Assert.Contains("排不下", LongTermSimTimeline.Summary(p, frames));
    }

    [Fact]
    public void 库容_内排优先扣()
    {
        // 内排在采空区里、运距最短 —— 现场就是先往那儿排
        var p = Plan((100, 100, LongTermDumpMode.Internal));
        var sinks = Sinks(("EX", "外排", SinkKind.ExternalDump, 100000),
                          ("IN", "内排", SinkKind.InternalDump, 100000));
        var f = LongTermSimTimeline.Build(p, sinks).Single();
        Assert.True(f.RemainWanM3["IN"] < f.RemainWanM3["EX"], "应当先扣内排");
    }

    [Fact]
    public void 库容_容量不限的去向不进剩余表()
    {
        // 破碎站/煤仓是通过型, 不占库容 —— 混进"剩余库容"表里只会让人以为它能排土
        var p = Plan((100, 100, LongTermDumpMode.External));
        var sinks = Sinks(("D1", "北排", SinkKind.ExternalDump, 1000));
        sinks.Put(new SinkNode { Id = "LUP-1", Name = "破碎站", Kind = SinkKind.Crusher, DesignCapacityM3 = 0 });
        var f = LongTermSimTimeline.Build(p, sinks).Single();
        Assert.True(f.RemainWanM3.ContainsKey("D1"));
        Assert.False(f.RemainWanM3.ContainsKey("LUP-1"));
    }

    [Fact]
    public void 库容_推演不改动台账上的真实已填量()
    {
        // ★ 按副本扣：推演是"如果这么排会怎样", 不该把台账改了
        var p = Plan((100, 500, LongTermDumpMode.External));
        var sinks = Sinks(("D1", "北排", SinkKind.ExternalDump, 100));
        double before = sinks.Find("D1")!.FilledM3;
        LongTermSimTimeline.Build(p, sinks);
        Assert.Equal(before, sinks.Find("D1")!.FilledM3, 9);
        Assert.Equal(100 * 1e4, sinks.Find("D1")!.RemainingM3, 3);
    }

    [Fact]
    public void 库容_没给去向时只出量不做校核()
    {
        // ★「没配去向台账」与「库容真的不够」必须分得开 —— 前者不该报成"全排不下"
        foreach (var sinks in new[] { (PitMine3D.Kylin.Cad.Tasks.SinkRegistry?)null, new PitMine3D.Kylin.Cad.Tasks.SinkRegistry() })
        {
            var frames = LongTermSimTimeline.Build(Plan((100, 100, LongTermDumpMode.External)), sinks);
            Assert.Single(frames);
            Assert.Equal(0, frames[0].SpilledWanM3, 9);
            Assert.Empty(frames[0].RemainWanM3);
            Assert.Equal(-1, LongTermSimTimeline.FirstSpillIndex(frames));
        }
    }

    [Fact]
    public void 库容_已填过的场只按剩余算()
    {
        var p = Plan((100, 100, LongTermDumpMode.External));
        var sinks = new SinkRegistry();
        sinks.Put(new SinkNode
        { Id = "D1", Name = "北排", Kind = SinkKind.ExternalDump, DesignCapacityM3 = 1000e4, FilledM3 = 900e4 });
        var f = LongTermSimTimeline.Build(p, sinks).Single();
        // 起算是剩余 100 万 m³(不是设计 1000)；本年要排 100×Kr=115 万 m³ 占容 ⇒ 排满且溢出 15
        Assert.Equal(0, f.RemainWanM3["D1"], 6);
        Assert.Equal(100 * LongTermSimTimeline.RockSwell - 100, f.SpilledWanM3, 3);
    }

    // ── 内排与达产 ──────────────────────────────────────────
    [Fact]
    public void 内排_直接读排产算好的那一位不自己再推()
    {
        // 自己按 InnerDumpStartYear 推会把基建期也数进去 ⇒ 起转年提前, 与逐年表对不上
        var p = Plan((0, 200, LongTermDumpMode.External),
                     (100, 100, LongTermDumpMode.External),
                     (100, 100, LongTermDumpMode.Internal));
        var frames = LongTermSimTimeline.Build(p);
        Assert.False(frames[0].InnerDump);
        Assert.False(frames[1].InnerDump);
        Assert.True(frames[2].InnerDump);
        Assert.Equal(2, LongTermSimTimeline.InnerDumpStartIndex(frames));
    }

    [Fact]
    public void 内排_全外排时返回负一()
        => Assert.Equal(-1, LongTermSimTimeline.InnerDumpStartIndex(
            LongTermSimTimeline.Build(Plan((100, 100, LongTermDumpMode.External)))));

    [Fact]
    public void 提示_剥采比超n经要报出来()
    {
        var p = Plan((100, 2000, LongTermDumpMode.External));   // 比 20，n经 12
        var f = LongTermSimTimeline.Build(p).Single();
        Assert.Contains(f.Notes, n => n.Contains("超 n经"));
    }

    [Fact]
    public void 提示_剥采比不超时不报()
    {
        var p = Plan((100, 500, LongTermDumpMode.External));    // 比 5
        Assert.DoesNotContain(LongTermSimTimeline.Build(p).Single().Notes, n => n.Contains("超 n经"));
    }

    [Fact]
    public void 提示_设计计算年点出来()
    {
        var p = Plan((100, 500, LongTermDumpMode.External));
        p.Periods[0].IsDesignCalcYear = true;
        Assert.Contains(LongTermSimTimeline.Build(p).Single().Notes, n => n.Contains("达产"));
    }

    // ── 累计 ────────────────────────────────────────────────
    [Fact]
    public void 累计_采出与排弃占容逐年累加()
    {
        var p = Plan((100, 100, LongTermDumpMode.External), (200, 300, LongTermDumpMode.External));
        var frames = LongTermSimTimeline.Build(p);
        double kr = LongTermSimTimeline.RockSwell;
        Assert.Equal(100, frames[0].CumCoalWanT, 6);
        Assert.Equal(300, frames[1].CumCoalWanT, 6);
        Assert.Equal(100 * kr, frames[0].CumDumpWanM3, 6);
        Assert.Equal(400 * kr, frames[1].CumDumpWanM3, 6);
    }

    // ── 兜底与文案 ──────────────────────────────────────────
    [Fact]
    public void 兜底_空方案给空时间轴不抛()
    {
        Assert.Empty(LongTermSimTimeline.Build(null));
        Assert.Empty(LongTermSimTimeline.Build(new LongTermPlan()));
        Assert.Contains("先排产", LongTermSimTimeline.Summary(null, Array.Empty<LongTermSimFrame>()));
    }

    [Fact]
    public void 文案_没接台账时说未校核而不是够排()
    {
        // ★ 三分不是两分：没查 ≠ 查过没问题
        var p = Plan((100, 100, LongTermDumpMode.External));
        string s = LongTermSimTimeline.Summary(p, LongTermSimTimeline.Build(p, null));
        Assert.Contains("未做库容校核", s);
        Assert.DoesNotContain("库容够排", s);
    }

    [Fact]
    public void 文案_顶栏把年数与累计说清楚()
    {
        var p = Plan((100, 100, LongTermDumpMode.External), (100, 100, LongTermDumpMode.Internal));
        var frames = LongTermSimTimeline.Build(p);
        string s = LongTermSimTimeline.Summary(p, frames);
        Assert.Contains("2 年", s);
        Assert.Contains("2027", s);
        Assert.Contains("累计采出", s);
        Assert.Contains("内排自", s);
    }

    // ── 与真排产联动 ────────────────────────────────────────
    [Fact]
    public void 联动_真排产出来的方案推得出完整时间轴()
    {
        var p = new LongTermPlan();
        LongTermScheduler.Schedule(p);
        var frames = LongTermSimTimeline.Build(p, Sinks(("D1", "北排", SinkKind.ExternalDump, 200000)));
        Assert.Equal(p.Periods.Count, frames.Count);
        Assert.All(frames, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
        Assert.True(frames[^1].CumCoalWanT > 0);
        // 内排起转在逐年表里确实出现过(默认 InnerDumpEnabled=true)
        Assert.True(LongTermSimTimeline.InnerDumpStartIndex(frames) >= 0);
    }

    [Fact]
    public void 联动_库容给小了就一定报排不下()
    {
        var p = new LongTermPlan();
        LongTermScheduler.Schedule(p);
        var frames = LongTermSimTimeline.Build(p, Sinks(("D1", "小场", SinkKind.ExternalDump, 1)));
        Assert.True(LongTermSimTimeline.FirstSpillIndex(frames) >= 0);
    }
}
