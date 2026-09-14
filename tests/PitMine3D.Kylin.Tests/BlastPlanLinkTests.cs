using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using PitMine3D.Kylin.Data;
using Xunit;
// Data 与 Scheduling 两个命名空间各有一个 ShiftWindow（前者是班次时窗 record struct，
// 后者是装箱盘子里的班次）—— 这里要的是 Data 那个，显式别名，不靠"就近优先"猜。
using ShiftWindow = PitMine3D.Kylin.Data.ShiftWindow;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 钻爆计划衔接 B1–B7（§三五二）。原窗读的是没有台账的穿孔计划，「清场警戒」「衔接采装面」「状态」
/// 三列还是写死的常量、「爆破窗口」每行都是同一个全局值；这里读的是 <c>blast_event</c> 真表。
///
/// 三条头号判据（都<b>不会报错</b>）：
///   ① 综合单耗 = Σ装药 ÷ Σ方量，<b>不是</b>各行单耗的平均 —— 两者都是"看着对"的数。
///   ② 台账没炮时那一行是<b>兜底窗口</b>，不是真炮次：数字列必须留空，不许合成一炮。
///   ③ 「进没进装箱时窗」在 Kylin 侧<b>判不了</b>（排产装配层未移植）—— 判不了不能写成"未进"，
///      更不能拿本表自己倒推的窗口去判自己（那会永远判通过）。
/// </summary>
public class BlastPlanLinkTests
{
    private static readonly DateTime Day = new(2026, 9, 11);

    private static BlastEventRow Shot(string time = "12:00", int seq = 1, string loc = "EP-08",
                                      int? holes = 100, double? meters = 1500, double? kg = 900,
                                      double? vol = 3000, double? unit = 0.3, string mat = "rh", string drill = "DR-1")
        => new()
        {
            BlastDate = "2026-09-11", BlastTime = time, BlastSeq = seq, LocationCode = loc, DrillId = drill,
            Material = mat, HoleCount = holes, TotalHoleLengthM = meters, ExplosiveKg = kg,
            BlastVolumeM3 = vol, UnitConsumptionKgM3 = unit,
        };

    private static FaceInput Face(string zone = "北一采", string ep = "EP-08", double bench = 1195,
                                  ProcessType p = ProcessType.Load)
        => new() { Zone = zone, EngineeringPositionId = ep, BenchElevationM = bench, Process = p };

    private static BlastPlanInputs Inp(params BlastEventRow[] shots) => new()
    {
        Date = Day, NowHour = 10, EngineKnown = true,
        Shots = shots.ToList(),
        Faces = new List<FaceInput> { Face() },
        Shifts = new List<ShiftWindow> { new("早班", 0, 8), new("中班", 8, 16), new("夜班", 16, 24) },
    };

    // ── B1/B6：一行 = 一炮；火工品合计 ────────────────────────
    [Fact]
    public void B1_一行一炮不是一个待爆区()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(seq: 1, loc: "EP-08"), Shot(time: "16:30", seq: 2, loc: "EP-08")));
        Assert.Equal(2, res.Shots.Count);
        Assert.True(res.FromLedger);
    }

    [Fact]
    public void B6_火工品五个量逐炮列出并合计()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(holes: 100, meters: 1500, kg: 900, vol: 3000),
                                            Shot(time: "16:30", seq: 2, holes: 50, meters: 700, kg: 400, vol: 1500)));
        Assert.Equal(150, res.HoleCountSum);
        Assert.Equal(2200, res.HoleMetersSum, 6);
        Assert.Equal(1300, res.ExplosiveKgSum, 6);
        Assert.Equal(4500, res.VolumeM3Sum, 6);
    }

    [Fact]
    public void B6_综合单耗是总装药除总方量不是逐行平均()
    {
        // ★ 逐行单耗的平均 = (0.30+0.50)/2 = 0.400；体积加权 = 1300/4500 = 0.289 —— 两个数都"看着对"
        var res = BlastPlanLink.Compose(Inp(Shot(kg: 900, vol: 3000, unit: 0.30),
                                            Shot(time: "16:30", seq: 2, kg: 400, vol: 800, unit: 0.50)));
        Assert.Equal(1300.0 / 3800.0, res.UnitKgM3!.Value, 6);
        Assert.NotEqual(0.40, res.UnitKgM3!.Value, 3);
    }

    [Fact]
    public void B6_没有方量时综合单耗是判不了而不是零()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(vol: null, unit: null)));
        Assert.Null(res.UnitKgM3);
    }

    // ── B2：停产窗口 = 爆破时刻 + 清场 ────────────────────────
    [Fact]
    public void B2_停产窗口逐炮各算各的不再全表一个值()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(time: "12:00"), Shot(time: "20:00", seq: 2)));
        Assert.Equal("12:00–12:40", res.Shots[0].StopWindow);
        Assert.Equal("20:00–20:40", res.Shots[1].StopWindow);
    }

    [Fact]
    public void B2_清场撞到零点时窗口截到24点()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(time: "23:50")));
        Assert.Equal("23:50–24:00", res.Shots[0].StopWindow);
    }

    [Fact]
    public void B2_清场时长与装箱同一个常数()
        => Assert.Equal(0.67, BlastPlanLink.ClearanceH, 6);

    // ── B3：状态由此刻 vs 本炮时刻推 ──────────────────────────
    [Theory]
    [InlineData(10.0, "待爆")]
    [InlineData(12.3, "清场中")]
    [InlineData(20.0, "已爆")]
    public void B3_状态三态(double now, string want)
    {
        var inp = Inp(Shot(time: "12:00"));
        inp.NowHour = now;
        Assert.StartsWith(want, BlastPlanLink.Compose(inp).Shots[0].Status);
    }

    [Fact]
    public void B3_待爆时把还差几小时写出来()
        => Assert.Equal("待爆 2h", BlastPlanLink.Compose(Inp(Shot(time: "12:00"))).Shots[0].Status);

    [Fact]
    public void B3_没记时刻的排在最后并点明装箱当它不存在()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(time: null, seq: 9), Shot(time: "12:00", seq: 1)));
        Assert.Equal("未记时刻", res.Shots[1].Status);
        Assert.Contains("装箱当它不存在", res.Shots[1].Note);
        Assert.Contains("没记爆破时刻", string.Join("|", res.Notes));
    }

    [Fact]
    public void 排序_按时刻排时刻相同按炮次()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(time: "20:00", seq: 3), Shot(time: "08:00", seq: 2),
                                            Shot(time: "08:00", seq: 1)));
        Assert.Equal(new int?[] { 1, 2, 3 }, res.Shots.Select(s => s.Seq));
    }

    [Fact]
    public void 班次_落在哪个班判得出来判不出就留空()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(time: "12:00")));
        Assert.Contains("中班", res.Shots[0].Shift);

        var noShift = Inp(Shot(time: "12:00"));
        noShift.Shifts.Clear();
        Assert.Equal("", BlastPlanLink.Compose(noShift).Shots[0].Shift);
    }

    // ── B4：衔接采装面 ───────────────────────────────────────
    [Fact]
    public void B4_按工程位置号对上时给面名与台阶()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(loc: "EP-08")));
        Assert.True(res.Shots[0].Linked);
        Assert.Equal("北一采（+1195）", res.Shots[0].LinkedFace);
    }

    [Fact]
    public void B4_工程位置号对不上时退到面名互含()
    {
        var inp = Inp(Shot(loc: "北一采"));
        Assert.True(BlastPlanLink.Compose(inp).Shots[0].Linked);
    }

    [Fact]
    public void B4_对不上时写出按什么对的而不是写死一个面()
    {
        var inp = Inp(Shot(loc: "EP-99"));
        var res = BlastPlanLink.Compose(inp);
        Assert.False(res.Shots[0].Linked);
        Assert.Contains("EP-99", res.Shots[0].LinkedFace);
        Assert.Contains("平盘编码 → 工程位置号 → 面名", string.Join("|", res.Notes));
    }

    [Fact]
    public void B4_本炮没记平盘编码时说的是这件事()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(loc: "")));
        Assert.Contains("没记平盘编码", res.Shots[0].LinkedFace);
    }

    [Fact]
    public void B4_盘子里只有排土面时不当成对上()
    {
        var inp = Inp(Shot(loc: "EP-08"));
        inp.Faces = new List<FaceInput> { Face(p: ProcessType.Dump) };
        var res = BlastPlanLink.Compose(inp);
        Assert.False(res.Shots[0].Linked);
        Assert.Contains("没有采装面", res.Shots[0].LinkedFace);
    }

    [Fact]
    public void B4_台阶标高为零时不画那个括号()
    {
        var inp = Inp(Shot(loc: "EP-08"));
        inp.Faces = new List<FaceInput> { Face(bench: 0) };
        Assert.Equal("北一采", BlastPlanLink.Compose(inp).Shots[0].LinkedFace);
    }

    // ── 衔接的第一段：穿孔计划 ────────────────────────────────
    [Fact]
    public void 穿孔_本区有孔时把谁在打几点到几点列出来()
    {
        var inp = Inp(Shot(loc: "EP-08"));
        inp.Drills = new List<DrillInput> { new() { EquipId = "DR-1", Zone = "EP-08", Start = 1, End = 8 } };
        var res = BlastPlanLink.Compose(inp);
        Assert.True(res.Shots[0].HasDrill);
        Assert.Equal("DR-1 01:00–08:00", res.Shots[0].DrillText);
    }

    [Fact]
    public void 穿孔_同区几台钻机分段打时全列出来()
    {
        var inp = Inp(Shot(loc: "EP-08"));
        inp.Drills = new List<DrillInput>
        {
            new() { EquipId = "DR-1", Zone = "EP-08", Start = 0, End = 8 },
            new() { EquipId = "DR-2", Zone = "EP-08", Start = 8, End = 16 },
        };
        Assert.Equal("DR-1 00:00–08:00、DR-2 08:00–16:00", BlastPlanLink.Compose(inp).Shots[0].DrillText);
    }

    [Fact]
    public void 穿孔_一条都没排时告诉人去哪儿排()
    {
        var res = BlastPlanLink.Compose(Inp(Shot()));
        Assert.False(res.Shots[0].HasDrill);
        Assert.Equal("无穿孔计划", res.Shots[0].DrillText);
        Assert.Contains("drill_plan 为空", string.Join("|", res.Notes));
    }

    [Fact]
    public void 穿孔_有孔但不在本区时说的是采准脱节()
    {
        var inp = Inp(Shot(loc: "EP-08"));
        inp.Drills = new List<DrillInput> { new() { EquipId = "DR-1", Zone = "EP-99", Start = 1, End = 8 } };
        var res = BlastPlanLink.Compose(inp);
        Assert.Equal("本区无穿孔计划", res.Shots[0].DrillText);
        Assert.Contains("排早了", string.Join("|", res.Notes));
    }

    // ── B5：进没进装箱时窗（Kylin 侧判不了）─────────────────────
    [Fact]
    public void B5_盘子取不到时是判不了而不是未进()
    {
        // ★ 判不了写成"未进"，会把一堆红字扣在一个根本没排过的计划上
        var inp = Inp(Shot(time: "12:00"), Shot(time: "20:00", seq: 2));
        inp.EngineKnown = false;
        var res = BlastPlanLink.Compose(inp);
        Assert.Equal(0, res.NotInEngineWindow);
        Assert.Equal(2, res.EngineUnjudged);
        Assert.All(res.Shots, s => Assert.False(s.EngineJudged));
        Assert.Contains("判不了", res.Shots[0].Note);
        Assert.Contains("永远判通过", string.Join("|", res.Notes));
    }

    [Fact]
    public void B5_盘子只扣了最早一炮时后面的炮要点名()
    {
        var inp = Inp(Shot(time: "12:00"), Shot(time: "20:00", seq: 2));
        inp.EngineStart = 12; inp.EngineEnd = 12.67;      // 补齐前那一版：一对标量只装得下一炮
        var res = BlastPlanLink.Compose(inp);
        Assert.True(res.Shots[0].DrivesEngine);
        Assert.False(res.Shots[1].DrivesEngine);
        Assert.Equal(1, res.NotInEngineWindow);
        Assert.Contains("没进计划时窗", string.Join("|", res.Notes));
    }

    [Fact]
    public void B5_盘子逐炮都扣了就一条也不报()
    {
        var inp = Inp(Shot(time: "12:00"), Shot(time: "20:00", seq: 2));
        inp.EngineWindows = new List<BlastWindow> { new(12, 12.67), new(20, 20.67) };
        var res = BlastPlanLink.Compose(inp);
        Assert.Equal(0, res.NotInEngineWindow);
        Assert.DoesNotContain("没进计划时窗", string.Join("|", res.Notes));
    }

    [Fact]
    public void B5_没记时刻的炮不算成没进时窗()
    {
        var inp = Inp(Shot(time: null));
        inp.EngineWindows = new List<BlastWindow> { new(12, 12.67) };
        Assert.Equal(0, BlastPlanLink.Compose(inp).NotInEngineWindow);
    }

    [Fact]
    public void B5_时窗列表优先于那一对标量()
    {
        var inp = Inp(Shot(time: "20:00"));
        inp.EngineStart = 12; inp.EngineEnd = 12.67;
        inp.EngineWindows = new List<BlastWindow> { new(20, 20.67) };
        Assert.True(BlastPlanLink.Compose(inp).Shots[0].DrivesEngine);
    }

    // ── B7：台账没炮 / 读不通 ────────────────────────────────
    [Fact]
    public void B7_台账无炮时那一行是兜底窗口数字列全空()
    {
        // ★ 兜底行合成成一炮，火工品合计就会凭空多出一炮的量
        var inp = Inp();
        inp.EngineStart = 12; inp.EngineEnd = 12.67;
        var res = BlastPlanLink.Compose(inp);
        Assert.False(res.FromLedger);
        Assert.Single(res.Shots);
        Assert.Null(res.Shots[0].HoleCount);
        Assert.Null(res.Shots[0].VolumeM3);
        Assert.Equal(0, res.HoleCountSum);
        Assert.Contains("兜底窗口", res.Shots[0].Note);
        Assert.Contains("不是真炮次", string.Join("|", res.Notes));
    }

    [Fact]
    public void B7_台账无炮又无停产窗口时说清楚本日没扣()
    {
        var res = BlastPlanLink.Compose(Inp());
        Assert.Equal("本日无停产窗口", res.Shots[0].StopWindow);
        Assert.Contains("未扣任何停产时段", res.Shots[0].Note);
    }

    [Fact]
    public void B7_台账读不通与本日无记录是两句不同的话()
    {
        var broken = Inp();
        broken.LedgerUsable = false;
        Assert.Contains("读不通", string.Join("|", BlastPlanLink.Compose(broken).Notes));
        Assert.Contains("无记录", string.Join("|", BlastPlanLink.Compose(Inp()).Notes));
    }

    // ── 抬头 ────────────────────────────────────────────────
    [Fact]
    public void 抬头_有炮时给炮数与火工品合计()
    {
        var inp = Inp(Shot());
        inp.EngineWindows = new List<BlastWindow> { new(12, 12.67) };
        string h = BlastPlanLink.Compose(inp).Header;
        Assert.Contains("本日 1 炮", h);
        Assert.Contains("孔 100 个", h);
        Assert.Contains("综合单耗", h);
        Assert.Contains("装箱停产 1 段", h);
    }

    [Fact]
    public void 抬头_盘子取不到时停产时段写判不了()
    {
        var inp = Inp(Shot());
        inp.EngineKnown = false;
        inp.EngineWindows = new List<BlastWindow> { new(12, 12.67) };
        Assert.Contains("判不了", BlastPlanLink.Compose(inp).Header);
    }

    // ── 物料 ────────────────────────────────────────────────
    [Fact]
    public void 物料_编码翻成中文名并把原文带上()
    {
        var res = BlastPlanLink.Compose(Inp(Shot(mat: "rh")));
        Assert.Contains("（rh）", res.Shots[0].MaterialText);
    }

    [Fact]
    public void 物料_空的就留空不编一个物料出来()
        => Assert.Equal("", BlastPlanLink.Compose(Inp(Shot(mat: ""))).Shots[0].MaterialText);

    // ── 由台账推停产时窗（WorkWindowCalc 此前缺的数据源）──────
    [Fact]
    public void 推时窗_逐炮各一段没记时刻的不产生时窗()
    {
        var ws = BlastPlanLink.BlastWindowsOf(new[] { Shot(time: "12:00"), Shot(time: null, seq: 2) }, out int noTime);
        Assert.Single(ws);
        Assert.Equal(1, noTime);
        Assert.Equal(12, ws[0].Start, 6);
        Assert.Equal(12.67, ws[0].End, 6);
    }

    [Fact]
    public void 推时窗_挨着的两炮并成一段()
    {
        var ws = BlastPlanLink.BlastWindowsOf(new[] { Shot(time: "12:00"), Shot(time: "12:30", seq: 2) }, out _);
        Assert.Single(ws);
        Assert.Equal(12, ws[0].Start, 6);
        Assert.Equal(13.17, ws[0].End, 2);
    }

    [Fact]
    public void 推时窗_隔开的两炮是两段()
        => Assert.Equal(2, BlastPlanLink.BlastWindowsOf(
                new[] { Shot(time: "12:00"), Shot(time: "20:00", seq: 2) }, out _).Count);

    [Fact]
    public void 推时窗_空表与null都不抛()
    {
        Assert.Empty(BlastPlanLink.BlastWindowsOf(null, out int a));
        Assert.Equal(0, a);
        Assert.Empty(BlastPlanLink.BlastWindowsOf(new BlastEventRow[] { null! }, out _));
    }

    // ── 取数（真表）────────────────────────────────────────
    [Fact]
    public void 取数_爆破台账按日读得出来且不串日子()
    {
        using var db = TestDb.Open();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM blast_event";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO blast_event (blast_date, blast_time, blast_seq, location_code, hole_count, blast_volume_m3) "
              // ★ 真库里 blast_event.location_code 是**平盘标高码**（'1195'），不是 EP-xx ——
              //   它对 mine_location 有外键，随手写个 EP-08 存不进去。这一点正是 B4 那条
              //   「平盘编码与作业面台账用了两套命名」提示在真库上一定会响的原因。
              + "VALUES ('2026-09-11','12:00',1,'1195',100,3000), ('2026-09-12','12:00',2,'1210',50,1500)";
            cmd.ExecuteNonQuery();
        }
        var rows = BlastPlanLink.ShotsByDate(db.Connection, Day, out string err);
        Assert.Equal("", err);
        Assert.Single(rows);
        Assert.Equal("1195", rows[0].LocationCode);
        Assert.Equal(100, rows[0].HoleCount);
    }

    [Fact]
    public void 取数_没有连接时给原因并降级不抛()
    {
        Assert.Empty(BlastPlanLink.ShotsByDate(null, Day, out string e1));
        Assert.Contains("数据库连接", e1);
        Assert.Empty(BlastPlanLink.FacesOfDay(null, out string e2));
        Assert.Contains("数据库连接", e2);
    }

    [Fact]
    public void 取数_整条路没有库时也能出一张兜底表()
    {
        var res = BlastPlanLink.Build(null, Day, 10);
        Assert.False(res.FromLedger);
        Assert.Single(res.Shots);
        Assert.Contains("读不通", string.Join("|", res.Notes));
    }

    [Fact]
    public void 取数_Build一律不判进没进装箱时窗()
    {
        // ★ 拿本表自己倒推出来的窗口去判"进没进计划"会永远判通过 —— 那是条会骗人的死路
        using var db = TestDb.Open();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM blast_event; "
                            + "INSERT INTO blast_event (blast_date, blast_time, blast_seq, location_code) "
                            + "VALUES ('2026-09-11','12:00',1,'1195')";
            cmd.ExecuteNonQuery();
        }
        var res = BlastPlanLink.Build(db.Connection, Day, 10);
        Assert.True(res.FromLedger);
        Assert.Equal(1, res.EngineUnjudged);
        Assert.Equal(0, res.NotInEngineWindow);
    }

    [Fact]
    public void 取数_作业面读的是当日路由表()
    {
        using var db = TestDb.Open();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM working_face_routing; "
                            + "INSERT INTO working_face_routing (face_code, process, engineering_position_id, bench_elevation_m) "
                            + "VALUES ('北一采','Load','EP-08',1195)";
            cmd.ExecuteNonQuery();
        }
        var faces = BlastPlanLink.FacesOfDay(db.Connection, out string err);
        Assert.Equal("", err);
        var f = faces.Single();
        Assert.Equal("北一采", f.Zone);
        Assert.Equal(ProcessType.Load, f.Process);
        Assert.Equal(1195, f.BenchElevationM, 6);
    }
}
