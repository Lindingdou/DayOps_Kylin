// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/UnitPlanFaceAdvanceTests.cs（逐行对应；仅命名空间适配）
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
/// UF′ 组 · <b>U1′ 按作业面粘着推进</b>（2026-08-18 现场令）。
///
/// <para>现场原话两条：「尽量整条带来控制采出」「尽量不要频繁调度设备，同时就近调度，
/// 采剥顺序要考虑衔接」。改的是<b>选煤的单位</b> —— 从"全局逐幅挑"变成"逐面推进"，
/// 一个作业面 = 一条带 = <c>(SeamCode, BandId)</c>。</para>
///
/// <para><b>为什么每条都要单独判</b>：这四条性质（面内连续 · 不跳带 · 锋面幅上限 · 煤压煤）
/// 在旧实现里有三条是<b>碰巧</b>成立的 —— 全局排序按 SeamCode/BandId 兜底，
/// 于是"看上去像"按面推进。碰巧成立的东西换一条轴就不成立，所以必须构造性地判。</para>
/// </summary>
public sealed class UnitPlanFaceAdvanceTests
{
    private readonly ITestOutputHelper _out;
    public UnitPlanFaceAdvanceTests(ITestOutputHelper o) => _out = o;

    private static (string Seam, int Band) FaceOf(UnitAssignment a) => (a.SeamCode, a.BandId);

    /// <summary>本月采到的煤幅，按面分组。</summary>
    private static Dictionary<(string, int), List<UnitAssignment>> CoalByFace(UnitPlanResult r)
        => r.Coal.GroupBy(FaceOf).ToDictionary(gr => gr.Key, gr => gr.ToList());

    [Fact(DisplayName = "UF′0 开面数要最少：同样的幅数，不许摊到更多的面上去")]
    public void UFp0_OpensAsFewFacesAsPossible()
    {
        // ⚠ 这条是这一组里【唯一能抓住旧实现】的：其余四条旧实现也全绿，
        //   因为在这个算例上旧的"全局按长带优先排序"恰好让每个面只贡献一幅
        //   —— 一幅一个面时"面内连续""不跳带"都平凡成立。
        //   真正的差别在【摊了几个面】：旧的是横扫（P1 取遍所有面），新的是推完一个面再开下一个。
        const int panelsPerFace = 4;
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 2, panels: panelsPerFace);
        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), 30e4);
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        int picked = r.Coal.Count();
        int faces = CoalByFace(r).Count;
        int least = (int)Math.Ceiling(picked / (double)panelsPerFace);
        _out.WriteLine($"选中 {picked} 幅 · 摊在 {faces} 个面上 · 装满算下限 {least} 个面");
        foreach (var (f, list) in CoalByFace(r))
            _out.WriteLine($"　{f.Item1}-B{f.Item2}: {string.Join(",", list.Select(a => a.PanelIndex).OrderBy(x => x))}");

        // 摊得越开，设备要挪的次数越多、每个面月末都留一个半截的锋面 —— 正是现场说的"频繁调度"。
        Assert.True(faces <= least,
            $"{picked} 幅摊在 {faces} 个作业面上（装满只要 {least} 个）—— 这是横扫，不是按面推进");
    }

    [Fact(DisplayName = "UF′1 面内沿走向连续：一个面里采到的幅号不许有缺口")]
    public void UFp1_PanelsWithinAFaceAreContiguous()
    {
        // 平摊算例：压覆边 0 ⇒ 谁都能采，选谁完全由 U1′ 决定（没有闭包在旁边帮忙排序）
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 2, panels: 4);
        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), 30e4);
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        foreach (var (face, list) in CoalByFace(r))
        {
            var idx = list.Select(a => a.PanelIndex).OrderBy(x => x).ToList();
            _out.WriteLine($"{face}: 幅 {string.Join(",", idx)}");
            // 连续 = 最大 − 最小 + 1 == 个数。跳着采的话现场要在一条带里来回挪，
            // 而且月末的推进面是锯齿状的，下个月接不上（"接续"这条）。
            Assert.Equal(idx.Count, idx[^1] - idx[0] + 1);
        }
    }

    [Fact(DisplayName = "UF′2 不跳带：一个面开了就推到底，中途不会插进别的面")]
    public void UFp2_FacesAreNotInterleaved()
    {
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 2, panels: 4);
        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), 30e4);
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        // 按【执行顺序】看：同一个面的幅必须挨在一起（出现过的面不许再出现第二段）
        var seq = r.Coal.OrderBy(a => a.Seq).Select(FaceOf).ToList();
        var seen = new HashSet<(string, int)>();
        var segments = new List<(string, int)>();
        foreach (var f in seq)
            if (segments.Count == 0 || segments[^1] != f) segments.Add(f);
        _out.WriteLine("面的出场序：" + string.Join(" → ", segments.Select(s => $"{s.Item1}-B{s.Item2}")));
        foreach (var f in segments)
        {
            Assert.True(seen.Add(f), $"面 {f} 在执行序里出现了两段 —— 中间跑去别的面又回来 = 一次白挨的转场");
        }
    }

    [Fact(DisplayName = "UF′3 锋面幅上限：跨月幅不超过本月开过的面数")]
    public void UFp3_PartialsBoundedByOpenFaces()
    {
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 2, panels: 4);
        // 目标故意落在两个整幅之间 ⇒ 必然要切一刀
        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), 23.7e4);
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        int faces = CoalByFace(r).Count;
        int partials = r.Coal.Count(a => a.DoneAfter < 1 - 1e-6);
        _out.WriteLine($"开面 {faces} 个 · 锋面幅 {partials} 个");
        // 旧口径是"全局最多 1 个"——那是"全局凑量 + 末位切一刀"的副产品，不是现场口径。
        // 现在钉的是构造性的上限：一个面最多在它的锋面上切一刀。
        Assert.True(partials <= faces, $"锋面幅 {partials} 个 > 开面 {faces} 个 —— 有面被切了不止一刀");
        Assert.True(partials >= 1, "目标卡在两个整幅之间却一刀没切 —— 煤量不可能凑准");
    }

    [Fact(DisplayName = "UF′4 煤压煤是硬约束：上覆煤层没采完，下伏那一幅不许采")]
    public void UFp4_CoalOverCoalIsHard()
    {
        // 同一 XY 上下两层煤（中间没有岩）：上层不采完，下层就够不着。
        // ⚠ 这条【不在必剥闭包那条路上】—— mandFrac 那个循环跳过煤前驱，只管岩。
        var units = new List<MineUnit>();
        for (int p = 1; p <= 3; p++)
        {
            double x0 = 1000 + (p - 1) * UnitPlanFixture.PitchX, y = 700;
            units.Add(UnitPlanFixture.Unit($"煤上-B1-P{p}", UnitKind.Coal, "煤上", 1, p, 3, x0, y, 1020, 1028, 5e4));
            units.Add(UnitPlanFixture.Unit($"煤下-B1-P{p}", UnitKind.Coal, "煤下", 1, p, 3, x0, y, 1000, 1008, 5e4));
        }
        var g = UnitPlanFixture.Graph(units);
        Assert.True(g.Units.Count == 6);

        // 目标只够采 2 幅 ⇒ 引擎必须挑上层的，不能去动下层
        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 1, 3e5, 5000, 5000, 1000),
                                        2 * 5e4 * UnitPlanFixture.CoalRho);
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        var done = r.Coal.ToDictionary(a => a.UnitId, a => a.DoneAfter, StringComparer.Ordinal);
        foreach (var a in r.Coal) _out.WriteLine($"{a.UnitId} → {a.DoneAfter * 100:0.#}%");
        for (int p = 1; p <= 3; p++)
        {
            double low = done.TryGetValue($"煤下-B1-P{p}", out double dl) ? dl : 0;
            double up = done.TryGetValue($"煤上-B1-P{p}", out double du) ? du : 0;
            Assert.True(up + 1e-9 >= low,
                $"P{p}：下层采到 {low * 100:0.#}% 而上层只有 {up * 100:0.#}% —— 上覆煤没采就采了下面");
        }
    }

    // ══ U1″ 面级配额 ══════════════════════════════════════════════════════
    //   份额来自「确定开采程序」、归属来自 FaceUnitResolver，都由调用方给（引擎不猜）。
    //   这几条判的是"拆了之后各面各按各的推"，而不是"拆得准不准"——
    //   份额本身是人填的，引擎只负责不把它悄悄改掉。

    /// <summary>按煤层给份额（这个算例里一层 = 一个面）。</summary>
    private static List<FaceQuota> Quotas(List<MineUnit> units, params (string Seam, string Face, double Share)[] spec)
        => spec.Select(s => new FaceQuota
        {
            FaceId = s.Face,
            SharePct = s.Share,
            UnitIds = units.Where(u => u.SeamCode == s.Seam).Select(u => u.UnitId).ToList(),
        }).ToList();

    [Fact(DisplayName = "UF″1 给了份额，每个在采面各留一个锋面幅（不再是全场只切一刀）")]
    public void UFq1_EachFaceKeepsItsOwnFrontier()
    {
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 1, panels: 4);
        var slots = UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000);
        const double target = 40e4;

        // 对照组：不给份额 —— 全局一个目标，锋面幅最多 1 个
        var plain = UnitPlanEngine.Solve(UnitPlanFixture.Input(units, slots, target));
        Assert.True(plain.Success, plain.Error);
        int plainPartials = plain.Coal.Count(a => a.DoneAfter < 1 - 1e-6);
        _out.WriteLine($"不给份额：锋面幅 {plainPartials} 个 · 开面 {plain.Coal.Select(FaceOf).Distinct().Count()} 个");
        Assert.True(plainPartials <= 1, "全局一个目标时不该出现第二个锋面幅");

        // 实验组：50/30/20
        var units2 = UnitPlanFixture.FlatFaces(seams: 3, bands: 1, panels: 4);
        var inp = UnitPlanFixture.Input(units2, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), target);
        inp.FaceQuotas = Quotas(units2, ("煤0", "东帮", 50), ("煤1", "西帮", 30), ("煤2", "南帮", 20));
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        var byFace = CoalByFace(r);
        foreach (var (f, list) in byFace)
            _out.WriteLine($"{f.Item1}: {list.Sum(a => a.TonnageT) / 1e4:0.00}万t · "
                         + string.Join("/", list.OrderBy(a => a.PanelIndex).Select(a => $"P{a.PanelIndex}={a.DoneAfter * 100:0}%")));

        // 三个面都得开（份额不为 0 的面都该干活），且总量仍在容差带里
        Assert.Equal(3, byFace.Count);
        Assert.InRange(r.CoalT, target * 0.995, target * 1.02);
        // 关键：锋面幅多于一个 —— 这正是"每月最多切一幅"那条限制被去掉的样子
        int partials = r.Coal.Count(a => a.DoneAfter < 1 - 1e-6);
        _out.WriteLine($"给份额：锋面幅 {partials} 个");
        Assert.True(partials >= 2, $"给了三个面的份额，锋面幅却只有 {partials} 个 —— 份额没起作用");
        // 每个面最多一个（面内仍是"推到锋面为止"）
        foreach (var (f, list) in byFace)
            Assert.True(list.Count(a => a.DoneAfter < 1 - 1e-6) <= 1, $"面 {f} 被切了不止一刀");
    }

    [Fact(DisplayName = "UF″2 份额按【有煤可采的面】归一：没料的面不参与，量不许凭空少")]
    public void UFq2_SharesNormalizedOverLiveFacesOnly()
    {
        // 煤2 一幅都不剩（全采完）⇒ 它的 20% 份额必须重新分给另外两个面，而不是把这 20% 的煤丢掉
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 1, panels: 4);
        foreach (var u in units.Where(u => u.SeamCode == "煤2")) u.DoneFraction = 1.0;

        const double target = 30e4;
        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), target);
        inp.FaceQuotas = Quotas(units, ("煤0", "东帮", 50), ("煤1", "西帮", 30), ("煤2", "南帮", 20));
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        _out.WriteLine($"煤量 {r.CoalT / 1e4:0.00}万t（目标 {target / 1e4:0.00}）");
        foreach (var n in r.Notes.Where(n => n.Contains("归一") || n.Contains("份额"))) _out.WriteLine("  " + n);
        Assert.InRange(r.CoalT, target * 0.995, target * 1.02);
        Assert.DoesNotContain(r.Coal, a => a.SeamCode == "煤2");
        // 归一这件事必须说出来 —— 不说的话，"份额 50%" 的面实际拿到 62.5% 没人解释得了
        Assert.Contains(r.Notes, n => n.Contains("归一"));
    }

    [Fact(DisplayName = "UF″3 某个面料不够：从别的面补齐，并且说清补的量不算它的份额")]
    public void UFq3_ShortFaceIsBackfilledAndSaidSo()
    {
        // 东帮只剩 1 幅（6.75 万t），份额却说它该出 20 万t
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 1, panels: 4)
                                   .Where(u => u.SeamCode != "煤0" || u.PanelIndex == 1).ToList();
        const double target = 40e4;
        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), target);
        inp.FaceQuotas = Quotas(units, ("煤0", "东帮", 50), ("煤1", "西帮", 30), ("煤2", "南帮", 20));
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        double east = r.Coal.Where(a => a.SeamCode == "煤0").Sum(a => a.TonnageT);
        _out.WriteLine($"东帮实取 {east / 1e4:0.00}万t（份额说 20.00）· 总量 {r.CoalT / 1e4:0.00}万t");
        Assert.True(east < 20e4 - 1e3, "东帮只有一幅，不可能达成份额");
        Assert.InRange(r.CoalT, target * 0.995, target * 1.02);          // 总量仍要达标
        // 补量必须留条：不说的话，"东帮欠了 13 万t 由西帮补上"这件事下月对账时无从解释
        Assert.Contains(r.Notes, n => n.Contains("补到") || n.Contains("料不够"));
    }

    [Fact(DisplayName = "UF″4 没归属的单元：份额够用时一点都不许动")]
    public void UFq4_UnattributedUnitsAreNotSilentlyUsed()
    {
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 1, panels: 4);
        const double target = 40e4;
        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), target);
        // 只给两个面的归属，煤2 整层没归属（FA5/FA6 那两类）
        inp.FaceQuotas = Quotas(units, ("煤0", "东帮", 60), ("煤1", "西帮", 40));
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        _out.WriteLine($"煤量 {r.CoalT / 1e4:0.00}万t · 用到的面 {string.Join("/", CoalByFace(r).Keys.Select(k => k.Item1))}");
        // 自动摊给某个面 = 替用户做了归属决定，那是 FA 组明令禁止的
        Assert.DoesNotContain(r.Coal, a => a.SeamCode == "煤2");
        Assert.Contains(r.Notes, n => n.Contains("没有归属"));
    }

    [Fact(DisplayName = "UF″5 标段：一条带切 N 段，段内仍沿台阶线连续，每段各留一个锋面幅")]
    public void UFq5_SegmentsAdvanceInParallelButStayContiguous()
    {
        // 现场口径：「可以把工作线分成两三个标段，但是还是要沿着台阶线采掘块体的，
        //           得考虑作业的规整性和作业设备的作业方便」。
        const double target = 40e4;
        List<FaceQuota> Q(List<MineUnit> u) => Quotas(u, ("煤0", "东帮", 50), ("煤1", "西帮", 30), ("煤2", "南帮", 20));

        // A：不切段 —— 每个面推到量满为止，一个面最多一个锋面幅
        var uA = UnitPlanFixture.FlatFaces(seams: 3, bands: 1, panels: 4);
        var inpA = UnitPlanFixture.Input(uA, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), target);
        inpA.FaceQuotas = Q(uA);
        var rA = UnitPlanEngine.Solve(inpA);
        Assert.True(rA.Success, rA.Error);
        int pa = rA.Coal.Count(a => a.DoneAfter < 1 - 1e-6);

        // B：每条带切 2 段 —— 两台设备各推各的，月末各留一个半截幅
        var uB = UnitPlanFixture.FlatFaces(seams: 3, bands: 1, panels: 4);
        var inpB = UnitPlanFixture.Input(uB, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), target);
        inpB.FaceQuotas = Q(uB);
        inpB.FaceSegments = 2;
        var rB = UnitPlanEngine.Solve(inpB);
        Assert.True(rB.Success, rB.Error);
        int pb = rB.Coal.Count(a => a.DoneAfter < 1 - 1e-6);

        foreach (var (f, list) in CoalByFace(rB))
            _out.WriteLine($"{f.Item1}: " + string.Join("/", list.OrderBy(a => a.PanelIndex)
                                                             .Select(a => $"P{a.PanelIndex}={a.DoneAfter * 100:0}%")));
        _out.WriteLine($"不切段 锋面幅 {pa} 个 · 切 2 段 锋面幅 {pb} 个");

        // ① 切了段就该有更多锋面 —— 否则"分标段"没起作用（几台设备还是排成一队跟着一台干）
        Assert.True(pb > pa, $"切了 2 段，锋面幅还是 {pb} 个（不切时 {pa} 个）—— 段没有各自领量");
        // ② 但不能超过 面数×段数
        int faces = CoalByFace(rB).Count;
        Assert.True(pb <= faces * 2, $"锋面幅 {pb} 个 > 面数 {faces} × 2 段 —— 有段被切了不止一刀");
        // ③ 规整性：连续性要**按段判**，不是按面判。
        //    切成 2 段之后，一个面里采到的幅是 {P1, P3} 这样 —— 中间的 P2 不是"缺口"，
        //    而是第一段这个月没推到的余量；两段各自都是连续的。
        //    所以判的是：一个面里的【连续段数 ≤ 标段数】。段内跳幅才是真的犬牙交错。
        foreach (var (f, list) in CoalByFace(rB))
        {
            var idx = list.Select(a => a.PanelIndex).OrderBy(x => x).ToList();
            int runs = 1;
            for (int i = 1; i < idx.Count; i++) if (idx[i] != idx[i - 1] + 1) runs++;
            Assert.True(runs <= 2, $"面 {f} 采到的幅断成 {runs} 段（只切了 2 个标段）—— 段内跳幅了");
        }
        // ④ 总量照旧要准
        Assert.InRange(rB.CoalT, target * 0.995, target * 1.02);
    }

    [Fact(DisplayName = "UF′6 排土也粘着：一个卸点填满再换，不许走了又回来")]
    public void UFp6_DumpSlotsAreFilledOneAtATime()
    {
        // 排土场上作业的是推土机，它跟着卸点走。逐笔按"运输功最小"挑的话，
        // 相邻两笔岩的质心差几十米就可能挑到不同位置 —— 卡车省下的那点运距，
        // 换成推土机来回搬，而报表上"卸点 N 个"看着完全正常。
        // ⚠ 算例要摆成"不粘着就一定会来回跳"，否则这条判据是空的（第一版就是：
        //   卸点挤在一起 + 默认排弃顺序，不粘着也刚好顺序填，关掉规则照样绿）。
        //   摆法：卸点【沿 x 铺开】+ 排弃顺序按【离源最近】 ⇒ 每笔各挑各的最近点；
        //   而岩是分两个压覆深度层的，第二层又从 x 小的那头重新开始 ⇒ 不粘着就会绕回已经用过的卸点。
        var units = UnitPlanFixture.Stacked(bands: 2, panels: 4);
        var slots = new List<DumpSlot>();
        for (int k = 0; k < 4; k++)
            slots.Add(new DumpSlot
            {
                DumpName = "外排1", Level = 0, Order = k * 100,
                CapacityM3 = 4e5,                                  // 够大：填不满，换点只可能是"挑的时候换的"
                Cx = 1000 + k * 300, Cy = 3000, Cz = 1000, AvailableFromMonth = 1,
            });
        var inp = UnitPlanFixture.Input(units, slots, 20e4, stripTargetM3: 90e4);
        inp.DumpOrder = DumpOrder.NearestToSource;
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        // 按执行序把所有排土流摊平，看每个卸点是不是只占一段
        var seq = r.Rock.OrderBy(a => a.Seq)
                        .SelectMany(a => a.Flows.Where(f => !f.IsCoalSink).Select(f => f.DestinationCode))
                        .ToList();
        var runs = new List<string>();
        foreach (var d in seq) if (runs.Count == 0 || runs[^1] != d) runs.Add(d);
        _out.WriteLine($"卸点序（{seq.Count} 笔 / 换点 {Math.Max(0, runs.Count - 1)} 次）：{string.Join(" → ", runs)}");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in runs)
            Assert.True(seen.Add(d), $"卸点 {d} 走了又回来 —— 推土机白挨一次搬家（换点 {runs.Count - 1} 次，卸点只有 {seen.Count} 个）");
    }

    [Fact(DisplayName = "UF′5 遗留面优先：上月在采的那个面本月先推完，再开新面")]
    public void UFp5_CarryOverFaceGoesFirst()
    {
        // 3 层 × 2 带，把【最后一个面】的第一幅设成遗留幅 —— 按任何"优先级"排它都不会排在前面，
        // 所以它要是真跑到最前，只能是"遗留优先"这条在起作用。
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 2, panels: 4);
        var target = units.Where(u => u.SeamCode == "煤2" && u.BandId == 2).OrderBy(u => u.PanelIndex).First();
        target.DoneFraction = 0.4;

        var inp = UnitPlanFixture.Input(units, UnitPlanFixture.Dump("外排1", 2, 3e5, 5000, 5000, 1000), 20e4);
        var r = UnitPlanEngine.Solve(inp);
        Assert.True(r.Success, r.Error);

        var first = r.Coal.OrderBy(a => a.Seq).First();
        _out.WriteLine("执行序第一个：" + first.UnitId);
        Assert.Equal(target.UnitId, first.UnitId);
        // 且它本月要采完 —— 遗留幅挂着不动正是这条规则要防的
        Assert.True(r.Coal.Single(a => a.UnitId == target.UnitId).DoneAfter > 1 - 1e-6,
            "遗留幅本月没采完 —— 一个幅挂三个月，而每一步看上去都合理");
    }
}
