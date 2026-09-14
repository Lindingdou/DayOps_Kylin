using System;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 日实绩读写（§三四九）。§三三六 的周计划一直在**读** <c>daily_mine_summary</c>，
/// 这一层补上**写**。故头号判据是**写进去的周计划读得回来、且合计口径一致** ——
/// 录入端与计划端显示成两个数是最难查的那种不一致。
///
/// 另两条：**"没录"与"录了 0"分得开**（读不到返回 null，不给一行全 0）、
/// **排弃回灌按占容方且只补增量**（重复保存不重复记账）。
/// </summary>
public class DailyActualsTests
{
    private static readonly DateTime Day = new(2026, 9, 8);

    private static void Clear(DbConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM daily_mine_summary";
        cmd.ExecuteNonQuery();
    }

    private static DailyActualRow Row(double belt = 100, double strip = 5000) => new()
    {
        Date = Day,
        BigBelt = belt, SmallBelt = 200, Longhua = 300,
        TruckExport = 400, Winnowed = 500, BigTruckPile = 600,
        StrippingM3 = strip, Silo1 = 10, Silo2 = 20, Silo3 = 30,
    };

    // ── 口径一致（头号判据）──────────────────────────────────
    [Fact]
    public void 口径_合计等于周计划那六列相加()
    {
        // ★ 录入端与计划端必须是同一个数
        var r = Row();
        Assert.Equal(2100, r.CoalTotalT, 6);                 // 100+200+300+400+500+600
        Assert.Equal(6, WeekPlanSource.CoalColumns.Length);
        Assert.Equal(2100 / WeekPlanSource.CoalDensity, r.CoalM3, 6);
    }

    [Fact]
    public void 口径_筒仓是库存不计入出煤()
    {
        var r = Row();
        Assert.Equal(60, r.SiloTotalT, 6);
        Assert.Equal(2100, r.CoalTotalT, 6);                 // 没把筒仓加进去
    }

    [Fact]
    public void 口径_写进去周计划读得回同一个数()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Equal("", DailyActuals.Save(db.Connection, Row()));

        var map = WeekPlanSource.ActualsInRange(db.Connection, Day.AddDays(-3), Day.AddDays(3));
        Assert.True(map.ContainsKey(Day));
        Assert.Equal(Row().CoalM3, map[Day].Load, 6);        // 折方口径一致
        Assert.Equal(5000, map[Day].Dump, 6);                // 剥离直取
    }

    // ── 存 / 读 / 删 ────────────────────────────────────────
    [Fact]
    public void 存读_往返一致()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DailyActuals.Save(db.Connection, Row(belt: 123.5, strip: 4567.5));

        var back = DailyActuals.Load(db.Connection, Day)!;
        Assert.Equal(123.5, back.BigBelt, 6);
        Assert.Equal(200, back.SmallBelt, 6);
        Assert.Equal(4567.5, back.StrippingM3, 6);
        Assert.Equal(10, back.Silo1, 6);
        Assert.Equal(Day, back.Date);
    }

    [Fact]
    public void 存读_同一天再存是更新不是加一行()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DailyActuals.Save(db.Connection, Row(belt: 100));
        DailyActuals.Save(db.Connection, Row(belt: 999));

        Assert.Single(DailyActuals.LoadRange(db.Connection, Day.AddDays(-5), Day.AddDays(5)));
        Assert.Equal(999, DailyActuals.Load(db.Connection, Day)!.BigBelt, 6);
    }

    [Fact]
    public void 存读_没录的那天返回空而不是一行全零()
    {
        // ★「没录」与「录了 0」是两回事：给一行全 0 会让达成度算出 0% 而不是「—」
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Null(DailyActuals.Load(db.Connection, Day));

        DailyActuals.Save(db.Connection, new DailyActualRow { Date = Day });   // 真的录了全 0
        var zero = DailyActuals.Load(db.Connection, Day);
        Assert.NotNull(zero);
        Assert.Equal(0, zero!.CoalTotalT, 9);
    }

    [Fact]
    public void 存读_区间只取范围内且按日期升序()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        foreach (int d in new[] { 3, 1, 2, 9 })
            DailyActuals.Save(db.Connection, new DailyActualRow { Date = new DateTime(2026, 9, d), BigBelt = d });

        var list = DailyActuals.LoadRange(db.Connection, new DateTime(2026, 9, 1), new DateTime(2026, 9, 3));
        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, list.Select(x => x.BigBelt));
    }

    [Fact]
    public void 删除_删掉之后读不到()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        DailyActuals.Save(db.Connection, Row());
        Assert.Equal("", DailyActuals.Delete(db.Connection, Day));
        Assert.Null(DailyActuals.Load(db.Connection, Day));
    }

    [Fact]
    public void 删除_删不存在的不报错()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Equal("", DailyActuals.Delete(db.Connection, Day));
    }

    // ── 校验与兜底 ──────────────────────────────────────────
    [Fact]
    public void 校验_负数拦下并点名是哪一项()
    {
        // 产量/剥离没有负的；录进去会把周月合计悄悄拉低
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = Row();
        r.Winnowed = -1;
        string err = DailyActuals.Save(db.Connection, r);
        Assert.Contains("风选煤", err);
        Assert.Contains("不能为负", err);
        Assert.Null(DailyActuals.Load(db.Connection, Day));   // 一行都没写
    }

    [Fact]
    public void 校验_剥离为负也拦()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = Row();
        r.StrippingM3 = -5;
        Assert.Contains("剥离总量", DailyActuals.Save(db.Connection, r));
    }

    [Fact]
    public void 校验_字段清单十项齐全()
    {
        // 校验与界面共用这一份清单, 少一项就等于那一项永远不校验
        var names = DailyActuals.Fields(Row()).Select(f => f.Name).ToList();
        Assert.Equal(10, names.Count);
        Assert.Contains("大皮带出煤", names);
        Assert.Contains("剥离总量", names);
        Assert.Contains("3号筒仓", names);
    }

    [Fact]
    public void 兜底_没有连接时只回原因不抛()
    {
        Assert.Contains("数据库连接", DailyActuals.Save(null, Row()));
        Assert.Contains("数据库连接", DailyActuals.Delete(null, Day));
        Assert.Null(DailyActuals.Load(null, Day));
        Assert.Empty(DailyActuals.LoadRange(null, Day, Day));
    }

    [Fact]
    public void 兜底_没有行可存时说清楚()
    {
        using var db = TestDb.Open();
        Assert.Contains("没有要保存", DailyActuals.Save(db.Connection, null));
    }

    // ── 排弃回灌 ────────────────────────────────────────────
    private static SinkRegistry Dump(double capWanM3 = 1000, double filledWanM3 = 0)
    {
        var r = new SinkRegistry();
        r.Put(new SinkNode
        {
            Id = "D1", Name = "北排土场", Kind = SinkKind.ExternalDump, RefEntityId = "D1",
            DesignCapacityM3 = capWanM3 * 1e4, FilledM3 = filledWanM3 * 1e4,
        });
        return r;
    }

    [Fact]
    public void 回灌_按占容方扣不是实方()
    {
        // ★ 排土场吃的是沉降稳定后的体积 V容 = V实 × Kr
        var sinks = Dump();
        double added = DailyActuals.BackfillDump(null, sinks, "D1", strippingM3: 10000);
        double kr = PitMine3D.Kylin.Cad.LongTermSimTimeline.RockSwell;
        Assert.Equal(10000 * kr, added, 3);
        Assert.Equal(10000 * kr, sinks.Find("D1")!.FilledM3, 3);
        Assert.True(added > 10000, "占容方一定大于实方");
    }

    [Fact]
    public void 回灌_只补增量_重复保存不重复记账()
    {
        // ★ 原版那条纪律：盘子装配时已扣过一次，再按全量扣就是重复记账
        var sinks = Dump();
        double kr = PitMine3D.Kylin.Cad.LongTermSimTimeline.RockSwell;
        double first = DailyActuals.BackfillDump(null, sinks, "D1", 10000, alreadyDumpedM3: 0);
        double again = DailyActuals.BackfillDump(null, sinks, "D1", 10000, alreadyDumpedM3: first);
        Assert.Equal(10000 * kr, first, 3);
        Assert.Equal(0, again, 9);
        Assert.Equal(10000 * kr, sinks.Find("D1")!.FilledM3, 3);   // 只扣了一次
    }

    [Fact]
    public void 回灌_量变大时只补差额()
    {
        var sinks = Dump();
        double kr = PitMine3D.Kylin.Cad.LongTermSimTimeline.RockSwell;
        double first = DailyActuals.BackfillDump(null, sinks, "D1", 10000, 0);
        double more = DailyActuals.BackfillDump(null, sinks, "D1", 15000, first);
        Assert.Equal(5000 * kr, more, 3);
        Assert.Equal(15000 * kr, sinks.Find("D1")!.FilledM3, 3);
    }

    [Fact]
    public void 回灌_通过型去向不回灌()
    {
        // 破碎站/煤仓不占库容, 往它身上扣是没有意义的
        var sinks = new SinkRegistry();
        sinks.Put(new SinkNode { Id = "LUP-1", Name = "破碎站", Kind = SinkKind.Crusher, RefEntityId = "LUP-1" });
        Assert.Equal(0, DailyActuals.BackfillDump(null, sinks, "LUP-1", 10000), 9);
    }

    [Theory]
    [InlineData("没有这个去向")]
    [InlineData("")]
    [InlineData(null)]
    public void 回灌_去向不存在时返回零不抛(string? id)
        => Assert.Equal(0, DailyActuals.BackfillDump(null, Dump(), id, 10000), 9);

    [Fact]
    public void 回灌_没有剥离量时不动库容()
    {
        var sinks = Dump();
        Assert.Equal(0, DailyActuals.BackfillDump(null, sinks, "D1", 0), 9);
        Assert.Equal(0, sinks.Find("D1")!.FilledM3, 9);
    }

    [Fact]
    public void 回灌_没有去向登记簿时返回零()
        => Assert.Equal(0, DailyActuals.BackfillDump(null, null, "D1", 10000), 9);

    // ── 端到端 ──────────────────────────────────────────────
    [Fact]
    public void 端到端_录一周之后周计划算得出达成度()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var monday = new DateTime(2026, 9, 7);
        for (int i = 0; i < 7; i++)
            DailyActuals.Save(db.Connection, new DailyActualRow
            { Date = monday.AddDays(i), BigBelt = 1000, StrippingM3 = 20000 });

        var map = WeekPlanSource.ActualsInRange(db.Connection, monday, monday.AddDays(6));
        Assert.Equal(7, map.Count);
        Assert.All(map.Values, v => Assert.Equal(1000 / WeekPlanSource.CoalDensity, v.Load, 6));
    }
}
