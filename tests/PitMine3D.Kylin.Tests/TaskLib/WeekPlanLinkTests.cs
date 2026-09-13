// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/WeekPlanLinkTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 月→周裂解的判据（W1–W9）。
///
/// <para>
/// 这组要挡的第一件事是<b>"逐日系数"回来</b>：老窗口的七行是
/// 当日引擎日产 × {0.96 1.0 1.02 0.98 1.03 0.90 0.62}，于是"周三比周二多 2%"这种数
/// 看着挺像回事，其实一个出处都没有。W3 让两个班数相同的日子必须给出**同一个**日均——
/// 任何按下标打折的实现都会红。
/// </para>
/// <para>
/// 第二件是<b>拿 0 冒充"没有"</b>：没排班的日子计划量是真 0（那天不出勤），
/// 但"本月日历一条记录都没有"是判不了（null）。两者显示成同一个 0，就再没人去补日历了。
/// W4/W6 分别钉住这两侧。
/// </para>
/// </summary>
public class WeekPlanLinkTests
{
    private static readonly DateTime Mon = new(2026, 8, 10);   // 周一
    private static readonly DateTime Today = new(2026, 8, 12); // 周三

    /// <summary>月基准：日采出 2000 m³ / 日剥离 5000 m³，作业日 25 天，日历里有记录。</summary>
    private static MonthBasis Basis(string key = "2026-08", double load = 2000, double dump = 5000)
        => new()
        {
            MonthKey = key, HasPlan = true, PlanLabel = "样例月计划 8月",
            Workdays = 25, WorkdayBasis = "日历口径 25 天", CalendarDays = 25,
            DayLoadM3 = load, DayDumpM3 = dump,
        };

    /// <summary>整周每天三班、有月计划、无实绩的基准盘。</summary>
    private static WeekPlanInputs Plate()
    {
        var inp = new WeekPlanInputs
        {
            Anchor = Today,
            Today = Today,
            Months = { ["2026-08"] = Basis() },
        };
        for (int i = 0; i < 7; i++) inp.ShiftsByDay[Mon.AddDays(i)] = 3;
        return inp;
    }

    // ── W1：本周由锚点日算，周一起 ────────────────────────────────────────────
    [Fact]
    public void W1_同一周内任取一天都落到同一个周一()
    {
        for (int i = 0; i < 7; i++)
            Assert.Equal(Mon, WeekPlanLink.MondayOf(Mon.AddDays(i)));

        Assert.Equal(Mon.AddDays(7), WeekPlanLink.MondayOf(Mon.AddDays(7)));
        Assert.Equal(Mon.AddDays(-7), WeekPlanLink.MondayOf(Mon.AddDays(-1)));   // 上周日属上一周
    }

    [Fact]
    public void W1b_七行且日期连续()
    {
        var r = WeekPlanLink.Compose(Plate());
        Assert.Equal(7, r.Days.Count);
        for (int i = 0; i < 7; i++) Assert.Equal(Mon.AddDays(i), r.Days[i].Date);
    }

    // ── W2：有效班取日历，不是写死的 3 ────────────────────────────────────────
    [Fact]
    public void W2_有效班逐日取日历()
    {
        var inp = Plate();
        inp.ShiftsByDay[Mon.AddDays(5)] = 2;    // 周六两班
        inp.ShiftsByDay.Remove(Mon.AddDays(6)); // 周日不排班

        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(3, r.Days[0].Shifts);
        Assert.Equal(2, r.Days[5].Shifts);
        Assert.Equal(0, r.Days[6].Shifts);
    }

    [Fact]
    public void W2b_日历读不通时班次判不了而不是零()
    {
        var inp = Plate();
        inp.CalendarUsable = false;
        inp.ShiftsByDay.Clear();

        var r = WeekPlanLink.Compose(inp);
        Assert.All(r.Days, d => Assert.Null(d.Shifts));
        Assert.All(r.Days.Where(d => !d.IsAnchor), d => Assert.Null(d.PlanLoadM3));
        Assert.Contains(r.Notes, n => n.Contains("班次日历读不通"));
    }

    // ── W3：日计划 = 月量 ÷ 作业日，**同班制的日子必须等值**（逐日系数在此处必红）──
    [Fact]
    public void W3_排了班的日子日均相同且等于月量除作业日()
    {
        var inp = Plate();
        inp.AnchorPlanLoadM3 = null;   // 让当日行也走月均，七天可直接互比

        var r = WeekPlanLink.Compose(inp);
        var vals = r.Days.Select(d => d.PlanLoadM3).ToList();
        Assert.All(vals, v => Assert.Equal(2000, v!.Value, 6));
        Assert.All(r.Days, d => Assert.Equal(5000, d.PlanDumpM3!.Value, 6));
        Assert.Equal(2000 * 7, r.PlanLoadSumM3, 6);
    }

    // ── W4：未排班 = 真 0；本月无日历 = 判不了 ────────────────────────────────
    [Fact]
    public void W4_未排班的日子计划为零且状态为未排班()
    {
        var inp = Plate();
        inp.AnchorPlanLoadM3 = null;
        inp.ShiftsByDay.Remove(Mon.AddDays(6));

        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(0, r.Days[6].PlanLoadM3);
        Assert.Equal("未排班", r.Days[6].Status);
        Assert.Equal(6, r.ScheduledDays);
        Assert.Equal(2000 * 6, r.PlanLoadSumM3, 6);   // 合计不含那一天
    }

    [Fact]
    public void W4b_本月日历一条都没有时是判不了不是零()
    {
        var inp = Plate();
        inp.AnchorPlanLoadM3 = null;
        inp.ShiftsByDay.Clear();                     // 全周都没有排班记录
        inp.Months["2026-08"] = Basis();
        inp.Months["2026-08"].CalendarDays = 0;      // 且本月一条记录也没有

        var r = WeekPlanLink.Compose(inp);
        Assert.All(r.Days, d => Assert.Null(d.PlanLoadM3));
        Assert.Equal(7, r.UnknownDays);
        Assert.Contains(r.Notes, n => n.Contains("班次日历没有记录"));
    }

    // ── W5：当日那一行取盘子实数，且标明出处 ───────────────────────────────────
    [Fact]
    public void W5_当日行取当日盘子而不是月均()
    {
        var inp = Plate();
        inp.AnchorPlanLoadM3 = 3333;
        inp.AnchorPlanDumpM3 = 7777;

        var r = WeekPlanLink.Compose(inp);
        var anchor = r.Days.Single(d => d.IsAnchor);
        Assert.Equal(3333, anchor.PlanLoadM3);
        Assert.Equal(7777, anchor.PlanDumpM3);
        Assert.Contains("盘子", anchor.Basis);

        // 其余日仍是月均——不许被当日实数带偏
        Assert.All(r.Days.Where(d => !d.IsAnchor), d => Assert.Equal(2000, d.PlanLoadM3!.Value, 6));
    }

    // ── W6：实绩没录就是 null，达成度不给数 ───────────────────────────────────
    [Fact]
    public void W6_没录实绩不写零达成度也不给()
    {
        var r = WeekPlanLink.Compose(Plate());
        Assert.All(r.Days, d =>
        {
            Assert.Null(d.ActualLoadM3);
            Assert.Null(d.AttainmentPct);
        });
    }

    [Fact]
    public void W6b_录了实绩才算达成度()
    {
        var inp = Plate();
        inp.AnchorPlanLoadM3 = null;
        inp.ActualsByDay[Mon] = (1900, 4000);

        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(95, r.Days[0].AttainmentPct!.Value, 6);
        Assert.Null(r.Days[1].AttainmentPct);
    }

    // ── W7：状态由事实推 ──────────────────────────────────────────────────────
    [Fact]
    public void W7_状态按日期与实绩推出来()
    {
        var inp = Plate();
        inp.AnchorPlanLoadM3 = null;
        inp.ActualsByDay[Mon] = (1990, 4000);          // 周一 99.5% → 已完成
        inp.ActualsByDay[Mon.AddDays(1)] = (900, 0);   // 周二 45%   → 部分完成

        var r = WeekPlanLink.Compose(inp);
        Assert.Equal("已完成", r.Days[0].Status);
        Assert.Equal("部分完成", r.Days[1].Status);
        Assert.Equal("执行中", r.Days[2].Status);       // 今天
        Assert.Equal("计划", r.Days[3].Status);         // 未来
    }

    [Fact]
    public void W7b_过去的日子没实绩要点名而不是显示已完成()
    {
        var inp = Plate();
        inp.AnchorPlanLoadM3 = null;
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal("无实绩录入", r.Days[0].Status);
        Assert.Equal("无实绩录入", r.Days[1].Status);
    }

    // ── W8：跨月的周逐日按各自月份摊 ──────────────────────────────────────────
    [Fact]
    public void W8_跨月周两侧各按各的月计划()
    {
        // 2026-08-31 是周一，本周跨到 09-06
        var aug31 = new DateTime(2026, 8, 31);
        var inp = new WeekPlanInputs
        {
            Anchor = aug31,
            Today = aug31,
            AnchorPlanLoadM3 = null,
            Months =
            {
                ["2026-08"] = Basis("2026-08", 2000, 5000),
                ["2026-09"] = Basis("2026-09", 3000, 6000),
            },
        };
        for (int i = 0; i < 7; i++) inp.ShiftsByDay[aug31.AddDays(i)] = 3;

        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(2000, r.Days[0].PlanLoadM3!.Value, 6);   // 08-31
        Assert.Equal(3000, r.Days[1].PlanLoadM3!.Value, 6);   // 09-01
        Assert.Contains(r.Notes, n => n.Contains("跨月"));
        Assert.Null(r.MonthSharePct);                          // 跨月不给"占本月"这个数
    }

    // ── W9：没有月计划时判不了，不拿任何常数顶 ────────────────────────────────
    [Fact]
    public void W9_无月计划时计划量判不了()
    {
        var inp = Plate();
        inp.AnchorPlanLoadM3 = null;
        inp.Months["2026-08"] = new MonthBasis { MonthKey = "2026-08", CalendarDays = 25 };  // HasPlan=false

        var r = WeekPlanLink.Compose(inp);
        Assert.All(r.Days, d => Assert.Null(d.PlanLoadM3));
        Assert.All(r.Days, d => Assert.Contains("判不了", d.Basis));
        Assert.Equal(0, r.PlanLoadSumM3);
        Assert.Equal(7, r.UnknownDays);
    }
}
