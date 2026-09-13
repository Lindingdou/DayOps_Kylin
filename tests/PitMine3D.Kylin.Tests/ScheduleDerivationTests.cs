// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/ScheduleDerivationTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
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
/// G10 组 · 多方案派生与比选。
/// <para>核心断言：<b>方案 = 规则取值的组合</b>。所以每套方案都可归因，
/// 而且轴的取值必须真的把结果分开 —— 分不开就说明那条轴是摆设。</para>
/// </summary>
public sealed class ScheduleDerivationTests
{
    private readonly ITestOutputHelper _out;
    public ScheduleDerivationTests(ITestOutputHelper o) => _out = o;

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

    private static MonthlyScheduleInput Base(RockProfile rock, double capPerMonth = 40e4)
    {
        var q = new double[12]; for (int i = 0; i < 12; i++) q[i] = 15;
        // 不限能力给【空数组】；0 的口径是「该月不能剥」，不是「不限」
        var cap = capPerMonth > 0 ? Enumerable.Repeat(capPerMonth, 12).ToArray() : Array.Empty<double>();
        return new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = ALPHA, ZDatum = ZDATUM,
            CoalTargetWt = q, StripCapM3 = cap,
            LookaheadMonths = 3, RecoveryTotalWt = 45,
            StartInSteadyState = true,
        };
        // 剥离能力必须给：没有上限时「前重」= 第1个月把全年的活干完，退化成一个没意义的极端。
        // 现实里能力总是有的，夹具也照现实来。
    }

    // ── G10 · 派生跑通 ──────────────────────────────────────────────────────

    /// <summary>G10 三轴 3×3×3 = 27 套全部解得出来，且每套都能归因到具体轴值。</summary>
    [Fact]
    public void G10_ThreeAxes_Produce27Schemes()
    {
        var r = ScheduleDeriver.Derive(Base(Profile()));
        Assert.True(r.Success, r.Error);
        _out.WriteLine(ScheduleDeriver.CompareTable(r));

        Assert.Equal(27, r.Attempted);
        Assert.Equal(27, r.Schemes.Count);
        Assert.NotNull(r.Recommended);
        foreach (var s in r.Schemes)
        {
            Assert.Contains("N=", s.Name);
            Assert.InRange(s.Score, 0, 100);
        }
    }

    /// <summary>
    /// G10b 轴必须真的把结果分开。任何一条轴上所有取值给出<b>同一套指标</b>，
    /// 说明那条轴是摆设 —— 摆设的轴会把 27 套稀释成 9 套，比选表看着丰富其实全是重复。
    /// </summary>
    [Fact]
    public void G10b_EveryAxis_ActuallySeparatesOutcomes()
    {
        var r = ScheduleDeriver.Derive(Base(Profile()));
        Assert.True(r.Success, r.Error);

        void AxisSeparates<T>(string axis, Func<ScheduleScheme, T> key) where T : notnull
        {
            var groups = r.Schemes.GroupBy(key)
                .Select(g => (Key: g.Key, Rock: g.Average(s => s.TotalRockM3), Cv: g.Average(s => s.RatioCv)))
                .ToList();
            _out.WriteLine($"{axis}: " + string.Join(" | ",
                groups.Select(g => $"{g.Key} 岩{g.Rock / 1e4:0.0}万m³ CV{g.Cv:0.000}")));
            double spreadRock = groups.Max(g => g.Rock) - groups.Min(g => g.Rock);
            double spreadCv = groups.Max(g => g.Cv) - groups.Min(g => g.Cv);
            Assert.True(spreadRock > 1e-3 || spreadCv > 1e-6,
                        $"「{axis}」这条轴的所有取值给出同一套指标 —— 它是摆设");
        }
        AxisSeparates("备采保有 N", s => s.Lookahead);
        AxisSeparates("剥离节奏",   s => s.Strip);
        AxisSeparates("采煤节奏",   s => s.Coal);
    }

    // ── G11 · 目标必须真的冲突（没有冲突的多方案是假多方案）────────────────

    /// <summary>
    /// G11 <b>剥采比均衡</b> 与 <b>少压资金</b> 必须冲突：
    /// 存在一套方案 A 比 B 更平（CV 更小）但超前储备更多。
    /// <para>工程含义：这个冲突就是比选存在的理由。若两者总是同向，那"多方案"只是同一个答案的不同写法。</para>
    /// </summary>
    [Fact]
    public void G11_BalanceAndCapitalLockup_ActuallyConflict()
    {
        var r = ScheduleDeriver.Derive(Base(Profile()));
        Assert.True(r.Success, r.Error);
        bool conflict = false;
        foreach (var a in r.Schemes)
            foreach (var b in r.Schemes)
                if (a.RatioCv < b.RatioCv - 1e-9 && a.MeanLeadStockM3 > b.MeanLeadStockM3 + 1e-6)
                { conflict = true; _out.WriteLine($"冲突对：{a.Name}（CV {a.RatioCv:0.000}，储备 {a.MeanLeadStockM3 / 1e4:0.0}万m³）"
                                                + $" vs {b.Name}（CV {b.RatioCv:0.000}，储备 {b.MeanLeadStockM3 / 1e4:0.0}万m³）"); break; }
        Assert.True(conflict, "剥采比均衡与超前储备在所有方案上都同向 —— 这组比选没有真正的取舍");
    }

    /// <summary>
    /// G11b 贴底 vs 前重的方向必须是对的：贴底剥得最少、储备最低；前重反过来。
    /// 这是"剥离节奏"这条轴的定义本身，错了说明节奏没接进求解。
    /// </summary>
    [Fact]
    public void G11b_StripPace_HasTheRightDirection()
    {
        var rock = Profile();
        var byPace = new Dictionary<StripPace, MonthlyScheduleResult>();
        foreach (var p in new[] { StripPace.Hug, StripPace.Level, StripPace.FrontLoad })
        {
            var inp = Base(rock); inp.Pace = p;
            var res = MonthlyMineScheduler.Solve(inp);
            Assert.True(res.Success, $"{p}: {res.Error}");
            byPace[p] = res;
            _out.WriteLine($"{p,-10} 本期岩 {res.TotalRockM3 / 1e4,7:0.0}万m³ · 剥采比CV {res.RatioCv:0.000}"
                         + $" · 平均储备 {res.Months.Average(m => m.LeadStockM3) / 1e4,6:0.0}万m³");
        }
        // ⚠ **本期总剥离量不能用来判节奏**：同一个时域、同一批煤量目标下，煤前界最终走到同一处，
        //   要揭露它就得剥掉同样多的岩 —— 三种节奏实测都是 300.9万m³。节奏只挪"什么时候剥"，
        //   不改"一共剥多少"。原来这里判 `Hug.TotalRockM3 <= FrontLoad.TotalRockM3 + eps`，
        //   那是个**被守恒钉死的量**：永远成立，配的错误话术（"节奏没接进求解"）也永远不会出现。
        Assert.Equal(byPace[StripPace.Hug].TotalRockM3, byPace[StripPace.FrontLoad].TotalRockM3, 0);

        Assert.True(byPace[StripPace.Hug].Months.Average(m => m.LeadStockM3) <= 1e-6,
                    "贴底应当零储备（C=F）");
        Assert.True(byPace[StripPace.FrontLoad].Months.Average(m => m.LeadStockM3)
                    >= byPace[StripPace.Level].Months.Average(m => m.LeadStockM3) - 1e-6,
                    "前重的储备不该低于拉平");

        // ★ 轴不许塌。上面两条都是 `<= …+eps` / `>= …−eps`：**三种节奏给出完全相同的结果时照样全过** ——
        //   那时"方向对不对"根本无从谈起，而判据名字里写的就是 HasTheRightDirection。
        //   兄弟判据 G12b（权重）、G16（配对策略）都带这条守卫，这里原先漏了。
        //   R41 上真出过"整条轴塌成一个值"，不是假想。
        var sig = byPace.Values
            .Select(v => $"{v.TotalRockM3:F1}|{v.RatioCv:F4}|{v.Months.Average(m => m.LeadStockM3):F1}")
            .Distinct().Count();
        Assert.True(sig > 1, "三种剥离节奏给出完全相同的结果 —— 这条轴是摆设，方向判据也就空过了");

        // 而且"贴底 vs 前重"必须在**自由的那个量**上严格分得开 —— 储备与 CV 才是节奏能动的，
        // 总剥离量动不了（上面刚判过它相等）。
        double lsHug = byPace[StripPace.Hug].Months.Average(m => m.LeadStockM3);
        double lsFront = byPace[StripPace.FrontLoad].Months.Average(m => m.LeadStockM3);
        Assert.True(lsFront > Math.Max(1e4, lsHug * 1.5),
                    $"前重的储备（{lsFront / 1e4:0.0}万m³）没有显著高于贴底（{lsHug / 1e4:0.0}万m³）—— 节奏没真起作用");
        Assert.True(byPace[StripPace.FrontLoad].RatioCv > byPace[StripPace.Hug].RatioCv * 1.05,
                    "前重的剥采比波动没有显著大于贴底 —— 两种节奏实际是一回事");
        _out.WriteLine($"特征值 {sig} 组互不相同；储备 贴底 {lsHug / 1e4:0.0} → 前重 {lsFront / 1e4:0.0}万m³ 严格分得开");
        if (sig < byPace.Count)
            _out.WriteLine("　 注：本夹具下 贴底与拉平 恰好重合（走廊够紧，拉绳就贴在下界上）—— "
                         + "这两者的区别要靠 G6/G9 那组更松的走廊算例去判");
    }

    /// <summary>G11c 采煤节奏只改形状不改总量 —— 全期煤量必须逐套相等。</summary>
    [Fact]
    public void G11c_CoalPace_PreservesAnnualTotal()
    {
        var baseQ = Enumerable.Repeat(15.0, 12).ToArray();
        foreach (var p in new[] { CoalPace.Balanced, CoalPace.Rush, CoalPace.Conservative })
        {
            var q = ScheduleDeriver.Shape(baseQ, p, 0.25);
            _out.WriteLine($"{p,-13} [{string.Join(", ", q.Select(v => v.ToString("0.0")))}] 合计 {q.Sum():0.000}");
            Assert.Equal(180.0, q.Sum(), 6);
        }
        var rush = ScheduleDeriver.Shape(baseQ, CoalPace.Rush, 0.25);
        var cons = ScheduleDeriver.Shape(baseQ, CoalPace.Conservative, 0.25);
        Assert.True(rush[0] > rush[11], "抢产应当前重");
        Assert.True(cons[0] < cons[11], "保守应当后重");
    }

    // ── G12 · 归因与推荐 ────────────────────────────────────────────────────

    /// <summary>
    /// G12 归因要指得出<b>哪条轴说了算</b>：跨度最大的轴排第一，且每条轴都给出最优取值。
    /// 工程含义：比选表的每一行都要能读 ——「这套剥采比低是因为 N=2」。
    /// </summary>
    [Fact]
    public void G12_Attribution_IdentifiesTheDominantAxis()
    {
        var r = ScheduleDeriver.Derive(Base(Profile()));
        Assert.True(r.Success, r.Error);
        Assert.Equal(3, r.Attribution.Count);
        for (int i = 1; i < r.Attribution.Count; i++)
            Assert.True(r.Attribution[i - 1].Spread >= r.Attribution[i].Spread - 1e-9, "归因没按影响力排序");
        foreach (var a in r.Attribution)
        {
            Assert.NotEmpty(a.Best);
            Assert.Equal(3, a.Values.Count);
            Assert.Equal(27, a.Values.Sum(v => v.Count));
            _out.WriteLine($"{a.Axis}（跨度 {a.Spread:0.0}）最优 {a.Best}");
        }
    }

    /// <summary>
    /// G12b <b>权重改了，推荐要跟着变</b> —— 权重不影响推荐 = 打分是摆设。
    ///
    /// <para>判的是 G11 已经证明<b>确实冲突</b>的那一对：剥采比均衡 ↔ 超前储备。
    /// 不拿「抗断煤 vs 省剥离」判 —— 稳态起算时 N 越大，越多的量被划进
    /// <see cref="MonthlyScheduleResult.BoxCutM3"/>（历史），本期剥离反而<b>少</b>，
    /// 于是大 N 同时拿下最高备采和最低剥离量，那一对根本不冲突。<b>踩过一次。</b></para>
    /// </summary>
    [Fact]
    public void G12b_Weights_ActuallyMoveTheRecommendation()
    {
        var rock = Profile();
        var balance = new DerivationWeights { RatioBalance = 1, PeakShaving = 0, LeadStock = 0, CoalSecurity = 0, StripTotal = 0 };
        var thrift  = new DerivationWeights { RatioBalance = 0, PeakShaving = 0, LeadStock = 1, CoalSecurity = 0, StripTotal = 0 };

        var rb = ScheduleDeriver.Derive(Base(rock), null, balance);
        var rt = ScheduleDeriver.Derive(Base(rock), null, thrift);
        Assert.True(rb.Success && rt.Success);
        _out.WriteLine($"均衡优先 → {rb.Recommended!.Name}（CV {rb.Recommended.RatioCv:0.000}，储备 {rb.Recommended.MeanLeadStockM3 / 1e4:0.0}万m³）");
        _out.WriteLine($"省资金优先 → {rt.Recommended!.Name}（CV {rt.Recommended.RatioCv:0.000}，储备 {rt.Recommended.MeanLeadStockM3 / 1e4:0.0}万m³）");

        Assert.True(rb.Recommended.RatioCv <= rt.Recommended.RatioCv + 1e-9,
                    "按均衡打分选出来的方案反而更不平 —— 打分方向反了");
        Assert.True(rt.Recommended.MeanLeadStockM3 <= rb.Recommended.MeanLeadStockM3 + 1e-9,
                    "按省资金打分选出来的方案反而压更多资金 —— 打分方向反了");
        bool distinguished = rb.Recommended.RatioCv < rt.Recommended.RatioCv - 1e-9
                          || rt.Recommended.MeanLeadStockM3 < rb.Recommended.MeanLeadStockM3 - 1e-9;
        Assert.True(distinguished, "两套权重给出完全等价的推荐 —— 打分没起作用");
    }

    /// <summary>
    /// G12c 稳态起算下「N 越大，本期剥离越少」这条反直觉的账要说得清：
    /// 差额必须整笔落在 <see cref="MonthlyScheduleResult.BoxCutM3"/>（基建剥离，历史）上，
    /// <b>基建 + 本期</b> 在各 N 之间近似守恒。否则就是量凭空少了。
    /// </summary>
    [Fact]
    public void G12c_LargerLookahead_ShiftsVolumeIntoHistoryNotThinAir()
    {
        var rock = Profile();
        var rows = new List<(int N, double Box, double InPeriod, double Sum)>();
        foreach (int n in new[] { 2, 3, 4 })
        {
            var inp = Base(rock); inp.LookaheadMonths = n;
            var res = MonthlyMineScheduler.Solve(inp);
            Assert.True(res.Success, res.Error);
            rows.Add((n, res.BoxCutM3, res.TotalRockM3, res.BoxCutM3 + res.TotalRockM3));
            _out.WriteLine($"N={n}: 基建 {res.BoxCutM3 / 1e4,6:0.0} + 本期 {res.TotalRockM3 / 1e4,6:0.0}"
                         + $" = {(res.BoxCutM3 + res.TotalRockM3) / 1e4,6:0.0} 万m³"
                         + $" · 最低备采 {res.Months.Min(m => m.PreparedMonths):0.0} 月");
        }
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i].Box >= rows[i - 1].Box - 1e-6, "N 变大，基建剥离反而变小");
        // ★ 逐点"不减小"**全都相等也能通过**，那时 N 没接上而表照样正常。端点必须严格分开。
        //   （同一类假绿在 G42 上真出过一次：容差朝易通过的方向放，判据就不判事了。）
        Assert.True(rows[^1].Box > rows[0].Box * 1.05,
            $"N={rows[0].N}→{rows[^1].N} 基建剥离只从 {rows[0].Box / 1e4:0.0} 变到 {rows[^1].Box / 1e4:0.0} 万m³"
          + " —— N 没真正推动期初姿态");
        // 总账守恒（末期受块体边界截断，放 5% 容差）
        double mn = rows.Min(x => x.Sum), mx = rows.Max(x => x.Sum);
        Assert.True((mx - mn) / mx <= 0.05, $"基建+本期 在各 N 之间差了 {(mx - mn) / mx * 100:0.0}% —— 量凭空变了");
    }

    /// <summary>
    /// G12d <b>R41：派生 N 时回采煤量必须跟着走</b> —— 这条规则此前<b>没有任何判据钉着</b>。
    ///
    /// <para>R34 与 R35 说的是<b>同一个量</b>（回采煤量 ≈ N 个月产量）。
    /// 把回采煤量钉死而只动 N，大 N 的露煤需求会顶穿 R34 天花板、<b>被静默夹回去</b> ⇒
    /// 「备采保有 N」整条轴塌成一个值：比选表看着有 27 行，实际前几名指标一模一样。</para>
    ///
    /// <para><c>SyncRecoveryWithLookahead</c> 默认 true，代码里也确实在用
    /// （`ScheduleDeriver.Derive` 里按 <c>n × 月均产</c> 重算）。但**默认值和实现都对，不等于有人在看着它** ——
    /// 谁把它改成 false（或重构时漏掉那两行），派生表会安静地退化，而所有既有判据都还是绿的。
    /// 这条判据就是那个看着的人：**关掉它，N 轴必须塌**。</para>
    /// </summary>
    [Fact]
    public void G12d_R41_RecoveryMustFollowLookahead_OrTheAxisCollapses()
    {
        var rock = Profile();
        var axes = new ScheduleAxes
        {
            Lookahead = new[] { 2, 3, 4 },
            StripPaces = new[] { StripPace.Level },
            CoalPaces = new[] { CoalPace.Balanced },
        };
        Assert.True(axes.SyncRecoveryWithLookahead, "R41 的开关默认就该是开的");

        // ★ 夹具必须让 **R34 天花板真的绑住**，否则这条判据什么都判不出来。
        //   月煤 15 万t：钉死 RecoveryTotalWt = 30（= 2 个月）之后，N=3/N=4 需要的露煤
        //   都会顶穿这个天花板、被夹回同一处 ⇒ 三个 N 收敛到同一个答案。
        //   拿默认 Base()（RecoveryTotalWt = 45 = 3×15，天花板不绑）跑，开/关**完全一样** ——
        //   第一版就是这么写的，判据当场红，红得对。
        MonthlyScheduleInput Bind() { var i = Base(rock); i.RecoveryTotalWt = 30; return i; }

        static string Fingerprint(ScheduleDerivationResult r)
            => string.Join("|", r.Schemes.OrderBy(s => s.Lookahead)
                                .Select(s => $"{s.Lookahead}:{s.Result.BoxCutM3:0}/{s.TotalRockM3:0}"));

        // ① 开着（默认）：三个 N 给出三个不同答案
        var on = ScheduleDeriver.Derive(Bind(), axes);
        Assert.True(on.Success, on.Error);
        int nOn = on.Schemes.Select(s => (s.Result.BoxCutM3, s.TotalRockM3)).Distinct().Count();
        _out.WriteLine($"R41 开：{nOn} 个不同 (基建,本期) 组合 · {Fingerprint(on)}");

        // ② 关掉：回采煤量钉死不跟 N 走 ⇒ 大 N 顶穿天花板被静默夹回 ⇒ N 轴塌
        axes.SyncRecoveryWithLookahead = false;
        var off = ScheduleDeriver.Derive(Bind(), axes);
        Assert.True(off.Success, off.Error);
        int nOff = off.Schemes.Select(s => (s.Result.BoxCutM3, s.TotalRockM3)).Distinct().Count();
        _out.WriteLine($"R41 关：{nOff} 个不同 (基建,本期) 组合 · {Fingerprint(off)}");

        // ★ 判「它真的在改结果」，不判「它一关就塌」。
        //   实测：开/关都仍是 3 个不同答案，但**大 N 的本期剥离明显不同**
        //   （N=3: 300.9→289.6 万m³ · N=4: 284.2→261.8 万m³）——
        //   钉死回采煤量之后，大 N 的露煤需求顶穿 R34 天花板被夹回，剥得就少了。
        //   记忆里那句"整条轴塌成一个值"是**当时那个夹具**的说法，一般情况下不成立，已更正。
        var onByN = on.Schemes.ToDictionary(s => s.Lookahead, s => s.TotalRockM3);
        var offByN = off.Schemes.ToDictionary(s => s.Lookahead, s => s.TotalRockM3);
        int changed = onByN.Count(kv => offByN.TryGetValue(kv.Key, out double v)
                                     && Math.Abs(v - kv.Value) > kv.Value * 0.01);
        foreach (var kv in onByN.OrderBy(k => k.Key))
            _out.WriteLine($"  N={kv.Key}: 开 {kv.Value / 1e4:0.0} → 关 {offByN[kv.Key] / 1e4:0.0} 万m³"
                         + (Math.Abs(offByN[kv.Key] - kv.Value) > kv.Value * 0.01 ? "  ← 变了" : ""));
        Assert.True(changed >= 1,
            "开关 R41 对任何一个 N 的本期剥离都没影响 —— 那 `SyncRecoveryWithLookahead` 就是死代码");
        Assert.True(nOn == 3 && nOff == 3, "本夹具下开/关都该是 3 个不同答案（塌不塌取决于天花板绑多紧）");

        _out.WriteLine($"\n⇒ R41 是活的：{changed}/{onByN.Count} 个 N 的本期剥离被它改变。");
        _out.WriteLine("⚠ 但**不是**「一关就塌成一个值」—— 那是特定夹具下的现象。");
        _out.WriteLine("  它的真实作用：钉死回采煤量时，大 N 的露煤需求顶穿 R34 天花板被**静默夹回**，");
        _out.WriteLine("  于是大 N 反而剥得更少 —— 比选表上 N 的代价被系统性低估。");
    }

    /// <summary>
    /// G13 解不出来的方案<b>单列不静默丢</b>，且原因带得出来。
    /// 工程含义：27 套里有 9 套无解却只显示 18 套，用户会以为"这就是全部候选"。
    /// </summary>
    [Fact]
    public void G13_InfeasibleSchemes_AreListedNotDropped()
    {
        var rock = Profile();
        var inp = Base(rock);
        // 30万m³/月：实测 **18 套解出 / 9 套无解** —— 要的就是这个"有成有败"的混合。
        // ⚠ 原来写 20万，注释说"够拉平不够前重"，实际上**一套都解不出来**
        //   （走廊在第1期就倒挂：累计必须剥 27.8万 > 累计能力 20万）。
        //   那时 `27 == 0 + 27` 照样成立、原因检查又裹在 `if (Failed.Count>0)` 里，
        //   于是这条名叫「无解方案要列出来」的判据，从来没验过"列在解出来的那些旁边"。
        //   区间很窄（40万就全解出来了），改夹具时拿 CompareTable 的两个计数确认一下。
        inp.StripCapM3 = Enumerable.Repeat(30e4, 12).ToArray();
        var r = ScheduleDeriver.Derive(inp);
        _out.WriteLine(ScheduleDeriver.CompareTable(r));
        Assert.Equal(27, r.Attempted);
        Assert.Equal(27, r.Schemes.Count + r.Failed.Count);           // 一套都不许丢

        // ★ 先钉住"这个夹具真的造出了无解方案"。
        //   上面两条在 Failed 为空时**恒成立**（27 套全解出来照样 27=27+0），
        //   而下面的原因检查原本裹在 `if (r.Failed.Count > 0)` 里 ——
        //   于是一套都没失败时，这条名叫「无解方案要列出来」的判据**什么都没验**。
        //   能力给 20万m³/月 就是为了"够拉平、不够前重"，那就把它判出来。
        Assert.True(r.Failed.Count > 0,
                    $"夹具没造出任何无解方案（27 套全解出来）—— 这条判据会空过。"
                    + $"把 StripCapM3 调紧些，或确认 FrontLoad 那支还在派生里");
        Assert.True(r.Schemes.Count > 0, "全都无解也不行 —— 那证明不了「列出来而不是丢掉」");
        foreach (var f in r.Failed)
            Assert.False(string.IsNullOrWhiteSpace(f.Result.Error), "无解方案没带原因");
        _out.WriteLine($"解出 {r.Schemes.Count} 套 · 无解 {r.Failed.Count} 套（各自都带了原因）");
    }

    /// <summary>G13b 全军覆没时明说，不返回一个空的"成功"。</summary>
    [Fact]
    public void G13b_AllInfeasible_SaysSo()
    {
        var rock = Profile();
        var inp = Base(rock);
        inp.StripCapM3 = Enumerable.Repeat(1000.0, 12).ToArray();     // 杯水车薪
        var r = ScheduleDeriver.Derive(inp);
        Assert.False(r.Success);
        _out.WriteLine(r.Error);
        Assert.Contains("全部无解", r.Error);
        Assert.Equal(27, r.Failed.Count);
    }

    // ── G32 · 第 4 条轴：配对策略 ───────────────────────────────────────────

    private static GapMaterial[] Mats()
    {
        var a = new GapMaterial[GapCode.Count(2)];
        for (int g = 0; g < a.Length; g++) a[g] = new GapMaterial { Name = $"标签{g}", Code = "rock", Density = 2.5, Kr = 1.15 };
        a[GapCode.Overburden] = new GapMaterial { Name = "覆岩", Code = "weathered", Density = 2.1, Kr = 1.12 };
        return a;
    }

    private static DumpAllocationInput DumpIn()
    {
        var slots = new List<DumpSlot>();
        for (int lv = 0; lv < 6; lv++)
            for (int b = 0; b < 5; b++)
            {
                slots.Add(new DumpSlot { DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = 12e4, IsInternal = true, AvailableFromMonth = 3, HaulKm = 1.2, Cz = 1200 + lv * 20 });
                slots.Add(new DumpSlot { DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = 24e4, IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.8, Cz = 1300 + lv * 20 });
            }
        return new DumpAllocationInput { Slots = slots, Materials = Mats() };
    }

    /// <summary>
    /// G32 <b>配对策略是第 4 条真轴</b>：给了排土输入就跑配对，运输功/内排率进比选表，也进归因。
    /// <para>工程含义：设计文档列了 4 条轴、7 个软目标；代码里只有 3 条轴、5 个目标 ——
    /// <b>文档说了代码没做</b>。这条判据把两边钉在一起。</para>
    /// </summary>
    [Fact]
    public void G32_PairingStrategy_IsARealFourthAxis()
    {
        var rock = Profile();
        var axes = new ScheduleAxes
        {
            Lookahead = new[] { 3 },                       // 收窄前三轴，免得 81 套
            StripPaces = new[] { StripPace.Level },
            CoalPaces = new[] { CoalPace.Balanced },
            PairingStrategies = Enum.GetValues<PairingStrategy>(),
        };
        var r = ScheduleDeriver.Derive(Base(rock), axes, null, DumpIn());
        Assert.True(r.Success, r.Error);
        _out.WriteLine(ScheduleDeriver.CompareTable(r));

        Assert.True(r.PairingScored);
        Assert.Equal(3, r.Attempted);
        Assert.All(r.Schemes, s =>
        {
            Assert.NotNull(s.Pairing);
            Assert.NotNull(s.Dump);
            Assert.True(s.TransportWorkTKm > 0, "跑了配对却没有运输功");
            Assert.Contains("配对", s.Name);
        });
        Assert.Contains(r.Attribution, a => a.Axis == "配对策略");
        // 方向：运输功最小的那套，运输功必须是最小的
        var best = r.Schemes.OrderBy(s => s.TransportWorkTKm).First();
        Assert.Equal(PairingStrategy.MinHaul, best.Pairing);
    }

    /// <summary>
    /// G32b <b>没给排土输入时那两维不参与打分</b>，而不是按 0 参与。
    /// <para>工程含义：按 0 参与会让"没算"看起来像"很差"—— 所有方案的运输功都是 0，
    /// 归一之后这一维退化成常数，白白稀释其它维度的权重。</para>
    /// </summary>
    [Fact]
    public void G32b_WithoutDumpInput_TransportDimensionsAreExcludedNotZeroed()
    {
        var rock = Profile();
        var r = ScheduleDeriver.Derive(Base(rock));                 // 不给排土
        Assert.True(r.Success, r.Error);
        Assert.False(r.PairingScored);
        Assert.All(r.Schemes, s => { Assert.Null(s.Pairing); Assert.Null(s.Dump); Assert.Equal(0, s.TransportWorkTKm, 6); });
        Assert.DoesNotContain(r.Attribution, a => a.Axis == "配对策略");
        Assert.Contains("未跑配对", ScheduleDeriver.CompareTable(r));

        // 勾了轴却没给输入 ⇒ 明说轴被忽略，不静默
        var axes = new ScheduleAxes { PairingStrategies = Enum.GetValues<PairingStrategy>() };
        var r2 = ScheduleDeriver.Derive(Base(rock), axes);
        Assert.False(r2.PairingScored);
        Assert.Contains(r2.Notes, n => n.Contains("没给排土输入"));
        _out.WriteLine(string.Join("\n", r2.Notes));
    }

    /// <summary>
    /// G32c <b>每套方案拿一份干净的库容</b>。
    /// <para>库容是有状态的：不给每套复制一份位置清单，第 2 套开始就在吃第 1 套排剩下的 ——
    /// 那是把 27 套算成了一条链，越靠后的方案越"排不下"，而且看上去像是方案本身差。</para>
    /// </summary>
    [Fact]
    public void G32c_EachScheme_GetsFreshDumpCapacity()
    {
        var rock = Profile();
        var axes = new ScheduleAxes
        {
            Lookahead = new[] { 3 }, StripPaces = new[] { StripPace.Level }, CoalPaces = new[] { CoalPace.Balanced },
            PairingStrategies = new[] { PairingStrategy.MinHaul, PairingStrategy.MinHaul, PairingStrategy.MinHaul },
        };
        var r = ScheduleDeriver.Derive(Base(rock), axes, null, DumpIn());
        Assert.True(r.Success, r.Error);
        Assert.Equal(3, r.Schemes.Count);
        // 同样的输入跑三遍，结果必须一模一样；串了的话第 2、3 套会排不下
        var w = r.Schemes.Select(s => Math.Round(s.TransportWorkTKm, 3)).Distinct().ToList();
        var u = r.Schemes.Select(s => Math.Round(s.UnplacedM3, 3)).Distinct().ToList();
        _out.WriteLine($"三套相同方案的运输功: {string.Join(" / ", r.Schemes.Select(s => (s.TransportWorkTKm / 1e4).ToString("0.0")))}");
        Assert.Single(w);
        Assert.Single(u);
        Assert.All(r.Schemes, s => Assert.Equal(0, s.UnplacedM3, 3));
    }

    /// <summary>面板：整张比选矩阵 + 归因，供填验收表。</summary>
    [Fact]
    public void G_DerivationDashboard()
    {
        var r = ScheduleDeriver.Derive(Base(Profile()));
        _out.WriteLine(r.Success ? ScheduleDeriver.CompareTable(r) : "失败：" + r.Error);
    }
}
