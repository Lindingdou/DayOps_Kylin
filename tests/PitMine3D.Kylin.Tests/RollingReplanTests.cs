// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/RollingReplanTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Tests.Synth;
using BlockModel = PitMine3D.Kylin.Tests.Synth.BlockModel;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
using MonthPeriod = PitMine3D.Kylin.Cad.Plan.MonthPeriod;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// G37 组 · <b>滚动重排</b>（拿实绩当期初，重排剩下的月份）。
///
/// <para>短期计划在现场是滚动的：你在第 5 个月，1–4 月已是既成事实，要重排的是 5–16 月。
/// 一次性排满 12 个月然后照着走，只在教科书里成立。</para>
///
/// <para>判三件事：<b>实绩真的当了期初</b>（不是拿计划冒充）、<b>偏差账算得对</b>
/// （采出与剥离必须分开看）、<b>欠剥要被压进后续月份</b>而不是消失。</para>
/// </summary>
public sealed class RollingReplanTests
{
    private readonly ITestOutputHelper _out;
    public RollingReplanTests(ITestOutputHelper o) => _out = o;

    // ⚠ 与 `MonthlyStripSessionTests` 同一个坑：NX=60 × 10m ⇒ 推进方向只有 600m，
    //   而工作帮超前 (K−1)×H/tanα ≈ 385m ⇒ 计划后半程最上台阶出界，「必须剥」会自己往下掉。
    //   本组判的是**欠账要压进后续月份 / 达成度分采剥 / 期初来源要诚实**，都不依赖尾部绝对量，
    //   所以照旧有效；但别在这个夹具上加"第 N 月该剥多少"这类判据。详见那边的注释。
    private const int NX = 60, NY = 5, NZ = 24;
    private const double CELL = 10, ROCK_H = 20, DENS = 1.35, ALPHA = 20, ZDATUM = 140, DIP = 0.06;

    private static TinSampler DipPlane(double z0)
    {
        double h = 6000;
        double Z(double x) => z0 - DIP * x;
        return TinSampler.TryBuild(new[] { -h, -h, Z(-h), h, -h, Z(h), h, h, Z(h), -h, h, Z(-h) },
                                   new[] { 0, 1, 2, 0, 2, 3 })!;
    }

    private static RockProfile Profile()
    {
        var spec = new BlockModelSpec
        { Origin = new Vec3d(0, 0, 0), BlockSize = new Vec3d(CELL, CELL, CELL), Dimensions = new Vec3i(NX, NY, NZ) };
        var m = new BlockModel { Name = "RR", Spec = spec };
        var cd = m.EnsureCellData();
        cd.SetConstant("cA", 0); cd.SetConstant("cB", 0);
        long nxy = (long)NX * NY;
        double aR = 200, bR = 110, th = 20;
        for (int k = 0; k < NZ; k++)
        {
            double cz = k * CELL + CELL * 0.5;
            for (int i = 0; i < NX; i++)
            {
                double cx = i * CELL + CELL * 0.5;
                string? col = (cz <= aR - DIP * cx && cz >= aR - DIP * cx - th) ? "cA"
                            : (cz <= bR - DIP * cx && cz >= bR - DIP * cx - th) ? "cB" : null;
                if (col == null) continue;
                for (int jy = 0; jy < NY; jy++) cd.SetCell(col, k * nxy + (long)jy * NX + i, 1.0);
            }
        }
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((-1500, -100, ZDATUM)); wl.Baseline.Add((-1500, 200, ZDATUM));
        wl.Samples.Add((-1500, 50, ZDATUM, 1, 0));
        var seams = new List<SeamSurfaces>
        {
            new() { Name = "A煤", Attribute = "cA", Density = DENS, Roof = DipPlane(aR), Floor = DipPlane(aR - th) },
            new() { Name = "B煤", Attribute = "cB", Density = DENS, Roof = DipPlane(bR), Floor = DipPlane(bR - th) },
        };
        var p = InclineVolumeEngine.BuildProfile(m, new[] { wl }, DipPlane(3000), seams, ALPHA, 1, 0.5, ROCK_H);
        Assert.True(p.Success, p.Error);
        return p.Rock!;
    }

    private static MonthlyScheduleInput Base(RockProfile rock, int months = 12)
    {
        var q = new double[months];
        for (int i = 0; i < months; i++) q[i] = rock.TotalCoalWt() * 0.5 / months;
        return new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = q[0] * 3, StartInSteadyState = true,
        };
    }

    // ── G37 · 滚动重排跑得通 ────────────────────────────────────────────────

    /// <summary>
    /// G37 <b>照计划走时，重排出来的剩余月份与原计划一致</b>。
    /// <para>这是滚动重排的基线：实绩 = 计划 ⇒ 重排不该改变任何东西。
    /// 变了就说明「实绩 → 期初」这一跳丢了信息。</para>
    /// </summary>
    [Fact]
    public void G37_OnPlanActuals_ReproduceTheRemainingPlan()
    {
        var rock = Profile();
        var baseIn = Base(rock);
        var plan0 = MonthlyMineScheduler.Solve(baseIn);
        Assert.True(plan0.Success, plan0.Error);

        int k = 4;
        var actual = RollingReplan.SimulateActual(plan0, k);   // 完全照计划走
        var rep = RollingReplan.Run(baseIn, plan0, actual);
        Assert.True(rep.Success, rep.Error);
        _out.WriteLine(rep.Summary());
        foreach (var n in rep.Notes) _out.WriteLine("  " + n);

        Assert.Equal(plan0.Months.Count - k, rep.Plan!.Months.Count);
        Assert.Equal(100.0, rep.CoalAttainPct, 1);
        Assert.Equal(100.0, rep.RockAttainPct, 1);
        Assert.Equal(0, rep.StripDebtM3, 3);

        // 剩余月份的煤量与原计划同期一致
        for (int t = 0; t < rep.Plan.Months.Count; t++)
            Assert.Equal(plan0.Months[k + t].CoalWt, rep.Plan.Months[t].CoalWt, 3);
        // 台阶位置也应当接得上（期初 = 第 k 月末的位置）
        for (int i = 0; i < rep.Plan.Levels.Length; i++)
            Assert.True(rep.Plan.Months[0].BenchX[i] >= plan0.Months[k - 1].BenchX[i] - 1e-6,
                        $"格{rep.Plan.Levels[i]} 的重排起点跑到实绩位置后方了");
        _out.WriteLine($"原计划剩余 {plan0.Months.Count - k} 月岩 "
                     + $"{(plan0.TotalRockM3 - plan0.Months[k - 1].RockCumM3) / 1e4:0.0}万m³ · "
                     + $"重排 {rep.Plan.TotalRockM3 / 1e4:0.0}万m³");
    }

    /// <summary>
    /// G37b <b>欠剥要被压进后续月份，不许消失</b>。
    /// <para>工程含义：<b>欠剥比欠采危险</b> —— 欠采是这个月少卖点煤，欠剥是下个月没煤可采。
    /// 重排必须把欠账背上，而不是"翻篇重来"。</para>
    /// </summary>
    [Fact]
    public void G37b_StripDebt_IsCarriedIntoRemainingMonths()
    {
        var rock = Profile();
        var baseIn = Base(rock);
        var plan0 = MonthlyMineScheduler.Solve(baseIn);
        Assert.True(plan0.Success, plan0.Error);
        int k = 4;

        var onPlan = RollingReplan.Run(baseIn, plan0, RollingReplan.SimulateActual(plan0, k));
        // 剥离只干了 75%：台阶位置也退回去（欠剥的直接后果就是台阶没推到位）
        var behind = RollingReplan.SimulateActual(plan0, k, coalRate: 1.0, rockRate: 0.75);
        var bx = plan0.Months[k - 1].BenchX;
        var prev = plan0.Months[Math.Max(0, k - 2)].BenchX;
        behind.BenchX = bx.Select((v, i) => prev[i] + (v - prev[i]) * 0.5).ToArray();   // 少推一半
        var late = RollingReplan.Run(baseIn, plan0, behind);

        Assert.True(onPlan.Success && late.Success, onPlan.Error + late.Error);
        _out.WriteLine("照计划: " + onPlan.Summary());
        _out.WriteLine("欠剥:   " + late.Summary());
        foreach (var n in late.Notes) _out.WriteLine("  " + n);

        Assert.True(late.StripDebtM3 > 0, "剥离只干了 75% 却没算出欠账");
        Assert.Contains(late.Notes, n => n.Contains("欠剥比欠采危险"));
        // 欠的活要在剩余月份补回来 ⇒ 重排的剥离总量必须更大
        Assert.True(late.Plan!.TotalRockM3 > onPlan.Plan!.TotalRockM3 + 1e-6,
                    $"欠剥后重排 {late.Plan.TotalRockM3 / 1e4:0.0}万m³ 不比照计划的 "
                  + $"{onPlan.Plan.TotalRockM3 / 1e4:0.0}万m³ 多 —— 欠账被翻篇了");
        _out.WriteLine($"欠账 {late.StripDebtM3 / 1e4:0.0}万m³ ⇒ 重排剥离 "
                     + $"{onPlan.Plan.TotalRockM3 / 1e4:0.0} → {late.Plan.TotalRockM3 / 1e4:0.0}万m³");
    }

    /// <summary>
    /// G37c <b>采出与剥离的达成度必须分开看</b>，不许合成一个"完成率"。
    /// </summary>
    [Fact]
    public void G37c_CoalAndStripAttainment_AreReportedSeparately()
    {
        var rock = Profile();
        var baseIn = Base(rock);
        var plan0 = MonthlyMineScheduler.Solve(baseIn);
        int k = 5;
        // 煤超产、岩欠剥 —— 最危险的组合：账面"产量很好"，实际下个月要断煤
        var a = RollingReplan.SimulateActual(plan0, k, coalRate: 1.08, rockRate: 0.70);
        var rep = RollingReplan.Run(baseIn, plan0, a);
        Assert.True(rep.Success, rep.Error);
        _out.WriteLine(rep.Summary());
        foreach (var n in rep.Notes) _out.WriteLine("  " + n);

        Assert.True(rep.CoalAttainPct > 105, "煤超产没体现出来");
        Assert.True(rep.RockAttainPct < 75, "岩欠剥没体现出来");
        Assert.True(rep.StripDebtM3 > 0);
        Assert.Contains(rep.Notes, n => n.Contains("采出达成") && n.Contains("偏离超过 5%"));
    }

    // ── G38 · 期初来源必须诚实 ──────────────────────────────────────────────

    /// <summary>
    /// G38 <b>实绩没给台阶位置时要明说"这等于假设照计划走"</b>。
    /// <para>工程含义：拿计划当实绩，偏差会一期一期累积，而且每次重排都显示"一切正常"。
    /// 这是滚动重排最容易出的错，必须在结果里看得见。</para>
    /// </summary>
    [Fact]
    public void G38_MissingActualBenchPositions_IsDeclaredNotAssumed()
    {
        var rock = Profile();
        var baseIn = Base(rock);
        var plan0 = MonthlyMineScheduler.Solve(baseIn);

        var noPos = RollingReplan.SimulateActual(plan0, 4);
        noPos.BenchX = Array.Empty<double>();                  // 只给了量，没给位置
        var rep = RollingReplan.Run(baseIn, plan0, noPos);
        Assert.True(rep.Success, rep.Error);
        foreach (var n in rep.Notes) _out.WriteLine("  " + n);
        Assert.Contains(rep.Notes, n => n.Contains("完全照计划走"));
        Assert.Contains(rep.Notes, n => n.Contains("现状面反算"));

        // 给了位置就不该有这条
        var withPos = RollingReplan.Run(baseIn, plan0, RollingReplan.SimulateActual(plan0, 4));
        Assert.DoesNotContain(withPos.Notes, n => n.Contains("完全照计划走"));
    }

    /// <summary>
    /// G38b 排土容量那条<b>累计</b>上限按原月轴算，重排后必须清空并说明 —— 否则会拿旧月轴的数卡新计划。
    /// </summary>
    [Fact]
    public void G38b_CumulativeCapFromOldTimeline_IsClearedAndDeclared()
    {
        var rock = Profile();
        var baseIn = Base(rock);
        // 宽松但非空 —— 这条判的是「重排后要清空」，不是能力紧不紧
        baseIn.ExtraCumCapM3 = Enumerable.Range(0, 13).Select(i => i * 1e7).ToArray();
        var plan0 = MonthlyMineScheduler.Solve(baseIn);
        Assert.True(plan0.Success, plan0.Error);

        var (inp, rep) = RollingReplan.Prepare(baseIn, plan0, RollingReplan.SimulateActual(plan0, 4));
        Assert.Empty(inp.ExtraCumCapM3);
        Assert.Contains(rep.Notes, n => n.Contains("外部累计上限") && n.Contains("清空"));
        _out.WriteLine(string.Join("\n  ", rep.Notes));
    }

    /// <summary>G38c 已完成月数超出计划长度时明确报错，不许排出个空计划。</summary>
    [Fact]
    public void G38c_ElapsedBeyondHorizon_FailsClearly()
    {
        var rock = Profile();
        var baseIn = Base(rock, months: 6);
        var plan0 = MonthlyMineScheduler.Solve(baseIn);
        var rep = RollingReplan.Run(baseIn, plan0, new ActualToDate { MonthsElapsed = 9, CoalWt = 10 });
        Assert.False(rep.Success);
        _out.WriteLine(rep.Error);
        Assert.Contains("没有可重排的月份", rep.Error);
    }

    /// <summary>面板：连续三次滚动重排，看偏差怎么被吸收。</summary>
    [Fact]
    public void G_RollingDashboard()
    {
        var rock = Profile();
        var baseIn = Base(rock);
        var plan = MonthlyMineScheduler.Solve(baseIn);
        Assert.True(plan.Success, plan.Error);
        _out.WriteLine($"期初计划：煤 {plan.TotalCoalWt:0.0}万t · 岩 {plan.TotalRockM3 / 1e4:0.0}万m³ · CV {plan.RatioCv:0.000}");

        var cur = baseIn; var curPlan = plan;
        var rates = new[] { (1.02, 0.88), (0.97, 0.95), (1.00, 1.05) };
        for (int round = 0; round < rates.Length; round++)
        {
            var (cr, rr) = rates[round];
            var act = RollingReplan.SimulateActual(curPlan, 3, cr, rr);
            var rep = RollingReplan.Run(cur, curPlan, act);
            if (!rep.Success) { _out.WriteLine($"第{round + 1}次重排失败：{rep.Error}"); break; }
            _out.WriteLine($"第{round + 1}次 " + rep.Summary());
            var (nextIn, _) = RollingReplan.Prepare(cur, curPlan, act);
            cur = nextIn; curPlan = rep.Plan!;
        }
    }
}
