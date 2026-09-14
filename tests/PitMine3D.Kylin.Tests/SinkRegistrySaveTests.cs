using System;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 去向台账写回路径（§三三七）：<see cref="SinkRegistryLoader.Save(DbConnection, SinkRegistry)"/> /
/// <c>Delete</c> / <c>Stocktake</c> / <c>AddFilled</c>。
///
/// 最要紧的两条：**普通保存绝不动「已填」**（那是实绩累计出来的账，照写会重复计量），
/// **跨族改类型要明说不许**（换表存储会留孤儿行）。全部走 SQLite 测试库。
/// </summary>
public class SinkRegistrySaveTests
{
    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ClearAll(DbConnection c)
    {
        Exec(c, "DELETE FROM dump_site");
        Exec(c, "DELETE FROM load_unload_point");
        Exec(c, "DELETE FROM sink_profile");
        Exec(c, "DELETE FROM sink_stocktake");
    }

    private static double Scalar(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        object? v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? double.NaN : Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    private static string Text(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static SinkNode Dump(string id, string name = "北排土场", double capM3 = 100e4) => new()
    {
        Id = id, Name = name, Kind = SinkKind.ExternalDump, RefEntityId = "",
        DesignCapacityM3 = capM3, BenchHeightM = 20, BenchSlopeAngleDeg = 35, Status = "active",
    };

    // ── 新建 ────────────────────────────────────────────────
    [Fact]
    public void 新建排土场_本体与档案各写一行()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.WorkLineLengthM = 400;
        s.AcceptedMaterials.Add("rock");

        var r = SinkRegistryLoader.Save(db.Connection, new[] { s });
        Assert.True(r.Ok, r.Caption);
        Assert.Equal(1, r.Inserted);
        Assert.Equal(1, Scalar(db.Connection, "SELECT COUNT(*) FROM dump_site WHERE dump_id='D-N1'"));
        Assert.Equal(1, Scalar(db.Connection, "SELECT COUNT(*) FROM sink_profile WHERE sink_id='D-N1'"));
        Assert.Equal(400, Scalar(db.Connection, "SELECT work_line_length_m FROM sink_profile WHERE sink_id='D-N1'"), 6);
        Assert.Equal("D-N1", s.RefEntityId);      // 回填，下次保存才认得出是更新而不是又一条新的
    }

    [Fact]
    public void 新建排土场_容量按万立方入库()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        SinkRegistryLoader.Save(db.Connection, new[] { Dump("D-N1", capM3: 120e4) });
        // ★ 契约 m³ ÷1e4 → 台账万 m³；做反了会差 4 个数量级
        Assert.Equal(120, Scalar(db.Connection, "SELECT design_capacity_wan_m3 FROM dump_site WHERE dump_id='D-N1'"), 6);
    }

    [Fact]
    public void 新建卸载点_自增编号回填到节点()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = SinkRegistryLoader.CreateNew(SinkKind.Crusher, "1号破碎站");
        s.AcceptTph = 1500; s.X = 100; s.Y = 200; s.Z = 30;
        Assert.StartsWith("LUP-新", s.Id);          // 占位编号

        var r = SinkRegistryLoader.Save(db.Connection, new[] { s });
        Assert.True(r.Ok, r.Caption);
        Assert.Matches(@"^LUP-\d+$", s.Id);         // 插完换成真编号
        Assert.Equal(s.Id, s.RefEntityId);
        Assert.Contains(r.Notes, n => n.Contains("已建档"));
        Assert.Equal("crusher", Text(db.Connection, "SELECT unload_sub FROM load_unload_point"));
        Assert.Equal(1500, Scalar(db.Connection, "SELECT throughput_tph FROM load_unload_point"), 6);
    }

    [Fact]
    public void 新建卸载点_不回填编号会重复插入_故必须回填()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = SinkRegistryLoader.CreateNew(SinkKind.Silo, "1号原煤仓");
        SinkRegistryLoader.Save(db.Connection, new[] { s });
        var r2 = SinkRegistryLoader.Save(db.Connection, new[] { s });   // 再存一次
        Assert.Equal(1, r2.Updated);                                    // 是更新，不是又插一条
        Assert.Equal(0, r2.Inserted);
        Assert.Equal(1, Scalar(db.Connection, "SELECT COUNT(*) FROM load_unload_point"));
    }

    [Fact]
    public void 新建去向_按类型选表()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var d = SinkRegistryLoader.CreateNew(SinkKind.InternalDump);
        var c = SinkRegistryLoader.CreateNew(SinkKind.Crusher);
        Assert.True(SinkRegistryLoader.Save(db.Connection, new[] { d, c }).Ok);
        Assert.Equal(1, Scalar(db.Connection, "SELECT COUNT(*) FROM dump_site"));
        Assert.Equal(1, Scalar(db.Connection, "SELECT COUNT(*) FROM load_unload_point"));
    }

    [Fact]
    public void 新建去向_通过型不占库容排弃类给初始库容()
    {
        Assert.Equal(0, SinkRegistryLoader.CreateNew(SinkKind.Crusher).DesignCapacityM3, 6);
        Assert.True(SinkRegistryLoader.CreateNew(SinkKind.ExternalDump).DesignCapacityM3 > 0);
    }

    // ── 「已填」纪律 ────────────────────────────────────────
    [Fact]
    public void 普通保存_绝不回写已填()
    {
        // ★ 本文件最要紧的一条：内存里的 FilledM3 常叠着当日实绩的界面增量，
        //   照写会把当日排弃量重复计一遍
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.FilledM3 = 10e4;
        SinkRegistryLoader.Save(db.Connection, new[] { s });          // 新建：开账初值写一次
        Assert.Equal(10, Scalar(db.Connection, "SELECT current_filled_wan_m3 FROM dump_site WHERE dump_id='D-N1'"), 6);

        s.FilledM3 = 55e4;                                            // 界面上叠了当日增量
        s.Name = "北排土场（改名）";
        var r = SinkRegistryLoader.Save(db.Connection, new[] { s });
        Assert.Equal(1, r.Updated);
        Assert.Equal("北排土场（改名）", Text(db.Connection, "SELECT name FROM dump_site WHERE dump_id='D-N1'"));
        Assert.Equal(10, Scalar(db.Connection, "SELECT current_filled_wan_m3 FROM dump_site WHERE dump_id='D-N1'"), 6);
    }

    [Fact]
    public void 更新_台阶参数为零时不抹掉台账里录好的()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.BenchHeightM = 12;
        SinkRegistryLoader.Save(db.Connection, new[] { s });

        s.BenchHeightM = 0;      // 0 = "界面没填"，不是"改成 0"
        SinkRegistryLoader.Save(db.Connection, new[] { s });
        Assert.Equal(12, Scalar(db.Connection, "SELECT bench_height_m FROM dump_site WHERE dump_id='D-N1'"), 6);
    }

    // ── 校验 ────────────────────────────────────────────────
    [Fact]
    public void 校验_状态非法整条不写()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.Status = "半开";
        var r = SinkRegistryLoader.Save(db.Connection, new[] { s });
        Assert.False(r.Ok);
        Assert.Equal(1, r.Failed);
        Assert.Contains("状态取值非法", r.Errors[0]);
        Assert.Equal(0, Scalar(db.Connection, "SELECT COUNT(*) FROM dump_site"));
    }

    [Theory]
    [InlineData("在用", "active")]
    [InlineData("ACTIVE", "active")]
    [InlineData("已排满", "full")]
    [InlineData("已关闭", "closed")]
    [InlineData("", "active")]
    public void 校验_状态中英文都收(string raw, string expect)
        => Assert.Equal(expect, SinkRegistryLoader.NormalizeStatus(raw));

    [Fact]
    public void 校验_负容量与负通过能力都拦下()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var a = Dump("D-A"); a.DesignCapacityM3 = -1;
        var b = Dump("D-B"); b.AcceptTph = -1;
        var r = SinkRegistryLoader.Save(db.Connection, new[] { a, b });
        Assert.Equal(2, r.Failed);
        Assert.Equal(0, Scalar(db.Connection, "SELECT COUNT(*) FROM dump_site"));
    }

    [Fact]
    public void 校验_跨族改类型明说不许而不是偷偷换表()
    {
        // 换表存储会留下孤儿行 + 主键换命名空间；让用户删了重建，账面才不会"同一去向两处存在"
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        SinkRegistryLoader.Save(db.Connection, new[] { s });

        s.Kind = SinkKind.Crusher;                    // 排弃类 → 通过型
        var r = SinkRegistryLoader.Save(db.Connection, new[] { s });
        Assert.False(r.Ok);
        Assert.Contains("换表存储", r.Errors[0]);
        Assert.Contains("删除本条后重新新增", r.Errors[0]);
        Assert.Equal(0, Scalar(db.Connection, "SELECT COUNT(*) FROM load_unload_point"));   // 没偷偷插
    }

    [Fact]
    public void 校验_同族改类型允许()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-T1", "南场");
        SinkRegistryLoader.Save(db.Connection, new[] { s });

        s.Kind = SinkKind.TopsoilYard;                // 排弃类 → 排弃类
        Assert.True(SinkRegistryLoader.Save(db.Connection, new[] { s }).Ok);
        // dump_type 只有 internal/external 两个合法值，表土落 external，细分在档案里
        Assert.Equal("external", Text(db.Connection, "SELECT dump_type FROM dump_site WHERE dump_id='D-T1'"));
        Assert.Equal("TopsoilYard", Text(db.Connection, "SELECT sink_kind FROM sink_profile WHERE sink_id='D-T1'"));
    }

    [Fact]
    public void 校验_空编号拦下并说清原因()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var r = SinkRegistryLoader.Save(db.Connection, new[] { new SinkNode { Id = "", Name = "无编号" } });
        Assert.Equal(1, r.Failed);
        Assert.Contains("去向编号为空", r.Errors[0]);
    }

    [Fact]
    public void 校验_没连接与没输入是两种中止()
    {
        Assert.Contains("没有可保存的去向", SinkRegistryLoader.Save(null, Array.Empty<SinkNode>()).AbortReason);
        Assert.Contains("没有数据库连接", SinkRegistryLoader.Save(null, new[] { Dump("D-N1") }).AbortReason);
    }

    // ── 存读往返 ────────────────────────────────────────────
    [Fact]
    public void 往返_存进去再读回来一模一样()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1", "北排土场", 88e4);
        s.Kind = SinkKind.InternalDump;
        s.WorkLineLengthM = 450; s.ActiveBenchLevel = 3; s.FallbackHaulKm = 1.2;
        s.OpenFromHour = 6; s.OpenToHour = 18; s.OpenFromPeriod = "2027Q2";
        s.AcceptTph = 900; s.BenchHeightM = 12; s.BenchSlopeAngleDeg = 38;
        s.AcceptedMaterials.Add("rock"); s.AcceptedMaterials.Add("weathered");
        s.X = 1000; s.Y = 2000; s.Z = 55;
        Assert.True(SinkRegistryLoader.Save(db.Connection, new[] { s }).Ok);

        var back = SinkRegistryLoader.Load(db.Connection).Find("D-N1")!;
        Assert.Equal(SinkKind.InternalDump, back.Kind);
        Assert.Equal(88e4, back.DesignCapacityM3, 3);
        Assert.Equal(900, back.AcceptTph, 6);
        Assert.Equal(450, back.WorkLineLengthM, 6);
        Assert.Equal(3, back.ActiveBenchLevel);
        Assert.Equal(1.2, back.FallbackHaulKm, 6);
        Assert.Equal((6.0, 18.0), (back.OpenFromHour, back.OpenToHour));
        Assert.Equal("2027Q2", back.OpenFromPeriod);
        Assert.Equal(12, back.BenchHeightM, 6);
        Assert.Equal(38, back.BenchSlopeAngleDeg, 6);
        Assert.Equal(new[] { "rock", "weathered" }, back.AcceptedMaterials.OrderBy(x => x));
        Assert.Equal((1000.0, 2000.0, 55.0), (back.X, back.Y, back.Z));
    }

    [Fact]
    public void 往返_卸载点坐标两处都落一份但读回以本体为准()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = SinkRegistryLoader.CreateNew(SinkKind.Crusher, "1号破碎站");
        s.X = 111; s.Y = 222; s.Z = 33;
        SinkRegistryLoader.Save(db.Connection, new[] { s });

        // 档案行里同样存一份：不至于留一串 0 看着像数据丢了，两边对不上时有据可查
        Assert.Equal(111, Scalar(db.Connection, $"SELECT x FROM sink_profile WHERE sink_id='{s.Id}'"), 6);
        Assert.Equal(111, Scalar(db.Connection, "SELECT x FROM load_unload_point"), 6);

        // 但读回来只认本体表的：把档案那份改掉，读出来仍是本体的值
        Exec(db.Connection, $"UPDATE sink_profile SET x = 999 WHERE sink_id='{s.Id}'");
        Assert.Equal(111, SinkRegistryLoader.Load(db.Connection).Find(s.Id)!.X, 6);
    }

    [Fact]
    public void 往返_时窗改回全天存得下来()
    {
        // 0/24 是有效取值；用 >0 过滤会让"改回全天"这个动作永远存不下来
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.OpenFromHour = 8; s.OpenToHour = 20;
        SinkRegistryLoader.Save(db.Connection, new[] { s });

        s.OpenFromHour = 0; s.OpenToHour = 24;
        SinkRegistryLoader.Save(db.Connection, new[] { s });
        var back = SinkRegistryLoader.Load(db.Connection).Find("D-N1")!;
        Assert.Equal((0.0, 24.0), (back.OpenFromHour, back.OpenToHour));
    }

    [Fact]
    public void 往返_脏坐标不进库()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.X = double.NaN; s.Y = double.PositiveInfinity; s.Z = 5;
        Assert.True(SinkRegistryLoader.Save(db.Connection, new[] { s }).Ok);
        Assert.Equal(0, Scalar(db.Connection, "SELECT x FROM sink_profile WHERE sink_id='D-N1'"), 6);
        Assert.Equal(0, Scalar(db.Connection, "SELECT y FROM sink_profile WHERE sink_id='D-N1'"), 6);
        Assert.Equal(5, Scalar(db.Connection, "SELECT z FROM sink_profile WHERE sink_id='D-N1'"), 6);
    }

    [Fact]
    public void 往返_名字里的单引号不炸SQL()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1", "O'Brien 排土场");
        Assert.True(SinkRegistryLoader.Save(db.Connection, new[] { s }).Ok);
        Assert.Equal("O'Brien 排土场", SinkRegistryLoader.Load(db.Connection).Find("D-N1")!.Name);
    }

    [Fact]
    public void 往返_脏物料码不进库()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.AcceptedMaterials.Add("rock");
        s.AcceptedMaterials.Add("根本不存在的码");
        SinkRegistryLoader.Save(db.Connection, new[] { s });
        Assert.Equal("rock", Text(db.Connection, "SELECT accepted_materials FROM sink_profile WHERE sink_id='D-N1'"));
    }

    // ── 删除 ────────────────────────────────────────────────
    [Fact]
    public void 删除_本体与档案一起删()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        SinkRegistryLoader.Save(db.Connection, new[] { s });

        var r = SinkRegistryLoader.Delete(db.Connection, s);
        Assert.True(r.Ok, r.Caption);
        Assert.Equal(1, r.Deleted);
        Assert.Equal(0, Scalar(db.Connection, "SELECT COUNT(*) FROM dump_site"));
        Assert.Equal(0, Scalar(db.Connection, "SELECT COUNT(*) FROM sink_profile"));
    }

    [Fact]
    public void 删除_卸载点走另一张表()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = SinkRegistryLoader.CreateNew(SinkKind.Crusher, "1号破碎站");
        SinkRegistryLoader.Save(db.Connection, new[] { s });
        Assert.True(SinkRegistryLoader.Delete(db.Connection, s).Ok);
        Assert.Equal(0, Scalar(db.Connection, "SELECT COUNT(*) FROM load_unload_point"));
    }

    [Fact]
    public void 删除_编号解析不出时不瞎删()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var bad = new SinkNode { Id = "LUP-新3", Name = "没入过库的", RefEntityId = "LUP-新3", Kind = SinkKind.Crusher };
        var r = SinkRegistryLoader.Delete(db.Connection, bad);
        Assert.Equal(1, r.Failed);
        Assert.Contains("解析不出", r.Errors[0]);
        Assert.Equal(0, r.Deleted);
    }

    [Fact]
    public void 删除_没指定去向时中止()
        => Assert.Contains("未指定", SinkRegistryLoader.Delete(null, null).AbortReason);

    // ── 盘点 ────────────────────────────────────────────────
    [Fact]
    public void 盘点_改账并留流水()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.FilledM3 = 10e4;
        SinkRegistryLoader.Save(db.Connection, new[] { s });

        var r = SinkRegistryLoader.Stocktake(db.Connection, s, 42e4, "实测扫描修正", "张三");
        Assert.True(r.Ok, r.Caption);
        Assert.Equal(42, Scalar(db.Connection, "SELECT current_filled_wan_m3 FROM dump_site WHERE dump_id='D-N1'"), 6);
        Assert.Equal(42e4, s.FilledM3, 3);      // 内存同步，UI 立刻看到新充填率
        Assert.Equal(1, Scalar(db.Connection, "SELECT COUNT(*) FROM sink_stocktake"));
        Assert.Equal(10e4, Scalar(db.Connection, "SELECT before_filled_m3 FROM sink_stocktake"), 3);
        Assert.Equal(32e4, Scalar(db.Connection, "SELECT delta_m3 FROM sink_stocktake"), 3);
        Assert.Equal("实测扫描修正", Text(db.Connection, "SELECT reason FROM sink_stocktake"));
        Assert.Equal("张三", Text(db.Connection, "SELECT operator FROM sink_stocktake"));
    }

    [Fact]
    public void 盘点_没有原因不许改账()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        SinkRegistryLoader.Save(db.Connection, new[] { s });
        var r = SinkRegistryLoader.Stocktake(db.Connection, s, 42e4, "  ");
        Assert.True(r.Aborted);
        Assert.Contains("无原因不许改账", r.AbortReason);
        Assert.Equal(0, Scalar(db.Connection, "SELECT COUNT(*) FROM sink_stocktake"));
    }

    [Fact]
    public void 盘点_通过型去向不占库容故不许盘点()
    {
        var c = new SinkNode { Id = "LUP-1", Kind = SinkKind.Crusher, RefEntityId = "LUP-1" };
        var r = SinkRegistryLoader.Stocktake(null, c, 1, "试试");
        Assert.True(r.Aborted);
        Assert.Contains("不占排土库容", r.AbortReason);
    }

    [Fact]
    public void 盘点_负数与非数都拦下()
    {
        var s = Dump("D-N1");
        Assert.True(SinkRegistryLoader.Stocktake(null, s, -1, "x").Aborted);
        Assert.True(SinkRegistryLoader.Stocktake(null, s, double.NaN, "x").Aborted);
        Assert.True(SinkRegistryLoader.Stocktake(null, s, double.PositiveInfinity, "x").Aborted);
    }

    [Fact]
    public void 盘点_台账里没建档时不静默新建()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var r = SinkRegistryLoader.Stocktake(db.Connection, Dump("D-NOPE"), 1e4, "补录");
        Assert.Equal(1, r.Failed);
        Assert.Contains("请先保存台账建档", r.Errors[0]);
        Assert.Equal(0, Scalar(db.Connection, "SELECT COUNT(*) FROM dump_site"));
    }

    // ── 实绩回灌 ────────────────────────────────────────────
    [Fact]
    public void 回灌_内存与台账同步()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = Dump("D-N1");
        s.FilledM3 = 10e4;
        SinkRegistryLoader.Save(db.Connection, new[] { s });

        var reg = SinkRegistryLoader.Load(db.Connection);          // 顺带把 FromDatabase 置真
        double rate = SinkRegistryLoader.AddFilled(db.Connection, reg, "D-N1", 5e4);
        Assert.Equal(0.15, rate, 6);                                // (10+5)/100
        Assert.Equal(15e4, reg.Find("D-N1")!.FilledM3, 3);
        Assert.Equal(15, Scalar(db.Connection, "SELECT current_filled_wan_m3 FROM dump_site WHERE dump_id='D-N1'"), 6);
    }

    [Fact]
    public void 回灌_通过型去向不回写台账()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var s = SinkRegistryLoader.CreateNew(SinkKind.Crusher, "1号破碎站");
        SinkRegistryLoader.Save(db.Connection, new[] { s });
        var reg = SinkRegistryLoader.Load(db.Connection);
        // 不占库容 ⇒ 充填率恒 0，且没有可回写的列（不该因此抛）
        Assert.Equal(0, SinkRegistryLoader.AddFilled(db.Connection, reg, s.Id, 5e4), 6);
        Assert.Equal(5e4, reg.Find(s.Id)!.FilledM3, 3);             // 内存仍如实累计
    }

    [Fact]
    public void 回灌_去向不存在返回零()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        Assert.Equal(0, SinkRegistryLoader.AddFilled(db.Connection, new SinkRegistry(), "没有这个", 1e4), 6);
    }

    [Fact]
    public void 回灌_没有连接时只更新内存不抛()
    {
        var reg = new SinkRegistry();
        var s = Dump("D-N1");
        reg.Put(s);
        Assert.Equal(0.05, SinkRegistryLoader.AddFilled(null, reg, "D-N1", 5e4), 6);
        Assert.Equal(5e4, s.FilledM3, 3);
    }

    // ── 结果文案 ────────────────────────────────────────────
    [Fact]
    public void 结果文案_把成败都说清楚()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var ok = Dump("D-A", "甲场");
        var bad = Dump("D-B", "乙场"); bad.Status = "半开";
        var r = SinkRegistryLoader.Save(db.Connection, new[] { ok, bad });

        Assert.Equal(1, r.Inserted);
        Assert.Equal(1, r.Failed);
        Assert.False(r.Ok);
        Assert.Contains("新增 1 条", r.Caption);
        Assert.Contains("失败 1 条", r.Caption);
        Assert.Contains("乙场", r.Caption);       // 哪一条败的要点名
    }

    [Fact]
    public void 结果文案_中止与失败分得开()
    {
        var aborted = SinkRegistryLoader.Save(null, new[] { Dump("D-N1") });
        Assert.True(aborted.Aborted);
        Assert.Equal(0, aborted.Failed);          // 一条都没跑起来 ≠ 跑了但败了
        Assert.StartsWith("未保存：", aborted.Caption);
    }

    [Fact]
    public void 结果文案_无改动也有话说()
    {
        var r = new SinkRegistryLoader.SinkSaveResult();
        Assert.Equal("无改动", r.Caption);
        Assert.True(r.Ok);
    }
}
