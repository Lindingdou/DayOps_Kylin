// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/UnitPlanEngineTests.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
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
/// G30 组 · <b>UnitPlanEngine 的判据</b>（此前一条都没有）。
///
/// <para>分五层，从"算例本身对不对"一直判到"判据本身会不会空过"：
/// <list type="number">
/// <item><b>算例自检</b> —— 压覆图必须是我摆出来的那个样子。图错了，下面全部判断都没有意义。</item>
/// <item><b>三条对账</b> —— 煤量 vs 月目标（相对差）· 占容 ≤ 库容 · 拓扑自洽。</item>
/// <item><b>方向判据</b> —— 煤量扫描的单调性；<b>不判具体数字</b>，换地质数字全变、方向不变。</item>
/// <item><b>规则判据</b> —— U1 跨月幅 / U7 纯函数 / U11 转场 / U12 排弃顺序。</item>
/// <item><b>变异测试</b> —— 故意把结果改坏，判据必须报红。只判"成功"的判据会空过。</item>
/// </list></para>
/// </summary>
public sealed class UnitPlanEngineTests
{
    private readonly ITestOutputHelper _out;
    public UnitPlanEngineTests(ITestOutputHelper o) => _out = o;

    private void Dump(string title, List<string> bad)
    {
        _out.WriteLine($"── {title}：{(bad.Count == 0 ? "无异议" : bad.Count + " 条异议")}");
        foreach (var b in bad) _out.WriteLine("   ◆ " + b);
    }

    // ════════════════════════════════════════════════════════════════════
    //  ① 算例自检 —— 判据的地基
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 【叠柱算例的压覆图必须是柱内三层链】
    ///
    /// 摆算例的时候我假定：柱内 岩1030 压 岩1010 压 煤2（外加 岩1030 直接压煤2 的那条），
    /// 柱与柱之间没有任何边。<b>下面每一条判据都建在这个假定上</b> —— 假定不成立的话，
    /// "必剥闭包应该是 2 个体"这种手算就全错，而判据照样能全绿。
    /// </summary>
    [Fact]
    public void 算例自检_叠柱压覆图只在柱内连边()
    {
        var units = UnitPlanFixture.Stacked(bands: 2, panels: 4);
        var g = UnitPlanFixture.Graph(units);
        foreach (var n in g.Notes) _out.WriteLine("图：" + n);

        var idx = Enumerable.Range(0, units.Count).ToDictionary(i => units[i].UnitId, i => i, StringComparer.Ordinal);
        var bad = new List<string>();
        for (int b = 1; b <= 2; b++)
            for (int p = 1; p <= 4; p++)
            {
                string coal = $"煤2-B{b}-P{p}", mid = $"岩1010-B{b}-P{p}", top = $"岩1030-B{b}-P{p}";
                var pc = g.Pred[idx[coal]].Select(i => units[i].UnitId).OrderBy(s => s, StringComparer.Ordinal).ToList();
                var pm = g.Pred[idx[mid]].Select(i => units[i].UnitId).ToList();
                var pt = g.Pred[idx[top]].Select(i => units[i].UnitId).ToList();
                if (!(pc.Count == 2 && pc.Contains(mid) && pc.Contains(top))) bad.Add($"{coal} 的前驱是 [{string.Join(",", pc)}]，应为 {mid} + {top}");
                if (!(pm.Count == 1 && pm[0] == top)) bad.Add($"{mid} 的前驱是 [{string.Join(",", pm)}]，应为 {top}");
                if (pt.Count != 0) bad.Add($"{top} 不该有前驱，实际 [{string.Join(",", pt)}]");
            }
        Dump("压覆图", bad);
        Assert.Empty(bad);
        Assert.Equal(8 * 3, g.EdgeCount);          // 每柱 3 条（top→mid, top→coal, mid→coal）× 8 柱
        g.TopoOrder(out int cyc);
        Assert.Equal(0, cyc);
    }

    // ════════════════════════════════════════════════════════════════════
    //  ② 三条对账
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 【三条对账 —— 基准算例】
    /// ① 入选煤量 vs 月目标（<b>比相对差</b>，取整是显示不是账）
    /// ② 每个去向的占容合计 ≤ 该位置库容，且总量 ≤ 总库容
    /// ③ 每个入选单元的前驱在本月或更早（累计完成度 + 月内推进序两级）
    /// 外加量的口径 / 组织指标 / 可行性诚实性。
    /// </summary>
    [Fact]
    public void 三条对账_基准算例()
    {
        var units = UnitPlanFixture.Stacked();
        var slots = UnitPlanFixture.Dump("外排1", levels: 3, capBase: 1.0e5, cx: 2500, cy: 700, baseZ: 1100);
        var inp = UnitPlanFixture.Input(units, slots, coalTargetT: 18e4, stripRatio: 5.0);
        inp.Graph = UnitPlanFixture.Graph(units);       // 引擎与判据共用同一张图，杜绝两处各建一份

        var r = UnitPlanEngine.Solve(inp);
        _out.WriteLine(r.Report());

        Assert.True(r.Success, r.Error);
        var bad = UnitPlanCriteria.All(r, inp, inp.Graph, slots);
        Dump("全部判据", bad);
        Assert.Empty(bad);

        // 引擎自己那份自检也跑一遍：两份账都要平（对不上再说谁错）
        var own = r.Validate();
        Dump("引擎自检 Validate", own);
        Assert.Empty(own);

        // 必剥闭包是手算得出来的：3 个柱被选中（其中 1 个只采一部分）
        _out.WriteLine($"必剥 {r.MandatoryStripM3 / 1e4:0.00}万m³ · 剥离 {r.StripM3 / 1e4:0.00}万m³ · 剥采比 {r.StripRatio:0.000}");
        Assert.True(r.StripM3 >= r.MandatoryStripM3 - 1e-6, "剥离量低于必剥闭包 —— 压在煤上的岩没剥够");
    }

    /// <summary>
    /// 【库容对账要在"排得下"和"排不下"两侧各判一次】
    /// 只在够用的算例上判 ≤ 库容会空过：不够用时超没超才是真问题。
    /// 这里把库容压到只够一半，判"多出来的量进了 UnplacedM3 而不是被塞进库容里"。
    /// </summary>
    [Fact]
    public void 对账二_库容不够时超出量必须进未落地而不是撑破库容()
    {
        var units = UnitPlanFixture.Stacked();
        var slots = UnitPlanFixture.Dump("外排1", levels: 1, capBase: 3.0e4, cx: 2500, cy: 700, baseZ: 1100);
        var inp = UnitPlanFixture.Input(units, slots, coalTargetT: 18e4, stripRatio: 5.0);
        inp.Graph = UnitPlanFixture.Graph(units);

        var r = UnitPlanEngine.Solve(inp);
        _out.WriteLine(r.Report());

        var bad = UnitPlanCriteria.All(r, inp, inp.Graph, slots);
        Dump("全部判据", bad);
        Assert.Empty(bad);

        double totalCap = slots.Sum(s => s.CapacityM3);
        double placed = r.Rock.SelectMany(a => a.Flows).Sum(f => f.DumpM3);
        _out.WriteLine($"总库容 {totalCap / 1e4:0.00}万m³占容 · 实排 {placed / 1e4:0.00} · 排不下 {r.UnplacedM3 / 1e4:0.00}万m³实方");
        Assert.True(placed <= totalCap + 1e-6, "占容撑破了库容");
        Assert.True(r.UnplacedM3 > 1e-6, "库容明显不够，却一方都没有【排不下】—— 缺口被吞了");
        Assert.False(r.Feasible);
    }

    // ════════════════════════════════════════════════════════════════════
    //  ③ 方向判据 —— 煤量扫描
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 【煤量从 0 扫到超上限，只判方向】
    ///
    /// 煤↑ ⇒ 采出煤量不减 · 必剥闭包不减 · 剥离量不减 · 缺口（目标 − 达成）不减。
    /// <b>一个具体数字都不判</b>：换一版地质数字全变，这四个方向不变。
    ///
    /// <para><b>为什么"缺口不减"要用 max(0, 目标−达成) 而不是直接用 ShortfallT</b>：
    /// ShortfallT 要是压根没被写进结果，它恒等于 0，"恒 0" 也是单调不减的 ——
    /// 判据就<b>空过</b>了。所以物理量与引擎自报量分开判，后者单列一条见
    /// <see cref="缺口必须落到结果上_目标超上限时()"/>。</para>
    /// </summary>
    [Theory]
    [InlineData(0.0)]      // 不给目标剥采比：只剥必剥闭包
    [InlineData(5.0)]      // 给目标剥采比：必剥 + 超前剥离
    public void 煤量扫描_只判方向不判数字(double stripRatio)
    {
        var units = UnitPlanFixture.Stacked();
        var slots = UnitPlanFixture.Dump("外排1", levels: 6, capBase: 3.0e5, cx: 2500, cy: 700, baseZ: 1100);
        var g = UnitPlanFixture.Graph(units);
        double total = UnitPlanFixture.TotalCoalT(units);

        double pc = -1, pm = -1, ps = -1, pgap = -1;
        int steps = 20;
        for (int k = 0; k <= steps; k++)
        {
            double target = total * 1.4 * k / steps;
            var inp = UnitPlanFixture.Input(units, slots, target, stripRatio);
            inp.Graph = g;
            var r = UnitPlanEngine.Solve(inp);
            Assert.True(r.Success, r.Error);

            var bad = UnitPlanCriteria.QuantitiesSound(r, inp);
            bad.AddRange(UnitPlanCriteria.DumpCapacityRespected(r, slots));
            bad.AddRange(UnitPlanCriteria.TopologySound(r, g));
            bad.AddRange(UnitPlanCriteria.AtMostOnePartialCoal(r));
            if (bad.Count > 0) { Dump($"目标 {target / 1e4:0.00}万t", bad); Assert.Empty(bad); }

            double gap = Math.Max(0, target - r.CoalT);
            _out.WriteLine($"目标 {target / 1e4,7:0.00}万t → 煤 {r.CoalT / 1e4,7:0.00} · 必剥 {r.MandatoryStripM3 / 1e4,8:0.00} "
                         + $"· 剥离 {r.StripM3 / 1e4,8:0.00}万m³ · 缺口 {gap / 1e4,6:0.00}万t · 单元 {r.Assignments.Count,3}");

            if (k > 0)
            {
                Assert.True(r.CoalT >= pc - 1e-6, $"煤量掉头：{pc:0.##} → {r.CoalT:0.##}");
                Assert.True(r.MandatoryStripM3 >= pm - 1e-6, $"必剥闭包掉头：{pm:0.##} → {r.MandatoryStripM3:0.##}");
                Assert.True(r.StripM3 >= ps - 1e-6,
                    $"剥离量掉头：{ps:0.##} → {r.StripM3:0.##}（煤多了剥离反而少）。"
                  + "机理见 超前剥离候选集漏掉了部分必剥的岩幅() —— 一个岩幅只要有一丝进了必剥闭包，"
                  + "它剩下的部分就整体退出超前剥离候选，于是新开一个柱的瞬间可剥总量往下掉一个整幅。");
                Assert.True(gap >= pgap - 1e-6, $"缺口掉头：{pgap:0.##} → {gap:0.##}");
            }
            pc = r.CoalT; pm = r.MandatoryStripM3; ps = r.StripM3; pgap = gap;
        }
        Assert.True(pgap > 1e-6, "扫到 1.4 倍可采总量还没出现缺口 —— 算例没扫过上限，这条判据是空的");
    }

    // ════════════════════════════════════════════════════════════════════
    //  ④ 规则判据
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 【U7 纯函数性】同输入两次跑<b>逐位相同</b>；改煤量再改回来，结果与第一次相同。
    ///
    /// <para>第二问才是真的那一问：增量更新会让"改回原值"得到与原来不同的结果，
    /// 而两次都自洽、谁也解释不了谁。顺带把 U6（库容是有状态的、必须取干净副本）也判了 ——
    /// 不复制的话第二次会在吃第一次排剩下的，第二次的"排不下"看上去像是煤量本身排不下。</para>
    /// </summary>
    [Fact]
    public void U7_纯函数性_两次逐位相同_改回原值也相同()
    {
        var units = UnitPlanFixture.Stacked(carryOver: 2);
        var slots = UnitPlanFixture.Dump("外排1", levels: 3, capBase: 1.0e5, cx: 2500, cy: 700, baseZ: 1100);
        var inp = UnitPlanFixture.Input(units, slots, coalTargetT: 22e4, stripRatio: 4.0);

        string a = UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(inp));
        string b = UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(inp));
        Assert.Equal(a, b);                                   // 同一个输入对象连跑两次（U6：库容副本）

        inp.CoalTargetT = 35e4; UnitPlanEngine.Solve(inp);
        inp.CoalTargetT = 8e4; UnitPlanEngine.Solve(inp);
        inp.CoalTargetT = 22e4;
        string c = UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(inp));
        Assert.Equal(a, c);                                   // 改煤量再改回来

        // 换一份全新的输入对象（同样的值）也必须一样 —— 排除"结果藏在输入对象里"
        var inp2 = UnitPlanFixture.Input(UnitPlanFixture.Stacked(carryOver: 2),
                                         UnitPlanFixture.Dump("外排1", 3, 1.0e5, 2500, 700, 1100), 22e4, 4.0);
        Assert.Equal(a, UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(inp2)));
        _out.WriteLine($"指纹长度 {a.Length} 字符，四次一致");
    }

    /// <summary>
    /// 【U1 每月最多一个跨月煤幅 · 遗留幅优先】
    ///
    /// 构造性成立：凑量是全局的，只需要切一幅就能对准目标。出现第二个说明选煤那一段错了。
    /// 遗留幅优先写成"Seq 最小"是错的判据 —— Seq 由定序链决定；
    /// 正确的可判性质是"只要排了新幅，所有没采完的遗留幅都必须在计划里"。
    /// </summary>
    [Fact]
    public void U1_每月最多一个跨月煤幅_且遗留幅优先()
    {
        var units = UnitPlanFixture.Stacked(carryOver: 3);
        var slots = UnitPlanFixture.Dump("外排1", levels: 4, capBase: 1.5e5, cx: 2500, cy: 700, baseZ: 1100);
        var g = UnitPlanFixture.Graph(units);

        // 扫一串目标，每个都判 —— 单个目标上判 U1 会空过（那一档正好整幅整除时永远绿）
        var partials = new List<int>();
        for (int k = 1; k <= 24; k++)
        {
            double target = 2e4 * k;
            var inp = UnitPlanFixture.Input(units, slots, target, 3.0);
            inp.Graph = g;
            var r = UnitPlanEngine.Solve(inp);
            Assert.True(r.Success, r.Error);

            var bad = UnitPlanCriteria.AtMostOnePartialCoal(r);
            bad.AddRange(UnitPlanCriteria.CarryOverFirst(r, units));
            if (bad.Count > 0) { Dump($"目标 {target / 1e4:0.0}万t", bad); Assert.Empty(bad); }
            partials.Add(r.Coal.Count(x => x.IsPartial));
        }
        _out.WriteLine("各档跨月煤幅个数：" + string.Join(",", partials));
        Assert.True(partials.Any(v => v == 1),
            "扫了 24 档一次都没切幅 —— 这条判据在这个算例上是空的（换个目标步长）");
        Assert.True(partials.All(v => v <= 1));
    }

    /// <summary>
    /// 【U11 转场代价 —— 同带各幅连续 · 跨层转场降到下界】
    ///
    /// 用<b>平摊算例</b>（压覆边 0 ⇒ 只有一个深度层）把定序链完全暴露出来。
    /// 判三件事：
    /// ① 同一（层,带）的各幅在推进序上<b>连成一段</b>，不许拆开；
    /// ② 跨层转场数 = <b>用到的层数 − 1</b>，即理论下界（每层只进一次）；
    /// ③ 与"纯按作业面优先级排"的基准比，转场数必须<b>明显下降</b>。
    ///
    /// <para>基准是<b>我自己按算例构造算的</b>（走向长是我编排的，纯优先级序就是逐幅横扫），
    /// 不是把引擎的代价函数抄一遍 —— 抄一遍的判据只能证明"它等于它自己"。</para>
    /// </summary>
    [Fact]
    public void U11_转场链_同带连续_跨层转场降到下界()
    {
        var units = UnitPlanFixture.FlatFaces(seams: 3, bands: 2, panels: 4);
        var slots = UnitPlanFixture.Dump("外排1", 2, 1e5, 2500, 700, 1100);
        double total = UnitPlanFixture.TotalCoalT(units);
        var inp = UnitPlanFixture.Input(units, slots, total);     // 全采：链覆盖所有单元
        inp.Graph = UnitPlanFixture.Graph(units);
        Assert.Equal(0, inp.Graph.EdgeCount);                     // 平摊算例必须没有压覆边

        var r = UnitPlanEngine.Solve(inp);
        _out.WriteLine(r.Report());
        Assert.Equal(units.Count, r.Assignments.Count);
        Assert.Empty(UnitPlanCriteria.All(r, inp, inp.Graph, slots));

        var order = r.Assignments.OrderBy(a => a.Seq).ToList();
        _out.WriteLine("推进序：" + string.Join(" ", order.Select(a => a.UnitId)));

        // ① 同（层,带）连续
        var bad = new List<string>();
        var seen = new HashSet<(string, int)>();
        (string, int) cur = ("", int.MinValue);
        foreach (var a in order)
        {
            var key = (a.SeamCode, a.BandId);
            if (key == cur) continue;
            if (!seen.Add(key)) bad.Add($"（{a.SeamCode},带{a.BandId}）被拆成了两段 —— 设备要来回挪");
            cur = key;
        }
        Dump("作业面连续性", bad);
        Assert.Empty(bad);

        // ② 跨层转场 = 层数 − 1（下界）
        int seams = order.Select(a => a.SeamCode).Distinct(StringComparer.Ordinal).Count();
        _out.WriteLine($"层数 {seams} · 作业面 {r.FaceCount} · 转场 {r.MoveCount} · 跨层 {r.CrossLevelMoves}");
        Assert.Equal(seams - 1, r.CrossLevelMoves);
        Assert.Equal(r.FaceCount - 1, r.MoveCount);               // 面内各幅相邻推进 ⇒ 转场只发生在换面时

        // ③ 与"纯按作业面优先级"的基准比
        var baseline = units.OrderByDescending(u => u.StrikeLenM)
                            .ThenBy(u => u.UnitId, StringComparer.Ordinal).ToList();
        int bMove = 0, bCross = 0;
        for (int i = 1; i < baseline.Count; i++)
        {
            var p = baseline[i - 1]; var q = baseline[i];
            bool same = string.Equals(p.SeamCode, q.SeamCode, StringComparison.Ordinal);
            if (!(same && p.BandId == q.BandId && Math.Abs(p.PanelIndex - q.PanelIndex) <= 1)) bMove++;
            if (!same) bCross++;
        }
        _out.WriteLine($"基准（纯优先级序）：转场 {bMove} · 跨层 {bCross}");
        Assert.True(bMove > 0 && bCross > 0, "基准序本身就没有转场 —— 算例没把两种序拉开，这条判据是空的");
        Assert.True(r.MoveCount < bMove, $"转场没降下来：链 {r.MoveCount} vs 基准 {bMove}");
        Assert.True(r.CrossLevelMoves < bCross, $"跨层转场没降下来：链 {r.CrossLevelMoves} vs 基准 {bCross}");
    }

    /// <summary>
    /// 【U11 的代价不许拿煤量去换】
    ///
    /// 作业组织那三条轴（作业面优先级 / 走向推进 / 排弃顺序）动的是<b>顺序</b>，
    /// 不许动"采多少"。逐个组合跑一遍：转场数可以差很多，
    /// <b>煤量偏差必须一条都不越容差带</b>。
    /// </summary>
    [Fact]
    public void U11_组织轴不许拿煤量去换()
    {
        var units = UnitPlanFixture.Stacked();
        var slots = UnitPlanFixture.Dump("外排1", 4, 1.2e5, 2500, 700, 1100);
        var g = UnitPlanFixture.Graph(units);
        double target = 21.3e4;                                   // 故意不是整幅的整数倍

        var moves = new List<int>(); var devs = new List<double>();
        foreach (FacePriority fp in Enum.GetValues<FacePriority>())
            foreach (StrikeAdvance sa in Enum.GetValues<StrikeAdvance>())
                foreach (DumpOrder dor in Enum.GetValues<DumpOrder>())
                {
                    var inp = UnitPlanFixture.Input(units, slots, target, 4.0);
                    inp.Graph = g; inp.FacePriority = fp; inp.StrikeAdvance = sa; inp.DumpOrder = dor;
                    var r = UnitPlanEngine.Solve(inp);
                    Assert.True(r.Success, r.Error);

                    var bad = UnitPlanCriteria.CoalVsTarget(r, inp);
                    bad.AddRange(UnitPlanCriteria.QuantitiesSound(r, inp));
                    bad.AddRange(UnitPlanCriteria.DumpCapacityRespected(r, slots));
                    bad.AddRange(UnitPlanCriteria.DumpBottomUp(r, slots, inp.Month));
                    bad.AddRange(UnitPlanCriteria.TopologySound(r, g));
                    bad.AddRange(UnitPlanCriteria.OrgMetricsMatchOrder(r));
                    if (bad.Count > 0) { Dump($"{fp}/{sa}/{dor}", bad); Assert.Empty(bad); }

                    moves.Add(r.MoveCount); devs.Add(r.CoalDeviationPct);
                }
        _out.WriteLine($"36 个组合：转场 {moves.Min()}~{moves.Max()} · 煤量偏差 {devs.Min():+0.###;-0.###;0}%~{devs.Max():+0.###;-0.###;0}%");
        Assert.True(moves.Max() > moves.Min(), "36 个组合转场数完全一样 —— 组织轴是死的，这条判据是空的");
        Assert.All(devs, d => Assert.InRange(d, -0.5, 2.0));
    }

    /// <summary>
    /// 【U9 轴 2 单独是不是活的】走向推进三种取值必须给出<b>不同的月内推进序</b>。
    ///
    /// <para><b>为什么要单独钉</b>：上面那条 36 组合的判据只证明"这几条轴<b>合起来</b>会动"，
    /// 一条轴死了它照样绿（另外两条把差别造出来了）。而参数排查的口径是<b>逐个</b>参数都要有用 ——
    /// 界面上给了下拉、调了没反应，那就是个骗人的控件。</para>
    ///
    /// <para>算例要用<b>同一条带多幅</b>（走向推进只改带内各幅的先后），
    /// 并且把作业面优先级固定住 —— 否则分不清差别是哪条轴造的。</para>
    /// </summary>
    [Fact]
    public void U9轴2_走向推进单独必须改变月内顺序()
    {
        var units = UnitPlanFixture.Stacked(bands: 1, panels: 6);      // 一条带 6 幅：带内先后才有得排
        var slots = UnitPlanFixture.Dump("外排1", 4, 1.5e5, 2500, 700, 1100);
        var g = UnitPlanFixture.Graph(units);

        var seqs = new Dictionary<StrikeAdvance, string>();
        foreach (StrikeAdvance sa in Enum.GetValues<StrikeAdvance>())
        {
            var inp = UnitPlanFixture.Input(units, slots, 20e4, 3.0);
            inp.Graph = g;
            inp.FacePriority = FacePriority.LongestFirst;              // 固定另一条轴
            inp.StrikeAdvance = sa;
            var r = UnitPlanEngine.Solve(inp);
            Assert.True(r.Success, r.Error);
            var bad = UnitPlanCriteria.All(r, inp, g, slots);
            Assert.True(bad.Count == 0, $"{sa}：\n　" + string.Join("\n　", bad));

            // 只看煤那一侧的顺序：岩是按压覆闭包带出来的，混进来会掩盖差别
            seqs[sa] = string.Join(">", r.Coal.OrderBy(a => a.Seq).Select(a => a.UnitId));
            _out.WriteLine($"{sa,-10} {seqs[sa]}");
        }

        Assert.True(seqs.Values.Distinct().Count() > 1,
            "走向推进三种取值排出来的顺序【完全一样】—— 这条轴是死的，"
            + "而界面上还给了下拉（调了没反应的控件比没有更糟）。"
            + "可能的原因：定序那一步的转场链把 PanelRank 的差别吃掉了。");
    }

    /// <summary>
    /// 【U12 排弃顺序是一条活轴】不同取值必须给出<b>不同的排弃序列</b>；
    /// 而且无论取哪个值，<b>自下而上那条硬约束不动</b>。
    /// </summary>
    [Fact]
    public void U12_排弃顺序四种取值给出不同序列_硬约束不动()
    {
        var units = UnitPlanFixture.Stacked();
        var slots = UnitPlanFixture.Dump("外排1", levels: 3, capBase: 1.0e5, cx: 2500, cy: 700, baseZ: 1100);
        var g = UnitPlanFixture.Graph(units);

        var seqs = new Dictionary<DumpOrder, string>();
        foreach (DumpOrder d in Enum.GetValues<DumpOrder>())
        {
            var inp = UnitPlanFixture.Input(units, slots, 18e4, 5.0);
            inp.Graph = g; inp.DumpOrder = d;
            var r = UnitPlanEngine.Solve(inp);
            Assert.True(r.Success, r.Error);

            var bad = UnitPlanCriteria.DumpBottomUp(r, slots, inp.Month);
            bad.AddRange(UnitPlanCriteria.DumpCapacityRespected(r, slots));
            if (bad.Count > 0) { Dump(d.ToString(), bad); Assert.Empty(bad); }

            var s = UnitPlanCriteria.DumpSequence(r);
            seqs[d] = string.Join(" → ", s);
            _out.WriteLine($"{d,-16} 卸点 {r.DumpSlotCount} 个 · 序列 {string.Join(",", s.Select(c => c.Split('-').Last()))}");
        }
        int distinct = seqs.Values.Distinct(StringComparer.Ordinal).Count();
        _out.WriteLine($"4 种取值给出 {distinct} 种不同的排弃序列");

        // ★ 2026-08-18 现场令改口径：「排土是按一个方向、由不同车辆依次向另一个方向推进的」。
        //   ⇒ 级内的填充锋面**单向、不回头**（D-R1，硬）。这条轴因此只剩「从哪一端起步」，
        //     四个取值最多塌成【正向 / 反向】两种 —— 原来那句"四种给出四种序列"已经不成立。
        //   ⚠ 塌轴要判"塌得对"，不能只判"没塌"：正反两向必须真的不同（否则轴是死的），
        //     而"离源最近""余量最多"塌到其中一向是**正确的塌**（它们只决定起步端）。
        //   ⚠ 用【小库容】的算例判：库容大到一级只用得上一个位置时，正反两向序列相同，
        //     那时的"塌"是算例没自由度，不是轴坏了 —— 判据会空过。
        var small = UnitPlanFixture.Dump("外排1", levels: 3, capBase: 2.5e4, cx: 2500, cy: 700, baseZ: 1100);
        string SeqOf(DumpOrder d)
        {
            var inp = UnitPlanFixture.Input(units, small, 18e4, 5.0);
            inp.Graph = g; inp.DumpOrder = d;
            var rr = UnitPlanEngine.Solve(inp);
            Assert.True(rr.Success, rr.Error);
            Assert.Empty(UnitPlanCriteria.DumpBottomUp(rr, small, inp.Month));   // 自下而上仍是硬的
            return string.Join(" → ", UnitPlanCriteria.DumpSequence(rr));
        }
        string fwd = SeqOf(DumpOrder.StepThenPanel), rev = SeqOf(DumpOrder.MostRoom);
        _out.WriteLine("正向：" + fwd);
        _out.WriteLine("反向：" + rev);
        Assert.NotEqual(fwd, rev);      // 起步端这条轴必须真的活着

        // 单向不回头：同一个卸点的用量必须是连续的一段，不许走了又回来
        foreach (var seq in new[] { fwd, rev })
        {
            var runs = seq.Split(" → ", StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? prev = null;
            foreach (var c in runs)
            {
                if (c == prev) continue;
                Assert.True(seen.Add(c), $"卸点 {c} 走了又回来 —— 锋面掉头了（D-R1 单向推进）");
                prev = c;
            }
        }
    }

    /// <summary>
    /// 【只有一个排土场时"配对策略"塌轴，是<b>正确的塌</b>】
    ///
    /// PairingStrategy 挑的是「去哪个排土场」。只有一个场时三种策略必然给出<b>逐位相同</b>的答案 ——
    /// 这不是失败，是这条轴在这份输入上没有自由度。<b>判成失败会让人去"修"一个没坏的东西。</b>
    /// 同一条判据的另一半：<b>给两个场，它必须活过来</b>。只判前一半会空过
    /// （策略被写死成常量时，前一半照样绿）。
    /// </summary>
    [Fact]
    public void U12_单场时配对策略塌轴是正确的塌_两场时必须活过来()
    {
        var units = UnitPlanFixture.Stacked();
        var g = UnitPlanFixture.Graph(units);

        // ── 一个场：三种策略必须逐位相同 ──
        var one = UnitPlanFixture.Dump("外排1", 3, 1.0e5, 2500, 700, 1100);
        var fps = new List<string>();
        foreach (PairingStrategy st in Enum.GetValues<PairingStrategy>())
        {
            var inp = UnitPlanFixture.Input(units, one, 18e4, 5.0);
            inp.Graph = g; inp.Strategy = st;
            var r = UnitPlanEngine.Solve(inp);
            Assert.True(r.Success, r.Error);
            Assert.Empty(UnitPlanCriteria.DumpBottomUp(r, one, inp.Month));
            fps.Add(UnitPlanCriteria.Fingerprint(r));
        }
        Assert.Single(fps.Distinct(StringComparer.Ordinal));
        _out.WriteLine("单场：三种配对策略逐位相同 —— 塌轴，且是正确的塌");

        // ── 两个场（近而小的外排 + 远而大的内排）：策略必须分得开 ──
        var two = UnitPlanFixture.Dump("外排1", 2, 4.0e4, 2200, 700, 1100);
        two.AddRange(UnitPlanFixture.Dump("内排1", 4, 3.0e5, 9000, 700, 1000, isInternal: true));
        var fps2 = new Dictionary<PairingStrategy, string>();
        foreach (PairingStrategy st in Enum.GetValues<PairingStrategy>())
        {
            var inp = UnitPlanFixture.Input(units, two, 18e4, 5.0);
            inp.Graph = g; inp.Strategy = st;
            var r = UnitPlanEngine.Solve(inp);
            Assert.True(r.Success, r.Error);
            var bad = UnitPlanCriteria.DumpBottomUp(r, two, inp.Month);
            bad.AddRange(UnitPlanCriteria.DumpCapacityRespected(r, two));
            if (bad.Count > 0) { Dump(st.ToString(), bad); Assert.Empty(bad); }
            fps2[st] = string.Join(",", UnitPlanCriteria.DumpSequence(r).Select(c => c.Split('-')[0]).Distinct());
            _out.WriteLine($"两场 · {st,-14} 内排率 {r.InternalRatePct:0.0}% · 首个去向 {UnitPlanCriteria.DumpSequence(r).FirstOrDefault()}");
        }
        Assert.True(fps2.Values.Distinct(StringComparer.Ordinal).Count() >= 2,
            "两个排土场时三种配对策略仍然逐位相同 —— 这条轴真的死了（不是塌）");
    }

    // ════════════════════════════════════════════════════════════════════
    //  ⑤ 退化输入 —— 要么出计划要么给原因，不抛异常
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("空输入")]
    [InlineData("零单元")]
    [InlineData("零位置")]
    [InlineData("煤量0")]
    [InlineData("煤量为负")]
    [InlineData("目标超上限")]
    [InlineData("剥采比为负")]
    [InlineData("车队能力0")]
    [InlineData("车队能力极小")]
    [InlineData("全部已采")]
    [InlineData("没有物料参数")]
    [InlineData("没有出矿位置")]
    [InlineData("煤视密度为0")]
    [InlineData("库容为负")]
    [InlineData("剥离能力小于必剥")]
    public void 退化输入_不抛异常且不静默(string what)
    {
        var units = UnitPlanFixture.Stacked();
        var slots = UnitPlanFixture.Dump("外排1", 3, 1.0e5, 2500, 700, 1100);
        double total = UnitPlanFixture.TotalCoalT(units);
        UnitPlanInput? inp = UnitPlanFixture.Input(units, slots, 18e4, 5.0);

        switch (what)
        {
            case "空输入": inp = null; break;
            case "零单元": inp!.Units = new List<MineUnit>(); break;
            case "零位置": inp!.Slots = new List<DumpSlot>(); break;
            case "煤量0": inp!.CoalTargetT = 0; break;
            case "煤量为负": inp!.CoalTargetT = -1e4; break;
            case "目标超上限": inp!.CoalTargetT = total * 3; break;
            case "剥采比为负": inp!.TargetStripRatio = -3; break;
            case "车队能力0": inp!.FleetCapTKm = 0; break;
            case "车队能力极小": inp!.FleetCapTKm = 1e3; break;
            case "全部已采":
                inp!.Units = units.Select(u => { var c = Clone(u); c.DoneFraction = 1.0; return c; }).ToList(); break;
            case "没有物料参数": inp!.Materials = Array.Empty<GapMaterial>(); break;
            case "没有出矿位置": inp!.CoalSinks = new List<CoalSink>(); break;
            case "煤视密度为0": inp!.CoalDensity = 0; break;
            case "库容为负":
                inp!.Slots = slots.Select(s => new DumpSlot
                {
                    DumpName = s.DumpName, Level = s.Level, Order = s.Order, CapacityM3 = -s.CapacityM3,
                    Cx = s.Cx, Cy = s.Cy, Cz = s.Cz, IsInternal = s.IsInternal,
                    AvailableFromMonth = s.AvailableFromMonth, HaulKm = s.HaulKm,
                }).ToList(); break;
            case "剥离能力小于必剥": inp!.StripCapM3 = 1e4; break;
        }

        var r = UnitPlanEngine.Solve(inp!);       // ← 不许抛
        _out.WriteLine($"【{what}】" + (r.Success ? r.Report() : "失败：" + r.Error));

        if (!r.Success)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Error), "失败却没有原因 —— 这就是静默");
            return;
        }
        var bad = UnitPlanCriteria.QuantitiesSound(r, inp!);
        bad.AddRange(UnitPlanCriteria.NotSilent(r, inp!));
        bad.AddRange(UnitPlanCriteria.DumpCapacityRespected(r, inp!.Slots));
        bad.AddRange(UnitPlanCriteria.OrgMetricsMatchOrder(r));
        bad.AddRange(UnitPlanCriteria.AtMostOnePartialCoal(r));
        Dump(what, bad);
        Assert.Empty(bad);

        // 出了计划就得自洽（引擎那份自检也不许有异议）
        var own = r.Validate();
        Dump(what + " · 引擎自检", own);
        Assert.Empty(own);
    }

    /// <summary>零位置时"排不下"必须显式记账，不许摊平进别的量。</summary>
    [Fact]
    public void 退化_零排土位置时岩必须显式记为排不下()
    {
        var units = UnitPlanFixture.Stacked();
        var inp = UnitPlanFixture.Input(units, new List<DumpSlot>(), 18e4, 5.0);
        var r = UnitPlanEngine.Solve(inp);
        _out.WriteLine(r.Report());
        Assert.True(r.Success);
        Assert.True(r.StripM3 > 1e-6, "选了岩才谈得上排不下");
        Assert.Equal(r.StripM3, r.UnplacedM3, 3);
        Assert.False(r.Feasible);
        Assert.Equal(-1, r.InternalRatePct);      // 没有排土流是 −1，不是 0（0 = 全外排，是另一个结论）
    }

    /// <summary>
    /// 【★ 煤量缺口必须落到结果上】
    ///
    /// 目标给到可采总量的 3 倍，引擎在 Notes 里写了"煤没凑够"，
    /// 但 <see cref="UnitPlanResult.ShortfallT"/> 是<b>结果对象上的那个数</b> ——
    /// 下游（比选窗口、月度目标对账、Feasible 判定）读的是它，不是 Notes。
    ///
    /// <para>这一条单列，是因为它<b>会被"缺口不减"那条扫描判据空过</b>：
    /// ShortfallT 恒 0 也是单调不减的。恒 0 的量必须有一条判据直接盯着。</para>
    /// </summary>
    [Fact]
    public void 缺口必须落到结果上_目标超上限时()
    {
        var units = UnitPlanFixture.Stacked();
        var slots = UnitPlanFixture.Dump("外排1", 8, 3e5, 2500, 700, 1100);
        double total = UnitPlanFixture.TotalCoalT(units);
        var inp = UnitPlanFixture.Input(units, slots, total * 3, 5.0);
        var r = UnitPlanEngine.Solve(inp);
        _out.WriteLine(r.Report());
        _out.WriteLine($"可采总量 {total / 1e4:0.00}万t · 目标 {inp.CoalTargetT / 1e4:0.00}万t · 达成 {r.CoalT / 1e4:0.00}万t "
                     + $"· ShortfallT {r.ShortfallT / 1e4:0.00}万t · Feasible {r.Feasible}");

        var bad = UnitPlanCriteria.CoalVsTarget(r, inp);
        bad.AddRange(UnitPlanCriteria.FeasibilityHonest(r, inp));
        Dump("缺口对账", bad);
        Assert.Empty(bad);
    }

    /// <summary>全部已采完时同理：一方煤都采不出来，缺口必须是整个目标。</summary>
    [Fact]
    public void 缺口必须落到结果上_全部已采时()
    {
        var units = UnitPlanFixture.Stacked().Select(u => { var c = Clone(u); c.DoneFraction = 1.0; return c; }).ToList();
        var slots = UnitPlanFixture.Dump("外排1", 3, 1e5, 2500, 700, 1100);
        var inp = UnitPlanFixture.Input(units, slots, 18e4, 5.0);
        var r = UnitPlanEngine.Solve(inp);
        _out.WriteLine(r.Report());
        Assert.True(r.Success);
        Assert.Empty(r.Assignments);

        var bad = UnitPlanCriteria.CoalVsTarget(r, inp);
        bad.AddRange(UnitPlanCriteria.FeasibilityHonest(r, inp));
        Dump("缺口对账", bad);
        Assert.Empty(bad);
    }

    /// <summary>
    /// 【★ 超前剥离候选集漏掉了"部分必剥"的岩幅】—— <see cref="煤量扫描_只判方向不判数字"/>
    /// 那条剥离量掉头的<b>机理</b>，单独摆出来免得只看到现象。
    ///
    /// <para>算例只有一个柱：煤采一半 ⇒ 压在它头上的两个岩幅各进必剥 50%。
    /// 目标剥采比调到高得离谱（20），剥离能力/库容/车队全不限 ——
    /// 这时候本月前沿上<b>凡是能剥的岩都该被剥掉</b>：上岩幅没有任何前驱，
    /// 剩下那 50% 现在就能剥，剥了下月备采还宽裕。</para>
    ///
    /// <para>判据只问一句：<b>解完之后，前沿上还有没有剥得动却没剥的岩</b>。
    /// 有就说明超前剥离的候选集把它漏了。<b>不判具体剥了多少方</b>。</para>
    /// </summary>
    [Fact]
    public void 超前剥离候选集漏掉了部分必剥的岩幅()
    {
        var units = UnitPlanFixture.Stacked(bands: 1, panels: 1);
        var slots = UnitPlanFixture.Dump("外排1", 20, 1e6, 2500, 700, 1100);      // 库容管够
        var g = UnitPlanFixture.Graph(units);
        double half = units.First(u => u.IsCoal).InSituM3 * 0.5 * UnitPlanFixture.CoalRho;

        var inp = UnitPlanFixture.Input(units, slots, half, stripRatio: 20.0);     // 目标剥采比高得离谱
        inp.Graph = g;
        var r = UnitPlanEngine.Solve(inp);
        _out.WriteLine(r.Report());
        Assert.True(r.Success, r.Error);
        Assert.Empty(UnitPlanCriteria.All(r, inp, g, slots));
        Assert.True(r.UnplacedM3 <= 1e-6 && r.UnhauledM3 <= 1e-6, "算例的库容/车队必须管够，否则这条判据说明不了问题");

        var taken = r.Assignments.ToDictionary(a => a.UnitId, a => a.Fraction, StringComparer.Ordinal);
        var left = new List<string>();
        double leftM3 = 0;
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (u.IsCoal || !g.IsFree(i, null)) continue;                          // 只看前沿上（无未完成前驱）的岩
            double rest = u.InSituM3 * Math.Max(0, 1 - u.DoneFraction - taken.GetValueOrDefault(u.UnitId));
            if (rest <= 1e-6) continue;
            leftM3 += rest;
            left.Add($"{u.UnitId} 还剩 {rest / 1e4:0.00}万m³ 没剥（本月只剥了 {taken.GetValueOrDefault(u.UnitId) * 100:0.#}%）");
        }
        Dump("前沿上剥得动却没剥的岩", left);
        _out.WriteLine($"目标剥离 {half * 20 / 1e4:0.0}万m³ · 实际 {r.StripM3 / 1e4:0.0}万m³ · 必剥 {r.MandatoryStripM3 / 1e4:0.0}万m³");
        Assert.True(leftM3 <= 1e-6,
            $"目标剥采比给到 20（目标剥离 {half * 20 / 1e4:0.0}万m³），实际只剥了 {r.StripM3 / 1e4:0.0}万m³ "
          + $"= 必剥闭包 {r.MandatoryStripM3 / 1e4:0.0}万m³，前沿上还有 {leftM3 / 1e4:0.00}万m³ 剥得动的岩没动。"
          + "根因在 ExtraRockOrder：`mandatory.Contains(i)` 把【只有一部分进了必剥】的岩幅整幅剔出了超前剥离候选，"
          + "而补岩循环里那句 `double already = rockPick[i]`（本意正是给这种幅做去重）因此永远是 0 —— 是死代码。");
    }

    private static MineUnit Clone(MineUnit u) => new()
    {
        UnitId = u.UnitId, Kind = u.Kind, SeamCode = u.SeamCode,
        BandId = u.BandId, PanelIndex = u.PanelIndex, PanelCount = u.PanelCount,
        Cx = u.Cx, Cy = u.Cy, Cz = u.Cz, ZLo = u.ZLo, ZHi = u.ZHi,
        StrikeLenM = u.StrikeLenM, WidthM = u.WidthM, ThickM = u.ThickM,
        InSituM3 = u.InSituM3, MaterialIndex = u.MaterialIndex, DoneFraction = u.DoneFraction,
        RailXy = (double[])u.RailXy.Clone(),
        MinX = u.MinX, MinY = u.MinY, MaxX = u.MaxX, MaxY = u.MaxY,
    };
}
