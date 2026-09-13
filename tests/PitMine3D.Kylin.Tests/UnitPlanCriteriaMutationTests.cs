// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/UnitPlanCriteriaMutationTests.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// G30 组 · <b>判据的变异测试</b> —— 判据本身也要被判一次。
///
/// <para><b>为什么必须有这一组</b>：只判"成功"的判据会空过。一条永远返回空清单的判据
/// 和一条真的在判的判据，在正常算例上<b>一模一样都是绿的</b>。
/// 这里把排产结果<b>逐条故意改坏</b>，每一条判据都必须报红；报不红的那条就是摆设。</para>
///
/// <para>变异做在<b>结果对象</b>上而不是引擎源码里：结果的每个字段都是公开的，
/// 改一个字段等价于"引擎在那一步算错了"，而且不会把别人正在改的源码搅进来。
/// 引擎源码那一侧的端到端变异另行做过一次（见任务记录），此处留的是能长期跑的这一份。</para>
/// </summary>
public sealed class UnitPlanCriteriaMutationTests
{
    private readonly ITestOutputHelper _out;
    public UnitPlanCriteriaMutationTests(ITestOutputHelper o) => _out = o;

    private sealed class Case
    {
        public UnitPlanInput Inp = null!;
        public UnitGraph G = null!;
        public List<DumpSlot> Slots = null!;
        public UnitPlanResult R = null!;
    }

    /// <summary>一份干净的、全部判据都无异议的基准解 —— 变异从它开始。</summary>
    private Case Baseline()
    {
        var units = UnitPlanFixture.Stacked();
        var slots = UnitPlanFixture.Dump("外排1", levels: 3, capBase: 1.0e5, cx: 2500, cy: 700, baseZ: 1100);
        var inp = UnitPlanFixture.Input(units, slots, coalTargetT: 18e4, stripRatio: 5.0);
        inp.Graph = UnitPlanFixture.Graph(units);
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);
        var bad = UnitPlanCriteria.All(r, inp, inp.Graph, slots);
        Assert.True(bad.Count == 0, "基准解本身就有异议，变异测试无从谈起：\n" + string.Join("\n", bad));
        return new Case { Inp = inp, G = inp.Graph, Slots = slots, R = r };
    }

    private void MustBeRed(string mutation, List<string> bad)
    {
        _out.WriteLine($"【变异】{mutation} → {bad.Count} 条异议");
        foreach (var b in bad) _out.WriteLine("   ◆ " + b);
        Assert.True(bad.Count > 0, $"变异「{mutation}」之后判据仍然是绿的 —— 这条判据是摆设");
    }

    // ── 对账 ① 煤量 ──────────────────────────────────────────────────

    [Fact]
    public void 变异_煤量少一半却不报缺口()
    {
        var c = Baseline();
        c.R.CoalT *= 0.5;
        MustBeRed("煤量砍半、缺口仍为 0", UnitPlanCriteria.CoalVsTarget(c.R, c.Inp));
    }

    [Fact]
    public void 变异_缺口是编出来的()
    {
        var c = Baseline();
        c.R.ShortfallT = 1e4;                    // 煤量没变，凭空报一个缺口
        MustBeRed("煤量达标却报缺口", UnitPlanCriteria.CoalVsTarget(c.R, c.Inp));
    }

    [Fact]
    public void 变异_认了欠产但账补不平()
    {
        var c = Baseline();
        c.R.CoalT *= 0.5;
        c.R.ShortfallT = 1.0;                    // 认了，但补的量对不上目标
        MustBeRed("达成 + 缺口 ≠ 目标", UnitPlanCriteria.CoalVsTarget(c.R, c.Inp));
    }

    [Fact]
    public void 变异_判为可行但只采到一半()
    {
        var c = Baseline();
        c.R.CoalT *= 0.5;                        // 三种缺口都还是 0 ⇒ Feasible 仍为 true
        Assert.True(c.R.Feasible);
        MustBeRed("Feasible=true 而煤量只有一半", UnitPlanCriteria.FeasibilityHonest(c.R, c.Inp));
    }

    // ── 对账 ② 库容 ──────────────────────────────────────────────────

    [Fact]
    public void 变异_某个位置占容超库容()
    {
        var c = Baseline();
        var f = c.R.Rock.SelectMany(a => a.Flows).First(x => !x.IsCoalSink);
        f.DumpM3 *= 10;                          // 单个位置撑破，总量也跟着破
        MustBeRed("单个排土位置占容 ×10", UnitPlanCriteria.DumpCapacityRespected(c.R, c.Slots));
    }

    [Fact]
    public void 变异_去向码对不上任何位置()
    {
        var c = Baseline();
        c.R.Rock.SelectMany(a => a.Flows).First(x => !x.IsCoalSink).DestinationCode = "外排1-Lv0-0";
        MustBeRed("去向码编码漂了", UnitPlanCriteria.DumpCapacityRespected(c.R, c.Slots));
    }

    [Fact]
    public void 变异_跳过最低未满级往上排()
    {
        // 全新造一个结果：一笔岩流直接排到 Level 2，而 Level 0/1 一方都没收
        var slots = UnitPlanFixture.Dump("外排1", levels: 3, capBase: 1.0e5, cx: 2500, cy: 700, baseZ: 1100);
        var top = slots.First(s => s.Level == 2);
        var r = new UnitPlanResult { Success = true };
        var a = new UnitAssignment { UnitId = "岩1030-B1-P1", Kind = UnitKind.Rock, Seq = 1, InSituM3 = 1e4, Fraction = 1, DoneAfter = 1 };
        a.Flows.Add(new UnitFlow
        {
            DestinationCode = UnitPlanCriteria.SlotCode(top), DestinationName = top.DumpName,
            Dx = top.Cx, Dy = top.Cy, Dz = top.Cz,
            InSituM3 = 1e4, DumpM3 = 1.15e4, TonnageT = 2.5e4, HaulKm = 1, Density = 2.5, Kr = 1.15,
        });
        r.Assignments.Add(a);
        MustBeRed("越过 Level 0/1 直接排 Level 2", UnitPlanCriteria.DumpBottomUp(r, slots, 1));
    }

    // ── 对账 ③ 拓扑 ──────────────────────────────────────────────────

    [Fact]
    public void 变异_前驱没剥够就采下面()
    {
        var c = Baseline();
        // 挑一个"有前驱也在计划里"的单元，把前驱的完成度压下去
        var byId = c.R.Assignments.ToDictionary(x => x.UnitId, StringComparer.Ordinal);
        UnitAssignment? pred = null;
        foreach (var a in c.R.Assignments)
        {
            int i = c.G.IndexOf(a.UnitId);
            foreach (int p in c.G.Pred[i])
                if (byId.TryGetValue(c.G.Units[p].UnitId, out var pa) && pa.DoneAfter >= a.DoneAfter - 1e-9)
                { pred = pa; break; }
            if (pred != null) break;
        }
        Assert.NotNull(pred);
        pred!.DoneAfter = 0.01;
        MustBeRed($"把前驱 {pred.UnitId} 的完成度压到 1%", UnitPlanCriteria.TopologySound(c.R, c.G));
    }

    [Fact]
    public void 变异_月内推进序把前驱排到后面()
    {
        var c = Baseline();
        var byId = c.R.Assignments.ToDictionary(x => x.UnitId, StringComparer.Ordinal);
        UnitAssignment? lo = null, hi = null;
        foreach (var a in c.R.Assignments)
        {
            int i = c.G.IndexOf(a.UnitId);
            foreach (int p in c.G.Pred[i])
                if (byId.TryGetValue(c.G.Units[p].UnitId, out var pa)) { lo = a; hi = pa; break; }
            if (lo != null) break;
        }
        Assert.NotNull(lo); Assert.NotNull(hi);
        (lo!.Seq, hi!.Seq) = (hi.Seq, lo.Seq);          // 前驱与后继对调
        MustBeRed($"{lo.UnitId} 与前驱 {hi.UnitId} 的推进序对调", UnitPlanCriteria.TopologySound(c.R, c.G));
    }

    // ── 量的口径 ─────────────────────────────────────────────────────

    [Fact]
    public void 变异_逐单元合计与汇总对不上()
    {
        var c = Baseline();
        c.R.Coal.First().TonnageT *= 1.02;
        MustBeRed("某个煤单元吨量 +2%", UnitPlanCriteria.QuantitiesSound(c.R, c.Inp));
    }

    [Fact]
    public void 变异_占容不等于实方乘Kr()
    {
        var c = Baseline();
        c.R.Rock.SelectMany(a => a.Flows).First(x => !x.IsCoalSink).DumpM3 *= 1.001;
        MustBeRed("某一笔占容 ≠ 实方 × Kr", UnitPlanCriteria.QuantitiesSound(c.R, c.Inp));
    }

    [Fact]
    public void 变异_推进序出现重号()
    {
        var c = Baseline();
        c.R.Assignments[1].Seq = c.R.Assignments[0].Seq;
        MustBeRed("推进序重号", UnitPlanCriteria.QuantitiesSound(c.R, c.Inp));
    }

    [Fact]
    public void 变异_完成度越界()
    {
        var c = Baseline();
        c.R.Assignments[0].DoneAfter = 1.5;
        MustBeRed("完成度 150%", UnitPlanCriteria.QuantitiesSound(c.R, c.Inp));
    }

    [Fact]
    public void 变异_出现NaN()
    {
        var c = Baseline();
        c.R.Rock.SelectMany(a => a.Flows).First().HaulKm = double.NaN;
        MustBeRed("某一笔运距是 NaN", UnitPlanCriteria.QuantitiesSound(c.R, c.Inp));
    }

    [Fact]
    public void 变异_内排率用0冒充没有排土流()
    {
        var c = Baseline();
        var r = new UnitPlanResult { Success = true, InternalRatePct = -2 };
        MustBeRed("内排率 −2（既不是 −1 也不在 0~100）", UnitPlanCriteria.QuantitiesSound(r, c.Inp));
    }

    // ── 作业组织指标 ─────────────────────────────────────────────────

    [Fact]
    public void 变异_转场次数与排出来的顺序对不上()
    {
        var c = Baseline();
        c.R.MoveCount += 1;
        MustBeRed("转场次数 +1", UnitPlanCriteria.OrgMetricsMatchOrder(c.R));
    }

    [Fact]
    public void 变异_作业面数按集合数而不是按顺序数()
    {
        var c = Baseline();
        c.R.FaceCount += 2;
        MustBeRed("作业面数 +2", UnitPlanCriteria.OrgMetricsMatchOrder(c.R));
    }

    [Fact]
    public void 变异_卸点数对不上()
    {
        var c = Baseline();
        c.R.DumpSlotCount = 0;
        MustBeRed("卸点数报 0", UnitPlanCriteria.OrgMetricsMatchOrder(c.R));
    }

    // ── U1 ───────────────────────────────────────────────────────────

    [Fact]
    public void 变异_一个月切了两个煤幅()
    {
        var c = Baseline();
        foreach (var a in c.R.Coal.Take(2)) a.DoneAfter = 0.5;
        MustBeRed("两个跨月煤幅", UnitPlanCriteria.AtMostOnePartialCoal(c.R));
    }

    [Fact]
    public void 变异_遗留幅被漏掉()
    {
        var units = UnitPlanFixture.Stacked(carryOver: 3);
        var slots = UnitPlanFixture.Dump("外排1", 4, 1.5e5, 2500, 700, 1100);
        var inp = UnitPlanFixture.Input(units, slots, 25e4, 3.0);
        inp.Graph = UnitPlanFixture.Graph(units);
        var r = UnitPlanEngine.Solve(inp);
        Assert.Empty(UnitPlanCriteria.CarryOverFirst(r, units));

        // 把一个遗留幅从计划里抹掉（= 实现漏排了它），其余不动
        var drop = r.Assignments.First(a => units.Any(u => u.UnitId == a.UnitId && u.IsCarryOver));
        r.Assignments.Remove(drop);
        MustBeRed($"把遗留幅 {drop.UnitId} 从计划里抹掉", UnitPlanCriteria.CarryOverFirst(r, units));
    }

    // ── 不静默 ───────────────────────────────────────────────────────

    [Fact]
    public void 变异_默认值没有留条()
    {
        var c = Baseline();
        c.Inp.CoalSinks = new List<CoalSink>();
        var r = UnitPlanEngine.Solve(c.Inp);
        Assert.Empty(UnitPlanCriteria.NotSilent(r, c.Inp));      // 引擎本来是留了条的
        r.Notes.Clear();                                          // 把条抹掉 = 静默降级
        MustBeRed("没有出矿位置却不留条", UnitPlanCriteria.NotSilent(r, c.Inp));
    }

    [Fact]
    public void 变异_失败却不给原因()
    {
        var r = new UnitPlanResult { Success = false, Error = "" };
        MustBeRed("Success=false 且 Error 为空", UnitPlanCriteria.FeasibilityHonest(r, new UnitPlanInput()));
    }

    // ── 对账 ①b 排弃量（U13）—— 这一组判的是 StripVsTarget 会不会红 ────────
    //
    //  ⚠ 基准必须走【给定排弃量】那条路线：拿剥采比那条路线当基准的话，
    //    `inp.StripTargetM3 > 0` 那半边判据一次都走不到，四条变异全在空过。

    /// <summary>走【给定排弃量】路线的干净基准解。</summary>
    private Case StripTargetBaseline()
    {
        var units = UnitPlanFixture.OneCoalLeft();
        var slots = UnitPlanFixture.Dump("外排1", levels: 4, capBase: 3e5, cx: 6000, cy: 500, baseZ: 1000);
        var inp = UnitPlanFixture.Input(units, slots, coalTargetT: 5e4 * UnitPlanFixture.CoalRho,
                                        stripRatio: 0, stripTargetM3: 5e5);
        inp.Graph = UnitPlanFixture.Graph(units);
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);
        Assert.True(r.StripTargetM3 > 1e-9 && r.ShortStripM3 <= 1e-6, "基准解没走到给定排弃量这条路线");
        var bad = UnitPlanCriteria.All(r, inp, inp.Graph, slots);
        Assert.True(bad.Count == 0, "基准解本身就有异议：\n" + string.Join("\n", bad));
        return new Case { Inp = inp, G = inp.Graph, Slots = slots, R = r };
    }

    [Fact]
    public void 变异_剥离量少一半却不报剥不够()
    {
        var c = StripTargetBaseline();
        c.R.StripM3 *= 0.5;
        MustBeRed("剥离量砍半、剥不够仍为 0", UnitPlanCriteria.StripVsTarget(c.R, c.Inp));
    }

    [Fact]
    public void 变异_剥不够是编出来的()
    {
        var c = StripTargetBaseline();
        c.R.ShortStripM3 = 5e4;                  // 剥离量没变，凭空报一个缺口
        MustBeRed("剥离量达标却报剥不够", UnitPlanCriteria.StripVsTarget(c.R, c.Inp));
    }

    [Fact]
    public void 变异_有效目标被悄悄换掉()
    {
        // ★ U13.3 的正面靶子：目标被"比 × 实际煤量"改写一遍时，
        //   达成量与目标仍然自洽 —— 两个数一起错，只有拿【输入里那个绝对量】比才看得出来。
        var c = StripTargetBaseline();
        double fake = c.R.StripM3 * 0.8;
        c.R.StripTargetM3 = fake;
        c.R.StripM3 = fake;                      // 连达成量一起改，账面照样是平的
        MustBeRed("目标与达成一起被缩了 20%", UnitPlanCriteria.StripVsTarget(c.R, c.Inp));
    }

    [Fact]
    public void 变异_没卡车队能力却说被车队闸削过()
    {
        var c = StripTargetBaseline();
        Assert.True(c.Inp.FleetCapTKm <= 0, "基准解不该卡着车队能力");
        c.R.TrimmedByFleetM3 = 3e4;
        MustBeRed("车队闸没启用却报了削减量", UnitPlanCriteria.StripVsTarget(c.R, c.Inp));
    }

    [Fact]
    public void 变异_有效目标没说来源()
    {
        var c = StripTargetBaseline();
        c.R.StripTargetNote = "";                // 目标从哪来的说不出 = 引擎替用户定了却不讲
        MustBeRed("有效目标没有来源说明", UnitPlanCriteria.StripVsTarget(c.R, c.Inp));
    }
}
