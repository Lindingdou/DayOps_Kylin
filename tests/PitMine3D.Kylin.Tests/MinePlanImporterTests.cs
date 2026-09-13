// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/MinePlanImporterTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
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
/// I 组 · 内核导出 → 短期月度计划的<b>适配层</b>（`MinePlanImporter`）。
///
/// <para>这一组判的是<b>跨模块的账对不对得上</b>：
/// <c>MonthPeriod</c> 的采出量/剥离量/剥采比在 <c>HasFlows</c> 时全是<b>按流派生</b>的，
/// 所以只要流建对了那几个数自然就对——但"建对"要验，而且 ρ/Kr 两边各算各的是最难查的一类错。</para>
///
/// <para><b>不走反射</b>：`PlanLib.csproj` 直接引用 `MineAssLib`，这里是普通类型转换，
/// 编译期就能发现字段改名。TaskLib 那条 `ShortTermLink` 反射桥读的是 PlanLib 自己的月计划，
/// 这里填好了那条桥一行不用改。</para>
/// </summary>
[Collection("ShortTermSchemeStore")]   // I4c 反射碰全局确定簿 —— 与 S 组串行，别并发读改
public sealed class MinePlanImporterTests
{
    private readonly ITestOutputHelper _out;
    public MinePlanImporterTests(ITestOutputHelper o) => _out = o;

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

    /// <summary>物料参数按【下游目录同值】填 —— 这是正常情况；I5 那条判据专门造不同值。</summary>
    private static GapMaterial[] Materials(double weatheredDensity = 2.10, double rockKr = 1.15)
    {
        var a = new GapMaterial[GapCode.Count(2)];
        for (int g = 0; g < a.Length; g++)
            a[g] = new GapMaterial { Name = $"标签{g}", Code = PlanMaterialCatalog.Rock, Density = 2.50, Kr = rockKr };
        a[GapCode.Overburden] = new GapMaterial
        { Name = "覆岩", Code = PlanMaterialCatalog.Weathered, Density = weatheredDensity, Kr = 1.12 };
        a[1] = new GapMaterial
        { Name = "层间", Code = PlanMaterialCatalog.Interburden, Density = 2.20, Kr = 1.13 };
        return a;
    }

    private static List<DumpSlot> Slots()
    {
        var l = new List<DumpSlot>();
        for (int lv = 0; lv < 6; lv++)
            for (int b = 0; b < 5; b++)
            {
                l.Add(new DumpSlot { DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = 10e4, IsInternal = true, AvailableFromMonth = 4, HaulKm = 1.2 });
                l.Add(new DumpSlot { DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = 20e4, IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.8 });
            }
        return l;
    }

    private static MinePlanExport Export(GapMaterial[]? mats = null, List<DumpSlot>? slots = null)
    {
        var rock = Profile();
        var q = new double[12]; for (int i = 0; i < 12; i++) q[i] = 8;
        var s = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = 24, StartInSteadyState = true,
        });
        Assert.True(s.Success, s.Error);
        var d = DumpAllocator.Allocate(s, new DumpAllocationInput
        { Slots = slots ?? Slots(), Materials = mats ?? Materials() });
        Assert.True(d.Success, d.Error);
        return MinePlanExport.Build(s, rock, d);
    }

    // ── I1 · 派生量必须对得上 ───────────────────────────────────────────────

    /// <summary>
    /// I1 只填流，<b>按流派生</b>的采出量/剥离量与导出表逐月吻合。
    ///
    /// <para>工程含义：这一条成立，"几何算的"和"经验形状编的"两个剥采比来源就不会打架 ——
    /// 因为标量根本不是填进去的，是流派生的。</para>
    ///
    /// <para><b>容差按 PlanLib 自己的取整粒度定，不是拍的</b>：
    /// <c>MonthPeriod</c> 把 <c>CoalWanT</c> 四舍五入到 <b>0.1 万t</b>、<c>StripWanM3</c> 到
    /// <b>1 万m³</b>，<b>然后才相除</b>算剥采比。所以小量级下剥采比会有约 1% 的口径差
    /// （实测 0.741 → 0.75）。这不是错，是它给报表定的显示粒度 ——
    /// 但**拿导出表的剥采比和月计划表的剥采比逐位对，会对不上**，得知道是这个原因。</para>
    /// </summary>
    [Fact]
    public void I1_DerivedScalars_MatchTheExport()
    {
        var e = Export();
        var r = MinePlanImporter.Import(e);
        Assert.True(r.Success, r.Error);
        _out.WriteLine(MinePlanImporter.Summary(r));
        foreach (var i in r.Issues) _out.WriteLine("  " + i);

        const double coalGrain = 0.05;   // CoalWanT 取整到 0.1 ⇒ 半格
        const double stripGrain = 0.5;   // StripWanM3 取整到 1  ⇒ 半格
        double worstRatioPct = 0;

        Assert.Equal(e.Months.Count, r.Months.Count);
        for (int k = 0; k < r.Months.Count; k++)
        {
            var mp = r.Months[k]; var em = e.Months[k];
            Assert.True(mp.HasFlows, $"第{em.Month}月一笔流都没有");
            Assert.True(Math.Abs(em.CoalWanT - mp.CoalWanT) <= coalGrain,
                        $"第{em.Month}月采出 {mp.CoalWanT} vs 导出 {em.CoalWanT}（超出 0.1万t 取整粒度）");
            double wantStrip = em.StripWanM3 - em.UnplacedWanM3;
            Assert.True(Math.Abs(wantStrip - mp.StripWanM3) <= stripGrain,
                        $"第{em.Month}月剥离 {mp.StripWanM3} vs 导出 {wantStrip}（超出 1万m³ 取整粒度）");

            // 剥采比：由两个已取整的数相除，误差按它们各自的粒度传播
            double bound = em.CoalWanT > 1e-6
                ? (stripGrain / em.CoalWanT + wantStrip * coalGrain / (em.CoalWanT * em.CoalWanT)) * 1.05
                : double.MaxValue;
            double diff = Math.Abs(em.Ratio - mp.Ratio);
            Assert.True(diff <= bound + 0.01,
                        $"第{em.Month}月剥采比 {mp.Ratio} vs 导出 {em.Ratio:0.000}，差 {diff:0.000} 超出取整传播上限 {bound:0.000}");
            if (em.Ratio > 1e-9) worstRatioPct = Math.Max(worstRatioPct, diff / em.Ratio * 100);
        }
        _out.WriteLine($"剥采比因取整产生的最大口径差 {worstRatioPct:0.0}%（量级越小越明显；这是 PlanLib 的显示粒度，不是算错）");
        Assert.True(r.Executable, "全绿的导出却判成不可执行：" + string.Join(" / ", r.Issues));
    }

    /// <summary>
    /// I1b 内/外排派生也要对：内排率 ≥50% 的月份 <c>Dump=Internal</c>。
    /// 工程含义：下游图表按这个二值分色，反了整张图就错了。
    /// </summary>
    [Fact]
    public void I1b_DumpMode_FollowsTheFlows()
    {
        var e = Export();
        var r = MinePlanImporter.Import(e);
        Assert.True(r.Success, r.Error);
        for (int k = 0; k < r.Months.Count; k++)
        {
            var want = e.Months[k].InternalRatePct >= 50 ? DumpMode.Internal : DumpMode.External;
            Assert.Equal(want, r.Months[k].Dump);
        }
        _out.WriteLine("逐月内外排: " + string.Join(" ", r.Months.Select(m => m.Dump == DumpMode.Internal ? "内" : "外")));
    }

    // ── I2 · 三条对账 ───────────────────────────────────────────────────────

    /// <summary>
    /// I2 <b>ρ/Kr 与下游目录对不上就记 Issue</b>。
    /// <para>工程含义：两边各算各的是最难查的一类错——库容按一套扣、配车按另一套算，
    /// 账面全都"达标"。把覆岩容重故意填成 2.60（目录里是 2.10）验它抓不抓得住。</para>
    /// </summary>
    [Fact]
    public void I2_CoefficientMismatch_IsReported()
    {
        var clean = MinePlanImporter.Import(Export());
        Assert.DoesNotContain(clean.Issues, i => i.Code == "I5");

        var r = MinePlanImporter.Import(Export(Materials(weatheredDensity: 2.60)));
        Assert.True(r.Success, r.Error);
        var i5 = r.Issues.Where(i => i.Code == "I5").ToList();
        foreach (var i in i5) _out.WriteLine("  " + i);
        Assert.NotEmpty(i5);
        Assert.Contains(i5, i => i.Text.Contains("容重") && i.Text.Contains("weathered"));
        Assert.False(i5.Any(i => i.Blocking), "系数对不上是要核对的账，不该直接判死计划");
    }

    /// <summary>
    /// I2b 物料码认不出来 → 退回 rock 并<b>记 Issue</b>，不静默。
    /// 工程含义：静默退回硬岩会让表土进排土场（Ks/Kr/允许去向全按硬岩算）。
    /// </summary>
    [Fact]
    public void I2b_UnknownMaterialCode_FallsBackLoudly()
    {
        var mats = Materials();
        mats[GapCode.Overburden] = new GapMaterial { Name = "覆岩", Code = "不存在的码", Density = 2.10, Kr = 1.12 };
        var r = MinePlanImporter.Import(Export(mats));
        Assert.True(r.Success, r.Error);
        var i4 = r.Issues.Where(i => i.Code == "I4").ToList();
        _out.WriteLine(i4.Count > 0 ? i4[0].ToString() : "(无)");
        Assert.NotEmpty(i4);
        Assert.Contains("不存在的码", i4[0].Text);
        Assert.All(r.Months.SelectMany(m => m.Flows).Where(f => !f.IsOre),
                   f => Assert.NotEqual("不存在的码", f.MaterialCode));
    }

    /// <summary>
    /// I2c 导出侧不自洽 / 判定不可行 / 缺采出侧 → <b>阻断</b>，不许当成可执行计划。
    /// </summary>
    [Fact]
    public void I2c_BadExport_IsBlockedNotSilentlyAccepted()
    {
        // ① 缺采出侧
        var e1 = Export(); e1.Coal.Clear();
        var r1 = MinePlanImporter.Import(e1);
        Assert.False(r1.Executable);
        Assert.Contains(r1.Issues, i => i.Code == "I3" && i.Blocking);

        // ② 导出侧自己判定不可行
        var e2 = Export(); e2.Feasible = false;
        var r2 = MinePlanImporter.Import(e2);
        Assert.False(r2.Executable);
        Assert.Contains(r2.Issues, i => i.Code == "I2" && i.Blocking);

        // ③ 被改坏的数
        var e3 = Export(); e3.Months[2].StripWanM3 *= 1.4;
        var r3 = MinePlanImporter.Import(e3);
        Assert.False(r3.Executable);
        Assert.Contains(r3.Issues, i => i.Blocking);
        foreach (var i in r3.Issues.Where(x => x.Blocking).Take(3)) _out.WriteLine("  " + i);

        // ④ 版本不认识
        var e4 = Export(); e4.SchemaVersion = 99;
        var r4 = MinePlanImporter.Import(e4);
        Assert.False(r4.Success);
        Assert.Contains("版本不匹配", r4.Error);
    }

    // ── I3 · 顺带闭合的东西 ─────────────────────────────────────────────────

    /// <summary>
    /// I3 <b>备采储量由几何算出来</b>，不再靠录入 —— 闭合「短期生产计划_设计」Phase2 待办 #2
    /// （"现三量为录入量；接块体/工程位置算备采储量"）。
    /// </summary>
    [Fact]
    public void I3_PreparedReserve_ComesFromGeometryNotDataEntry()
    {
        var e = Export();
        var r = MinePlanImporter.Import(e);
        Assert.True(r.Success, r.Error);
        Assert.True(r.PreparedReserveWanT > 0, "备采储量算出来是 0");
        Assert.Equal(e.Months.Min(m => m.PreparedWanT), r.PreparedReserveWanT, 6);
        _out.WriteLine($"备采储量（几何）= {r.PreparedReserveWanT:0.0} 万t"
                     + $"，最低保有 {e.Months.Min(m => m.PreparedMonths):0.0} 个月");
    }

    /// <summary>I3b 起始月可平移（年中接续），月标签跟着走。</summary>
    [Fact]
    public void I3b_StartMonth_ShiftsLabels()
    {
        var r = MinePlanImporter.Import(Export(), startMonth: 7);
        Assert.True(r.Success, r.Error);
        Assert.Equal(7, r.Months[0].Month);
        Assert.Equal(18, r.Months[^1].Month);
        Assert.Equal("M07", r.Months[0].Label);
    }

    // ── I4 · 放坡参数接通三维模拟 ───────────────────────────────────────────
    //
    // ⚠ 这个文件里 `I<n>` 有【两套】互不相干的编号，别看混：
    //   · **判据名**（方法名 `I1/I2/I3/I4…`）—— 验收册 §5.1 那一行的编号；
    //   · **Issue 码**（字符串 `"I1".."I8"`）—— `MinePlanImporter` 报给用户的问题编号。
    // 所以下面这条判据叫 I4，钉的却是 Issue **I8**；而判据 I2b 钉的是 Issue **I4**。
    // 查代码时：`void I4` 找判据，`"I4"` 找 Issue 码。

    /// <summary>
    /// I4 <b>放坡参数写进短期方案，三维层体的真台阶就通了</b>。
    ///
    /// <para>TaskLib 的 <c>SimModel.LoadSlope</c> 用<b>反射按属性名</b>从 <c>ShortTermPlan</c> 上软读
    /// α/W/β（候选名 <c>BenchFaceAngleDeg</c> / <c>BermWidthM</c> / <c>OverallSlopeAngleDeg</c>），
    /// 读不到就降级成「层体按<b>垂直壁</b>建」。这正是 `采运排一体化` §十 已知边界 1 说的
    /// 「**PlanLib 侧加上字段后会自动接通**」—— 所以这条判据钉的是<b>属性名对不对得上</b>，
    /// 名字写错了 TaskLib 那边照样降级，而且<b>不会报错</b>。</para>
    /// </summary>
    [Fact]
    public void I4_SlopeParams_ReachTheShortTermPlanUnderTheNamesTaskLibReads()
    {
        var e = Export();
        e.Geometry = new ExportGeometry
        {
            RockBenchHeightM = 15, RockFaceDeg = 65, CoalFaceDeg = 65,
            MinBermM = 40, WorkingSlopeDeg = 18, Source = "设计参数",
        };
        var plan = new ShortTermPlan();
        var r = MinePlanImporter.Import(e);
        int n = MinePlanImporter.ApplyGeometry(e, plan, r);
        Assert.Equal(3, n);
        Assert.Equal(65, plan.BenchFaceAngleDeg, 6);
        Assert.Equal(40, plan.BermWidthM, 6);
        Assert.Equal(18, plan.OverallSlopeAngleDeg, 6);
        Assert.Equal(15, plan.BenchHeightM, 6);

        // ★ 按 TaskLib 实际用的那组属性名反射一遍 —— 名字对不上它只会静默降级
        foreach (var name in new[] { "BenchFaceAngleDeg", "BermWidthM", "OverallSlopeAngleDeg" })
        {
            var pi = typeof(ShortTermPlan).GetProperty(name);
            Assert.True(pi != null, $"TaskLib 按名字 「{name}」 反射读放坡参数，ShortTermPlan 上没有这个属性");
            double v = Convert.ToDouble(pi!.GetValue(plan));
            Assert.True(v > 0, $"「{name}」 反射读出来是 {v}");
            _out.WriteLine($"  反射 {name} = {v}");
        }
    }

    /// <summary>
    /// I4b <b>没有放坡参数时不许塞缺省角度</b>，并且要说清后果。
    /// <para>塞个缺省角度进去等于拿猜出来的几何冒充工程量 —— 而 TaskLib 本来就会
    /// 「按垂直壁建 + 写清原因」，那才是对的降级。</para>
    /// </summary>
    [Fact]
    public void I4b_MissingSlopeParams_AreLeftEmptyNotGuessed()
    {
        var e = Export();                        // 没给 Geometry
        var plan = new ShortTermPlan();
        var r = MinePlanImporter.Import(e);
        int n = MinePlanImporter.ApplyGeometry(e, plan, r);
        Assert.Equal(0, n);
        Assert.Equal(0, plan.BenchFaceAngleDeg, 6);
        Assert.Equal(0, plan.BermWidthM, 6);
        Assert.Contains(r.Issues, i => i.Code == "I8" && i.Text.Contains("垂直壁"));
        _out.WriteLine(r.Issues.First(i => i.Code == "I8").ToString());
    }

    /// <summary>
    /// I4c <b>把 TaskLib 反射读 PlanLib 的整张面钉住</b>（不只是放坡那三个字段）。
    ///
    /// <para>三维动态模拟这条链**全程是软反射**，一处对不上就掉进 <c>catch{}</c> 静默降级：
    /// <c>SimBuilder.ReadMonthPeriods</c> 按<b>类型全名 + 程序集名</b>找确定簿
    /// （<c>"ShortTermSchemeStore, PlanLib"</c>）、按属性名取
    /// <c>Confirmed</c> / <c>Name</c> / <c>Months</c>，再逐月取 9 个属性；
    /// <c>SimModel.Load</c> 另取 <c>BenchHeightM</c> / <c>WorkLineLenM</c>。</para>
    ///
    /// <para>改名、挪命名空间、换程序集名 —— 编译全过，判据全绿，**只有三维模拟悄悄退回外推**。
    /// 所以这条判据<b>照 TaskLib 的写法原样反射一遍</b>：找不到就红在这里，而不是红在现场。
    /// （名单来源：`SimBuilder.cs:327-345` 与 `SimModel.cs:173-182`。）</para>
    /// </summary>
    [Fact]
    public void I4c_TaskLibReflectionSurface_OverPlanLib_IsPinned()
    {
        // ① 类型全名 + 程序集名 —— TaskLib 就是拿这个字符串找的
        var store = Type.GetType("PitMine3D.Kylin.Cad.Plan.ShortTermSchemeStore, PitMine3D.Kylin");   // Kylin：SimBuilder/SimModel 按这个字符串找
        Assert.True(store != null,
            "SimBuilder/SimModel 按 \"PitMine3D.Kylin.Cad.Plan.ShortTermSchemeStore, PitMine3D.Kylin\" 找确定簿；" +
            "类型全名或程序集名一变，三维模拟静默退回外推");

        // ② 静态确定簿属性
        var confirmed = store!.GetProperty("Confirmed",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.True(confirmed != null, "ShortTermSchemeStore.Confirmed（public static）没了");
        Assert.True(typeof(ShortTermPlan).IsAssignableFrom(confirmed!.PropertyType),
            $"Confirmed 的类型是 {confirmed.PropertyType.Name}，不是 ShortTermPlan —— 下面那批属性名就白钉了");

        // ③ 计划对象上的属性（SimBuilder 取 Name/Months；SimModel 取 H/L 与放坡三件套）
        foreach (var name in new[] { "Name", "Months", "BenchHeightM", "WorkLineLenM",
                                     "BenchFaceAngleDeg", "BermWidthM", "OverallSlopeAngleDeg" })
            Assert.True(typeof(ShortTermPlan).GetProperty(name) != null,
                $"TaskLib 按名字 「{name}」 反射读短期方案，ShortTermPlan 上没有");

        // ④ 逐月行上的属性（SimBuilder.ReadMonthPeriods）
        foreach (var name in new[] { "Label", "CoalWanT", "StripWanM3", "AdvanceM",
                                     "Workdays", "FlagText", "DumpText", "ActiveFace" })
            Assert.True(typeof(MonthPeriod).GetProperty(name) != null,
                $"TaskLib 按名字 「{name}」 反射读逐月行，MonthPeriod 上没有");

        // ⑤ 反射真的取得到值 —— 光有属性名不够，还得确认导入填的数走得通这条路
        var e = Export();
        var r = MinePlanImporter.Import(e);
        Assert.True(r.Success, r.Error);
        var plan = new ShortTermPlan { Name = "反射体检" };
        foreach (var m in r.Months) plan.Months.Add(m);
        MinePlanImporter.ApplyGeometry(e, plan, r);

        object? m0 = ((System.Collections.IEnumerable)typeof(ShortTermPlan)
                        .GetProperty("Months")!.GetValue(plan)!).Cast<object>().FirstOrDefault();
        Assert.True(m0 != null, "反射取到的 Months 是空的");
        double adv = Convert.ToDouble(m0!.GetType().GetProperty("AdvanceM")!.GetValue(m0));
        double coal = Convert.ToDouble(m0.GetType().GetProperty("CoalWanT")!.GetValue(m0));
        Assert.True(adv > 0, $"反射读出的首月推进是 {adv} —— SimBuilder 的「计划自带推进」会一直显示不出来");
        Assert.True(coal > 0, $"反射读出的首月煤量是 {coal}");
        _out.WriteLine($"  反射链通：Confirmed:{confirmed.PropertyType.Name} → Months[0] " +
                       $"推进 {adv:0.##}m · 煤 {coal:0.##}万t");
    }

    /// <summary>面板：导入摘要 + 全部 Issue + 逐月表头几行。</summary>
    [Fact]
    public void I_ImportDashboard()
    {
        var e = Export();
        var r = MinePlanImporter.Import(e);
        _out.WriteLine(MinePlanImporter.Summary(r));
        _out.WriteLine("指纹: " + e.Provenance);
        foreach (var i in r.Issues) _out.WriteLine("  " + i);
        _out.WriteLine("月 | 采出万t | 剥离万m³ | 剥采比 | 内外排 | 流笔数 | 推进m");
        foreach (var m in r.Months.Take(6))
            _out.WriteLine($"{m.Month,2} | {m.CoalWanT,7:0.0} | {m.StripWanM3,8:0} | {m.Ratio,6:0.00} | "
                         + $"{(m.Dump == DumpMode.Internal ? "内排" : "外排"),6} | {m.Flows.Count,6} | {m.AdvanceM,6:0.0}");
    }
}
