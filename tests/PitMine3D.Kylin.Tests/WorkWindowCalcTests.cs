using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 有效工时口径（§三三四，逐行移植原 <c>TaskLib.Engine.WorkWindowCalc</c>）：
/// 有效时窗 = 班时窗 − 检修 − 爆破清场 − 非首班交接损失（− 滚动重排起点）。
///
/// 这条口径**只许有一处实现**：装箱与逐日能力日历都要用，各写一份就会出现
/// "计划里那天排了 8 小时、实际那天在定修"且不报错。所以本组把每一维都单独钉住。
/// </summary>
public class WorkWindowCalcTests
{
    private static readonly ShiftWindow Night = new("夜班", 0, 8);
    private static readonly ShiftWindow Day = new("早班", 8, 16);
    private static readonly ShiftWindow Swing = new("中班", 16, 24);
    private static List<ShiftWindow> Three() => new() { Night, Day, Swing };

    private static MaintenanceHourWindow M(string eq, double s, double e) => new() { EquipId = eq, Start = s, End = e };

    // ── 基本 ────────────────────────────────────────────────
    [Fact]
    public void 无检修无爆破_首班拿满()
    {
        var (ws, we) = WorkWindowCalc.Of(Night, "WD-01", null, null, handoverH: 0.5);
        Assert.Equal(0, ws, 6);          // 首班(Start==0)不扣交接
        Assert.Equal(8, we, 6);
    }

    [Fact]
    public void 非首班扣交接损失()
    {
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", null, null, handoverH: 0.5);
        Assert.Equal(8.5, ws, 6);        // 早班起点被推后半小时
        Assert.Equal(16, we, 6);
    }

    // ── 检修 ────────────────────────────────────────────────
    [Fact]
    public void 检修把班起点推到档期结束之后()
    {
        var mw = new[] { M("WD-01", 8, 11) };
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", mw, null, 0);
        Assert.Equal(11, ws, 6);
        Assert.Equal(16, we, 6);
    }

    [Fact]
    public void 检修跨整班时该班没有可用时间()
    {
        var mw = new[] { M("WD-01", 6, 18) };
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", mw, null, 0);
        Assert.Equal(ws, we);            // we == ws 即"这个班干不了"
    }

    [Fact]
    public void 别的设备的检修不影响本台()
    {
        var mw = new[] { M("WD-02", 8, 15) };
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", mw, null, 0);
        Assert.Equal(8, ws, 6);
        Assert.Equal(16, we, 6);
    }

    [Fact]
    public void 设备号比对不分大小写()
    {
        var mw = new[] { M("wd-01", 8, 11) };
        var (ws, _) = WorkWindowCalc.Of(Day, "WD-01", mw, null, 0);
        Assert.Equal(11, ws, 6);
    }

    [Fact]
    public void 不压本班的检修不影响()
    {
        var mw = new[] { M("WD-01", 0, 7) };     // 全在夜班
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", mw, null, 0);
        Assert.Equal(8, ws, 6);
        Assert.Equal(16, we, 6);
    }

    [Fact]
    public void 多条检修取最晚的结束时刻()
    {
        var mw = new[] { M("WD-01", 8, 10), M("WD-01", 9, 13), M("WD-01", 8.5, 11) };
        var (ws, _) = WorkWindowCalc.Of(Day, "WD-01", mw, null, 0);
        Assert.Equal(13, ws, 6);
    }

    // ── 爆破清场 ────────────────────────────────────────────
    [Fact]
    public void 爆破切开时只取最长的一段()
    {
        // 一个班里让同一台设备干两段活在现场是两次进退场, 装箱不假装能无缝拼起来
        var blasts = new[] { new BlastWindow(10, 11) };
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", null, blasts, 0);
        Assert.Equal(11, ws, 6);        // 后段 [11,16) 长 5h > 前段 [8,10) 长 2h
        Assert.Equal(16, we, 6);
    }

    [Fact]
    public void 爆破在班尾时取前段()
    {
        var blasts = new[] { new BlastWindow(13, 16) };
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", null, blasts, 0);
        Assert.Equal(8, ws, 6);
        Assert.Equal(13, we, 6);
    }

    [Fact]
    public void 爆破盖满整班时没有可用时间()
    {
        var blasts = new[] { new BlastWindow(7, 17) };
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", null, blasts, 0);
        Assert.Equal(ws, we);
    }

    // ── 滚动重排 ────────────────────────────────────────────
    [Fact]
    public void 滚动重排只排此刻之后()
    {
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", null, null, 0, fromHour: 12);
        Assert.Equal(12, ws, 6);
        Assert.Equal(16, we, 6);
    }

    [Fact]
    public void 滚动起点越过班尾则该班没时间()
    {
        var (ws, we) = WorkWindowCalc.Of(Day, "WD-01", null, null, 0, fromHour: 20);
        Assert.Equal(ws, we);
    }

    // ── 全天合计 ────────────────────────────────────────────
    [Fact]
    public void 全天合计_无干扰时三班合计24小时减交接()
    {
        // 夜班首班不扣, 早/中两班各扣 0.5h
        double h = WorkWindowCalc.DayHours(Three(), "WD-01", null, null, handoverH: 0.5);
        Assert.Equal(24 - 1.0, h, 6);
    }

    [Fact]
    public void 全天合计_有检修的那天少排()
    {
        var mw = new[] { M("WD-01", 8, 16) };     // 早班整班定修
        double h = WorkWindowCalc.DayHours(Three(), "WD-01", mw, null, 0);
        Assert.Equal(16, h, 6);                   // 只剩夜班 8 + 中班 8
    }

    [Fact]
    public void 全天合计_半小时以下的碎片按干不了算()
    {
        // 与装箱侧 avail >= 0.5 的门槛保持一致, 否则日历说"还有 0.2 小时", 装箱一条也排不出来
        var mw = new[] { M("WD-01", 0, 7.8), M("WD-01", 8, 15.7), M("WD-01", 16, 23.9) };
        double h = WorkWindowCalc.DayHours(Three(), "WD-01", mw, null, 0);
        // 夜班剩 0.2(丢) · 早班剩 0.3(丢) · 中班剩 0.1(丢)
        Assert.Equal(0, h, 6);
    }

    [Fact]
    public void 全天合计_恰好半小时的算数()
    {
        var mw = new[] { M("WD-01", 0, 7.5), M("WD-01", 8, 16), M("WD-01", 16, 24) };
        double h = WorkWindowCalc.DayHours(Three(), "WD-01", mw, null, 0);
        Assert.Equal(0.5, h, 6);      // 门槛是 >= 0.5, 不是 > 0.5
    }

    [Fact]
    public void 全天合计_空班次表回0不崩()
        => Assert.Equal(0, WorkWindowCalc.DayHours(null!, "WD-01", null, null, 0));

    // ── 停产时窗的合并与挖除 ────────────────────────────────
    [Fact]
    public void 停产窗合并_重叠并成一段()
    {
        var m = BlastWindow.Merge(new[] { new BlastWindow(10, 12, "一炮"), new BlastWindow(11, 13, "二炮") });
        Assert.Single(m);
        Assert.Equal(10, m[0].Start, 6);
        Assert.Equal(13, m[0].End, 6);
        Assert.Contains("一炮", m[0].Label);
        Assert.Contains("二炮", m[0].Label);
    }

    [Fact]
    public void 停产窗合并_挨着的也并起来不留碎片()
    {
        // 两炮挨着放会切出 0.001h 的碎片, 那种段没有意义
        var m = BlastWindow.Merge(new[] { new BlastWindow(10, 12), new BlastWindow(12, 14) });
        Assert.Single(m);
        Assert.Equal(14, m[0].End, 6);
    }

    [Fact]
    public void 停产窗合并_丢弃非法窗()
    {
        var m = BlastWindow.Merge(new[] { new BlastWindow(10, 10), new BlastWindow(12, 11) });
        Assert.Empty(m);
    }

    [Fact]
    public void 挖除_切成两段()
    {
        var segs = BlastWindow.Subtract(8, 16, new[] { new BlastWindow(10, 11) });
        Assert.Equal(2, segs.Count);
        Assert.Equal((8.0, 10.0), segs[0]);
        Assert.Equal((11.0, 16.0), segs[1]);
    }

    [Fact]
    public void 挖除_窗完全在外时原样返回()
    {
        var segs = BlastWindow.Subtract(8, 16, new[] { new BlastWindow(0, 5), new BlastWindow(20, 22) });
        Assert.Single(segs);
        Assert.Equal((8.0, 16.0), segs[0]);
    }

    [Fact]
    public void 挖除_空区间回空()
    {
        Assert.Empty(BlastWindow.Subtract(8, 8, null));
        Assert.Empty(BlastWindow.Subtract(16, 8, null));
    }

    // ── 台账行 → 小时制 ─────────────────────────────────────
    [Fact]
    public void 台账行转小时制()
    {
        var w = MaintenanceHourWindow.From(new MaintenanceWindowRow
        { EquipmentId = "WD-01", StartTime = "08:30", EndTime = "12:00", Kind = "保养" });
        Assert.NotNull(w);
        Assert.Equal(8.5, w!.Start, 6);
        Assert.Equal(12, w.End, 6);
        Assert.Equal("保养", w.Label);
    }

    [Fact]
    public void 台账行_时刻非法的被丢弃而不是按0点算()
    {
        Assert.Null(MaintenanceHourWindow.From(new MaintenanceWindowRow
        { EquipmentId = "WD-01", StartTime = "上午", EndTime = "12:00" }));
        Assert.Null(MaintenanceHourWindow.From(new MaintenanceWindowRow
        { EquipmentId = "WD-01", StartTime = "22:00", EndTime = "02:00" }));   // 跨零点没拆
    }

    [Fact]
    public void 台账整批转换_跳过非法行()
    {
        var rows = new[]
        {
            new MaintenanceWindowRow { EquipmentId = "A", StartTime = "08:00", EndTime = "10:00" },
            new MaintenanceWindowRow { EquipmentId = "B", StartTime = "坏的", EndTime = "10:00" },
            new MaintenanceWindowRow { EquipmentId = "C", StartTime = "22:00", EndTime = "24:00" },
        };
        var list = WorkWindowCalc.HourWindowsOf(rows);
        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, x => x.EquipId == "B");
        Assert.Contains(list, x => x.EquipId == "C" && Math.Abs(x.End - 24) < 1e-9);
    }

    // ── 端到端：日历 + 档期 → 有效工时 ──────────────────────
    [Fact]
    public void 端到端_由班次日历与检修档期算出当天可用工时()
    {
        var shifts = MaintenanceWindows.ShiftWindowsOf(new List<ShiftCalendarRow>
        {
            new() { Shift = "A", StartTime = "08:00" },
            new() { Shift = "B", StartTime = "16:00" },
            new() { Shift = "C", StartTime = "00:00" },
        });
        var maint = WorkWindowCalc.HourWindowsOf(new[]
        {
            new MaintenanceWindowRow { EquipmentId = "WD-01", StartTime = "08:00", EndTime = "12:00", Kind = "定修" },
        });

        double h = WorkWindowCalc.DayHours(shifts, "WD-01", maint, null, handoverH: 0.5);
        // 夜班[0,8) 首班不扣 = 8；早班[8,16) 检修到 12 再扣 0.5 交接 = 3.5；中班[16,24) 扣 0.5 = 7.5
        Assert.Equal(8 + 3.5 + 7.5, h, 6);
    }
}
