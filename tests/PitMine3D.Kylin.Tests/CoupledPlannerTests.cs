// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/CoupledPlannerTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
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
/// G18 组 · 采—排—运外循环。
/// <para>判的是这条环<b>咬得住</b>：内排容量受采空区约束、运距逐月变、循环收敛且逐轮可查。
/// 不收敛时必须明说，不许拿末轮冒充答案。</para>
/// </summary>
public sealed class CoupledPlannerTests
{
    private readonly ITestOutputHelper _out;
    public CoupledPlannerTests(ITestOutputHelper o) => _out = o;

    private const int    NX = 100, NY = 5, NZ = 20;
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

    private static GapMaterial[] Materials()
    {
        var arr = new GapMaterial[GapCode.Count(2)];
        for (int g = 0; g < arr.Length; g++) arr[g] = new GapMaterial { Name = $"标签{g}", Density = 2.50, Kr = 1.15 };
        arr[GapCode.Overburden] = new GapMaterial { Name = "覆岩(风化岩)", Density = 2.10, Kr = 1.12 };
        return arr;
    }

    /// <summary>
    /// 排土位置 + 它们在推进轴 u 上的坐标。
    /// 内排摆在采场后方（u = 2050..2450，随煤前界推过而逐个"腾出来"）；外排在侧后方、恒定偏距。
    /// </summary>
    private static (List<DumpSlot> Slots, double[] U, double[] Off) Layout(
        double innerPerSlot = 8e4, double outerPerSlot = 20e4, int levels = 6, int bands = 5,
        double innerU0 = 2050, double innerStep = 100)
    {
        var slots = new List<DumpSlot>(); var u = new List<double>(); var off = new List<double>();
        for (int lv = 0; lv < levels; lv++)
            for (int b = 0; b < bands; b++)
            {
                slots.Add(new DumpSlot
                {
                    DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = innerPerSlot,
                    IsInternal = true, AvailableFromMonth = 1, HaulKm = 1.2,
                });
                u.Add(innerU0 + b * innerStep);   // 沿推进方向摊开：越靠前的越晚腾出来
                off.Add(0.3);
                slots.Add(new DumpSlot
                {
                    DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = outerPerSlot,
                    IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.8,
                });
                u.Add(1800);                // 固定在采场后方
                off.Add(2.5);
            }
        return (slots, u.ToArray(), off.ToArray());
    }

    private static CoupledPlanInput Input(RockProfile rock, double innerPerSlot = 8e4, double outerPerSlot = 20e4,
                                          double innerU0 = 2050, double innerStep = 100)
    {
        var (slots, u, off) = Layout(innerPerSlot, outerPerSlot, innerU0: innerU0, innerStep: innerStep);
        var q = new double[12]; for (int i = 0; i < 12; i++) q[i] = 15;
        return new CoupledPlanInput
        {
            Schedule = new MonthlyScheduleInput
            {
                Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM,
                CoalTargetWt = q, LookaheadMonths = 3, RecoveryTotalWt = 45,
                StartInSteadyState = true,
            },
            Dump = new DumpAllocationInput { Slots = slots, Materials = Materials(), Strategy = PairingStrategy.MinHaul },
            SlotU = u, SlotOffsetKm = off,
            InternalClearanceM = 200, VoidFillFactor = 0.9, CoalDensity = DENS,
            MaxIterations = 5, ConvergeTolPct = 1.0,
        };
    }

    // ── G18 · 循环咬得住 ────────────────────────────────────────────────────

    /// <summary>G18 外循环跑得通并收敛，逐轮记录可查。</summary>
    [Fact]
    public void G18_OuterLoop_Converges()
    {
        var r = CoupledMinePlanner.Solve(Input(Profile()));
        Assert.True(r.Success, r.Error);
        _out.WriteLine(CoupledMinePlanner.IterationReport(r));
        _out.WriteLine(DumpAllocator.FlowMatrix(r.Dump!, r.Schedule!));

        Assert.True(r.Converged, "外循环没收敛：" + r.ConvergenceNote);
        Assert.True(r.Iterations.Count >= 2, "一轮就报收敛 —— 没真跑循环");
        Assert.NotNull(r.Schedule); Assert.NotNull(r.Dump);
    }

    /// <summary>
    /// G18b <b>第 0 轮全外排</b>是保守起点：内排一方都不许用。
    /// 工程含义：期初采空区还没形成，内排无从谈起。从最保守处起解，循环才是单调上升的。
    /// </summary>
    [Fact]
    public void G18b_RoundZero_UsesNoInternalDump()
    {
        var r = CoupledMinePlanner.Solve(Input(Profile()));
        Assert.True(r.Success, r.Error);
        var round0 = r.Iterations[0];
        _out.WriteLine(round0.ToString());
        Assert.Equal(0, round0.InternalSlotsEnabled);
        Assert.Equal(0, round0.InternalRatePct, 6);
    }

    /// <summary>
    /// G18c 循环是<b>单调上升</b>的：内排启用数只增不减，内排率只升不降。
    /// 工程含义：这是收敛性的来源 —— 剥得多 → 采空区大 → 内排容量大 → 能剥更多。
    /// </summary>
    [Fact]
    public void G18c_Loop_IsMonotoneUpward()
    {
        var r = CoupledMinePlanner.Solve(Input(Profile()));
        Assert.True(r.Success, r.Error);
        var ok = r.Iterations.Where(i => i.ScheduleOk).ToList();
        for (int i = 1; i < ok.Count; i++)
        {
            Assert.True(ok[i].InternalSlotsEnabled >= ok[i - 1].InternalSlotsEnabled,
                        $"第{ok[i].Round}轮内排启用数反而变少了");
            Assert.True(ok[i].InternalRatePct >= ok[i - 1].InternalRatePct - 1e-6,
                        $"第{ok[i].Round}轮内排率反而降了");
        }
        _out.WriteLine(string.Join("\n", ok.Select(x => "  " + x)));
    }

    /// <summary>
    /// G18d <b>内排容量受采空区约束</b>：把可回填比例压到极低，内排率必须跟着掉。
    /// 工程含义：坑还没挖出来就没法回填 —— 不卡这条会排出"往还不存在的空间里排土"的计划。
    /// </summary>
    [Fact]
    public void G18d_InternalCapacity_IsBoundedByMinedVoid()
    {
        var rock = Profile();
        var loose = CoupledMinePlanner.Solve(Input(rock));
        var tight = Input(rock); tight.VoidFillFactor = 0.02;      // 采空区几乎不许回填
        var r2 = CoupledMinePlanner.Solve(tight);
        Assert.True(loose.Success && r2.Success, loose.Error + r2.Error);
        _out.WriteLine($"回填比 0.9  → 内排率 {loose.Dump!.OverallInternalRatePct:0.0}% · 运输功 {loose.Dump.TotalTransportWorkTKm / 1e4:0.0}万t·km");
        _out.WriteLine($"回填比 0.02 → 内排率 {r2.Dump!.OverallInternalRatePct:0.0}% · 运输功 {r2.Dump.TotalTransportWorkTKm / 1e4:0.0}万t·km");
        Assert.True(r2.Dump.OverallInternalRatePct < loose.Dump.OverallInternalRatePct - 1e-6,
                    "把采空区回填比压到 2% 内排率却没降 —— 这条约束没接上");
    }

    /// <summary>
    /// G18e <b>运距逐月在变</b>：同一个内排位置，不同月份的运距必须不同。
    /// 工程含义：坑越推越远，静态运距会让「运输功最小」前几个月挑对、后几个月挑错。
    /// </summary>
    [Fact]
    public void G18e_HaulDistance_ChangesMonthByMonth()
    {
        var r = CoupledMinePlanner.Solve(Input(Profile()));
        Assert.True(r.Success, r.Error);
        var internalFlows = r.Dump!.Months.SelectMany(m => m.Flows).Where(f => f.IsInternal).ToList();
        Assert.NotEmpty(internalFlows);
        var distinct = internalFlows.Select(f => Math.Round(f.HaulKm, 3)).Distinct().Count();
        _out.WriteLine("内排逐月运距(km): " + string.Join(" · ",
            r.Dump.Months.Where(m => m.Flows.Any(f => f.IsInternal))
                .Select(m => $"{m.Month}月 {m.Flows.Where(f => f.IsInternal).Average(f => f.HaulKm):0.00}")));
        Assert.True(distinct > 1, "所有内排流的运距一模一样 —— 逐月运距没接上，还是静态值");
    }

    /// <summary>
    /// G18f 第 0 轮全外排<b>无解</b>时，循环要能靠开内排救回来，而不是直接判死。
    ///
    /// <para>工程含义：这正是外循环存在的理由 —— 一次求解看不出"开了内排就可行"。</para>
    ///
    /// <para><b>夹具的前提必须成立</b>：外排要够撑到内排腾出来的那个月，撑不到就是<b>真无解</b>，
    /// 循环也救不回来（内排最早第 5 月才可用，而走廊在第 2 月就倒挂 —— 那不是算法弱，是计划真不可行）。
    /// 所以外排给到能撑过前半年，内排位置也往前摆。<b>这个夹具第一版就摆错了。</b></para>
    /// </summary>
    [Fact]
    public void G18f_LoopRecoversWhatSingleShotWouldReject()
    {
        var rock = Profile();
        // 外排 6万×30 = 180万m³占容（约 157万m³实方）—— 撑得过前 5 个月，撑不到全年；
        // 内排 12万×30 = 360万m³，且摆在采场后方更近处，第 4 个月起陆续腾出来。
        var inp = Input(rock, innerPerSlot: 12e4, outerPerSlot: 6e4, innerU0: 2000, innerStep: 80);
        var r = CoupledMinePlanner.Solve(inp);
        _out.WriteLine(CoupledMinePlanner.IterationReport(r));
        Assert.True(r.Success, r.Error);

        var r0 = r.Iterations[0];
        Assert.False(r0.ScheduleOk, "第0轮（全外排）竟然就解出来了 —— 这个夹具证明不了循环的价值");
        Assert.Equal(0, r0.InternalSlotsEnabled);

        var last = r.Iterations.Last(i => i.ScheduleOk);
        _out.WriteLine($"第0轮(全外排) 无解 → 第{last.Round}轮 内排启用 {last.InternalSlotsEnabled} 个位置、"
                     + $"内排率 {last.InternalRatePct:0.0}%、排不下 {last.UnplacedM3 / 1e4:0.0}万m³");
        Assert.True(last.InternalSlotsEnabled > 0, "内排一直没启用 —— 循环白跑");
        Assert.True(last.InternalRatePct > 0, "内排启用了却一方没进");
    }

    /// <summary>
    /// G18g 不收敛时<b>明说</b>，不拿末轮冒充答案。把轮次上限压到 1 制造"没跑够"。
    /// </summary>
    [Fact]
    public void G18g_NonConvergence_IsDeclaredNotHidden()
    {
        var inp = Input(Profile()); inp.MaxIterations = 1; inp.ConvergeTolPct = 0.0001;
        var r = CoupledMinePlanner.Solve(inp);
        _out.WriteLine(CoupledMinePlanner.IterationReport(r));
        Assert.False(r.Converged);
        Assert.Contains("未收敛", r.ConvergenceNote);
        Assert.Contains("摆动", r.ConvergenceNote);
        Assert.True(r.Success, "未收敛不等于没结果 —— 末轮仍要给出来，只是要标明");
    }

    // ── G18h · 等效运距（含坡度折算）─────────────────────────────────────────

    /// <summary>
    /// G18h <b>运距必须含高差，不能只算平距</b>。
    /// <para>工程含义：露天矿运输功大头在<b>提升</b>。<b>内排通常是下排</b>（重车下坡、几乎白送）、
    /// <b>外排常要爬升到排土场顶</b>。只算平距会让平距相同的内/外排位看起来一样贵 ——
    /// 于是「内排优先」和「运输功最小」在<b>该分开的地方分不开</b>，
    /// 而内排率恰恰是这套设计里最重要的决策变量之一。</para>
    /// </summary>
    [Fact]
    public void G18h_HaulDistance_IncludesGrade()
    {
        var h = new HaulModel { Tortuosity = 1.3, UphillEquivalent = 12, DownhillEquivalent = 3 };
        // 同样 1.0 km 平距：下排 100m vs 上排 100m
        double down = h.EquivalentKm(1.0, -100);
        double flat = h.EquivalentKm(1.0, 0);
        double up = h.EquivalentKm(1.0, +100);
        _out.WriteLine($"平距 1.0km：下排100m → {down:0.000}km · 纯平 → {flat:0.000}km · 上排100m → {up:0.000}km");
        Assert.Equal(1.3, flat, 6);
        Assert.Equal(1.3 + 0.3, down, 6);      // 100m × 3 / 1000
        Assert.Equal(1.3 + 1.2, up, 6);        // 100m × 12 / 1000
        // 【方向：上坡 > 下坡 > 纯平】—— **下坡不是免费的**：要多走一段坡道，还受制动与安全限速约束。
        // 只是比上坡便宜得多（本例 4 倍）。一开始把断言写成 `纯平 > 下坡`，是把"省力"当成了"零成本"。
        Assert.True(up > down, "上坡竟然不比下坡贵");
        Assert.True(down > flat, "下坡竟然和纯平一样 —— 下坡不是免费的（多走坡道 + 制动限速）");
        Assert.True((up - flat) > 3 * (down - flat), "上下坡的不对称性不够 —— 上坡该贵得多");
        Assert.Contains("待现场核准", h.Text());
    }

    /// <summary>
    /// G18i <b>接进外循环后，下排的内排位真的比平距相同的外排位便宜</b>（端到端验行为，不只验公式）。
    /// </summary>
    [Fact]
    public void G18i_DownhillInternalDump_IsCheaperThanUphillExternal()
    {
        var rock = Profile();
        var inp = Input(rock);
        // 造一对：平距相同，一个在坑底（下排）、一个在高处（上排）
        var slots = new List<DumpSlot>();
        var u = new List<double>(); var off = new List<double>();
        for (int lv = 0; lv < 4; lv++)
            for (int b = 0; b < 3; b++)
            {
                slots.Add(new DumpSlot { DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = 20e4, IsInternal = true, AvailableFromMonth = 1, Cz = 40 + lv * 10 });
                u.Add(2100); off.Add(1.0);
                slots.Add(new DumpSlot { DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = 20e4, IsInternal = false, AvailableFromMonth = 1, Cz = 320 + lv * 10 });
                u.Add(2100); off.Add(1.0);      // ★ 平距完全相同，只有标高不同
            }
        inp.Dump = new DumpAllocationInput { Slots = slots, Materials = Materials(), Strategy = PairingStrategy.MinHaul };
        inp.SlotU = u.ToArray(); inp.SlotOffsetKm = off.ToArray();
        inp.Haul = new HaulModel { Tortuosity = 1.3, UphillEquivalent = 12, DownhillEquivalent = 3 };

        var r = CoupledMinePlanner.Solve(inp);
        Assert.True(r.Success, r.Error);
        _out.WriteLine(CoupledMinePlanner.IterationReport(r));

        var flows = r.Dump!.Months.SelectMany(m => m.Flows).ToList();
        double inner = flows.Where(f => f.IsInternal).DefaultIfEmpty().Average(f => f?.HaulKm ?? 0);
        double outer = flows.Where(f => !f.IsInternal).DefaultIfEmpty().Average(f => f?.HaulKm ?? 0);
        _out.WriteLine($"平距相同下：内排(下排)均运距 {inner:0.000}km · 外排(上排)均运距 {outer:0.000}km");
        // 两边都必须真有流 —— 本条判的就是"内排 vs 外排"这个比较本身。
        // 原来外排那半裹在 `if (flows.Any(f => !f.IsInternal))` 里：策略是 MinHaul、内排又更便宜，
        // 一旦全进了内排，`inner < outer` 这句**核心断言整段跳过**，判据照样绿。
        Assert.True(flows.Any(f => f.IsInternal), "一方都没进内排 —— 这个夹具证明不了什么");
        Assert.True(flows.Any(f => !f.IsInternal),
                    "一方都没进外排 —— 那就没有可比的对象，这条判据会空过（把外排容量调大或内排容量调小）");
        Assert.True(inner < outer, "平距相同的下排内排位竟然不比上排外排位便宜 —— 高差没算进去");
        Assert.Contains("提升当量", r.HaulNote);
    }

    /// <summary>面板：逐轮表 + 末轮流向矩阵。</summary>
    [Fact]
    public void G_CoupledDashboard()
    {
        var r = CoupledMinePlanner.Solve(Input(Profile()));
        _out.WriteLine(CoupledMinePlanner.IterationReport(r));
        if (r.Success)
        {
            _out.WriteLine(MonthlyMineScheduler.Report(r.Schedule!));
            _out.WriteLine(DumpAllocator.FlowMatrix(r.Dump!, r.Schedule!));
        }
        else _out.WriteLine("失败：" + r.Error);
    }

    /// <summary>
    /// G18j · <b>外循环那份手写 clone 不许漏字段</b>。
    ///
    /// <para><b>为什么非用反射不可</b>：漏字段是<b>静默</b>的 —— 漏掉的那个变成缺省值，
    /// 排产照跑、照出计划，只是**按另一套输入算的**。逐个手写"这个字段也要相等"
    /// 等于把同一份清单抄第三遍，下次加字段照样一起漏。</para>
    ///
    /// <para><b>实测漏过 <c>CoalTargetBySeam</c></b>：用户填的逐层煤量在外循环里被丢掉，
    /// 引擎改按统一前界自己摊 —— 而 R36 那套诚实机制<b>照常工作</b>，
    /// 如实报了"这是引擎摊的"。也就是说<b>诚实报告正好把丢字段掩盖成了一句合理的说明</b>，
    /// 从日志上永远看不出用户的输入被吃了。</para>
    /// </summary>
    [Fact]
    public void G18j_CloneSchedule_CopiesEveryField()
    {
        // 每个字段都填成**非缺省**值，漏掉哪个都会在下面比出来
        var src = new MonthlyScheduleInput
        {
            // Pace 取**非缺省**值（缺省是 Level）—— 填缺省值的话漏没漏都比不出来
            Rock = Profile(), AlphaDeg = 17.5, ZDatum = 33, Pace = StripPace.FrontLoad,
            CoalTargetWt = new[] { 7.0, 8.0, 9.0 },
            CoalTargetBySeam = new[] { new[] { 3.0, 4.0 }, new[] { 3.5, 4.5 }, new[] { 4.0, 5.0 } },
            StripCapM3 = new[] { 1e6, 1.1e6, 1.2e6 },
            LookaheadMonths = 4, RecoveryTotalWt = 123, RatioCeiling = 9.5,
            CoalStartWt = 2.5, StartInSteadyState = true,
            InitialBenchX = new[] { 11.0, 22.0, 33.0 },
        };
        // 三处都抄同一个类型，**必须一起判** —— 实测就是一处漏、另一处也漏。
        // 不开例外名单：例外名单一开，下一个真漏掉的字段就有地方藏
        // （为此把外循环那份也照抄 ExtraCumCapM3，反正它随后就被改写）。
        var clones = new (string Who, Func<MonthlyScheduleInput, MonthlyScheduleInput> F)[]
        {
            ("CoupledMinePlanner", CoupledMinePlanner.CloneScheduleForTest),
            ("ScheduleDerivation", ScheduleDeriver.CloneForTest),
            ("RollingReplan",      RollingReplan.CloneForTest),
        };
        var fields = typeof(MonthlyScheduleInput).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).ToList();
        Assert.True(fields.Count >= 12, "字段数骤减 —— 先确认反射拿对了地方，再谈通过");

        foreach (var (who, f) in clones)
        {
            var dst = f(src);
            var missed = new List<string>();
            var aliased = new List<string>();
            foreach (var fi in fields)
            {
                object? a = fi.GetValue(src), b = fi.GetValue(dst);
                bool same = a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb
                          ? Flat(ea).SequenceEqual(Flat(eb))
                          : Equals(a, b);
                if (!same) missed.Add($"{fi.Name}（源 {Show(a)} → 复制后 {Show(b)}）");
                // 数组必须是**新的一份**：共用同一批行的话，改克隆件会改到来源上。
                // Rock 是故意共享的（剖面很大且当只读用），不在此列。
                else if (fi.Name != nameof(MonthlyScheduleInput.Rock)
                      && a is Array arr && arr.Length > 0 && ReferenceEquals(a, b))
                    aliased.Add(fi.Name);
            }
            foreach (var m in missed) _out.WriteLine($"✗ {who} 漏了 " + m);
            foreach (var m in aliased) _out.WriteLine($"✗ {who} 与来源共用同一个数组：" + m);
            Assert.Empty(missed);
            Assert.Empty(aliased);
            _out.WriteLine($"{who}：逐字段比过 {fields.Count} 个，数组均为新拷贝");
        }

        // ── 配对输入那份 clone 同样要判 ──────────────────────────────────────
        // 这里**只判值、不判"是不是新数组"**：`Materials` 是只读物料目录、`Slots` 是调用方
        // 新造的一批，都刻意共享。这不是开例外名单，是这个类型本来就没有"逐轮改写的数组"。
        var dsrc = new DumpAllocationInput
        {
            Slots = new List<DumpSlot> { new() { DumpName = "北排", Level = 1, CapacityM3 = 5e5, Cz = 30 } },
            Materials = new[] { new GapMaterial { Name = "覆岩", Code = "rock", Density = 2.4, Kr = 1.2 } },
            Strategy = PairingStrategy.InternalFirst,     // 非缺省值，抄漏了才比得出来
            InternalCumCapM3 = new[] { 0.0, 1e5, 2e5 },
            HaulProvider = (_, _) => 4.25,
        };
        var newSlots = new List<DumpSlot> { new() { DumpName = "南排" } };
        var ddst = CoupledMinePlanner.CloneDumpForTest(dsrc, newSlots);

        Assert.Same(newSlots, ddst.Slots);                 // Slots 是**故意换成新的那批**
        Assert.Same(dsrc.Materials, ddst.Materials);
        Assert.Equal(dsrc.Strategy, ddst.Strategy);
        Assert.Same(dsrc.InternalCumCapM3, ddst.InternalCumCapM3);
        // ★ 曾漏的就是它：`di.HaulProvider = …` 在 if (hasU) 里面，没工作线时根本不执行
        Assert.NotNull(ddst.HaulProvider);
        Assert.Equal(4.25, ddst.HaulProvider!(1, newSlots[0]), 6);

        var dfields = typeof(DumpAllocationInput).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).ToList();
        var dmissed = dfields.Where(f => f.Name != nameof(DumpAllocationInput.Slots)
                                      && f.GetValue(ddst) == null && f.GetValue(dsrc) != null)
                             .Select(f => f.Name).ToList();
        Assert.Empty(dmissed);
        _out.WriteLine($"CloneDump：{dfields.Count} 个字段无一为空（Slots 故意换新），HaulProvider 可调用");

        static IEnumerable<string> Flat(System.Collections.IEnumerable e)
        {
            foreach (var x in e)
                if (x is System.Collections.IEnumerable inner and not string)
                    foreach (var y in Flat(inner)) yield return y;
                else yield return Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        static string Show(object? o) => o is System.Collections.IEnumerable e and not string
            ? "[" + string.Join(",", Flat(e).Take(6)) + "]" : Convert.ToString(o) ?? "null";
    }
}
