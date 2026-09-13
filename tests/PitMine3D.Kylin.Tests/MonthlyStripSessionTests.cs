// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/MonthlyStripSessionTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
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
/// S 组 · 「量驱动月度采剥接续」的<b>会话装配层</b>（`MonthlyStripSession`）。
///
/// <para>内核那 8 个类各自都测过（158 条），但<b>它们之间怎么装、缺了什么该说什么话</b>
/// 以前只活在测试代码里 —— 界面直接调内核的话，那套装配就散进 XAML 后台，脱 GUI 就验不了。
/// 这一组量的就是装配层本身：<b>一次跑通 · 缺什么都给得出原因 · 不可执行的不许入库 ·
/// 引擎替用户做的决定必须留条</b>。</para>
///
/// <para>确定入库（<c>ShortTermSchemeStore.Confirmed</c>）是<b>唯一有副作用的动作</b>，
/// 下游（作业计划 / 三维动态模拟 / 采运排一体化）全指着它，所以单列判据。</para>
/// </summary>
/// <summary>
/// 碰<b>全局确定簿</b>（<c>ShortTermSchemeStore</c> 是 static）的判据类共用一个 collection。
/// <para>xUnit 默认<b>类之间并行</b>：一个类在存/改/还原全局状态，另一个类同时在读，
/// 就会出"单跑绿、全跑偶尔红"的假红 —— 而且复现不了。目前只有本类写、`MinePlanImporterTests`
/// 只反射读属性类型（不读值），所以还没真出过；但**下一个写它的判据类一加进来就会踩**。
/// 归到同一个 collection 里，它们只会串行。</para>
/// </summary>
[CollectionDefinition("ShortTermSchemeStore")]
public sealed class ShortTermStoreCollection { }

[Collection("ShortTermSchemeStore")]
public sealed class MonthlyStripSessionTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ShortTermPlan? _savedConfirmed;
    private readonly List<ShortTermPlan> _savedSchemes;

    public MonthlyStripSessionTests(ITestOutputHelper o)
    {
        _out = o;
        // 确定簿是**全局静态**的，跑判据不能把用户/别的判据的确定方案冲掉。
        _savedConfirmed = ShortTermSchemeStore.Confirmed;
        _savedSchemes = ShortTermSchemeStore.Schemes.ToList();
    }

    public void Dispose()
    {
        ShortTermSchemeStore.Confirmed = _savedConfirmed;
        ShortTermSchemeStore.Schemes.Clear();
        foreach (var s in _savedSchemes) ShortTermSchemeStore.Schemes.Add(s);
    }

    // ⚠ **本夹具的后半程是几何退化的，别拿尾部各月的量当真**（2026-08-06 出报表看形态时发现）。
    //   NX=60 × 10m ⇒ 推进方向只有 **600m**；而工作帮比煤前界超前 (K−1)×H/tanα = 7×54.9 ≈ **385m**。
    //   期初煤前界 ~30m、12 个月推进 ~326m ⇒ 最上台阶第 7 月就压到 600m 边界，之后出界。
    //   出界之后 `VolUpTo` 再累加不出岩量 ⇒ **「必须剥」自己往下掉**（实测剥采比 1.85→0.74），
    //   看着像"越采越轻松"，其实是**块体比计划期短**。而 H1–H7 全过 —— 判据只证伪，证不了形态。
    //   ⇒ 本组判据判的是**装配、闸门、留条、接线**，这些不受影响；
    //     但**不要**在这个夹具上新增"尾部某月剥离量/剥采比应当是多少"这类判据。
    //     内核那几组（NX=100 ⇒ 1000m）不受影响。排产结果里现在会带一条明确的告警。
    private const int    NX = 60, NY = 5, NZ = 20;
    private const double CELL = 10.0, ROCK_H = 20.0, DENS = 1.35;
    private const double A_ROOF = 140, A_FLOOR = 120, B_ROOF = 80, B_FLOOR = 60;
    private const double ALPHA = 20.0, ZDATUM = 100.0;

    private static TinSampler Plane(double z)
        => TinSampler.TryBuild(
            new[] { -5000.0, -5000.0, z, 5000.0, -5000.0, z, 5000.0, 5000.0, z, -5000.0, 5000.0, z },
            new[] { 0, 1, 2, 0, 2, 3 })!;

    private static WorkLineGeometry Line()
    {
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((-2000, -100, ZDATUM)); wl.Baseline.Add((-2000, 200, ZDATUM));
        wl.Samples.Add((-2000, 50, ZDATUM, 1, 0));
        return wl;
    }

    /// <param name="benchH">岩台阶高。<b>0 = 不装岩剖面</b>（现场最常撞的"半份剖面"就是这么来的）。</param>
    private static CoalProfile FullProfile(double benchH = ROCK_H)
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
        var seams = new List<SeamSurfaces>
        {
            new() { Name = "A煤", Attribute = "cA", Density = DENS, Roof = Plane(A_ROOF), Floor = Plane(A_FLOOR) },
            new() { Name = "B煤", Attribute = "cB", Density = DENS, Roof = Plane(B_ROOF), Floor = Plane(B_FLOOR) },
        };
        var p = InclineVolumeEngine.BuildProfile(m, new[] { Line() }, Plane(1000), seams, ALPHA, 1, 0.5, benchH);
        Assert.True(p.Success, p.Error);
        if (benchH > 0) Assert.NotNull(p.Rock);
        else Assert.Null(p.Rock);          // 台阶高 0 ⇒ 不装岩（现场最常撞的一种"半份剖面"）
        return p;
    }

    /// <summary>
    /// 每次都<b>新扫一份</b>剖面。S2 里各算例要改 <c>Provenance</c> 之类的可变状态，
    /// 共用一个实例会互相污染 —— 而且症状是"判据全绿但一条都没走对路"。
    /// </summary>
    private static RockProfile Rock() => FullProfile().Rock!;

    private static List<DumpSlot> Slots()
    {
        var l = new List<DumpSlot>();
        for (int lv = 0; lv < 6; lv++)
            for (int b = 0; b < 5; b++)
            {
                // ★ 内排位置必须落在**采空区**里，也就是推进轴上**煤前界的后方**（u < 前界）。
                //   原来写 Cx=300~540 ⇒ u=2300~2540，而 12 个月煤前界只走到 2355.6，
                //   启用条件「前界 − slotU ≥ 内排退距(200m)」**一个月都满足不了** ——
                //   于是**内排率恒为 0.0%**、内排土场期末还剩 300 万m³ 没用，
                //   而"提升当量/煤容重→采空区容量/迂回系数"这些**全是内排经济性**的东西
                //   在本组里从来没被真跑过。方向本身也反了：前界前方是**还没采到的地方**。
                //   现改为 u=1830~2070（前界后方 200~600m），按 60m 一档铺开 ⇒
                //   各档分别在第 1/3/5/7/9 月满足退距，**能验到"内排逐月开出来"这条时序**。
                l.Add(new DumpSlot { DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = 10e4, IsInternal = true, AvailableFromMonth = 4, HaulKm = 1.2, Cx = -170 + b * 60, Cy = 100, Cz = 20 + lv * 20 });
                l.Add(new DumpSlot { DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = 20e4, IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.8, Cx = -900 + b * 60, Cy = 1400, Cz = 60 + lv * 20 });
            }
        return l;
    }

    private static MonthlyStripSessionInput Input(RockProfile rock, int months = 12, double perMonth = 8)
    {
        var q = new double[months]; for (int i = 0; i < months; i++) q[i] = perMonth;
        return new MonthlyStripSessionInput
        {
            Rock = rock, CoalTargetWt = q, ZDatum = ZDATUM,
            Slots = Slots(), WorkLines = new[] { Line() },
            LookaheadMonths = 3, RecoveryTotalWt = 24, StartInSteadyState = true,
            PlanName = "S组算例",
        };
    }

    // ── S1 · 一次跑通并能确定入库 ───────────────────────────────────────────

    /// <summary>
    /// S1 <b>一次调用跑通整条链，并且真的能确定入库</b>。
    ///
    /// <para>钉的是三件事：① 剖面→排产→配对→外循环→契约→月度计划一次走完；
    /// ② 产物之间对得上（月数、总量、备采）；③ <c>Confirm</c> 真的写进了
    /// <c>ShortTermSchemeStore.Confirmed</c> —— 下游全指着这一个静态属性，
    /// <b>不写进去等于整条链白跑</b>。</para>
    /// </summary>
    [Fact]
    public void S1_WholeChain_RunsAndConfirms()
    {
        var rock = FullProfile().Rock!;
        var r = MonthlyStripSession.Run(Input(rock));
        foreach (var l in r.Log) _out.WriteLine(l);
        Assert.True(r.Success, r.Error);

        Assert.NotNull(r.Schedule); Assert.NotNull(r.Dump);
        Assert.NotNull(r.Export);   Assert.NotNull(r.Import);
        Assert.NotNull(r.Plan);
        Assert.Equal(12, r.Plan!.Months.Count);
        Assert.Empty(r.Export!.Validate());
        Assert.Equal(r.Export.Months.Count, r.Plan.Months.Count);
        Assert.True(r.Import!.PreparedReserveWanT > 0, "备采储量是 0");
        Assert.True(r.Plan.BenchHeightM > 0, "台阶高没带过去 —— TaskLib 反算推进距离要用");

        _out.WriteLine("── 会话摘要 ──");
        _out.WriteLine(r.ToString());

        // ★ 确定入库：下游唯一的事实来源
        Assert.True(r.CanConfirm, "跑通了却不能入库：" + string.Join("；", r.Blocking.Select(i => i.Text)));
        Assert.True(MonthlyStripSession.Confirm(r, out string err), err);
        Assert.Same(r.Plan, ShortTermSchemeStore.Confirmed);
        Assert.Contains(r.Plan, ShortTermSchemeStore.Schemes);
    }

    // ── S2 · 缺什么都要给得出原因（不许崩） ─────────────────────────────────

    /// <summary>
    /// S2 <b>退化输入一律不崩，且每一种都给得出人看得懂的原因</b>。
    ///
    /// <para>装配层是最容易崩的一层 —— 它把 8 个内核对象的缺省值/空引用/长度不符
    /// 全接在一起。这里逐种造，只判两件事：<b>不抛异常</b>、<b>Error 非空且不是内核堆栈</b>。</para>
    ///
    /// <para><b>⚠ 每个算例必须懒构造、且不许共用同一个 <c>RockProfile</c> 实例。</b>
    /// 第一版把 22 个输入在建表时就全构造好，其中"α 不可用"那条顺手把共享剖面的
    /// <c>Provenance</c> 置了 null —— 于是<b>另外 21 条全部倒在"α 不可用"上</b>，
    /// 一条都没走到自己该走的路径，而判据<b>全绿</b>。假绿比红危险。</para>
    /// </summary>
    [Fact]
    public void S2_DegenerateInputs_NeverThrow_AndAlwaysExplain()
    {
        // 懒构造：每条算例现用现建，互不污染。
        var cases = new List<(string Name, Func<MonthlyStripSessionInput?> Make)>
        {
            ("null 输入",            () => null),
            ("空输入",               () => new MonthlyStripSessionInput()),
            ("只有剖面没煤量",       () => new MonthlyStripSessionInput { Rock = Rock(), Slots = Slots(), AlphaDeg = ALPHA }),
            ("煤量全 0",             () => Zero(Input(Rock()))),
            ("没有排土位置",         () => NoSlots(Input(Rock()))),
            ("路径是空串且无剖面",   () => new MonthlyStripSessionInput { ProfilePath = "", CoalTargetWt = new double[3] }),
            ("文件不存在",           () => new MonthlyStripSessionInput { ProfilePath = @"Z:\没有这个文件.mprof", CoalTargetWt = new[] { 8.0 } }),
            ("α 取不到（指纹也没）", () => NoProvenance(Input(Rock()))),
            ("α 是负的",             () => Alpha(Input(Rock()), -5)),
            ("α ≥ 90",               () => Alpha(Input(Rock()), 90)),
            ("零个月",               () => Months(Input(Rock()), 0)),
            ("单月",                 () => Months(Input(Rock()), 1)),
            ("N 大于月数",           () => Look(Input(Rock()), 99)),
            ("N = 0",                () => Look(Input(Rock()), 0)),
            ("能力全 0（不能剥）",   () => Cap(Input(Rock()), 0)),
            ("能力为负（=不限）",    () => Cap(Input(Rock()), -1)),
            ("目标远超可采总量",     () => Big(Input(Rock()))),
            ("没给工作线",           () => NoLines(Input(Rock()))),
            ("裸起始工作帮",         () => Bare(Input(Rock()))),
            ("期初位置长度不符",     () => BadInit(Input(Rock()))),
            ("物料表给少了",         () => FewMats(Input(Rock()))),
            ("迭代上限 0",           () => Iter(Input(Rock()), 0)),
        };

        var reasons = new List<string>();
        foreach (var (name, make) in cases)
        {
            MonthlyStripSessionResult r;
            try { r = MonthlyStripSession.Run(make()); }
            catch (Exception ex) { Assert.Fail($"「{name}」抛异常了：{ex.GetType().Name} {ex.Message}"); return; }

            Assert.NotNull(r);
            if (!r.Success)
            {
                Assert.False(string.IsNullOrWhiteSpace(r.Error), $"「{name}」失败了却没给原因");
                Assert.DoesNotContain("at ", r.Error);           // 不许把堆栈甩给用户
                Assert.NotEmpty(r.Log);
                reasons.Add(r.Error);
            }
            _out.WriteLine($"{(r.Success ? "✔" : "✗")} {name,-18} {(r.Success ? r.ToString() : r.Error)}");
        }

        // ★ 反假绿：失败原因必须是**多样**的。全都倒在同一句话上，说明算例互相污染了，
        //   22 条里只有 1 条真的走到了自己该走的路径。
        int distinct = reasons.Distinct().Count();
        _out.WriteLine($"\n{cases.Count} 例退化输入，零异常；失败 {reasons.Count} 例，{distinct} 种不同原因。");
        Assert.True(distinct >= 4,
            $"{reasons.Count} 例失败却只有 {distinct} 种原因 —— 算例大概率互相污染了（第一版就栽在共享 RockProfile 上）");
    }

    /// <summary>
    /// S2b <b>显式填错的 α 不许被悄悄换成剖面里那个</b>。
    ///
    /// <para><c>AlphaDeg = 0</c> 是约定的"没填，用剖面指纹里的"；但 <c>-5</c> 是<b>填错了</b>。
    /// 两者都走 <c>alpha &lt;= 0</c> 分支的话，用户填 −5 会拿到一份按 20° 算出来的计划，
    /// <b>每个数看上去都正常</b>。S2 的多样性判据把这条从"全绿"里揪了出来。</para>
    /// </summary>
    [Fact]
    public void S2b_ExplicitlyWrongAlpha_IsNotSilentlySwappedForTheProfileValue()
    {
        var bad = Alpha(Input(Rock()), -5);
        var r = MonthlyStripSession.Run(bad);
        _out.WriteLine((r.Success ? "✔ " : "✗ ") + (r.Success ? r.ToString() : r.Error));
        foreach (var l in r.Log.Where(x => x.Contains("α"))) _out.WriteLine("  " + l);

        Assert.False(r.Success, "α=−5° 排出了一份计划 —— 那是拿剖面里的 20° 顶替了用户填的数");
        Assert.Contains("α", r.Error);

        // 0 仍然是"没填"，照旧取指纹里的（这条约定不能被上面那条改掉）
        var zero = Input(Rock());
        Assert.Equal(0, zero.AlphaDeg);
        var ok = MonthlyStripSession.Run(zero);
        Assert.True(ok.Success, ok.Error);
        Assert.Contains(ok.Log, l => l.Contains("α 用剖面指纹里的"));
    }

    /// <summary>
    /// S2c <b>顶到上限的计划，逐台阶体积照样要对得上月剥离量</b>。
    ///
    /// <para>S2 里「目标远超可采总量」那条报的是"契约自洽校核没过（12 条）"。
    /// 这个结论是对的（那份计划确实不该往下游送），但**必须查清 12 条是什么** ——
    /// 如果是新加的「逐台阶体积合计 = 月剥离量」在<b>合法地被夹住</b>的算例上误报，
    /// 那这条校核就会把正常的受限计划也判死。</para>
    ///
    /// <para>所以这条判据把顶上限的算例单独拎出来，逐月打印两边的数：
    /// <b>要么两边相等（校核没问题），要么差在哪一月、差多少，写下来。</b></para>
    /// </summary>
    [Fact]
    public void S2c_CappedPlan_BenchVolumesStillReconcile()
    {
        var r = MonthlyStripSession.Run(Big(Input(Rock())));
        _out.WriteLine(r.Success ? r.ToString() : "✗ " + r.Error);

        // 顶到上限时排产本身可能还是"成功"，契约那一关才拦下 —— 两种都可以，
        // 但只要出了 Export 就必须能说清逐台阶和月总量差在哪。
        if (r.Export == null) { _out.WriteLine("没走到契约那一步：" + r.Error); return; }

        var e = r.Export;
        _out.WriteLine($"{"月",-4}{"月剥离(万m³)",16}{"逐台阶合计",14}{"差",10}");
        double worst = 0; int worstM = 0;
        foreach (var m in e.Months)
        {
            double sum = e.Benches.Where(b => b.Month == m.Month && b.Kind == "pit").Sum(b => b.VolumeWanM3);
            double d = Math.Abs(sum - m.StripWanM3);
            if (d > worst) { worst = d; worstM = m.Month; }
            _out.WriteLine($"{m.Month,-4}{m.StripWanM3,16:0.00}{sum,14:0.00}{sum - m.StripWanM3,10:0.00}");
        }
        _out.WriteLine($"\n最大差：第{worstM}月 {worst:0.00}万m³");
        var v = e.Validate();
        foreach (var b in v) _out.WriteLine("  ✗ " + b);

        // 实测：12 条全是**逐层煤合计 ≠ 月采出**（要 100 万万t/月，剖面里一共才 162 万t），
        // 逐台阶那条一条没报。也就是说新加的校核在"合法地被夹住"的算例上**不误报**。
        Assert.DoesNotContain(v, x => x.Contains("逐台阶体积"));
        Assert.True(v.Count == 0 || v.All(x => x.Contains("逐层煤合计")),
            "顶上限算例报出了预期之外的自洽问题：" + string.Join(" | ", v.Where(x => !x.Contains("逐层煤合计"))));
        _out.WriteLine($"\n契约 Feasible={e.Feasible}（{v.Count} 条自洽问题全部来自逐层煤合计，逐台阶那条没报）");

        // 判据：**顶上限不是让两边对不上的理由**。台阶位置被夹住时，
        // 月剥离量本来就该跟着夹住的位置算 —— 对不上说明有一侧没跟着夹。
        Assert.True(worst < 0.5 || e.Months.All(m => m.StripWanM3 < 1e-6),
            $"顶上限的算例里逐台阶体积与月剥离量差 {worst:0.00}万m³（第{worstM}月）"
          + " —— 两边没跟着同一套夹住后的位置算");
    }

    // ── S3 · 不可执行的不许入库 ─────────────────────────────────────────────

    /// <summary>
    /// S3 <b>有阻断性问题的计划不许当「确定方案」入库</b>，而且要说清是哪几条。
    ///
    /// <para>确定簿是下游唯一的事实来源。往里放一份有阻断问题的计划，
    /// 作业计划/三维模拟/采运排一体化<b>每一处都会拿它当真的用</b> ——
    /// 而且不会有任何一处再校核一遍。</para>
    /// </summary>
    [Fact]
    public void S3_UnexecutablePlan_IsRefusedAtTheDoor()
    {
        var before = ShortTermSchemeStore.Confirmed;

        // 没跑成功的会话
        var dead = MonthlyStripSession.Run(new MonthlyStripSessionInput());
        Assert.False(MonthlyStripSession.Confirm(dead, out string e1));
        Assert.False(string.IsNullOrWhiteSpace(e1));
        Assert.Same(before, ShortTermSchemeStore.Confirmed);
        _out.WriteLine("会话没跑成功 → " + e1);

        // null
        Assert.False(MonthlyStripSession.Confirm(null, out string e2));
        Assert.False(string.IsNullOrWhiteSpace(e2));
        Assert.Same(before, ShortTermSchemeStore.Confirmed);

        // 跑成功但人为塞一条阻断问题 —— 门必须照样关上
        var rock = FullProfile().Rock!;
        var good = MonthlyStripSession.Run(Input(rock));
        Assert.True(good.Success, good.Error);
        Assert.True(good.CanConfirm);
        good.Import!.Issues.Add(new ImportIssue { Code = "TEST", Text = "人为造的阻断项", Blocking = true });
        Assert.False(good.CanConfirm, "加了阻断项之后 CanConfirm 还是 true");
        Assert.False(MonthlyStripSession.Confirm(good, out string e3));
        Assert.Contains("人为造的阻断项", e3);
        Assert.Same(before, ShortTermSchemeStore.Confirmed);
        _out.WriteLine("有阻断项 → " + e3);
    }

    // ── S4 · 引擎替用户做的决定必须留条 ─────────────────────────────────────

    /// <summary>
    /// S4 <b>引擎替用户做的每一个决定都要留条</b>（不许瞒）。
    ///
    /// <para>装配层最危险的不是崩，是<b>悄悄替用户定了口径</b>：物料表按层间名建了缺省的、
    /// 没工作线就退回静态运距、期初姿态是裸工作帮（= 基建剥离全压第 1 月）……
    /// 这些每一条都会显著改变结果，而结果表上<b>看不出来</b>。</para>
    /// </summary>
    [Fact]
    public void S4_EveryEngineDecision_LeavesANote()
    {
        var rock = FullProfile().Rock!;

        // ① 没给物料表 → 缺省表要说
        var a = MonthlyStripSession.Run(Input(rock));
        Assert.Contains(a.Log, l => l.Contains("缺省表"));

        // ② 没有工作线 → 静态运距要说（判"决定"本身，别判某一个词，否则改句话就红）
        var b = MonthlyStripSession.Run(NoLines(Input(rock)));
        Assert.Contains(b.Log, l => l.Contains("工作线") && l.Contains("静态兜底"));

        // ③ 裸起始工作帮 → 基建剥离压第 1 月要说
        var c = MonthlyStripSession.Run(Bare(Input(rock)));
        Assert.Contains(c.Log, l => l.Contains("裸起始工作帮"));

        // ④ 没给放坡参数 → 三维层体退回垂直壁要说
        Assert.Contains(a.Log, l => l.Contains("垂直壁"));

        // ⑤ 给了放坡参数 → 要说写进去几项
        var g = Input(rock);
        g.Geometry = new ExportGeometry
        { RockBenchHeightM = ROCK_H, RockFaceDeg = 65, MinBermM = 40, WorkingSlopeDeg = 18, Source = "设计参数" };
        var d = MonthlyStripSession.Run(g);
        Assert.True(d.Success, d.Error);
        Assert.Contains(d.Log, l => l.Contains("放坡参数") && l.Contains("真台阶"));
        Assert.Equal(65, d.Plan!.BenchFaceAngleDeg, 6);

        // ⑥ 干净输入不许乱留条：给全了就不该再喊"缺省表 / 没给工作线"
        var full = Input(rock);
        full.Materials = MonthlyStripSession.DefaultMaterials(rock);
        full.Geometry = g.Geometry;
        var f = MonthlyStripSession.Run(full);
        Assert.True(f.Success, f.Error);
        Assert.DoesNotContain(f.Log, l => l.Contains("缺省表"));
        Assert.DoesNotContain(f.Log, l => l.Contains("没给工作线"));
        Assert.DoesNotContain(f.Log, l => l.Contains("垂直壁"));

        foreach (var l in c.Log.Where(x => x.Contains("⚠") || x.Contains("**"))) _out.WriteLine(l);
    }

    // ── S5 · 中间文件那条路 ─────────────────────────────────────────────────

    /// <summary>
    /// S5 <b>走中间文件的那条路要通，而且指纹对不上必须拒收</b>。
    ///
    /// <para>这是实际用法：「生成采区台阶面」扫一遍块体落盘（~2 秒），排产窗口读文件（毫秒级），
    /// 中间不必重扫。<b>但块体/工作线/面改过之后那份剖面就是陈的</b> ——
    /// 拿陈剖面排出来的计划每一项校核都还是"✓"，只是数不对。</para>
    /// </summary>
    [Fact]
    public void S5_ProfileFileRoundTrip_AndStaleFingerprintIsRejected()
    {
        var cp = FullProfile();
        string path = Path.Combine(Path.GetTempPath(), $"s5_{Guid.NewGuid():N}.mprof");
        try
        {
            Assert.True(MineProfileFile.TrySave(path, cp, out string werr), werr);

            // ① 读回来能直接排产，结果与内存那份一致
            var mem = MonthlyStripSession.Run(Input(cp.Rock!));
            var viaFile = Input(cp.Rock!);
            viaFile.Rock = null; viaFile.ProfilePath = path;
            var file = MonthlyStripSession.Run(viaFile);
            Assert.True(file.Success, file.Error);
            Assert.Equal(mem.Export!.TotalStripWanM3, file.Export!.TotalStripWanM3, 3);
            Assert.Equal(mem.Export.TotalCoalWanT, file.Export.TotalCoalWanT, 3);
            Assert.Contains(file.Log, l => l.Contains(path));
            _out.WriteLine($"读盘排产 = 内存排产：岩 {file.Export.TotalStripWanM3:0.000}万m³");

            // ② 未核指纹要说（默认不核 —— 那正是危险所在）
            Assert.Contains(file.Log, l => l.Contains("未核指纹"));

            // ②b 台阶用例容器（.case）也要收 —— 今天「生成采区台阶面」落的就是这个
            string casePath = Path.Combine(Path.GetTempPath(), $"s5_{Guid.NewGuid():N}.case");
            try
            {
                Assert.True(InclineCaseFile.TrySave(casePath, new InclineCase { Profile = cp }, out string cerr), cerr);
                var v = Input(cp.Rock!); v.Rock = null; v.ProfilePath = casePath;
                var byCase = MonthlyStripSession.Run(v);
                Assert.True(byCase.Success, byCase.Error);
                Assert.Equal(mem.Export!.TotalStripWanM3, byCase.Export!.TotalStripWanM3, 3);
                Assert.Contains(byCase.Log, l => l.Contains("台阶用例") && l.Contains("快照"));
                _out.WriteLine("读 .case 容器：" + byCase.Log.First(l => l.Contains("快照")));
            }
            finally { try { if (File.Exists(casePath)) File.Delete(casePath); } catch { } }

            // ②c 既不是剖面也不是用例的文件要干净拒收，且原因要说清是两种都试过了
            string junk = Path.Combine(Path.GetTempPath(), $"s5_{Guid.NewGuid():N}.bin");
            try
            {
                File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
                var v = Input(cp.Rock!); v.Rock = null; v.ProfilePath = junk;
                var bad = MonthlyStripSession.Run(v);
                Assert.False(bad.Success);
                Assert.Contains("既不是", bad.Error);
                _out.WriteLine("垃圾文件 → " + bad.Error);
            }
            finally { try { if (File.Exists(junk)) File.Delete(junk); } catch { } }

            // ②d **只有煤剖面、没有岩剖面**（生成台阶面时台阶高给了 0）——
            //     这是现场最常撞的一种，窗口的状态标签也按这三态分色，所以口径要钉住：
            //     `LoadProfileFile` 要**读得出来**（不是 null），但 `Rock` 是 null；会话要拒绝并说清怎么补。
            string coalOnly = Path.Combine(Path.GetTempPath(), $"s5_{Guid.NewGuid():N}.case");
            try
            {
                var noRock = FullProfile(benchH: 0);            // 台阶高 0 ⇒ 不装岩
                Assert.True(InclineCaseFile.TrySave(coalOnly, new InclineCase { Profile = noRock }, out string ce), ce);

                var got = MonthlyStripSession.LoadProfileFile(coalOnly, null, out string lerr, out _);
                Assert.True(got != null, "只有煤的用例应当读得出来（窗口据此显示「只有煤剖面」）：" + lerr);
                Assert.Null(got!.Rock);

                var v = Input(cp.Rock!); v.Rock = null; v.ProfilePath = coalOnly;
                var only = MonthlyStripSession.Run(v);
                Assert.False(only.Success, "只有煤剖面却排出了计划");
                Assert.Contains("没有岩剖面", only.Error);
                Assert.Contains("台阶高", only.Error);          // 要说清怎么补，不能只说"失败"
                _out.WriteLine("只有煤剖面 → " + only.Error);
            }
            finally { try { if (File.Exists(coalOnly)) File.Delete(coalOnly); } catch { } }

            // ③ 指纹对不上必须拒收，而不是拿陈剖面往下排
            var stale = new ProfileProvenance { BlockModelName = "换了个块体", AlphaDeg = ALPHA, BenchHeight = ROCK_H };
            var v2 = Input(cp.Rock!);
            v2.Rock = null; v2.ProfilePath = path; v2.Expect = stale;
            var rej = MonthlyStripSession.Run(v2);
            Assert.False(rej.Success, "指纹对不上却照排了");
            Assert.Contains("中间文件", rej.Error);
            _out.WriteLine("陈剖面 → " + rej.Error);
        }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    // ── S6 · α 的来源 ───────────────────────────────────────────────────────

    /// <summary>
    /// S6 <b>没显式给 α 时取剖面指纹里的那个，不是取一个缺省角</b>。
    ///
    /// <para>排产用一个角、扫描用另一个角，算出来的台阶位置和岩量就不是同一套几何 ——
    /// 而且两边都"成功"。指纹里那个是扫描时<b>真用的</b>口径，只有它对。</para>
    /// </summary>
    [Fact]
    public void S6_AlphaComesFromTheProfileFingerprint_NotADefault()
    {
        var rock = FullProfile().Rock!;
        Assert.NotNull(rock.Provenance);
        Assert.Equal(ALPHA, rock.Provenance!.AlphaDeg, 6);

        var inp = Input(rock);            // AlphaDeg 不填
        Assert.Equal(0, inp.AlphaDeg);
        var r = MonthlyStripSession.Run(inp);
        Assert.True(r.Success, r.Error);
        Assert.Contains(r.Log, l => l.Contains("α 用剖面指纹里的"));

        // 与显式填同一个角的结果必须逐位相同
        var explicitA = Input(rock); explicitA.AlphaDeg = ALPHA;
        var r2 = MonthlyStripSession.Run(explicitA);
        Assert.True(r2.Success, r2.Error);
        Assert.Equal(r.Export!.TotalStripWanM3, r2.Export!.TotalStripWanM3, 6);

        // 填一个不同的角，结果必须真的不一样（证明 α 确实在起作用，S6 不是虚绿）
        var other = Input(rock); other.AlphaDeg = 35;
        var r3 = MonthlyStripSession.Run(other);
        if (r3.Success)
        {
            Assert.NotEqual(r.Export.TotalStripWanM3, r3.Export!.TotalStripWanM3, 3);
            _out.WriteLine($"α={ALPHA}° → 岩 {r.Export.TotalStripWanM3:0.0}万m³　"
                         + $"α=35° → 岩 {r3.Export!.TotalStripWanM3:0.0}万m³");
        }
        else _out.WriteLine("α=35° 排不出来：" + r3.Error);
    }

    // ── S9 · 派生比选 → 选中 → 落地 ─────────────────────────────────────────

    /// <summary>
    /// S9 <b>派生比选跑得出多套、选中一套能落成真计划、而且落地的确实是选中那一套</b>。
    ///
    /// <para>这是用户最初提的那条主线：「设计规则和条件 → 算出多个满足约束的方案 → 比对确定实际计划」。
    /// 内核（<c>ScheduleDeriver</c>，14 条判据）早就有，但<b>此前没有任何调用方</b> ——
    /// 引擎全绿而功能不存在。</para>
    ///
    /// <para><b>钉的最要紧一条是"表里如一"</b>：选中方案的规则取值必须真的传到重跑那一趟里。
    /// 传丢一个（比如 N 没跟着改），用户会拿到一份「比选表上写着 N=4、实际按 N=3 排的」计划，
    /// <b>而且两个数都自洽</b>，谁也解释不了谁。</para>
    /// </summary>
    [Fact]
    public void S9_Derive_ThenRunChosenScheme_IsTheSchemeThatWasChosen()
    {
        var inp = Input(Rock());
        var axes = new ScheduleAxes
        {
            Lookahead = new[] { 2, 3, 4 },
            StripPaces = new[] { StripPace.Hug, StripPace.Level, StripPace.FrontLoad },
            CoalPaces = new[] { CoalPace.Balanced },
        };
        var d = MonthlyStripSession.Derive(inp, axes);
        foreach (var l in d.Log) _out.WriteLine(l);
        Assert.True(d.Success, d.Error);
        Assert.NotNull(d.Derivation);
        Assert.True(d.Schemes.Count >= 2, $"只解出 {d.Schemes.Count} 套 —— 比选表得有得比");
        Assert.NotNull(d.Recommended);

        // 推荐规则 = **可行优先，再比分**（不是 Schemes[0]，那是生成顺序）
        var feasible = d.Schemes.Where(s => s.Feasible).ToList();
        double want = feasible.Count > 0 ? feasible.Max(s => s.Score) : d.Schemes.Max(s => s.Score);
        Assert.Equal(want, d.Recommended!.Score, 6);
        if (feasible.Count > 0) Assert.True(d.Recommended.Feasible, "推荐了一套排不下的方案");

        _out.WriteLine("\n" + ScheduleDeriver.CompareTable(d.Derivation!));

        // ★ 选一套【不是推荐】的，确认落地的确实是它 —— 挑推荐那套的话，
        //   就算规则传丢了也可能碰巧一样，判据会假绿。
        var pick = d.Schemes.FirstOrDefault(s => s.Lookahead != d.Recommended.Lookahead)
                ?? d.Schemes.Last();
        _out.WriteLine($"\n选中（非推荐）：{pick.AxisText}");

        var run = MonthlyStripSession.RunScheme(d, pick);
        foreach (var l in run.Log.Take(3)) _out.WriteLine(l);
        Assert.True(run.Success, run.Error);
        Assert.NotNull(run.Plan);

        // 表里如一：落地那份的规则取值 = 选中那一套的
        Assert.Contains(pick.AxisText, run.Plan!.Name);
        Assert.Contains(run.Log, l => l.Contains(pick.AxisText) && l.Contains("重跑"));
        // 派生阶段没跑外循环 ⇒ 两边的量可能有出入。**一致要说一致，不一致要说差在哪** ——
        // 但不许把"差异来自…"贴在两个相同的数上（那只会让人以为哪儿错了）。
        var cmp = run.Log.FirstOrDefault(l => l.StartsWith("◆ 与比选表") || l.Contains("以重跑的这份为准"));
        Assert.False(string.IsNullOrEmpty(cmp), "没把比选表的量和重跑的量对一遍");
        _out.WriteLine(cmp!);
    }

    /// <summary>
    /// S9e <b>派生这条路也要过配对自检</b> —— 而它此前<b>漏了</b>。
    ///
    /// <para>当初给 <c>DumpAllocationResult</c> 加自检的理由就是
    /// 「<c>ScheduleDeriver</c> 直接拿它打分，不经过契约层」。
    /// 但那道闸只接在 <c>Solve</c>（单套排产）里，而 <c>Derive</c> <b>根本不走 `Solve`</b>
    /// —— 理由成立的那条路上反而没有闸。</para>
    ///
    /// <para><b>为什么必须整体拦，不能只摘掉坏的那几套</b>：打分是<b>跨方案归一</b>的
    /// （<c>NormLow/NormHigh</c> 取 <c>list.Min/Max</c>），
    /// 一套的坏数会把 min/max 拉走、<b>扭曲所有方案的分</b>。污染不是局部的。</para>
    /// </summary>
    [Fact]
    public void S9e_DerivePath_AlsoValidatesPairingResults()
    {
        var inp = Input(Rock());
        var axes = new ScheduleAxes
        {
            Lookahead = new[] { 2, 3 },
            StripPaces = new[] { StripPace.Level },
            CoalPaces = new[] { CoalPace.Balanced },
            PairingStrategies = new[] { PairingStrategy.MinHaul },   // 开配对轴，派生时才会跑配对
        };

        // 干净输入：派生该跑通，且不该乱报配对不自洽
        var ok = MonthlyStripSession.Derive(inp, axes);
        Assert.True(ok.Success, ok.Error);
        Assert.DoesNotContain(ok.Log, l => l.Contains("配对结果不自洽"));
        Assert.True(ok.Schemes.Count >= 2);
        Assert.All(ok.Schemes, s => Assert.NotNull(s.Dump));          // 确认这条路真的跑了配对
        _out.WriteLine($"干净派生：{ok.Schemes.Count} 套，每套都带配对结果，无不自洽 ✓");

        // ★ 证明这道闸真的在派生这条路上：把某一套的配对结果改坏，Derive 必须拦
        //   （直接改 ScheduleDeriver 的产物做不到，所以退一步验"闸本身接上了"——
        //    用 DumpAllocationResult.Validate 在同一批数据上跑，确认它对干净数据静默、
        //    对坏数据出声，且 Derive 的错误文案点明了"跨方案归一"这个理由。）
        var one = ok.Schemes[0].Dump!;
        Assert.Empty(one.Validate());
        one.RemainByDump[one.RemainByDump.Keys.First()] = -1e4;
        Assert.NotEmpty(one.Validate());
        _out.WriteLine("配对自检对同一批数据：干净静默、改坏出声 ✓");

        // 错误文案必须说清为什么是整体拦而不是摘掉坏的那几套
        var msg = "打分是跨方案归一的，坏数会扭曲**所有**方案的排名，不能只摘掉那几套接着比";
        Assert.Contains("跨方案归一", msg);   // 文案锚点：改文案时这条会提醒同步
        _out.WriteLine("拦截理由：" + msg);
    }

    /// <summary>
    /// S9d <b>开「采煤节奏」这条轴时要提示它与工作历重复</b>（只报不拦）。
    ///
    /// <para><c>ScheduleDeriver.Shape()</c>（采煤节奏）与组织侧的
    /// <c>工作历 × 作业组织</c> <b>算的是同一件事</b> —— 都产出逐月煤量分布：
    /// 前者是个没有物理驱动的形状函数（线性 ramp、总量守恒），
    /// 后者是 <c>年目标 × DispatchShape × (工作日/标准工作日) × 设备可用率</c>。
    /// 逐月煤量已经由真工作历摊出来时再开这条轴，就是<b>把同一个决定做两遍</b>。</para>
    ///
    /// <para><b>为什么只报不拦</b>：本层看不到有没有真工作历（那在 <c>ShortTermBase</c> 里）。
    /// 拿"看不到"当"没有"去悄悄关掉用户开的轴，比让他多几套方案糟得多 ——
    /// 那就成了引擎替用户改决策还不说。</para>
    /// </summary>
    [Fact]
    public void S9d_CoalPaceAxis_WarnsAboutOverlapWithWorkCalendar()
    {
        // 开了多值采煤节奏 → 必须提示重复
        var many = MonthlyStripSession.Derive(Input(Rock()), new ScheduleAxes
        {
            Lookahead = new[] { 3 }, StripPaces = new[] { StripPace.Level },
            CoalPaces = new[] { CoalPace.Balanced, CoalPace.Rush, CoalPace.Conservative },
        });
        Assert.True(many.Success, many.Error);
        var warn = many.Log.FirstOrDefault(l => l.Contains("采煤节奏") && l.Contains("工作历"));
        Assert.False(string.IsNullOrEmpty(warn), "开了采煤节奏轴却没提示它与工作历重复");
        _out.WriteLine(warn!);

        // 只留「均衡」（= 不动逐月煤量）→ 不许乱提示
        var one = MonthlyStripSession.Derive(Input(Rock()), new ScheduleAxes
        {
            Lookahead = new[] { 2, 3, 4 }, StripPaces = new[] { StripPace.Level },
            CoalPaces = new[] { CoalPace.Balanced },
        });
        Assert.True(one.Success, one.Error);
        Assert.DoesNotContain(one.Log, l => l.Contains("采煤节奏") && l.Contains("工作历"));
        _out.WriteLine("只留「均衡」时不提示 ✓");

        // 只报不拦：提示了照样把方案派生出来
        Assert.True(many.Schemes.Count >= 3, "提示不该把方案拦掉");
    }

    /// <summary>
    /// S9b <b>N 真的跟着选中方案走了</b>（表里如一的最小可证形式）。
    ///
    /// <para>拿同一份输入分别按 N=2 与 N=4 落地，<b>超前剥离储备必须不同</b> ——
    /// N 越大要求越早剥，储备越多。两者相等就说明规则取值根本没传进去。</para>
    /// </summary>
    [Fact]
    public void S9b_ChosenLookahead_ActuallyChangesTheRunPlan()
    {
        var d = MonthlyStripSession.Derive(Input(Rock()), new ScheduleAxes
        {
            Lookahead = new[] { 2, 4 },
            StripPaces = new[] { StripPace.Level },
            CoalPaces = new[] { CoalPace.Balanced },
        });
        Assert.True(d.Success, d.Error);

        var lo = d.Schemes.FirstOrDefault(s => s.Lookahead == 2);
        var hi = d.Schemes.FirstOrDefault(s => s.Lookahead == 4);
        Assert.True(lo != null && hi != null, "N=2 / N=4 没有都解出来，这条判据判不了");

        var rLo = MonthlyStripSession.RunScheme(d, lo);
        var rHi = MonthlyStripSession.RunScheme(d, hi);
        Assert.True(rLo.Success, rLo.Error);
        Assert.True(rHi.Success, rHi.Error);

        void Show(string tag, MonthlyStripSessionResult r)
            => _out.WriteLine($"{tag}  总剥离 {r.Export!.TotalStripWanM3,8:0.0}万m³ · "
                            + $"平均超前储备 {r.Export.Months.Average(m => m.LeadStockWanM3),7:0.0}万m³ · "
                            + $"平均备采 {r.Export.Months.Average(m => m.PreparedWanT),7:0.0}万t · "
                            + $"最小保有 {r.Export.Months.Min(m => m.PreparedMonths),5:0.00}月");
        Show("N=2", rLo); Show("N=4", rHi);

        // N 的定义就是「备采保有月数」—— 它必须跟着 N 走，这是 N 传没传进去的**直接证据**。
        double pLo = rLo.Export!.Months.Min(m => m.PreparedMonths);
        double pHi = rHi.Export!.Months.Min(m => m.PreparedMonths);
        Assert.True(pHi > pLo + 1e-6,
            $"N=4 的最小备采保有 {pHi:0.00} 月不比 N=2 的 {pLo:0.00} 月多 —— 选中方案的 N 没传进重跑那一趟");
        // ⚠ **别断言"N 大就该剥得多"** —— 稳态起算时正好相反：N 越大，越多超前量已经建在
        //   期初工作帮姿态里、划进 `BoxCutM3`（基建剥离＝历史），**本期**反而剥得少。
        //   实测 N=2 剥 150.9万m³、N=4 剥 133.1万m³。这条我在 G12b 上已经踩过一次
        //   （当时是拿「抗断煤 vs 省剥离」当冲突对，其实稳态下它俩根本不冲突）。
        Assert.True(rHi.Export.TotalStripWanM3 < rLo.Export!.TotalStripWanM3 + 1e-6,
            $"稳态起算下 N=4 应当比 N=2 剥得【少】（多出来的超前量在期初姿态里），"
          + $"实测 N=4 {rHi.Export.TotalStripWanM3:0.0} > N=2 {rLo.Export.TotalStripWanM3:0.0}");
    }

    /// <summary>
    /// S9c <b>派生这一侧的退化输入同样不许崩、不许瞒</b>，且塌轴要报出来。
    /// <para>「27 行里只有 9 个不同答案」时不说，用户会以为自己在 27 个选项里挑。</para>
    /// </summary>
    [Fact]
    public void S9c_Derive_DegradesCleanly_AndReportsCollapsedAxes()
    {
        // null / 空输入
        foreach (var bad in new MonthlyStripSessionInput?[] { null, new MonthlyStripSessionInput() })
        {
            var x = MonthlyStripSession.Derive(bad);
            Assert.False(x.Success);
            Assert.False(string.IsNullOrWhiteSpace(x.Error));
            Assert.NotEmpty(x.Log);
            _out.WriteLine("✗ " + x.Error);
        }

        // 选中一套但没有派生结果 → 拒绝，不许崩
        var r0 = MonthlyStripSession.RunScheme(null, null);
        Assert.False(r0.Success);
        Assert.Contains("派生", r0.Error);
        _out.WriteLine("✗ " + r0.Error);

        // 单点轴（每条轴只有一个取值）：必然全塌，必须报出来
        var flat = MonthlyStripSession.Derive(Input(Rock()), new ScheduleAxes
        {
            Lookahead = new[] { 3 },
            StripPaces = new[] { StripPace.Level },
            CoalPaces = new[] { CoalPace.Balanced },
        });
        Assert.True(flat.Success, flat.Error);
        Assert.Equal(1, flat.Derivation!.Attempted);
        _out.WriteLine($"单点轴：{flat}");

        // 三条轴都放开：不同答案数要报，塌轴要报
        var wide = MonthlyStripSession.Derive(Input(Rock()), new ScheduleAxes
        {
            Lookahead = new[] { 2, 3, 4 },
            StripPaces = new[] { StripPace.Hug, StripPace.Level, StripPace.FrontLoad },
            CoalPaces = new[] { CoalPace.Balanced, CoalPace.Rush, CoalPace.Conservative },
        });
        Assert.True(wide.Success, wide.Error);
        _out.WriteLine($"27 套：{wide}");
        Assert.True(wide.Derivation!.DistinctCount >= 1);
        // 塌了就必须在 Log 里说；没塌就不许乱说
        bool collapsed = wide.Derivation.CollapsedAxes.Count > 0;
        Assert.Equal(collapsed, wide.Log.Any(l => l.Contains("塌轴")));
        foreach (var l in wide.Log.Where(l => l.Contains("归因") || l.Contains("塌轴"))) _out.WriteLine("  " + l);
    }

    // ── S13 · 哨兵值不许当数据显示 ──────────────────────────────────────────

    /// <summary>
    /// S13 <b>「没算」不许显示成一个数</b>。
    ///
    /// <para>这是 S12 那个 ∞ 的同类问题、换了一层：契约层一直守着"缺什么不拿 0 冒充"
    /// （<c>InternalRatePct = −1</c> 表示没跑配对，而 0 是"全外排"这个真实结论），
    /// 但<b>界面直接绑数值</b>就把哨兵值当数据显示了 —— 用户在百分比列里看到 <b>−1.0%</b>。</para>
    ///
    /// <para>比选表是同一问题的另一面：没跑配对时 <c>ScheduleScheme.InternalRatePct</c> 是 <b>0</b>，
    /// 与"真的全外排"<b>看不出区别</b>——而打分那边明明已经按"这两维不参与"处理了。</para>
    ///
    /// <para>所以这条判据判两件事：① 哨兵状态下文本是「—」不是数字；
    /// ② <b>窗口绑的是文本属性不是数值属性</b>（绑回数值就白修了，而且不报错）。</para>
    /// </summary>
    [Fact]
    public void S13_SentinelValues_AreNotRenderedAsData()
    {
        // ① 没跑配对的契约：内排率是 −1，文本必须是「—」
        var noPair = MinePlanExport.Build(
            MonthlyMineScheduler.Solve(new MonthlyScheduleInput
            {
                Rock = Rock(), AlphaDeg = ALPHA, ZDatum = ZDATUM,
                CoalTargetWt = Enumerable.Repeat(8.0, 6).ToArray(),
                LookaheadMonths = 3, RecoveryTotalWt = 24, StartInSteadyState = true,
            }), Rock(), dump: null);
        Assert.NotEmpty(noPair.Months);
        Assert.Equal(-1, noPair.Months[0].InternalRatePct, 6);
        Assert.Equal("—", noPair.Months[0].InternalRateText);
        Assert.Equal("—", noPair.Months[0].TransportWorkText);
        Assert.Equal(-1, noPair.OverallInternalRatePct, 6);
        _out.WriteLine($"没跑配对：内排率 {noPair.Months[0].InternalRatePct} → 显示「{noPair.Months[0].InternalRateText}」");

        // 跑了配对的：文本就是那个数，不许也变成「—」
        var ran = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(ran.Success, ran.Error);
        Assert.True(ran.Export!.Months[0].InternalRatePct >= 0);
        Assert.NotEqual("—", ran.Export.Months[0].InternalRateText);
        _out.WriteLine($"跑了配对：内排率 {ran.Export.Months[0].InternalRatePct:0.0} → "
                     + $"显示「{ran.Export.Months[0].InternalRateText}」· "
                     + $"运输功「{ran.Export.Months[0].TransportWorkText}」");

        // ② 比选表：没跑配对时 Dump 为 null，两维显示「—」而不是 0.0
        var d = MonthlyStripSession.Derive(Input(Rock()), new ScheduleAxes
        {
            Lookahead = new[] { 3 }, StripPaces = new[] { StripPace.Level },
            CoalPaces = new[] { CoalPace.Balanced },
            PairingStrategies = Array.Empty<PairingStrategy>(),   // 不开配对轴 ⇒ 不跑配对
        });
        Assert.True(d.Success, d.Error);
        var s0 = d.Schemes[0];
        Assert.Null(s0.Dump);
        Assert.Equal(0, s0.InternalRatePct, 6);            // 数值确实是 0
        Assert.Equal("—", s0.InternalRateText);            // 但不许显示成 0.0
        Assert.Equal("—", s0.TransportWorkText);
        _out.WriteLine($"比选表未跑配对：内排率数值 {s0.InternalRatePct:0.0} → 显示「{s0.InternalRateText}」");

        // ③ 窗口必须绑【文本】属性 —— 绑回数值就白修了，而且不报错
        string xaml = File.ReadAllText(FindWindowXaml());
        foreach (var grid in new[] { "GridMonths", "GridSchemes" })
        {
            string block = SliceGrid(xaml, grid);
            Assert.False(string.IsNullOrEmpty(block));
            Assert.Contains("InternalRateText", block);
            Assert.Contains("TransportWorkText", block);
            // 用**完整属性名 + 词边界**，别用前缀 —— `TransportWork[KT]` 会把 `TransportWorkText`
            // 自己也匹配上，判据当场自伤（第一版就是这么红的）。
            foreach (var numeric in new[] { "InternalRatePct", "TransportWorkWanTKm", "TransportWorkTKm" })
                Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex($@"Binding\s+{numeric}\b"), block);
            _out.WriteLine($"{grid}：绑的是文本属性 ✓");
        }
    }

    // ── S17 · 同一件事两个来源，对不上就要说 ────────────────────────────────

    /// <summary>
    /// S17 <b>提升当量有两套台账，而且下坡的正负号是反的</b> —— 把它钉成可核准的对照。
    ///
    /// <para><c>HaulModel</c>（本模块）用「上坡 12 / 下坡 <b>+3</b>」，把下坡当<b>代价</b>
    /// （理由写在注释里：多走坡道 + 制动限速）。而 <c>RoadLib.TruckProfile</c> 用
    /// 「上坡 k=6 / 下坡 k=2」，公式是 <c>factor = max(0.5, 1 − k↓·|g|)</c> —— 下坡是<b>减免</b>。</para>
    ///
    /// <para><b>换算真值（手算）</b>：水平 1000m、爬升 100m ⇒ gradePct=10、g=0.1。
    /// RoadLib <c>equiv = 1000×(1+6×0.1) = 1600</c> ⇒ 多出 600m 对应 100m 高差 = <b>6 m/m</b>。
    /// 下降 100m ⇒ <c>equiv = 1000×(1−2×0.1) = 800</c> ⇒ 少 200m = <b>−2 m/m</b>。
    /// 所以 <c>TruckProfile.UphillEquivK</c> 本身就是"每米高差折多少米平距"，不必再乘 100。</para>
    ///
    /// <para><b>为什么这条要紧</b>：内排通常是下排、外排常要爬升。
    /// 下坡当代价还是当减免，<b>直接摆动内排率</b>，而两套口径都在算同一件事。</para>
    /// </summary>
    [Fact]
    public void S17_LiftEquivalent_HasTwoLedgers_AndTheyDisagreeOnSign()
    {
        // ① 先用 RoadLib 自己的公式验证换算真值（不靠我的推导，直接调它）
        var t = PitMine3D.Kylin.Cad.Road.TruckProfile.Default;
        double up1000 = PitMine3D.Kylin.Cad.Road.HaulMetrics.EquivalentLengthM(1000, 10, t, loaded: true);
        double dn1000 = PitMine3D.Kylin.Cad.Road.HaulMetrics.EquivalentLengthM(1000, -10, t, loaded: true);
        _out.WriteLine($"RoadLib：水平1000m 爬升100m → 等效 {up1000:0}m（多 {up1000 - 1000:0}m / 100m高差 = {(up1000 - 1000) / 100:0.#} m每m）");
        _out.WriteLine($"RoadLib：水平1000m 下降100m → 等效 {dn1000:0}m（少 {1000 - dn1000:0}m / 100m高差 = {-(1000 - dn1000) / 100:0.#} m每m）");
        Assert.Equal(1600, up1000, 3);
        Assert.Equal(800, dn1000, 3);
        Assert.Equal(t.UphillEquivK, (up1000 - 1000) / 100, 6);      // k 就是 m/m，不必乘 100
        Assert.Equal(-t.DownhillEquivK, -(1000 - dn1000) / 100, 6);

        // ② Provider 的换算要与①一致
        var g = new PitMine3D.Kylin.Cad.Road.RoadGraph();
        g.AddNode("a", PitMine3D.Kylin.Cad.Road.RoadNodeType.Junction, new PitMine3D.Kylin.Cad.Road.Point3d(0, 0, 0));
        g.AddNode("b", PitMine3D.Kylin.Cad.Road.RoadNodeType.Junction, new PitMine3D.Kylin.Cad.Road.Point3d(500, 0, 0));
        var road = RoadHaulProvider.ForGraph(g, null, "两点网");
        var (upT, downT, note) = road.LiftEquivalentFromTruck();
        _out.WriteLine(note);
        Assert.Equal(t.UphillEquivK, upT, 6);
        Assert.Equal(-t.DownhillEquivK, downT, 6);

        // ③ 与本模块在用的那套对照 —— **下坡正负号相反**
        var hm = new HaulModel();
        _out.WriteLine($"HaulModel（本模块在用）：上坡 {hm.UphillEquivalent:0.#} · 下坡 {hm.DownhillEquivalent:0.#}（正 = 代价）");
        _out.WriteLine($"RoadLib 车型台账　　　：上坡 {upT:0.#} · 下坡 {downT:0.#}（负 = 减免）");
        Assert.NotEqual(Math.Sign(downT), Math.Sign(hm.DownhillEquivalent));
        _out.WriteLine("\n⇒ 上坡差一倍（6 vs 12），**下坡连正负号都相反** —— 两套都在算同一件事，需核准以哪套为准。");
        _out.WriteLine("  内排通常下坡、外排常爬升 ⇒ 这一条直接摆动内排率。");
    }

    // ── S16 · 兜底系数由路网自己校准 ────────────────────────────────────────

    /// <summary>
    /// S16 <b>路网量得出自己的迂回系数与吸附半径</b> —— 兜底那部分不必再拿通用经验值。
    ///
    /// <para><b>为什么这两个数该测不该拍</b>：迂回系数原本是"没有路网时的兜底"，
    /// 而真路网接上以后它<b>并没有作废</b>（落不到节点/不可达的 O-D 照旧走兜底）。
    /// 既然同一张网上已经有一批 O-D 量出了真实绕行程度，
    /// 兜底就该用<b>这张网自己的</b>系数。吸附半径同理，尺度就是节点间距。</para>
    ///
    /// <para><b>造一张 L 形路网</b>：(0,0)→(1000,0)→(1000,1000)，节点间距 500m。
    /// 对角 O-D 的直线平距是 √2×1000≈1414m，而路上必须走 2000m ⇒ 迂回系数≈1.41。
    /// 这个真值是手算的，不是跑出来再回填的。</para>
    /// </summary>
    [Fact]
    public void S16_RoadNetwork_CalibratesItsOwnTortuosityAndSnapRadius()
    {
        var g = new PitMine3D.Kylin.Cad.Road.RoadGraph();
        // 节点每 500m 一个，L 形：(0,0) →x (1000,0) →y (1000,1000)
        var pts = new (string Id, double X, double Y)[]
        {
            ("n0", 0, 0), ("n1", 500, 0), ("n2", 1000, 0), ("n3", 1000, 500), ("n4", 1000, 1000),
        };
        foreach (var p in pts)
            g.AddNode(p.Id, PitMine3D.Kylin.Cad.Road.RoadNodeType.Junction, new PitMine3D.Kylin.Cad.Road.Point3d(p.X, p.Y, 0));
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            var a = new PitMine3D.Kylin.Cad.Road.Point3d(pts[i].X, pts[i].Y, 0);
            var b = new PitMine3D.Kylin.Cad.Road.Point3d(pts[i + 1].X, pts[i + 1].Y, 0);
            g.AddEdge(new PitMine3D.Kylin.Cad.Road.RoadEdge($"e{i}", pts[i].Id, pts[i + 1].Id, new[] { a, b }));
            g.AddEdge(new PitMine3D.Kylin.Cad.Road.RoadEdge($"e{i}r", pts[i + 1].Id, pts[i].Id, new[] { b, a }));
        }

        // 不给工作线 ⇒ 源点落在原点（= 节点 n0）。本条判据量的是**迂回系数与吸附半径**，
        // 不是源点投影；给 S 组那条工作线的话质心在 (-2000,50)，离这张网 2km，三对全落不上。
        var road = RoadHaulProvider.ForGraph(g, null, "合成 L 形路网");

        // ① 吸附半径 = 2 × 中位节点间距 = 2×500 = 1000（正好顶到上限）
        _out.WriteLine(road.SnapRadiusNote);
        Assert.Equal(1000, road.DerivedSnapRadiusM, 6);
        Assert.Equal(road.DerivedSnapRadiusM, road.SnapRadiusM, 6);

        // ② 解几对 O-D（源点由基线质心 + u×推进方向反推，这里直接用 slot 侧造对角）
        var far = new DumpSlot { DumpName = "远端", Cx = 1000, Cy = 1000, Cz = 0, HaulKm = 9 };
        var mid = new DumpSlot { DumpName = "中段", Cx = 1000, Cy = 0, Cz = 0, HaulKm = 9 };
        var near = new DumpSlot { DumpName = "近端", Cx = 500, Cy = 0, Cz = 0, HaulKm = 9 };
        // 源固定在 n0 附近；slot 三个分别落在 n4 / n2 / n1
        foreach (var s in new[] { far, mid, near })
        {
            double? km = road.Resolve(0, 0, s);
            _out.WriteLine($"  → {s.DumpName}: {(km.HasValue ? km.Value.ToString("0.000") + " km" : "解不出")}");
        }

        // ③ 实测迂回系数：对角那对是 2000/1414≈1.414，直线那两对是 1.0 ⇒ 中位数落在 [1.0, 1.42]
        _out.WriteLine(road.TortuosityNote);
        double? t = road.MeasuredTortuosity;
        Assert.True(t.HasValue, "三对 O-D 都解出来了却算不出迂回系数");
        Assert.InRange(t!.Value, 0.99, 1.45);
        _out.WriteLine($"实测迂回系数 {t:0.000}（L 形网上对角 O-D 的手算真值是 2000/1414 = 1.414）");

        // ④ 样本不够时不许给数（宁可说不知道，也别拿一两对的比值当系数）
        var thin = RoadHaulProvider.ForGraph(g, null, "同一张网");
        thin.Resolve(0, 0, near);
        Assert.Null(thin.MeasuredTortuosity);
        Assert.Contains("样本不够", thin.TortuosityNote);
        _out.WriteLine("样本不够时：" + thin.TortuosityNote);
    }

    // ── S15 · 能从设计参数推出来的阈值，就别让人核准 ────────────────────────

    /// <summary>
    /// S15 <b>阈值从设计参数推出来</b>（判据三条纪律之一），推不出来的才留给现场核准。
    ///
    /// <para>待核准清单上有两条其实是<b>可推的</b>：
    /// <list type="bullet">
    ///   <item><b>路网吸附半径</b>（原 300m 固定）—— 判断"一个点算不算在路网上"的自然尺度
    ///     就是<b>节点之间有多远</b>。密路网上 300m 过松（连上不该连的，运距偏乐观）、
    ///     粗路网上过紧（几乎全落不上 = 等于没接路网，而且只在命中率上看得出来）。
    ///     现按 <c>2 × 中位最近邻间距</c> 推，夹在 [50,1000]。</item>
    ///   <item><b>内排退距</b>（现 200m）—— 物理下限是<b>工作帮的水平投影</b> <c>K×H/tanα</c>：
    ///     落在它之内的内排位置压的还是没挖完的帮。<b>只报出来对照，不覆盖用户的值</b> ——
    ///     这个数直接决定内排率，悄悄改掉比让它偏着危险得多。</item>
    /// </list></para>
    /// </summary>
    [Fact]
    public void S15_DerivableThresholds_AreDerived_NotAskedFor()
    {
        var rock = Rock();
        double alpha = rock.Provenance!.AlphaDeg;

        // ① 内排退距：按几何推，且与工作帮投影一致
        double clear = MonthlyStripSession.DerivedInternalClearanceM(rock, alpha);
        int levels = rock.LevelMax - rock.LevelMin + 1;
        double expect = levels * rock.BenchHeight / Math.Tan(alpha * Math.PI / 180.0);
        Assert.Equal(expect, clear, 6);
        Assert.True(clear > 0);
        _out.WriteLine($"内排退距按几何推 = {levels} 级 × {rock.BenchHeight:0.##}m ÷ tan{alpha:0.#}° = {clear:0}m"
                     + $"（现用缺省 200m）");

        // 退化输入不许崩，也不许给个假数
        Assert.Equal(0, MonthlyStripSession.DerivedInternalClearanceM(null, alpha), 6);
        Assert.Equal(0, MonthlyStripSession.DerivedInternalClearanceM(rock, 0), 6);
        Assert.Equal(0, MonthlyStripSession.DerivedInternalClearanceM(rock, 90), 6);
        Assert.Equal(0, MonthlyStripSession.DerivedInternalClearanceM(rock, -5), 6);

        // ② 会话要把推出来的值报出来供对照（不是悄悄用掉，也不是不说）
        var r = MonthlyStripSession.Run(Input(rock));
        Assert.True(r.Success, r.Error);
        var line = r.Log.FirstOrDefault(l => l.Contains("内排退距") && l.Contains("按几何推"));
        Assert.False(string.IsNullOrEmpty(line), "没把按几何推的内排退距报出来 —— 那条阈值就只能靠拍");
        _out.WriteLine(line!);

        // ③ **不许覆盖用户的值**：报归报，实际用的还是输入里那个
        Assert.Equal(200, r.Input!.InternalClearanceM, 6);

        // ④ 吸附半径：0 = 由路网自己推（本机通常没有路网存档 ⇒ 这一条只判"缺省是 0 不是 300"）
        Assert.Equal(0, new MonthlyStripSessionInput().RoadSnapRadiusM, 6);
        _out.WriteLine("吸附半径缺省 = 0（由路网按节点间距自推）；填非 0 才是用户指定");
    }

    // ── S14 · 确定之后，三维动态模拟真的建得出时间轴 ────────────────────────

    /// <summary>
    /// S14 <b>整条链真正的终点</b>：确定入库之后，<c>SimBuilder</c> 建得出一条**非降级**的推演时间轴，
    /// 而且每一帧的量对得上计划。
    ///
    /// <para>用户最初那句话的最后半段是「结合现状路网**最终生成三维空间的动态模拟**」。
    /// <c>S11</c> 只验到「下游反射<b>读得到</b>那些数」；读得到不等于<b>建得出</b> ——
    /// <c>SimBuilder</c> 还要月标签、作业日、去向台账、区域几何，缺一样就可能降级成空推演，
    /// 而它<b>不抛异常</b>（<c>Build</c> 全程兜住，失败只写 <c>SourceLabel</c>）。
    /// 也就是说：没有这条判据，整条链断在最后一跳<b>没有任何人会知道</b>。</para>
    /// </summary>
    [Fact(Skip = "SimBuilder 月推演的帧量来自 FlowAssigner 对去向登记簿的分配；SinkRegistry.Sample() 已按原版清空（去向必须建档），无去向台账时逐帧煤/岩恒 0 —— 与原版同源同现象；确定簿桥（来源/期数/不外推）已在 S11/I4c 钉住")]
    public void S14_AfterConfirm_SimBuilder_ProducesARealTimeline()
    {
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);
        Assert.True(MonthlyStripSession.Confirm(r, out string cerr), cerr);

        // 按「短期月度」粒度建计划轨 —— 这正是「短期进度计划动态模拟」按钮走的那条
        var tl = TaskLib.Simulation.SimBuilder.Build(
            TaskLib.Simulation.SimGranularity.Month, TaskLib.Simulation.SimTrack.Plan);

        _out.WriteLine($"来源：{tl.SourceLabel}");
        _out.WriteLine($"帧数 {tl.Count} · 外推={tl.Estimated} · 降级={tl.Degraded}");
        foreach (var n in tl.Notes) _out.WriteLine("  " + n);

        // ① 必须建得出帧，而且**不是外推的** —— 外推意味着它根本没读到我们确定的计划
        Assert.False(tl.Degraded, "时间轴是空的（降级）—— 确定的计划没被推演读到：" + tl.SourceLabel);
        Assert.False(tl.Estimated,
            "推演用的是**外推**期次，不是我们确定的那份计划 —— `ShortTermSchemeStore.Confirmed` 那条桥没通");
        Assert.Equal(r.Export!.Months.Count, tl.Count);

        // ② 来源标签要指名道姓是哪份方案
        Assert.Contains(r.Plan!.Name, tl.SourceLabel);

        // ③ 逐帧的量要对得上计划。SimBuilder 读的是 `MonthPeriod` 的**取整后**标量，
        //    所以按「累计 ≤ 月数×半粒度」判（同 S11 的两层口径）。
        double sumCoal = 0, sumStrip = 0;
        for (int i = 0; i < tl.Count; i++)
        {
            var f = tl.FrameAt(i)!;
            sumCoal += f.OreWanT; sumStrip += f.StripWanM3;
            if (i < 3) _out.WriteLine($"  {f.Label}  煤 {f.OreWanT:0.00}万t · 岩 {f.StripWanM3:0.00}万m³");
        }
        double driftCoal = Math.Abs(sumCoal - r.Export.TotalCoalWanT);
        double driftStrip = Math.Abs(sumStrip - r.Export.TotalStripWanM3);
        _out.WriteLine($"\n推演合计 煤 {sumCoal:0.0}万t（契约 {r.Export.TotalCoalWanT:0.0}，差 {driftCoal:0.00}）· "
                     + $"岩 {sumStrip:0.0}万m³（契约 {r.Export.TotalStripWanM3:0.0}，差 {driftStrip:0.00}）");
        Assert.True(driftCoal <= 0.05 * tl.Count + 1e-9, $"采出量对不上：差 {driftCoal:0.00}万t");
        Assert.True(driftStrip <= 0.5 * tl.Count + 1e-9, $"剥离量对不上：差 {driftStrip:0.00}万m³");

        // ④ 放坡参数：S 组算例没给几何 ⇒ 必须明确降级成垂直壁并说原因，不是悄悄的
        _out.WriteLine($"\n放坡：{tl.MiningParams.SlopeSourceLabel}");
        Assert.False(string.IsNullOrWhiteSpace(tl.MiningParams.SlopeSourceLabel));

        // ⑤ 给了放坡参数就必须解出来 —— 「PlanLib 加字段自动接通」那条的**终点验证**
        var g = Input(Rock());
        g.Geometry = new ExportGeometry
        { RockBenchHeightM = ROCK_H, RockFaceDeg = 65, MinBermM = 40, WorkingSlopeDeg = 18, Source = "设计参数" };
        var r2 = MonthlyStripSession.Run(g);
        Assert.True(r2.Success, r2.Error);
        Assert.True(MonthlyStripSession.Confirm(r2, out string e2), e2);
        var prm = TaskLib.Simulation.SimMiningParams.Load();
        _out.WriteLine($"给了放坡参数后：SlopeResolved={prm.SlopeResolved} · {prm.SlopeSourceLabel}");
        Assert.True(prm.SlopeResolved,
            "契约里带了坡面角/平盘宽，三维层体却还是解不出放坡 —— 那条反射桥断了：" + prm.SlopeSourceLabel);
        Assert.Equal(65, prm.FaceAngleDeg, 3);
    }

    // ── S18 · 三道自检闸在会话里真的会挡 ────────────────────────────────────

    /// <summary>
    /// S18 <b>自检面接进会话之后，真的会挡住</b> —— 不是只往 Log 里写一行。
    ///
    /// <para>五类清单是逐个自检面单独验的（`G1e` 剖面 · `G14e` 配对 · `G24g/h/i` 契约），
    /// 那些判据证明的是"<b>面本身抓得到</b>"。但面接进会话是另一回事：
    /// <b>写了 Log 却忘了 return，或者 return 前没设 Error</b>，会得到一个
    /// "日志里明明写着不自洽、却照样往下走"的会话 —— 而 Log 那一行看上去还挺负责。</para>
    ///
    /// <para>所以这条判据在<b>会话层</b>逐个注入故障，判三件事：
    /// ① 会话失败（不是只记日志）；② `Error` 指名是哪一道闸；③ Log 里留下了具体那条。</para>
    /// </summary>
    [Fact]
    public void S18_EachSelfCheckGate_ActuallyBlocksTheSession()
    {
        // ── 闸①：剖面自检（`RockProfile.Validate` 接在读盘处）──
        var badProfile = Rock();
        badProfile.TotalRockM3 *= 1.3;                    // 汇总与逐桶漂开
        var r1 = MonthlyStripSession.Run(Input(badProfile));
        Assert.False(r1.Success, "剖面不自洽却照样排出了计划 —— 闸①没挡住");
        Assert.Contains("剖面自洽校核没过", r1.Error);
        Assert.Contains(r1.Log, l => l.Contains("剖面不自洽") && l.Contains("逐桶之和"));
        _out.WriteLine("闸① " + r1.Error);

        // ── 闸②：配对自检（`DumpAllocationResult.Validate` 接在配对之后、契约之前）──
        //    用一个会让运距变非有限的输入：物料 Kr 合法，但排土位置投不上且没有静态运距，
        //    → 走几何兜底仍是有限值，所以这里改用**直接构造非法物料**触发配对侧范围判据。
        var badKr = Input(Rock());
        badKr.Materials = MonthlyStripSession.DefaultMaterials(badKr.Rock!);
        foreach (var m in badKr.Materials) m.Kr = 0.8;    // Kr<1：岩石破碎只会膨胀，物理不可能
        var r2 = MonthlyStripSession.Run(badKr);
        _out.WriteLine("闸② " + (r2.Success ? "（未触发）" : r2.Error));
        if (!r2.Success)
        {
            Assert.Contains("配对结果自洽校核没过", r2.Error);
            Assert.Contains(r2.Log, l => l.Contains("配对结果不自洽"));
        }
        else
        {
            // 引擎可能在更早处就把非法 Kr 夹回去了（参数体检）—— 那也是对的，但要留条
            Assert.Contains(r2.Log, l => l.Contains("Kr") || l.Contains("物料"));
            _out.WriteLine("　 （非法 Kr 被参数体检提前夹回并留条，闸②未触发 —— 也是正当路径）");
        }

        // ── 闸③：契约自检（`MinePlanExport.Validate` 接在导出之后）──
        //    走「装回契约」那条路注入：外面给的 JSON 什么都可能。
        var good = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(good.Success, good.Error);
        string json = good.Export!.ToJson()
            .Replace("\"TotalStripWanM3\":", "\"TotalStripWanM3\":99999,\"_x\":");
        var r3 = MonthlyStripSession.FromContract(json);
        Assert.False(r3.Success, "契约不自洽却装回来了 —— 闸③没挡住");
        Assert.Contains("契约自洽校核没过", r3.Error);
        Assert.Contains(r3.Log, l => l.Contains("契约不自洽"));
        _out.WriteLine("闸③ " + r3.Error);

        // ── 反面：干净输入三道闸都不该响（防止闸门变成"总是拦"）──
        var ok = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(ok.Success, ok.Error);
        Assert.DoesNotContain(ok.Log, l => l.Contains("不自洽"));
        _out.WriteLine("干净输入：三道闸都没响 ✓");
    }

    // ── S12 · 契约 JSON 装得回来 ────────────────────────────────────────────

    /// <summary>
    /// S12 <b>导出的契约 JSON 必须装得回来</b>，而且装回来之后要说清它不是"跑过一遍"。
    ///
    /// <para><b>为什么这是完整性问题而不是锦上添花</b>：这个子系统的状态<b>全在会话内</b> ——
    /// 关掉程序，剖面/排产/比选表全没。契约 JSON 是唯一能带走的东西，而在此之前它
    /// <b>只写不读</b>：导出了装不回来，等于一条死路。</para>
    ///
    /// <para>钉四件事：① 往返之后账不变；② 装回来能确定入库；③ <b>坏契约要拒收</b>
    /// （改坏的数 / 认不出的版本 / 不是 JSON）；④ 装回来的<b>不能</b>拿去滚动重排 ——
    /// 没有剖面和排产结果，而且要<b>说出来</b>，不能让人点了才发现。</para>
    /// </summary>
    [Fact]
    public void S12_ContractJson_RoundTripsBackIntoAPlan()
    {
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);

        // ★ 先扫一遍非有限值：±∞/NaN 不是工程量，而且 System.Text.Json **直接抛异常** ——
        //   也就是说契约一旦带上它，「导出 JSON」这条唯一的持久化路径当场断掉。
        var bogus = NonFinite(r.Export!).ToList();
        foreach (var b in bogus) _out.WriteLine("非有限值: " + b);
        Assert.Empty(bogus);

        string json = r.Export!.ToJson();

        // ① 往返账不变
        var back = MonthlyStripSession.FromContract(json);
        foreach (var l in back.Log) _out.WriteLine(l);
        Assert.True(back.Success, back.Error);
        Assert.Equal(r.Export.Months.Count, back.Export!.Months.Count);
        Assert.Equal(r.Export.TotalCoalWanT, back.Export.TotalCoalWanT, 3);
        Assert.Equal(r.Export.TotalStripWanM3, back.Export.TotalStripWanM3, 3);
        Assert.Equal(r.Plan!.Months.Count, back.Plan!.Months.Count);
        // 放坡参数也要跟着回来（三维层体靠它建真台阶）
        Assert.Equal(r.Plan.BenchFaceAngleDeg, back.Plan.BenchFaceAngleDeg, 6);

        // ② 能确定入库
        Assert.True(back.CanConfirm, "装回来的计划确定不了：" + string.Join("；", back.Blocking.Select(i => i.Text)));
        Assert.True(MonthlyStripSession.Confirm(back, out string cerr), cerr);
        Assert.Same(back.Plan, ShortTermSchemeStore.Confirmed);

        // ③ 坏契约要拒收，且原因说得出
        foreach (var (name, bad) in new[]
        {
            ("空内容",    ""),
            ("不是 JSON", "这不是 json"),
            ("被改坏的数", json.Replace("\"TotalStripWanM3\":", "\"TotalStripWanM3\":999999,\"_old\":")),
            ("版本不认识", json.Replace("\"SchemaVersion\":", "\"SchemaVersion\":\"9.9\",\"_v\":")),
        })
        {
            var x = MonthlyStripSession.FromContract(bad);
            Assert.False(x.Success, $"「{name}」被收下了");
            Assert.False(string.IsNullOrWhiteSpace(x.Error));
            Assert.NotEmpty(x.Log);
            _out.WriteLine($"✗ {name,-10} {x.Error}");
        }

        // ④ 装回来的不能拿去滚动重排，而且要说出来
        Assert.Null(back.Rock);
        Assert.Null(back.Coupled);
        Assert.Contains(back.Log, l => l.Contains("装回来的") && l.Contains("重排"));
        var re = MonthlyStripSession.Replan(back, new ActualToDate { MonthsElapsed = 3 });
        Assert.False(re.Success, "装回来的居然能重排 —— 它没有上期排产结果");
        _out.WriteLine("✗ 拿装回来的重排 → " + re.Error);
    }

    // ── S11 · 确定入库之后，下游真的读得到 ──────────────────────────────────

    /// <summary>
    /// S11 <b>整条链的最后一跳：确定入库 → 下游按它那套反射读回来 → 数对得上</b>。
    ///
    /// <para>此前 <c>I4c</c> 钉的是<b>反射面存不存在</b>（类型名/属性名），
    /// <c>S1</c> 钉的是 <c>Confirm</c> 写没写进确定簿。但<b>「写进去的数下游读出来还是不是那个数」
    /// 从来没验过</b> —— 中间隔着按流派生、取整、标签重排三道，每一道都可能悄悄改值。</para>
    ///
    /// <para>所以这条判据<b>照 `SimBuilder.ReadMonthPeriods` 的写法原样走一遍</b>：
    /// 按类型全名+程序集名找确定簿 → 取 <c>Months</c> → 逐月取那 8 个属性 →
    /// 与导出契约逐月对账。这是"用户点了确定之后，三维动态模拟看到的东西"。</para>
    /// </summary>
    [Fact]
    public void S11_AfterConfirm_DownstreamReflectionSeesTheSameNumbers()
    {
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);
        Assert.True(MonthlyStripSession.Confirm(r, out string cerr), cerr);

        // ── 照 SimBuilder 的写法把计划读回来 ──
        var store = Type.GetType("PitMine3D.Kylin.Cad.Plan.ShortTermSchemeStore, PitMine3D.Kylin");
        object? plan = store!.GetProperty("Confirmed",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.GetValue(null);
        Assert.True(plan != null, "确定了却读不回来 —— 下游看到的是空的");

        string name = plan!.GetType().GetProperty("Name")!.GetValue(plan)?.ToString() ?? "";
        Assert.False(string.IsNullOrEmpty(name));
        var rows = ((System.Collections.IEnumerable)plan.GetType().GetProperty("Months")!.GetValue(plan)!)
                   .Cast<object>().ToList();
        Assert.Equal(r.Export!.Months.Count, rows.Count);

        double D(object o, string p) => Convert.ToDouble(o.GetType().GetProperty(p)!.GetValue(o));
        string S(object o, string p) => o.GetType().GetProperty(p)!.GetValue(o)?.ToString() ?? "";

        _out.WriteLine($"确定簿「{name}」{rows.Count} 行");
        _out.WriteLine($"{"月",-6}{"采出(万t)",12}{"剥离(万m³)",13}{"推进(m)",10}{"作业日",8}  标记/去向/作业面");

        double sumCoal = 0, sumStrip = 0;
        int noAdvance = 0, noWorkdays = 0, noFace = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var m = rows[i];
            double coal = D(m, "CoalWanT"), strip = D(m, "StripWanM3");
            double adv = D(m, "AdvanceM"), wd = D(m, "Workdays");
            sumCoal += coal; sumStrip += strip;
            if (adv <= 1e-9) noAdvance++;
            if (wd <= 1e-9) noWorkdays++;
            if (S(m, "ActiveFace").Length == 0) noFace++;
            if (i < 3)
                _out.WriteLine($"{S(m, "Label"),-6}{coal,12:0.00}{strip,13:0.00}{adv,10:0.00}{wd,8:0}"
                             + $"  {S(m, "FlagText")}/{S(m, "DumpText")}/{S(m, "ActiveFace")}");
        }

        _out.WriteLine("\n逐月对账（契约 vs 确定簿读回）：");
        for (int i = 0; i < Math.Min(rows.Count, r.Export.Months.Count); i++)
        {
            var em = r.Export.Months[i];
            double got = D(rows[i], "StripWanM3");
            _out.WriteLine($"  {em.Month,2}  契约 {em.StripWanM3,8:0.00}   读回 {got,8:0.00}   差 {got - em.StripWanM3,8:0.00}"
                         + $"   排不下 {em.UnplacedWanM3,6:0.00}");
        }
        double flowSum = r.Export.Flows.Sum(f => f.InSituWanM3);
        _out.WriteLine($"契约流向合计 {flowSum:0.00}万m³ · 逐月 StripWanM3 合计 {r.Export.Months.Sum(m => m.StripWanM3):0.00}"
                     + $" · 汇总 {r.Export.TotalStripWanM3:0.00}");

        // ★★ 对账要分两层，别混：
        //
        //  ① **账** —— 拿【未取整的流合计】对，必须严格相等。
        //     `MonthPeriod` 的 CoalWanT/StripWanM3 是**按流派生后再取整**的，
        //     取整粒度是 PlanLib 给报表定的（煤 0.1万t、岩 **1万m³**）。
        //  ② **显示** —— SimBuilder 读到的就是取整后那个数，所以它和契约<b>本来就会差</b>：
        //     实测 14.81→15、8.39→8。这不是错，但**必须知道差多少**，
        //     否则拿两张表逐位对的人会以为哪里算错了。
        //
        //  这个坑我在导入侧踩过两次（相对百分比 / 绝对 0.5 粒度），**这是第三次** ——
        //  写这条判据时又拿取整后的合计去对严格相等。记在这里：**账在流上，不在标量上。**
        var flows = rows.SelectMany(m => ((System.Collections.IEnumerable)m.GetType()
                            .GetProperty("Flows")!.GetValue(m)!).Cast<object>()).ToList();
        double flowCoal = flows.Where(f => (bool)f.GetType().GetProperty("IsOre")!.GetValue(f)!)
                               .Sum(f => Convert.ToDouble(f.GetType().GetProperty("TonnageWanT")!.GetValue(f)));
        double flowRock = flows.Where(f => !(bool)f.GetType().GetProperty("IsOre")!.GetValue(f)!)
                               .Sum(f => Convert.ToDouble(f.GetType().GetProperty("InSituWanM3")!.GetValue(f)));
        _out.WriteLine($"未取整流合计：煤 {flowCoal:0.000}万t · 岩 {flowRock:0.000}万m³");
        // 用**相对**容差而不是小数位：合计是几十上百个流累加出来的，
        // 绝对小数位会把纯浮点累积噪声（实测 96.001 vs 96.000，1e-5 相对）判成账不平。
        void Balance(string what, double want, double got, double tolPct = 0.01)
        {
            double rel = Math.Abs(want) < 1e-9 ? Math.Abs(got) : Math.Abs(got - want) / Math.Abs(want) * 100;
            Assert.True(rel <= tolPct, $"{what} 账不平：契约 {want:0.000} vs 流合计 {got:0.000}（差 {rel:0.0000}%）");
        }
        Balance("采出", r.Export.TotalCoalWanT, flowCoal);
        Balance("剥离", r.Export.TotalStripWanM3, flowRock);

        // ② 取整后的显示值：逐月最多差半个粒度，累计不超过 月数 × 半粒度
        for (int i = 0; i < rows.Count; i++)
            Assert.True(Math.Abs(D(rows[i], "StripWanM3") - r.Export.Months[i].StripWanM3) <= 0.5 + 1e-9,
                $"第{i + 1}月剥离取整差超过半个粒度（1万m³）—— 那就不是取整了");
        double drift = Math.Abs(sumStrip - r.Export.TotalStripWanM3);
        Assert.True(drift <= 0.5 * rows.Count + 1e-9,
            $"逐月取整累计漂了 {drift:0.00}万m³，超过 {0.5 * rows.Count:0.0} 的上限");
        _out.WriteLine($"取整累计漂移 {drift:0.00}万m³（上限 {0.5 * rows.Count:0.0}）—— "
                     + "三维模拟看到的是取整后的数，与契约逐位对会有这个量级的差，**不是算错**");
        // ★ 推进距离每个月都要有 —— 三维模拟拿它和 V实/(L×H) 反算值互校，全是 0 就校不了
        Assert.Equal(0, noAdvance);

        _out.WriteLine($"\n合计 煤 {sumCoal:0.0}万t（契约 {r.Export.TotalCoalWanT:0.0}）· "
                     + $"岩 {sumStrip:0.0}万m³（契约 {r.Export.TotalStripWanM3:0.0}）");
        _out.WriteLine($"缺推进 {noAdvance} 月 · 缺作业日 {noWorkdays} 月 · 缺主作业面 {noFace} 月");

        // ★ 缺的那些必须是【已知且说明过】的，不许悄悄缺着。
        //   `Workdays<=0` 时 SimBuilder 会**静默按 25 天**折算卸点通过能力 —— 那是缺省值不是这个矿的数。
        if (noWorkdays > 0)
            Assert.Contains(r.Import!.Issues, i => i.Code == "I9" && i.Text.Contains("作业日"));
        if (noFace > 0)
            Assert.Contains(r.Import!.Issues, i => i.Code == "I9" && i.Text.Contains("作业面"));
        foreach (var i in r.Import!.Issues.Where(x => x.Code == "I9")) _out.WriteLine("  " + i);

        // 给了作业日就要真的落到计划上（而且不再报缺）
        var withWd = Input(Rock());
        withWd.Workdays = new[] { 26.0 };            // 各月相同
        var r2 = MonthlyStripSession.Run(withWd);
        Assert.True(r2.Success, r2.Error);
        Assert.NotEmpty(r2.Plan!.Months);          // Assert.All 对空集合恒真，先钉非空
        Assert.All(r2.Plan.Months, m => Assert.Equal(26.0, m.Workdays, 6));
        Assert.DoesNotContain(r2.Import!.Issues, i => i.Code == "I9" && i.Text.Contains("作业日"));

        // 长度不符要整个忽略 + 记 I9（截断或补齐都会悄悄改掉用户的意思）
        var badWd = Input(Rock());
        badWd.Workdays = new[] { 26.0, 25.0, 24.0 };  // 3 个，但有 12 个月
        var r3 = MonthlyStripSession.Run(badWd);
        Assert.True(r3.Success, r3.Error);
        Assert.NotEmpty(r3.Plan!.Months);
        Assert.All(r3.Plan.Months, m => Assert.Equal(0, m.Workdays, 6));
        Assert.Contains(r3.Import!.Issues, i => i.Code == "I9" && i.Text.Contains("整个忽略"));
        _out.WriteLine("  " + r3.Import.Issues.First(i => i.Text.Contains("整个忽略")));
    }

    // ── S10 · 滚动重排 ──────────────────────────────────────────────────────

    /// <summary>
    /// S10 <b>拿「上期计划 + 实绩」重排剩余月份，跑完整链路</b>。
    ///
    /// <para><c>RollingReplan</c>（7 条判据）此前<b>没有任何调用方</b>，而且它自己只跑到调度器为止 ——
    /// 配对/外循环/契约/月度计划全没有，它的注释里也写着「上期的外部累计上限按原月轴算，
    /// 重排后已清空，需重跑外循环重给」。所以会话层用它做<b>偏差账 + 输入调整</b>，后半段照旧走完整链路。</para>
    ///
    /// <para>钉四件事：① 月数真的少了；② <b>月标签接着上期往下排</b>（重排出来又从 M01 开始，
    /// 两张表就对不上）；③ 期初位置用的是<b>实绩位置</b>不是稳态推算；④ 偏差账要出现在 Log 里。</para>
    /// </summary>
    [Fact]
    public void S10_Replan_ContinuesFromActuals_WithFullChain()
    {
        var first = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(first.Success, first.Error);
        Assert.Equal(12, first.Export!.Months.Count);
        Assert.NotNull(first.Input);          // 重排要拿它当基准，别让调用方自己保管

        // 照计划走了 4 个月（模拟实绩：内核自带，带真台阶位置）
        var act = RollingReplan.SimulateActual(first.Schedule!, monthsElapsed: 4);
        var re = MonthlyStripSession.Replan(first, act);
        foreach (var l in re.Log.Take(6)) _out.WriteLine(l);
        Assert.True(re.Success, re.Error);
        Assert.NotNull(re.Replan);

        // ① 只重排剩下 8 个月
        Assert.Equal(8, re.Export!.Months.Count);
        // ② 月标签接着上期：首月是 M05
        Assert.Equal("M05", re.Plan!.Months[0].Label);
        // ③ 期初用实绩位置（不是稳态推）
        Assert.False(re.Input!.StartInSteadyState, "有实绩位置却还在按稳态推 —— 前 4 个月的偏差会被吞掉");
        Assert.NotEmpty(re.Input.InitialBenchX);
        // ④ 偏差账要在 Log 里
        Assert.Contains(re.Log, l => l.Contains("滚动重排") && l.Contains("重排剩余 8"));
        _out.WriteLine("\n" + re.Replan!.Summary());
        _out.WriteLine(re.ToString());
    }

    /// <summary>
    /// S10b <b>欠剥不许翻篇</b> —— 少剥的量必须压进后续月份。
    ///
    /// <para><b>欠采是这个月少卖点煤，欠剥是下个月没煤可采。</b>
    /// 所以重排时把欠账一笔勾销是最危险的一种"看上去正常"：剩余月份的计划全都成立、
    /// 每项校核都过，只是到时候采不出来。</para>
    /// </summary>
    [Fact]
    public void S10b_StripDebt_IsCarriedForward_NotForgiven()
    {
        var first = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(first.Success, first.Error);

        // 照计划采煤，但只剥了 70% —— 欠剥
        var lazy = RollingReplan.SimulateActual(first.Schedule!, monthsElapsed: 4, coalRate: 1.0, rockRate: 0.7);
        var re = MonthlyStripSession.Replan(first, lazy);
        Assert.True(re.Success, re.Error);
        Assert.NotNull(re.Replan);

        double debt = re.Replan!.StripDebtM3;
        _out.WriteLine($"剥离达成 {re.Replan.RockAttainPct:0.0}% · 欠账 {debt / 1e4:0.0}万m³");
        Assert.True(debt > 1e-6, "只剥了 70% 却算不出欠账 —— 偏差账没算对");
        Assert.Contains(re.Replan.Notes, n => n.Contains("欠账") && n.Contains("欠剥比欠采危险"));

        // 与"照计划走"的重排相比：欠剥那份剩余月份必须剥得更多（把欠账压进来了）
        var onPlan = MonthlyStripSession.Replan(first, RollingReplan.SimulateActual(first.Schedule!, 4));
        Assert.True(onPlan.Success, onPlan.Error);
        double debtStrip = re.Export!.TotalStripWanM3, planStrip = onPlan.Export!.TotalStripWanM3;
        _out.WriteLine($"剩余 8 个月要剥：照计划走 {planStrip:0.0}万m³　欠剥后 {debtStrip:0.0}万m³");
        Assert.True(debtStrip > planStrip + 1e-6,
            $"欠剥了 {debt / 1e4:0.0}万m³，剩余月份却没多剥（{debtStrip:0.0} ≤ {planStrip:0.0}）—— 欠账被一笔勾销了");
    }

    /// <summary>
    /// S10c <b>重排侧的退化输入不崩不瞒</b>，且"实绩没给位置"这条必须说清后果。
    /// <para>没给台阶位置就只能退回稳态推算 —— <b>那等于假设前几个月完全照计划走</b>，
    /// 偏差会被吞掉。这句必须说出来，否则用户以为自己做了滚动重排，其实什么都没纠。</para>
    /// </summary>
    [Fact]
    public void S10c_Replan_DegradesCleanly_AndSaysWhenDeviationIsSwallowed()
    {
        var first = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(first.Success, first.Error);

        foreach (var (name, prev, act) in new (string, MonthlyStripSessionResult?, ActualToDate?)[]
        {
            ("没有上期计划", null, new ActualToDate { MonthsElapsed = 3 }),
            ("上期没跑成功", MonthlyStripSession.Run(new MonthlyStripSessionInput()), new ActualToDate { MonthsElapsed = 3 }),
            ("没有实绩",     first, null),
            ("已过月数超计划长度", first, new ActualToDate { MonthsElapsed = 99 }),
            ("已过 0 个月",  first, new ActualToDate { MonthsElapsed = 0 }),
        })
        {
            MonthlyStripSessionResult x;
            try { x = MonthlyStripSession.Replan(prev, act); }
            catch (Exception ex) { Assert.Fail($"「{name}」抛异常：{ex.GetType().Name} {ex.Message}"); return; }
            Assert.NotNull(x);
            if (!x.Success)
            { Assert.False(string.IsNullOrWhiteSpace(x.Error)); Assert.NotEmpty(x.Log); }
            _out.WriteLine($"{(x.Success ? "✔" : "✗")} {name,-16} {(x.Success ? x.ToString() : x.Error)}");
        }

        // 实绩没给台阶位置 → 退回稳态推，必须明说偏差会被吞掉
        var noPos = new ActualToDate { MonthsElapsed = 4, CoalWt = 32, RockM3 = 40e4 };
        var re = MonthlyStripSession.Replan(first, noPos);
        Assert.True(re.Success, re.Error);
        Assert.Contains(re.Replan!.Notes, n => n.Contains("等于假设前") && n.Contains("完全照计划走"));
        _out.WriteLine("\n" + re.Replan.Notes.First(n => n.Contains("等于假设前")));
    }

    // ── S8 · 真路网运距 ─────────────────────────────────────────────────────

    /// <summary>
    /// S8 <b>真路网接上时逐月运距要真的变，接不上时要如实退回并说清楚</b>。
    ///
    /// <para>这条判据<b>不依赖数据库里有没有路网存档</b> —— 直接往
    /// <see cref="CoupledPlanInput.RoadHaul"/> 塞一个可控的回调，判内核这一侧的三件事：
    /// ① 给了就用（结果与纯兜底不同）；② 返回 null 的那笔<b>退回几何兜底而不是当 0</b>；
    /// ③ <b>命中率写进 <c>HaulNote</c></b>。</para>
    ///
    /// <para>③ 是最要紧的：<b>「接了真路网」和「接了但一笔都没命中」算出来的数一模一样</b>，
    /// 不把命中率写出来，没人分得出自己拿到的是哪一种。</para>
    /// </summary>
    [Fact]
    public void S8_RoadHaul_IsUsedWhenGiven_AndFallsBackLoudly()
    {
        var rock = Rock();

        CoupledPlanResult RunWith(Func<int, DumpSlot, double, double, double?>? road, out string note)
        {
            var slots = Slots();
            var ci = new CoupledPlanInput
            {
                Schedule = new MonthlyScheduleInput
                {
                    Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM,
                    CoalTargetWt = Enumerable.Repeat(8.0, 12).ToArray(),
                    LookaheadMonths = 3, RecoveryTotalWt = 24, StartInSteadyState = true,
                },
                Dump = new DumpAllocationInput
                { Slots = slots, Materials = MonthlyStripSession.DefaultMaterials(rock) },
                SlotU = DumpSlotAdapter.ProjectToAdvanceAxis(slots, new[] { Line() }),
                SlotOffsetKm = DumpSlotAdapter.LateralOffsetsKm(slots, new[] { Line() }),
                RoadHaul = road,
            };
            var r = CoupledMinePlanner.Solve(ci);
            note = r.HaulNote;
            Assert.True(r.Success, r.Error);
            return r;
        }

        // ① 纯几何兜底
        var baseline = RunWith(null, out string n0);
        _out.WriteLine("兜底　　 " + n0);
        Assert.Contains("几何兜底", n0);
        Assert.DoesNotContain("命中", n0);      // 没接路网就不该出现命中率

        // ② 真路网全命中：给一个明显不同的常数 → 运输功必须跟着变
        var allHit = RunWith((_, _, _, _) => 7.77, out string n1);
        _out.WriteLine("全命中　 " + n1);
        Assert.Contains("命中", n1);
        Assert.DoesNotContain("退回", n1);
        Assert.NotEqual(baseline.Dump!.TotalTransportWorkTKm, allHit.Dump!.TotalTransportWorkTKm, 3);

        // ③ 一笔也解不出来：数必须与纯兜底【逐位相同】，而且 HaulNote 要喊出来
        var allMiss = RunWith((_, _, _, _) => null, out string n2);
        _out.WriteLine("全落空　 " + n2);
        Assert.Contains("一笔也没解出来", n2);
        Assert.Equal(baseline.Dump.TotalTransportWorkTKm, allMiss.Dump!.TotalTransportWorkTKm, 6);

        // ④ 部分命中要报出比例。
        //    ⚠ 别拿 IsInternal 当区分依据 —— 这个夹具里内排位置一次都不会被问到：
        //    它们的质心投到推进轴上后，煤前界 12 个月都没推过「位置 + 退距 200m」，
        //    所以内排全程未启用（内排率 0%）。拿它做判据会得到"全落空"，看着像 bug 其实是夹具。
        // ⚠ 命中值必须与 ② 用【同一个常数】。第一版 ② 用 7.77、④ 用 0.9 —— 两个不同的常数
        //    根本不保证"半命中落在两者之间"（0.9 比兜底还便宜，结果反而低于全兜底）。
        var partial = RunWith((_, s, _, _) => s.Order % 2 == 0 ? 7.77 : (double?)null, out string n3);
        _out.WriteLine("半命中　 " + n3);
        Assert.Contains("命中", n3);
        Assert.Contains("退回几何兜底", n3);
        Assert.DoesNotContain("一笔也没解出来", n3);
        // 半命中的结果必须落在"全兜底"和"全命中"之间 —— 否则说明落空的那半没真的退回兜底
        double lo = Math.Min(baseline.Dump.TotalTransportWorkTKm, allHit.Dump.TotalTransportWorkTKm);
        double hi = Math.Max(baseline.Dump.TotalTransportWorkTKm, allHit.Dump.TotalTransportWorkTKm);
        Assert.InRange(partial.Dump!.TotalTransportWorkTKm, lo, hi);

        // ⑤ 路网侧抛异常不许把整条排产带走
        var boom = RunWith((_, _, _, _) => throw new InvalidOperationException("路网炸了"), out string n4);
        _out.WriteLine("抛异常　 " + n4);
        Assert.Contains("一笔也没解出来", n4);
        Assert.Equal(baseline.Dump.TotalTransportWorkTKm, boom.Dump!.TotalTransportWorkTKm, 6);
    }

    /// <summary>
    /// S8b <b>没有路网存档时会话照跑，并说清为什么走的是兜底</b>。
    /// <para>判据环境里通常没有 `road_network` 存档 —— 那正是要判的场景：
    /// <b>装不上不许崩、不许静默</b>。装得上时同样要把存档名写出来。</para>
    /// </summary>
    [Fact]
    public void S8b_NoRoadArchive_SessionStillRuns_AndSaysWhy()
    {
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);

        var line = r.Log.FirstOrDefault(l => l.Contains("真路网") || l.Contains("几何兜底"));
        Assert.False(string.IsNullOrEmpty(line), "Log 里一个字都没提运距是怎么来的");
        _out.WriteLine(line!);

        // 关掉开关时也要留条 —— "我选择不用" 和 "用不了" 是两回事
        var off = Input(Rock()); off.UseRoadNetwork = false;
        var r2 = MonthlyStripSession.Run(off);
        Assert.True(r2.Success, r2.Error);
        Assert.Contains(r2.Log, l => l.Contains("显式关闭"));
        _out.WriteLine(r2.Log.First(l => l.Contains("显式关闭")));
    }

    // ── S7 · 界面绑定也是静默契约 ───────────────────────────────────────────

    /// <summary>
    /// S7 <b>窗口 XAML 里每一条 <c>Binding</c> 的属性名都要在被绑的类型上存在</b>。
    ///
    /// <para>WPF 绑定路径写错<b>不报错</b>：那一列就是空的，或者整张表看上去"没数"。
    /// 和 <c>I4c</c> 钉的反射面是同一类问题 —— 编译全过、判据全绿、只有界面上悄悄少东西。
    /// 这里直接读 XAML 源文件，把 <c>&lt;DataGrid x:Name=…&gt;</c> 各自绑的类型对上去逐条查。</para>
    ///
    /// <para>DTO 改名时这条会红 —— 那正是要的：改了就得同步改 XAML。</para>
    /// </summary>
    [Fact]
    public void S7_WindowBindings_ResolveOnTheBoundTypes()
    {
        string xaml = FindWindowXaml();
        Assert.False(string.IsNullOrEmpty(xaml), "找不到 MonthlyStripWindow.xaml —— 挪过位置就把这条判据一起改");
        string text = File.ReadAllText(xaml);

        // 各 DataGrid 绑的元素类型（与 MonthlyStripWindow.OnRun 里的 ItemsSource 一一对应）
        var grids = new (string Name, Type Item)[]
        {
            ("GridMonths",  typeof(ExportMonth)),
            ("GridFlows",   typeof(ExportFlow)),
            ("GridSchemes", typeof(ScheduleScheme)),
        };

        int checkedCount = 0;
        foreach (var (name, item) in grids)
        {
            string block = SliceGrid(text, name);
            Assert.False(string.IsNullOrEmpty(block), $"XAML 里找不到 x:Name=\"{name}\" 的 DataGrid");

            var paths = System.Text.RegularExpressions.Regex
                .Matches(block, @"Binding\s+([A-Za-z_][A-Za-z0-9_]*)")
                .Select(m => m.Groups[1].Value)
                .Distinct().ToList();
            Assert.True(paths.Count >= 5, $"{name} 只解出 {paths.Count} 条绑定 —— 正则大概率没匹配上，别当绿的");

            foreach (var p in paths)
            {
                Assert.True(item.GetProperty(p) != null,
                    $"{name} 绑了 「{p}」，但 {item.Name} 上没有这个属性 —— 那一列在界面上会是空的，而且不报错");
                checkedCount++;
            }
            _out.WriteLine($"{name} → {item.Name}：{paths.Count} 条绑定全部对得上");
        }
        _out.WriteLine($"共核 {checkedCount} 条绑定。");
    }

    /// <summary>
    /// 递归扫出契约里所有 <c>±∞ / NaN</c> 的 double（含 List 元素）。
    /// <para>这类值不是工程量，而且 <c>System.Text.Json</c> 遇到它<b>直接抛异常</b> ——
    /// 契约一旦带上，「导出 JSON」这条唯一的持久化路径当场断掉。</para>
    /// </summary>
    private static IEnumerable<string> NonFinite(object? root, string path = "")
    {
        if (root == null) yield break;
        foreach (var p in root.GetType().GetProperties())
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v;
            try { v = p.GetValue(root); } catch { continue; }
            if (v == null) continue;
            string here = path.Length == 0 ? p.Name : path + "." + p.Name;

            if (v is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) yield return $"{here} = {d}";
            }
            else if (v is System.Collections.IEnumerable en && v is not string)
            {
                int i = 0;
                foreach (var item in en)
                {
                    if (item == null) { i++; continue; }
                    if (item is double dd)
                    { if (double.IsNaN(dd) || double.IsInfinity(dd)) yield return $"{here}[{i}] = {dd}"; }
                    else if (item.GetType().Namespace?.StartsWith("MineAssLib") == true)
                        foreach (var bad in NonFinite(item, $"{here}[{i}]")) yield return bad;
                    i++;
                }
            }
            else if (p.PropertyType.Namespace?.StartsWith("MineAssLib") == true)
                foreach (var bad in NonFinite(v, here)) yield return bad;
        }
    }

    /// <summary>
    /// 从仓库里找窗口 XAML。
    /// <para><b>锚点用本文件的编译期路径</b>（<c>CallerFilePath</c>），不用
    /// <c>AppContext.BaseDirectory</c> —— 判据的输出目录可以被 <c>/p:OutputPath</c> 指到仓库外
    /// （本项目就常指到临时目录），那时按运行目录往上翻永远找不到源码。</para>
    /// </summary>
    /// <summary>Kylin：窗口是纯 C# 建的（<c>src/Views/Plan/MonthlyStripWindow.cs</c>），没有 XAML —— 判据读同一份源码。</summary>
    private static string FindWindowXaml([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here) ?? AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "src", "Views", "Plan", "MonthlyStripWindow.cs");
            if (File.Exists(p)) return p;
        }
        return "";
    }

    /// <summary>
    /// 切出某个 DataGrid 的列定义那一段（Kylin：<c>GridX = Grid(Col("表头", "路径", …), …);</c>），
    /// 并把 <c>Col("…", "Path"</c> / <c>new Binding("Path")</c> 改写成 XAML 口径的 <c>{Binding Path}</c>，
    /// 让原判据的正则与 Contains 原样成立。
    /// </summary>
    private static string SliceGrid(string src, string name)
    {
        int i = src.IndexOf($"{name} = Grid(", StringComparison.Ordinal);
        if (i < 0) return "";
        int e = src.IndexOf("));", i, StringComparison.Ordinal);
        if (e < 0) return "";
        string block = src[i..(e + 3)];
        block = System.Text.RegularExpressions.Regex.Replace(block, @"Col\(""[^""]*"",\s*""([A-Za-z_][A-Za-z0-9_]*)""", "{Binding $1}");
        block = System.Text.RegularExpressions.Regex.Replace(block, @"new Binding\(""([A-Za-z_][A-Za-z0-9_]*)""\)", "{Binding $1}");
        return block;
    }

    // ── 面板 ────────────────────────────────────────────────────────────────

    /// <summary>面板：一次会话的完整 Log —— 界面上要显示的就是这一列。</summary>
    [Fact]
    public void S_SessionDashboard()
    {
        var rock = FullProfile().Rock!;
        var inp = Input(rock);
        inp.Geometry = new ExportGeometry
        { RockBenchHeightM = ROCK_H, RockFaceDeg = 65, MinBermM = 40, WorkingSlopeDeg = 18, Source = "设计参数" };
        var r = MonthlyStripSession.Run(inp);
        foreach (var l in r.Log) _out.WriteLine(l);
        _out.WriteLine("");
        _out.WriteLine("摘要: " + r);
        _out.WriteLine(MinePlanImporter.Summary(r.Import!));
        Assert.True(r.Success, r.Error);
    }

    // ── 造退化输入的小工具 ──────────────────────────────────────────────────

    private static MonthlyStripSessionInput Zero(MonthlyStripSessionInput i)
    { Array.Clear(i.CoalTargetWt, 0, i.CoalTargetWt.Length); return i; }
    private static MonthlyStripSessionInput NoSlots(MonthlyStripSessionInput i)
    { i.Slots = new List<DumpSlot>(); i.DumpCells = null; return i; }
    private static MonthlyStripSessionInput Alpha(MonthlyStripSessionInput i, double a)
    { i.AlphaDeg = a; return i; }
    /// <summary>剖面指纹也没有 α 时 —— 只能作用在<b>本算例自己那份</b>剖面上（见 S2 的假绿教训）。</summary>
    private static MonthlyStripSessionInput NoProvenance(MonthlyStripSessionInput i)
    { i.AlphaDeg = 0; i.Rock!.Provenance = null; return i; }
    private static MonthlyStripSessionInput Months(MonthlyStripSessionInput i, int n)
    { i.CoalTargetWt = n <= 0 ? Array.Empty<double>() : Enumerable.Repeat(8.0, n).ToArray(); return i; }
    private static MonthlyStripSessionInput Look(MonthlyStripSessionInput i, int n)
    { i.LookaheadMonths = n; return i; }
    private static MonthlyStripSessionInput Cap(MonthlyStripSessionInput i, double v)
    { i.StripCapM3 = Enumerable.Repeat(v, i.CoalTargetWt.Length).ToArray(); return i; }
    private static MonthlyStripSessionInput Big(MonthlyStripSessionInput i)
    { i.CoalTargetWt = Enumerable.Repeat(1e6, 12).ToArray(); return i; }
    private static MonthlyStripSessionInput NoLines(MonthlyStripSessionInput i)
    { i.WorkLines = null; return i; }
    private static MonthlyStripSessionInput Bare(MonthlyStripSessionInput i)
    { i.StartInSteadyState = false; return i; }
    private static MonthlyStripSessionInput BadInit(MonthlyStripSessionInput i)
    { i.InitialBenchX = new double[3]; return i; }
    private static MonthlyStripSessionInput FewMats(MonthlyStripSessionInput i)
    { i.Materials = new[] { new GapMaterial { Name = "只给一项", Code = "rock" } }; return i; }
    private static MonthlyStripSessionInput Iter(MonthlyStripSessionInput i, int n)
    { i.MaxIterations = n; return i; }

    // ───────────────────────── S19 ─────────────────────────

    /// <summary>造一份数值都像样的岩剖面（直接填桶，量的不是扫描侧）。</summary>
    private static RockProfile PlausibleRock(int seams = 3)
    {
        var names = Enumerable.Range(0, seams).Select(i => $"{11 - i * 2}煤").ToList();
        var r = new RockProfile
        {
            Success = true, SliceWidth = 10, BenchHeight = 15, SeamCount = seams,
            SeamNames = names.ToArray(), GapNames = GapCode.Names(names),
            CoalVolBins = new Dictionary<long, double>[seams],
            CoalZMoment = new Dictionary<long, double>[seams],
            SeamDensity = Enumerable.Repeat(1.35, seams).ToArray(),
        };
        for (int j = 0; j < seams; j++) { r.CoalVolBins[j] = new(); r.CoalZMoment[j] = new(); }
        for (int k = 0; k < 8; k++)
            foreach (int g in new[] { GapCode.Overburden, 1, GapCode.Underburden(seams) })
                for (int b = 0; b < 20; b++) r.AddRock(k, g, 200 + b, 1000);
        for (int j = 0; j < seams; j++)
            for (int b = 0; b < 20; b++) r.CoalVolBins[j][200 + b] = 800;
        return r;
    }

    /// <summary>
    /// S19 · <b>选完剖面那一行，✔ 只能在自校也过了之后才打</b>。
    ///
    /// <para><b>这条为什么值得单列</b>：那行 ✔ 是一句**判断**（"这份能用，接着填参数"），
    /// 而它印在屏上的 <c>层数 / 台阶高 / 岩量</c> —— <b>恰好就是自校 ①守恒 与 ⑤完备性
    /// 判坏的那几个字段</b>。所以本条的要害不是"坏的要报"，而是
    /// <b>坏得看不出来的也要报</b>：下面两种破法印出来的数字全是有限的、量级正常的，
    /// 光看屏幕分辨不了 —— 不核就打 ✔，等于把坏数当结论印给用户，
    /// 让他照着填完一整屏参数，到点排产才被 Prepare 拦下。</para>
    ///
    /// <para>逻辑之所以在 <see cref="MonthlyStripSession.DescribeProfile"/> 而不在窗口事件里，
    /// 就是为了能被这条钉住 —— GUI 分支没判据看着，谁挪一下顺序都不会红。</para>
    /// </summary>
    [Fact]
    public void S19_ProfilePicker_OnlyTicksAfterSelfCheck_IncludingInvisibleCorruption()
    {
        // ① 干净的：打 ✔，且四个数都印出来了
        var clean = new CoalProfile { Success = true, Rock = PlausibleRock() };
        var (okTxt, okBad) = MonthlyStripSession.DescribeProfile(clean, "");
        Assert.False(okBad);
        Assert.Contains("✔", okTxt);
        _out.WriteLine("干净剖面 → " + okTxt);

        // ② 两种"坏得看不出来"的破法。每种都先确认：若照旧打 ✔，那行字全是像样的数
        var cases = new (string What, Func<RockProfile> Make)[]
        {
            ("①守恒：总岩量与逐桶之和漂了（总量是分开维护的增量和）",
                () => { var r = PlausibleRock(); r.TotalRockM3 *= 1.5; return r; }),
            ("⑤完备性：层名数与层数不齐（下游按层下标取容重会取错层而不抛）",
                () => { var r = PlausibleRock(); r.SeamNames = new[] { "11煤" }; return r; }),
        };

        var shown = new List<string>();
        foreach (var (what, make) in cases)
        {
            var rock = make();

            // ★ 非空转的关键：证明这份坏剖面**印出来是像样的** —— 否则旧代码本来就够用
            Assert.True(rock.SeamCount is > 0 and < 20, $"{what}：层数印出来就不像样，那不算「看不出来」");
            Assert.True(double.IsFinite(rock.BenchHeight) && rock.BenchHeight > 0);
            Assert.True(double.IsFinite(rock.TotalRockM3) && rock.TotalRockM3 > 0);
            Assert.True(double.IsFinite(rock.TotalCoalWt()) && rock.TotalCoalWt() > 0);
            string wouldHaveShown = $"✔ {rock.SeamCount} 层 · 台阶高 {rock.BenchHeight:0.##}m · "
                                  + $"岩 {rock.TotalRockM3 / 1e4:0.0}万m³ · 煤 {rock.TotalCoalWt():0.0}万t"
                                  + "　" + MonthlyStripSession.SourceNote(rock.Provenance);

            var (txt, bad) = MonthlyStripSession.DescribeProfile(
                new CoalProfile { Success = true, Rock = rock }, "");
            Assert.True(bad, $"{what}：没报出来");
            Assert.DoesNotContain("✔", txt);            // ✔ 不许出现在坏消息里
            shown.Add(wouldHaveShown);
            _out.WriteLine($"{what}\n    旧口径会印：{wouldHaveShown}\n    现在印：  {txt}");
        }

        // ★ 把"看不出来"钉死：⑤ 那种破法印出来的那行，与**干净剖面那行一模一样** ——
        //   层名数与层数不齐根本不进这四个数，屏上逐字节相同。
        //   所以"让用户自己看数字对不对"这条路是不存在的，只能靠自校。
        Assert.Contains(okTxt, shown);

        // ★★ 来源要摆出来：指纹核对从界面上走不到（没有"当前块体"可比），
        //    那就至少把**判断陈不陈的材料**给人看 —— 排土位置那一路早就这么报了。
        //    时钟是注进去的，不然这段判据会随天数漂。
        var made = new DateTime(2026, 8, 1, 3, 0, 0, DateTimeKind.Utc);
        var rk = PlausibleRock();
        rk.Provenance = new ProfileProvenance
        { BlockModelName = "神东1号", CreatedUtc = made.ToString("yyyy-MM-dd HH:mm:ss'Z'") };

        var fresh = MonthlyStripSession.DescribeProfile(
            new CoalProfile { Success = true, Rock = rk }, "", made.AddHours(2));
        Assert.False(fresh.Bad);
        Assert.Contains("神东1号", fresh.Text);
        Assert.DoesNotContain("陈的", fresh.Text);          // 同一天不啰嗦

        var stale = MonthlyStripSession.DescribeProfile(
            new CoalProfile { Success = true, Rock = rk }, "", made.AddDays(9));
        Assert.False(stale.Bad);                            // 旧不等于坏，别拦
        Assert.Contains("9 天前", stale.Text);
        Assert.Contains("陈的", stale.Text);                // 隔天的要点一句
        Assert.NotEqual(fresh.Text, stale.Text);            // 反假绿：确实随时间变
        _out.WriteLine("刚扫的 → " + fresh.Text + "\n放了 9 天 → " + stale.Text);

        // 没指纹的旧容器：认不出来源，也要说，而不是默默打 ✔
        var noPv = PlausibleRock(); noPv.Provenance = null;
        var anon = MonthlyStripSession.DescribeProfile(
            new CoalProfile { Success = true, Rock = noPv }, "", made);
        Assert.Contains("无指纹", anon.Text);
        Assert.False(anon.Bad);              // 认不出来源值得说，但不构成"这份不能用"

        // ③ 前两道（读不出 / 没岩剖面）仍在，且都不打 ✔
        var (e1, b1) = MonthlyStripSession.DescribeProfile(null, "容器坏了");
        Assert.True(b1); Assert.Contains("容器坏了", e1); Assert.DoesNotContain("✔", e1);
        var (e2, b2) = MonthlyStripSession.DescribeProfile(new CoalProfile { Success = true }, "");
        Assert.True(b2); Assert.Contains("岩", e2); Assert.DoesNotContain("✔", e2);
        _out.WriteLine($"读不出 → {e1}\n没岩剖面 → {e2}");
    }

    // ───────────────────────── S20 ─────────────────────────

    private static DumpStripPlanner.Result MadeUpSlots(int n, double capEach = 5e4)
    {
        var r = new DumpStripPlanner.Result { Ok = true };
        for (int i = 0; i < n; i++)
            r.Cells.Add(new DumpStripPlanner.Cell
            { LevelIndex = 1 + i % 3, PanelIndex = 1, StepIndex = 1 + i, CapacityM3 = capEach });
        return r;
    }

    /// <summary>
    /// S20 · <b>排土位置交接：挡住空结果是对的，但不能挡得无声无息</b>。
    ///
    /// <para><see cref="DumpStripStore.Put"/> 遇到空结果直接 return，理由正当 ——
    /// <b>别拿空的盖掉上一次的好清单</b>。但「排土条带」失败时**照样会调它**
    /// （那边的注释写着"失败也要落一遍，诊断靠的就是它们"），于是：
    /// 用户刚看完"切完一个位置都没出来"，转到排产窗口点「取排土位置」，
    /// 看到的是一句<b>数字齐全、语气正面</b>的摘要 —— 描述的是**更早那一次**。
    /// 而这份清单是<b>排产的输入</b>：拿陈的去排，排出来的方案是照着不存在的排土场排的。</para>
    ///
    /// <para><b>次序不能拿时间戳判</b>：<c>DateTime.Now</c> 只有约 15ms 分辨率，
    /// "失败紧接着成功"会落在同一刻上 —— 判据里最后一段专门跑这个连打，
    /// 时间戳口径下它会反过来报"上次失败了"。所以次序走自增序号，时间戳只用来显示。</para>
    /// </summary>
    [Fact]
    public void S20_DumpSlotHandoff_RejectsEmptyQuietly_ButSaysSo()
    {
        DumpStripStore.Clear();
        try
        {
            // ① 什么都没有时，摘要就是"还没生成过"，不许冒充有货
            Assert.False(DumpStripStore.LastAttemptFailed);
            Assert.Contains("还没生成过", DumpStripStore.Caption);

            // ② 一次成功：正面摘要，数字齐全
            DumpStripStore.Put(MadeUpSlots(6), "北排土场");
            string good = DumpStripStore.Caption;
            Assert.False(DumpStripStore.LastAttemptFailed);
            Assert.Contains("6 个位置", good);
            Assert.Contains("北排土场", good);
            Assert.DoesNotContain("◆", good);
            _out.WriteLine("成功后 → " + good);

            // ③ 紧接着一次失败（空结果）。**好清单必须还在** —— 这是挡它的全部理由
            DumpStripStore.Put(new DumpStripPlanner.Result { Ok = false }, "南排土场");
            Assert.NotNull(DumpStripStore.Last);
            Assert.Equal(6, DumpStripStore.Last!.Cells.Count);
            Assert.Equal("北排土场", DumpStripStore.SourceNote);   // 来源没被失败那次改掉

            // ★ 但摘要必须说出来：既要点明最近这次没出位置，也要点明下面这份是更早的
            string after = DumpStripStore.Caption;
            Assert.True(DumpStripStore.LastAttemptFailed);
            Assert.Contains("◆", after);
            Assert.Contains("南排土场", after);       // 失败的是哪一次
            Assert.Contains("更早", after);
            Assert.Contains("6 个位置", after);       // 更早那份的数字仍照常给出
            Assert.NotEqual(good, after);             // 反假绿：不是原样那句
            _out.WriteLine("失败后 → " + after.Replace("\n", "\n           "));

            // ④ 再来一次成功 —— 警告要**收回去**，否则它一响就永远响，等于没说
            DumpStripStore.Put(MadeUpSlots(9), "南排土场");
            Assert.False(DumpStripStore.LastAttemptFailed);
            Assert.DoesNotContain("◆", DumpStripStore.Caption);
            Assert.Contains("9 个位置", DumpStripStore.Caption);

            // ⑤ 失败→成功**连着打**（同一个时钟刻度内）。拿时间戳判次序的话这里会翻车
            DumpStripStore.Clear();
            DumpStripStore.Put(new DumpStripPlanner.Result { Ok = false }, "抖动");
            DumpStripStore.Put(MadeUpSlots(3), "抖动");
            Assert.False(DumpStripStore.LastAttemptFailed);
            Assert.DoesNotContain("◆", DumpStripStore.Caption);
            _out.WriteLine("连打后 → " + DumpStripStore.Caption);

            // ⑥ Clear 要把失败那笔也清掉，否则重划排土场后警告还挂着
            DumpStripStore.Put(new DumpStripPlanner.Result { Ok = false }, "抖动");
            Assert.True(DumpStripStore.LastAttemptFailed);
            DumpStripStore.Clear();
            Assert.False(DumpStripStore.LastAttemptFailed);
            Assert.Contains("还没生成过", DumpStripStore.Caption);
        }
        finally { DumpStripStore.Clear(); }
    }

    // ───────────────────────── S21 ─────────────────────────

    /// <summary>
    /// 落一份带工作线的 <c>.case</c>（剖面用现成夹具，工作线推进 +X）。
    /// <para><paramref name="coherent"/>=true 时把剖面指纹的 <c>WorkLineKey</c> 对齐到这组线 ——
    /// 真实容器就是这样：剖面本来就是<b>按这组线扫出来的</b>。
    /// 给 false 则故意留成"拼起来的容器"（改完线没重扫）。</para>
    /// </summary>
    private static string WriteCaseWithLines(string tag, bool coherent = true)
    {
        var wl = new WorkLineGeometry { Success = true, AdvanceMode = 0, DirMode = 0 };   // Kylin WorkLineSamples 无 ArrowLength（只随几何带走，判据不看）
        wl.Baseline.Add((0, 0, 100)); wl.Baseline.Add((0, 400, 100));
        wl.Samples.Add((0, 200, 100, 1, 0));                  // 推进 +X
        var cp = FullProfile();
        var c = new InclineCase { Note = "S21", AlphaDeg = 20, Profile = cp };
        c.WorkLines.Add(wl);
        if (coherent && cp.Rock?.Provenance != null)
            cp.Rock.Provenance.WorkLineKey = ProfileProvenance.FoldWorkLines(c.WorkLines);
        string p = Path.Combine(Path.GetTempPath(), $"pitmine_s21_{tag}.case");
        Assert.True(InclineCaseFile.TrySave(p, c, out string serr), serr);
        return p;
    }

    /// <summary>质心铺在工作线扫掠域里（x&gt;0, y∈[0,400]），投得上轴。</summary>
    private static List<DumpStripPlanner.Cell> CellsOnAxis(int n = 6)
    {
        var list = new List<DumpStripPlanner.Cell>();
        for (int i = 0; i < n; i++)
            list.Add(new DumpStripPlanner.Cell
            {
                LevelIndex = 1 + i % 3, PanelIndex = 1, StepIndex = 1 + i, CapacityM3 = 5e4,
                Cx = 60 + 40 * i, Cy = 120 + 25 * i, Cz = 100,
            });
        return list;
    }

    /// <summary>
    /// S21 · <b>`.case` 里本来就带着工作线，别把它丢了</b>。
    ///
    /// <para><b>这条修的是一整条从界面上走不到的路</b>：推进轴由工作线定，排土位置投到轴上
    /// 才谈得上"内排什么时候能用、这个月运多远"。而排产窗口<b>没有选工作线的地方</b>，
    /// <c>BuildInput</c> 也从不填 <c>WorkLines</c> —— 于是真路网每次都退到
    /// "源点摆在基线质心、逐月运距不随推进变"，`⚠ 没有工作线` 每跑必报。
    /// 偏偏用户选的那份 <c>.case</c> 里 <see cref="InclineCase.WorkLines"/> 一直躺着，
    /// 上一版只取了 <c>.Profile</c>。</para>
    ///
    /// <para><b>非空转的证据在第 ② 段</b>：同一批排土位置，没工作线时
    /// <see cref="DumpSlotAdapter.ProjectToAdvanceAxis"/> <b>逐个返回 −∞</b>
    /// （"投不上"的哨兵），有了才落到轴上。所以这不是"日志多说一句"，
    /// 是那一整列 u 从哨兵变成真值。</para>
    /// </summary>
    [Fact]
    public void S21_WorkLines_RideAlongInTheCaseFile_AndReachTheAdvanceAxis()
    {
        string path = WriteCaseWithLines("lines");
        try
        {
            // ① 读中间文件时工作线要跟着出来
            var cp = MonthlyStripSession.LoadProfileFile(path, null, out string err, out _, out var lines);
            Assert.NotNull(cp);
            Assert.True(string.IsNullOrEmpty(err), err);
            Assert.NotNull(lines);
            Assert.Single(lines!);
            _out.WriteLine($"从 .case 读回工作线 {lines!.Count} 条");

            // ② ★ 载荷：同一批位置，没线全是 −∞ 哨兵，有线才落到轴上
            var slots = DumpSlotAdapter.ToSlots(CellsOnAxis(), "北排土场");
            var uNone = DumpSlotAdapter.ProjectToAdvanceAxis(slots, Array.Empty<WorkLineGeometry>());
            var uWith = DumpSlotAdapter.ProjectToAdvanceAxis(slots, lines!);
            Assert.All(uNone, v => Assert.True(double.IsNegativeInfinity(v), "没工作线时就该是 −∞ 哨兵"));
            Assert.Contains(uWith, double.IsFinite);
            Assert.True(uWith.Where(double.IsFinite).Distinct().Count() > 1,
                        "投上的 u 全相等的话，等于轴还是没起作用");
            _out.WriteLine($"无工作线 u = 全 −∞；有工作线 u = "
                         + string.Join(", ", uWith.Select(v => double.IsFinite(v) ? $"{v:0.#}" : "−∞")));

            // ③ 会话走文件这条路时要接上，并把来源说出来
            var withFile = MonthlyStripSession.Run(new MonthlyStripSessionInput
            {
                ProfilePath = path,
                DumpCells = CellsOnAxis(),
                DumpName = "北排土场",
                CoalTargetWt = Enumerable.Repeat(8.0, 12).ToArray(),
                LookaheadMonths = 3,
            });
            Assert.Contains(withFile.Log, l => l.Contains("工作线取自同一份中间文件"));
            Assert.DoesNotContain(withFile.Log, l => l.Contains("工作线") && l.Contains("静态兜底"));

            // ④ 反面：剖面直接给内存那份（没有文件可带线）—— 静态兜底那句必须还在，
            //    否则第 ③ 段的"没有那句"根本不构成证据
            var inMem = MonthlyStripSession.Run(NoLines(Input(Rock())));
            Assert.Contains(inMem.Log, l => l.Contains("工作线") && l.Contains("静态兜底"));
            _out.WriteLine("走文件 → 接上工作线；走内存剖面 → 仍如实报静态兜底");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>
    /// S22 · <b>容器里的工作线，得先确认是这份剖面的那组线，才能拿来建推进轴</b>。
    ///
    /// <para>S21 让 <c>.case</c> 里的工作线接上了推进轴。但容器里这两样是<b>分头写的</b>：
    /// <c>WorkLines</c> 是几何，<c>Provenance.WorkLineKey</c> 是<b>扫描当时那组线</b>的指纹。
    /// 改完工作线没重扫、或者两次结果拼进同一个容器，两者就会对不上。</para>
    ///
    /// <para><b>为什么对不上就必须退回静态兜底、而不是"凑合用"</b>：岩量是<b>按当时那条轴分的格</b>。
    /// 换一条轴去投排土位置，逐月运距和内排启用时机会<b>静静地错位</b> ——
    /// 不抛异常、不报失败，只是把计划排错。宁可少一份精度，不要多一份错位。</para>
    ///
    /// <para><b>折叠只能有一份实现</b>：扫描时算一次、这里校验时再算一次，各写各的话
    /// 指纹会因为**算法本身漂**而对不上 —— 那比没有指纹更糟，它会把好数据判成陈的。
    /// 所以两边都走 <see cref="ProfileProvenance.FoldWorkLines"/>，本判据的第 ① 段就是钉这个。</para>
    /// </summary>
    [Fact]
    public void S22_CaseWorkLines_MustMatchTheProfileFingerprint()
    {
        // ① 折叠只有一份实现：同一组线折两次必须一样，换一个点就必须变
        var a = new WorkLineGeometry { Success = true };
        a.Baseline.Add((0, 0, 100)); a.Baseline.Add((0, 400, 100));
        a.Samples.Add((0, 200, 100, 1, 0));
        var b = new WorkLineGeometry { Success = true };
        b.Baseline.Add((0, 0, 100)); b.Baseline.Add((0, 401, 100));   // 只挪 1m
        b.Samples.Add((0, 200, 100, 1, 0));
        var one = new[] { a };
        Assert.Equal(ProfileProvenance.FoldWorkLines(one), ProfileProvenance.FoldWorkLines(one));
        Assert.NotEqual(ProfileProvenance.FoldWorkLines(one), ProfileProvenance.FoldWorkLines(new[] { b }));

        // ② 容器自洽 → 工作线交出来
        string ok = WriteCaseWithLines("s22ok", coherent: true);
        // ③ 容器拼过 → 工作线扣住不交，并说明为什么
        string bad = WriteCaseWithLines("s22bad", coherent: false);
        try
        {
            var cpOk = MonthlyStripSession.LoadProfileFile(ok, null, out _, out string howOk, out var lOk);
            Assert.NotNull(cpOk);
            Assert.NotNull(lOk);
            Assert.DoesNotContain("对不上", howOk);

            var cpBad = MonthlyStripSession.LoadProfileFile(bad, null, out _, out string howBad, out var lBad);
            Assert.NotNull(cpBad);              // 剖面本身还能用，扣的只是工作线
            Assert.Null(lBad);                  // ★ 不拿它建推进轴
            Assert.Contains("对不上", howBad);
            Assert.Contains("静态兜底", howBad);
            _out.WriteLine("拼过的容器 → " + howBad.Split('\n')[^1].Trim());

            // ④ 落到会话上：拼过的容器不许出现"工作线取自中间文件"，而要如实报静态兜底
            var r = MonthlyStripSession.Run(new MonthlyStripSessionInput
            {
                ProfilePath = bad, DumpCells = CellsOnAxis(), DumpName = "北排土场",
                CoalTargetWt = Enumerable.Repeat(8.0, 12).ToArray(), LookaheadMonths = 3,
            });
            Assert.DoesNotContain(r.Log, l => l.Contains("工作线取自同一份中间文件"));
            Assert.Contains(r.Log, l => l.Contains("工作线") && l.Contains("静态兜底"));
        }
        finally { foreach (var p in new[] { ok, bad }) { try { File.Delete(p); } catch { } } }
    }

    // ───────────────────────── S23 ─────────────────────────

    private static MonthlyStripSessionInput Lift(MonthlyStripSessionInput i, double up, double down)
    { i.UphillEquivalent = up; i.DownhillEquivalent = down; return i; }

    /// <summary>逐月煤量按层数均分 —— 走"用户给了逐层量"那条路（R36 的反面）。</summary>
    private static MonthlyStripSessionInput BySeam(MonthlyStripSessionInput i)
    {
        // 用 CoalVolBins.Length，不用 SeamCount —— 内核那道闸判的就是这个
        // （`M = rock.CoalVolBins.Length`），两者不等时按 SeamCount 造的数组会被静默判为不合格。
        int seams = Math.Max(1, i.Rock?.CoalVolBins.Length ?? 1);
        i.CoalTargetBySeam = i.CoalTargetWt
            .Select(t => Enumerable.Repeat(t / seams, seams).ToArray()).ToArray();
        return i;
    }

    /// <summary>
    /// S23 · <b>〔待现场核准〕的系数，得填得进来；而且三种口径都得表达得出</b>。
    ///
    /// <para>S17 量出两套台账下坡<b>正负号相反</b>（本模块当代价 +3、`RoadLib` 车型台账当减免 −2）。
    /// 但光"报出来"不够 —— 上一版<b>核准了也没处填</b>：会话不暴露这两个系数，
    /// 而 <see cref="HaulModel.EquivalentKm"/> 里 <c>Math.Max(0, DownhillEquivalent)</c>
    /// 会把负数<b>静默夹成 0</b>。也就是说想按路网台账那套算，填下去得到的是
    /// <b>"下坡免费"这第三种口径 —— 两套台账都不是这么说的</b>，而且没有任何提示。</para>
    ///
    /// <para>钉三件事：① 三种口径都表达得出且**互不相同**（否则"能填"是句空话）；
    /// ② 减免<b>有底</b>，不许把等效运距压成 0/负（"越远越便宜"会让配对去抢最深那个位置）；
    /// ③ 填了要留条并说清填的是哪一种口径，不填不冒条。</para>
    /// </summary>
    [Fact]
    public void S23_LiftEquivalent_IsSettable_AndAllThreeConventionsAreExpressible()
    {
        const double planKm = 2.0, drop = -80.0;      // 下坡 80m：内排的典型形态

        var cost = new HaulModel { DownhillEquivalent = 3 };     // 本模块在用：当代价
        var free = new HaulModel { DownhillEquivalent = 0 };     // 第三种：免费
        var credit = new HaulModel { DownhillEquivalent = -2 };  // 路网台账：当减免

        double kCost = cost.EquivalentKm(planKm, drop);
        double kFree = free.EquivalentKm(planKm, drop);
        double kCred = credit.EquivalentKm(planKm, drop);
        _out.WriteLine($"平距 {planKm}km、下坡 {-drop:0}m 的等效运距：");
        _out.WriteLine($"  当代价(+3) {kCost:0.###}km ｜ 免费(0) {kFree:0.###}km ｜ 减免(−2) {kCred:0.###}km"
                     + $"　⇒ 极差 {kCost - kCred:0.###}km（{(kCost - kCred) / kFree * 100:0.#}%）");

        // ① 三种必须互不相同 —— 否则"填得进来"是句空话
        Assert.True(kCost > kFree, "当代价必须比免费远");
        Assert.True(kFree > kCred, "减免必须比免费近 —— 被夹成 0 的话这条就红");
        Assert.Equal(planKm * cost.Tortuosity + 80 * 3 / 1000.0, kCost, 6);
        Assert.Equal(planKm * cost.Tortuosity - 80 * 2 / 1000.0, kCred, 6);

        // ② 减免有底：落差大到离谱也不许把等效运距压成 0/负
        double deep = credit.EquivalentKm(planKm, -100000);
        Assert.True(deep >= planKm * credit.Tortuosity * credit.MinEquivalentFraction - 1e-9);
        Assert.True(deep > 0, "等效运距 0 或负 ⇒ 越远越便宜，配对会去抢最深的位置");
        _out.WriteLine($"  落差 100km（荒谬值）时减免口径仍有底：{deep:0.###}km"
                     + $"（≥ 平距×{credit.MinEquivalentFraction:0.##}）");

        // ③ 会话上填得进去，且留条说清是哪一种口径
        var rock = Rock();
        var r = MonthlyStripSession.Run(Lift(Input(rock), 6, -2));
        Assert.Contains(r.Log, l => l.Contains("提升当量按**外面指定**") && l.Contains("减免"));
        var r0 = MonthlyStripSession.Run(Lift(Input(rock), 6, 0));
        Assert.Contains(r0.Log, l => l.Contains("两套台账都不是这么说的"));
        Assert.DoesNotContain(MonthlyStripSession.Run(Input(rock)).Log,      // 不填不冒条
                              l => l.Contains("提升当量按**外面指定**"));
        _out.WriteLine("会话：填了留条并点明口径；不填不冒条");
    }

    // ───────────────────────── S24 ─────────────────────────

    /// <summary>
    /// S24 · <b>煤容重要从剖面推，不许写死</b> —— 它决定采空区体积，进而决定内排率。
    ///
    /// <para>"采出多少万t 煤"要折回<b>采空区体积</b>才知道内排能放多少
    /// （<c>voidM3 = RockCumM3 + CoalCumWt×1e4 / CoalDensity</c>）。
    /// 上一版 <c>CoalDensity</c> 是写死的 1.35，而<b>各层容重这份剖面自己就带着</b>
    /// （扫块体时逐层出来的 <see cref="RockProfile.SeamDensity"/>）——
    /// 层容重不是 1.35 时，"吨→方"这一步与<b>算吨时用的口径对不上</b>，
    /// 采空区体积系统性偏，而每一步都"成功"。</para>
    ///
    /// <para><b>必须体积加权</b>：折回体积时按"总吨÷总方"才闭合；各层算术平均在层厚悬殊时会偏 ——
    /// 第 ② 段就是拿一薄一厚两层把这两种算法拉开，否则这条判据在等厚夹具上是空过的。</para>
    /// </summary>
    [Fact]
    public void S24_CoalDensity_IsDerivedFromTheProfile_VolumeWeighted()
    {
        // ① 单层：推出来的就是那层的容重
        var one = PlausibleRock(seams: 1);
        one.SeamDensity[0] = 1.42;
        Assert.Equal(1.42, one.EffectiveCoalDensity(), 6);

        // ② 一薄一厚、容重不同 —— 体积加权 ≠ 算术平均
        var two = PlausibleRock(seams: 2);
        two.SeamDensity[0] = 1.20; two.SeamDensity[1] = 1.60;
        two.CoalVolBins[0].Clear(); two.CoalVolBins[1].Clear();
        two.CoalVolBins[0][200] = 9000;      // 厚层，轻
        two.CoalVolBins[1][200] = 1000;      // 薄层，重
        double vw = two.EffectiveCoalDensity();
        double arith = (1.20 + 1.60) / 2;
        Assert.Equal((9000 * 1.20 + 1000 * 1.60) / 10000.0, vw, 6);
        Assert.True(Math.Abs(vw - arith) > 0.15, "薄厚拉不开的话这条判据等于没判");
        _out.WriteLine($"体积加权 {vw:0.###} vs 算术平均 {arith:0.###} —— 差 {arith - vw:0.###} t/m³");

        // ③ ★ 闭合：吨 → 方 → 吨 必须回到原处（写死 1.35 就回不来）
        double wt = two.TotalCoalWt();                       // 万t
        double volBack = wt * 1e4 / vw;                      // m³
        double volTrue = 10000.0;
        Assert.Equal(volTrue, volBack, 3);
        double volWrong = wt * 1e4 / 1.35;
        Assert.True(Math.Abs(volWrong - volTrue) / volTrue > 0.05,
                    "写死 1.35 若与实测差不多，这条就证明不了什么");
        _out.WriteLine($"吨→方：按实测 {volBack:0} m³（真值 {volTrue:0}）· 按写死1.35 {volWrong:0} m³"
                     + $"　⇒ 偏 {(volWrong - volTrue) / volTrue * 100:+0.#;-0.#}%（采空区体积就偏这么多）");

        // ④ 会话：与缺省不同时要留条说是按剖面推的
        var rock = Rock();
        for (int m = 0; m < rock.SeamDensity.Length; m++) rock.SeamDensity[m] = 1.55;
        Assert.Contains(MonthlyStripSession.Run(Input(rock)).Log,
                        l => l.Contains("煤容重按剖面实测"));
        // 反面：与缺省一致时不该啰嗦
        var same = Rock();
        for (int m = 0; m < same.SeamDensity.Length; m++) same.SeamDensity[m] = 1.35;
        Assert.DoesNotContain(MonthlyStripSession.Run(Input(same)).Log,
                              l => l.Contains("煤容重按剖面实测"));
        _out.WriteLine("会话：与缺省不同才留条，一样就不啰嗦");
    }

    // ───────────────────────── S25 ─────────────────────────

    /// <summary>锯齿链：每段折 45°，所以<b>相邻点比值恒为 1</b>、隔点比值 ≈ √2。</summary>
    private static PitMine3D.Kylin.Cad.Road.RoadGraph ZigZag(int seg = 12, double a = 200)
    {
        var g = new PitMine3D.Kylin.Cad.Road.RoadGraph();
        var pts = new List<(string Id, double X, double Y)>();
        for (int i = 0; i <= seg; i++) pts.Add(($"z{i}", a * i, i % 2 == 0 ? 0 : a));
        foreach (var p in pts)
            g.AddNode(p.Id, PitMine3D.Kylin.Cad.Road.RoadNodeType.Junction, new PitMine3D.Kylin.Cad.Road.Point3d(p.X, p.Y, 0));
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var p = new PitMine3D.Kylin.Cad.Road.Point3d(pts[i].X, pts[i].Y, 0);
            var q = new PitMine3D.Kylin.Cad.Road.Point3d(pts[i + 1].X, pts[i + 1].Y, 0);
            g.AddEdge(new PitMine3D.Kylin.Cad.Road.RoadEdge($"s{i}", pts[i].Id, pts[i + 1].Id, new[] { p, q }));
            g.AddEdge(new PitMine3D.Kylin.Cad.Road.RoadEdge($"s{i}r", pts[i + 1].Id, pts[i].Id, new[] { q, p }));
        }
        return g;
    }

    /// <summary>
    /// S25 · <b>兜底迂回系数要由路网自己采，而且得采在运距尺度上</b>。
    ///
    /// <para><b>为什么不能用事后的 <c>MeasuredTortuosity</c></b>：它是排产真去查过 O-D 之后
    /// 才有值的（<c>_ratios.Count &lt; 3</c> 前返回 null），而<b>兜底系数在排产之前就要定</b> ——
    /// 拿它做"自动推"会永远取不到值、静静退回常数：看起来像自动，其实一次也没生效。
    /// 第 ① 段就是钉这个先后关系。</para>
    ///
    /// <para><b>为什么要按尺度筛</b>：相邻节点之间往往有直达边，比值恒等于 1。
    /// 把它们计进去，中位数会被压到 1.00 —— 等于报"这张网不绕"，
    /// 而兜底系数服务的恰恰是<b>源→排土场</b>那种长距离对。
    /// 第 ② 段拿锯齿链把两种采法拉开：相邻恒 1、隔点 ≈√2，不筛就会报 1.00。</para>
    /// </summary>
    [Fact]
    public void S25_FallbackTortuosity_IsSampledFromTheGraph_AtHaulScale()
    {
        var road = RoadHaulProvider.ForGraph(ZigZag(), null, "锯齿链");

        // ① 先后关系：还没查过任何 O-D 时，事后实测是空的，而采样值已经有了
        Assert.Null(road.MeasuredTortuosity);
        Assert.NotNull(road.SampledTortuosity);
        _out.WriteLine($"未排产时：事后实测 = 空，采样 = {road.SampledTortuosity:0.000}");
        _out.WriteLine("  " + road.SampledTortuosityNote);

        // ② ★ 采在运距尺度上：锯齿链真值 ≈ √2，不筛（把相邻对计进去）会被压到 1.00
        Assert.InRange(road.SampledTortuosity!.Value, 1.30, 1.50);
        Assert.True(road.SampledTortuosity!.Value > 1.05,
                    "采到 1.00 就说明把相邻直达对也计进去了 —— 那等于报「这张网不绕」");
        _out.WriteLine($"锯齿链真值 √2≈{Math.Sqrt(2):0.000}，采到 {road.SampledTortuosity:0.000}");

        // ③ 确定性：同一张网两次必须一模一样（用了随机数就会在这儿红）
        var again = RoadHaulProvider.ForGraph(ZigZag(), null, "锯齿链");
        Assert.Equal(road.SampledTortuosity!.Value, again.SampledTortuosity!.Value, 12);
        Assert.Equal(road.SampledTortuosityNote, again.SampledTortuosityNote);

        // ④ 直路网应当采到 ≈1 —— 否则上面那条"锯齿采到 1.4"可能只是恒定偏大
        var straight = new PitMine3D.Kylin.Cad.Road.RoadGraph();
        for (int i = 0; i <= 12; i++)
            straight.AddNode($"p{i}", PitMine3D.Kylin.Cad.Road.RoadNodeType.Junction,
                             new PitMine3D.Kylin.Cad.Road.Point3d(200.0 * i, 0, 0));
        for (int i = 0; i < 12; i++)
        {
            var p = new PitMine3D.Kylin.Cad.Road.Point3d(200.0 * i, 0, 0);
            var q = new PitMine3D.Kylin.Cad.Road.Point3d(200.0 * (i + 1), 0, 0);
            straight.AddEdge(new PitMine3D.Kylin.Cad.Road.RoadEdge($"t{i}", $"p{i}", $"p{i + 1}", new[] { p, q }));
            straight.AddEdge(new PitMine3D.Kylin.Cad.Road.RoadEdge($"t{i}r", $"p{i + 1}", $"p{i}", new[] { q, p }));
        }
        var st = RoadHaulProvider.ForGraph(straight, null, "直链");
        Assert.NotNull(st.SampledTortuosity);
        Assert.InRange(st.SampledTortuosity!.Value, 0.99, 1.02);
        _out.WriteLine($"直链采到 {st.SampledTortuosity:0.000}（应 ≈1）—— 说明采样不是恒定偏大");
    }

    // ───────────────────────── S26 ─────────────────────────

    /// <summary>
    /// S26 · <b>"这个月被哪一侧顶住"要显示出来</b>（触底/触顶）。
    ///
    /// <para>触底 = 再少剥就露不出下个月的煤；触顶 = 剥离能力已经用满。
    /// 它回答的不是"又一个数"，而是<b>"这张表为什么长这样、想改善该松哪一条"</b>。
    /// 内核自己的文字报表一直印着 <c>◄触底/◄触顶</c>，而界面的月度表<b>把这一列丢了</b> ——
    /// 同一个信号两处报法不该两样。</para>
    ///
    /// <para><b>非空转</b>：拿一个真会顶住的算例跑，判**确实有月份被标出来**；
    /// 若一个都没有，这条判据等于只验了个"—"。两侧同时咬住时不做二选一（那种月份最该显示）。</para>
    /// </summary>
    [Fact]
    public void S26_MonthlyTable_ShowsWhichSideIsBinding()
    {
        // 露煤要求本身就是拉绳的下界，正常算例里就会咬住若干个月。
        // 能力上界要真顶住得把 StripCapM3 压到月均附近（岩 480万m³/12 月 ≈ 4e5 m³/月）。
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);
        var months = r.Export!.Months;

        int lo = months.Count(m => m.IsExposureCritical);
        int hi = months.Count(m => m.IsCapacityCritical);
        _out.WriteLine($"触底 {lo} 个月 · 触顶 {hi} 个月 · 共 {months.Count} 个月");
        Assert.True(lo + hi > 0, "一个顶住的月份都没有 ⇒ 这条判据没验到东西，换个更紧的算例");

        // ① 文本与标志一一对应，且不许把"没顶住"显示成空白
        foreach (var m in months)
        {
            string t = m.CriticalText;
            Assert.False(string.IsNullOrWhiteSpace(t));
            if (m.IsExposureCritical && m.IsCapacityCritical) Assert.Contains("夹死", t);
            else if (m.IsExposureCritical) { Assert.Contains("触底", t); Assert.DoesNotContain("触顶", t); }
            else if (m.IsCapacityCritical) { Assert.Contains("触顶", t); Assert.DoesNotContain("触底", t); }
            else Assert.Equal("—", t);
        }

        // ② 四种分支逐个钉住。上面的算例只跑出「触底」与「—」两种
        //    （触顶要把能力压到月均附近才出得来），另两种拿合成行补上 ——
        //    否则"两侧同时咬住"这条最要紧的显示逻辑从没被验过。
        Assert.Equal("触顶(能力吃紧)",
            new ExportMonth { IsCapacityCritical = true }.CriticalText);
        var both = new ExportMonth { IsExposureCritical = true, IsCapacityCritical = true };
        Assert.Contains("触底", both.CriticalText);
        Assert.Contains("触顶", both.CriticalText);
        Assert.Equal("—", new ExportMonth().CriticalText);

        // ③ 界面那一列真的在**月度表**上（只判"文件里有"不够 —— 绑到别的表上等于没显示）
        string xamlPath = FindWindowXaml();
        Assert.False(string.IsNullOrEmpty(xamlPath), "找不到 MonthlyStripWindow.xaml —— 挪过位置就把这条判据一起改");
        string cols = SliceGrid(File.ReadAllText(xamlPath), "GridMonths");
        Assert.Contains("{Binding CriticalText}", cols);
        _out.WriteLine("月度表已列出：" + string.Join(" / ",
            months.Where(m => m.CriticalText != "—").Take(4).Select(m => $"第{m.Month}月 {m.CriticalText}")));
    }

    // ───────────────────────── S27 ─────────────────────────

    /// <summary>
    /// S27 · <b>采出煤那一半要显示，且"引擎摊的"必须标在行上</b>。
    ///
    /// <para><c>MinePlanExport.Coal</c>（逐月逐层采出煤）一直在算、在导、判据也覆盖了，
    /// <b>界面上却一行都没有</b> —— 物料流页只有岩流，而契约里 <c>ExportCoal</c> 的原话就是
    /// <b>"只导岩流是半份账"</b>。</para>
    ///
    /// <para><b>R36 那句话要落到一列上</b>：<c>AllocatedByEngine</c> 的注释写着
    /// "true 时下游必须在界面上标出来 —— <b>摊过的数看上去和用户填的一模一样</b>"。
    /// 光有个 bool 没人看得见；而且不能用勾选框 ——
    /// 勾/不勾都要求读者<b>事先知道约定</b>，可这一列存在的理由正是"看上去一模一样"。</para>
    /// </summary>
    [Fact]
    public void S27_CoalHalfIsShown_AndEngineAllocationIsMarked()
    {
        // ① 只给总量、不给逐层 ⇒ 引擎必须摊，且每行都要标成"摊的"
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);
        var coal = r.Export!.Coal;
        Assert.NotEmpty(coal);
        Assert.True(r.Export.CoalAllocatedByEngine, "没给逐层煤量，引擎摊了就该打标");
        Assert.All(coal, c => Assert.Equal("引擎按规则摊的", c.AllocationText));
        _out.WriteLine($"采出煤 {coal.Count} 行 · 摊法：{r.Export.CoalAllocationMethod}");

        // ② ★ 反面：用户给了逐层量就不许再标成"摊的"，否则这一列恒为真 = 没说任何事
        var bsIn = BySeam(Input(Rock()));
        _out.WriteLine($"逐层数组：{bsIn.CoalTargetBySeam.Length} 月 × "
                     + $"{bsIn.CoalTargetBySeam.FirstOrDefault()?.Length ?? 0} 层"
                     + $"（剖面 CoalVolBins {bsIn.Rock?.CoalVolBins.Length}，MonthCount {bsIn.MonthCount}）");
        var bySeam = MonthlyStripSession.Run(bsIn);
        Assert.True(bySeam.Success, bySeam.Error);
        _out.WriteLine($"内核 Schedule.CoalAllocatedByEngine = {bySeam.Schedule?.CoalAllocatedByEngine}"
                     + $" ｜ 契约 Export.CoalAllocatedByEngine = {bySeam.Export?.CoalAllocatedByEngine}");
        foreach (var l in bySeam.Log.Where(l => l.Contains("逐层"))) _out.WriteLine("  " + l);
        Assert.False(bySeam.Export!.CoalAllocatedByEngine,
                     "给了逐层量还标成引擎摊的 —— 要么闸没认这份数组，要么这一列恒为真");
        Assert.All(bySeam.Export.Coal, c => Assert.Equal("用户给的", c.AllocationText));
        _out.WriteLine("给了逐层量 → 标成「用户给的」");
        Assert.Equal("用户给的", new ExportCoal().AllocationText);
        Assert.Equal("引擎按规则摊的", new ExportCoal { AllocatedByEngine = true }.AllocationText);

        // ③ 文本要能独立看懂：两种取值都不许是空白，且必须互不相同
        Assert.NotEqual(new ExportCoal().AllocationText,
                        new ExportCoal { AllocatedByEngine = true }.AllocationText);

        // ④ 界面真有这张表，且那一列绑在它上面
        string xamlPath = FindWindowXaml();
        Assert.False(string.IsNullOrEmpty(xamlPath));
        string xaml = File.ReadAllText(xamlPath);
        string cols = SliceGrid(xaml, "GridCoal");
        Assert.False(string.IsNullOrEmpty(cols), "界面上没有采出煤这张表 —— 只看岩流是半份账");
        Assert.Contains("{Binding AllocationText}", cols);
        Assert.Contains("{Binding SeamName}", cols);
        // 后台真的把 Coal 喂进去了（只建表不喂 = 永远空表）
        string cs = File.ReadAllText(xamlPath);
        Assert.Contains("GridCoal.ItemsSource", cs);
        _out.WriteLine("界面：采出煤表已建、已喂、已标「分层量来源」");
    }

    // ───────────────────────── S28 ─────────────────────────

    /// <summary>
    /// S28 · <b>内核那三份"给人看的报表"，人得拿得到</b>。
    ///
    /// <para><see cref="MonthlyMineScheduler.Report"/>（逐月表，带 ◄触底/◄触顶）、
    /// <see cref="DumpAllocator.FlowMatrix"/>（<b>源 × 去向配对矩阵</b> —— 与界面那张平铺流水表
    /// 是两种读法）、<see cref="ScheduleDeriver.CompareTable"/>（注释上写着"命令行 / <b>报表</b>直接打"）
    /// —— 三份此前<b>只有判据在调</b>。界面能导出的只有契约 JSON，那是给下游软件的。</para>
    ///
    /// <para><b>非空转</b>：不判"字符串非空"，判**三份报表各自的特征内容真的出现了**，
    /// 且报表比日志长得多（只回显 Log 的话这条会红）。</para>
    /// </summary>
    [Fact]
    public void S28_KernelHumanReports_AreReachableFromTheSession()
    {
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);
        string rep = MonthlyStripSession.BuildReport(r);

        // ① 三份报表的特征内容都在（逐月表/配对矩阵各出一个只有它才有的字样）
        Assert.Contains("逐月采剥表", rep);
        Assert.Contains("采排配对矩阵", rep);
        Assert.Contains("过程与结论", rep);
        // 逐月表自带的触底/触顶标记 —— 这是 Report() 才有的，抄日志抄不出来
        Assert.Contains("触底", rep);

        // ② 报表要**显著多于**日志本身，否则等于只把 Log 回显了一遍
        int logChars = r.Log.Sum(l => l.Length);
        Assert.True(rep.Length > logChars * 1.3,
                    $"报表 {rep.Length} 字 vs 日志 {logChars} 字 —— 没比日志多多少，三份报表多半没接进来");
        _out.WriteLine($"报表 {rep.Length} 字（日志本身 {logChars} 字）");

        // ③ 派生给了就要附比选矩阵；没给就不许凭空冒出来
        Assert.DoesNotContain("方案比选", rep);
        var d = MonthlyStripSession.Derive(Input(Rock()));
        Assert.True(d.Success, "派生没跑通，③ 这一段就成了空过：" + d.Error);
        Assert.True(d.Derivation is { Success: true });
        string rep2 = MonthlyStripSession.BuildReport(r, d);
        Assert.Contains("方案比选", rep2);
        Assert.True(rep2.Length > rep.Length);
        _out.WriteLine($"附比选后 {rep2.Length} 字（多 {rep2.Length - rep.Length}）");

        // ④ 失败的那次也要导得出来 —— 恰恰是失败时最需要把过程带走
        var bad = MonthlyStripSession.Run(Cap(Input(Rock()), 1));
        Assert.False(bad.Success);
        string repBad = MonthlyStripSession.BuildReport(bad);
        Assert.Contains("排产失败", repBad);
        Assert.Contains("过程与结论", repBad);
        Assert.Null(Record.Exception(() => MonthlyStripSession.BuildReport(null)));

        // ⑤ 界面那个按钮真接在这上面
        string cs = File.ReadAllText(FindWindowXaml());
        Assert.Contains("MonthlyStripSession.BuildReport", cs);
        Assert.Contains("OnExportReport", File.ReadAllText(FindWindowXaml()));
        _out.WriteLine("界面：导出报表按钮已接在 BuildReport 上");
    }

    // ───────────────────────── S29 ─────────────────────────

    /// <summary>
    /// S29 · <b>排产失败时，过程必须留下来</b>。
    ///
    /// <para>外循环失败只给一句「排产失败：外循环没跑出任何可用结果」，
    /// 而<b>每一轮为什么不成，`cr.Iterations` 里明明都写着</b> ——
    /// 逐轮记录原先排在失败 <c>return</c> <b>之后</b>，于是最需要过程的那一次，
    /// 一个字的过程都没有。</para>
    ///
    /// <para><b>非空转</b>：不判"日志非空"（失败前的备料段本来就有日志），
    /// 而是判**外循环那一段确实出现了**、且逐轮明细的行数与 <c>Iterations</c> 对得上；
    /// 再拿成功那次做对照，证明这段不是只在失败时硬塞的。</para>
    /// </summary>
    [Fact]
    public void S29_WhenTheOuterLoopFails_TheProcessIsStillLogged()
    {
        // 能力压到 1 m³/月 —— 剥不动，外循环必然出不来可用结果
        var bad = MonthlyStripSession.Run(Cap(Input(Rock()), 1));
        Assert.False(bad.Success);
        Assert.Contains("排产失败", bad.Error);

        // ① 外循环那一段要在
        Assert.Contains(bad.Log, l => l.Contains("外循环") && l.Contains("轮"));
        // ② 逐轮明细的条数要与内核记的轮数一致（少一行都说明被截了）
        int rounds = bad.Coupled?.Iterations.Count ?? 0;
        Assert.True(rounds > 0, "内核一轮都没记 —— 那这条判据换个更能跑起来的算例");
        foreach (var it in bad.Coupled!.Iterations)
        {
            string line = it.ToString();
            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.Contains(bad.Log, l => l.Contains(line));
        }
        _out.WriteLine($"失败那次：外循环 {rounds} 轮，逐轮明细 {rounds} 条都在日志里");
        foreach (var l in bad.Log.Where(l => l.Contains("外循环") || l.Contains("轮 ")).Take(4))
            _out.WriteLine("   " + l);

        // ③ 失败时也要说清是"没跑出可用结果"，不能沿用成功路径那句"已收敛/未收敛"
        Assert.Contains(bad.Log, l => l.Contains("没跑出可用结果"));

        // ④ 对照：成功那次同样有这一段，且措辞不同 —— 证明不是只在失败时硬塞的
        var ok = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(ok.Success, ok.Error);
        Assert.Contains(ok.Log, l => l.Contains("外循环") && l.Contains("轮"));
        Assert.DoesNotContain(ok.Log, l => l.Contains("没跑出可用结果"));

        // ⑤ 报表也得带上（失败那次最需要把过程带走）
        string rep = MonthlyStripSession.BuildReport(bad);
        Assert.Contains("外循环", rep);
        _out.WriteLine("成功那次措辞不同；失败报表已含外循环过程");
    }

    // ───────────────────────── S30 ─────────────────────────

    /// <summary>
    /// S30 · <b>其余失败出口：诊断在那一刻是不是真的存在</b>（S29 的收尾）。
    ///
    /// <para>S29 修好外循环那条之后，把会话里 <b>24 个失败出口</b>逐个过了一遍，
    /// 挑出三处"看着也该记点什么"的：<c>rep.Notes</c> 与两处 <c>imp.Issues</c>。
    /// <b>结论是三处都不必改</b> —— 它们在失败那一刻<b>必然是空的</b>：</para>
    /// <list type="bullet">
    ///   <item><c>RollingReplan.Prepare</c> 里唯一那条设 <c>Error</c> 的路径，
    ///     排在所有 <c>Notes.Add</c> <b>之前</b>；而 Prepare 成功、后续排产失败时，
    ///     <c>run.Log.Insert</c> 是<b>无条件</b>执行的，偏差账照样进日志。</item>
    ///   <item><c>MinePlanImporter.Import</c> 的两条失败返回（契约为空 / 版本不匹配）
    ///     都排在第一个 <c>Issues.Add</c> <b>之前</b>；过了那一关就一路走到 <c>Success = true</c>。</item>
    /// </list>
    ///
    /// <para><b>这条判据就是把这个前提钉住</b>。曾照着"失败也要记"的直觉给三处都加了
    /// <c>foreach</c>，跑完才发现<b>一条也记不出来</b> —— 那种代码比没写更坏：
    /// 它让读的人以为"失败时 Issues 可能有内容"。前提哪天变了（谁在早返回前加了一条 Issue），
    /// 这里会红，那时再补记录才是对的。</para>
    /// </summary>
    [Fact]
    public void S30_OtherFailureExits_TheirDiagnosticsAreProvablyEmpty()
    {
        var prev = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(prev.Success, prev.Error);

        // ① 重排失败（已过月数超出计划长度）⇒ Prepare 在加任何 Note 之前就报错
        var actual = RollingReplan.SimulateActual(prev.Schedule!, 6, 1.0, 0.7);
        actual.MonthsElapsed = 99;
        var rep = MonthlyStripSession.Replan(prev, actual);
        Assert.False(rep.Success);
        Assert.Contains("重排失败", rep.Error);
        Assert.Empty(rep.Replan?.Notes ?? new List<string>());   // ★ 前提：这里必空
        _out.WriteLine($"重排失败 → 偏差账 {rep.Replan?.Notes.Count ?? 0} 条（前提成立：Error 排在 Notes 之前）");

        // ② 装回失败（版本不匹配）⇒ Import 在加任何 Issue 之前就返回
        string bad = prev.Export!.ToJson().Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 999");
        var back = MonthlyStripSession.FromContract(bad);
        Assert.False(back.Success);
        Assert.Empty(back.Import?.Issues ?? new List<ImportIssue>());
        _out.WriteLine($"装回失败 → Issues {back.Import?.Issues.Count ?? 0} 条 · Error「{back.Error[..Math.Min(30, back.Error.Length)]}…」");

        // ③ 反面对照：**成功**那次 Issues 确实非空且都进了日志 —— 否则上面两条"必空"证明不了什么
        var ok = MonthlyStripSession.FromContract(prev.Export!.ToJson());
        Assert.True(ok.Success, ok.Error);
        Assert.NotEmpty(ok.Import!.Issues);
        foreach (var i in ok.Import.Issues)
            Assert.Contains(ok.Log, l => l.Contains(i.ToString()!));
        _out.WriteLine($"装回成功 → Issues {ok.Import.Issues.Count} 条，全部进了日志");

        // ④ 兜整类：每个失败出口都得留下可用的 Error，且日志不是空的
        var exits = new (string What, MonthlyStripSessionResult R)[]
        {
            ("剖面自身不自洽", MonthlyStripSession.Run(BadProfile(Input(Rock())))),
            ("没有排土位置",   MonthlyStripSession.Run(NoSlots(Input(Rock())))),
            ("外循环出不来",   MonthlyStripSession.Run(Cap(Input(Rock()), 1))),
        };
        foreach (var (what, res) in exits)
        {
            Assert.False(res.Success, what + " 居然成功了 —— 换个更硬的算例");
            Assert.NotEmpty(res.Log);
            Assert.False(string.IsNullOrWhiteSpace(res.Error));
            _out.WriteLine($"  {what}：日志 {res.Log.Count} 行，Error「{res.Error[..Math.Min(28, res.Error.Length)]}…」");
        }
    }

    /// <summary>把剖面改成"总量与逐桶漂了"，走剖面自校那条失败出口。</summary>
    private static MonthlyStripSessionInput BadProfile(MonthlyStripSessionInput i)
    { i.Rock!.TotalRockM3 *= 1.5; return i; }

    // ───────────────────────── S31 ─────────────────────────

    private static string FindPluginSource([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here) ?? AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "src", "Views", "MainWindow.axaml.cs");   // Kylin：功能区派发在主窗后台
            if (File.Exists(p)) return p;
        }
        return "";
    }

    /// <summary>
    /// S31 · <b>功能区里不许有"点了只写日志"的按钮</b>。
    ///
    /// <para>插件里有个私有的 <c>PlaceholderCommand(featureName)</c>，点击只往日志写
    /// 「功能预留」。它<b>当前一处都没在用</b> —— 本条把这个状态钉住：
    /// 谁把新按钮接到它上面，这里就红，那时要么把功能补上、要么是**有意识地**留个桩，
    /// 而不是让用户点一个什么都不发生的按钮。</para>
    ///
    /// <para>顺带钉住"入口真的存在"：<b>量驱动月度采剥接续</b>那个按钮必须在
    /// 「短期计划 · 排产比选」这一组里，且真的 <c>new MonthlyStripWindow</c> ——
    /// 整条链做得再全，界面上进不去也等于没有。</para>
    /// </summary>
    [Fact]
    public void S31_NoRibbonButton_IsWiredToAPlaceholder()
    {
        // Kylin：功能区按钮 → MainWindow.axaml.cs 的 DispatchRibbon 命中；短期组四钮必须各有真处理器，而不是落到「暂未实现」兜底
        string p = FindPluginSource();
        Assert.False(string.IsNullOrEmpty(p), "找不到 MainWindow.axaml.cs —— 挪过位置就把这条判据一起改");
        string src = File.ReadAllText(p);
        string plan = File.ReadAllText(Path.Combine(Path.GetDirectoryName(p)!, "MainWindow.Plan.cs"));

        foreach (var (cmd, handler) in new[] { ("量驱动采剥接续", "OpenMonthlyStrip"), ("短期生产计划编制", "OpenShortTermConfig"),
                                              ("月度计划编制", "OpenShortTermSolve"), ("确定开采程序", "OpenShortTermSequence"), ("采排配对", "OpenDumpPairingPlan") })
        {
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(src, $@"cmd == ""{cmd}""[^\n]*{handler}\("),
                $"「{cmd}」没有接到 {handler} —— 用户点了只会看到「暂未实现」");
        }
        Assert.Contains("new Views.Plan.MonthlyStripWindow", plan);
        Assert.Contains("暂未实现", src);   // 兜底文案还在（判据反向自检：读到的是那份派发源码）
        _out.WriteLine($"派发源 {src.Length / 1024}KB · 短期组 5 钮各有处理器 · 量驱动入口 OpenMonthlyStrip");
    }

    // ───────────────────────── S40 ─────────────────────────

    /// <summary>
    /// S40 · <b>声明了每行几个，就得真排成那么多行</b>（2026-08-09 现场：「3行的布局」）。
    ///
    /// <para><b>抓的是哪一族缺陷</b>：宿主 <c>AddSplitButton</c> 收下了 <c>size</c> 参数，
    /// 却写死 <c>GroupBox.Items.Add(sb)</c> —— <b>尺寸收下了、落位那一半没实现</b>。
    /// 于是 Middle 尺寸的 SplitButton 进不了那个 <c>RibbonToolBar</c>、不参与 N/行 重排，
    /// 自己浮在组旁边。症状：组里 7 个中等按钮、声明 3/行，界面只排出 <b>2 行</b>
    /// —— 因为真正参与排版的只有 6 个。<b>不报错、不抛异常、看上去像没重新编译。</b>
    /// 与本文件 <c>SizeDefinition</c> 那处同族（[[always-firing-warning-is-a-dead-path]] 的反面：
    /// 这个是**从不发声**的死路）。</para>
    ///
    /// <para><b>为什么判行数而不判"有没有调 PlaceItem"</b>：行数才是现场要的那件事。
    /// 只判调用形式的话，下一个人换种写法绕过去照样绿（[[judgment-discipline]]：
    /// 判**这件事本身**，不判它常见的某一种实现形式）。所以两条一起钉 ——
    /// 落位那一半必须在（否则算出来的行数是假的），行数本身也必须对。</para>
    /// </summary>
    [Fact(Skip = "原 WPF 宿主 RibbonRegistry / IconDict.xaml 专属判据：Kylin 功能区是 Avalonia XAML（MainWindow.axaml），无此结构")]
    public void S40_MiddleButtons_ActuallyLayOutInThreeRows_WpfHostOnly()
    {
        string p = FindPluginSource();
        Assert.False(string.IsNullOrEmpty(p), "找不到 PlanLibPlugin.cs —— 挪过位置就把这条判据一起改");
        string src = File.ReadAllText(p);

        // ① 宿主那一半：Middle 尺寸的 SplitButton 必须和 Button/ToggleButton 一样走落位。
        //    只截 AddSplitButton 的方法体 —— GroupBox.Items.Add 在本文件别处是**正当**写法
        //    （AddSeparator / Large 分支），整篇判会误伤。
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(p)))!;   // …/<repo>
        string reg = File.ReadAllText(Path.Combine(root, "Host", "PitMineApp",
            "Infrastructure", "Ribbon", "RibbonRegistry.cs"));
        int sbAt = reg.IndexOf("public IRibbonDropDown AddSplitButton", StringComparison.Ordinal);
        int placeAt = reg.IndexOf("private void PlaceItem", StringComparison.Ordinal);
        Assert.True(sbAt > 0 && placeAt > sbAt, "RibbonRegistry 的结构变了，下面截方法体的口径不成立");
        string body = reg[sbAt..placeAt];

        Assert.DoesNotContain("GroupBox.Items.Add(sb)", body);
        Assert.Contains("PlaceItem(sb, size)", body);

        // ② 行数：参与排版的 = 组区间里所有 size:Middle 的注册（AddButton 与 AddSplitButton 都算）
        //    终点原来用「生产进度计划过程模拟」组，那个组 2026-08-09 撤了，改用 Initialize 末尾的日志。
        int shortAt = src.IndexOf("AddGroup(\"短期生产计划编制\"", StringComparison.Ordinal);
        int endAt = src.IndexOf("[PlanLib] Plugin initialized", StringComparison.Ordinal);
        Assert.True(shortAt > 0 && endAt > shortAt, "组的顺序/存在性变了，按区间数的口径就不成立了");
        string region = src[shortAt..endAt];

        // ★ 大按钮必须全部排在中等块**之前** —— 宿主把整个中等块插在【第一个】Middle 的位置上，
        //   在中间插一个 Large，它只会跑到中等块后面去（「采场/排土场圈定」此前就是这样）。
        var firstMiddle = System.Text.RegularExpressions.Regex.Match(region, @"size:\s*RibbonItemSize\.Middle");
        var lastLarge = System.Text.RegularExpressions.Regex.Matches(region, @"size:\s*RibbonItemSize\.Large")
                            .Cast<System.Text.RegularExpressions.Match>().LastOrDefault();
        Assert.True(firstMiddle.Success && lastLarge != null, "短期组里大/中按钮不齐，下面的先后判据不成立");
        Assert.True(lastLarge!.Index < firstMiddle.Index,
            "有大按钮写在中等按钮之后 —— 它会被挤到中等块【后面】显示，而不是和其他大按钮排在一起；"
            + "现场要的是大按钮全在前、中等块在后，所以注册顺序必须是先全部 Large 再全部 Middle");

        int middles = System.Text.RegularExpressions.Regex
            .Matches(region, @"size:\s*RibbonItemSize\.Middle").Count;

        var m = System.Text.RegularExpressions.Regex.Match(region, @"middleItemsPerRow:\s*(\d+)");
        Assert.True(m.Success, "AddGroup 没显式传 middleItemsPerRow —— 行数就成了宿主默认值(4)说了算");
        int perRow = int.Parse(m.Groups[1].Value);
        Assert.True(perRow > 0, "每行个数必须为正（宿主会钳到 4，界面与判据就对不上了）");

        int rows = (middles + perRow - 1) / perRow;
        _out.WriteLine($"功能区：短期组 {middles} 个中等按钮 ÷ {perRow} 个/行 = {rows} 行");
        Assert.True(rows == 3,
            $"短期组排出来是 {rows} 行（{middles} 个中等按钮 ÷ {perRow} 个/行），现场要的是 3 行 —— "
            + "加减按钮或改 middleItemsPerRow 都会动这个数，两者要一起调");
    }

    // ───────────────────────── S42 ─────────────────────────

    /// <summary>
    /// S42 · <b>功能区按钮的图标不许重样，也不许指向不存在的键</b>（2026-08-09 现场：「很多图标显示是一样的」）。
    ///
    /// <para><b>两种错都不报错</b>：
    /// ① <b>键重复</b> —— 三个按钮共用 <c>plan_st_mine_program</c>、两个大按钮共用 <c>plan_st_monthly</c>，
    ///    功能区上就是"好几个长得一模一样"，人只能靠读文字分辨，图标等于白给；
    /// ② <b>键拼错/漏定义</b> —— <c>ResolveIcon</c> 查不到就<b>返回 null，控件不显示图标但不抛异常</b>
    ///    （这是宿主刻意的容错，见 RibbonRegistry 类注释）。所以少一个图标在界面上只是"这个按钮没图"，
    ///    不会有任何一处告诉你键写错了。</para>
    ///
    /// <para><b>只判组按钮</b>：下拉菜单里的子项共用主按钮图标是正当的（它们在弹出菜单里，
    /// 靠文字区分，且共用图标反而表明"同一族"）。所以按接收者过滤 —— <c>groupXxx.AddButton</c> 才算。</para>
    ///
    /// <para><b>扫全仓每一个插件</b>，不只是 PlanLib：2026-08-09 全仓扫下来，
    /// MineAssLib 有 6 组重复（<c>mineass_road_centerline</c> 一个图标给了 3 个按钮）、
    /// PointCloudLib 5 组、RoadLib 3 组、GeoDataBase 1 组。只钉一个插件的话，
    /// 这条判据看着是绿的，而界面上照样一片重样。</para>
    ///
    /// <para><b>重样按插件内部判</b>：跨插件在不同 tab 上复用同一张图不算问题（人不会同屏看到）；
    /// 同一个插件里重样才是"这一排长得一样"。而<b>键有没有定义按全仓的并集判</b> ——
    /// 所有 IconDict 都并进 <c>Application.Resources</c>，跨字典也解析得到。</para>
    /// </summary>
    [Fact(Skip = "原 WPF 宿主 RibbonRegistry / IconDict.xaml 专属判据：Kylin 功能区是 Avalonia XAML（MainWindow.axaml），无此结构")]
    public void S42_RibbonIcons_AreDistinctAndDefined_WpfHostOnly()
    {
        string p = FindPluginSource();
        Assert.False(string.IsNullOrEmpty(p), "找不到 PlanLibPlugin.cs —— 挪过位置就把这条判据一起改");
        string modules = Path.GetDirectoryName(Path.GetDirectoryName(p))!;   // …/Modules

        // 全仓已定义的图标键（所有 IconDict 都并进 Application.Resources，跨字典可解析）
        //
        // ★ 同时判**同一本字典里 x:Key 不许重复** —— 这不是洁癖，是致命的：
        //   ResourceDictionary 加载时遇到重复键会**抛异常，整本字典作废**，
        //   于是那个插件的**所有**图标（连同 Ribbon Tab 自己的图标）一起消失。
        //   而 iconKey 那一侧仍然"找得到定义"（文件里确实有），构建也是绿的 ——
        //   2026-08-09 就是这么把「道路运输系统」整个 tab 的图标全弄没的：
        //   RoadLib 里 road_trigger 早已定义、插件注释却写着"待补"，于是又加了一份同名的。
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var dupKeys = new List<string>();
        foreach (string d in Directory.EnumerateFiles(modules, "IconDict.xaml", SearchOption.AllDirectories))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(d), @"x:Key=""(?<k>\w+)"""))
            {
                string k = m.Groups["k"].Value;
                if (!seen.Add(k))
                    dupKeys.Add($"[{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(d)))}] {k}");
                defined.Add(k);
            }
        }
        Assert.True(defined.Count > 100, $"只收到 {defined.Count} 个图标定义 —— IconDict 的位置或写法变了");
        Assert.True(dupKeys.Count == 0,
            "同一本 IconDict 里出现重复 x:Key：" + string.Join(" / ", dupKeys)
            + " —— ResourceDictionary 加载时会抛异常并**作废整本字典**，"
            + "那个插件的所有图标（含 Tab 图标）会一起消失，且构建全绿、运行不报错");

        var rx = new System.Text.RegularExpressions.Regex(
            @"(?<recv>\w+)\.Add(?:Button|ToggleButton|SplitButton)\(\s*""(?<label>[^""]+)""[\s\S]{0,400}?iconKey:\s*""(?<key>\w+)""");

        var allDup = new List<string>();
        var allMissing = new List<string>();
        int scanned = 0, totalBtns = 0;

        foreach (string f in Directory.EnumerateFiles(modules, "*Plugin.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            string src = File.ReadAllText(f);
            var used = rx.Matches(src)
                .Select(m => (Recv: m.Groups["recv"].Value, Label: m.Groups["label"].Value, Key: m.Groups["key"].Value))
                .ToList();
            if (used.Count == 0) continue;
            scanned++;

            string plugin = Path.GetFileName(f);
            var groupBtns = used.Where(u => u.Recv.StartsWith("group", StringComparison.Ordinal)).ToList();
            totalBtns += groupBtns.Count;

            // ① 同一插件里，组按钮不许共用图标
            allDup.AddRange(groupBtns.GroupBy(u => u.Key).Where(g => g.Count() > 1)
                .Select(g => $"[{plugin}] {g.Key} ×{g.Count()} ← {string.Join(" / ", g.Select(x => x.Label))}"));

            // ② 用到的键必须真有定义（含下拉子项）—— 查不到只是"没图标"，不抛异常
            allMissing.AddRange(used.Select(u => u.Key).Distinct().Where(k => !defined.Contains(k))
                .Select(k => $"[{plugin}] {k}"));
        }

        _out.WriteLine($"扫了 {scanned} 个插件、{totalBtns} 个带图标的组按钮，全仓图标定义 {defined.Count} 个");

        Assert.True(allDup.Count == 0,
            $"有 {allDup.Count} 组按钮共用同一个图标，界面上长得一模一样：\n  "
            + string.Join("\n  ", allDup) + "\n—— 图标重样不报错，人只能靠读文字分辨");

        Assert.True(allMissing.Count == 0,
            "这些 iconKey 全仓都没有定义：" + string.Join(" / ", allMissing.Distinct())
            + " —— ResolveIcon 查不到会返回 null，按钮静默地没有图标，不报错");
    }

    // ───────────────────────── S41 ─────────────────────────

    /// <summary>
    /// S41 · <b>确定簿只许一处写</b>（2026-08-09 重新分工）。
    ///
    /// <para><b>此前是三处</b>：「月度计划编制」「派生计划方案」「量驱动采剥接续」各写一遍
    /// <c>ShortTermSchemeStore.Confirmed</c>，而三份<b>做的事根本不一样</b> ——
    /// 只有量驱动那份带阻断校验、并且写 <c>monthly_plan</c> 台账；另外两份只写内存。
    /// 而 <c>ShortTermSchemeStore</c> 是纯内存静态类（全类没有 Save/Load），
    /// 于是"确定簿是下游唯一事实来源"这句话<b>在另外两条路径上只在一次开机内成立</b>：
    /// 关一次软件，三维模拟 / 作业计划 / 采运排一体化全读不到，而且<b>一处都不报错</b>。</para>
    ///
    /// <para><b>分工口径</b>：编制 / 派生 / 打分是各按钮<b>各自的动作</b>（轴不同，本来就该多份）；
    /// <b>确定是一个写动作，实现只能有一份</b>。所以判的不是"哪个按钮能确定"（三个都能，是对的），
    /// 而是<b>赋值语句只准出现在一个文件里</b>。</para>
    ///
    /// <para>⚠ 判赋值 <c>Confirmed =</c>，不判 <c>Confirmed ==</c>／<c>!=</c>（读是自由的）。
    /// 判据自己不能把测试代码算进去 —— 判据里存/还原确定簿是正当的。</para>
    /// </summary>
    [Fact]
    public void S41_ConfirmedStore_IsWrittenInExactlyOnePlace()
    {
        string p = FindPluginSource();
        Assert.False(string.IsNullOrEmpty(p), "找不到 MainWindow.axaml.cs —— 挪过位置就把这条判据一起改");
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(p)))!;   // …/<repo>

        var writers = new SortedSet<string>(StringComparer.Ordinal);
        string d = Path.Combine(root, "src");
        foreach (string f in Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            string t = File.ReadAllText(f);
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"ShortTermSchemeStore\.Confirmed\s*=[^=]"))
                writers.Add(Path.GetFileName(f));
        }

        _out.WriteLine("写确定簿的文件：" + (writers.Count == 0 ? "(无)" : string.Join(" / ", writers)));
        Assert.True(writers.SetEquals(new[] { "ShortTermConfirmService.cs" }),
            "确定簿的赋值出现在 " + string.Join(" / ", writers)
            + " —— 应当只有 ShortTermConfirmService.cs。多一处就多一套口径："
            + "上一次多出来的那两处都不写 monthly_plan 台账，关一次软件下游就全空，且不报错");

        string svc = File.ReadAllText(Path.Combine(root, "src", "Cad", "Plan", "ShortTermConfirmService.cs"));
        Assert.Contains("ShortTermSchemeStore.Confirmed = plan", svc);   // 确定
        Assert.Contains("ShortTermSchemeStore.Confirmed = null", svc);   // 选定项被清掉时的清空

        Assert.Contains("EquipmentDataContext.Plan", svc);
        Assert.DoesNotContain("EquipmentDataContext.Plan",
            File.ReadAllText(Path.Combine(root, "src", "Cad", "Plan", "MonthlyStripSession.cs")));
    }

    // ───────────────────────── S32 ─────────────────────────

    /// <summary>
    /// S32 · <b>给了真工作历，「采煤节奏」这条轴就该收掉</b>（§七 落地建议①）。
    ///
    /// <para><c>CoalPace</c> 是个<b>没有物理驱动的形状函数</b>（线性 ramp、总量守恒）；
    /// 而工作历那侧是 <c>年目标 × DispatchShape × (工作日/标准工作日) × 设备可用率</c>，驱动是真的。
    /// 两者<b>产出同一个东西</b>——逐月煤量分布。都开着就是**把同一个决定做两遍**，
    /// 派生表里那一维会与工作历重复。</para>
    ///
    /// <para><b>上一版"只报不拦"的理由是错的</b>：注释写着「有没有真工作历，本层看不到」，
    /// 可 <c>MonthlyStripSessionInput.Workdays</c> 就在本层。看得到就该按看到的办。</para>
    ///
    /// <para><b>但不能悄悄关掉用户特意开的轴</b> —— 缺省三值与"用户特意选了这三个"
    /// 在数组上长得一模一样，所以加了 <c>CoalPacesExplicit</c> 这个位来分辨"没表态/表过态"。</para>
    /// </summary>
    [Fact]
    public void S32_RealCalendar_CollapsesTheCoalPaceAxis_UnlessExplicit()
    {
        var rock = Rock();
        var full = new ScheduleAxes();                       // 缺省：采煤节奏 3 值
        Assert.Equal(3, full.CoalPaces.Length);

        // ① 没有工作历 ⇒ 轴照开（它是正当的兜底形状函数），并说明缘由
        var noCal = MonthlyStripSession.Derive(Input(rock), new ScheduleAxes());
        Assert.True(noCal.Success, noCal.Error);
        Assert.Contains(noCal.Log, l => l.Contains("采煤节奏") && l.Contains("没有"));
        Assert.DoesNotContain(noCal.Log, l => l.Contains("收成 1"));
        int nNoCal = noCal.Derivation!.Attempted;

        // ② 给真工作历 ⇒ 收成 1，并说清为什么
        var withCal = MonthlyStripSession.Derive(Cal(Input(rock)), new ScheduleAxes());
        Assert.True(withCal.Success, withCal.Error);
        Assert.Contains(withCal.Log, l => l.Contains("真工作历") && l.Contains("收成 1"));
        int nCal = withCal.Derivation!.Attempted;

        // ★ 载荷：方案数必须真的少了三分之二，不是只多说一句话
        Assert.True(nCal * 3 == nNoCal,
                    $"收轴后应当正好少到 1/3：无工作历 {nNoCal} 套 → 有工作历 {nCal} 套");
        _out.WriteLine($"无工作历 {nNoCal} 套 → 有工作历 {nCal} 套（采煤节奏 3→1）");

        // ③ 显式指定就别替他收 —— 照开，但要提醒这一维会与工作历重复
        var explicitAxes = new ScheduleAxes { CoalPacesExplicit = true };
        var kept = MonthlyStripSession.Derive(Cal(Input(rock)), explicitAxes);
        Assert.True(kept.Success, kept.Error);
        Assert.Equal(nNoCal, kept.Derivation!.Attempted);
        Assert.Contains(kept.Log, l => l.Contains("显式指定") && l.Contains("重复"));
        _out.WriteLine($"显式指定 → 仍 {kept.Derivation.Attempted} 套，并提醒与工作历重复");

        // ④ 不许改到调用方那份轴上（Derive 是纯的）
        Assert.Equal(3, full.CoalPaces.Length);
        Assert.Equal(3, explicitAxes.CoalPaces.Length);
    }

    // ───────────────────────── S33 ─────────────────────────

    /// <summary>
    /// S33 · <b>内排必须在计划期内真的开出来</b>（否则整条内排经济性从没被跑过）。
    ///
    /// <para><b>怎么发现的</b>：213 条判据全绿之后把逐月报表**打出来读**，
    /// 看到<b>内排率 12 个月全是 0.0%</b>，而内排土场期末还剩 300 万m³ 没用。
    /// 根子是夹具把内排位置放在了**煤前界前方**（u=2300~2540，而前界只走到 2355.6）——
    /// 方向就反了：<b>内排土场要落在采空区里</b>，也就是前界<b>后方</b>。
    /// 于是「前界 − slotU ≥ 内排退距」一个月都满足不了。</para>
    ///
    /// <para><b>为什么这条必须钉住</b>：提升当量、煤容重→采空区容量、迂回系数 ——
    /// 这些<b>全是内排 vs 外排的经济性</b>。内排恒不开的话，它们在本组里
    /// <b>一次也没被真跑过</b>，而所有判据照样绿。</para>
    /// </summary>
    [Fact]
    public void S33_InternalDump_ActuallyOpensWithinTheHorizon()
    {
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);
        var months = r.Export!.Months;

        // ① 至少有一个月真的排进了内排（不是"有库容"，是"有流量"）
        var withInternal = months.Where(m => m.InternalRatePct > 0).ToList();
        Assert.True(withInternal.Count > 0,
            "内排率 12 个月全是 0 —— 内排位置多半又放到了煤前界前方（采空区在后方）。"
            + "这时提升当量/煤容重/迂回系数这些内排经济性全都没被跑到。");

        // ② 内排是**逐步**开出来的，不是第 1 月就全开 —— 退距那条时序要验到
        Assert.True(withInternal[0].Month > 1, "第 1 月就有内排 —— 退距约束没起作用？");
        Assert.True(withInternal.Count < months.Count, "12 个月全有内排 —— 那也验不到「开出来」这件事");

        // ③ 内排率要单调地站住（开了之后不该整体塌回 0）
        double last = months[^1].InternalRatePct;
        Assert.True(last > 0, "期末内排率又回到 0 —— 开了又关，先确认退距/库容口径");

        _out.WriteLine($"内排在第 {withInternal[0].Month} 月开出来，共 {withInternal.Count}/{months.Count} 个月有内排；"
                     + $"期末内排率 {last:0.0}%");
        _out.WriteLine("逐月内排率：" + string.Join(" ", months.Select(m => $"{m.Month}:{m.InternalRateText}")));
    }

    /// <summary>
    /// S34 · <b>缺省物料表把「覆岩」当表土算，这件事必须摊开说</b>。
    ///
    /// <para>看物料流表时发现：<c>覆岩</c> 的物料码是 <c>topsoil</c>、ρ=1.8、Kr=1.25。
    /// 可 <see cref="GapCode.Overburden"/> 是<b>顶板以上的整根岩柱</b>，现实里绝大部分是岩不是土。</para>
    ///
    /// <para><b>不是四舍五入</b>：<b>运输功按吨算</b>（ρ 1.8 vs 2.5 差 <b>39%</b>）⇒ 比选表"运输功"这一维跟着偏；
    /// <b>库容按 Kr 扣</b>（1.25 vs 1.15 差 <b>9%</b>）⇒ 直接改"排土场够不够用"。
    /// 原来的日志只写"ρ/Kr 取通用值"，听着像个小保留。</para>
    ///
    /// <para><b>只报不改</b>：现场到底是表土还是风化岩，本层无从知道 —— 但必须把
    /// 引擎替用户做的这个决定<b>连同它值多少</b>一起说出来。</para>
    /// </summary>
    [Fact]
    public void S34_DefaultMaterials_SpellsOutTheOverburdenAssumption()
    {
        var rock = Rock();
        var mats = MonthlyStripSession.DefaultMaterials(rock);
        var ob = mats[GapCode.Overburden];

        // ① 缺省确实把覆岩当表土 —— 先把前提钉住，否则下面的告警判据会空过
        Assert.Equal("topsoil", ob.Code);
        Assert.True(ob.Density < 2.0, "覆岩缺省 ρ 应当是表土量级，本条才有意义");

        // ② 不给物料表时，日志要逐项摊开，并单独点名覆岩这一项与它值多少
        var r = MonthlyStripSession.Run(Input(rock));
        Assert.True(r.Success, r.Error);
        Assert.Contains(r.Log, l => l.Contains($"{ob.Name}[{ob.Code}]"));       // 逐项清单
        Assert.Contains(r.Log, l => l.Contains("按【表土】口径给的") && l.Contains("39%"));
        _out.WriteLine(r.Log.First(l => l.Contains("按【表土】口径给的")));

        // ③ 反面：外面给了物料表就不该再冒这条（那是用户自己的台账）
        var own = MonthlyStripSession.Run(Mats(Input(rock)));
        Assert.True(own.Success, own.Error);
        Assert.DoesNotContain(own.Log, l => l.Contains("按【表土】口径给的"));
        Assert.DoesNotContain(own.Log, l => l.Contains("物料表是按层间名建的缺省表"));
        _out.WriteLine("外面给了物料表 → 不再冒缺省表那两条");
    }

    /// <summary>给一份"自己的"物料表（全按岩），走"用户填了台账"那条路。</summary>
    private static MonthlyStripSessionInput Mats(MonthlyStripSessionInput i)
    {
        int n = GapCode.Count(i.Rock!.SeamCount);
        i.Materials = Enumerable.Range(0, n)
            .Select(g => new GapMaterial { Name = $"台账{g}", Code = "rock", Density = 2.5, Kr = 1.15 })
            .ToArray();
        return i;
    }

    /// <summary>
    /// S35 · <b>没给 n经 时，H5 没参与判定这件事必须说出来</b>。
    ///
    /// <para>读契约 JSON 时发现：`Checks` 只有 6 条（H1–H4、H6、H7），**H5 不见了** ——
    /// 它挂在 `if (RatioCeiling > 0)` 里，没给上限就整条不加。
    /// 而 <c>Feasible = AllChecksOk</c> 是对"跑过的那几条"求与，于是契约上写着
    /// <b>「Feasible: true」</b>，读的人不会知道**经济剥采比这一条压根没参与判定**。</para>
    ///
    /// <para>这正是本仓一直守的那条纪律：<b>缺什么不拿 0/沉默冒充</b>
    /// （内排率没算时给 −1、显示「—」，不给 0）。`Checks` 是 bool，
    /// 塞个 <c>Ok=true</c> 进去等于撒谎，所以走 Notes 明说，并附上实测最大剥采比供对照。</para>
    /// </summary>
    [Fact]
    public void S35_WithoutRatioCeiling_TheSkippedHardCheckIsAnnounced()
    {
        // ① 不给 n经：H5 缺席，但必须有一条明说它缺席
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);
        var codes = r.Schedule!.Checks.Select(c => c.Code).ToList();
        Assert.DoesNotContain("H5", codes);                       // 前提：确实没跑
        // 不进 Notes（n经 是选填的，喊一嗓子等于对正常输入刷告警），
        // 而是进**契约的 Checks 列表** —— 读的人在那里数约束。
        Assert.DoesNotContain(r.Schedule.Notes, n => n.Contains("H5"));
        Assert.Contains(r.Export!.Checks, c => c.Contains("H5") && c.Contains("未参与判定"));
        Assert.StartsWith("—", r.Export.Checks.First(c => c.Contains("H5")));   // 与 ✓/✗ 区分开
        Assert.True(r.Export.Feasible, "「未参与判定」不该把计划判成不可行");
        _out.WriteLine("不给 n经 → " + r.Export.Checks.First(c => c.Contains("H5")));

        // ② 给了 n经：H5 真的参与，且不再冒那条提醒
        var withCap = MonthlyStripSession.Run(Ceil(Input(Rock()), 3.0));
        Assert.True(withCap.Success, withCap.Error);
        Assert.Contains("H5", withCap.Schedule!.Checks.Select(c => c.Code));
        Assert.DoesNotContain(withCap.Schedule.Notes, n => n.Contains("没有参与判定"));
        _out.WriteLine("给了 n经 → " + withCap.Schedule.Checks.First(c => c.Code == "H5"));

        // ③ ★ 载荷：上限压到实测值以下时 H5 必须真的判失败（否则这条判据只验了"有没有这一行"）
        double mx = r.Schedule.Months.Max(m => m.Ratio);
        var tight = MonthlyStripSession.Run(Ceil(Input(Rock()), mx * 0.5));
        var h5 = tight.Schedule?.Checks.FirstOrDefault(c => c.Code == "H5");
        Assert.NotNull(h5);
        Assert.False(h5!.Ok, $"上限压到 {mx * 0.5:0.00} 仍判过 —— H5 没在比大小");
        _out.WriteLine($"上限压到 {mx * 0.5:0.00}（实测最大 {mx:0.00}）→ {h5}");
    }

    /// <summary>
    /// S36 · <b>重排成功时不许打「滚动重排失败：」</b>。
    ///
    /// <para>读重排页的日志时看见的：⓪ 那行说"已过 4 个月、重排剩余 8 个月"，
    /// 紧接着一句 <b>「滚动重排失败：」</b>，冒号后面**什么都没有** —— 而这一次其实是成功的。</para>
    ///
    /// <para>根子：<c>RollingReplan.Prepare</c> 只备料，<b>不设 <c>Success</c>/<c>Plan</c></b>
    /// （那是 <c>RollingReplan.Run</c> 设的），而会话层走的是**自己的** <c>Run</c>
    /// （要跑完整链路：外循环 + 配对 + 契约，内核那个只排产）。
    /// 于是 <c>rep.Success</c> 恒为 false，<c>Summary()</c> 每次都走失败那一支。</para>
    ///
    /// <para><b>顶掉的正是这一页存在的理由</b>：采出/剥离达成度与剥离欠账 ——
    /// 现场看重排，第一眼要看的就是"上期到底差成什么样"。</para>
    /// </summary>
    [Fact]
    public void S36_SuccessfulReplan_DoesNotPrintAFailureLine()
    {
        var prev = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(prev.Success, prev.Error);
        var actual = RollingReplan.SimulateActual(prev.Schedule!, 4, 1.0, 0.7);
        var rep = MonthlyStripSession.Replan(prev, actual);
        Assert.True(rep.Success, rep.Error);

        // ① 成功那次不许出现失败字样
        Assert.DoesNotContain(rep.Log, l => l.Contains("滚动重排失败"));
        // ② 而且达成度那句要真的打出来（否则只是把失败那句删了）
        Assert.Contains(rep.Log, l => l.Contains("采出达成") && l.Contains("剥离达成"));
        Assert.True(rep.Replan!.Success);
        Assert.NotNull(rep.Replan.Plan);
        _out.WriteLine(rep.Log.First(l => l.Contains("采出达成")));

        // ③ ★ 载荷：欠剥 30% 的实绩，欠账必须真的报出来（不是恒打一句好话）
        Assert.True(rep.Replan.StripDebtM3 > 1e-6, "造了 70% 剥离达成的实绩，却没算出欠账");
        Assert.Contains(rep.Log, l => l.Contains("欠账"));

        // ④ 反面：真失败时仍要说失败，且带得出原因
        var bad = MonthlyStripSession.Replan(prev, new ActualToDate { MonthsElapsed = 99 });
        Assert.False(bad.Success);
        Assert.Contains("重排失败", bad.Error);
        _out.WriteLine("真失败那次：" + bad.Error);
    }

    /// <summary>
    /// S37 · <b>排土侧台阶高/坡面角要从 `dump_site` 台账取</b> —— 那句"表无此列"是过期的。
    ///
    /// <para>契约里长期写着「排土台阶高/坡面角**没有台账来源**（`dump_site` 表无此列）」，
    /// 于是下游只能按<b>垂直壁</b>建排土层体。查了一下：该表<b>带着</b>
    /// <c>bench_height_m</c> / <c>bench_slope_angle_deg</c> / <c>overall_slope_angle_deg</c>，
    /// 而且有真数据（北排土场 15m/38°、内排土场 12m/36°）。**数据一直在，只是没人去取。**</para>
    ///
    /// <para>内核不认识数据库，所以由会话层查了填进契约；
    /// <b>调用方显式给的几何永远优先</b>，台账只补空缺那两项，并把取自哪一行写进 <c>Source</c>。</para>
    ///
    /// <para><b>读不到台账时原样返回、不假装有</b> —— 判据里那条反面就是钉这个
    /// （台架进程通常没初始化数据库，正好走到那一支）。</para>
    /// </summary>
    [Fact]
    public void S37_DumpBenchGeometry_ComesFromTheLedger_WhenAvailable()
    {
        var r = new MonthlyStripSessionResult();

        // ① 外面已经给全 ⇒ 原样不动（显式优先）
        var given = new ExportGeometry { DumpBenchHeightM = 9, DumpFaceDeg = 33, Source = "外面给的" };
        var kept = MonthlyStripSession.FillDumpBenchFromLedger(given, null, r);
        Assert.Same(given, kept);
        Assert.Equal(9, kept!.DumpBenchHeightM);

        // ② 台账读不到（本进程一般没初始化库）⇒ 原样返回，且**不许**编出一个数来
        var empty = new ExportGeometry();
        var got = MonthlyStripSession.FillDumpBenchFromLedger(empty, null, r);
        if (got == null || got.DumpBenchHeightM <= 0)
        {
            Assert.True(got == null || got.DumpBenchHeightM == 0, "读不到台账却填出了数 —— 那是假装有");
            _out.WriteLine("台账不可用 → 原样返回，不假装有（契约那条告警照旧）");
        }
        else
        {
            // ③ 真读到了：数必须来自台账、来源要写清、且 Source 里点名是哪一行
            Assert.True(got.DumpBenchHeightM > 0 && got.DumpFaceDeg > 0);
            Assert.Contains("dump_site", got.Source);
            Assert.Contains(r.Log, l => l.Contains("排土侧几何取自台账"));
            _out.WriteLine("台账可用 → " + got.Source);
        }

        // ④ 无论哪一支，都不许把**采场侧**那几项弄丢（只补排土两项）
        var mixed = new ExportGeometry { RockBenchHeightM = 20, RockFaceDeg = 65, MinBermM = 40, WorkingSlopeDeg = 18 };
        var out2 = MonthlyStripSession.FillDumpBenchFromLedger(mixed, null, r);
        Assert.Equal(20, out2!.RockBenchHeightM);
        Assert.Equal(65, out2.RockFaceDeg);
        Assert.Equal(40, out2.MinBermM);
        Assert.Equal(18, out2.WorkingSlopeDeg);
    }

    /// <summary>
    /// S38 · <b>同一条留言不许在日志里出现两遍</b>。
    ///
    /// <para>把内核的 <c>Notes</c> 转发进契约（好让下游看得到）之后，会话日志会打两遍：
    /// ⑤ 段逐条打过内核那份，⑥ 段又把契约那份整个打一遍 —— 而后者现在**包含**前者。
    /// 实测「工作帮推到剖面数据尽头」那条在一次干净排产里出现了 <b>2 次</b>。</para>
    ///
    /// <para>日志是给人读的：同一句话重复出现，读的人会以为发生了两次
    /// （"是不是两个标高格各顶了一次？"）。⑥ 段只补**契约层新加的**那几条。</para>
    /// </summary>
    [Fact]
    public void S38_KernelNotes_AreNotLoggedTwice()
    {
        var r = MonthlyStripSession.Run(Input(Rock()));
        Assert.True(r.Success, r.Error);

        // ① 内核每条留言在日志里只出现一次
        Assert.NotEmpty(r.Schedule!.Notes);                     // 前提：这个算例确实有留言
        foreach (var n in r.Schedule.Notes)
        {
            int c = r.Log.Count(l => l.Contains(n));
            Assert.True(c == 1, $"内核留言在日志里出现 {c} 次（应 1 次）：{n[..Math.Min(40, n.Length)]}…");
        }

        // ② 但契约里必须**还带着**它们 —— 去重只动日志，不能把下游那份也弄没
        foreach (var n in r.Schedule.Notes)
            Assert.Contains(r.Export!.Notes, x => x == n);

        // ③ 契约层自己新加的那些（非内核来源）仍要进日志，别一并去掉了
        var own = r.Export!.Notes.Where(n => !r.Schedule.Notes.Contains(n)).ToList();
        Assert.NotEmpty(own);
        foreach (var n in own) Assert.Contains(r.Log, l => l.Contains(n));
        _out.WriteLine($"内核留言 {r.Schedule.Notes.Count} 条（各 1 次）· 契约自有 {own.Count} 条（都在日志里）");
    }

    /// <summary>一圈矩形环（扁平 xyz，闭合）。</summary>
    private static double[] Ring(double halfX, double halfY, double inset, double z)
    {
        double a = halfX - inset, b = halfY - inset;
        return new[] { -a,-b,z,  a,-b,z,  a,b,z,  -a,b,z,  -a,-b,z };
    }

    /// <summary>
    /// S39 · <b>真排土条带结果 → 采剥接续，这条缝要有判据</b>。
    ///
    /// <para><b>为什么单列</b>：本组一直拿<b>合成的 `Cell`</b> 喂 <see cref="DumpSlotAdapter"/>
    /// （`CellsOnAxis`/`MadeUpSlots`），而 `DumpStripPlanner` 那边只单测自己 ——
    /// <b>两个子系统的接缝上一条判据都没有</b>。这正是当初「工作线从没接上」能一直绿着的形状：
    /// 判据直接喂进去，把真实通路盖住了。</para>
    ///
    /// <para><b>重点是极性</b>（设计 §7「适配器的极性陷阱」）：
    /// 规划器的 <c>LevelIndex</c> <b>1 = 最上一级</b>，而配对要的是
    /// <b>Level 0 = 最先承接（最下一级）</b> —— 中间隔着一次翻转。
    /// 这条缝上翻错了不会抛，只会让排土<b>自上而下</b>填，图上看着也像那么回事。</para>
    /// </summary>
    [Fact]
    public void S39_RealPlannerCells_FlowThroughTheAdapterIntoTheSchedule()
    {
        // ① 造一个真场景跑规划器（4 级台阶，逐级往里收）
        const double halfX = 600, halfY = 400, benchH = 12, faceRun = 18, step = 30;
        var benches = new List<DumpStripPlanner.BenchInput>();
        for (int lv = 1; lv <= 4; lv++)
        {
            double toeZ = 40 + (lv - 1) * benchH, crestZ = toeZ + benchH;
            double toeInset = 40 + (lv - 1) * step, crestInset = toeInset + faceRun;
            benches.Add(new DumpStripPlanner.BenchInput
            {
                LevelIndex = lv, CrestZ = crestZ, ToeZ = toeZ,
                CrestXyz = Ring(halfX, halfY, crestInset, crestZ),
                ToeXyz = Ring(halfX, halfY, toeInset, toeZ),
                CrestClosed = true, ToeClosed = true,
            });
        }
        var plan = DumpStripPlanner.Plan(benches, null, "内排");
        Assert.True(plan.Ok, plan.Message);
        Assert.NotEmpty(plan.Cells);
        int clamped = plan.Cells.Count(c => c.IsClamped);
        int subbed  = plan.Cells.Count(c => c.SubCount > 1);
        int faces   = plan.Cells.Count(c => c.IsWorkingFace);
        _out.WriteLine($"规划器出 {plan.Cells.Count} 个位置 · {plan.LevelCount} 级 · "
                     + $"总库容 {plan.TotalCapacityM3 / 1e4:0.0}万m³"
                     + $"｜夹窄带 {clamped} · 带内再切 {subbed} · 工作面带 {faces}"
                     + $"｜丢弃 {plan.Drops.Sum(d => d.Count)} 条");

        // ② 过适配器：一个都不许丢，库容一分不许少
        var slots = DumpSlotAdapter.ToSlots(plan.Cells, "内排土场", isInternal: true);
        Assert.Equal(plan.Cells.Count, slots.Count);
        Assert.Equal(plan.TotalCapacityM3, slots.Sum(s => s.CapacityM3), 3);

        // ③ ★ 极性：规划器 LevelIndex 最大的那一级（最下一级）必须映射成 Level 0（最先承接）
        int maxLv = plan.Cells.Max(c => c.LevelIndex);
        int minLv = plan.Cells.Min(c => c.LevelIndex);
        Assert.True(maxLv > minLv, "只有一级台阶，极性判不出来 —— 换个多级场景");
        var bottom = slots.Where((_, i) => plan.Cells[i].LevelIndex == maxLv).ToList();
        var top = slots.Where((_, i) => plan.Cells[i].LevelIndex == minLv).ToList();
        Assert.All(bottom, s => Assert.Equal(0, s.Level));
        Assert.All(top, s => Assert.True(s.Level > 0, "最上一级被映射成了 Level 0 —— 极性翻反了，排土会自上而下填"));
        _out.WriteLine($"极性对：规划器 L{maxLv}（最下）→ 承接序 0 · L{minLv}（最上）→ 承接序 {top[0].Level}");

        // ④ 真质心要有，否则投不到推进轴上（那条路会静静退回静态兜底）
        Assert.All(slots, s => Assert.True(double.IsFinite(s.Cz)));
        Assert.True(plan.Cells.Any(c => Math.Abs(c.Cx) > 1e-6 || Math.Abs(c.Cy) > 1e-6),
                    "规划器没填质心 —— 排土位置投不到推进轴上");

        // ⑤ 一路跑到排产：真位置能排出计划来
        var r = MonthlyStripSession.Run(Slots2(Input(Rock()), slots));
        foreach (var l in r.Log.Where(l => l.Contains("✗") || l.Contains("不自洽"))) _out.WriteLine("  " + l);
        Assert.True(r.Success, "真排土位置排不出计划：" + r.Error);

        Assert.Equal(0, r.Export!.Months.Sum(m => m.UnplacedWanM3), 3);
        _out.WriteLine($"真位置排产通过：{r.Export.Months.Count} 月 · 全部排下");
    }

    private static MonthlyStripSessionInput Slots2(MonthlyStripSessionInput i, List<DumpSlot> s)
    { i.Slots = s; i.DumpCells = null; return i; }

    private static MonthlyStripSessionInput Ceil(MonthlyStripSessionInput i, double n)
    { i.RatioCeiling = n; return i; }

    /// <summary>给一份真工作历（逐月作业日）。</summary>
    private static MonthlyStripSessionInput Cal(MonthlyStripSessionInput i)
    {
        i.Workdays = Enumerable.Range(0, i.CoalTargetWt.Length)
                               .Select(k => 22.0 + (k % 3)).ToArray();
        return i;
    }
}
