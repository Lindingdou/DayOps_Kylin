using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 当日盘子装配层（§三五四）。此前「生产任务编制」跑的是<b>写死在代码里的示例</b>
/// （4煤南 / WK-35A / T1..T5），而各张台账其实已经一张张接通了。本类把它们装成一份盘子。
///
/// 头号判据是原版 2026-08-18 那条决定：**样例不兜底**。
/// 编一份看着正常的假盘子 —— 有面、有量、有编组，每个数都自洽 —— 比空着更危险：
/// 人分不出自己看的是这个矿还是示例矿。唯一的例外是班制（工程缺省参数，不携带这个矿特有的信息），
/// 但它必须记 <c>Partial</c> 而不是 <c>Real</c>，且要说明"这不是日历里排的班"。
/// </summary>
public class ProductionPlanContextTests
{
    private static readonly DateTime Day = new(2026, 9, 11);

    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>清掉本轮要摆弄的几张表（种子库里本来就有东西）。</summary>
    private static void ClearAll(DbConnection c)
    {
        Exec(c, "DELETE FROM working_face_routing");
        Exec(c, "DELETE FROM blast_event");
        Exec(c, "DELETE FROM drill_plan");
        Exec(c, "DELETE FROM maintenance_window");
        Exec(c, "DELETE FROM shift_calendar");
        CompileOverrides.SetForTest(new CompileAnchors());
    }

    private static void AddFace(DbConnection c, string code, string equip = "", double target = 4000,
                                string process = "Load", string ep = "EP-01")
        => Exec(c, "INSERT INTO working_face_routing (face_code, process, engineering_position_id, "
                 + "bench_elevation_m, material_code, day_target_m3, main_equipment) VALUES ("
                 + $"'{code}', '{process}', '{ep}', 1195, 'rock', {target.ToString(System.Globalization.CultureInfo.InvariantCulture)}, '{equip}')");

    private static PlanAssembly Asm(DbConnection c) => ProductionPlanContext.Assemble(c, Day, 10);

    // ── 样例不兜底 ──────────────────────────────────────────
    [Fact]
    public void 作业面_一个都没有时如实说排不出来且不编面()
    {
        // ★ 编一份看着正常的假盘子，人分不出自己看的是这个矿还是示例矿
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var a = Asm(db.Connection);
        Assert.True(a.NoFaces);
        Assert.False(a.Usable);
        Assert.False(a.FacesFromLedger);
        Assert.Equal(ChainState.Missing, a.Seg("作业面")!.State);
        string notes = string.Join("|", a.Notes);
        Assert.Contains("不编样例面", notes);
        Assert.Contains("补法", notes);
    }

    [Fact]
    public void 作业面_台账有面时进盘子并标成台账()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采");
        var a = Asm(db.Connection);
        Assert.True(a.Usable);
        Assert.True(a.FacesFromLedger);
        Assert.Equal(ChainState.Real, a.Seg("作业面")!.State);
        Assert.Equal("北一采", a.Config.Faces.Single().Zone);
        Assert.Equal(4000, a.Config.Faces.Single().DayTargetM3, 6);
    }

    [Fact]
    public void 作业面_配了没量的面照进盘子且点名说不是漏排()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采", target: 0);
        var a = Asm(db.Connection);
        Assert.Single(a.Config.Faces);
        Assert.Contains("不是漏排", string.Join("|", a.Notes));
    }

    // ── 班次：唯一允许的兜底 ────────────────────────────────
    [Fact]
    public void 班次_日历没排时按工程缺省三班但记成缺省不是台账()
    {
        // ★ 记 Real 就等于说"日历里是这么排的" —— 那是句假话
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        var a = Asm(db.Connection);
        var seg = a.Seg("班次")!;
        Assert.Equal(ChainState.Partial, seg.State);
        Assert.Equal("工程缺省", seg.StateZh);
        Assert.Equal(3, a.Config.Shifts.Count);
        Assert.Contains("这不是日历里排的班", string.Join("|", a.Notes));
        Assert.Contains("补法", string.Join("|", a.Notes));
    }

    [Fact]
    public void 班次_日历排了就按日历且记成台账()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        Exec(db.Connection, "INSERT INTO shift_calendar (date, shift, start_time) VALUES "
                          + "('2026-09-11','A','06:00'), ('2026-09-11','B','14:00'), ('2026-09-11','C','22:00')");
        var a = Asm(db.Connection);
        Assert.Equal(ChainState.Real, a.Seg("班次")!.State);
        Assert.Equal(3, a.Config.Shifts.Count);
        Assert.Equal(6, a.Config.Shifts[0].Start, 6);      // 按日历的 06:00，不是缺省的 00:00
        Assert.Equal(24, a.Config.Shifts[^1].End, 6);
    }

    [Fact]
    public void 班次_缺省三班是工程常数不带矿特有信息()
    {
        var s = ProductionPlanContext.DefaultThreeShifts();
        Assert.Equal(new[] { 0.0, 8.0, 16.0 }, s.Select(x => x.Start));
        Assert.Equal(new[] { 8.0, 16.0, 24.0 }, s.Select(x => x.End));
    }

    // ── 爆破时窗：逐炮，不是只取最早一炮 ─────────────────────
    [Fact]
    public void 爆破_逐炮切段进盘子()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采");
        Exec(db.Connection, "INSERT INTO blast_event (blast_date, blast_time, blast_seq, location_code) VALUES "
                          + "('2026-09-11','10:00',1,'1195'), ('2026-09-11','20:00',2,'1210')");
        var a = Asm(db.Connection);
        Assert.Equal(ChainState.Real, a.Seg("爆破时窗")!.State);
        Assert.Equal(2, a.Config.BlastWindows().Count);   // ★ 两炮两段，不是只认最早那一炮
        Assert.Equal(10, a.Config.BlastStart, 6);         // 兼容视图仍是最早那一段
    }

    [Fact]
    public void 爆破_本日无炮时不扣停产且如实标没有()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采");
        var a = Asm(db.Connection);
        Assert.Equal(ChainState.Missing, a.Seg("爆破时窗")!.State);
        Assert.Empty(a.Config.BlastWindows());
    }

    [Fact]
    public void 爆破_没记时刻的炮要点名()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采");
        Exec(db.Connection, "INSERT INTO blast_event (blast_date, blast_time, blast_seq, location_code) VALUES "
                          + "('2026-09-11','10:00',1,'1195'), ('2026-09-11',NULL,2,'1210')");
        Assert.Contains("没记爆破时刻", string.Join("|", Asm(db.Connection).Notes));
    }

    // ── 穿孔与检修 ──────────────────────────────────────────
    [Fact]
    public void 穿孔_台账有就进盘子()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采");
        Exec(db.Connection, "INSERT INTO drill_plan (equipment_id, plan_date, start_time, end_time, zone) "
                          + "VALUES ('DR-1','2026-09-11','01:00','08:00','1195')");
        var a = Asm(db.Connection);
        Assert.Equal(ChainState.Real, a.Seg("穿孔计划")!.State);
        Assert.Equal(1, a.Config.Drills.Single().Start, 6);
    }

    [Fact]
    public void 检修_台账有就进盘子且真的扣时窗()
    {
        // ★ 检修此前恒为 0：一台上午定修的电铲，计划里从 0 点起就在满负荷干活
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        Exec(db.Connection, "INSERT INTO maintenance_window (equipment_id, plan_date, start_time, end_time, kind) "
                          + "VALUES ('E1','2026-09-11','00:00','04:00','定修')");
        AddFace(db.Connection, "北一采", equip: "E1");
        var a = Asm(db.Connection);
        Assert.Equal(ChainState.Real, a.Seg("检修档期")!.State);
        var mw = a.Config.Maintenance.Single();
        Assert.Equal("E1", mw.EquipId);
        Assert.Equal(4, mw.End, 6);
    }

    [Fact]
    public void 检修_时刻非法的条数要点名说计划里没有()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采");
        Exec(db.Connection, "INSERT INTO maintenance_window (equipment_id, plan_date, start_time, end_time, kind) "
                          + "VALUES ('E1','2026-09-11','不知道','04:00','定修')");
        Assert.Contains("起止时刻非法已丢弃", string.Join("|", Asm(db.Connection).Notes));
    }

    // ── 编组班产：与「编组优化」同一份物理口径 ────────────────
    [Fact]
    public void 编组_对上规则的面算得出班产()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        Exec(db.Connection, "INSERT OR IGNORE INTO equipment (equipment_id, category, model) VALUES ('E1','电铲','2800XP')");
        AddFace(db.Connection, "北一采", equip: "E1");
        var a = Asm(db.Connection);
        var g = a.Config.Faces.Single().Group;
        Assert.True(g.GroupCapacityM3PerH > 0, "对上编组规则的面应算得出班产");
        Assert.True(g.RecommendedTrucks > 0);
        Assert.True(g.HasCycleBreakdown, "周期分解要带下来，否则分环节降效只能退回全盘值");
        Assert.Equal(ChainState.Real, a.Seg("编组班产")!.State);
    }

    [Fact]
    public void 编组_对不上规则时班产留空并点名不拿典型值顶()
    {
        // ★ 顶一个"典型班产"上去，排出来的量看着正常，但那不是这个面的能力
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采", equip: "查无此机");
        var a = Asm(db.Connection);
        Assert.Equal(0, a.Config.Faces.Single().Group.GroupCapacityM3PerH, 9);
        Assert.Equal(ChainState.Missing, a.Seg("编组班产")!.State);
        Assert.Contains("对不上编组规则", string.Join("|", a.Notes));
    }

    [Fact]
    public void 编组_占位车数与荐车数相等不误报运力不足()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        Exec(db.Connection, "INSERT OR IGNORE INTO equipment (equipment_id, category, model) VALUES ('E1','电铲','2800XP')");
        AddFace(db.Connection, "北一采", equip: "E1");
        var g = Asm(db.Connection).Config.Faces.Single().Group;
        Assert.Equal(g.RecommendedTrucks, g.Trucks.Count);
    }

    [Fact]
    public void 编组_同铲型多条规则时取评分最高的那条()
    {
        // ★ 随手取第一条会让班产随表的插入顺序变，而且不报错
        var rules = new List<FleetDispatchRule>
        {
            new() { ShovelModel = "S1", TruckModel = "T-low", CycleTimeMin = 6, RecommendedTruckCount = 3, TruckPayloadT = 100, BucketLoadsPerTruck = 4, EfficiencyScore = 40 },
            new() { ShovelModel = "S1", TruckModel = "T-high", CycleTimeMin = 6, RecommendedTruckCount = 5, TruckPayloadT = 220, BucketLoadsPerTruck = 4, EfficiencyScore = 90 },
        };
        var f = new FaceInput { Zone = "A", Process = ProcessType.Load, Group = new EquipmentGroup { MainEquipment = "S1" } };
        ProductionPlanContext.AttachGroup(f, rules, new Dictionary<string, string>());
        Assert.Equal(5, f.Group.RecommendedTrucks);
    }

    // ── 锚点排在最前 ────────────────────────────────────────
    [Fact]
    public void 锚点_配煤与降效在装箱前就盖上了()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        CompileOverrides.SetForTest(new CompileAnchors { MaxAshPct = 11, WeatherDeratePct = 20, HandoverRampH = 0 });
        AddFace(db.Connection, "北一采");
        var a = Asm(db.Connection);
        Assert.Equal(11, a.Config.Blend!.MaxAshPct, 6);
        Assert.Equal(20, a.Config.WeatherDeratePct, 6);
        Assert.Equal(0, a.Config.HandoverRampH, 6);
        Assert.Equal(ChainState.Real, a.Seg("编制锚点")!.State);
        CompileOverrides.SetForTest(new CompileAnchors());
    }

    [Fact]
    public void 锚点_没设过时不造配煤标准()
    {
        // 造一份 BlendStandard 就等于把配煤约束整个打开了
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采");
        var a = Asm(db.Connection);
        Assert.Null(a.Config.Blend);
        Assert.Equal(ChainState.Missing, a.Seg("编制锚点")!.State);
    }

    [Fact]
    public void 煤质_一律留空不拿默认煤质冒充()
    {
        // ★ 顶一份默认煤质上去，「配煤达标」就变成一句假话
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        AddFace(db.Connection, "北一采");
        Assert.Null(Asm(db.Connection).Config.Faces.Single().Quality);
    }

    // ── 装配本身 ────────────────────────────────────────────
    [Fact]
    public void 装配_每次都重新装不缓存()
    {
        // ★ 缓存会让"改完看不见"重新长出来
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        Assert.True(Asm(db.Connection).NoFaces);
        AddFace(db.Connection, "北一采");
        Assert.False(Asm(db.Connection).NoFaces);
    }

    [Fact]
    public void 装配_没有库时不抛且每段都有交代()
    {
        var a = ProductionPlanContext.Assemble(null, Day, 10);
        Assert.False(a.Usable);
        Assert.All(a.Chain, c => Assert.False(string.IsNullOrWhiteSpace(c.Label)));
        Assert.Contains("班次", a.SourceLabel);
    }

    [Fact]
    public void 装配_未移植的段照实登记不假装装过()
    {
        var seg = ProductionPlanContext.Assemble(null, Day, 10).Seg("月计划分解")!;
        Assert.Equal(ChainState.NotPorted, seg.State);
        Assert.Equal("未移植", seg.StateZh);
    }

    [Fact]
    public void 装配_日期与此刻按传进来的走()
    {
        var a = ProductionPlanContext.Assemble(null, Day, 13.5);
        Assert.Equal("2026-09-11", a.Config.DateLabel);
        Assert.Equal(13.5, a.Config.NowHour, 6);
        Assert.Equal(Day, a.Date);
    }

    // ── 装出来的盘子真能排 ───────────────────────────────────
    [Fact]
    public void 端到端_装配出的盘子装箱能排出任务()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        Exec(db.Connection, "INSERT OR IGNORE INTO equipment (equipment_id, category, model) VALUES ('E1','电铲','2800XP')");
        AddFace(db.Connection, "北一采", equip: "E1", target: 3000);
        var a = Asm(db.Connection);
        var r = TaskExploder.Explode(a.Config);
        Assert.Contains(r.Tasks, t => t.Process == ProcessType.Load && t.TargetVolumeM3 > 0);
    }

    [Fact]
    public void 端到端_检修真的把班首推后()
    {
        using var db = TestDb.Open();
        ClearAll(db.Connection);
        Exec(db.Connection, "INSERT OR IGNORE INTO equipment (equipment_id, category, model) VALUES ('E1','电铲','2800XP')");
        Exec(db.Connection, "INSERT INTO maintenance_window (equipment_id, plan_date, start_time, end_time, kind) "
                          + "VALUES ('E1','2026-09-11','00:00','04:00','定修')");
        AddFace(db.Connection, "北一采", equip: "E1", target: 3000);
        var r = TaskExploder.Explode(Asm(db.Connection).Config);
        var first = r.Tasks.Where(t => t.Process == ProcessType.Load).OrderBy(t => t.StartHour).First();
        Assert.True(first.StartHour >= 4 - 1e-6, $"定修到 04:00，早班不该从 {first.StartHour} 起排");
    }
}
