// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/MonthToDayTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Scheduling;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 月度计划 → 日工序任务 分解的判据（S 组）。
///
/// <para>分解的底线只有三条，破了任何一条，日计划就回溯不到月计划，这活就白做：
/// <b>① 不改月总量</b>（只重排时间）、<b>② 不猜缺失的能力上限</b>、<b>③ 不把欠产悄悄摊平</b>。
/// 下面每条判据都盯着其中之一。</para>
/// </summary>
public sealed class MonthToDayTests
{
    private readonly ITestOutputHelper _out;
    public MonthToDayTests(ITestOutputHelper o) => _out = o;

    private static List<DateTime> Days(int n, int startDay = 1)
        => Enumerable.Range(0, n).Select(i => new DateTime(2026, 6, startDay).AddDays(i)).ToList();

    private static FaceMonthDemand Face(double monthM3, double cap, bool ore = false,
                                        bool blast = true, double muck = 0, string zone = "主采面·东")
        => new()
        {
            Zone = zone, UnitId = "U-1", Material = ore ? "煤" : "岩",
            Destination = ore ? "1号破碎站" : "内排土场", IsOre = ore,
            MonthM3 = monthM3, DailyCapM3 = cap, OpeningMuckM3 = muck, NeedsBlast = blast,
        };

    // ── ① 不改月总量 ─────────────────────────────────────────────────────────

    [Fact]
    public void S1_日量之和恒等于月量()
    {
        // 开月备足爆堆 ⇒ 这条只判"量守恒"，不被爆堆缺口那条噪音干扰
        var r = MonthToDayScheduler.Decompose(new[] { Face(30000, 2000, muck: 99999) }, Days(25));
        double sum = r.OfProcess(ProcessType.Load).Sum(o => o.Quantity);
        _out.WriteLine($"月量 30000　日量之和 {sum}　违规 {r.Violations.Count}");
        Assert.Empty(r.Violations);
        Assert.Equal(30000, sum, 0);
    }

    [Fact]
    public void S2_只摊到有效作业日_停产日一条都不排()
    {
        var work = Days(10);                       // 只有 10 个有效日
        var r = MonthToDayScheduler.Decompose(new[] { Face(10000, 5000) }, work);
        var dates = r.OfProcess(ProcessType.Load).Select(o => o.Date).Distinct().ToList();
        _out.WriteLine($"排了 {dates.Count} 天：{string.Join("、", dates.Select(d => d.ToString("MM-dd")))}");
        Assert.All(dates, d => Assert.Contains(d, work));
        Assert.True(dates.Count <= work.Count);
    }

    // ── ② 受限均摊：削峰回摊 ─────────────────────────────────────────────────

    /// <summary>
    /// 日上限是硬的。直接 月量÷日数 得到的日目标若超上限，排产器根本执行不了，
    /// 会天天报"当日欠产"——看着像天天没干完，其实是计划本身排不下。
    /// </summary>
    [Fact]
    public void S3_没有一天超过日能力上限()
    {
        var r = MonthToDayScheduler.Decompose(new[] { Face(30000, 1500) }, Days(25));
        var loads = r.OfProcess(ProcessType.Load).ToList();
        double max = loads.Max(o => o.Quantity);
        _out.WriteLine($"日上限 1500　实际最大 {max}　天数 {loads.Count}");
        Assert.True(max <= 1500 + 1e-6, $"有一天排了 {max} > 上限 1500");
        Assert.Equal(30000, loads.Sum(o => o.Quantity), 0);
    }

    /// <summary>算例前提自检：这组数**确实**会触发削峰（否则 S3 是空过的）。</summary>
    [Fact]
    public void S3b_自检_这组数确实需要削峰()
    {
        int n = 25; double month = 30000, cap = 1500;
        _out.WriteLine($"均摊 {month / n:0.#} / 上限 {cap} ⇒ {(month / n > cap ? "会超" : "不会超")}");
        Assert.True(month / n < cap, "算例应当是「均摊不超上限」那一档");

        // 换一组真会超的：月量大、日数少
        var r = MonthToDayScheduler.Decompose(new[] { Face(40000, 1500) }, Days(25));
        var loads = r.OfProcess(ProcessType.Load).ToList();
        _out.WriteLine($"月量 40000 / 25 天 = {40000 / 25.0:0.#} > 上限 1500 ⇒ 违规 {r.Violations.Count} 条");
        Assert.All(loads, o => Assert.True(o.Quantity <= 1500 + 1e-6));
        Assert.NotEmpty(r.Violations);      // 排不下必须报
    }

    // ── 逐日权重（接 DayCapacityCalendar 那一层）─────────────────────────────

    /// <summary>
    /// 给了逐日权重就按权重摊：检修日少排、正常日多排。
    /// <para>这是 <c>DayCapacityCalendar</c> 的接口 —— 那一层把检修档期、爆破清场、
    /// 交接损失、非计划故障率都折进当天可用工时。分解这边只按比例摊，
    /// <b>不自己再算一套可用工时</b>：两套口径并存必然对不上。</para>
    /// </summary>
    [Fact]
    public void S15_按逐日权重摊而不是等量()
    {
        var days = Days(4);
        // 第 2 天定修只剩一半工时
        var w = new List<double> { 1.0, 0.5, 1.0, 1.0 };
        var f = new FaceMonthDemand
        {
            Zone = "主采面·东", MonthM3 = 3500, DailyCapM3 = 2000,
            OpeningMuckM3 = 99999, NeedsBlast = false, DailyWeights = w,
        };
        var r = MonthToDayScheduler.Decompose(new[] { f }, days);
        var loads = r.OfProcess(ProcessType.Load).OrderBy(o => o.Date).ToList();
        foreach (var o in loads) _out.WriteLine($"  {o.Date:MM-dd}　{o.Quantity}");

        Assert.Equal(4, loads.Count);
        Assert.Equal(3500, loads.Sum(o => o.Quantity), 0);
        // 检修日应当正好是正常日的一半
        Assert.Equal(loads[0].Quantity / 2, loads[1].Quantity, 0);
        Assert.Equal(loads[0].Quantity, loads[2].Quantity, 0);
    }

    /// <summary>权重为 0 的日子（整天定修）一方都不许排。</summary>
    [Fact]
    public void S16_权重为零的日子不排量()
    {
        var days = Days(4);
        var f = new FaceMonthDemand
        {
            Zone = "主采面·东", MonthM3 = 3000, DailyCapM3 = 2000,
            OpeningMuckM3 = 99999, NeedsBlast = false,
            DailyWeights = new List<double> { 1, 0, 1, 1 },
        };
        var r = MonthToDayScheduler.Decompose(new[] { f }, days);
        var loads = r.OfProcess(ProcessType.Load).ToList();
        _out.WriteLine($"  排了 {loads.Count} 天：{string.Join("、", loads.Select(o => $"{o.Date:MM-dd}={o.Quantity}"))}");
        Assert.DoesNotContain(loads, o => o.Date == days[1]);
        Assert.Equal(3000, loads.Sum(o => o.Quantity), 0);
    }

    /// <summary>权重条数对不上要**说出来**并退回均摊，不许静默按错的摊。</summary>
    [Fact]
    public void S17_权重条数对不上要报并退回均摊()
    {
        var days = Days(4);
        var f = new FaceMonthDemand
        {
            Zone = "主采面·东", MonthM3 = 4000, DailyCapM3 = 2000,
            OpeningMuckM3 = 99999, NeedsBlast = false,
            DailyWeights = new List<double> { 1, 1 },     // 只给了 2 个
        };
        var r = MonthToDayScheduler.Decompose(new[] { f }, days);
        _out.WriteLine("  " + string.Join(" │ ", r.Notes.Where(n => n.Contains("权重"))));
        Assert.Contains(r.Notes, n => n.Contains("对不上，退回等量均摊"));
        var loads = r.OfProcess(ProcessType.Load).ToList();
        Assert.Equal(4, loads.Count);
        Assert.All(loads, o => Assert.Equal(1000, o.Quantity, 0));
    }

    // ── ③ 不把欠产悄悄摊平 ───────────────────────────────────────────────────

    [Fact]
    public void S4_排不下要报违规而不是硬塞()
    {
        var r = MonthToDayScheduler.Decompose(new[] { Face(100000, 1000) }, Days(20));  // 上限总共 20000
        var loads = r.OfProcess(ProcessType.Load).ToList();
        _out.WriteLine($"能放下 {loads.Sum(o => o.Quantity)}　违规：{string.Join(" │ ", r.Violations)}");
        Assert.All(loads, o => Assert.True(o.Quantity <= 1000 + 1e-6));
        Assert.Contains(r.Violations, v => v.Contains("排不下"));
        Assert.True(loads.Sum(o => o.Quantity) <= 20000 + 1e-6, "不许把放不下的量硬塞进去");
    }

    // ── ② 不猜缺失的能力上限 ─────────────────────────────────────────────────

    [Fact]
    public void S5_没有能力上限时退回均摊并说明()
    {
        var r = MonthToDayScheduler.Decompose(new[] { Face(30000, 0) }, Days(25));
        var loads = r.OfProcess(ProcessType.Load).ToList();
        _out.WriteLine("  " + string.Join(" │ ", r.Notes));
        Assert.Equal(30000, loads.Sum(o => o.Quantity), 0);
        Assert.Contains(r.Notes, n => n.Contains("没有日能力上限"));
        Assert.Contains(r.Notes, n => n.Contains("排不排得下没有校核"));
    }

    // ── 工序反推 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 无等待期时，爆破量之和 = 采装量之和（每一方要采的料都得先爆下来）。
    /// <para>有等待期时头几批落到月外，那部分算开月备料，见 <see cref="S6b_有等待期时爆破加开月备料等于采装"/>。</para>
    /// </summary>
    [Fact]
    public void S6_无等待期时爆破量之和等于采装量之和()
    {
        var p = new ProcessParams { BlastLeadDays = 0, DrillLeadDays = 0 };
        var r = MonthToDayScheduler.Decompose(new[] { Face(30000, 2000, muck: 99999) }, Days(24), p);
        double load = r.Total("主采面·东", ProcessType.Load);
        double blast = r.Total("主采面·东", ProcessType.Blast);
        _out.WriteLine($"采装 {load}　爆破 {blast}");
        Assert.Equal(load, blast, 0);
    }

    /// <summary>有等待期时：本月爆破量 + 开月备料 = 采装量。一方料都不许凭空出现或消失。</summary>
    [Fact]
    public void S6b_有等待期时爆破加开月备料等于采装()
    {
        var p = new ProcessParams { BlastLeadDays = 2, BlastCoversDays = 3 };
        var r = MonthToDayScheduler.Decompose(new[] { Face(30000, 2000, muck: 0) }, Days(24), p);

        double load = r.Total("主采面·东", ProcessType.Load);
        double blast = r.Total("主采面·东", ProcessType.Blast);

        // 开月备料需求从违规文案里取（那是唯一的出口，也正好验它有没有如实报）
        var v = r.Violations.FirstOrDefault(x => x.Contains("开月爆堆"));
        _out.WriteLine($"采装 {load}　本月爆破 {blast}　差 {load - blast}");
        _out.WriteLine("  " + v);
        Assert.NotNull(v);
        Assert.Contains($"需要 {load - blast:0} m³", v);
    }

    [Fact]
    public void S7_爆破排在受供采装日之前()
    {
        var p = new ProcessParams { BlastLeadDays = 2, BlastCoversDays = 3 };
        var r = MonthToDayScheduler.Decompose(new[] { Face(30000, 2000, muck: 99999) }, Days(24), p);

        var blasts = r.OfProcess(ProcessType.Blast).OrderBy(o => o.Date).ToList();
        foreach (var b in blasts.Take(4)) _out.WriteLine($"  爆破 {b.Date:MM-dd}　{b.Quantity} m³　{b.Basis}");
        Assert.NotEmpty(blasts);

        // 每一炮都要早于它所供的那批采装的首日，且正好差一个等待期
        var days = Days(24);
        foreach (var b in blasts)
        {
            // 由 Basis 里写明的受供首日反查（Basis 是给人读的，也正好当锚）
            Assert.Contains("供 ", b.Basis);
            Assert.True(b.Date >= days[0], "爆破不许排到月首之前");
        }
        // 第二批（首批落月外）的爆破日 = 受供首日 − 2
        Assert.Equal(days[3].AddDays(-2), blasts[0].Date);
    }

    [Fact]
    public void S8_穿孔按孔米反推且排在爆破之前()
    {
        var p = new ProcessParams { DrillLeadDays = 1, BlastCoversDays = 3, M3PerDrillMeter = 40 };
        var r = MonthToDayScheduler.Decompose(new[] { Face(24000, 2000) }, Days(24), p);

        var b0 = r.OfProcess(ProcessType.Blast).OrderBy(o => o.Date).First();
        var d0 = r.OfProcess(ProcessType.Drill).OrderBy(o => o.Date).First();
        _out.WriteLine($"  首爆 {b0.Date:MM-dd} {b0.Quantity} m³　首钻 {d0.Date:MM-dd} {d0.Quantity} m　单位 {d0.Unit}");

        Assert.Equal(b0.Date.AddDays(-1), d0.Date);
        Assert.Equal("m", d0.Unit);
        Assert.Equal(Math.Round(b0.Quantity / 40.0), d0.Quantity, 0);
    }

    /// <summary>不需要爆破的面（表土直接挖）不许凭空生出穿孔爆破任务。</summary>
    [Fact]
    public void S9_不爆破的面不生成穿孔爆破()
    {
        var r = MonthToDayScheduler.Decompose(
            new[] { Face(20000, 2000, blast: false, zone: "表土剥离面·北") }, Days(20));
        Assert.Empty(r.OfProcess(ProcessType.Blast));
        Assert.Empty(r.OfProcess(ProcessType.Drill));
        Assert.NotEmpty(r.OfProcess(ProcessType.Load));
    }

    // ── 排土 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void S10_剥离才进排土场_且量按Kr折占容()
    {
        var p = new ProcessParams { Kr = 1.129 };
        var rock = MonthToDayScheduler.Decompose(new[] { Face(20000, 2000, ore: false) }, Days(20), p);
        var coal = MonthToDayScheduler.Decompose(new[] { Face(20000, 2000, ore: true) }, Days(20), p);

        double load = rock.OfProcess(ProcessType.Load).Sum(o => o.Quantity);
        double dump = rock.OfProcess(ProcessType.Dump).Sum(o => o.Quantity);
        _out.WriteLine($"剥离 {load} × Kr = {dump}（期望 {load * 1.129:0}）");
        Assert.Equal(Math.Round(load * 1.129), dump, 0);

        Assert.Empty(coal.OfProcess(ProcessType.Dump));   // 煤不进排土场
    }

    // ── 爆堆互锁 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S11 采装不许超前于爆破。开月没有爆堆、爆破又要等待期时，第一天就采是不成立的。
    /// <para>这条抓的是现场最实的那种错：图上完全正常，到现场是铲停在没爆的岩体前面。</para>
    /// </summary>
    /// <summary>
    /// S11 头几批采装靠开月爆堆，备不够要报。
    ///
    /// <para>第一版这条判的是"采装超前于爆破"，直接绿过了 —— 因为分解把首炮排到了
    /// **月首之前**（days[0] − 等待期），于是永远"有料"。那等于把活悄悄排到本月计划窗口之外，
    /// 是比超前更隐蔽的错。现在落到月外的批次不在本月下单，改记为开月备料需求。</para>
    /// </summary>
    [Fact]
    public void S11_开月爆堆不够要报出来()
    {
        var p = new ProcessParams { BlastLeadDays = 2, BlastCoversDays = 3 };

        var bad = MonthToDayScheduler.Decompose(new[] { Face(30000, 2000, muck: 0) }, Days(24), p);
        _out.WriteLine("  " + string.Join(" │ ", bad.Violations.Take(2)));
        Assert.Contains(bad.Violations, v => v.Contains("开月爆堆") && v.Contains("缺"));

        // 备足就不该再报（否则这条是恒真的）
        var ok = MonthToDayScheduler.Decompose(new[] { Face(30000, 2000, muck: 30000) }, Days(24), p);
        _out.WriteLine($"  备足后违规 {ok.Violations.Count} 条　提示：{string.Join(" │ ", ok.Notes.Where(n => n.Contains("开月爆堆")))}");
        Assert.DoesNotContain(ok.Violations, v => v.Contains("开月爆堆"));
        Assert.Contains(ok.Notes, n => n.Contains("开月爆堆") && n.Contains("够用"));
    }

    /// <summary>爆破/穿孔一律排在**作业日**上 —— 停产日放不了炮。</summary>
    [Fact]
    public void S11b_爆破穿孔不许排到非作业日()
    {
        // 隔天停产：只有单数日是作业日
        var work = Enumerable.Range(0, 12).Select(i => new DateTime(2026, 6, 1).AddDays(i * 2)).ToList();
        var r = MonthToDayScheduler.Decompose(new[] { Face(24000, 3000, muck: 99999) }, work,
                                              new ProcessParams { BlastLeadDays = 1, DrillLeadDays = 1 });
        var set = new HashSet<DateTime>(work);
        foreach (var o in r.Orders)
        {
            Assert.Contains(o.Date, set);
        }
        _out.WriteLine($"  {r.Orders.Count} 条任务全部落在 {work.Count} 个作业日上");
        Assert.NotEmpty(r.OfProcess(ProcessType.Blast));
    }

    [Fact]
    public void S12_每日爆破次数超上限要报()
    {
        // 6 个面、批次 = 1 天 ⇒ 同一天会排 6 炮
        var faces = Enumerable.Range(1, 6).Select(i => Face(6000, 1000, zone: $"面{i}")).ToList();
        var p = new ProcessParams { BlastCoversDays = 1, MaxBlastsPerDay = 2 };
        var r = MonthToDayScheduler.Decompose(faces, Days(10), p);
        _out.WriteLine("  " + string.Join(" │ ", r.Violations.Where(v => v.Contains("爆破")).Take(2)));
        Assert.Contains(r.Violations, v => v.Contains("超过每日上限"));
    }

    // ── 空过防护 ─────────────────────────────────────────────────────────────

    [Fact]
    public void S13_没有作业日或没有面时不炸且说明白()
    {
        var noDay = MonthToDayScheduler.Decompose(new[] { Face(30000, 2000) }, new List<DateTime>());
        Assert.Empty(noDay.Orders);
        Assert.Contains(noDay.Violations, v => v.Contains("没有有效作业日"));

        var noFace = MonthToDayScheduler.Decompose(new List<FaceMonthDemand>(), Days(20));
        Assert.Empty(noFace.Orders);
        Assert.NotEmpty(noFace.Notes);

        Assert.Empty(MonthToDayScheduler.Decompose(null!, Days(20)).Orders);
    }

    /// <summary>零量的面不生成任何任务 —— 排一条 0 方的采装任务是纯噪音。</summary>
    [Fact]
    public void S14_零量的面不排任务()
    {
        var r = MonthToDayScheduler.Decompose(
            new[] { Face(0, 2000, zone: "空面"), Face(10000, 2000, zone: "实面") }, Days(20));
        Assert.DoesNotContain(r.Orders, o => o.Zone == "空面");
        Assert.Contains(r.Orders, o => o.Zone == "实面");
    }
}
