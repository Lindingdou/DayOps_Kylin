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
