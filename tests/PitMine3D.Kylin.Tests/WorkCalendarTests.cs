using System;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 班次日历与作业日口径（§三三二，移植原 <c>TaskLib.Engine.WorkCalendar</c>）。
///
/// 盯的是原注释点名过的两条：**日历没数据时不许猜一个数出来**（该由调用方决定兜底），
/// 以及**"月→日"与"月→周"必须共用同一个除数**（两处各写一遍三层兜底，周计划与日计划
/// 迟早显示成两个数，而那种不一致最难查）。
/// </summary>
public class WorkCalendarTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d);

    // ── 班次名/码互转 ────────────────────────────────────────
    [Theory]
    [InlineData("A", "早班")]
    [InlineData("b", "中班")]
    [InlineData("C", "夜班")]
    [InlineData("", "班次")]
    [InlineData("D", "D")]        // 其它班制原样保留
    public void 班次码转名(string code, string name) => Assert.Equal(name, WorkCalendar.ShiftName(code));

    [Theory]
    [InlineData("早班", "A")]
    [InlineData("中班", "B")]
    [InlineData("夜班", "C")]
    [InlineData("大夜班", "大夜班")]   // 认不出就原样写回, 不猜
    [InlineData(null, "")]
    public void 班次名转码(string? name, string code) => Assert.Equal(code, WorkCalendar.ShiftCode(name));

    [Fact]
    public void 名码互转对得上()
    {
        foreach (var c in new[] { "A", "B", "C" })
            Assert.Equal(c, WorkCalendar.ShiftCode(WorkCalendar.ShiftName(c)));
    }

    // ── 整月生成 ────────────────────────────────────────────
    [Fact]
    public void 整月生成_每天三班()
    {
        var (rows, kept) = WorkCalendar.BuildMonth(D(2026, 2, 10), Array.Empty<ShiftCalendarRow>());
        Assert.Empty(kept);
        Assert.Equal(28 * 3, rows.Count);                       // 2026 年 2 月 28 天
        Assert.Equal(28, rows.Select(r => r.Date).Distinct().Count());
        Assert.All(rows.GroupBy(r => r.Date), g => Assert.Equal(new[] { "A", "B", "C" }, g.Select(x => x.Shift).OrderBy(x => x).ToArray()));
    }

    [Fact]
    public void 整月生成_闰年二月给29天()
    {
        var (rows, _) = WorkCalendar.BuildMonth(D(2028, 2, 1), Array.Empty<ShiftCalendarRow>());
        Assert.Equal(29 * 3, rows.Count);
    }

    [Fact]
    public void 整月生成_已排过的日子原样保留不覆盖()
    {
        // 排班表上人工改过的东西不能被"再生成一次"抹掉
        var existing = new[]
        {
            new ShiftCalendarRow { Date = D(2026, 3, 5), Shift = "A", LeaderName = "老张" },
            new ShiftCalendarRow { Date = D(2026, 3, 6), Shift = "B" },
        };
        var (rows, kept) = WorkCalendar.BuildMonth(D(2026, 3, 20), existing);
        Assert.Equal(2, kept.Count);
        Assert.Contains(D(2026, 3, 5), kept);
        Assert.DoesNotContain(rows, r => r.Date == D(2026, 3, 5));   // 那天一条都不再生成
        Assert.Equal((31 - 2) * 3, rows.Count);
    }

    [Fact]
    public void 整月生成_默认开班时刻按班次给()
    {
        var (rows, _) = WorkCalendar.BuildMonth(D(2026, 4, 1), Array.Empty<ShiftCalendarRow>());
        var day1 = rows.Where(r => r.Date == D(2026, 4, 1)).OrderBy(r => r.Shift).ToList();
        Assert.Equal("08:00", day1[0].StartTime);
        Assert.Equal("16:00", day1[1].StartTime);
        Assert.Equal("00:00", day1[2].StartTime);
    }

    [Fact]
    public void 整月生成_可自定班制()
    {
        var (rows, _) = WorkCalendar.BuildMonth(D(2026, 5, 1), Array.Empty<ShiftCalendarRow>(),
                                                shifts: new[] { "A", "B" }, startTimes: new[] { "07:00", "19:00" });
        Assert.Equal(31 * 2, rows.Count);
        Assert.All(rows, r => Assert.Contains(r.Shift, new[] { "A", "B" }));
    }

    // ── 作业日裁定（三层兜底）────────────────────────────────
    [Fact]
    public void 作业日_日历有数据时以日历为准()
    {
        using var db = TestDb.Open();
        // 造 22 天出勤
        for (int i = 1; i <= 22; i++)
            Assert.Equal("", WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow
            { Date = D(2026, 6, i), Shift = "A", StartTime = "08:00" }));

        var r = WorkCalendar.ResolveWorkdays(db.Connection, D(2026, 6, 15), planWorkdays: 25);
        Assert.Equal(WorkdaySource.Calendar, r.Source);
        Assert.Equal(22, r.Workdays);
        Assert.True(r.Disagrees, "日历 22 天与月计划 25 天对不上, 该点出来");
        Assert.Contains("日历口径 22 天", r.Basis);
        Assert.Contains("25", r.Basis);       // 把月计划口径也说出来
    }

    [Fact]
    public void 作业日_日历没数据时退月计划_不猜一个数()
    {
        using var db = TestDb.Open();
        var info = WorkCalendar.MonthWorkdays(db.Connection, D(2026, 7, 1));
        Assert.False(info.FromLedger);
        Assert.Equal(0, info.Workdays);        // 没有就是没有, 不许猜
        Assert.Contains("无班次日历记录", info.Label);

        var r = WorkCalendar.ResolveWorkdays(db.Connection, D(2026, 7, 1), planWorkdays: 26);
        Assert.Equal(WorkdaySource.MonthPlan, r.Source);
        Assert.Equal(26, r.Workdays);
    }

    [Fact]
    public void 作业日_日历与月计划都没有时才兜底25()
    {
        using var db = TestDb.Open();
        var r = WorkCalendar.ResolveWorkdays(db.Connection, D(2026, 8, 1), planWorkdays: 0);
        Assert.Equal(WorkdaySource.Fallback, r.Source);
        Assert.Equal(WorkCalendar.FallbackMonthWorkdays, r.Workdays);
        Assert.Contains("兜底", r.Basis);
    }

    [Fact]
    public void 作业日_月与周共用同一个除数()
    {
        // 同一个月问两次(模拟"月→日"与"月→周"两条路)必须得到同一个数 ——
        // 两处各写一遍三层兜底正是原注释点名要避免的
        using var db = TestDb.Open();
        for (int i = 1; i <= 20; i++)
            WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow { Date = D(2026, 9, i), Shift = "A", StartTime = "08:00" });

        var a = WorkCalendar.ResolveWorkdays(db.Connection, D(2026, 9, 3), 25);
        var b = WorkCalendar.ResolveWorkdays(db.Connection, D(2026, 9, 28), 25);
        Assert.Equal(a.Workdays, b.Workdays);
        Assert.Equal(a.Source, b.Source);
    }

    [Fact]
    public void 作业日_只数天数不数班次条数()
    {
        using var db = TestDb.Open();
        foreach (var sh in new[] { "A", "B", "C" })
            WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow { Date = D(2026, 10, 1), Shift = sh, StartTime = "08:00" });

        var info = WorkCalendar.MonthWorkdays(db.Connection, D(2026, 10, 1));
        Assert.Equal(1, info.Workdays);       // 一天三班仍是 1 个作业日
        Assert.Equal(3, info.ShiftRows);
    }

    [Fact]
    public void 作业日_没填开班时刻的天数要点出来()
    {
        // 装箱排不出时窗, 得让人知道是哪几天的问题
        using var db = TestDb.Open();
        WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow { Date = D(2026, 11, 1), Shift = "A", StartTime = "08:00" });
        WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow { Date = D(2026, 11, 2), Shift = "A", StartTime = null });

        var info = WorkCalendar.MonthWorkdays(db.Connection, D(2026, 11, 1));
        Assert.Equal(2, info.Workdays);
        Assert.Equal(1, info.DaysWithoutTime);
        Assert.Contains("未填开班时刻", info.Label);
    }

    // ── 读写 ────────────────────────────────────────────────
    [Fact]
    public void 写回再读_字段都对得上()
    {
        using var db = TestDb.Open();
        var row = new ShiftCalendarRow
        {
            Date = D(2026, 12, 3), Shift = "B", StartTime = "16:00",
            LeaderName = "李班长", IsBlastShift = true, Weather = "小雪", Notes = "备注里带'单引号'",
        };
        Assert.Equal("", WorkCalendar.Upsert(db.Connection, row));

        var back = WorkCalendar.Day(db.Connection, D(2026, 12, 3), out string err).Single();
        Assert.Equal("", err);
        Assert.Equal("B", back.Shift);
        Assert.Equal("16:00", back.StartTime);
        Assert.Equal("李班长", back.LeaderName);
        Assert.True(back.IsBlastShift);
        Assert.Equal("小雪", back.Weather);
        Assert.Equal("备注里带'单引号'", back.Notes);   // 单引号要能安全存回来
    }

    [Fact]
    public void 写回同一主键是更新而不是插重复()
    {
        using var db = TestDb.Open();
        var d = D(2026, 12, 10);
        WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow { Date = d, Shift = "A", LeaderName = "老张" });
        WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow { Date = d, Shift = "A", LeaderName = "老王" });

        var rows = WorkCalendar.Day(db.Connection, d, out _);
        Assert.Single(rows);
        Assert.Equal("老王", rows[0].LeaderName);
    }

    [Fact]
    public void 删除()
    {
        using var db = TestDb.Open();
        var d = D(2026, 12, 20);
        WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow { Date = d, Shift = "A" });
        Assert.Single(WorkCalendar.Day(db.Connection, d, out _));
        Assert.Equal("", WorkCalendar.Delete(db.Connection, d, "A"));
        Assert.Empty(WorkCalendar.Day(db.Connection, d, out _));
    }

    [Fact]
    public void 区间查含首尾()
    {
        using var db = TestDb.Open();
        for (int i = 1; i <= 5; i++)
            WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow { Date = D(2027, 1, i), Shift = "A" });

        var rows = WorkCalendar.InRange(db.Connection, D(2027, 1, 2), D(2027, 1, 4), out _);
        Assert.Equal(3, rows.Count);                       // 2、3、4 三天, 含首尾
        Assert.Equal(D(2027, 1, 2), rows.First().Date);
        Assert.Equal(D(2027, 1, 4), rows.Last().Date);
    }

    [Fact]
    public void 没有连接时给原因而不是崩()
    {
        Assert.Empty(WorkCalendar.Day(null!, DateTime.Today, out string e1));
        Assert.NotEmpty(e1);
        Assert.NotEmpty(WorkCalendar.Upsert(null!, new ShiftCalendarRow { Shift = "A" }));
        var r = WorkCalendar.ResolveWorkdays(null!, DateTime.Today, 0);
        Assert.Equal(WorkdaySource.Fallback, r.Source);   // 连不上库也要给得出一个能用的除数
    }

    [Fact]
    public void 空班次拒写()
        => Assert.Contains("班次", WorkCalendar.Upsert(TestDb.Open().Connection, new ShiftCalendarRow { Shift = "  " }));
}
