using System;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 当日入方（§三三七）：<c>working_face_routing</c> 的采装面当日目标按去向汇总。
///
/// 盯两处：**实方→占容→吨量的三步换算**（口径与 <see cref="MaterialFlow"/> 同一处公式），
/// 以及**混采面按 splits_json 拆流**。全部走 SQLite 测试库。
/// </summary>
public class SinkInboundTests
{
    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AddFace(DbConnection c, string face, string process = "Load", string material = "rock",
                                string destId = "D-N1", string destName = "北排土场", string destKind = "ExternalDump",
                                double target = 10000, string splits = "[]", double km = 2)
    {
        Exec(c, $"DELETE FROM working_face_routing WHERE face_code = '{face}'");
        Exec(c, "INSERT INTO working_face_routing (face_code, process, material_code, destination_id, destination_name, "
              + "destination_kind, haul_distance_km, equiv_haul_km, day_target_m3, splits_json) VALUES ("
              + $"'{face}','{process}','{material}','{destId}','{destName}','{destKind}',{km},0,{target},'{splits}')");
    }

    private static void Clear(DbConnection c) => Exec(c, "DELETE FROM working_face_routing");

    // ── 换算口径 ────────────────────────────────────────────
    [Fact]
    public void 换算_实方按残余松散系数折占容按密度折吨()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", material: "rock", target: 10000);

        var acc = SinkInbound.Today(db.Connection, out string err).Single();
        Assert.Equal("", err);
        var rock = MaterialCatalog.Resolve("rock");
        Assert.Equal(10000, acc.PlanInSituM3, 6);
        Assert.Equal(rock.ToDumpM3(10000), acc.PlanDumpM3, 6);       // ★ 排土场按占容方扣库容
        Assert.Equal(rock.ToTonnage(10000), acc.PlanTonnageT, 6);
        Assert.True(acc.PlanDumpM3 > acc.PlanInSituM3);              // 岩石排出来一定比实方大
    }

    [Fact]
    public void 换算_煤与岩的占容不同()
    {
        // 同样 1 万实方，煤与硬岩的占容方、吨量都不一样 —— 口径混用会让库容账整体走样
        double coal = MaterialCatalog.Resolve("coal").ToDumpM3(10000);
        double rock = MaterialCatalog.Resolve("rock").ToDumpM3(10000);
        Assert.NotEqual(coal, rock, 3);
    }

    // ── 取数纪律 ────────────────────────────────────────────
    [Fact]
    public void 取数_只看采装面()
    {
        // 排土面的日目标由入方推导（derived_from_inbound），再算一遍就是重复计
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", process: "Load", target: 10000);
        AddFace(db.Connection, "F-2", process: "Dump", target: 99999);

        var flows = SinkInbound.FlowsFromRouting(db.Connection, out _);
        Assert.Single(flows);
        Assert.Equal("F-1", flows[0].SourceId);
    }

    [Fact]
    public void 取数_零目标的面不进账()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", target: 0);
        Assert.Empty(SinkInbound.FlowsFromRouting(db.Connection, out _));
    }

    [Fact]
    public void 取数_多面同去向合并成一条()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", target: 10000);
        AddFace(db.Connection, "F-2", target: 20000);

        var acc = SinkInbound.Today(db.Connection, out _).Single();
        Assert.Equal("D-N1", acc.SinkId);
        Assert.Equal(30000, acc.PlanInSituM3, 6);
    }

    [Fact]
    public void 取数_按占容方降序排_大头先跳出来()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", destId: "D-小", destName: "小场", target: 1000);
        AddFace(db.Connection, "F-2", destId: "D-大", destName: "大场", target: 50000);

        var list = SinkInbound.Today(db.Connection, out _);
        Assert.Equal("D-大", list[0].SinkId);
    }

    [Fact]
    public void 取数_没填去向的归到空键由界面单独提示()
    {
        // "没指定去向"不能悄悄丢掉：那些方量今天没有落点，正是要提醒的事
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", destId: "", destName: "", target: 10000);

        var acc = SinkInbound.Today(db.Connection, out _).Single();
        Assert.Equal("", acc.SinkId);
        Assert.Equal(10000, acc.PlanInSituM3, 6);
    }

    [Fact]
    public void 取数_没有连接时给出原因而不是崩()
    {
        Assert.Empty(SinkInbound.Today(null, out string err));
        Assert.Contains("没有数据库连接", err);
    }

    [Fact]
    public void 已排_Kylin无数据源故不冒充零()
    {
        // working_face_routing 只存当日目标, 没有实绩列 —— "判不了"必须与"0"分得开
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", target: 10000);
        var acc = SinkInbound.Today(db.Connection, out _).Single();
        Assert.False(acc.DoneKnown);
        Assert.Equal(0, acc.DoneDumpM3, 6);
        Assert.Equal(acc.PlanDumpM3, acc.PendingDumpM3, 6);   // 保守口径：今日一方都还没排
    }

    // ── 混采拆流 ────────────────────────────────────────────
    [Fact]
    public void 混采_按splits拆成多条流()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        string splits = "[{\"MaterialCode\":\"coal\",\"Fraction\":0.6,\"DestinationId\":\"LUP-1\","
                      + "\"DestinationName\":\"1号煤仓\",\"DestinationKind\":\"Silo\"},"
                      + "{\"MaterialCode\":\"rock\",\"Fraction\":0.4}]";
        AddFace(db.Connection, "F-1", material: "rock", target: 10000, splits: splits);

        var flows = SinkInbound.FlowsFromRouting(db.Connection, out _);
        Assert.Equal(2, flows.Count);

        var c = flows.Single(f => f.MaterialCode == "coal");
        Assert.Equal(6000, c.InSituM3, 6);
        Assert.Equal("LUP-1", c.SinkId);
        Assert.Equal(SinkKind.Silo, c.SinkKind);

        // 分项没自带去向 ⇒ 跟主去向走（连类型一起），不能落成枚举默认值
        var r = flows.Single(f => f.MaterialCode == "rock");
        Assert.Equal(4000, r.InSituM3, 6);
        Assert.Equal("D-N1", r.SinkId);
    }

    [Fact]
    public void 混采_拆开后两个去向各记各的()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        string splits = "[{\"MaterialCode\":\"coal\",\"Fraction\":0.6,\"DestinationId\":\"LUP-1\",\"DestinationName\":\"1号煤仓\",\"DestinationKind\":\"Silo\"},"
                      + "{\"MaterialCode\":\"rock\",\"Fraction\":0.4}]";
        AddFace(db.Connection, "F-1", target: 10000, splits: splits);

        var list = SinkInbound.Today(db.Connection, out _);
        Assert.Equal(2, list.Count);
        Assert.Equal(6000, list.Single(x => x.SinkId == "LUP-1").PlanInSituM3, 6);
        Assert.Equal(4000, list.Single(x => x.SinkId == "D-N1").PlanInSituM3, 6);
    }

    [Fact]
    public void 混采_零占比的分项不进账()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", target: 10000,
                splits: "[{\"MaterialCode\":\"coal\",\"Fraction\":1.0},{\"MaterialCode\":\"rock\",\"Fraction\":0}]");
        Assert.Single(SinkInbound.FlowsFromRouting(db.Connection, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("不是 JSON")]
    [InlineData("{\"不是\":\"数组\"}")]
    public void 混采_拆不开就按主物料记一条而不是丢掉(string splits)
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        AddFace(db.Connection, "F-1", material: "rock", target: 10000, splits: splits.Replace("'", "''"));

        var flows = SinkInbound.FlowsFromRouting(db.Connection, out _);
        Assert.Single(flows);
        Assert.Equal("rock", flows[0].MaterialCode);
        Assert.Equal(10000, flows[0].InSituM3, 6);
    }

    [Fact]
    public void 混采_解析器本身不抛()
    {
        Assert.Empty(SinkInbound.ParseSplits(null));
        Assert.Empty(SinkInbound.ParseSplits("乱写"));
        Assert.Single(SinkInbound.ParseSplits("[{\"MaterialCode\":\"coal\",\"Fraction\":1}]"));
    }

    // ── 与台账联动 ──────────────────────────────────────────
    [Fact]
    public void 联动_排后剩余算得出排超()
    {
        using var db = TestDb.Open();
        Exec(db.Connection, "DELETE FROM dump_site");
        Exec(db.Connection, "DELETE FROM load_unload_point");
        Exec(db.Connection, "DELETE FROM sink_profile");
        Clear(db.Connection);

        // 容量 1 万 m³、已填 0 的场，今天要排 10 万实方岩石 ⇒ 必然排超
        Exec(db.Connection, "INSERT INTO dump_site (dump_id, name, dump_type, design_capacity_wan_m3, "
                          + "current_filled_wan_m3, status) VALUES ('D-N1','北排土场','external',1,0,'active')");
        AddFace(db.Connection, "F-1", material: "rock", target: 100000);

        var reg = SinkRegistryLoader.Load(db.Connection);
        var acc = SinkInbound.Today(db.Connection, out _).Single();
        var node = reg.Find("D-N1")!;
        Assert.True(node.RemainingM3 - acc.PendingDumpM3 < 0);
    }

    [Fact]
    public void 联动_通过型去向不占库容故排后剩余不限()
    {
        using var db = TestDb.Open();
        Exec(db.Connection, "DELETE FROM dump_site");
        Exec(db.Connection, "DELETE FROM load_unload_point");
        Exec(db.Connection, "DELETE FROM sink_profile");
        Clear(db.Connection);

        Exec(db.Connection, "INSERT INTO load_unload_point (name, kind, unload_sub) VALUES ('1号原煤仓','unloading','dump')");
        var reg = SinkRegistryLoader.Load(db.Connection);
        var node = reg.All.Single();
        Assert.False(node.IsCapacityLimited);
        Assert.Equal(double.PositiveInfinity, node.RemainingM3);
    }
}
