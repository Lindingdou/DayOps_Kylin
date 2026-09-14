using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

/// <summary>
/// 裂解装箱引擎 TaskExploder 已知值回归 —— 忠实移植原 TaskLib.Engine.TaskExploder:
/// 按班次时窗×编组班产装箱 + 配煤约束 + 运力/双占/接续校核。纯算法可算。
/// </summary>
public class TaskExploderTests
{
    static ShiftWindow Zao => new("早", 0, 8);

    static ExploderConfig OneFace(double target, double capPerH, int trucks = 3, int recTrucks = 3)
        => new()
        {
            IdPrefix = "D", Shifts = { Zao },
            Faces = { new FaceInput { Zone = "A", Process = ProcessType.Load, DayTargetM3 = target,
                Group = new EquipmentGroup { MainEquipment = "E1", GroupCapacityM3PerH = capPerH,
                    RecommendedTrucks = recTrucks, Trucks = Enumerable.Range(0, trucks).Select(i => $"T{i}").ToList() } } },
        };

    [Fact]
    public void BinPack_single_face_known_volume_and_hours()
    {
        // 目标 1000 m³, 班产 200 m³/h, 早班 8h → vol=1000, hours=5, 0→5h。
        var r = TaskExploder.Explode(OneFace(1000, 200));
        var load = Assert.Single(r.Tasks.Where(t => t.Process == ProcessType.Load));
        Assert.Equal(1000, load.TargetVolumeM3, 6);
        Assert.Equal(5.0, load.PlannedHours, 6);
        Assert.Equal(0.0, load.StartHour, 6);
        Assert.Equal(5.0, load.EndHour, 6);
        Assert.DoesNotContain(r.Violations, v => v.Code == "当日欠产");
    }

    [Fact]
    public void Capacity_shortage_flags_underproduction()
    {
        // 目标 2000, 班产 200×8h=1600 上限 → 欠 400。
        var r = TaskExploder.Explode(OneFace(2000, 200));
        var load = Assert.Single(r.Tasks.Where(t => t.Process == ProcessType.Load));
        Assert.Equal(1600, load.TargetVolumeM3, 6);
        Assert.Contains(r.Violations, v => v.Code == "当日欠产");
    }

    // ─── 爆破从"一炮"改成"逐炮"（§三五二 补齐原版 2026-08-11 的那个修）───
    //  Kylin 侧此前是 we = min(we, cfg.BlastStart)：只认最早一炮，一天三炮时后两炮
    //  在装箱里根本不存在 —— 界面上排得整整齐齐，计划里那几个时段仍在满负荷作业。

    [Fact]
    public void 爆破_第二炮起也从班里扣掉()
    {
        var cfg = OneFace(9999, 100);                     // 目标够大，装满整班
        cfg.SetBlasts(new[] { new PitMine3D.Kylin.Data.BlastWindow(2, 2.67),
                              new PitMine3D.Kylin.Data.BlastWindow(5, 5.67) });
        var load = Assert.Single(TaskExploder.Explode(cfg).Tasks.Where(t => t.Process == ProcessType.Load));
        // 早班 0–8 被两炮切成 0–2 / 2.67–5 / 5.67–8：最长的一段是 2.67–5（2.33 h）
        Assert.Equal(2.67, load.StartHour, 2);
        Assert.Equal(5.0, load.EndHour, 2);
    }

    [Fact]
    public void 爆破_兼容视图那一对标量仍然管用()
    {
        var cfg = OneFace(9999, 100);
        cfg.BlastStart = 6; cfg.BlastEnd = 6.67;          // 没写列表，只给了老那一对
        var load = Assert.Single(TaskExploder.Explode(cfg).Tasks.Where(t => t.Process == ProcessType.Load));
        Assert.Equal(0.0, load.StartHour, 2);
        Assert.Equal(6.0, load.EndHour, 2);
    }

    [Fact]
    public void 爆破_盖住班首时取得回后面那一段而不是整班压成零()
    {
        // ★ 原式 we = min(we, BlastStart) 会把 we 压到 0 → 整班停产；实际是"清场解除后开工"
        var cfg = OneFace(9999, 100);
        cfg.SetBlasts(new[] { new PitMine3D.Kylin.Data.BlastWindow(0, 1) });
        var load = Assert.Single(TaskExploder.Explode(cfg).Tasks.Where(t => t.Process == ProcessType.Load));
        Assert.Equal(1.0, load.StartHour, 2);
        Assert.Equal(8.0, load.EndHour, 2);
    }

    [Fact]
    public void 爆破_写入时兼容视图同步成最早那一段()
    {
        var cfg = new ExploderConfig();
        cfg.SetBlasts(new[] { new PitMine3D.Kylin.Data.BlastWindow(5, 5.67),
                              new PitMine3D.Kylin.Data.BlastWindow(2, 2.67) });
        Assert.Equal(2, cfg.BlastStart, 6);
        Assert.Equal(2.67, cfg.BlastEnd, 6);
        Assert.Equal(2, cfg.BlastWindows().Count);
    }

    [Fact]
    public void 爆破_切成几段时放弃掉的工时要如实报出来()
    {
        // 只取最长一段是结构约束（一条 面×班 只出一条任务）；放弃的小时数不许闷声吞掉
        var cfg = OneFace(9999, 100);
        cfg.SetBlasts(new[] { new PitMine3D.Kylin.Data.BlastWindow(4, 4.67) });
        var r = TaskExploder.Explode(cfg);
        var v = Assert.Single(r.Violations.Where(x => x.Code == "爆破清场"));
        Assert.Contains("切成 2 段", v.Message);
        Assert.Contains("放弃", v.Message);
    }

    [Fact]
    public void 爆破_整班落在清场内时说本班排不出作业()
    {
        var cfg = OneFace(9999, 100);
        cfg.SetBlasts(new[] { new PitMine3D.Kylin.Data.BlastWindow(0, 8) });
        var r = TaskExploder.Explode(cfg);
        Assert.Contains(r.Violations, v => v.Code == "爆破清场" && v.Message.Contains("整班落在爆破清场内"));
    }

    [Fact]
    public void 爆破_没有停产时窗时一条都不报()
        => Assert.DoesNotContain(TaskExploder.Explode(OneFace(1000, 200)).Violations, v => v.Code == "爆破清场");

    [Fact]
    public void Truck_shortage_flags_when_below_recommended()
    {
        var r = TaskExploder.Explode(OneFace(500, 200, trucks: 2, recTrucks: 4));
        Assert.Contains(r.Violations, v => v.Code == "运力不足");
    }

    [Fact]
    public void Blend_constraint_moves_volume_high_to_low_ash()
    {
        var cfg = new ExploderConfig
        {
            Shifts = { Zao }, Blend = new BlendStandard { MaxAshPct = 12, EffHoursPerDay = 20 },
            Faces =
            {
                new FaceInput { Zone = "A", Process = ProcessType.Load, DayTargetM3 = 500, Quality = new CoalQuality { AshPct = 15 },
                    Group = new EquipmentGroup { MainEquipment = "E1", GroupCapacityM3PerH = 200, RecommendedTrucks = 0 } },
                new FaceInput { Zone = "B", Process = ProcessType.Load, DayTargetM3 = 500, Quality = new CoalQuality { AshPct = 10 },
                    Group = new EquipmentGroup { MainEquipment = "E2", GroupCapacityM3PerH = 200, RecommendedTrucks = 0 } },
            },
        };
        var r = TaskExploder.Explode(cfg);
        // 综合灰分 12.5→需移 ≥100 从 A(15)到 B(10) 使 ≤12: A 500→400, B 500→600, 综合 12.0。
        Assert.Contains(r.Violations, v => v.Code == "配煤调整");
        Assert.Equal(400, cfg.Faces[0].DayTargetM3, 6);
        Assert.Equal(600, cfg.Faces[1].DayTargetM3, 6);
    }

    [Fact]
    public void Blend_already_ok_reports_pass()
    {
        var cfg = new ExploderConfig
        {
            Shifts = { Zao }, Blend = new BlendStandard { MaxAshPct = 13 },
            Faces =
            {
                new FaceInput { Zone = "A", Process = ProcessType.Load, DayTargetM3 = 500, Quality = new CoalQuality { AshPct = 12 },
                    Group = new EquipmentGroup { MainEquipment = "E1", GroupCapacityM3PerH = 200 } },
                new FaceInput { Zone = "B", Process = ProcessType.Load, DayTargetM3 = 500, Quality = new CoalQuality { AshPct = 12 },
                    Group = new EquipmentGroup { MainEquipment = "E2", GroupCapacityM3PerH = 200 } },
            },
        };
        var r = TaskExploder.Explode(cfg);
        Assert.Contains(r.Violations, v => v.Code == "配煤达标");
    }

    [Fact]
    public void Drill_and_maintenance_tasks_generated_and_continuity()
    {
        var cfg = OneFace(500, 200);
        cfg.Drills.Add(new DrillInput { EquipId = "ZJ1", Zone = "A", Start = 0, End = 3 });
        cfg.Maintenance.Add(new MaintenanceWindow { EquipId = "E2", Start = 0, End = 2, Label = "保养" });
        var r = TaskExploder.Explode(cfg);
        Assert.Contains(r.Tasks, t => t.Process == ProcessType.Drill && t.PlannedHours == 3);
        Assert.Contains(r.Tasks, t => t.Process == ProcessType.Idle && t.Reasons.Contains(SchedReason.Maintenance));
        Assert.DoesNotContain(r.Violations, v => v.Code == "工序接续");   // 有采装+有穿孔 → 接续 OK
    }

    [Fact]
    public void Load_without_drill_flags_continuity()
    {
        var r = TaskExploder.Explode(OneFace(500, 200));   // 无穿孔任务
        Assert.Contains(r.Violations, v => v.Code == "工序接续");
    }
}
