// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/UnitPlanStripTargetTests.cs（逐行对应；仅命名空间适配）
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
/// <b>U13 组</b> —— 「给定采煤量<b>和排弃量</b> → 自动排本月」这条路线的判据。
///
/// <para>现场口径（2026-08-08 逐条问定，见 `docs/采掘单元排产_设计.md` U13）：
/// ① 排弃量按<b>采场侧原位实方</b>（= 逐月配置表「剥离」那一列）；
/// ② 它是<b>目标</b>不是上限（上限是另一道闸 <c>StripCapM3</c>）；
/// ③ 与煤量<b>脱钩</b>，照绝对量剥，<b>不等比缩放</b> ⇒ 剥采比是结果不是输入；
/// ④ 与剥采比都给时<b>以排弃量为准</b>并留条。</para>
///
/// <para><b>这一组里最要紧的是 <see cref="U13c_煤欠产时排弃量不许跟着缩"/></b> ——
/// 它是「给排弃量」与「给剥采比」的分水岭。两条路线在<b>煤刚好凑满</b>的算例上
/// 答案一模一样，只有煤欠产时才分得开，所以判据必须挑那种局面。</para>
/// </summary>
public class UnitPlanStripTargetTests
{
    private readonly ITestOutputHelper _out;
    public UnitPlanStripTargetTests(ITestOutputHelper o) => _out = o;

    private const double CoalVol = 5e4, MidVol = 1.2e5, TopVol = 1.6e5;
    /// <summary>该算例里必剥闭包的手算值：选中那一柱的中岩 + 上岩。</summary>
    private const double Closure = MidVol + TopVol;                     // 28 万m³
    /// <summary>本月前沿上真正剥得动的总量（手算）：闭包 + 另外 3 柱的上岩。</summary>
    private const double Frontier = Closure + 3 * TopVol;               // 76 万m³
    /// <summary>该算例里唯一还能采的那一幅煤（t）。</summary>
    private const double CoalLeftT = CoalVol * UnitPlanFixture.CoalRho; // 6.75 万t

    private static List<DumpSlot> BigDump() => UnitPlanFixture.Dump("外排1", 4, 3e5, 6000, 500, 1000);

    /// <summary>只剩一幅煤可采 + 库容充裕的标准局面。<paramref name="coalT"/> 给大就是煤欠产。</summary>
    private static UnitPlanInput Case(double coalT, double stripTargetM3 = 0, double stripRatio = 0,
                                      double stripCapM3 = 0, double fleetCapTKm = 0)
    {
        var inp = UnitPlanFixture.Input(UnitPlanFixture.OneCoalLeft(CoalVol, MidVol, TopVol),
                                        BigDump(), coalT, stripRatio, stripTargetM3);
        inp.StripCapM3 = stripCapM3;
        inp.FleetCapTKm = fleetCapTKm;
        return inp;
    }

    private void Check(UnitPlanResult r, UnitPlanInput inp)
    {
        var g = UnitPlanFixture.Graph(inp.Units);
        var bad = UnitPlanCriteria.All(r, inp, g, inp.Slots);
        var self = r.Validate();
        _out.WriteLine(r.Report());
        Assert.True(bad.Count == 0, "判据不过：\n　" + string.Join("\n　", bad));
        Assert.True(self.Count == 0, "引擎自检不过：\n　" + string.Join("\n　", self));
    }

    // ════════════════════════════════════════════════════════════════════
    //  算例自检 —— 先证明夹具确实摆成了"煤欠 + 闭包不大"，否则下面每条都可能空过
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void 算例自检_只剩一幅煤_闭包与前沿量都是手算得出的()
    {
        var units = UnitPlanFixture.OneCoalLeft(CoalVol, MidVol, TopVol);
        Assert.Equal(4, units.Count(u => u.IsCoal));
        Assert.Equal(1, units.Count(u => u.IsCoal && u.RemainM3 > 1e-9));      // 只剩一幅
        Assert.Equal(CoalLeftT, UnitPlanFixture.TotalCoalT(units), 3);
        Assert.Equal(Frontier, UnitPlanFixture.FrontierRockM3(MidVol, TopVol), 3);

        // 闭包：那一幅煤压着本柱的两级岩，别的柱一律不压（叠柱算例的构造性质）
        var g = UnitPlanFixture.Graph(units);
        int ci = g.IndexOf(units.First(u => u.IsCoal && u.RemainM3 > 1e-9).UnitId);
        double closure = g.Closure(ci).Where(p => !g.Units[p].IsCoal).Sum(p => g.Units[p].InSituM3);
        Assert.Equal(Closure, closure, 3);
    }

    // ════════════════════════════════════════════════════════════════════
    //  U13.1 / U13.2 —— 给定量真的被凑到，且它是目标不是上限
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void U13a_给定排弃量真的被凑到()
    {
        double want = 5e5;                                   // 50 万m³：闭包(28) < 它 < 前沿可剥(76)
        var inp = Case(CoalLeftT, stripTargetM3: want);
        var r = UnitPlanEngine.Solve(inp);
        Check(r, inp);

        Assert.Equal(want, r.StripTargetM3, 3);
        Assert.True(Math.Abs(r.StripM3 - want) / want <= 5e-3,
            $"没凑到给定排弃量：{r.StripM3:0} vs {want:0} m³（相对差 {(r.StripM3 - want) / want * 100:0.###}%）");
        Assert.True(r.ShortStripM3 <= 1e-6, $"凑到了却报了剥不够 {r.ShortStripM3:0}");
        Assert.True(r.StripM3 > Closure + 1e-6, "只剥了必剥闭包 —— 超前剥离那一段没走");
        Assert.Contains("给定排弃量", r.StripTargetNote);
    }

    [Fact]
    public void U13b_它是目标不是上限_剥离能力才是上限()
    {
        // 同一个给定量，一次不卡能力、一次把能力卡在给定量之下 ——
        // 若把给定量当成"上限"来实现，这两次会一样（都 ≤ 给定量），判据就分不开。
        double want = 5e5, cap = 3.5e5;
        var free = Case(CoalLeftT, stripTargetM3: want);
        var capped = Case(CoalLeftT, stripTargetM3: want, stripCapM3: cap);
        var r1 = UnitPlanEngine.Solve(free);
        var r2 = UnitPlanEngine.Solve(capped);
        Check(r1, free); Check(r2, capped);

        Assert.Equal(want, r1.StripTargetM3, 3);             // 不卡能力：目标就是给定量
        Assert.Equal(cap, r2.StripTargetM3, 3);              // 卡了能力：夹到能力
        Assert.True(Math.Abs(r2.StripM3 - cap) / cap <= 5e-3, $"没剥到能力上限：{r2.StripM3:0} vs {cap:0}");

        // ★ 能力夹回造成的少剥【不算缺口】—— 那是用户自己给的闸，不是干不成
        Assert.True(r2.ShortStripM3 <= 1e-6,
            $"被剥离能力夹回却记了 {r2.ShortStripM3:0}m³ 缺口 —— 缺口列会天天非零，真的那条就被淹没了");
        Assert.True(r2.Feasible, "被能力夹回不该判为不可行");
        Assert.Contains(r2.Notes, n => n.Contains("夹回"));
    }

    // ════════════════════════════════════════════════════════════════════
    //  U13.3 —— 与煤量脱钩（这一组的分水岭）
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void U13c_煤欠产时排弃量不许跟着缩()
    {
        double want = 5e5;
        double askCoal = 20e4;                               // 要 20 万t，前沿上只有 6.75 万t ⇒ 煤欠
        var inp = Case(askCoal, stripTargetM3: want);
        var r = UnitPlanEngine.Solve(inp);
        Check(r, inp);

        Assert.True(r.ShortfallT > 1e-6, "算例没造出煤欠产 —— 这条判据会空过");
        Assert.Equal(want, r.StripTargetM3, 3);
        Assert.True(Math.Abs(r.StripM3 - want) / want <= 5e-3,
            $"煤欠产把剥离量也缩了：{r.StripM3:0} vs 给定 {want:0} m³");

        // 反面对照：同一个局面改走【剥采比】那条路线 —— 目标 = 比 × 实际煤量，必然缩。
        // 两条路线在这个算例上给出【不同】答案，正是 U13.3 存在的理由。
        double equiv = want / askCoal;                       // 按"想要的煤量"定的比
        var byRatio = Case(askCoal, stripRatio: equiv);
        var r2 = UnitPlanEngine.Solve(byRatio);
        Check(r2, byRatio);
        _out.WriteLine($"给排弃量 {r.StripM3 / 1e4:0.0}万m³ vs 给剥采比 {r2.StripM3 / 1e4:0.0}万m³");
        Assert.True(r2.StripM3 < r.StripM3 - 1e-6,
            "改走剥采比路线剥离量竟然没变少 —— 说明这个算例分不开两条路线，判据在空过");
        Assert.True(r2.StripM3 >= Closure - 1e-6, "剥采比路线也不能跌破必剥闭包");
    }

    [Fact]
    public void U13c2_煤超采时排弃量同样不跟着涨()
    {
        // 另一头：煤按整幅凑会略微超过目标（+2% 容差内）。按比算的话剥离量会跟着涨，
        // 给定排弃量则纹丝不动。判两头，才说明"脱钩"不是碰巧。
        double want = 5e5;
        double askCoal = CoalLeftT / 1.015;                  // 目标略低于那一整幅 ⇒ 整幅凑进去就超采
        var inp = Case(askCoal, stripTargetM3: want);
        var r = UnitPlanEngine.Solve(inp);
        Check(r, inp);

        Assert.True(r.CoalDeviationPct > 0, "算例没造出超采 —— 这条判据会空过");
        Assert.Equal(want, r.StripTargetM3, 3);
        Assert.True(Math.Abs(r.StripM3 - want) / want <= 5e-3, $"超采把剥离量也带高了：{r.StripM3:0} vs {want:0}");
    }

    // ════════════════════════════════════════════════════════════════════
    //  两道硬夹 + 第四类缺口
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void U13d_给低于必剥闭包时按闭包剥不缩放()
    {
        double want = 1e5;                                   // 10 万m³ < 闭包 28 万m³
        var inp = Case(CoalLeftT, stripTargetM3: want);
        var r = UnitPlanEngine.Solve(inp);
        Check(r, inp);

        Assert.Equal(Closure, r.StripTargetM3, 3);           // 被下界顶上去
        Assert.True(r.StripM3 >= Closure - 1e-6, $"剥的比必剥闭包还少：{r.StripM3:0} < {Closure:0}");
        Assert.True(r.ShortStripM3 <= 1e-6, "按闭包剥是正常结论，不该记缺口");
        Assert.Contains(r.Notes, n => n.Contains("给低了"));
    }

    [Fact]
    public void U13e_前沿上剥不动那么多时记剥不够且判不可行()
    {
        double want = 2e6;                                   // 200 万m³ ≫ 前沿可剥 76 万m³
        var inp = Case(CoalLeftT, stripTargetM3: want);
        var r = UnitPlanEngine.Solve(inp);
        Check(r, inp);

        Assert.Equal(want, r.StripTargetM3, 3);
        Assert.True(r.ShortStripM3 > 1e-6, "剥不动那么多却一个缺口都没报");
        Assert.Equal(want - r.StripM3, r.ShortStripM3, 3);   // 账要平：达成 + 剥不够 = 目标
        Assert.False(r.Feasible, "有剥不够却判为可行");
        Assert.Contains(r.Notes, n => n.Contains("剥离量没凑够"));

        // 四类缺口分开记：这一条是【前沿无岩】，不是排不下、也不是拉不动
        Assert.True(r.UnplacedM3 <= 1e-6, "库容充裕却报了排不下 —— 缺口串味了");
        Assert.True(r.UnhauledM3 <= 1e-6, "没卡车队能力却报了拉不动");
    }

    // ════════════════════════════════════════════════════════════════════
    //  U13.4 两个都给 / 都不给
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void U13f_排弃量与剥采比都给时以排弃量为准并留条()
    {
        double want = 5e5;
        var inp = Case(CoalLeftT, stripTargetM3: want, stripRatio: 1.0);   // 比只有 1.0，比它小得多
        var r = UnitPlanEngine.Solve(inp);
        Check(r, inp);

        Assert.Equal(want, r.StripTargetM3, 3);
        Assert.Contains(r.Notes, n => n.Contains("以排弃量为准"));
        Assert.Contains(r.Notes, n => n.Contains("没有参与运算"));
    }

    [Fact]
    public void U13g_两个都不给时只剥必剥闭包并说清楚()
    {
        var inp = Case(CoalLeftT);
        var r = UnitPlanEngine.Solve(inp);
        Check(r, inp);

        Assert.Equal(Closure, r.StripTargetM3, 3);
        Assert.True(Math.Abs(r.StripM3 - Closure) / Closure <= 5e-3, $"{r.StripM3:0} vs 闭包 {Closure:0}");
        Assert.Contains(r.Notes, n => n.Contains("只剥【必剥闭包】"));
    }

    // ════════════════════════════════════════════════════════════════════
    //  ★ 闸门自检：把这条规则关掉，结果必须【真的变】
    //    —— 判据自己也要判：不然它可能一直在判一件本来就成立的事（F0 的做法）
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void U13h_关掉给定排弃量这条闸_结果必须真的不一样()
    {
        double want = 5e5;
        var on = Case(CoalLeftT, stripTargetM3: want);
        var off = Case(CoalLeftT);                            // 同一算例，只把这条闸关掉
        string a = UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(on));
        string b = UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(off));
        Assert.NotEqual(a, b);                                // 一样就说明这条闸是摆设，上面每条都在空过
    }

    [Fact]
    public void U13i_纯函数性_给定排弃量这条链两次逐位相同()
    {
        var inp = Case(CoalLeftT, stripTargetM3: 5e5);
        string a = UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(inp));
        string b = UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(inp));
        Assert.Equal(a, b);

        // 改了再改回来（U7）：库容是有状态的，不拿干净副本这里就会红
        inp.StripTargetM3 = 9e5; UnitPlanEngine.Solve(inp);
        inp.StripTargetM3 = 1e5; UnitPlanEngine.Solve(inp);
        inp.StripTargetM3 = 5e5;
        Assert.Equal(a, UnitPlanCriteria.Fingerprint(UnitPlanEngine.Solve(inp)));
    }

    // ════════════════════════════════════════════════════════════════════
    //  退化输入 —— 不抛、不静默
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("负数")]
    [InlineData("零")]
    [InlineData("极小")]
    [InlineData("极大")]
    [InlineData("煤量为零但要剥")]
    public void U13j_退化的排弃量不抛异常也不静默(string what)
    {
        var inp = what switch
        {
            "负数" => Case(CoalLeftT, stripTargetM3: -5e5),
            "零" => Case(CoalLeftT, stripTargetM3: 0),
            "极小" => Case(CoalLeftT, stripTargetM3: 1e-3),
            "极大" => Case(CoalLeftT, stripTargetM3: 1e12),
            _ => Case(0, stripTargetM3: 3e5),                 // 纯剥离月：不采煤，只剥
        };

        var r = UnitPlanEngine.Solve(inp);
        Check(r, inp);
        Assert.True(r.Success, "退化输入不该让求解失败：" + r.Error);

        switch (what)
        {
            case "负数":
                Assert.Contains(r.Notes, n => n.Contains("负数"));
                Assert.Equal(Closure, r.StripTargetM3, 3);    // 按"没给"处理 ⇒ 退回闭包
                break;
            case "零":
            case "极小":
                // 低于闭包一律被下界顶到闭包（"极小"那一档还要走一遍"给低了"的说法）
                Assert.Equal(Closure, r.StripTargetM3, 3);
                break;
            case "极大":
                Assert.True(r.ShortStripM3 > 1e-6, "要 1e12 m³ 却说凑得到");
                Assert.False(r.Feasible);
                break;
            default:
                // 煤量 0 ⇒ 闭包为 0 ⇒ 目标就是给定量，纯超前剥离月，完全合法
                Assert.True(r.CoalT <= 1e-6, "没要煤却采了煤");
                Assert.Equal(3e5, r.StripTargetM3, 3);
                Assert.True(r.StripM3 > 1e-6, "纯剥离月一方都没剥");
                break;
        }
    }
}
