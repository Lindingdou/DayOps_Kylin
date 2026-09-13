// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/MinePlanEndToEndTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
using System;
using System.Collections.Generic;
using System.IO;
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
/// E2E 组 · <b>整条链一次跑通</b>。
///
/// <code>
/// 块体+顶底板+工作线 → 剖面(+指纹) → 落盘 → 读回 → 四步排产 → 派生比选
///                                  → 采排配对 → 外循环 → 导出契约 → JSON → 导入 → PlanLib 月计划
/// </code>
///
/// <para><b>为什么单独立这一组</b>：前面 119 条判据都是<b>逐件</b>判的 —— 每件都绿，
/// 不代表接起来还对。跨模块的账最容易在"交接处"漏：一边按实方一边按吨、
/// 一边 0 起一边 1 起、一边算了一边没算。这一组只判<b>一件事</b>：
/// <b>同一批方量，从块体一路走到月计划表，每一跳都对得上。</b></para>
/// </summary>
public sealed class MinePlanEndToEndTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    public MinePlanEndToEndTests(ITestOutputHelper o)
    {
        _out = o;
        _dir = Path.Combine(Path.GetTempPath(), "pitmine_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── 合成矿：两层倾斜煤 + 三个排土级 ─────────────────────────────────────
    private const int    NX = 80, NY = 6, NZ = 30;
    private const double CELL = 10.0, ROCK_H = 20.0, DENS = 1.35;
    private const double DIP = 0.10, A_ROOF0 = 260, B_ROOF0 = 140, THICK = 20;
    private const double ALPHA = 20.0, ZDATUM = 180.0, WL_X = -1500;
    private const int    MONTHS = 12;

    private static TinSampler DipPlane(double z0)
    {
        double h = 5000;
        double Z(double x) => z0 - DIP * x;
        return TinSampler.TryBuild(
            new[] { -h, -h, Z(-h), h, -h, Z(h), h, h, Z(h), -h, h, Z(-h) }, new[] { 0, 1, 2, 0, 2, 3 })!;
    }
    private static double ARoof(double x) => A_ROOF0 - DIP * x;
    private static double BRoof(double x) => B_ROOF0 - DIP * x;

    private static CoalProfile Scan()
    {
        var spec = new BlockModelSpec
        {
            Origin = new Vec3d(0, 0, 0), BlockSize = new Vec3d(CELL, CELL, CELL),
            Dimensions = new Vec3i(NX, NY, NZ),
        };
        var m = new BlockModel { Name = "E2E矿", Spec = spec };
        var cd = m.EnsureCellData();
        cd.SetConstant("cA", 0); cd.SetConstant("cB", 0);
        long nxy = (long)NX * NY;
        for (int k = 0; k < NZ; k++)
        {
            double cz = k * CELL + CELL * 0.5;
            for (int i = 0; i < NX; i++)
            {
                double cx = i * CELL + CELL * 0.5;
                string? col = (cz <= ARoof(cx) && cz >= ARoof(cx) - THICK) ? "cA"
                            : (cz <= BRoof(cx) && cz >= BRoof(cx) - THICK) ? "cB" : null;
                if (col == null) continue;
                for (int jy = 0; jy < NY; jy++) cd.SetCell(col, k * nxy + (long)jy * NX + i, 1.0);
            }
        }
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((WL_X, -100, ZDATUM)); wl.Baseline.Add((WL_X, 200, ZDATUM));
        wl.Samples.Add((WL_X, 50, ZDATUM, 1, 0));
        var seams = new List<SeamSurfaces>
        {
            new() { Name = "A煤", Attribute = "cA", Density = DENS, Roof = DipPlane(A_ROOF0), Floor = DipPlane(A_ROOF0 - THICK) },
            new() { Name = "B煤", Attribute = "cB", Density = DENS, Roof = DipPlane(B_ROOF0), Floor = DipPlane(B_ROOF0 - THICK) },
        };
        var p = InclineVolumeEngine.BuildProfile(m, new[] { wl }, DipPlane(2000), seams, ALPHA, 1, 0.5, ROCK_H);
        Assert.True(p.Success, p.Error);
        return p;
    }

    private static GapMaterial[] Materials()
    {
        var a = new GapMaterial[GapCode.Count(2)];
        for (int g = 0; g < a.Length; g++)
            a[g] = new GapMaterial { Name = $"标签{g}", Code = PlanMaterialCatalog.Rock, Density = 2.50, Kr = 1.15 };
        a[GapCode.Overburden] = new GapMaterial { Name = "覆岩", Code = PlanMaterialCatalog.Weathered, Density = 2.10, Kr = 1.12 };
        a[1] = new GapMaterial { Name = "层间", Code = PlanMaterialCatalog.Interburden, Density = 2.20, Kr = 1.13 };
        return a;
    }

    private static (List<DumpSlot> S, double[] U, double[] Off) Dump()
    {
        var s = new List<DumpSlot>(); var u = new List<double>(); var off = new List<double>();
        for (int lv = 0; lv < 5; lv++)
            for (int b = 0; b < 4; b++)
            {
                s.Add(new DumpSlot { DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = 30e4, IsInternal = true, AvailableFromMonth = 1, HaulKm = 1.2, Cx = 200 + b * 80, Cy = 60, Cz = 100 + lv * 20 });
                u.Add(1450 + b * 80); off.Add(0.3);
                s.Add(new DumpSlot { DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = 60e4, IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.8, Cx = -700 + b * 80, Cy = 900, Cz = 200 + lv * 20 });
                u.Add(1200); off.Add(2.5);
            }
        return (s, u.ToArray(), off.ToArray());
    }

    /// <summary>
    /// E2E1 <b>从块体到月计划表，每一跳的量都对得上。</b>
    /// </summary>
    [Fact]
    public void E2E1_BlockModelToMonthlyPlan_ConservesVolumeAtEveryHop()
    {
        // ── ① 扫块体出剖面 ───────────────────────────────────────────────
        var prof = Scan();
        var rock = prof.Rock!;
        _out.WriteLine("① 剖面  " + rock.Summary());
        Assert.NotNull(rock.Provenance);

        // ── ② 落盘 → 读回（带指纹校验）─────────────────────────────────
        string path = Path.Combine(_dir, "e2e.profile");
        Assert.True(MineProfileFile.TrySave(path, prof, out string se), se);
        var loaded = MineProfileFile.TryLoad(path, out string le, rock.Provenance);
        Assert.True(loaded != null, le);
        var rock2 = loaded!.Rock!;
        _out.WriteLine($"② 落盘  {new FileInfo(path).Length / 1024.0:0.0} KB · 指纹校验通过 · "
                     + $"岩 {rock2.TotalRockM3 / 1e4:0.000}万m³（原 {rock.TotalRockM3 / 1e4:0.000}）");
        Assert.Equal(rock.TotalRockM3, rock2.TotalRockM3, 3);
        Assert.Equal(rock.TotalCoalWt(), rock2.TotalCoalWt(), 6);

        // ── ③ 四步排产（用【读回来的】剖面，证明整条链能脱离内存态跑）──
        double totalCoal = rock2.TotalCoalWt();
        var q = new double[MONTHS];
        for (int i = 0; i < MONTHS; i++) q[i] = totalCoal * 0.5 / MONTHS;
        var schedIn = new MonthlyScheduleInput
        {
            Rock = rock2, AlphaDeg = ALPHA, ZDatum = ZDATUM, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = totalCoal * 0.125, StartInSteadyState = true,
        };
        var sched = MonthlyMineScheduler.Solve(schedIn);
        Assert.True(sched.Success, sched.Error);
        foreach (var c in sched.Checks) Assert.True(c.Ok, c.ToString());
        _out.WriteLine($"③ 排产  煤 {sched.TotalCoalWt:0.00}万t · 岩 {sched.TotalRockM3 / 1e4:0.00}万m³ "
                     + $"· 剥采比 {sched.OverallRatio:0.00} · CV {sched.RatioCv:0.000} · 硬约束全过");
        Assert.Equal(q.Sum(), sched.TotalCoalWt, 3);

        // ── ④ 采排配对 ──────────────────────────────────────────────────
        var (slots, su, soff) = Dump();
        var dumpIn = new DumpAllocationInput { Slots = slots, Materials = Materials(), Strategy = PairingStrategy.MinHaul };
        var dump = DumpAllocator.Allocate(sched, dumpIn);
        Assert.True(dump.Success, dump.Error);
        _out.WriteLine($"④ 配对  流 {dump.Months.Sum(m => m.Flows.Count)} 笔 · 内排率 {dump.OverallInternalRatePct:0.0}% "
                     + $"· 运输功 {dump.TotalTransportWorkTKm / 1e4:0.0}万t·km · 排不下 {dump.TotalUnplacedM3 / 1e4:0.0}万m³");
        // 【跳①→④守恒】排下的 + 排不下的 = 排产的剥离量
        Assert.Equal(sched.TotalRockM3, dump.TotalInSituM3 + dump.TotalUnplacedM3, 0);

        // ── ⑤ 外循环 ────────────────────────────────────────────────────
        var coupled = CoupledMinePlanner.Solve(new CoupledPlanInput
        {
            Schedule = schedIn, Dump = dumpIn, SlotU = su, SlotOffsetKm = soff,
            InternalClearanceM = 150, VoidFillFactor = 0.9, CoalDensity = DENS,
            MaxIterations = 4, ConvergeTolPct = 1.0,
        });
        Assert.True(coupled.Success, coupled.Error);
        _out.WriteLine("⑤ 外循环 " + coupled.ConvergenceNote.Trim());
        Assert.True(coupled.Converged, coupled.ConvergenceNote);

        // ── ⑥ 导出契约 → JSON → 读回 ───────────────────────────────────
        var export = MinePlanExport.Build(coupled.Schedule!, rock2, coupled.Dump, coupled,
            new ExportGeometry
            {
                RockBenchHeightM = ROCK_H, RockFaceDeg = 65, CoalFaceDeg = 65,
                MinBermM = 60, WorkingSlopeDeg = ALPHA, DumpBenchHeightM = 20, DumpFaceDeg = 35,
                Source = "E2E 设计参数",
            });
        Assert.Empty(export.Validate());
        string json = export.ToJson();
        var back = MinePlanExport.FromJson(json, out string je);
        Assert.True(back != null, je);
        Assert.Empty(back!.Validate());
        _out.WriteLine($"⑥ 契约  JSON {json.Length / 1024.0:0.0} KB · 月{back.Months.Count} 煤流{back.Coal.Count} "
                     + $"岩流{back.Flows.Count} 台阶{back.Benches.Count} · 自洽 ✓");
        // 【跳④→⑥守恒】
        Assert.Equal(coupled.Schedule!.TotalCoalWt, back.TotalCoalWanT, 3);
        Assert.Equal(coupled.Schedule.TotalRockM3 / 1e4, back.TotalStripWanM3, 3);

        // ── ⑦ 导入 PlanLib 月计划 ───────────────────────────────────────
        var imp = MinePlanImporter.Import(back, new CoalDestination { Name = "破碎站", HaulKm = 2.0 });
        Assert.True(imp.Success, imp.Error);
        foreach (var i in imp.Issues) _out.WriteLine("      " + i);
        Assert.True(imp.Executable, "端到端跑完却判成不可执行：" + string.Join(" / ", imp.Issues));
        _out.WriteLine("⑦ 月计划 " + MinePlanImporter.Summary(imp));

        // ── 全链守恒：块体里的煤 ⊇ 计划采出；每一跳的煤/岩逐月对得上 ──────
        Assert.Equal(MONTHS, imp.Months.Count);
        double planCoal = imp.Months.Sum(m => m.CoalWanT);
        double planStrip = imp.Months.Sum(m => m.StripWanM3);
        _out.WriteLine($"── 全链：块体煤 {totalCoal:0.00}万t ⊇ 计划采出 {planCoal:0.00}万t "
                     + $"· 排产岩 {coupled.Schedule.TotalRockM3 / 1e4:0.00} → 月计划岩 {planStrip:0.00}万m³");
        Assert.True(planCoal <= totalCoal + 1e-6, "计划采出超过了块体里的煤总量");
        Assert.True(Math.Abs(planCoal - coupled.Schedule.TotalCoalWt) <= 0.05 * MONTHS,
                    $"月计划采出 {planCoal:0.00} 与排产 {coupled.Schedule.TotalCoalWt:0.00} 万t 对不上");
        Assert.True(Math.Abs(planStrip - coupled.Schedule.TotalRockM3 / 1e4) <= 0.5 * MONTHS,
                    $"月计划剥离 {planStrip:0.00} 与排产 {coupled.Schedule.TotalRockM3 / 1e4:0.00} 万m³ 对不上");

        _out.WriteLine("\n月 | 采出万t | 剥离万m³ | 剥采比 | 内外排 | 流笔数");
        foreach (var m in imp.Months)
            _out.WriteLine($"{m.Month,2} | {m.CoalWanT,7:0.0} | {m.StripWanM3,8:0} | {m.Ratio,6:0.00} | "
                         + $"{(m.Dump == DumpMode.Internal ? "内排" : "外排"),6} | {m.Flows.Count,6}");
    }

    /// <summary>
    /// E2E2 <b>27 套派生 → 挑一套 → 走完剩下的链</b>，推荐方案落得了地。
    /// <para>工程含义：比选表上分最高的那套，必须是真能执行的 —— 否则比选就是纸上谈兵。</para>
    /// </summary>
    [Fact]
    public void E2E2_RecommendedScheme_SurvivesTheWholeChain()
    {
        var rock = Scan().Rock!;
        double totalCoal = rock.TotalCoalWt();
        var q = new double[MONTHS];
        for (int i = 0; i < MONTHS; i++) q[i] = totalCoal * 0.5 / MONTHS;
        var baseIn = new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = totalCoal * 0.125, StartInSteadyState = true,
        };
        var (slots, _, _) = Dump();
        var dumpIn = new DumpAllocationInput { Slots = slots, Materials = Materials() };

        var der = ScheduleDeriver.Derive(baseIn, new ScheduleAxes(), null, dumpIn);
        Assert.True(der.Success, der.Error);
        _out.WriteLine(ScheduleDeriver.CompareTable(der));
        var best = der.Recommended!;
        Assert.True(best.Feasible, "推荐的方案本身不可行 —— 比选是纸上谈兵");

        // 拿推荐方案的参数重跑一遍完整链
        var inp = new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM,
            CoalTargetWt = ScheduleDeriver.Shape(q, best.Coal, 0.25),
            LookaheadMonths = best.Lookahead, Pace = best.Strip,
            RecoveryTotalWt = best.Lookahead * q.Sum() / MONTHS, StartInSteadyState = true,
        };
        var sched = MonthlyMineScheduler.Solve(inp);
        Assert.True(sched.Success, sched.Error);
        Assert.Equal(best.TotalRockM3, sched.TotalRockM3, 3);       // 重跑与派生时算的一致

        var dump = DumpAllocator.Allocate(sched, new DumpAllocationInput
        { Slots = Dump().S, Materials = Materials(), Strategy = best.Pairing ?? PairingStrategy.MinHaul });
        Assert.True(dump.Success, dump.Error);

        var exp = MinePlanExport.Build(sched, rock, dump);
        Assert.Empty(exp.Validate());
        var imp = MinePlanImporter.Import(exp);
        Assert.True(imp.Success, imp.Error);
        Assert.True(imp.Executable, string.Join(" / ", imp.Issues));
        _out.WriteLine($"推荐方案「{best.Name}」落地：" + MinePlanImporter.Summary(imp));
    }
}
