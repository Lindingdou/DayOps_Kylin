// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/DownstreamRobustnessTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
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
/// INV6/INV7 组 · <b>下游部件的退化输入</b>。
///
/// <para>INV4/INV5 只审了调度器。配对、外循环、导出/导入、剖面文件这几件<b>从没受过同样的审</b>——
/// 而它们同样会被用户点出退化状态：没有排土位置、物料表缺项、容重填负、文件被截断……</para>
///
/// <para>判两件事，与 INV4/INV5 同一套纪律：<b>不许崩</b>（要么成功要么给出原因）、
/// <b>不许瞒</b>（引擎替用户改了参数要留条）。</para>
/// </summary>
public sealed class DownstreamRobustnessTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    public DownstreamRobustnessTests(ITestOutputHelper o)
    {
        _out = o;
        _dir = Path.Combine(Path.GetTempPath(), "pitmine_rb_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const int NX = 40, NY = 4, NZ = 24;
    private const double CELL = 10, ROCK_H = 20, DENS = 1.35, ALPHA = 18, ZDATUM = 160;

    private static TinSampler Plane(double z)
        => TinSampler.TryBuild(new[] { -6000.0, -6000.0, z, 6000.0, -6000.0, z, 6000.0, 6000.0, z, -6000.0, 6000.0, z },
                               new[] { 0, 1, 2, 0, 2, 3 })!;

    private static CoalProfile Scan()
    {
        var spec = new BlockModelSpec
        { Origin = new Vec3d(0, 0, 0), BlockSize = new Vec3d(CELL, CELL, CELL), Dimensions = new Vec3i(NX, NY, NZ) };
        var m = new BlockModel { Name = "RB", Spec = spec };
        var cd = m.EnsureCellData();
        cd.SetConstant("cA", 0); cd.SetConstant("cB", 0);
        long nxy = (long)NX * NY;
        for (int k = 0; k < NZ; k++)
        {
            double cz = k * CELL + CELL * 0.5;
            string? col = (cz >= 160 && cz <= 180) ? "cA" : (cz >= 80 && cz <= 100) ? "cB" : null;
            if (col == null) continue;
            for (long c = 0; c < nxy; c++) cd.SetCell(col, k * nxy + c, 1.0);
        }
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((-1200, -100, ZDATUM)); wl.Baseline.Add((-1200, 200, ZDATUM));
        wl.Samples.Add((-1200, 50, ZDATUM, 1, 0));
        var seams = new List<SeamSurfaces>
        {
            new() { Name = "A煤", Attribute = "cA", Density = DENS, Roof = Plane(180), Floor = Plane(160) },
            new() { Name = "B煤", Attribute = "cB", Density = DENS, Roof = Plane(100), Floor = Plane(80) },
        };
        var p = InclineVolumeEngine.BuildProfile(m, new[] { wl }, Plane(5000), seams, ALPHA, 1, 0.5, ROCK_H);
        Assert.True(p.Success, p.Error);
        return p;
    }

    private static MonthlyScheduleResult Sched(RockProfile rock)
    {
        var q = new double[8];
        for (int i = 0; i < 8; i++) q[i] = rock.TotalCoalWt() * 0.3 / 8;
        var r = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = q[0] * 3, StartInSteadyState = true,
        });
        Assert.True(r.Success, r.Error);
        return r;
    }

    private static GapMaterial[] Mats(int seams = 2)
    {
        var a = new GapMaterial[GapCode.Count(seams)];
        for (int g = 0; g < a.Length; g++) a[g] = new GapMaterial { Name = $"g{g}", Code = "rock", Density = 2.5, Kr = 1.15 };
        return a;
    }

    private static List<DumpSlot> Slots(double cap = 1e6)
    {
        var l = new List<DumpSlot>();
        for (int lv = 0; lv < 3; lv++)
            for (int b = 0; b < 3; b++)
                l.Add(new DumpSlot
                {
                    DumpName = b == 0 ? "内排" : "外排", Level = lv, Order = b, CapacityM3 = cap,
                    IsInternal = b == 0, AvailableFromMonth = 1, HaulKm = b == 0 ? 1.0 : 4.0, Cz = 100 + lv * 20,
                });
        return l;
    }

    // ── INV6 · 配对 / 外循环 / 导出导入 的退化输入 ─────────────────────────

    /// <summary>
    /// INV6 <b>下游部件退化输入：不许崩，失败必须说清。</b>
    /// </summary>
    [Fact]
    public void INV6_DownstreamDegenerateInputs_NeverThrow()
    {
        var rock = Scan().Rock!;
        var sched = Sched(rock);
        int ok = 0, fail = 0;

        void Try(string name, Func<(bool Ok, string Msg)> run)
        {
            try
            {
                var (o, msg) = run();
                if (o) ok++; else { fail++; Assert.False(string.IsNullOrWhiteSpace(msg), $"「{name}」失败却没给原因"); }
                _out.WriteLine($"{(o ? "✓" : "✗"),-3} {name,-28} {(o ? "" : msg.Split('—')[0].Trim())}");
            }
            catch (Exception ex) { Assert.Fail($"「{name}」抛异常：{ex.GetType().Name} {ex.Message}"); }
        }

        // 配对侧
        Try("没有排土位置", () => { var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = new(), Materials = Mats() }); return (r.Success, r.Error); });
        Try("没有物料参数", () => { var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(), Materials = Array.Empty<GapMaterial>() }); return (r.Success, r.Error); });
        Try("物料表比标签短", () => { var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(), Materials = new[] { Mats()[0] } }); return (r.Success, r.Error); });
        Try("库容全 0", () => { var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(0), Materials = Mats() }); return (r.Success, r.Error); });
        Try("库容为负", () => { var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(-100), Materials = Mats() }); return (r.Success, r.Error); });
        Try("Kr 为 0", () => { var m = Mats(); foreach (var x in m) x.Kr = 0; var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(), Materials = m }); return (r.Success, r.Error); });
        Try("Kr 为负", () => { var m = Mats(); foreach (var x in m) x.Kr = -1.2; var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(), Materials = m }); return (r.Success, r.Error); });
        Try("容重为负", () => { var m = Mats(); foreach (var x in m) x.Density = -2.5; var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(), Materials = m }); return (r.Success, r.Error); });
        Try("所有去向都不接纳", () => { var m = Mats(); foreach (var x in m) x.AllowedDumps = new[] { "根本不存在" }; var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(), Materials = m }); return (r.Success, r.Error); });
        Try("内排永不启用", () => { var s = Slots(); foreach (var x in s) x.AvailableFromMonth = 9999; var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = s, Materials = Mats() }); return (r.Success, r.Error); });
        Try("排产结果为空", () => { var r = DumpAllocator.Allocate(new MonthlyScheduleResult { Success = false, Error = "上游没成" }, new DumpAllocationInput { Slots = Slots(), Materials = Mats() }); return (r.Success, r.Error); });

        // 外循环
        var baseIn = new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM,
            CoalTargetWt = Enumerable.Repeat(rock.TotalCoalWt() * 0.3 / 8, 8).ToArray(),
            LookaheadMonths = 3, RecoveryTotalWt = rock.TotalCoalWt() * 0.1, StartInSteadyState = true,
        };
        Try("外循环·无排土位置", () => { var r = CoupledMinePlanner.Solve(new CoupledPlanInput { Schedule = baseIn, Dump = new DumpAllocationInput { Slots = new(), Materials = Mats() } }); return (r.Success, r.Error); });
        Try("外循环·SlotU 长度不符", () => { var r = CoupledMinePlanner.Solve(new CoupledPlanInput { Schedule = baseIn, Dump = new DumpAllocationInput { Slots = Slots(), Materials = Mats() }, SlotU = new double[] { 1, 2 } }); return (r.Success, r.Error); });
        Try("外循环·迭代上限 0", () => { var r = CoupledMinePlanner.Solve(new CoupledPlanInput { Schedule = baseIn, Dump = new DumpAllocationInput { Slots = Slots(), Materials = Mats() }, MaxIterations = 0 }); return (r.Success, r.Error); });
        Try("外循环·回填比为负", () => { var r = CoupledMinePlanner.Solve(new CoupledPlanInput { Schedule = baseIn, Dump = new DumpAllocationInput { Slots = Slots(), Materials = Mats() }, VoidFillFactor = -1 }); return (r.Success, r.Error); });

        // 导出/导入
        Try("导出·排产失败", () => { var e = MinePlanExport.Build(new MonthlyScheduleResult { Success = false, Error = "x" }, rock); return (e.ToJson().Length > 0, ""); });
        Try("导出·无剖面", () => { var e = MinePlanExport.Build(sched, null); return (e.Validate().Count == 0, string.Join("/", e.Validate())); });
        Try("导入·空契约", () => { var r = MineAssLibImport(null); return (r, ""); });
        Try("导入·空月份表", () => { var e = MinePlanExport.Build(sched, rock); e.Months.Clear(); e.Coal.Clear(); e.Flows.Clear(); return (e.Validate().Count >= 0, ""); });

        _out.WriteLine($"\n下游退化 {ok + fail} 例：成功 {ok} · 明确失败 {fail} · 抛异常 0");
        Assert.True(ok + fail >= 18);
    }

    private static bool MineAssLibImport(MinePlanExport? e)
    {
        // 导入在 PlanLib，这里只验 MineAssLib 侧的解析不崩
        if (e == null) return MinePlanExport.FromJson("", out _) == null;
        return true;
    }

    /// <summary>
    /// INV6b <b>配对侧的静默回退也要留条</b>：Kr/容重非正被夹回、库容为负被当 0。
    /// </summary>
    [Fact]
    public void INV6b_PairingSilentFallbacks_AreNoted()
    {
        var rock = Scan().Rock!;
        var sched = Sched(rock);

        var m = Mats(); m[GapCode.Overburden].Kr = -1.2; m[1].Density = 0;
        var r = DumpAllocator.Allocate(sched, new DumpAllocationInput
        { Slots = Slots(), Materials = m });
        Assert.True(r.Success, r.Error);
        foreach (var w in r.Warnings) _out.WriteLine("  " + w);
        Assert.Contains(r.Warnings, w => w.Contains("Kr"));
        Assert.Contains(r.Warnings, w => w.Contains("容重"));

        // 库容为负要当 0，并说出来
        var r2 = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(-100), Materials = Mats() });
        Assert.True(r2.Success, r2.Error);
        Assert.True(r2.TotalUnplacedM3 > 0, "库容全负却报全部排下");
        foreach (var kv in r2.RemainByDump) Assert.True(kv.Value >= -1e-6);
        _out.WriteLine($"库容为负：排不下 {r2.TotalUnplacedM3 / 1e4:0.0}万m³ · 告警 {r2.Warnings.Count} 条");
    }

    // ── INV7 · 剖面文件被损坏 ───────────────────────────────────────────────

    /// <summary>
    /// INV7 <b>剖面文件损坏时不许崩，也不许读出半份数据。</b>
    /// <para>工程含义：中间文件跟工程走，会被拷贝、被网盘同步、被杀毒软件截断。
    /// 读出半份剖面比读不出来危险得多 —— 半份剖面能算出一份"看上去正常"的月计划。</para>
    /// </summary>
    [Theory]
    [InlineData("empty", "空文件")]
    [InlineData("garbage", "随机字节")]
    [InlineData("truncated", "被截断")]
    [InlineData("badmagic", "magic 被改")]
    [InlineData("badversion", "版本号被改")]
    [InlineData("missing", "文件不存在")]
    public void INV7_CorruptProfileFile_FailsCleanly(string how, string label)
    {
        var prof = Scan();
        string good = Path.Combine(_dir, "good.profile");
        Assert.True(MineProfileFile.TrySave(good, prof, out _));
        var bytes = File.ReadAllBytes(good);
        string path = Path.Combine(_dir, $"{how}.profile");

        switch (how)
        {
            case "empty": File.WriteAllBytes(path, Array.Empty<byte>()); break;
            case "garbage": { var r = new Random(3); var b = new byte[bytes.Length]; r.NextBytes(b); File.WriteAllBytes(path, b); break; }
            case "truncated": File.WriteAllBytes(path, bytes.Take(bytes.Length / 3).ToArray()); break;
            case "badmagic": { var b = (byte[])bytes.Clone(); b[0] ^= 0xFF; File.WriteAllBytes(path, b); break; }
            case "badversion": { var b = (byte[])bytes.Clone(); b[4] = 99; File.WriteAllBytes(path, b); break; }
            case "missing": break;   // 不建文件
        }

        CoalProfile? got;
        string err;
        try { got = MineProfileFile.TryLoad(path, out err); }
        catch (Exception ex) { Assert.Fail($"「{label}」抛异常：{ex.GetType().Name} {ex.Message}"); return; }

        _out.WriteLine($"{label,-12} → {(got == null ? "拒收：" + err : "⚠ 竟然读出来了")}");
        Assert.True(got == null, $"「{label}」竟然读出了一份剖面 —— 半份数据比读不出来危险得多");
        Assert.False(string.IsNullOrWhiteSpace(err), $"「{label}」拒收却没说原因");
    }

    // ── INV8 · 扫描入口（BuildProfile）的退化输入与静默 ───────────────────

    private static (BlockModel M, List<SeamSurfaces> S, WorkLineGeometry W) Fixture()
    {
        var spec = new BlockModelSpec
        { Origin = new Vec3d(0, 0, 0), BlockSize = new Vec3d(CELL, CELL, CELL), Dimensions = new Vec3i(NX, NY, NZ) };
        var m = new BlockModel { Name = "RB", Spec = spec };
        var cd = m.EnsureCellData();
        cd.SetConstant("cA", 0); cd.SetConstant("cB", 0);
        long nxy = (long)NX * NY;
        for (int k = 0; k < NZ; k++)
        {
            double cz = k * CELL + CELL * 0.5;
            string? col = (cz >= 160 && cz <= 180) ? "cA" : (cz >= 80 && cz <= 100) ? "cB" : null;
            if (col == null) continue;
            for (long c = 0; c < nxy; c++) cd.SetCell(col, k * nxy + c, 1.0);
        }
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((-1200, -100, ZDATUM)); wl.Baseline.Add((-1200, 200, ZDATUM));
        wl.Samples.Add((-1200, 50, ZDATUM, 1, 0));
        var seams = new List<SeamSurfaces>
        {
            new() { Name = "A煤", Attribute = "cA", Density = DENS, Roof = Plane(180), Floor = Plane(160) },
            new() { Name = "B煤", Attribute = "cB", Density = DENS, Roof = Plane(100), Floor = Plane(80) },
        };
        return (m, seams, wl);
    }

    /// <summary>INV8 <b>扫描入口的退化输入：不许崩，失败必须说清。</b></summary>
    [Fact]
    public void INV8_BuildProfileDegenerateInputs_NeverThrow()
    {
        int ok = 0, fail = 0;
        void Try(string name, Func<CoalProfile> run)
        {
            CoalProfile p;
            try { p = run(); }
            catch (Exception ex) { Assert.Fail($"「{name}」抛异常：{ex.GetType().Name} {ex.Message}"); return; }
            if (p.Success) ok++;
            else { fail++; Assert.False(string.IsNullOrWhiteSpace(p.Error), $"「{name}」失败却没给原因"); }
            _out.WriteLine($"{(p.Success ? "✓" : "✗"),-3} {name,-26} {(p.Success ? "" : p.Error.Split('（', '；')[0].Trim())}");
        }
        var (m0, s0, w0) = Fixture();

        Try("块体为 null", () => InclineVolumeEngine.BuildProfile(null, new[] { w0 }, Plane(5000), s0, ALPHA, 1, 0.5, ROCK_H));
        Try("无工作线", () => InclineVolumeEngine.BuildProfile(m0, Array.Empty<WorkLineGeometry>(), Plane(5000), s0, ALPHA, 1, 0.5, ROCK_H));
        Try("工作线退化(1 个点)", () => { var w = new WorkLineGeometry { Success = true }; w.Baseline.Add((0, 0, 0)); return InclineVolumeEngine.BuildProfile(m0, new[] { w }, Plane(5000), s0, ALPHA, 1, 0.5, ROCK_H); });
        Try("工作线无样本", () => { var w = new WorkLineGeometry { Success = true }; w.Baseline.Add((0, 0, 0)); w.Baseline.Add((0, 100, 0)); return InclineVolumeEngine.BuildProfile(m0, new[] { w }, Plane(5000), s0, ALPHA, 1, 0.5, ROCK_H); });
        Try("无煤层", () => InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(5000), new List<SeamSurfaces>(), ALPHA, 1, 0.5, ROCK_H));
        Try("煤层无顶底板", () => { var s = s0.Select(x => new SeamSurfaces { Name = x.Name, Attribute = x.Attribute, Density = x.Density }).ToList(); return InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(5000), s, ALPHA, 1, 0.5, ROCK_H); });
        Try("属性列不存在", () => { var s = s0.Select(x => new SeamSurfaces { Name = x.Name, Attribute = "没有这列", Density = x.Density, Roof = x.Roof, Floor = x.Floor }).ToList(); return InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(5000), s, ALPHA, 1, 0.5, ROCK_H); });
        Try("现状面为 null", () => InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, null, s0, ALPHA, 1, 0.5, ROCK_H));
        Try("现状面压到底(全已采)", () => InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(-1000), s0, ALPHA, 1, 0.5, ROCK_H));
        Try("台阶高为负", () => InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(5000), s0, ALPHA, 1, 0.5, -15));
        Try("α 为 0", () => InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(5000), s0, 0, 1, 0.5, ROCK_H));
        Try("α 为 180", () => InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(5000), s0, 180, 1, 0.5, ROCK_H));
        Try("判煤阈值高到没煤", () => InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(5000), s0, ALPHA, 1, 1e9, ROCK_H));
        Try("coalMode=2(全算煤)", () => InclineVolumeEngine.BuildProfile(m0, new[] { w0 }, Plane(5000), s0, ALPHA, 2, 0, ROCK_H));
        Try("工作线远在扫掠域外", () => { var w = new WorkLineGeometry { Success = true }; w.Baseline.Add((0, 9e5, 0)); w.Baseline.Add((0, 9e5 + 100, 0)); w.Samples.Add((0, 9e5 + 50, 0, 1, 0)); return InclineVolumeEngine.BuildProfile(m0, new[] { w }, Plane(5000), s0, ALPHA, 1, 0.5, ROCK_H); });

        _out.WriteLine($"\n扫描入口退化 {ok + fail} 例：成功 {ok} · 明确失败 {fail} · 抛异常 0");
        Assert.True(ok + fail == 15);
    }

    /// <summary>
    /// INV8b <b>判煤规则的可疑输入必须留条</b>。
    /// <para>最危险的一条：<b>类别名打错一个字</b> ⇒ 那一层永远数不到煤，
    /// 而其他层照常有煤、剖面照样报<b>成功</b>。用户只看到那层是 0，不知道为什么。</para>
    /// </summary>
    [Fact]
    public void INV8b_SuspiciousCoalRules_AreNoted()
    {
        var (m, s, w) = Fixture();

        // ① 容重非正
        var s1 = s.Select(x => new SeamSurfaces { Name = x.Name, Attribute = x.Attribute, Density = 0, Roof = x.Roof, Floor = x.Floor }).ToList();
        var p1 = InclineVolumeEngine.BuildProfile(m, new[] { w }, Plane(5000), s1, ALPHA, 1, 0.5, ROCK_H);
        Assert.True(p1.Success, p1.Error);
        Assert.Contains(p1.Notes, n => n.Contains("容重") && n.Contains("1.35"));

        // ② 属性列不存在（第二层）
        var s2 = new List<SeamSurfaces>
        {
            s[0],
            new() { Name = "B煤", Attribute = "打错的列名", Density = DENS, Roof = s[1].Roof, Floor = s[1].Floor },
        };
        var p2 = InclineVolumeEngine.BuildProfile(m, new[] { w }, Plane(5000), s2, ALPHA, 1, 0.5, ROCK_H);
        Assert.True(p2.Success, p2.Error);           // ★ 整体仍然"成功" —— 这正是危险所在
        Assert.Contains(p2.Notes, n => n.Contains("打错的列名"));
        Assert.Contains(p2.Notes, n => n.Contains("B煤") && n.Contains("一吨煤都没数到"));
        foreach (var n in p2.Notes) _out.WriteLine("  " + n);
        _out.WriteLine($"  ⇒ 剖面 Success={p2.Success}，A煤 {p2.SeamBins[0].Count} 桶 / B煤 {p2.SeamBins[1].Count} 桶");
    }

    /// <summary>INV8c 输入都正常时<b>不许乱留条</b> —— 与 INV5b 同一条纪律。</summary>
    [Fact]
    public void INV8c_CleanScan_ProducesNoNoise()
    {
        var (m, s, w) = Fixture();
        var p = InclineVolumeEngine.BuildProfile(m, new[] { w }, Plane(5000), s, ALPHA, 1, 0.5, ROCK_H);
        Assert.True(p.Success, p.Error);
        _out.WriteLine(p.Notes.Count == 0 ? "干净扫描：无告警 ✓" : "意外告警：" + string.Join(" / ", p.Notes));
        Assert.Empty(p.Notes);
    }

    /// <summary>INV7b 好文件必须读得回来 —— 否则上一条可能是"什么都读不出来"的假绿。</summary>
    [Fact]
    public void INV7b_GoodFile_StillLoads()
    {
        var prof = Scan();
        string path = Path.Combine(_dir, "ok.profile");
        Assert.True(MineProfileFile.TrySave(path, prof, out _));
        var got = MineProfileFile.TryLoad(path, out string err);
        Assert.True(got != null, err);
        Assert.Equal(prof.Rock!.TotalRockM3, got!.Rock!.TotalRockM3, 3);
        _out.WriteLine($"好文件读回：岩 {got.Rock.TotalRockM3 / 1e4:0.000}万m³ ✓");
    }
}
