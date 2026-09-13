// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/MinePlanExportTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
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
/// G24 组 · 下游导出契约。
/// <para>判三件事：<b>自洽</b>（流向对得上逐月、逐月对得上汇总）、<b>版本失配要拒收</b>、
/// <b>缺什么不许拿 0 冒充</b>（没跑配对时内排率是 −1 不是 0 —— 0 是"全外排"这个真实结论）。</para>
/// </summary>
public sealed class MinePlanExportTests
{
    private readonly ITestOutputHelper _out;
    public MinePlanExportTests(ITestOutputHelper o) => _out = o;

    // ⚠ 与 `MonthlyStripSessionTests` / `RollingReplanTests` 同一个坑：NX=60 × 10m ⇒ 推进方向仅 600m，
    //   工作帮超前 (K−1)×H/tanα ≈ 385m ⇒ **第 7 月起最上 3/8 个标高格顶在剖面数据边界上**，
    //   此后「必须剥」会自己往下掉（排产结果里现在有明确告警）。
    //   本组判的是**契约往返 / JSON 可读 / 哨兵值 / 缺参数要声明**，都不依赖尾部绝对量，所以照旧有效；
    //   但别在这个夹具上加"第 N 月剥离量该是多少"这类判据。
    private const int    NX = 60, NY = 5, NZ = 20;
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

    private static MonthlyScheduleResult Schedule(RockProfile rock)
    {
        var q = new double[12]; for (int i = 0; i < 12; i++) q[i] = 8;
        var r = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = 24, StartInSteadyState = true,
        });
        Assert.True(r.Success, r.Error);
        return r;
    }

    private static GapMaterial[] Materials()
    {
        var a = new GapMaterial[GapCode.Count(2)];
        for (int g = 0; g < a.Length; g++)
            a[g] = new GapMaterial { Name = $"标签{g}", Code = "rock", Density = 2.50, Kr = 1.15 };
        a[GapCode.Overburden] = new GapMaterial { Name = "覆岩", Code = "weathered", Density = 2.10, Kr = 1.12 };
        a[1] = new GapMaterial { Name = "层间", Code = "interburden", Density = 2.20, Kr = 1.13 };
        return a;
    }

    /// <summary>
    /// 排土位置。<b>质心必须给</b> —— 层体按它定位、运距按它算。
    /// 排土自下而上：Level 0 = 最下一级、标高最低（与 `DumpSlotAdapter` 的极性一致）。
    /// </summary>
    private static List<DumpSlot> Slots(double dumpBaseZ = 1200, double dumpBenchH = 20)
    {
        var l = new List<DumpSlot>();
        for (int lv = 0; lv < 6; lv++)
            for (int b = 0; b < 5; b++)
            {
                double z = dumpBaseZ + (lv + 0.5) * dumpBenchH;      // Level 越大越高
                l.Add(new DumpSlot
                {
                    DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = 10e4,
                    IsInternal = true, AvailableFromMonth = 4, HaulKm = 1.2,
                    Cx = 300 + b * 60, Cy = 200, Cz = z,
                });
                l.Add(new DumpSlot
                {
                    DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = 20e4,
                    IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.8,
                    Cx = -900 + b * 60, Cy = 1400, Cz = z + 40,
                });
            }
        return l;
    }

    private static (MonthlyScheduleResult S, DumpAllocationResult D, RockProfile R) Full()
    {
        var rock = Profile();
        var s = Schedule(rock);
        var d = DumpAllocator.Allocate(s, new DumpAllocationInput { Slots = Slots(), Materials = Materials() });
        Assert.True(d.Success, d.Error);
        return (s, d, rock);
    }

    // ── G24 · 自洽 ──────────────────────────────────────────────────────────

    /// <summary>
    /// G24 导出契约<b>自洽</b>：流向合计 = 逐月剥离−排不下；逐月合计 = 汇总。
    /// 工程含义：A5 的教训 —— 两个数谁也解释不了谁，是最难查的一类错。
    /// </summary>
    [Fact]
    public void G24_Export_IsSelfConsistent()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        var bad = e.Validate();
        foreach (var b in bad) _out.WriteLine("✗ " + b);
        Assert.Empty(bad);

        _out.WriteLine($"月 {e.Months.Count} 行 · 流向 {e.Flows.Count} 笔 · 台阶 {e.Benches.Count} 条");
        _out.WriteLine($"煤 {e.TotalCoalWanT:0.0}万t · 岩 {e.TotalStripWanM3:0.0}万m³ · 剥采比 {e.OverallRatio:0.00}"
                     + $" · 内排率 {e.OverallInternalRatePct:0.0}% · 运输功 {e.TotalTransportWorkWanTKm:0.0}万t·km");
        Assert.NotEmpty(e.Months); Assert.NotEmpty(e.Flows); Assert.NotEmpty(e.Benches);
        Assert.NotEmpty(e.Checks);
    }

    /// <summary>G24b Validate 抓得住被人为改坏的数（判据本身有效）。</summary>
    [Fact]
    public void G24b_Validate_CatchesTamperedNumbers()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        Assert.Empty(e.Validate());

        e.Months[3].StripWanM3 *= 1.5;                     // 改坏一个月的剥离量
        var bad = e.Validate();
        _out.WriteLine(string.Join("\n", bad));
        Assert.NotEmpty(bad);
        Assert.Contains(bad, x => x.Contains("第4月"));
    }

    /// <summary>
    /// G24f <b>第 1 个月的逐台阶推进与体积不许是 0</b>。
    ///
    /// <para>原来 <c>Build</c> 拿 <c>Months[i-1]</c> 当上月位置，第 1 个月没有上一月 ⇒
    /// 退回<b>本月减本月</b> ⇒ 逐台阶推进和逐台阶体积**恒为 0**，而
    /// <c>ExportMonth.StripWanM3</c>（来自调度器）却是对的。于是：
    /// 三维层体第 1 帧空着 · 逐台阶表第 1 月全是 0 · **而每一项既有校核都还是"✓"**。
    /// 第 1 个月恰恰是最多人看的那个月。</para>
    ///
    /// <para>修法是让调度器把<b>期初那一排</b>报出来（<c>MonthlyScheduleResult.InitialBenchX</c>），
    /// 它本来就在 <c>xMin[0,*]</c> 里、经过 <c>EnforceStaircase</c>，只是没往外给。
    /// 这条判据同时钉住「逐台阶体积合计 = 月剥离量」这条新加的自洽校核 —— 那才是通用的兜网。</para>
    /// </summary>
    [Fact]
    public void G24f_FirstMonth_BenchAdvanceAndVolume_AreNotZero()
    {
        var (s, d, r) = Full();

        // ① 期初那一排必须报出来，且与标高格同长、满足台阶超前。
        //    `Levels` 升序 = 标高由低到高，而**上面的台阶要超前下面的**（C2: x_上 − x_下 ≥ 一级横距）
        //    ⇒ 位置随 k 递【增】。
        Assert.Equal(s.Levels.Length, s.InitialBenchX.Length);
        for (int k = 1; k < s.Levels.Length; k++)
            Assert.True(s.InitialBenchX[k] >= s.InitialBenchX[k - 1] - 1e-6,
                $"期初台阶倒挂：上面的格{s.Levels[k]} {s.InitialBenchX[k]:0.0} "
              + $"落在下面的格{s.Levels[k - 1]} {s.InitialBenchX[k - 1]:0.0} 后方");

        var e = MinePlanExport.Build(s, r, d);
        int m1 = e.Months[0].Month;
        var b1 = e.Benches.Where(b => b.Month == m1 && b.Kind == "pit").ToList();
        Assert.NotEmpty(b1);

        // ② 第 1 月：至少有台阶真的推进了，体积合计要对得上月剥离量
        Assert.True(b1.Any(b => b.AdvanceM > 1e-6),
            "第1月逐台阶推进全是 0 —— 又拿本月减本月了");
        double sum1 = b1.Sum(b => b.VolumeWanM3);
        Assert.True(sum1 > 1e-6, "第1月逐台阶体积合计是 0");
        Assert.Equal(e.Months[0].StripWanM3, sum1, 1);
        Assert.True(e.Months[0].AdvanceM > 1e-6, "第1月综合推进是 0");

        // ③ 每个月都要对得上（不只第 1 月）
        Assert.Empty(e.Validate());

        // ④ 判据本身有效：把期初那一排弄没，Validate 必须点名第 1 月
        s.InitialBenchX = System.Array.Empty<double>();
        var bad = MinePlanExport.Build(s, r, d).Validate();
        _out.WriteLine("去掉期初位置后：" + string.Join(" | ", bad));
        Assert.Contains(bad, x => x.Contains($"第{m1}月") && x.Contains("逐台阶体积"));

        _out.WriteLine($"第{m1}月 综合推进 {e.Months[0].AdvanceM:0.##}m · "
                     + $"逐台阶体积合计 {sum1:0.00} = 月剥离 {e.Months[0].StripWanM3:0.00} 万m³");
        foreach (var b in b1.Take(4))
            _out.WriteLine($"  格{b.Level} z={b.ElevZ:0.#} 位置 {b.Position:0.#} 推进 {b.AdvanceM:0.##}m "
                         + $"体积 {b.VolumeWanM3:0.00}万m³");
    }

    /// <summary>
    /// G24g <b>累计量与位置是有方向的</b> —— 契约要自己校单调性与前缀和，不能只校求和。
    ///
    /// <para><b>为什么求和校不出来</b>：`SimRegions` 按【累计推进】把区域环整体外移
    /// （`RingOffset.Offset(ring, cumAdvance)`）。累计量一回退、台阶位置一后退，环就往回缩 ——
    /// 图上看着像"采空区又长回去了"，而**每一项求和校核都还是 ✓**（总量没变，只是顺序错了）。</para>
    ///
    /// <para>四条：月序号递增 · 累计不回退 · <b>累计 = 逐月前缀和</b>（两个数各记各的会漂）·
    /// 台阶位置逐月不后退（采场是推进位置 C1，排土是已填占比"只增不减"）。
    /// 每条都要**改坏能抓**，否则判据本身是空的。</para>
    /// </summary>
    [Fact]
    public void G24g_CumulativeAndPositions_AreMonotone_AndCatchTampering()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        Assert.Empty(e.Validate());                       // 干净契约先过

        // ① 月序号乱序
        var a = MinePlanExport.Build(s, r, d);
        (a.Months[2].Month, a.Months[3].Month) = (a.Months[3].Month, a.Months[2].Month);
        Assert.Contains(a.Validate(), x => x.Contains("月序号没递增"));

        // ② 累计采出回退
        var b = MinePlanExport.Build(s, r, d);
        b.Months[5].CumCoalWanT = b.Months[4].CumCoalWanT - 10;
        Assert.Contains(b.Validate(), x => x.Contains("累计采出回退"));

        // ③ 累计与逐月前缀和不一致（**只改累计、不改逐月** —— 求和校核照样全过）
        var c = MinePlanExport.Build(s, r, d);
        for (int i = 6; i < c.Months.Count; i++) c.Months[i].CumStripWanM3 += 50;
        var badC = c.Validate();
        Assert.Contains(badC, x => x.Contains("累计剥离") && x.Contains("前缀和"));
        Assert.DoesNotContain(badC, x => x.Contains("逐月岩合计"));   // 证明求和那条确实抓不到它
        _out.WriteLine("只改累计不改逐月 → " + badC.First(x => x.Contains("前缀和")));

        // ④ 台阶位置后退
        var f = MinePlanExport.Build(s, r, d);
        var pit = f.Benches.Where(x => x.Kind == "pit").OrderBy(x => x.Level).ThenBy(x => x.Month).ToList();
        Assert.NotEmpty(pit);
        var lvl = pit.GroupBy(x => x.Level).First(g => g.Count() >= 2).ToList();
        lvl[^1].Position = lvl[0].Position - 100;          // 末月缩回起点之前
        Assert.Contains(f.Validate(), x => x.Contains("位置回退"));
        _out.WriteLine("台阶位置后退 → " + f.Validate().First(x => x.Contains("位置回退")));

        // ⑤ 排土侧同理（已填占比只增不减）
        var g2 = MinePlanExport.Build(s, r, d);
        var dumps = g2.Benches.Where(x => x.Kind == "dump")
                              .GroupBy(x => (x.DumpName, x.Level)).First(x => x.Count() >= 2).ToList();
        dumps[^1].Position = 0;                            // 期末反而没填
        Assert.Contains(g2.Validate(), x => x.Contains("位置回退"));
    }

    /// <summary>
    /// G24h <b>范围与引用完整性</b> —— 第四类自洽：数在不在合法域里、指的东西存不存在。
    ///
    /// <para>前三类（求和守恒 / 有序 / 非有限）都只看**数之间的关系**，
    /// 看不出"这个去向根本不存在"或"Kr 小于 1"。自产的契约由构造保证一致，
    /// 但 <c>FromContract</c> 允许把**外面给的 JSON** 装回来（换机器复现、别人给的、手改过的）——
    /// 那条路上什么都可能。</para>
    ///
    /// <para><b>Kr &lt; 1 是物理不可能</b>：那意味着排土占的空间比挖出来的坑还小，
    /// 而岩石破碎后只会膨胀。这种数不该靠人眼看出来。</para>
    /// </summary>
    [Fact]
    public void G24h_RangesAndReferentialIntegrity_AreChecked()
    {
        var (s, d, r) = Full();
        Assert.Empty(MinePlanExport.Build(s, r, d).Validate());     // 干净契约先过

        // ① 内排率越界（−1 是哨兵，合法；150 不是）
        var a = MinePlanExport.Build(s, r, d);
        a.Months[2].InternalRatePct = 150;
        Assert.Contains(a.Validate(), x => x.Contains("内排率") && x.Contains("越界"));
        a.Months[2].InternalRatePct = -1;                            // 哨兵不许误报
        Assert.DoesNotContain(a.Validate(), x => x.Contains("越界"));

        // ② Kr < 1：坑挖出来 100m³，排土只占 90m³ —— 岩石不会缩
        var b = MinePlanExport.Build(s, r, d);
        var f0 = b.Flows.First(x => x.KrUsed > 0);
        f0.KrUsed = 0.9; f0.DumpWanM3 = f0.InSituWanM3 * 0.9;        // 连占容一起改，避开 Kr 换算那条
        Assert.Contains(b.Validate(), x => x.Contains("Kr") && x.Contains("膨胀"));

        // ③ 引用完整性：流指向一个排土层体里没有的去向
        var c = MinePlanExport.Build(s, r, d);
        Assert.NotEmpty(c.Benches.Where(x => x.Kind == "dump"));
        c.Flows.First().DestinationName = "根本不存在的排土场";
        Assert.Contains(c.Validate(), x => x.Contains("没有这个去向"));
        _out.WriteLine(c.Validate().First(x => x.Contains("没有这个去向")));

        // ④ 煤层下标越界
        var e4 = MinePlanExport.Build(s, r, d);
        Assert.NotEmpty(e4.Coal);
        e4.Coal[0].SeamIndex = 99;
        Assert.Contains(e4.Validate(), x => x.Contains("层下标") && x.Contains("越界"));

        // ⑤ 月序号 < 1
        var e5 = MinePlanExport.Build(s, r, d);
        e5.Months[0].Month = 0;
        Assert.Contains(e5.Validate(), x => x.Contains("月序号 < 1"));

        // ⑥ 这些都是**前三类抓不到**的 —— 否则就是重复劳动
        var only = MinePlanExport.Build(s, r, d);
        only.Flows.First().DestinationName = "查无此处";
        var bad = only.Validate();
        Assert.Single(bad);                                          // 只响引用完整性这一条
        Assert.Contains("没有这个去向", bad[0]);
        _out.WriteLine("只改去向名 → 只响 1 条：" + bad[0]);
    }

    /// <summary>
    /// G24i <b>完备性</b> —— 第五类自洽：该有的是不是都在。
    ///
    /// <para>前四类（守恒 / 有序 / 非有限 / 范围引用）**都只看在场的那些行**。
    /// 少了一整行它们一条都不响：月份 1,2,4,5 缺了第 3 月 ——
    /// 求和照样自洽（汇总跟着少）· 序号照样严格递增 · 范围引用也都没问题。
    /// 而下游是按 <c>Months</c> 逐帧推演的，那一期就这么没了。</para>
    ///
    /// <para>三条：月份连续无缺口 · 明细表不许有<b>孤儿行</b>（月份在逐月表里不存在）·
    /// 采场台阶各月标高格集合一致。</para>
    /// </summary>
    [Fact]
    public void G24i_Completeness_CatchesMissingRows()
    {
        var (s, d, r) = Full();
        Assert.Empty(MinePlanExport.Build(s, r, d).Validate());

        // ① 月份缺口：删掉中间一个月 —— 前四类全都抓不到
        var a = MinePlanExport.Build(s, r, d);
        int gone = a.Months[3].Month;
        a.Months.RemoveAt(3);
        a.Flows.RemoveAll(x => x.Month == gone);
        a.Coal.RemoveAll(x => x.Month == gone);
        a.Benches.RemoveAll(x => x.Month == gone);
        a.TotalCoalWanT = a.Months.Sum(x => x.CoalWanT);       // 汇总跟着改 ⇒ 守恒那条也过
        a.TotalStripWanM3 = a.Months.Sum(x => x.StripWanM3);
        for (int i = 0; i < a.Months.Count; i++)               // 累计也重排 ⇒ 有序那条也过
        {
            a.Months[i].CumCoalWanT = a.Months.Take(i + 1).Sum(x => x.CoalWanT);
            a.Months[i].CumStripWanM3 = a.Months.Take(i + 1).Sum(x => x.StripWanM3);
        }
        var badA = a.Validate();
        _out.WriteLine($"删掉第{gone}月（汇总/累计都补齐）→ " + string.Join(" | ", badA));
        Assert.Contains(badA, x => x.Contains("月份不连续") && x.Contains($"缺第 {gone}"));
        Assert.DoesNotContain(badA, x => x.Contains("合计") || x.Contains("前缀和"));   // 证明前四类真抓不到

        // ② 孤儿行：给一笔流安一个不存在的月份
        var b = MinePlanExport.Build(s, r, d);
        b.Flows[0].Month = 99;
        Assert.Contains(b.Validate(), x => x.Contains("物料流") && x.Contains("逐月表里没有这个月"));

        // ③ 某月少一级台阶
        var c = MinePlanExport.Build(s, r, d);
        var victim = c.Benches.First(x => x.Kind == "pit" && x.Month == c.Months[2].Month);
        c.Benches.Remove(victim);
        var badC = c.Validate();
        Assert.Contains(badC, x => x.Contains("少了标高格"));
        _out.WriteLine("某月少一级台阶 → " + badC.First(x => x.Contains("少了标高格")));
    }

    // ── G25 · 序列化与版本 ──────────────────────────────────────────────────

    /// <summary>G25 JSON 往返保真，且中文不转义（人能直接读）。</summary>
    [Fact]
    public void G25_Json_RoundTripsAndStaysReadable()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        string json = e.ToJson();
        _out.WriteLine($"JSON {json.Length / 1024.0:0.0} KB");
        _out.WriteLine(json[..Math.Min(600, json.Length)]);
        Assert.Contains("覆岩", json);                      // 中文没被 \uXXXX 掉
        Assert.Contains("\"SchemaVersion\": 1", json);

        var back = MinePlanExport.FromJson(json, out string err);
        Assert.True(back != null, err);
        Assert.Empty(back!.Validate());
        Assert.Equal(e.Months.Count, back.Months.Count);
        Assert.Equal(e.Flows.Count, back.Flows.Count);
        Assert.Equal(e.TotalStripWanM3, back.TotalStripWanM3, 6);
        Assert.Equal(e.Provenance, back.Provenance);
        for (int i = 0; i < e.Flows.Count; i++)
        {
            Assert.Equal(e.Flows[i].InSituWanM3, back.Flows[i].InSituWanM3, 9);
            Assert.Equal(e.Flows[i].MaterialCode, back.Flows[i].MaterialCode);
            Assert.Equal(e.Flows[i].KrUsed, back.Flows[i].KrUsed, 9);
        }
    }

    /// <summary>
    /// G25b 版本不认识就<b>拒收</b>，不猜、不读半份。
    /// 工程含义：契约的意义就在这——格式变了要当场失败，而不是下游读到半份数据继续算。
    /// </summary>
    [Fact]
    public void G25b_UnknownSchema_IsRejected()
    {
        var (s, d, r) = Full();
        string json = MinePlanExport.Build(s, r, d).ToJson().Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99");
        var back = MinePlanExport.FromJson(json, out string err);
        Assert.Null(back);
        _out.WriteLine(err);
        Assert.Contains("版本不匹配", err);
        Assert.Contains("99", err);

        Assert.Null(MinePlanExport.FromJson("{ 这不是 json", out string e2));
        Assert.Contains("解析失败", e2);
    }

    // ── G26 · 缺什么不许拿 0 冒充 ───────────────────────────────────────────

    /// <summary>
    /// G26 <b>没跑配对时内排率是 −1 不是 0</b>，且标注说明。
    /// 工程含义：0 是"全外排"这个真实结论。拿 0 冒充"没算"，下游会以为内排一方没用上。
    /// </summary>
    [Fact]
    public void G26_MissingPairing_IsMinusOneNotZero()
    {
        var rock = Profile();
        var e = MinePlanExport.Build(Schedule(rock), rock);           // 不给 dump
        Assert.Equal(-1, e.OverallInternalRatePct, 6);
        Assert.All(e.Months, m => Assert.Equal(-1, m.InternalRatePct, 6));
        Assert.Empty(e.Flows);
        Assert.Contains(e.Notes, n => n.Contains("未跑采排配对"));
        _out.WriteLine(string.Join("\n", e.Notes));
        Assert.Empty(e.Validate());                                    // 没配对不影响自洽
    }

    /// <summary>
    /// G26b 每份导出都<b>带来源</b>：剖面指纹 + 期初姿态 + 基建剥离。
    /// 工程含义：期初姿态填错时每项硬校核都是"✓"，只有这些标注看得出来。
    /// </summary>
    [Fact]
    public void G26b_EveryExport_CarriesItsProvenance()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        _out.WriteLine("指纹: " + e.Provenance);
        _out.WriteLine("期初: " + e.InitialPosture + " · 基建剥离 " + e.BoxCutWanM3.ToString("0.0") + "万m³");
        Assert.Contains("块体", e.Provenance);
        Assert.Contains("稳态", e.InitialPosture);
        Assert.True(e.BoxCutWanM3 > 0);

        // 没有指纹时也要如实说，不留空白让人以为可追溯
        var noProv = MinePlanExport.Build(s, null, d);
        Assert.Contains("不可追溯", noProv.Provenance);
    }

    /// <summary>
    /// G26c 硬约束不过时 <c>Feasible=false</c>，且校核条目带过来。
    /// 工程含义：下游拿到 <c>Feasible=false</c> 不得当成可执行计划。
    /// </summary>
    [Fact]
    public void G26c_InfeasiblePlan_IsFlaggedNotSilentlyExported()
    {
        var rock = Profile();
        var q = new double[12]; for (int i = 0; i < 12; i++) q[i] = 8;
        var s = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = 24, StartInSteadyState = true,
            RatioCeiling = 0.01,                                       // 荒谬地低 ⇒ H5 必红
        });
        Assert.True(s.Success);
        var e = MinePlanExport.Build(s, rock);
        Assert.False(e.Feasible);
        Assert.Contains(e.Checks, c => c.StartsWith("✗") && c.Contains("H5"));
        _out.WriteLine(string.Join("\n", e.Checks));

        // 库容不够 ⇒ AllPlaced=false 也要压到 Feasible
        var d = DumpAllocator.Allocate(Schedule(rock), new DumpAllocationInput
        { Slots = Slots().Take(2).ToList(), Materials = Materials() });
        var e2 = MinePlanExport.Build(Schedule(rock), rock, d);
        Assert.False(e2.AllPlaced);
        Assert.False(e2.Feasible);
    }

    /// <summary>
    /// G27 <b>采出侧逐层分解</b>必须有，且逐层吨量之和 = 月采出量。
    /// <para>工程含义：下游的 <c>MonthPeriod.CoalWanT</c> 是<b>按煤流派生</b>的
    /// （<c>Flows.Where(IsOre).Sum(TonnageWanT)</c>）。只导岩流是半份账 ——
    /// 下游会得到"采出量 0、剥采比 0"的月计划，而且它自己看不出少了什么。</para>
    /// </summary>
    [Fact]
    public void G27_CoalSide_IsBrokenDownBySeamAndSumsUp()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        Assert.NotEmpty(e.Coal);
        Assert.NotEmpty(e.SeamNames);
        foreach (var m in e.Months)
        {
            double sum = e.Coal.Where(c => c.Month == m.Month).Sum(c => c.TonnageWanT);
            Assert.Equal(m.CoalWanT, sum, 3);
        }
        // 实方 × 容重 = 吨量，逐笔
        foreach (var c in e.Coal) Assert.Equal(c.InSituWanM3 * c.DensityUsed, c.TonnageWanT, 6);
        var byS = e.Coal.GroupBy(c => c.SeamName).Select(g => $"{g.Key} {g.Sum(x => x.TonnageWanT):0.0}万t");
        _out.WriteLine("逐层合计: " + string.Join(" · ", byS));
        Assert.Equal(e.TotalCoalWanT, e.Coal.Sum(c => c.TonnageWanT), 3);
    }

    /// <summary>G27b 采出侧被改坏时 Validate 抓得住（判据本身有效）。</summary>
    [Fact]
    public void G27b_TamperedCoalBreakdown_IsCaught()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        Assert.Empty(e.Validate());
        e.Coal[0].TonnageWanT *= 2;
        var bad = e.Validate();
        _out.WriteLine(string.Join("\n", bad));
        Assert.Contains(bad, x => x.Contains("逐层煤合计"));
    }

    // ── G28 · 三维层体要用的东西 ────────────────────────────────────────────

    /// <summary>
    /// G28 <b>排土侧层体数据是全的</b>：标高、质心、已填占比、去向名。
    /// <para>工程含义：这四样齐了，层体才画得出"这个月排土场长成什么样"。
    /// 原来我把标高写死成 0（"由位置清单给，这里不猜"）—— 那等于把活推给下游，
    /// 而位置质心其实就在流里，加权求一下就有。</para>
    /// </summary>
    [Fact]
    public void G28_DumpBenches_CarryElevationAndFillFraction()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        var dumps = e.Benches.Where(b => b.Kind == "dump").ToList();
        Assert.NotEmpty(dumps);
        Assert.All(dumps, b =>
        {
            Assert.NotEqual(0, b.ElevZ);                       // 标高不再是写死的 0
            Assert.NotEmpty(b.DumpName);
            Assert.InRange(b.Position, 0, 1.0000001);          // 已填占比
        });
        // 同一 (去向,级) 的占比必须逐月单调不减 —— 排土只增不减
        foreach (var g in dumps.GroupBy(b => (b.DumpName, b.Level)))
        {
            var seq = g.OrderBy(b => b.Month).Select(b => b.Position).ToList();
            for (int i = 1; i < seq.Count; i++)
                Assert.True(seq[i] >= seq[i - 1] - 1e-9, $"{g.Key} 的已填占比回退了");
            Assert.Equal(1.0, seq[^1], 6);                     // 全期末必然填满到本级的全部投放量
        }
        var sample = dumps.OrderBy(b => b.Month).First();
        _out.WriteLine($"排土层体样本: 第{sample.Month}月 {sample.DumpName} L{sample.Level} "
                     + $"标高 {sample.ElevZ:0.#}m 质心({sample.Cx:0},{sample.Cy:0}) 已填 {sample.Position:P0}");
        // 采场侧仍然是实方、排土侧是占容 —— 两侧口径不同，判据里也不许混
        var pit = e.Benches.Where(b => b.Kind == "pit").ToList();
        Assert.NotEmpty(pit);
        Assert.All(pit, b => Assert.Empty(b.DumpName));
    }

    /// <summary>
    /// G28b 几何参数缺了要<b>明说</b>下游会退回垂直壁，不留空白。
    /// 工程含义：`采运排一体化` §十 边界 1 就是这条 —— 缺参数时下游"退回垂直壁并明确说明，
    /// 不塞缺省值冒充工程量"。导出侧要把话传到。
    /// </summary>
    [Fact]
    public void G28b_MissingGeometry_IsDeclaredNotDefaulted()
    {
        var (s, d, r) = Full();

        var bare = MinePlanExport.Build(s, r, d);              // 不给几何
        Assert.Contains(bare.Notes, n => n.Contains("垂直壁"));
        Assert.Contains(bare.Notes, n => n.Contains("排土台阶高"));
        Assert.Equal(ROCK_H, bare.Geometry.RockBenchHeightM, 6);   // 剖面推得出来的仍要填
        Assert.Equal(0, bare.Geometry.RockFaceDeg, 6);             // 推不出来的不许瞎填
        _out.WriteLine("缺参数: " + string.Join(" / ", bare.Notes));

        var full = MinePlanExport.Build(s, r, d, null, new ExportGeometry
        {
            RockBenchHeightM = ROCK_H, RockFaceDeg = 65, CoalFaceDeg = 65,
            MinBermM = 80, WorkingSlopeDeg = ALPHA,
            DumpBenchHeightM = 20, DumpFaceDeg = 35, Source = "设计参数",
        });
        Assert.DoesNotContain(full.Notes, n => n.Contains("垂直壁"));
        Assert.DoesNotContain(full.Notes, n => n.Contains("排土台阶高"));
        Assert.Equal("设计参数", full.Geometry.Source);
        Assert.Empty(full.Validate());
    }

    /// <summary>面板：导出 JSON 的头部 + 自洽校核结果。</summary>
    [Fact]
    public void G_ExportDashboard()
    {
        var (s, d, r) = Full();
        var e = MinePlanExport.Build(s, r, d);
        _out.WriteLine($"SchemaVersion {e.SchemaVersion} · Feasible {e.Feasible} · AllPlaced {e.AllPlaced}");
        _out.WriteLine("指纹: " + e.Provenance);
        _out.WriteLine("期初: " + e.InitialPosture);
        foreach (var n in e.Notes) _out.WriteLine("注: " + n);
        foreach (var c in e.Checks) _out.WriteLine("  " + c);
        _out.WriteLine($"月{e.Months.Count} 流向{e.Flows.Count} 台阶{e.Benches.Count} · JSON {e.ToJson().Length / 1024.0:0.0} KB");
        var bad = e.Validate();
        _out.WriteLine(bad.Count == 0 ? "自洽校核 ✓" : "自洽校核 ✗: " + string.Join(" / ", bad));
    }
}
