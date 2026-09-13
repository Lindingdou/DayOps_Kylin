// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/SourceGranularityTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
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
/// G40 · <b>采排配对的源粒度</b>：台阶×层间 vs 作业面 —— 把这条待核准口径从"我的判断"变成<b>可复算的证据</b>。
///
/// <para>验收册里那条待核准项写的是：<i>「我的判断是必须下沉 —— 不是精度问题，
/// 是『表土只能进表土堆场』这类硬约束在聚合后**数学上不成立**」</i>。
/// 但那一直只是一句话。这条判据把两种粒度<b>并排跑同一批量</b>，让差别自己显出来。</para>
///
/// <para><b>算例</b>：一个标高格里同时有【表土】和【岩】两个层间；
/// 表土有硬约束「只能进表土堆场」，而表土堆场的库容<b>只够装表土那部分</b>。
/// <list type="bullet">
///   <item><b>台阶×层间</b>（现状实现）：表土进表土堆场、岩进排土场，两边都排得下。</item>
///   <item><b>作业面</b>（把两个层间聚合成一种"平均物料"）：无论平均后算哪一种，
///     都得出错误结论 —— 要么<b>违约</b>（把岩塞进表土堆场），要么<b>假报排不下</b>。</item>
/// </list></para>
///
/// <para><b>这条判据不主张任何设计</b>，它只把两种粒度的实际后果摆出来。要改口径的话，
/// 先让这条红，再改 —— 它就是那条口径的可执行定义。</para>
/// </summary>
public sealed class SourceGranularityTests
{
    private readonly ITestOutputHelper _out;
    public SourceGranularityTests(ITestOutputHelper o) => _out = o;

    private const string TopsoilDump = "表土堆场";
    private const string WasteDump = "排土场";

    /// <summary>表土 30 万m³实方 · 岩 70 万m³实方（同一个月、同一个标高格）。</summary>
    private const double TopsoilM3 = 30e4, RockM3 = 70e4;
    private const double KrTop = 1.25, KrRock = 1.15;

    /// <summary>去向：表土堆场只够装表土的占容；排土场只够装岩的占容。都不留富余。</summary>
    private static List<DumpSlot> Slots() => new()
    {
        new DumpSlot { DumpName = TopsoilDump, Level = 0, Order = 0,
                       CapacityM3 = TopsoilM3 * KrTop, IsInternal = false, AvailableFromMonth = 1, HaulKm = 2.0 },
        new DumpSlot { DumpName = WasteDump,   Level = 0, Order = 0,
                       CapacityM3 = RockM3 * KrRock,   IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.0 },
    };

    /// <summary>直接造一份单月排产结果 —— 这条判据只关心配对，不需要真几何。</summary>
    private static MonthlyScheduleResult OneMonth(double[] byGap)
    {
        var r = new MonthlyScheduleResult { Success = true, Levels = new[] { 0 } };
        r.Months.Add(new MonthRow
        {
            Month = 1,
            CoalWt = 10, CoalCumWt = 10,
            RockM3 = byGap.Sum(), RockCumM3 = byGap.Sum(),
            RockByGapM3 = byGap,
            BenchX = new double[] { 100 },
        });
        r.TotalCoalWt = 10; r.TotalRockM3 = byGap.Sum();
        return r;
    }

    [Fact]
    public void G40_SourceGranularity_AggregatingToWorkFace_BreaksTheHardConstraint()
    {
        // ── ① 台阶×层间：两个层间各自带自己的物料与硬约束 ──
        var fine = OneMonth(new[] { TopsoilM3, RockM3 });          // g0=表土, g1=岩
        var fineMats = new[]
        {
            new GapMaterial { Name = "表土", Code = "topsoil", Density = 1.8, Kr = KrTop,
                              AllowedDumps = new[] { TopsoilDump } },      // ★ 硬约束
            new GapMaterial { Name = "岩",   Code = "rock",    Density = 2.5, Kr = KrRock },
        };
        var a = DumpAllocator.Allocate(fine, new DumpAllocationInput { Slots = Slots(), Materials = fineMats });
        Assert.True(a.Success, a.Error);

        _out.WriteLine("【台阶×层间】");
        foreach (var f in a.Months.SelectMany(m => m.Flows))
            _out.WriteLine($"  {f.MaterialName,-4} {f.InSituM3 / 1e4,6:0.0}万m³实方 → {f.DumpName}");
        _out.WriteLine($"  排不下 {a.TotalUnplacedM3 / 1e4:0.0}万m³");
        foreach (var w in a.Warnings) _out.WriteLine("  " + w);

        // 全排得下，且表土一方也没进错去向
        Assert.Equal(0, a.TotalUnplacedM3, 3);
        Assert.All(a.Months.SelectMany(m => m.Flows).Where(f => f.MaterialName == "表土"), f => Assert.Equal(TopsoilDump, f.DumpName));
        double topsoilPlaced = a.Months.SelectMany(m => m.Flows).Where(f => f.MaterialName == "表土").Sum(f => f.InSituM3);
        Assert.Equal(TopsoilM3, topsoilPlaced, 0);

        // ── ② 作业面：两个层间聚合成一种"平均物料" ──
        //    聚合之后只剩一个物料码，硬约束只有两种填法，两种都是错的：
        double total = TopsoilM3 + RockM3;
        double krAvg = (TopsoilM3 * KrTop + RockM3 * KrRock) / total;   // 体积加权平均 Kr

        // ②a 平均物料**继承**表土的硬约束（"只能进表土堆场"）
        var coarseA = OneMonth(new[] { total });
        var matA = new[]
        {
            new GapMaterial { Name = "作业面平均料", Code = "mixed", Density = 2.29, Kr = krAvg,
                              AllowedDumps = new[] { TopsoilDump } },
        };
        var rA = DumpAllocator.Allocate(coarseA, new DumpAllocationInput { Slots = Slots(), Materials = matA });
        Assert.True(rA.Success, rA.Error);
        _out.WriteLine("\n【作业面 · 平均料继承表土约束】");
        foreach (var f in rA.Months.SelectMany(m => m.Flows)) _out.WriteLine($"  {f.InSituM3 / 1e4,6:0.0}万m³ → {f.DumpName}");
        _out.WriteLine($"  排不下 {rA.TotalUnplacedM3 / 1e4:0.0}万m³　← 排土场空着，却报排不下");

        // ②b 平均物料**丢掉**硬约束（不限去向）
        var coarseB = OneMonth(new[] { total });
        var matB = new[]
        {
            new GapMaterial { Name = "作业面平均料", Code = "mixed", Density = 2.29, Kr = krAvg },
        };
        var rB = DumpAllocator.Allocate(coarseB, new DumpAllocationInput { Slots = Slots(), Materials = matB });
        Assert.True(rB.Success, rB.Error);
        double intoTopsoil = rB.Months.SelectMany(m => m.Flows).Where(f => f.DumpName == TopsoilDump).Sum(f => f.InSituM3);
        _out.WriteLine("\n【作业面 · 平均料丢掉约束】");
        foreach (var f in rB.Months.SelectMany(m => m.Flows)) _out.WriteLine($"  {f.InSituM3 / 1e4,6:0.0}万m³ → {f.DumpName}");
        _out.WriteLine($"  进了表土堆场 {intoTopsoil / 1e4:0.0}万m³，其中**非表土** {(intoTopsoil - TopsoilM3) / 1e4:0.0}万m³");

        // ── 结论：两种填法各错各的，而台阶×层间两条都对 ──
        Assert.True(rA.TotalUnplacedM3 > 1e-6,
            "②a 应当【假报排不下】：平均料继承表土约束后，岩也被逼进表土堆场，装不下");
        Assert.True(intoTopsoil > TopsoilM3 + 1e-6,
            "②b 应当【违约】：丢掉约束后有非表土物料进了表土堆场");

        _out.WriteLine("\n──────── 结论 ────────");
        _out.WriteLine($"台阶×层间：排不下 0，表土 {topsoilPlaced / 1e4:0.0}万m³ 全部且仅有表土进表土堆场 ✓");
        _out.WriteLine($"作业面·继承约束：假报排不下 {rA.TotalUnplacedM3 / 1e4:0.0}万m³（排土场是空的）✗");
        _out.WriteLine($"作业面·丢掉约束：{(intoTopsoil - TopsoilM3) / 1e4:0.0}万m³ 非表土进了表土堆场 ✗");
        _out.WriteLine("\n聚合到作业面之后，「表土只能进表土堆场」**没有任何一种填法能同时不违约、不误报** ——");
        _out.WriteLine("这不是精度损失，是那条硬约束在聚合后失去了可表达性。");
    }
}

/// <summary>
/// G41 · <b>备采保有月数 N 的代价表</b> —— 把待核准项 1 从"核准这个数"变成"看这张表挑一个"。
///
/// <para>N 不是几何量、也不是可以从设计参数推出来的量，它是一条<b>风险偏好</b>：
/// 想备着几个月的煤。所以它没法像吸附半径那样自推。<b>但它的代价可以算</b>。</para>
///
/// <para><b>这张表要纠正一个直觉错误</b>（我自己在 S9b 上踩过）：
/// 「N 越大越保险，代价是多剥岩」——<b>在稳态起算下正好相反</b>。
/// N 越大，越多超前量已经建在<b>期初工作帮姿态</b>里、划进 <c>BoxCutM3</c>（基建剥离＝历史），
/// <b>本期反而剥得少</b>。真正的代价是<b>前期一次性投入</b>，不是本期工程量。</para>
/// </summary>
public sealed class LookaheadCostTests
{
    private readonly ITestOutputHelper _out;
    public LookaheadCostTests(ITestOutputHelper o) => _out = o;

    private const int NX = 60, NY = 5, NZ = 20;
    private const double CELL = 10.0, ROCK_H = 20.0, DENS = 1.35;
    private const double A_ROOF = 140, A_FLOOR = 120, B_ROOF = 80, B_FLOOR = 60;
    private const double ALPHA = 20.0, ZDATUM = 100.0;

    private static TinSampler Plane(double z)
        => TinSampler.TryBuild(
            new[] { -5000.0, -5000.0, z, 5000.0, -5000.0, z, 5000.0, 5000.0, z, -5000.0, 5000.0, z },
            new[] { 0, 1, 2, 0, 2, 3 })!;

    private static RockProfile Profile()
    {
        var spec = new BlockModelSpec
        {
            Origin = new Vec3d(0, 0, 0), BlockSize = new Vec3d(CELL, CELL, CELL),
            Dimensions = new Vec3i(NX, NY, NZ),
        };
        var m = new BlockModel { Name = "SYNTH", Spec = spec };
        var cd = m.EnsureCellData();
        cd.SetConstant("cA", 0); cd.SetConstant("cB", 0);
        long nxy = (long)NX * NY;
        for (int k = 0; k < NZ; k++)
        {
            double cz = k * CELL + CELL * 0.5;
            string? col = (cz >= A_FLOOR && cz <= A_ROOF) ? "cA" : (cz >= B_FLOOR && cz <= B_ROOF) ? "cB" : null;
            if (col == null) continue;
            for (long c = 0; c < nxy; c++) cd.SetCell(col, k * nxy + c, 1.0);
        }
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((-2000, -100, ZDATUM)); wl.Baseline.Add((-2000, 200, ZDATUM));
        wl.Samples.Add((-2000, 50, ZDATUM, 1, 0));
        var seams = new List<SeamSurfaces>
        {
            new() { Name = "A煤", Attribute = "cA", Density = DENS, Roof = Plane(A_ROOF), Floor = Plane(A_FLOOR) },
            new() { Name = "B煤", Attribute = "cB", Density = DENS, Roof = Plane(B_ROOF), Floor = Plane(B_FLOOR) },
        };
        var p = InclineVolumeEngine.BuildProfile(m, new[] { wl }, Plane(1000), seams, ALPHA, 1, 0.5, ROCK_H);
        Assert.True(p.Success, p.Error);
        return p.Rock!;
    }

    [Fact]
    public void G41_Lookahead_CostTable_ShowsWhatEachNBuysAndCosts()
    {
        var rock = Profile();
        const int T = 12;
        const double perMonth = 8.0;

        _out.WriteLine("N 的代价表（稳态起算，月煤 8 万t × 12 月）");
        _out.WriteLine($"{"N",-4}{"最低备采保有(月)",18}{"本期剥离(万m³)",16}{"基建剥离(万m³)",16}"
                     + $"{"合计投入(万m³)",16}{"剥采比变异",12}");
        _out.WriteLine(new string('─', 84));

        var rows = new List<(int N, double Prep, double Strip, double Box, double Cv)>();
        for (int n = 1; n <= 5; n++)
        {
            var s = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
            {
                Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM,
                CoalTargetWt = Enumerable.Repeat(perMonth, T).ToArray(),
                LookaheadMonths = n,
                RecoveryTotalWt = perMonth * n,      // R41：回采煤量要跟着 N 走
                StartInSteadyState = true, Pace = StripPace.Level,
            });
            if (!s.Success) { _out.WriteLine($"{n,-4}解不出来：{s.Error}"); continue; }
            double prep = s.Months.Min(m => m.PreparedMonths);
            double strip = s.TotalRockM3 / 1e4;
            double box = s.BoxCutM3 / 1e4;
            rows.Add((n, prep, strip, box, s.RatioCv));
            _out.WriteLine($"{n,-4}{prep,18:0.00}{strip,16:0.0}{box,16:0.0}{strip + box,16:0.0}{s.RatioCv,12:0.000}");
        }
        Assert.True(rows.Count >= 3, "解出来的 N 太少，排不成表");

        // ① N 越大，备采保有越多 —— 这是买到的东西
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i].Prep > rows[i - 1].Prep - 1e-6,
                $"N={rows[i].N} 的备采保有 {rows[i].Prep:0.00} 不比 N={rows[i - 1].N} 的 {rows[i - 1].Prep:0.00} 多");

        // ② **本期剥离随 N 递减**（稳态下超前量转进期初姿态）—— 这是最反直觉的一格
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i].Strip < rows[i - 1].Strip + 1e-6,
                $"N={rows[i].N} 本期剥离 {rows[i].Strip:0.0} 不比 N={rows[i - 1].N} 的 {rows[i - 1].Strip:0.0} 少"
              + " —— 稳态起算下超前量应当转进期初姿态");

        // ③ 基建剥离随 N 递增 —— 这才是 N 的真实代价
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i].Box > rows[i - 1].Box - 1e-6,
                $"N={rows[i].N} 基建剥离 {rows[i].Box:0.0} 不比 N={rows[i - 1].N} 的 {rows[i - 1].Box:0.0} 多");

        var lo = rows.First(); var hi = rows.Last();

        // ★ 三条逐点单调只保证"不倒退"，**全都相等也能通过** —— 那时 N 根本没接上，
        //   而表看上去完全正常。所以端点必须**严格**分开（同 G42 上自伤过的那条教训）。
        Assert.True(hi.Prep > lo.Prep * 1.1,
            $"N={lo.N}→{hi.N} 备采保有只从 {lo.Prep:0.00} 变到 {hi.Prep:0.00} —— N 没真正起作用");
        Assert.True(hi.Strip < lo.Strip * 0.95,
            $"N={lo.N}→{hi.N} 本期剥离只从 {lo.Strip:0.0} 变到 {hi.Strip:0.0} —— 超前量没转进期初姿态");
        Assert.True(hi.Box > lo.Box * 1.1,
            $"N={lo.N}→{hi.N} 基建剥离只从 {lo.Box:0.0} 变到 {hi.Box:0.0} —— N 的代价没体现出来");

        _out.WriteLine(new string('─', 84));
        _out.WriteLine($"N: {lo.N} → {hi.N}　备采保有 {lo.Prep:0.00} → {hi.Prep:0.00} 月（**买到的**）");
        _out.WriteLine($"　　　　　本期剥离 {lo.Strip:0.0} → {hi.Strip:0.0} 万m³（**反而少**：超前量转进期初姿态）");
        _out.WriteLine($"　　　　　基建剥离 {lo.Box:0.0} → {hi.Box:0.0} 万m³（**这才是代价**：前期一次性投入）");
        _out.WriteLine($"　　　　　合计投入 {lo.Strip + lo.Box:0.0} → {hi.Strip + hi.Box:0.0} 万m³");
        _out.WriteLine("\nN 是风险偏好不是几何量，推不出来 —— 但它买什么、花什么，这张表说得清。");
        _out.WriteLine("⚠ 上面这条只在【稳态起算】下成立。裸起始工作帮时 N 个月超前要在第 1 月一次建出来，");
        _out.WriteLine("  那时 N 越大第 1 月越重（见 R40）。核准 N 前先确认期初姿态那一项填对了。");
    }

    /// <summary>
    /// G42 · <b>采空区回填比的敏感度表</b> —— 待核准项 2 也变成"看表挑"。
    ///
    /// <para><c>VoidFillFactor</c> 是<b>操作性损失</b>（坡道要留、排土坡面要退、工作面要空着），
    /// 推不出来。但它<b>值多少</b>可以算：每 0.1 换来多少内排率、省多少运输功。</para>
    ///
    /// <para><b>这条判据还兼一个回归网</b>：这个系数曾经<b>压根不起作用</b> ——
    /// 当时只卡了剥离上限 <c>ExtraCumCapM3</c>（管"能剥多少"），没卡配对侧的
    /// <c>InternalCumCapM3</c>（管"剥出来往哪儿放"），把回填比压到 2% 内排率纹丝不动。
    /// <b>所以这里必须判"它真的在动"，不能只打印一张表。</b></para>
    /// </summary>
    [Fact]
    public void G42_VoidFillFactor_SensitivityTable_AndItActuallyBites()
    {
        var rock = Profile();
        const int T = 12;
        const double perMonth = 8.0;

        // 内排位置放在采场后方（u 很小），保证它们会被启用 —— 否则整条内排路径没走到，
        // 表全是 0，判据就成了假绿。
        static List<DumpSlot> Slots()
        {
            var l = new List<DumpSlot>();
            for (int lv = 0; lv < 6; lv++)
                for (int b = 0; b < 5; b++)
                {
                    l.Add(new DumpSlot { DumpName = "内排", Level = lv, Order = b, CapacityM3 = 30e4,
                                         IsInternal = true, AvailableFromMonth = 1, HaulKm = 1.0 });
                    l.Add(new DumpSlot { DumpName = "外排", Level = lv, Order = b, CapacityM3 = 60e4,
                                         IsInternal = false, AvailableFromMonth = 1, HaulKm = 4.0 });
                }
            return l;
        }
        var mats = new GapMaterial[GapCode.Count(rock.SeamCount)];
        for (int g = 0; g < mats.Length; g++)
            mats[g] = new GapMaterial { Name = $"层间{g}", Code = "rock", Density = 2.5, Kr = 1.15 };

        _out.WriteLine("采空区回填比的敏感度（月煤 8 万t × 12 月，内排位置全程可用）");
        _out.WriteLine($"{"回填比",-10}{"内排率(%)",14}{"运输功(万t·km)",18}{"排不下(万m³)",16}");
        _out.WriteLine(new string('─', 60));

        var rows = new List<(double F, double Rate, double Work)>();
        foreach (double f in new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
        {
            var slots = Slots();
            var r = CoupledMinePlanner.Solve(new CoupledPlanInput
            {
                Schedule = new MonthlyScheduleInput
                {
                    Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM,
                    CoalTargetWt = Enumerable.Repeat(perMonth, T).ToArray(),
                    LookaheadMonths = 3, RecoveryTotalWt = perMonth * 3,
                    StartInSteadyState = true, Pace = StripPace.Level,
                },
                Dump = new DumpAllocationInput { Slots = slots, Materials = mats },
                // 都在推进轴原点后方 ⇒ 内排会被启用。
                // ⚠ 外排要给**横向偏距**，否则两者的几何运距一模一样 —— 那时内外排怎么换，
                //    运输功都不动（`hasU` 为真时 `slot.HaulKm` 不参与，运距全由位置算）。
                //    第一版就是这么把运输功那条判据做成假绿的。
                SlotU = slots.Select(_ => 0.0).ToArray(),
                SlotOffsetKm = slots.Select(s => s.IsInternal ? 0.0 : 3.0).ToArray(),
                VoidFillFactor = f,
                InternalClearanceM = 0,                             // 本条只量回填比，别让退距混进来
            });
            if (!r.Success || r.Dump == null) { _out.WriteLine($"{f,-10:0.0}解不出来：{r.Error}"); continue; }
            rows.Add((f, r.Dump.OverallInternalRatePct, r.Dump.TotalTransportWorkTKm / 1e4));
            _out.WriteLine($"{f,-10:0.0}{r.Dump.OverallInternalRatePct,14:0.0}"
                         + $"{r.Dump.TotalTransportWorkTKm / 1e4,18:0.0}{r.Dump.TotalUnplacedM3 / 1e4,16:0.00}");
        }
        Assert.True(rows.Count >= 4, "解出来的点太少，排不成敏感度表");

        // ★ 回归网：这个系数**必须真的在动**。它曾经压根不起作用（卡错了地方）。
        var lo = rows.First(); var hi = rows.Last();
        _out.WriteLine(new string('─', 60));
        _out.WriteLine($"回填比 {lo.F:0.0} → {hi.F:0.0}：内排率 {lo.Rate:0.0}% → {hi.Rate:0.0}%"
                     + $"　运输功 {lo.Work:0.0} → {hi.Work:0.0} 万t·km");
        Assert.True(hi.Rate > lo.Rate + 1e-6,
            $"回填比从 {lo.F:0.0} 提到 {hi.F:0.0}，内排率却没变（{lo.Rate:0.0}% → {hi.Rate:0.0}%）"
          + " —— 这个系数又没接到配对侧的 InternalCumCapM3 上（它曾经就是这么失效的）");
        // ⚠ 这里要用**严格小于**，别写成 `hi < lo + 1e-6` —— 那样两边相等也判过。
        //    第一版就是这么放过了"运输功全程 1141.4 纹丝不动"（当时外排没给横向偏距，
        //    内外排几何运距一样）。**判"应当变"的时候，容差要往难通过的方向放。**
        Assert.True(hi.Work < lo.Work * 0.99,
            $"内排率从 {lo.Rate:0.0}% 提到 {hi.Rate:0.0}%，运输功却几乎没降"
          + $"（{lo.Work:0.0} → {hi.Work:0.0} 万t·km）—— 内外排的运距根本没拉开");

        // 单调：回填比越大内排率越高（不该有回头）
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i].Rate >= rows[i - 1].Rate - 1e-6,
                $"回填比 {rows[i].F:0.0} 的内排率 {rows[i].Rate:0.0}% 反而低于 {rows[i - 1].F:0.0} 的 {rows[i - 1].Rate:0.0}%");

        double per01 = (hi.Rate - lo.Rate) / ((hi.F - lo.F) * 10);
        _out.WriteLine($"\n每 0.1 回填比 ≈ {per01:0.0} 个百分点的内排率。");
        _out.WriteLine("它是操作性损失（坡道/排土坡面/工作面要留空），推不出来 —— 但值多少，这张表说得清。");
    }
}
