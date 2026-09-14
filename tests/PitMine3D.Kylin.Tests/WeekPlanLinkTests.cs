using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 月→周裂解口径 W1–W8（§三三五，移植原 <c>TaskLib.Engine.WeekPlanLink.Compose</c>）。
///
/// 这组口径的共同主张是**"判不了"与"0"必须分开**：没排班是 0（那正是作业日的定义），
/// 日历读不通是判不了；实绩没录是"—"不是 0。混成一个数，合计就会悄悄偏小而没人发现。
/// </summary>
public class WeekPlanLinkTests
{
    private static readonly DateTime Wed = new(2026, 9, 9);    // 周三
    private static DateTime Mon => new(2026, 9, 7);

    private static MonthBasis Basis(double dayLoad = 1000, double dayDump = 3000, int calDays = 30, double workdays = 30)
        => new()
        {
            MonthKey = "2026-09", HasPlan = true, PlanLabel = "短期计划A · 9月",
            Workdays = workdays, WorkdayBasis = $"日历口径 {workdays:0} 天",
            CalendarDays = calDays, DayLoadM3 = dayLoad, DayDumpM3 = dayDump,
        };

    private static WeekPlanInputs Inputs(bool calendarUsable = true, int shiftsPerDay = 3)
    {
        var inp = new WeekPlanInputs { Anchor = Wed, Today = Wed, CalendarUsable = calendarUsable };
        for (int i = 0; i < 7; i++) inp.ShiftsByDay[Mon.AddDays(i)] = shiftsPerDay;
        inp.Months["2026-09"] = Basis();
        return inp;
    }

    // ── W1 本周 ─────────────────────────────────────────────
    [Theory]
    [InlineData(2026, 9, 7)]    // 周一
    [InlineData(2026, 9, 9)]    // 周三
    [InlineData(2026, 9, 13)]   // 周日
    public void W1_自然周周一起(int y, int m, int d)
        => Assert.Equal(new DateTime(2026, 9, 7), WeekPlanLink.MondayOf(new DateTime(y, m, d)));

    [Fact]
    public void W1_一周七行且首行是周一()
    {
        var r = WeekPlanLink.Compose(Inputs());
        Assert.Equal(7, r.Days.Count);
        Assert.Equal(Mon, r.Days[0].Date);
        Assert.Equal(Mon.AddDays(6), r.Sunday);
        Assert.StartsWith("周一", r.Days[0].DayLabel);
    }

    // ── W2 有效班 ───────────────────────────────────────────
    [Fact]
    public void W2_班次数取日历记录数()
    {
        var inp = Inputs();
        inp.ShiftsByDay[Mon] = 2;
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(2, r.Days[0].Shifts);
        Assert.True(r.Days[0].Scheduled);
    }

    [Fact]
    public void W2_日历读不通时是判不了而不是0班()
    {
        // "不拿每天三班顶" —— 也不能显示成 0 班(那等于说全周停产)
        var r = WeekPlanLink.Compose(Inputs(calendarUsable: false));
        Assert.All(r.Days, d => Assert.Null(d.Shifts));
        Assert.All(r.Days, d => Assert.Null(d.PlanLoadM3));
        Assert.All(r.Days, d => Assert.Contains("判不了", d.Basis));
        Assert.Contains(r.Notes, n => n.Contains("班次日历读不通"));
    }

    // ── W3 日计划 ───────────────────────────────────────────
    [Fact]
    public void W3_日计划等于月量除作业日()
    {
        var r = WeekPlanLink.Compose(Inputs());
        Assert.All(r.Days, d => Assert.Equal(1000, d.PlanLoadM3!.Value, 6));
        Assert.All(r.Days, d => Assert.Equal(3000, d.PlanDumpM3!.Value, 6));
        Assert.Contains("月计划日均", r.Days[0].Basis);
        Assert.Contains("÷30", r.Days[0].Basis.Replace(" ", ""));
    }

    [Fact]
    public void W3_无月计划时判不了()
    {
        var inp = Inputs();
        inp.Months["2026-09"] = new MonthBasis { MonthKey = "2026-09", CalendarDays = 30 };   // HasPlan=false
        var r = WeekPlanLink.Compose(inp);
        Assert.All(r.Days, d => Assert.Null(d.PlanLoadM3));
        Assert.All(r.Days, d => Assert.Contains("无月计划", d.Basis));
    }

    // ── W4 未排班 = 0 ───────────────────────────────────────
    [Fact]
    public void W4_未排班的日子计划量是0()
    {
        var inp = Inputs();
        inp.ShiftsByDay[Mon.AddDays(6)] = 0;      // 周日不出勤
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(0, r.Days[6].PlanLoadM3!.Value, 6);
        Assert.Contains("未排班", r.Days[6].Basis);
        Assert.Equal("未排班", r.Days[6].Status);
    }

    [Fact]
    public void W4_本月日历一条记录都没有时是判不了而不是0()
    {
        // 只有"本月日历确实有记录"才敢说某天未排班 = 0
        var inp = Inputs();
        inp.ShiftsByDay.Clear();
        inp.Months["2026-09"] = Basis(calDays: 0);
        var r = WeekPlanLink.Compose(inp);
        Assert.All(r.Days, d => Assert.Null(d.PlanLoadM3));
        Assert.All(r.Days, d => Assert.Contains("本月日历无记录", d.Basis));
        Assert.Contains(r.Notes, n => n.Contains("班次日历没有记录"));
    }

    // ── W5 当日行取盘子实数 ─────────────────────────────────
    [Fact]
    public void W5_当日行优先取盘子实数()
    {
        var inp = Inputs();
        inp.AnchorPlanLoadM3 = 1234;
        inp.AnchorPlanDumpM3 = 5678;
        inp.AnchorBasis = "当日盘子";
        var r = WeekPlanLink.Compose(inp);
        var anchor = r.Days.Single(d => d.IsAnchor);
        Assert.Equal(1234, anchor.PlanLoadM3!.Value, 6);
        Assert.Equal("当日盘子", anchor.Basis);
        // 其余各天仍走月均
        Assert.All(r.Days.Where(d => !d.IsAnchor), d => Assert.Equal(1000, d.PlanLoadM3!.Value, 6));
    }

    [Fact]
    public void W5_没有盘子时当日行退回月均()
    {
        // Kylin 侧暂无装箱盘子, 传 null 应自动退回 —— 算法一行不改
        var r = WeekPlanLink.Compose(Inputs());
        var anchor = r.Days.Single(d => d.IsAnchor);
        Assert.Equal(1000, anchor.PlanLoadM3!.Value, 6);
        Assert.Contains("月计划日均", anchor.Basis);
    }

    // ── W6 实绩 / W7 达成度 ─────────────────────────────────
    [Fact]
    public void W6_没录实绩是null不是0()
    {
        var r = WeekPlanLink.Compose(Inputs());
        Assert.All(r.Days, d => Assert.Null(d.ActualLoadM3));
    }

    [Fact]
    public void W7_达成度要计划与实绩都有才算()
    {
        var inp = Inputs();
        inp.ActualsByDay[Mon] = (900, 2700);
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(90, r.Days[0].AttainmentPct!.Value, 6);
        Assert.Null(r.Days[1].AttainmentPct);          // 没实绩 → 不给这个数
    }

    [Fact]
    public void W7_计划为0时不给达成度()
    {
        // 未排班那天计划是 0, 除下去会得 ∞
        var inp = Inputs();
        inp.ShiftsByDay[Mon] = 0;
        inp.ActualsByDay[Mon] = (100, 100);
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(0, r.Days[0].PlanLoadM3!.Value, 6);
        Assert.Null(r.Days[0].AttainmentPct);
    }

    // ── W8 状态由事实推 ─────────────────────────────────────
    [Fact]
    public void W8_今天是执行中()
        => Assert.Equal("执行中", WeekPlanLink.Compose(Inputs()).Days.Single(d => d.Date == Wed).Status);

    [Fact]
    public void W8_过去没录实绩是无实绩录入()
        => Assert.Equal("无实绩录入", WeekPlanLink.Compose(Inputs()).Days[0].Status);   // 周一 < 周三

    [Fact]
    public void W8_将来是计划()
        => Assert.Equal("计划", WeekPlanLink.Compose(Inputs()).Days[6].Status);          // 周日 > 周三

    [Fact]
    public void W8_达成98以上算已完成_不足算部分完成()
    {
        var inp = Inputs();
        inp.ActualsByDay[Mon] = (1000, 3000);          // 100%
        inp.ActualsByDay[Mon.AddDays(1)] = (500, 1500); // 50%
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal("已完成", r.Days[0].Status);
        Assert.Equal("部分完成", r.Days[1].Status);
    }

    [Fact]
    public void W8_日期没到却录了实绩要点出来()
    {
        var inp = Inputs();
        inp.ActualsByDay[Mon.AddDays(6)] = (100, 100);   // 周日还没到
        var r = WeekPlanLink.Compose(inp);
        Assert.Contains("日期未到", r.Days[6].Status);
    }

    // ── 合计与跨月 ──────────────────────────────────────────
    [Fact]
    public void 合计只累判得出的那几天_并说出漏了几天()
    {
        var inp = Inputs(calendarUsable: true);
        inp.Months["2026-09"] = Basis(calDays: 0);        // 本月日历无记录 → 全周判不了
        inp.ShiftsByDay.Clear();
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(0, r.PlanLoadSumM3, 6);
        Assert.Equal(7, r.UnknownDays);
        Assert.Contains(r.Notes, n => n.Contains("未计入"));
    }

    [Fact]
    public void 跨月的周按各自月份摊_并给出提示()
    {
        // 2026-09-28(周一) 那周跨到 10 月
        var inp = new WeekPlanInputs { Anchor = new DateTime(2026, 9, 30), Today = new DateTime(2026, 9, 30) };
        for (int i = 0; i < 7; i++) inp.ShiftsByDay[new DateTime(2026, 9, 28).AddDays(i)] = 3;
        inp.Months["2026-09"] = Basis(dayLoad: 1000);
        inp.Months["2026-10"] = new MonthBasis
        {
            MonthKey = "2026-10", HasPlan = true, PlanLabel = "10月", Workdays = 31,
            CalendarDays = 31, DayLoadM3 = 2000, DayDumpM3 = 6000,
        };
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(1000, r.Days[0].PlanLoadM3!.Value, 6);      // 9-28 走 9 月
        Assert.Equal(2000, r.Days[6].PlanLoadM3!.Value, 6);      // 10-04 走 10 月
        Assert.Contains(r.Notes, n => n.Contains("跨月"));
        Assert.Null(r.MonthSharePct);                             // 跨月不给占比
    }

    [Fact]
    public void 同月且基准可用时才给本周占月比()
    {
        var r = WeekPlanLink.Compose(Inputs());
        // 7 天 × 1000 ÷ (1000 × 30) = 23.33%
        Assert.NotNull(r.MonthSharePct);
        Assert.Equal(7000.0 / 30000 * 100, r.MonthSharePct!.Value, 6);
    }

    [Fact]
    public void 抬头带周区间与月计划口径()
    {
        var r = WeekPlanLink.Compose(Inputs());
        Assert.Contains("2026-09-07", r.Header);
        Assert.Contains("短期计划A", r.Header);
        Assert.Contains("日历口径", r.Header);
    }
}
