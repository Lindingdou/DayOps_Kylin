// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ShiftCapacityBasisTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 逐班分解里"这一班能干多少"的口径（MD 组）。
///
/// <para>
/// <b>为什么单独钉一组</b>：2026-08-20 实测，一台电铲被排到<b>单班 40,736 m³</b>
/// （8 小时折 5,092 m³/h）。根因是 <see cref="PlanMachine.RatePerDay"/> 是**日**台效
/// （采装侧 = 编组班产 × 24h，钻机侧 = 台效台账的 M3PerDay），
/// 而折算分母写的是"标准班时 8" —— <b>每个班都排了一整天的活，一天三班就是三倍</b>。
/// </para>
/// <para>
/// 后果不是"数字大了点"：全月 381 万m³ 的采装在 <b>11 个作业日</b>里被排完，
/// 08-23 之后排了班却一条任务都没有，08-20 只剩最后一台电铲的 3 条 ——
/// 界面上看就是「甘特里只有一台电铲」。**没有任何一处报错**，
/// 每一行的量、每一班的时窗、总量守恒全都自洽。
/// </para>
/// <para>
/// 这一组不测绝对值，只测**比例关系**：三班之和 = 日台效、两班制只有 16/24、
/// 爆破班按扣完清场的小时数折。比例是口径，绝对值是台账。
/// </para>
/// </summary>
public sealed class ShiftCapacityBasisTests
{
    private readonly ITestOutputHelper _out;
    public ShiftCapacityBasisTests(ITestOutputHelper o) => _out = o;

    private const double RatePerDay = 24_000;    // 日台效 m³/日（= 1000 m³/h × 24h）

    /// <summary>一个大到吃不完的免爆单元 —— 让"能力"成为唯一的约束。</summary>
    private static PlanUnit BigUnit() => new()
    {
        UnitId = "U-1", FaceName = "算例面", MaterialCode = "", Seq = 1,
        RemainM3 = 10_000_000, NeedsBlasting = false, DrillM3 = 0, Rail = null,
    };

    private static PlanMachine Shovel(double ratePerDay = RatePerDay)
        => new("S-1", "算例铲", ProcessType.Load, ratePerDay);

    private static ShiftPlanResult Run(IReadOnlyList<PlanShift> shifts, double ratePerDay = RatePerDay)
    {
        var day = new DateTime(2026, 8, 20);
        return MonthlyShiftDecomposer.Decompose(
            "2026-08",
            new[] { BigUnit() },
            new[] { Shovel(ratePerDay) },
            new[] { day },
            _ => shifts);
    }

    private static PlanShift[] ThreeShifts => new[]
    {
        new PlanShift("早", 0, 8, false),
        new PlanShift("中", 8, 16, false),
        new PlanShift("夜", 16, 24, false),
    };

    /// <summary>MD1 三班（3×8h、无爆破）之和 = <b>一份</b>日台效，不是三份。</summary>
    [Fact]
    public void MD1_ThreeShifts_SumToOneDayRate()
    {
        var res = Run(ThreeShifts);
        double sum = res.Rows.Sum(r => r.VolumeM3);
        _out.WriteLine($"三班合计 {sum:N0} m³（日台效 {RatePerDay:N0}）");

        Assert.Equal(3, res.Rows.Count);
        Assert.Equal(RatePerDay, sum, 1);
        Assert.All(res.Rows, r => Assert.Equal(RatePerDay / 3, r.VolumeM3, 1));
    }

    /// <summary>MD2 两班制只干 16/24 —— 少上一个班就少一个班的量，不是照样干满一天。</summary>
    [Fact]
    public void MD2_TwoShifts_GetTwoThirdsOfDayRate()
    {
        var res = Run(new[]
        {
            new PlanShift("早", 0, 8, false),
            new PlanShift("中", 8, 16, false),
        });
        double sum = res.Rows.Sum(r => r.VolumeM3);
        _out.WriteLine($"两班合计 {sum:N0} m³");

        Assert.Equal(RatePerDay * 16.0 / 24.0, sum, 1);
    }

    /// <summary>
    /// MD3 爆破班按<b>扣完清场</b>的小时数折（8h − 1.5h 清场 = 6.5h）。
    /// <para>清场是真占时间的：那一班干不了满班的量。</para>
    /// </summary>
    [Fact]
    public void MD3_BlastShift_LosesClearingHours()
    {
        var res = Run(new[]
        {
            new PlanShift("早", 0, 8, true),     // 爆破班
            new PlanShift("中", 8, 16, false),
            new PlanShift("夜", 16, 24, false),
        });

        var blast = res.Rows.Single(r => r.Shift == "早");
        var normal = res.Rows.Single(r => r.Shift == "中");
        _out.WriteLine($"爆破班 {blast.VolumeM3:N0} · 常班 {normal.VolumeM3:N0}");

        Assert.Equal(RatePerDay * 6.5 / 24.0, blast.VolumeM3, 1);
        Assert.Equal(RatePerDay * 8.0 / 24.0, normal.VolumeM3, 1);
        Assert.True(blast.VolumeM3 < normal.VolumeM3);
    }

    /// <summary>
    /// MD4 <b>关闸自检</b>：台效为 0 = 不可派，一条都不排。
    /// <para>"没台效"不等于"能力无限" —— 拿 0 当无限，班表会排满而现场一铲都动不了。</para>
    /// </summary>
    [Fact]
    public void MD4_ZeroRate_SchedulesNothing()
    {
        var res = Run(ThreeShifts, ratePerDay: 0);
        Assert.Empty(res.Rows);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  逐日配额（Q 组，2026-08-20）
    //
    //  分解器原来是 **ASAP 排产**：每台设备每班顶格干到单元采完。
    //  实测后果 —— 全月的量压进前十几天，逐日条数单调衰减到 1 台，月末排了班却没有活。
    //  它不报错：量守恒、时窗合法、每一行都对，只是"这个月的活什么时候干"没人管过。
    //  现在多了一条约束：v = min(设备能力, 单元剩余, **当日剩余配额**)。
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Q1 量足够多时**摊平到每一天**，不再前重后轻。
    /// <para>断的是形态不是数值：最忙的一天 ≤ 最闲的一天的 1.05 倍，且**每一天都有活**。</para>
    /// </summary>
    [Fact]
    public void Q1_SpreadsAcrossAllWorkdays_NotFrontLoaded()
    {
        // 需求 = 10 天 × 日台效，能力刚好够 —— ASAP 会在前 10 天干完（后面的天全空）
        var days = Enumerable.Range(0, 20).Select(i => new DateTime(2026, 8, 1).AddDays(i)).ToList();
        var unit = BigUnit();
        unit.RemainM3 = RatePerDay * 10;

        var res = MonthlyShiftDecomposer.Decompose(
            "2026-08", new[] { unit }, new[] { Shovel() }, days, _ => ThreeShifts);

        var byDay = res.Rows.GroupBy(r => r.Date).ToDictionary(g => g.Key, g => g.Sum(r => r.VolumeM3));
        _out.WriteLine($"有活的天数 {byDay.Count}/20 · 最多 {byDay.Values.Max():N0} · 最少 {byDay.Values.Min():N0}");

        Assert.Equal(20, byDay.Count);                                  // 每一天都有活
        Assert.True(byDay.Values.Max() <= byDay.Values.Min() * 1.05 + 1,
            $"前重后轻：最忙 {byDay.Values.Max():N0}、最闲 {byDay.Values.Min():N0}");
        Assert.Equal(unit.RemainM3, res.Rows.Sum(r => r.VolumeM3), 0);   // 总量不变
    }

    /// <summary>Q2 外部配额说了算：给一个小配额，当天就只排这么多。</summary>
    [Fact]
    public void Q2_ExternalQuota_CapsTheDay()
    {
        var days = new List<DateTime> { new(2026, 8, 20), new(2026, 8, 21) };
        const double quota = 5_000;                                     // 远小于日台效 24,000

        var res = MonthlyShiftDecomposer.Decompose(
            "2026-08", new[] { BigUnit() }, new[] { Shovel() }, days, _ => ThreeShifts,
            loadQuotaOf: _ => quota);

        foreach (var g in res.Rows.GroupBy(r => r.Date))
        {
            _out.WriteLine($"{g.Key} {g.Sum(r => r.VolumeM3):N0} m³");
            Assert.Equal(quota, g.Sum(r => r.VolumeM3), 1);
        }
    }

    /// <summary>
    /// Q3 配额<b>只封顶、不保底</b>：能力不够就排不满，差额记进缺口。
    /// <para>反过来做（配额当目标硬塞）就是拿计划量冒充能力 —— 班表满满当当而现场干不出来。</para>
    /// </summary>
    [Fact]
    public void Q3_QuotaCapsOnly_NeverInflatesBeyondCapacity()
    {
        var days = new List<DateTime> { new(2026, 8, 20) };
        var res = MonthlyShiftDecomposer.Decompose(
            "2026-08", new[] { BigUnit() }, new[] { Shovel() }, days, _ => ThreeShifts,
            loadQuotaOf: _ => RatePerDay * 10);                         // 配额给到十倍能力

        Assert.Equal(RatePerDay, res.Rows.Sum(r => r.VolumeM3), 1);     // 仍然只干得了一天的量
    }

    /// <summary>
    /// Q4 配额<b>按班再分一次</b>：三个班都有活，不是早班把一天的量吃光。
    /// <para>只按天封顶时实测 6 台钻机全挤在早班 —— 甘特上就是"钻机只上早班"。</para>
    /// </summary>
    [Fact]
    public void Q4_QuotaIsSplitPerShift_NotEatenByTheFirstShift()
    {
        var days = new List<DateTime> { new(2026, 8, 20) };
        var res = MonthlyShiftDecomposer.Decompose(
            "2026-08", new[] { BigUnit() }, new[] { Shovel() }, days, _ => ThreeShifts,
            loadQuotaOf: _ => RatePerDay);

        var shifts = res.Rows.Select(r => r.Shift).Distinct().ToList();
        _out.WriteLine("有活的班：" + string.Join("/", shifts));
        Assert.Equal(3, shifts.Count);
        Assert.All(res.Rows, r => Assert.Equal(RatePerDay / 3, r.VolumeM3, 1));
    }

    /// <summary>
    /// MD6 <b>穿孔只排白班</b>（M10）：夜里没有视线、警戒与装药条件。
    /// <para>夜里排穿孔，班表看着满满当当而那几台钻机根本不出勤，
    /// 下游的"穿爆超前"就按一份不存在的进度在放行采装。</para>
    /// </summary>
    [Fact]
    public void MD6_Drilling_OnlyOnDayShift()
    {
        var unit = BigUnit();
        unit.NeedsBlasting = true;
        unit.DrillM3 = 10_000_000;

        var res = MonthlyShiftDecomposer.Decompose(
            "2026-08", new[] { unit },
            new[] { new PlanMachine("D-1", "算例钻机", ProcessType.Drill, RatePerDay) },
            new[] { new DateTime(2026, 8, 20) }, _ => ThreeShifts);

        var shifts = res.Rows.Where(r => r.Process == ProcessType.Drill).Select(r => r.Shift).Distinct().ToList();
        _out.WriteLine("穿孔排在：" + string.Join("/", shifts));

        Assert.NotEmpty(res.Rows);
        Assert.Equal(new[] { "中" }, shifts);        // 00–08 与 16–24 都不是白班
    }

    /// <summary>
    /// MD7 白班按<b>时窗</b>判，不按班名判。
    /// <para>换一套班制（四班三运转 / 交接不在整点）时按名字判会静默失效，
    /// 而失效的样子是"穿孔爆破排到夜里"，图上看不出异常。</para>
    /// </summary>
    [Fact]
    public void MD7_DayShift_IsDecidedByWindow_NotByName()
    {
        // 四班制，班名与常规完全不同
        var four = new[]
        {
            new PlanShift("甲", 0, 6, false),
            new PlanShift("乙", 6, 12, false),      // 全落在白天
            new PlanShift("丙", 12, 18, false),     // 全落在白天
            new PlanShift("丁", 18, 24, false),
        };
        var unit = BigUnit();
        unit.NeedsBlasting = true;
        unit.DrillM3 = 10_000_000;

        var res = MonthlyShiftDecomposer.Decompose(
            "2026-08", new[] { unit },
            new[] { new PlanMachine("D-1", "算例钻机", ProcessType.Drill, RatePerDay) },
            new[] { new DateTime(2026, 8, 20) }, _ => four);

        var shifts = res.Rows.Where(r => r.Process == ProcessType.Drill).Select(r => r.Shift).Distinct().OrderBy(x => x).ToList();
        _out.WriteLine("四班制下穿孔排在：" + string.Join("/", shifts));
        Assert.Equal(new[] { "丙", "乙" }, shifts);
    }

    /// <summary>
    /// MD8 只上白班的设备，<b>日台效不该被折掉</b>：折算分母是"这台设备当天能上的班"。
    /// <para>按 24h 折 ⇒ 白班只给 1/3 —— 台账里的日台效凭空少了 2/3，
    /// 而下游的穿爆超前照单全收，采装被挡掉一大片，看着像"设备不够"。</para>
    /// </summary>
    [Fact]
    public void MD8_DayShiftOnlyMachine_KeepsItsFullDailyRate()
    {
        var unit = BigUnit();
        unit.NeedsBlasting = true;
        unit.DrillM3 = 10_000_000;

        var res = MonthlyShiftDecomposer.Decompose(
            "2026-08", new[] { unit },
            new[] { new PlanMachine("D-1", "算例钻机", ProcessType.Drill, RatePerDay) },
            new[] { new DateTime(2026, 8, 20) }, _ => ThreeShifts);

        double drilled = res.Rows.Where(r => r.Process == ProcessType.Drill).Sum(r => r.VolumeM3);
        _out.WriteLine($"白班一天打了 {drilled:N0} m³（日台效 {RatePerDay:N0}）");
        Assert.Equal(RatePerDay, drilled, 1);
    }

    /// <summary>
    /// MD5 一天的量不受"月里还有多少天"影响 —— 能力是当天的事。
    /// <para>顺带证明 MD1 那个 1/3 不是被单元量或天数凑出来的。</para>
    /// </summary>
    [Fact]
    public void MD5_DayRate_IsIndependentOfHowManyDaysFollow()
    {
        var days = Enumerable.Range(0, 5).Select(i => new DateTime(2026, 8, 20).AddDays(i)).ToList();
        var res = MonthlyShiftDecomposer.Decompose(
            "2026-08", new[] { BigUnit() }, new[] { Shovel() }, days, _ => ThreeShifts);

        foreach (var g in res.Rows.GroupBy(r => r.Date))
        {
            _out.WriteLine($"{g.Key} {g.Sum(r => r.VolumeM3):N0} m³");
            Assert.Equal(RatePerDay, g.Sum(r => r.VolumeM3), 1);
        }
        Assert.Equal(5, res.Rows.Select(r => r.Date).Distinct().Count());
    }
}
