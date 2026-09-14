using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 穿孔作业计划台账（§三五二）。这张表 V044 建好之后一直零消费者，本轮接上。
///
/// 头号判据是那两条**不会报错**的口径：
///   ① 起止时刻非法的行要<b>丢弃并计数</b> —— 一条 (0,0) 的穿孔任务在时窗里不与任何班次重叠，
///      等于静默失效：界面上看得见、计划里没有。
///   ② 台账那一列是<b>单孔</b>延米，总延米 = 孔数 × 单孔 —— 不换算的话，
///      一条 300 孔 × 15 m 的穿孔任务在下游变成 15 m 的活。
/// 其次是 NULL ≠ 0：台阶 0 与孔数 0 都是合法值，用 null 表达"没录"。
/// </summary>
public class DrillPlanStoreTests
{
    private static void Clear(DbConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM drill_plan";
        cmd.ExecuteNonQuery();
    }

    private static readonly DateTime Day = new(2026, 9, 11);

    private static DrillPlanRow R(string equip = "DR-1", string start = "08:00", string end = "16:00",
                                  string zone = "EP-08", double? bench = 1195, int? holes = 100, double? meters = 15,
                                  string status = DrillPlanStore.StatusPlanned)
        => new()
        {
            EquipmentId = equip, PlanDate = DrillPlanStore.D(Day), StartTime = start, EndTime = end,
            Zone = zone, BenchElevationM = bench, HoleCount = holes, HoleLengthM = meters, Status = status,
        };

    // ── 校验 ────────────────────────────────────────────────
    [Fact]
    public void 校验_跨零点要拆两条且把怎么拆说清楚()
    {
        string? bad = DrillPlanStore.Validate("22:00", "02:00");
        Assert.NotNull(bad);
        Assert.Contains("拆两条", bad);
        Assert.Contains("22:00–24:00", bad);
    }

    [Fact]
    public void 校验_起等于止也不收()
        => Assert.NotNull(DrillPlanStore.Validate("08:00", "08:00"));

    [Fact]
    public void 校验_认得出24点当日终点()
        => Assert.Null(DrillPlanStore.Validate("22:00", "24:00"));

    [Theory]
    [InlineData("", "16:00")]
    [InlineData("八点", "16:00")]
    [InlineData("08:00", "")]
    [InlineData("08:00", "下午四点")]
    public void 校验_认不出的时刻分别报出是起还是止(string s, string e)
    {
        string? bad = DrillPlanStore.Validate(s, e);
        Assert.NotNull(bad);
        Assert.Contains(bad.StartsWith("起") ? "起始时刻" : "结束时刻", bad);
    }

    [Fact]
    public void 校验_光写小时数也认()
        => Assert.Null(DrillPlanStore.Validate("8", "16.5"));

    [Theory]
    [InlineData("计划", "计划")]
    [InlineData("完成", "完成")]
    [InlineData("取消", "取消")]
    [InlineData("", "计划")]
    [InlineData("随便写", "计划")]
    public void 校验_状态归一不新造状态(string raw, string want)
        => Assert.Equal(want, DrillPlanStore.NormalizeStatus(raw));

    // ── 存读改删 ────────────────────────────────────────────
    [Fact]
    public void 存读_往返一致含可空三列()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Equal("", DrillPlanStore.Upsert(db.Connection, R()));

        var back = DrillPlanStore.ByDate(db.Connection, Day, out string err).Single();
        Assert.Equal("", err);
        Assert.Equal("DR-1", back.EquipmentId);
        Assert.Equal("EP-08", back.Zone);
        Assert.Equal("08:00", back.StartTime);
        Assert.Equal("16:00", back.EndTime);
        Assert.Equal(1195, back.BenchElevationM);
        Assert.Equal(100, back.HoleCount);
        Assert.Equal(15, back.HoleLengthM);
        Assert.Equal("计划", back.Status);
    }

    [Fact]
    public void 存读_留空的三列读回来还是空而不是零()
    {
        // ★ 0 是合法标高、也是合法孔数；用 0 表示"没录"就分不开了
        using var db = TestDb.Open();
        Clear(db.Connection);
        DrillPlanStore.Upsert(db.Connection, R(bench: null, holes: null, meters: null));
        var back = DrillPlanStore.ByDate(db.Connection, Day, out _).Single();
        Assert.Null(back.BenchElevationM);
        Assert.Null(back.HoleCount);
        Assert.Null(back.HoleLengthM);
    }

    [Fact]
    public void 存读_台阶零存得下且不被当成未录()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DrillPlanStore.Upsert(db.Connection, R(bench: 0, holes: 0));
        var back = DrillPlanStore.ByDate(db.Connection, Day, out _).Single();
        Assert.Equal(0, back.BenchElevationM);
        Assert.Equal(0, back.HoleCount);
    }

    [Fact]
    public void 存读_同主键再存是覆盖不是新增()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DrillPlanStore.Upsert(db.Connection, R(zone: "EP-08"));
        DrillPlanStore.Upsert(db.Connection, R(zone: "EP-09"));
        var rows = DrillPlanStore.ByDate(db.Connection, Day, out _);
        Assert.Single(rows);
        Assert.Equal("EP-09", rows[0].Zone);
    }

    [Fact]
    public void 存读_一台钻机一天两个区靠起始时刻分得开()
    {
        // 主键含起始时刻正是为了这个：上午 A 区、下午 B 区
        using var db = TestDb.Open();
        Clear(db.Connection);
        DrillPlanStore.Upsert(db.Connection, R(start: "00:00", end: "08:00", zone: "A"));
        DrillPlanStore.Upsert(db.Connection, R(start: "08:00", end: "16:00", zone: "B"));
        Assert.Equal(2, DrillPlanStore.ByDate(db.Connection, Day, out _).Count);
    }

    [Fact]
    public void 存读_只读本日不串到别的日子()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DrillPlanStore.Upsert(db.Connection, R());
        var other = R(); other.PlanDate = DrillPlanStore.D(Day.AddDays(1));
        DrillPlanStore.Upsert(db.Connection, other);
        Assert.Single(DrillPlanStore.ByDate(db.Connection, Day, out _));
    }

    [Fact]
    public void 存读_按起始时刻排序()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DrillPlanStore.Upsert(db.Connection, R(equip: "DR-2", start: "16:00", end: "24:00"));
        DrillPlanStore.Upsert(db.Connection, R(equip: "DR-1", start: "00:00", end: "08:00"));
        var rows = DrillPlanStore.ByDate(db.Connection, Day, out _);
        Assert.Equal(new[] { "00:00", "16:00" }, rows.Select(r => r.StartTime));
    }

    [Fact]
    public void 入库_校验不过就不写脏数据()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Contains("拆两条", DrillPlanStore.Upsert(db.Connection, R(start: "22:00", end: "02:00")));
        Assert.Empty(DrillPlanStore.ByDate(db.Connection, Day, out _));
    }

    [Fact]
    public void 入库_没填钻机与没连接分别报()
    {
        using var db = TestDb.Open();
        Assert.Contains("钻机编号", DrillPlanStore.Upsert(db.Connection, R(equip: "  ")));
        Assert.Contains("数据库连接", DrillPlanStore.Upsert(null, R()));
        Assert.Contains("没有要保存", DrillPlanStore.Upsert(db.Connection, null));
    }

    [Fact]
    public void 删除_删掉之后本日没有()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DrillPlanStore.Upsert(db.Connection, R());
        Assert.Equal("", DrillPlanStore.Delete(db.Connection, "DR-1", DrillPlanStore.D(Day), "08:00"));
        Assert.Empty(DrillPlanStore.ByDate(db.Connection, Day, out _));
    }

    [Fact]
    public void 兜底_名字里的单引号不炸SQL()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DrillPlanStore.Upsert(db.Connection, R(zone: "O'Brien 区"));
        Assert.Equal("O'Brien 区", DrillPlanStore.ByDate(db.Connection, Day, out _).Single().Zone);
    }

    [Fact]
    public void 兜底_没有连接时读列表为空并给原因()
    {
        var rows = DrillPlanStore.ByDate(null, Day, out string err);
        Assert.Empty(rows);
        Assert.Contains("数据库连接", err);
    }

    // ── 台账 → 装箱输入 ─────────────────────────────────────
    [Fact]
    public void 转换_总延米按孔数乘单孔()
    {
        // ★ 台账那一列是单孔延米；不乘的话 300 孔 × 15 m 在下游变成 15 m 的活
        var load = DrillPlanStore.ToDrillInputs(new[] { R(holes: 300, meters: 15) }, Day);
        Assert.Equal(4500, load.Drills.Single().HoleLengthM);
        Assert.Equal(300, load.Drills.Single().HoleCount);
    }

    [Fact]
    public void 转换_只录了延米没录孔数时原样带下去()
    {
        var load = DrillPlanStore.ToDrillInputs(new[] { R(holes: null, meters: 15) }, Day);
        Assert.Equal(15, load.Drills.Single().HoleLengthM);
        Assert.Null(load.Drills.Single().HoleCount);
    }

    [Fact]
    public void 转换_起止非法的丢弃并计数不按零点算()
    {
        var load = DrillPlanStore.ToDrillInputs(new[] { R(start: "?", end: "16:00"), R(equip: "DR-2") }, Day);
        Assert.Single(load.Drills);
        Assert.Equal(1, load.Bad);
        Assert.Equal(8, load.Drills[0].Start);       // 活下来的那条是好的，不是被塞成 (0,0)
        Assert.Contains("起止时刻非法已丢弃", load.Label);
    }

    [Fact]
    public void 转换_取消的不排且单独计数()
    {
        var load = DrillPlanStore.ToDrillInputs(
            new[] { R(status: DrillPlanStore.StatusCancelled), R(equip: "DR-2") }, Day);
        Assert.Single(load.Drills);
        Assert.Equal(1, load.Cancelled);
        Assert.Contains("已取消不排", load.Label);
    }

    [Fact]
    public void 转换_完成与进行中都照排()
    {
        var load = DrillPlanStore.ToDrillInputs(new[]
        {
            R(equip: "DR-1", status: DrillPlanStore.StatusDone),
            R(equip: "DR-2", status: DrillPlanStore.StatusRunning),
        }, Day);
        Assert.Equal(2, load.Drills.Count);
    }

    [Fact]
    public void 转换_没录量的条数要报出来()
    {
        var load = DrillPlanStore.ToDrillInputs(new[] { R(holes: null, meters: null) }, Day);
        Assert.Equal(1, load.WithoutQty);
        Assert.Contains("进度只能按完成与否判", load.Label);
    }

    [Fact]
    public void 转换_一条都没有时说清楚去哪儿排()
    {
        var load = DrillPlanStore.ToDrillInputs(new List<DrillPlanRow>(), Day);
        Assert.Empty(load.Drills);
        Assert.Contains("无穿孔计划", load.Label);
        Assert.Contains("钻爆计划衔接", load.Label);
    }

    [Fact]
    public void 转换_台阶未录时按零带下去而不是抛()
    {
        var load = DrillPlanStore.ToDrillInputs(new[] { R(bench: null) }, Day);
        Assert.Equal(0, load.Drills.Single().BenchElevationM);
    }

    [Fact]
    public void 转换_空表与null都不抛()
    {
        Assert.Empty(DrillPlanStore.ToDrillInputs(null, Day).Drills);
        Assert.Empty(DrillPlanStore.ToDrillInputs(new DrillPlanRow[] { null! }, Day).Drills);
    }
}
