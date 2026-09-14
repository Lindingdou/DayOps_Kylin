using System;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 去向台账读取路径（§三三七）：<c>dump_site</c> + <c>load_unload_point(kind='unloading')</c>
/// + <c>sink_profile</c> → <see cref="SinkRegistry"/>。
///
/// 重点盯三处最容易搞反的：**万m³↔m³ 换算**、**坐标权威归属**、**档案只作同族细化**。
/// 全部走 SQLite 测试库，不碰真库。
/// </summary>
public class SinkRegistryLoaderTests
{
    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AddDump(DbConnection c, string id, string name, string type = "external",
                                double capWan = 100, double filledWan = 0, string status = "active",
                                double? benchH = null, double? slope = null)
    {
        Exec(c, "DELETE FROM dump_site WHERE dump_id = '" + id + "'");
        Exec(c, $"INSERT INTO dump_site (dump_id, name, dump_type, design_capacity_wan_m3, current_filled_wan_m3, "
              + $"bench_height_m, bench_slope_angle_deg, status) VALUES ('{id}','{name}','{type}',{capWan},{filledWan},"
              + $"{(benchH?.ToString() ?? "NULL")},{(slope?.ToString() ?? "NULL")},'{status}')");
    }

    private static long AddLup(DbConnection c, string name, string kind = "unloading", string? sub = null,
                               double x = 0, double y = 0, double z = 0, double tph = 0)
    {
        Exec(c, $"INSERT INTO load_unload_point (name, kind, unload_sub, x, y, z, throughput_tph) "
              + $"VALUES ('{name}','{kind}',{(sub == null ? "NULL" : "'" + sub + "'")},{x},{y},{z},{tph})");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(id) FROM load_unload_point";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void AddProfile(DbConnection c, string sinkId, string kind = "", string status = "active",
                                   double acceptTph = 0, string materials = "", double workLine = 0,
                                   int bench = 1, double fbKm = 0, double from = 0, double to = 24,
                                   string? period = null, double x = 0, double y = 0, double z = 0)
    {
        Exec(c, "DELETE FROM sink_profile WHERE sink_id = '" + sinkId + "'");
        Exec(c, $"INSERT INTO sink_profile (sink_id, sink_kind, status, accept_tph, accepted_materials, "
              + $"work_line_length_m, active_bench_level, fallback_haul_km, open_from_hour, open_to_hour, "
              + $"open_from_period, x, y, z) VALUES ('{sinkId}','{kind}','{status}',{acceptTph},'{materials}',"
              + $"{workLine},{bench},{fbKm},{from},{to},{(period == null ? "NULL" : "'" + period + "'")},{x},{y},{z})");
    }

    /// <summary>V004 给 dump_site 播了种；测哪条就先清干净，别让种子数据混进断言。</summary>
    private static void ClearAll(DbConnection c)
    {
        Exec(c, "DELETE FROM dump_site");
        Exec(c, "DELETE FROM load_unload_point");
        Exec(c, "DELETE FROM sink_profile");
    }

    // ── 单位换算 ────────────────────────────────────────────
    [Fact]
    public void 排土场_容量按万立方折成立方()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场", capWan: 120, filledWan: 30);

        var s = SinkRegistryLoader.Load(db.Connection).Find("D-N1");
        Assert.NotNull(s);
        Assert.Equal(120e4, s!.DesignCapacityM3, 6);     // ×1e4，做反了会差 4 个数量级
        Assert.Equal(30e4, s.FilledM3, 6);
        Assert.Equal(90e4, s.RemainingM3, 6);
        Assert.True(s.IsCapacityLimited);
    }

    [Fact]
    public void 排土场_台阶参数为空时用工程缺省值()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场");        // bench_height_m / slope 都是 NULL

        var s = SinkRegistryLoader.Load(db.Connection).Find("D-N1")!;
        Assert.Equal(SinkRegistryLoader.DefaultBenchHeightM, s.BenchHeightM, 6);
        Assert.Equal(SinkRegistryLoader.DefaultBenchSlopeDeg, s.BenchSlopeAngleDeg, 6);
    }

    [Fact]
    public void 排土场_录了台阶参数就用录的()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场", benchH: 12, slope: 38);

        var s = SinkRegistryLoader.Load(db.Connection).Find("D-N1")!;
        Assert.Equal(12, s.BenchHeightM, 6);
        Assert.Equal(38, s.BenchSlopeAngleDeg, 6);
    }

    // ── 类型判定 ────────────────────────────────────────────
    [Theory]
    [InlineData("北排土场", "external", SinkKind.ExternalDump)]
    [InlineData("内排土场", "internal", SinkKind.InternalDump)]
    [InlineData("表土堆场", "external", SinkKind.TopsoilYard)]
    [InlineData("腐殖土场", "internal", SinkKind.TopsoilYard)]   // 名字优先于 dump_type
    public void 排土场_类型判定(string name, string type, SinkKind expect)
        => Assert.Equal(expect, SinkRegistryLoader.DumpKindOf(name, type));

    [Fact]
    public void 排土场_表土名字压过内排列()
    {
        // 表土必须单独堆存供复垦：即使 dump_type 填的 internal，也不能归成内排土场
        Assert.Equal(SinkKind.TopsoilYard, SinkRegistryLoader.DumpKindOf("南表土场", "internal"));
    }

    [Theory]
    [InlineData("1号原煤仓", null, SinkKind.Silo)]
    [InlineData("Silo-A", "crusher", SinkKind.Silo)]          // 名字含 silo 压过子类
    [InlineData("半固定破碎站", null, SinkKind.Crusher)]
    [InlineData("卸载点3", "crusher", SinkKind.Crusher)]
    [InlineData("储煤堆场", null, SinkKind.Stockpile)]
    [InlineData("卸载点4", "stockpile", SinkKind.Stockpile)]
    [InlineData("表土卸点", "dump", SinkKind.TopsoilYard)]
    [InlineData("内排卸点", "dump", SinkKind.InternalDump)]
    [InlineData("卸载点9", null, SinkKind.ExternalDump)]       // 都判不出 → 外排
    public void 卸载点_类型判定(string name, string? sub, SinkKind expect)
        => Assert.Equal(expect, SinkRegistryLoader.UnloadKindOf(name, sub));

    // ── 卸载点 ──────────────────────────────────────────────
    [Fact]
    public void 卸载点_只收unloading并加LUP前缀()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        long a = AddLup(db.Connection, "1号破碎站", "unloading", "crusher", 100, 200, 30, tph: 1500);
        AddLup(db.Connection, "北采区采装点", "loading");     // 源，不该进登记簿

        var reg = SinkRegistryLoader.Load(db.Connection);
        Assert.Single(reg.All);
        var s = reg.Find($"LUP-{a}");
        Assert.NotNull(s);
        Assert.Equal(SinkKind.Crusher, s!.Kind);
        Assert.Equal(1500, s.AcceptTph, 6);
        Assert.Equal((100, 200, 30), (s.X, s.Y, s.Z));
    }

    [Fact]
    public void 卸载点_通过型去向不占库容()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        long a = AddLup(db.Connection, "1号破碎站", sub: "crusher");

        var s = SinkRegistryLoader.Load(db.Connection).Find($"LUP-{a}")!;
        Assert.False(s.IsCapacityLimited);
        Assert.Equal(double.PositiveInfinity, s.RemainingM3);   // 卸多少走多少
    }

    [Fact]
    public void 卸载点_没名字时用编号兜底()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        long a = AddLup(db.Connection, "");
        Assert.Equal($"卸载点{a}", SinkRegistryLoader.Load(db.Connection).Find($"LUP-{a}")!.Name);
    }

    // ── 兜底运距 ────────────────────────────────────────────
    [Fact]
    public void 兜底运距_内排明显近于外排()
    {
        // 内排在采空区里 —— 这正是内排降本的由来；量级搞反会让内外排优选整个失效
        Assert.True(SinkRegistryLoader.FallbackKmOf(SinkKind.InternalDump)
                  < SinkRegistryLoader.FallbackKmOf(SinkKind.ExternalDump));
        Assert.Equal(1.5, SinkRegistryLoader.FallbackKmOf(SinkKind.InternalDump), 6);
        Assert.Equal(2.0, SinkRegistryLoader.FallbackKmOf(SinkKind.TopsoilYard), 6);
        Assert.Equal(2.5, SinkRegistryLoader.FallbackKmOf(SinkKind.Crusher), 6);
        Assert.Equal(2.5, SinkRegistryLoader.FallbackKmOf(SinkKind.Stockpile), 6);
        Assert.Equal(3.0, SinkRegistryLoader.FallbackKmOf(SinkKind.Silo), 6);
        Assert.Equal(3.0, SinkRegistryLoader.FallbackKmOf(SinkKind.ExternalDump), 6);
    }

    // ── 档案细化 ────────────────────────────────────────────
    [Fact]
    public void 档案_排土场坐标从档案补回()
    {
        // dump_site 没有坐标列，sink_profile 是它唯一的家
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场");
        AddProfile(db.Connection, "D-N1", x: 1000, y: 2000, z: 55);

        var s = SinkRegistryLoader.Load(db.Connection).Find("D-N1")!;
        Assert.Equal((1000.0, 2000.0, 55.0), (s.X, s.Y, s.Z));
    }

    [Fact]
    public void 档案_排土场没档案时坐标保持零而不是猜()
    {
        // 0 = "没录坐标"，调用方据此判无坐标；瞎猜一个会算出看着像真的假运距
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场");
        var s = SinkRegistryLoader.Load(db.Connection).Find("D-N1")!;
        Assert.Equal((0.0, 0.0, 0.0), (s.X, s.Y, s.Z));
    }

    [Fact]
    public void 档案_卸载点坐标不被档案盖掉()
    {
        // ★ 本文件最要紧的一条：本体表才是卸载点坐标的权威，档案里那份可能是过期副本
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        long a = AddLup(db.Connection, "1号破碎站", sub: "crusher", x: 111, y: 222, z: 33);
        AddProfile(db.Connection, $"LUP-{a}", x: 999, y: 888, z: 77);

        var s = SinkRegistryLoader.Load(db.Connection).Find($"LUP-{a}")!;
        Assert.Equal((111.0, 222.0, 33.0), (s.X, s.Y, s.Z));
    }

    [Fact]
    public void 档案_类型只在同族内细化()
    {
        // 排弃类 → 排弃类：认
        var d = new SinkNode { Id = "D-N1", Kind = SinkKind.ExternalDump, RefEntityId = "D-N1" };
        SinkRegistryLoader.ApplyProfile(d, "TopsoilYard", "", 0, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal(SinkKind.TopsoilYard, d.Kind);

        // 排弃类 → 通过型：不认（行躺在 dump_site 是物理事实，它不可能是破碎站）
        var d2 = new SinkNode { Id = "D-N1", Kind = SinkKind.ExternalDump, RefEntityId = "D-N1" };
        SinkRegistryLoader.ApplyProfile(d2, "Crusher", "", 0, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal(SinkKind.ExternalDump, d2.Kind);

        // 通过型 → 通过型：认
        var l = new SinkNode { Id = "LUP-1", Kind = SinkKind.Crusher, RefEntityId = "LUP-1" };
        SinkRegistryLoader.ApplyProfile(l, "Silo", "", 0, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal(SinkKind.Silo, l.Kind);
    }

    [Fact]
    public void 档案_状态只对卸载点权威()
    {
        // dump_site 自带 status，「排土场管理」也在改它；load_unload_point 没有 status 列
        var d = new SinkNode { Id = "D-N1", RefEntityId = "D-N1", Status = "full" };
        SinkRegistryLoader.ApplyProfile(d, "", "active", 0, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal("full", d.Status);

        var l = new SinkNode { Id = "LUP-1", RefEntityId = "LUP-1", Status = "active" };
        SinkRegistryLoader.ApplyProfile(l, "", "closed", 0, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal("closed", l.Status);
    }

    [Fact]
    public void 档案_通过能力只对排土场权威()
    {
        // 卸载点侧以 load_unload_point.throughput_tph 为准；排土场表根本没这一列
        var d = new SinkNode { Id = "D-N1", RefEntityId = "D-N1", AcceptTph = 0 };
        SinkRegistryLoader.ApplyProfile(d, "", "", 800, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal(800, d.AcceptTph, 6);

        var l = new SinkNode { Id = "LUP-1", RefEntityId = "LUP-1", AcceptTph = 1500 };
        SinkRegistryLoader.ApplyProfile(l, "", "", 800, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal(1500, l.AcceptTph, 6);
    }

    [Fact]
    public void 档案_时窗全天是有效取值()
    {
        // 0/24 不能被 >0 过滤掉，否则"改回全天"这个动作永远存不下来
        var s = new SinkNode { Id = "D-N1", RefEntityId = "D-N1", OpenFromHour = 8, OpenToHour = 20 };
        SinkRegistryLoader.ApplyProfile(s, "", "", 0, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal(0, s.OpenFromHour, 6);
        Assert.Equal(24, s.OpenToHour, 6);
    }

    [Fact]
    public void 档案_时窗非法时保持原值()
    {
        var s = new SinkNode { Id = "D-N1", RefEntityId = "D-N1", OpenFromHour = 8, OpenToHour = 20 };
        SinkRegistryLoader.ApplyProfile(s, "", "", 0, "", 0, 0, 0, 18, 6, null, 0, 0, 0);   // 起 > 止
        Assert.Equal(8, s.OpenFromHour, 6);
        Assert.Equal(20, s.OpenToHour, 6);
    }

    [Fact]
    public void 档案_零值字段不覆盖缺省()
    {
        // "没在台账里编辑过" ≠ "被清零"
        var s = new SinkNode
        { Id = "D-N1", RefEntityId = "D-N1", WorkLineLengthM = 300, ActiveBenchLevel = 3, FallbackHaulKm = 1.5 };
        SinkRegistryLoader.ApplyProfile(s, "", "", 0, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Equal(300, s.WorkLineLengthM, 6);
        Assert.Equal(3, s.ActiveBenchLevel);
        Assert.Equal(1.5, s.FallbackHaulKm, 6);
    }

    [Fact]
    public void 档案_录了值就覆盖()
    {
        var s = new SinkNode { Id = "D-N1", RefEntityId = "D-N1" };
        SinkRegistryLoader.ApplyProfile(s, "", "", 0, "", 450, 4, 2.2, 0, 24, "2027Q2", 0, 0, 0);
        Assert.Equal(450, s.WorkLineLengthM, 6);
        Assert.Equal(4, s.ActiveBenchLevel);
        Assert.Equal(2.2, s.FallbackHaulKm, 6);
        Assert.Equal("2027Q2", s.OpenFromPeriod);
        // 工作线长录上之后，按量反算推进距离才算得出来
        Assert.True(s.AdvanceMetersFor(450 * 20 * 10) > 0);
    }

    [Fact]
    public void 档案_孤儿行被忽略()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场");
        AddProfile(db.Connection, "D-XX", workLine: 999);      // 本体表里没有这个去向

        var reg = SinkRegistryLoader.Load(db.Connection);
        Assert.Single(reg.All);
        Assert.Null(reg.Find("D-XX"));
    }

    // ── 可接物料 ────────────────────────────────────────────
    [Fact]
    public void 可接物料_多种分隔符都认()
    {
        var set = SinkRegistryLoader.ParseMaterials("coal, rock;topsoil");
        Assert.Equal(3, set.Count);
        Assert.Contains("coal", set);
    }

    [Fact]
    public void 可接物料_脏码丢弃而不是让白名单变成什么都不收()
    {
        var set = SinkRegistryLoader.ParseMaterials("coal, 不存在的物料码xyz");
        Assert.Single(set);
        Assert.Contains("coal", set);
    }

    [Fact]
    public void 可接物料_空串等于不设白名单()
    {
        var s = new SinkNode { Id = "D-N1", RefEntityId = "D-N1", Kind = SinkKind.ExternalDump };
        SinkRegistryLoader.ApplyProfile(s, "", "", 0, "", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.Empty(s.AcceptedMaterials);
        // 白名单为空 ⇒ 退回物料自身的 AllowedSinks 判定，而不是"什么都不收"
        Assert.True(s.Accepts(MaterialCatalog.Resolve("rock")));
    }

    [Fact]
    public void 可接物料_白名单收窄后不接白名单外的()
    {
        var s = new SinkNode { Id = "D-N1", RefEntityId = "D-N1", Kind = SinkKind.ExternalDump };
        SinkRegistryLoader.ApplyProfile(s, "", "", 0, "topsoil", 0, 0, 0, 0, 24, null, 0, 0, 0);
        Assert.True(s.Accepts(MaterialCatalog.Resolve("topsoil")));
        Assert.False(s.Accepts(MaterialCatalog.Resolve("rock")));
    }

    // ── 容错与来源文案 ──────────────────────────────────────
    [Fact]
    public void 容错_没有连接时回落空登记簿并说明原因()
    {
        var reg = SinkRegistryLoader.Load(null);
        Assert.Empty(reg.All);
        Assert.False(SinkRegistryLoader.FromDatabase);
        Assert.Contains("未接通", SinkRegistryLoader.LastSourceLabel);
    }

    [Fact]
    public void 容错_两张表都空时回落并说明去哪儿录()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var reg = SinkRegistryLoader.Load(db.Connection);
        Assert.Empty(reg.All);
        Assert.False(SinkRegistryLoader.FromDatabase);
        Assert.Contains("空表", SinkRegistryLoader.LastSourceLabel);
        Assert.Contains("排土场管理", SinkRegistryLoader.LastSourceLabel);   // 告诉人去哪儿补数据
    }

    [Fact]
    public void 来源文案_读到了就把条数写清楚()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场");
        AddDump(db.Connection, "D-S1", "南排土场", "internal");
        AddLup(db.Connection, "1号破碎站", sub: "crusher");
        AddProfile(db.Connection, "D-N1", workLine: 400);

        var reg = SinkRegistryLoader.Load(db.Connection);
        Assert.Equal(3, reg.All.Count);
        Assert.True(SinkRegistryLoader.FromDatabase);
        string lbl = SinkRegistryLoader.LastSourceLabel;
        Assert.Contains("3 个去向", lbl);
        Assert.Contains("排土场 2 + 卸载点 1", lbl);
        Assert.Contains("扩展档案 1", lbl);
    }

    [Fact]
    public void 已排满的排土场也进登记簿只是不活跃()
    {
        // 库容校核要看得见"已排满"，不能在读取时就把它过滤掉
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场", status: "full", capWan: 100, filledWan: 100);

        var reg = SinkRegistryLoader.Load(db.Connection);
        var s = reg.Find("D-N1")!;
        Assert.Equal("full", s.Status);
        Assert.False(s.IsActive);
        Assert.Empty(reg.Active);            // 派工不会选它
        Assert.Single(reg.All);              // 但账上看得见
    }

    [Fact]
    public void 端到端_排土场与卸载点主键不撞车()
    {
        // dump_id 是 TEXT、装卸点主键是自增整数：不加 LUP- 前缀迟早撞上
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "1", "编号就叫1的排土场");
        long a = AddLup(db.Connection, "1号破碎站", sub: "crusher");
        Assert.Equal(1, a);                                        // 自增也是 1

        var reg = SinkRegistryLoader.Load(db.Connection);
        Assert.Equal(2, reg.All.Count);
        Assert.Equal("编号就叫1的排土场", reg.Find("1")!.Name);
        Assert.Equal("1号破碎站", reg.Find("LUP-1")!.Name);
    }

    [Fact]
    public void 端到端_候选去向按物料筛得出来()
    {
        // 这条是接线的意义所在：接上台账之后，编组/流向分配才有真去向可选
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddDump(db.Connection, "D-N1", "北排土场");
        AddDump(db.Connection, "D-T1", "表土堆场");
        AddLup(db.Connection, "1号原煤仓", sub: "dump");

        var reg = SinkRegistryLoader.Load(db.Connection);
        var forRock = reg.CandidatesFor(MaterialCatalog.Resolve("rock")).Select(s => s.Id).ToList();
        Assert.Contains("D-N1", forRock);
        Assert.DoesNotContain("LUP-1", forRock);          // 岩石不进煤仓
    }
}
