// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/DumpAllocationTests.cs（逐行对应；仅命名空间适配）
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
/// G14 组 · 采排配对（把逐月剥离量摊到排土位置上）。
/// <para>合成排土场：内排 6 级 × 5 带 × 8 万m³占容（第 4 月起启用）+ 外排 6 级 × 5 带 × 12 万m³。
/// 真值可手算：库容、Kr 换算、自下而上的顺序都是确定的。</para>
/// </summary>
public sealed class DumpAllocationTests
{
    private readonly ITestOutputHelper _out;
    public DumpAllocationTests(ITestOutputHelper o) => _out = o;

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
        // Kylin：原 BlockModelLib.BlockModel(单元格数据列) → InclineBlockSource(块列表 + 属性数组)，同一几何同一属性
        var blocks = new List<BlockModel.Block>(); var cA = new List<double>(); var cB = new List<double>();
        for (int k = 0; k < NZ; k++)
        {
            double cz = k * CELL + CELL * 0.5;
            bool a = cz >= A_FLOOR && cz <= A_ROOF, b = cz >= B_FLOOR && cz <= B_ROOF;
            for (int j = 0; j < NY; j++) for (int i = 0; i < NX; i++)
            {
                blocks.Add(new BlockModel.Block { X = i * CELL + CELL * 0.5, Y = j * CELL + CELL * 0.5, Z = cz, Size = CELL, Grade = a ? 1 : b ? 2 : 0 });
                cA.Add(a ? 1.0 : 0.0); cB.Add(b ? 1.0 : 0.0);
            }
        }
        var m = new InclineBlockSource { Name = "SYNTH", Blocks = blocks, Attrs = new Dictionary<string, double[]> { ["cA"] = cA.ToArray(), ["cB"] = cB.ToArray() } };
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
        var q = new double[12]; for (int i = 0; i < 12; i++) q[i] = 15;
        var r = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM,
            CoalTargetWt = q, LookaheadMonths = 3, RecoveryTotalWt = 45,
            StartInSteadyState = true,
        });
        Assert.True(r.Success, r.Error);
        return r;
    }

    /// <summary>合成排土场：内排（近，第 4 月起启用）+ 外排（远，一直可用）。</summary>
    private static List<DumpSlot> Slots(double innerPerSlot = 8e4, double outerPerSlot = 12e4,
                                        int innerFrom = 4, int levels = 6, int bands = 5)
    {
        var list = new List<DumpSlot>();
        for (int lv = 0; lv < levels; lv++)
            for (int b = 0; b < bands; b++)
            {
                list.Add(new DumpSlot
                {
                    DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = innerPerSlot,
                    IsInternal = true, AvailableFromMonth = innerFrom, HaulKm = 1.2,
                });
                list.Add(new DumpSlot
                {
                    DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = outerPerSlot,
                    IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.8,
                });
            }
        return list;
    }

    /// <summary>逐层间标签的物料：覆岩=风化岩 · 层间=硬岩 · 底板下=硬岩 · 夹矸=夹矸（数值取自采运排文档）。</summary>
    private static GapMaterial[] Materials(int seamCount = 2, string[]? topsoilOnly = null)
    {
        var arr = new GapMaterial[GapCode.Count(seamCount)];
        for (int g = 0; g < arr.Length; g++) arr[g] = new GapMaterial { Name = $"标签{g}", Density = 2.50, Kr = 1.15 };
        arr[GapCode.Overburden] = new GapMaterial
        {
            Name = "覆岩(风化岩)", Density = 2.10, Kr = 1.12,
            AllowedDumps = topsoilOnly ?? Array.Empty<string>(),
        };
        arr[1] = new GapMaterial { Name = "层间(硬岩)", Density = 2.50, Kr = 1.15 };
        arr[GapCode.Underburden(seamCount)] = new GapMaterial { Name = "底板下(硬岩)", Density = 2.50, Kr = 1.15 };
        for (int i = 0; i < seamCount; i++)
            arr[GapCode.Parting(seamCount, i)] = new GapMaterial { Name = $"层{i}夹矸", Density = 2.20, Kr = 1.13 };
        return arr;
    }

    // ── G14 · 配对跑通 + 守恒 ───────────────────────────────────────────────

    /// <summary>G14 配对跑得通，且逐月/合计的实方与月度计划<b>逐笔对得上</b>（不许多也不许少）。</summary>
    [Fact]
    public void G14_Pairing_ConservesVolume()
    {
        var sched = Schedule(Profile());
        var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(), Materials = Materials() });
        Assert.True(r.Success, r.Error);
        _out.WriteLine(DumpAllocator.FlowMatrix(r, sched));

        for (int i = 0; i < sched.Months.Count; i++)
        {
            double planned = sched.Months[i].RockM3;
            double placed = r.Months[i].InSituM3 + r.Months[i].UnplacedM3;
            Assert.Equal(planned, placed, 0);          // 排下的 + 排不下的 = 计划剥离的
        }
        Assert.Equal(sched.TotalRockM3, r.TotalInSituM3 + r.TotalUnplacedM3, 0);
    }

    /// <summary>
    /// G14e <b>配对结果自己也要过五类自洽</b>（守恒 / 有序 / 非有限 / 范围引用 / 完备）。
    ///
    /// <para><b>为什么不能只靠契约层校</b>：<c>ScheduleDeriver</c> 是<b>直接拿
    /// <see cref="DumpAllocationResult"/> 打分</b>的（运输功、内排率两维），
    /// <b>根本不经过 `MinePlanExport.Validate`</b>。配对一歪，比选表就照着歪的数排名，
    /// 而契约层的校核那时还没发生。</para>
    ///
    /// <para>其中两条是<b>踩过的坑的回归网</b>：剩余库容为负（「越排越多」）· 运输功 ±∞。</para>
    /// </summary>
    [Fact]
    public void G14e_AllocationResult_PassesTheFiveClassSelfCheck()
    {
        var sched = Schedule(Profile());
        DumpAllocationResult Run() => DumpAllocator.Allocate(
            sched, new DumpAllocationInput { Slots = Slots(), Materials = Materials() });

        var clean = Run().Validate();
        foreach (var x in clean) _out.WriteLine("✗ " + x);
        Assert.Empty(clean);                                  // 干净结果先过

        // ① 守恒：改坏一个月的实方（不动它的流）
        var a = Run(); a.Months[1].InSituM3 *= 1.5;
        Assert.Contains(a.Validate(), x => x.Contains("实方") && x.Contains("逐笔流之和"));

        // ②⑤ 有序/完备：删掉中间一个月
        var b = Run();
        // 夹具必须真有 ≥3 个月，否则底下这段（⑤完备性：月份不连续要抓）**整段跳过**，
        // 而这条判据的名字里就写着"五类自洽"—— 少验一类还全绿，比没写更糟。
        Assert.True(b.Months.Count >= 3, $"夹具只有 {b.Months.Count} 个月，抠不出「月份不连续」—— 这段会空过");
        b.Months.RemoveAt(1);
        Assert.Contains(b.Validate(), x => x.Contains("月份不连续"));

        // ③ 非有限：运距塞 ∞ —— 这正是 SlotU=−∞ 那个真错的形状
        var c = Run();
        c.Months.SelectMany(m => m.Flows).First().HaulKm = double.PositiveInfinity;
        Assert.Contains(c.Validate(), x => x.Contains("非有限值"));
        _out.WriteLine(c.Validate().First(x => x.Contains("非有限值")));

        // ④ 范围：剩余库容为负 —— 「库容越排越多」那个坑的回归网
        var d = Run();
        d.RemainByDump[d.RemainByDump.Keys.First()] = -1e4;
        Assert.Contains(d.Validate(), x => x.Contains("剩余库容") && x.Contains("为负"));
        _out.WriteLine(d.Validate().First(x => x.Contains("剩余库容")));

        // 只改一处 → 只响一条（证明五类不是互相重复）
        var e = Run();
        e.RemainByDump[e.RemainByDump.Keys.First()] = -1;
        Assert.Single(e.Validate());
    }

    /// <summary>
    /// G14b 按层间标签分账的量，合计 = 月总剥离量。
    /// 工程含义：分账是配对的输入，分账对不上总量，后面全错。
    /// </summary>
    [Fact]
    public void G14b_GapBreakdown_SumsToMonthlyTotal()
    {
        var sched = Schedule(Profile());
        foreach (var m in sched.Months)
        {
            Assert.NotEmpty(m.RockByGapM3);
            Assert.Equal(m.RockM3, m.RockByGapM3.Sum(), 0);
        }
        var g0 = sched.Months[0].RockByGapM3;
        _out.WriteLine("首月分账(万m³): " + string.Join(" · ",
            g0.Select((v, i) => (v, i)).Where(x => x.v > 1).Select(x => $"g{x.i}={x.v / 1e4:0.0}")));
    }

    /// <summary>
    /// G14c <b>V实 → V容</b> 换算逐笔正确（占容 = 实方 × Kr），且库容按 V容 扣。
    /// 工程含义：混用口径会让「排得下吗」在数学上无解 —— 三个体积口径是全系统最容易错的地方。
    /// </summary>
    [Fact]
    public void G14c_VolumeConversion_UsesResidualSwellNotLoose()
    {
        var sched = Schedule(Profile());
        var mats = Materials();
        var slots = Slots();
        var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = slots, Materials = mats });
        Assert.True(r.Success, r.Error);
        foreach (var f in r.Months.SelectMany(m => m.Flows))
        {
            Assert.Equal(f.InSituM3 * mats[f.Gap].Kr, f.DumpM3, 6);
            Assert.Equal(f.InSituM3 * mats[f.Gap].Density, f.TonnageT, 6);
        }
        // 总占容 = 总库容 − 期末剩余
        double totalCap = slots.Sum(s => s.CapacityM3);
        double placedDump = r.Months.Sum(m => m.DumpM3);
        Assert.Equal(totalCap - r.RemainByDump.Values.Sum(), placedDump, 0);
    }

    // ── G15 · 三条时空硬约束 ────────────────────────────────────────────────

    /// <summary>
    /// G15 <b>内排启用时机</b>：启用月之前一方都不许进内排。
    /// 工程含义：内排土场必须等采空区形成 —— 时空约束第 1 条。
    /// </summary>
    [Fact]
    public void G15_InternalDump_NotUsedBeforeAvailable()
    {
        var sched = Schedule(Profile());
        var r = DumpAllocator.Allocate(sched, new DumpAllocationInput
        { Slots = Slots(innerFrom: 4), Materials = Materials(), Strategy = PairingStrategy.InternalFirst });
        Assert.True(r.Success, r.Error);
        foreach (var m in r.Months.Where(x => x.Month < 4))
            Assert.True(m.Flows.All(f => !f.IsInternal),
                        $"第{m.Month}月就往内排送了 {m.Flows.Where(f => f.IsInternal).Sum(f => f.InSituM3) / 1e4:0.0}万m³");
        _out.WriteLine("逐月内排率: " + string.Join(" · ", r.Months.Select(m => $"{m.Month}月 {m.InternalRatePct:0}%")));
        Assert.True(r.Months.Any(m => m.Month >= 4 && m.InternalRatePct > 0), "启用后一方都没进内排");
    }

    /// <summary>
    /// G15b <b>排土台阶自下而上</b>：某一级还没填满，就不许往更高一级排。
    /// 工程含义：时空约束第 2 条 —— 同一去向同一时刻只有一个当前可排台阶层在接收。
    /// </summary>
    [Fact]
    public void G15b_DumpBenches_FillBottomUp()
    {
        var sched = Schedule(Profile());
        var slots = Slots();
        var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = slots, Materials = Materials() });
        Assert.True(r.Success, r.Error);
        // 对每个去向：被用过的级必须是从最低一级起、连续的；且除最高的在用级外，其余必须已满
        foreach (var grp in slots.GroupBy(s => s.DumpName))
        {
            var used = grp.Where(s => s.CapacityM3 - s.Remain > 1e-6).ToList();
            if (used.Count == 0) continue;
            int top = used.Max(s => s.Level);
            int bottom = used.Min(s => s.Level);
            Assert.Equal(grp.Min(s => s.Level), bottom);
            for (int lv = bottom; lv < top; lv++)
            {
                double remain = grp.Where(s => s.Level == lv).Sum(s => s.Remain);
                Assert.True(remain <= 1e-6,
                            $"{grp.Key} 第{lv}级还剩 {remain / 1e4:0.0}万m³ 就去排第 {top} 级了 —— 违反自下而上");
            }
            _out.WriteLine($"{grp.Key}: 用到第 {bottom}~{top} 级");
        }
    }

    /// <summary>
    /// G15c <b>允许去向是硬约束</b>：限定只能进某个去向的物料，一方都不许进别处；
    /// 该去向满了必须<b>报排不下</b>，不许悄悄改投。
    /// 工程含义：「表土只能进表土堆场」是复垦资源的规定，改投等于违规。
    /// </summary>
    [Fact]
    public void G15c_AllowedDestination_IsHardNotPreference()
    {
        var sched = Schedule(Profile());
        var mats = Materials(topsoilOnly: new[] { "表土堆场" });     // 覆岩被限定到一个【不存在】的去向
        var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = Slots(), Materials = mats });
        Assert.True(r.Success, r.Error);

        Assert.True(r.Months.SelectMany(m => m.Flows).All(f => f.Gap != GapCode.Overburden),
                    "被限定去向的物料混进了别的排土场 —— 允许去向被当成了偏好");
        Assert.True(r.TotalUnplacedM3 > 0, "去向不存在却报全部排下");
        Assert.False(r.AllPlaced);
        _out.WriteLine($"排不下 {r.TotalUnplacedM3 / 1e4:0.0}万m³");
        Assert.Contains(r.Warnings, w => w.Contains("表土堆场"));
    }

    // ── G16 · 配对策略（第 4 条派生轴）──────────────────────────────────────

    /// <summary>
    /// G16 三种配对策略的方向必须对：运输功最小 ≤ 其余；内排优先的内排率 ≥ 运输功最小。
    /// <para>工程含义：这是第 4 条派生轴。策略之间分不开，那这条轴就是摆设。</para>
    /// </summary>
    [Fact]
    public void G16_PairingStrategies_HaveTheRightDirection()
    {
        var sched = Schedule(Profile());
        var res = new Dictionary<PairingStrategy, DumpAllocationResult>();
        foreach (PairingStrategy st in Enum.GetValues<PairingStrategy>())
        {
            var r = DumpAllocator.Allocate(sched, new DumpAllocationInput
            { Slots = Slots(), Materials = Materials(), Strategy = st });
            Assert.True(r.Success, $"{st}: {r.Error}");
            res[st] = r;
            _out.WriteLine($"{st,-14} 运输功 {r.TotalTransportWorkTKm / 1e4,8:0.0}万t·km"
                         + $" · 内排率 {r.OverallInternalRatePct,5:0.0}%"
                         + $" · 排不下 {r.TotalUnplacedM3 / 1e4:0.0}万m³");
        }
        Assert.True(res[PairingStrategy.MinHaul].TotalTransportWorkTKm
                    <= res[PairingStrategy.LevelCapacity].TotalTransportWorkTKm + 1e-6,
                    "「运输功最小」的运输功竟然不是最小的");
        Assert.True(res[PairingStrategy.InternalFirst].OverallInternalRatePct
                    >= res[PairingStrategy.LevelCapacity].OverallInternalRatePct - 1e-6,
                    "「内排优先」的内排率竟然不比库容均衡高");
        // 至少有两种策略给出不同结果，否则这条轴塌了
        var distinct = res.Values.Select(v => $"{v.TotalTransportWorkTKm:F1}|{v.OverallInternalRatePct:F1}").Distinct().Count();
        Assert.True(distinct > 1, "三种配对策略给出完全相同的结果 —— 这条轴是摆设");
    }

    /// <summary>
    /// G17 库容不够时<b>明说排不下</b>并指出哪个月、多少方，不许摊平或静默丢弃。
    /// 工程含义：排弃能力是硬的。悄悄"排下了"会让下游按不存在的库容排车。
    /// </summary>
    [Fact]
    public void G17_InsufficientCapacity_IsReportedNotSwallowed()
    {
        var sched = Schedule(Profile());
        var r = DumpAllocator.Allocate(sched, new DumpAllocationInput
        { Slots = Slots(innerPerSlot: 2e4, outerPerSlot: 2e4), Materials = Materials() });   // 总库容远远不够
        Assert.True(r.Success, r.Error);
        Assert.False(r.AllPlaced);
        Assert.True(r.TotalUnplacedM3 > 0);
        Assert.NotEmpty(r.Warnings);
        _out.WriteLine($"排不下 {r.TotalUnplacedM3 / 1e4:0.0}万m³；首条告警：{r.Warnings[0]}");
        Assert.Contains("排不下", DumpAllocator.FlowMatrix(r, sched));
        // 库容不许被排超
        foreach (var kv in r.RemainByDump) Assert.True(kv.Value >= -1e-6, $"{kv.Key} 剩余库容为负 —— 排超了");
    }

    /// <summary>面板：源—汇流向矩阵，供填验收表。</summary>
    [Fact]
    public void G_PairingDashboard()
    {
        var sched = Schedule(Profile());
        foreach (PairingStrategy st in Enum.GetValues<PairingStrategy>())
        {
            _out.WriteLine($"══════ 配对策略：{st} ══════");
            var r = DumpAllocator.Allocate(sched, new DumpAllocationInput
            { Slots = Slots(), Materials = Materials(), Strategy = st });
            _out.WriteLine(r.Success ? DumpAllocator.FlowMatrix(r, sched) : "失败：" + r.Error);
        }
    }
}
