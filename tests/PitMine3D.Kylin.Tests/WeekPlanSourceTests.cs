using System;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 周计划取数壳（§三三六）：把 Kylin 自己的 <c>shift_calendar</c> / <c>monthly_plan</c> /
/// <c>daily_mine_summary</c> 凑成 <see cref="WeekPlanLink.Compose"/> 的输入。
///
/// 纪律照搬原版：**任何一处取不到都只降级为"判不了"，不抛、不造数**。
/// 全部走 SQLite 测试库，不碰真库。
/// </summary>
[Collection("LicenseTimeGuard")]   // 与其它写测试库的用例错开，避免同一临时库互相干扰
public class WeekPlanSourceTests
{
    private static readonly DateTime Wed = new(2026, 9, 9);
    private static DateTime Mon => new(2026, 9, 7);

    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void SeedMonthPlan(DbConnection c, int y, int m, double coalWanT, double stripWanM3)
    {
        Exec(c, $"DELETE FROM monthly_plan WHERE year={y} AND month={m}");
        Exec(c, $"INSERT INTO monthly_plan (year, month, plan_strip_wan_m3, plan_coal_wan_t) VALUES ({y},{m},{stripWanM3},{coalWanT})");
    }

    private static void SeedShifts(DbConnection c, DateTime from, int days, int perDay = 3)
    {
        for (int i = 0; i < days; i++)
            for (int k = 0; k < perDay; k++)
                WorkCalendar.Upsert(c, new ShiftCalendarRow
                { Date = from.AddDays(i), Shift = "ABC"[k].ToString(), StartTime = "08:00" });
    }

    // ── 月基准 ──────────────────────────────────────────────
    [Fact]
    public void 月基准_日量等于月量除作业日且煤按密度折方()
    {
        using var db = TestDb.Open();
        SeedShifts(db.Connection, new DateTime(2026, 9, 1), 30);      // 30 个作业日
        SeedMonthPlan(db.Connection, 2026, 9, coalWanT: 30, stripWanM3: 60);

        var b = WeekPlanSource.MonthBasisOf(db.Connection, "2026-09");
        Assert.True(b.HasPlan);
        Assert.True(b.Usable);
        Assert.Equal(30, b.Workdays, 6);
        Assert.Equal(30 * 1e4 / WeekPlanSource.CoalDensity / 30, b.DayLoadM3, 3);   // 煤 t → m³ 实方
        Assert.Equal(60 * 1e4 / 30, b.DayDumpM3, 3);
        Assert.Contains("日历口径", b.WorkdayBasis);
    }

    [Fact]
    public void 月基准_没有月计划时不可用()
    {
        using var db = TestDb.Open();
        SeedShifts(db.Connection, new DateTime(2026, 9, 1), 30);
        var b = WeekPlanSource.MonthBasisOf(db.Connection, "2026-09");
        Assert.False(b.HasPlan);
        Assert.False(b.Usable);
    }

    [Fact]
    public void 月基准_没有班次日历时退兜底除数()
    {
        using var db = TestDb.Open();
        SeedMonthPlan(db.Connection, 2026, 9, 30, 60);
        var b = WeekPlanSource.MonthBasisOf(db.Connection, "2026-09");
        Assert.True(b.HasPlan);
        Assert.Equal(WorkCalendar.FallbackMonthWorkdays, b.Workdays, 6);
        Assert.Equal(0, b.CalendarDays);        // 日历一条都没有 → Compose 那边按"判不了"处理未排班日
        Assert.Contains("兜底", b.WorkdayBasis);
    }

    [Fact]
    public void 月基准_月份键非法时不崩()
    {
        using var db = TestDb.Open();
        foreach (var k in new[] { "", "2026", "2026-13", "乱写" })
            Assert.False(WeekPlanSource.MonthBasisOf(db.Connection, k).HasPlan);
    }

    [Fact]
    public void 月基准_没有连接时不崩()
        => Assert.False(WeekPlanSource.MonthBasisOf(null, "2026-09").HasPlan);

    // ── 实绩 ────────────────────────────────────────────────
    [Fact]
    public void 实绩_六路煤相加并折方_剥离直取()
    {
        using var db = TestDb.Open();
        Exec(db.Connection,
            "INSERT INTO daily_mine_summary (date, big_belt_coal_t, small_belt_coal_t, longhua_coal_t, "
          + "truck_coal_export_t, winnowed_coal_t, big_truck_pile_coal_t, stripping_total_m3) "
          + "VALUES ('2026-09-08', 100, 200, 300, 400, 500, 600, 9999)");

        var map = WeekPlanSource.ActualsInRange(db.Connection, Mon, Mon.AddDays(6));
        var v = map[new DateTime(2026, 9, 8)];
        Assert.Equal(2100 / WeekPlanSource.CoalDensity, v.Load, 3);   // 六路合计 2100 t → m³
        Assert.Equal(9999, v.Dump, 6);
    }

    [Fact]
    public void 实绩_没记录的日子不出现在字典里()
    {
        // "没录"与"录了 0"必须分开 —— 字典里没有这个键, Compose 才会显示"—"
        using var db = TestDb.Open();
        Exec(db.Connection, "INSERT INTO daily_mine_summary (date, big_belt_coal_t) VALUES ('2026-09-08', 10)");
        var map = WeekPlanSource.ActualsInRange(db.Connection, Mon, Mon.AddDays(6));
        Assert.True(map.ContainsKey(new DateTime(2026, 9, 8)));
        Assert.False(map.ContainsKey(new DateTime(2026, 9, 9)));
    }

    [Fact]
    public void 实绩_只取区间内的()
    {
        using var db = TestDb.Open();
        Exec(db.Connection, "INSERT INTO daily_mine_summary (date, big_belt_coal_t) VALUES ('2026-09-06', 10)");
        Exec(db.Connection, "INSERT INTO daily_mine_summary (date, big_belt_coal_t) VALUES ('2026-09-14', 10)");
        Assert.Empty(WeekPlanSource.ActualsInRange(db.Connection, Mon, Mon.AddDays(6)));
    }

    [Fact]
    public void 实绩_口径文案把所用列名写出来()
    {
        // 让看数的人能一眼核对"是不是把某两路重复算了"
        string t = WeekPlanSource.CoalBasisText;
        foreach (var c in WeekPlanSource.CoalColumns) Assert.Contains(c, t);
        Assert.Contains("筒仓", t);          // 说明筒仓存量不计
    }

    // ── 端到端 ──────────────────────────────────────────────
    [Fact]
    public void 端到端_取数后算出一周七行()
    {
        using var db = TestDb.Open();
        SeedShifts(db.Connection, new DateTime(2026, 9, 1), 30);
        SeedMonthPlan(db.Connection, 2026, 9, 30, 60);
        Exec(db.Connection, "INSERT INTO daily_mine_summary (date, big_belt_coal_t, stripping_total_m3) VALUES ('2026-09-07', 74, 20000)");

        var inp = WeekPlanSource.Load(db.Connection, Wed, Wed);
        var r = WeekPlanLink.Compose(inp);

        Assert.Equal(7, r.Days.Count);
        Assert.All(r.Days, d => Assert.Equal(3, d.Shifts));            // 每天三班
        Assert.All(r.Days, d => Assert.NotNull(d.PlanLoadM3));         // 有月计划 + 有日历 → 都判得出
        Assert.Equal(0, r.UnknownDays);
        Assert.NotNull(r.Days[0].ActualLoadM3);                        // 周一录了实绩
        Assert.Null(r.Days[1].ActualLoadM3);                           // 周二没录
        Assert.NotNull(r.MonthSharePct);                               // 同月且基准可用
    }

    [Fact]
    public void 端到端_没有连接时全周判不了而不是崩()
    {
        var inp = WeekPlanSource.Load(null, Wed, Wed);
        var r = WeekPlanLink.Compose(inp);
        Assert.Equal(7, r.Days.Count);
        Assert.All(r.Days, d => Assert.Null(d.Shifts));
        Assert.All(r.Days, d => Assert.Null(d.PlanLoadM3));
        Assert.Contains(r.Notes, n => n.Contains("班次日历读不通"));
    }

    [Fact]
    public void 端到端_跨月的周各取各月的基准()
    {
        using var db = TestDb.Open();
        SeedShifts(db.Connection, new DateTime(2026, 9, 1), 30);
        SeedShifts(db.Connection, new DateTime(2026, 10, 1), 31);
        SeedMonthPlan(db.Connection, 2026, 9, 30, 60);
        SeedMonthPlan(db.Connection, 2026, 10, 62, 124);

        var inp = WeekPlanSource.Load(db.Connection, new DateTime(2026, 9, 30), new DateTime(2026, 9, 30));
        Assert.Equal(2, inp.Months.Count);
        var r = WeekPlanLink.Compose(inp);
        Assert.Contains(r.Notes, n => n.Contains("跨月"));
        Assert.NotEqual(r.Days[0].PlanLoadM3, r.Days[6].PlanLoadM3);   // 9 月与 10 月日均不同
    }
}
