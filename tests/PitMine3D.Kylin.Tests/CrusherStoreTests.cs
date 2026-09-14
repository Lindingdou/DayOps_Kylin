using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 破碎站子集写入器（§三三八）。这个类的存在理由就是**别把 <c>load_unload_point</c> 整表清掉**，
/// 故本文件的判据几乎全是"什么不该发生"：
///
///   · 非破碎站的记录（采剥点 / 排土场 / 储矿场）**一条都不许被碰**；
///   · 更新走 UPDATE 而不是删了重插 —— <b>主键必须不变</b>，
///     否则去向台账的 <c>LUP-{id}</c> 引用（<c>sink_profile.sink_id</c>）会集体错位；
///   · 删除权限只覆盖**窗口打开时看见的那批**，别处新建的破碎站不许被顺手删掉。
///
/// 全部走 SQLite 测试库。
/// </summary>
public class CrusherStoreTests
{
    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Add(DbConnection c, string name, string kind, string? sub,
                            double x = 0, double y = 0, double z = 0, double tph = 0)
    {
        Exec(c, $"INSERT INTO load_unload_point (name, kind, unload_sub, x, y, z, throughput_tph) VALUES ("
              + $"'{name}','{kind}',{(sub == null ? "NULL" : "'" + sub + "'")},{x},{y},{z},{tph})");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(id) FROM load_unload_point";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long Crusher(DbConnection c, string name, double x = 0, double y = 0, double tph = 0)
        => Add(c, name, "unloading", "crusher", x, y, 0, tph);

    private static void Clear(DbConnection c)
    {
        Exec(c, "DELETE FROM load_unload_point");
        Exec(c, "DELETE FROM sink_profile");
        Exec(c, "DELETE FROM dump_site");
    }

    private static long Count(DbConnection c, string where = "1=1")
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM load_unload_point WHERE " + where;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string Text(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    // ── 判别 ────────────────────────────────────────────────
    [Theory]
    [InlineData("unloading", "crusher", true)]
    [InlineData("UNLOADING", "CRUSHER", true)]      // 大小写不敏感
    [InlineData("unloading", "dump", false)]
    [InlineData("unloading", "stockpile", false)]
    [InlineData("unloading", null, false)]
    [InlineData("loading", "crusher", false)]        // 源不是汇
    [InlineData("", "", false)]
    public void 判别_只有卸载点里的破碎站算数(string kind, string? sub, bool expect)
        => Assert.Equal(expect, CrusherStore.IsCrusher(kind, sub));

    // ── 读种子 ──────────────────────────────────────────────
    [Fact]
    public void 播种_只读破碎站不读别的类别()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Crusher(db.Connection, "C1", 100, 200, 1500);
        Add(db.Connection, "北采区", "loading", null);
        Add(db.Connection, "北排土场", "unloading", "dump");
        Add(db.Connection, "储煤场", "unloading", "stockpile");

        var seed = CrusherStore.LoadCrushers(db.Connection);
        Assert.Single(seed);
        Assert.Equal("C1", seed[0].Name);
        Assert.Equal((100.0, 200.0, 1500.0), (seed[0].X, seed[0].Y, seed[0].ThroughputTph));
        Assert.True(seed[0].Id > 0);        // 主键要带回来, 否则 UPDATE/DELETE 定不了位
    }

    [Fact]
    public void 播种_没有连接时返回空而不是崩()
        => Assert.Empty(CrusherStore.LoadCrushers(null));

    // ── 增量 CRUD ───────────────────────────────────────────
    [Fact]
    public void 保存_新破碎站按插入算()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("C1", 10, 20, 30, 1500) }, Array.Empty<string>());

        Assert.Equal(new CrusherSaveResult(1, 0, 0), r);
        Assert.Equal(1, Count(db.Connection));
        Assert.Equal("crusher", Text(db.Connection, "SELECT unload_sub FROM load_unload_point"));
        Assert.Equal("unloading", Text(db.Connection, "SELECT kind FROM load_unload_point"));
    }

    [Fact]
    public void 保存_已有的按名匹配走更新且主键不变()
    {
        // ★ 本文件最要紧的一条：去向台账用 LUP-{id} 存引用，重新编号会让卸载点档案集体错位
        using var db = TestDb.Open();
        Clear(db.Connection);
        long id = Crusher(db.Connection, "C1", 1, 1, 100);

        var r = CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("C1", 999, 888, 77, 2000) }, new[] { "C1" });

        Assert.Equal(new CrusherSaveResult(0, 1, 0), r);
        Assert.Equal(1, Count(db.Connection));
        Assert.Equal(id.ToString(CultureInfo.InvariantCulture),
                     Text(db.Connection, "SELECT id FROM load_unload_point WHERE name='C1'"));   // 主键没变
        Assert.Equal("999", Text(db.Connection, "SELECT x FROM load_unload_point WHERE name='C1'"));
    }

    [Fact]
    public void 保存_更新不断去向台账的引用()
    {
        // 端到端把第 2 条事故演一遍：改破碎站位置后，台账里那条 LUP-{id} 档案还认得出这个去向
        using var db = TestDb.Open();
        Clear(db.Connection);
        long id = Crusher(db.Connection, "C1", 1, 1, 100);
        Exec(db.Connection, $"INSERT INTO sink_profile (sink_id, sink_kind, work_line_length_m) "
                          + $"VALUES ('LUP-{id}','Crusher',321)");

        CrusherStore.SaveScoped(db.Connection, new[] { new CrusherSpec("C1", 999, 888, 77, 2000) }, new[] { "C1" });

        var node = SinkRegistryLoader.Load(db.Connection).Find($"LUP-{id}");
        Assert.NotNull(node);
        Assert.Equal(321, node!.WorkLineLengthM, 6);     // 档案还挂得上 ⇒ 引用没断
        Assert.Equal(999, node.X, 6);                    // 新坐标也读得回来
    }

    [Fact]
    public void 保存_窗口里去掉的按删除算()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Crusher(db.Connection, "C1");
        Crusher(db.Connection, "C2");

        var r = CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("C1", 0, 0, 0, 0) }, new[] { "C1", "C2" });

        Assert.Equal(new CrusherSaveResult(0, 1, 1), r);
        Assert.Equal(0, Count(db.Connection, "name='C2'"));
    }

    // ── 处置权边界（这个类存在的理由）────────────────────────
    [Fact]
    public void 边界_非破碎站的记录一条都不许被碰()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        long src = Add(db.Connection, "北采区", "loading", null, 5, 6, 7, 80);
        long dmp = Add(db.Connection, "北排土场", "unloading", "dump", 8, 9, 10, 0);
        long stk = Add(db.Connection, "储煤场", "unloading", "stockpile", 11, 12, 13, 0);
        Crusher(db.Connection, "C1");

        // 窗口里一行都不剩 —— 若退回"清表重插"，上面三条会被整片抹掉
        var r = CrusherStore.SaveScoped(db.Connection, Array.Empty<CrusherSpec>(), new[] { "C1" });

        Assert.Equal(1, r.Deleted);
        Assert.Equal(3, Count(db.Connection));
        Assert.Equal(0, Count(db.Connection, "unload_sub='crusher'"));
        // 主键也没动过
        Assert.Equal(1, Count(db.Connection, $"id={src} AND name='北采区' AND kind='loading'"));
        Assert.Equal(1, Count(db.Connection, $"id={dmp} AND name='北排土场' AND unload_sub='dump'"));
        Assert.Equal(1, Count(db.Connection, $"id={stk} AND name='储煤场' AND unload_sub='stockpile'"));
    }

    [Fact]
    public void 边界_别处新建的破碎站不许被顺手删掉()
    {
        // ★ 交叉删除：本窗口开着的这段时间里，「去向台账」新建了一座破碎站。
        //   若按"rows 即全集"删，它会在本窗口一存的时候被静默删掉 —— 现场只会看到"我明明建过"。
        using var db = TestDb.Open();
        Clear(db.Connection);
        Crusher(db.Connection, "C1");
        var seeded = new[] { "C1" };            // 窗口打开时只看见 C1

        Crusher(db.Connection, "C9");           // 期间别处新建

        var r = CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("C1", 0, 0, 0, 0) }, seeded);

        Assert.Equal(0, r.Deleted);
        Assert.Equal(1, Count(db.Connection, "name='C9'"));   // 还在
    }

    [Fact]
    public void 边界_种子集合为空时一条都不删()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Crusher(db.Connection, "C1");
        // 库不可用时窗口不播种（见 LoadCrushers 注释）⇒ 删除权限为空，只能新增
        var r = CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("C2", 0, 0, 0, 0) }, Array.Empty<string>());
        Assert.Equal(new CrusherSaveResult(1, 0, 0), r);
        Assert.Equal(2, Count(db.Connection));
    }

    [Fact]
    public void 边界_种子里有但已被别处删掉的不报错()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = CrusherStore.SaveScoped(db.Connection, Array.Empty<CrusherSpec>(), new[] { "早没了" });
        Assert.Equal(new CrusherSaveResult(0, 0, 0), r);
    }

    // ── 兜底 ────────────────────────────────────────────────
    [Fact]
    public void 兜底_空名不落库()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("  ", 0, 0, 0, 0), new CrusherSpec("C1", 0, 0, 0, 0) }, Array.Empty<string>());
        Assert.Equal(1, r.Inserted);
        Assert.Equal(1, Count(db.Connection));
    }

    [Fact]
    public void 兜底_重名且库里已有时后者覆盖前者()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Crusher(db.Connection, "C1");
        var r = CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("C1", 1, 1, 1, 1), new CrusherSpec("C1", 2, 2, 2, 2) }, new[] { "C1" });
        Assert.Equal(2, r.Updated);                     // 同一行更新两次
        Assert.Equal(1, Count(db.Connection));
        Assert.Equal("2", Text(db.Connection, "SELECT x FROM load_unload_point WHERE name='C1'"));
    }

    [Fact]
    public void 兜底_两条都是新名的重名会各插一条_去重是窗口的责任()
    {
        // 口径与原版一致：existing 是**进循环前**快照的，第二条找不到刚插进去的那行 ⇒ 两条都 INSERT。
        // 原版的"重名时后者覆盖前者"说的是**库里已有**那种情形（上一条用例）。
        // 故 Name 去重由窗口负责（Commit 里已校验唯一），本层只保证不抛。
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("C1", 1, 1, 1, 1), new CrusherSpec("C1", 2, 2, 2, 2) }, Array.Empty<string>());
        Assert.Equal(2, r.Inserted);
        Assert.Equal(2, Count(db.Connection));
    }

    [Fact]
    public void 兜底_名字里的单引号不炸SQL()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        CrusherStore.SaveScoped(db.Connection, new[] { new CrusherSpec("O'Brien 站", 1, 2, 3, 4) }, Array.Empty<string>());
        Assert.Equal("O'Brien 站", CrusherStore.LoadCrushers(db.Connection).Single().Name);
    }

    [Fact]
    public void 兜底_空行集与空种子集的参数校验()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Equal(new CrusherSaveResult(0, 0, 0),
                     CrusherStore.SaveScoped(db.Connection, null, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => CrusherStore.SaveScoped(null!, Array.Empty<CrusherSpec>(), Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => CrusherStore.SaveScoped(db.Connection, Array.Empty<CrusherSpec>(), null!));
    }

    // ── 与去向台账联动 ──────────────────────────────────────
    [Fact]
    public void 联动_这里存的破碎站在去向台账里认得出来()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        CrusherStore.SaveScoped(db.Connection,
            new[] { new CrusherSpec("1号破碎站", 100, 200, 30, 1500) }, Array.Empty<string>());

        var reg = SinkRegistryLoader.Load(db.Connection);
        var node = reg.All.Single();
        Assert.Equal(SinkKind.Crusher, node.Kind);
        Assert.Equal(1500, node.AcceptTph, 6);
        Assert.Equal((100.0, 200.0, 30.0), (node.X, node.Y, node.Z));
        Assert.False(node.IsCapacityLimited);          // 通过型去向不占库容
        // 煤能进破碎站, 岩石不能 —— 接线通了, 编组/流向分配才有真去向可选
        Assert.Contains(node, reg.CandidatesFor(MaterialCatalog.Resolve("coal")));
        Assert.DoesNotContain(node, reg.CandidatesFor(MaterialCatalog.Resolve("rock")));
    }
}
