using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 检修档期（§三三三，移植原 <c>TaskLib.Features.MaintenancePlanWindow</c> 的数据与判定部分）。
///
/// 盯的是原注释点名的两条：**跨零点必须拆两条**（装箱时窗是同一天内的 [起,止)，绕回 0 点会把次日的活
/// 算进今天），以及**时刻非法要如实报**（引擎会丢弃这类记录，界面不标人就只会觉得"填了没生效"）。
/// 全部用 SQLite 测试库，**不碰真库**。
/// </summary>
public class MaintenanceWindowTests
{
    // ── 时刻解析 ────────────────────────────────────────────
    [Theory]
    [InlineData("08:00", 8)]
    [InlineData("08:30", 8.5)]
    [InlineData("00:00", 0)]
    [InlineData("24:00", 24)]
    [InlineData("8", 8)]           // 光写小时数也认
    [InlineData("22.5", 22.5)]
    public void 时刻解析(string text, double h) => Assert.Equal(h, MaintenanceWindows.Hour(text)!.Value, 6);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("下午三点")]
    [InlineData("25:00")]          // 越界不猜
    [InlineData("-1")]
    public void 时刻解析不了就返回null(string? text) => Assert.Null(MaintenanceWindows.Hour(text));

    [Fact]
    public void 小时数转文本_24点不绕回0点()
    {
        Assert.Equal("08:00", MaintenanceWindows.HourText(8));
        Assert.Equal("08:30", MaintenanceWindows.HourText(8.5));
        Assert.Equal("24:00", MaintenanceWindows.HourText(24));   // 绕回 00:00 就成了次日
        Assert.Equal("00:00", MaintenanceWindows.HourText(0));
    }

    // ── 校验（本组最要紧的一条）──────────────────────────────
    [Fact]
    public void 合法档期通过()
        => Assert.Null(MaintenanceWindows.Validate("08:00", "12:00"));

    [Fact]
    public void 跨零点被拒并说清怎么拆()
    {
        // 22:00–02:00 绕回 0 点, 会把次日的活算进今天
        var why = MaintenanceWindows.Validate("22:00", "02:00");
        Assert.NotNull(why);
        Assert.Contains("拆两条", why);
        Assert.Contains("22:00", why);      // 把该怎么写直接给出来
    }

    [Fact]
    public void 止等于起也被拒()
        => Assert.NotNull(MaintenanceWindows.Validate("08:00", "08:00"));

    [Fact]
    public void 时刻非法时给的是格式提示而不是先后提示()
    {
        var why = MaintenanceWindows.Validate("上午", "12:00");
        Assert.NotNull(why);
        Assert.Contains("HH:mm", why);
    }

    // ── 影响班次 ────────────────────────────────────────────
    private static List<ShiftCalendarRow> ThreeShifts() => new()
    {
        new ShiftCalendarRow { Shift = "A", StartTime = "08:00" },
        new ShiftCalendarRow { Shift = "B", StartTime = "16:00" },
        new ShiftCalendarRow { Shift = "C", StartTime = "00:00" },
    };

    [Fact]
    public void 班次时窗_每班到下一班开班为止()
    {
        var w = MaintenanceWindows.ShiftWindowsOf(ThreeShifts());
        Assert.Equal(3, w.Count);
        Assert.Equal(new ShiftWindow("夜班", 0, 8), w[0]);      // 00:00 → 08:00
        Assert.Equal(new ShiftWindow("早班", 8, 16), w[1]);
        Assert.Equal(new ShiftWindow("中班", 16, 24), w[2]);    // 最后一班到 24:00
    }

    [Fact]
    public void 班次时窗_没填开班时刻的班直接跳过()
    {
        // 不按 0 点算 —— 那会凭空造出一个从午夜开始的班
        var rows = ThreeShifts();
        rows[2].StartTime = null;
        var w = MaintenanceWindows.ShiftWindowsOf(rows);
        Assert.Equal(2, w.Count);
        Assert.DoesNotContain(w, x => x.Name == "夜班");
    }

    [Fact]
    public void 班次时窗_空表回空而不是崩()
    {
        Assert.Empty(MaintenanceWindows.ShiftWindowsOf(new List<ShiftCalendarRow>()));
        Assert.Empty(MaintenanceWindows.ShiftWindowsOf(null!));
    }

    [Fact]
    public void 压到哪几个班()
    {
        var w = MaintenanceWindows.ShiftWindowsOf(ThreeShifts());
        Assert.Equal("早班", MaintenanceWindows.Overlap(w, 9, 12));            // 全在早班内
        Assert.Equal("早班 / 中班", MaintenanceWindows.Overlap(w, 15, 17));    // 跨两班
        Assert.Equal("夜班", MaintenanceWindows.Overlap(w, 0, 4));
    }

    [Fact]
    public void 边界相接不算压上()
    {
        // 早班 [8,16): 8:00 结束的检修不该算压早班; 16:00 开始的也不该
        var w = MaintenanceWindows.ShiftWindowsOf(ThreeShifts());
        Assert.DoesNotContain("早班", MaintenanceWindows.Overlap(w, 6, 8));
        Assert.DoesNotContain("早班", MaintenanceWindows.Overlap(w, 16, 18));
    }

    [Fact]
    public void 没有班次日历时如实说而不是说不压任何班()
    {
        // "不压任何班次"会让人以为排查过了; 实际是根本没日历可比
        var s = MaintenanceWindows.Overlap(new List<ShiftWindow>(), 9, 12);
        Assert.Contains("无班次日历", s);
    }

    // ── 读写（SQLite 测试库）────────────────────────────────
    [Fact]
    public void 写回再读_字段都对得上()
    {
        using var db = TestDb.Open();
        var row = new MaintenanceWindowRow
        {
            EquipmentId = "WD-01", PlanDate = "2026-09-20", StartTime = "08:00", EndTime = "12:00",
            Kind = "定修", Note = "换斗齿'含单引号'",
        };
        Assert.Equal("", MaintenanceWindows.Upsert(db.Connection, row));

        var back = MaintenanceWindows.ByDate(db.Connection, new DateTime(2026, 9, 20), out string err).Single();
        Assert.Equal("", err);
        Assert.Equal("WD-01", back.EquipmentId);
        Assert.Equal("08:00", back.StartTime);
        Assert.Equal("12:00", back.EndTime);
        Assert.Equal("定修", back.Kind);
        Assert.Equal("换斗齿'含单引号'", back.Note);
    }

    [Fact]
    public void 写回时也校验_跨零点写不进去()
    {
        using var db = TestDb.Open();
        var bad = new MaintenanceWindowRow
        { EquipmentId = "WD-02", PlanDate = "2026-09-21", StartTime = "22:00", EndTime = "02:00" };
        Assert.Contains("拆两条", MaintenanceWindows.Upsert(db.Connection, bad));
        Assert.Empty(MaintenanceWindows.ByDate(db.Connection, new DateTime(2026, 9, 21), out _));   // 没写脏数据
    }

    [Fact]
    public void 缺设备号拒写()
    {
        using var db = TestDb.Open();
        var r = new MaintenanceWindowRow { PlanDate = "2026-09-22", StartTime = "08:00", EndTime = "10:00" };
        Assert.Contains("设备编号", MaintenanceWindows.Upsert(db.Connection, r));
    }

    [Fact]
    public void 同主键是更新而不是插重复()
    {
        using var db = TestDb.Open();
        var d = "2026-09-23";
        MaintenanceWindows.Upsert(db.Connection, new MaintenanceWindowRow
        { EquipmentId = "WD-03", PlanDate = d, StartTime = "08:00", EndTime = "10:00", Note = "一稿" });
        MaintenanceWindows.Upsert(db.Connection, new MaintenanceWindowRow
        { EquipmentId = "WD-03", PlanDate = d, StartTime = "08:00", EndTime = "11:00", Note = "二稿" });

        var rows = MaintenanceWindows.ByDate(db.Connection, new DateTime(2026, 9, 23), out _);
        Assert.Single(rows);
        Assert.Equal("11:00", rows[0].EndTime);
        Assert.Equal("二稿", rows[0].Note);
    }

    [Fact]
    public void 同设备同日不同起时刻是两条()
    {
        // 跨零点拆两条之后, 同设备同日会有两条 —— 主键含起时刻正是为了这个
        using var db = TestDb.Open();
        var d = "2026-09-24";
        MaintenanceWindows.Upsert(db.Connection, new MaintenanceWindowRow
        { EquipmentId = "WD-04", PlanDate = d, StartTime = "00:00", EndTime = "02:00" });
        MaintenanceWindows.Upsert(db.Connection, new MaintenanceWindowRow
        { EquipmentId = "WD-04", PlanDate = d, StartTime = "22:00", EndTime = "24:00" });

        Assert.Equal(2, MaintenanceWindows.ByDate(db.Connection, new DateTime(2026, 9, 24), out _).Count);
    }

    [Fact]
    public void 删除()
    {
        using var db = TestDb.Open();
        var d = "2026-09-25";
        MaintenanceWindows.Upsert(db.Connection, new MaintenanceWindowRow
        { EquipmentId = "WD-05", PlanDate = d, StartTime = "08:00", EndTime = "10:00" });
        Assert.Single(MaintenanceWindows.ByDate(db.Connection, new DateTime(2026, 9, 25), out _));
        Assert.Equal("", MaintenanceWindows.Delete(db.Connection, "WD-05", d, "08:00"));
        Assert.Empty(MaintenanceWindows.ByDate(db.Connection, new DateTime(2026, 9, 25), out _));
    }

    [Fact]
    public void 按日取只取当日()
    {
        using var db = TestDb.Open();
        MaintenanceWindows.Upsert(db.Connection, new MaintenanceWindowRow
        { EquipmentId = "WD-06", PlanDate = "2026-09-26", StartTime = "08:00", EndTime = "10:00" });
        MaintenanceWindows.Upsert(db.Connection, new MaintenanceWindowRow
        { EquipmentId = "WD-06", PlanDate = "2026-09-27", StartTime = "08:00", EndTime = "10:00" });

        Assert.Single(MaintenanceWindows.ByDate(db.Connection, new DateTime(2026, 9, 26), out _));
    }

    [Fact]
    public void 没有连接时给原因而不是崩()
    {
        Assert.Empty(MaintenanceWindows.ByDate(null!, DateTime.Today, out string e));
        Assert.NotEmpty(e);
        Assert.NotEmpty(MaintenanceWindows.Upsert(null!, new MaintenanceWindowRow { EquipmentId = "X", StartTime = "1", EndTime = "2" }));
        Assert.NotEmpty(MaintenanceWindows.Delete(null!, "X", "2026-01-01", "08:00"));
    }
}
