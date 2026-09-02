using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

/// <summary>
/// 滚动重排 TaskRescheduler 已知值回归 —— 忠实原 TaskLib.Engine.TaskRescheduler:
/// 扣实绩得剩余 → 按原因码策略改写 Config(补车/换面/降效/降产/备机)→ 重跑 TaskExploder。
/// </summary>
public class TaskReschedulerTests
{
    static ExploderConfig Base()
        => new()
        {
            IdPrefix = "D", Shifts = { new("早", 0, 8), new("中", 8, 16) },
            Faces =
            {
                new FaceInput { Zone = "A", Process = ProcessType.Load, DayTargetM3 = 1000,
                    Group = new EquipmentGroup { MainEquipment = "E1", GroupCapacityM3PerH = 200, RecommendedTrucks = 5,
                        Trucks = new() { "T1", "T2", "T3" } } },
                new FaceInput { Zone = "B", Process = ProcessType.Load, DayTargetM3 = 800,
                    Group = new EquipmentGroup { MainEquipment = "E2", GroupCapacityM3PerH = 200, RecommendedTrucks = 3,
                        Trucks = new() { "T4", "T5", "T6" } } },
            },
        };

    static List<ShiftTask> Done(string zone, double actual)
        => new() { new ShiftTask { WorkZone = zone, Process = ProcessType.Load, ActualVolumeM3 = actual } };

    [Fact]
    public void StrategyFor_maps_reasons()
    {
        Assert.Equal(AdjustStrategy.ReassignBackup, TaskRescheduler.StrategyFor(SchedReason.Fault));
        Assert.Equal(AdjustStrategy.AddTrucks, TaskRescheduler.StrategyFor(SchedReason.TruckShortage));
        Assert.Equal(AdjustStrategy.SwitchFace, TaskRescheduler.StrategyFor(SchedReason.OreShortage));
        Assert.Equal(AdjustStrategy.ReduceCapacity, TaskRescheduler.StrategyFor(SchedReason.Weather));
        Assert.Equal(AdjustStrategy.RollForward, TaskRescheduler.StrategyFor(SchedReason.RoadCongestion));
    }

    [Fact]
    public void Remaining_is_target_minus_actual_and_rolled()
    {
        var r = TaskRescheduler.Reschedule(Base(), Done("A", 400), 4, new Dictionary<string, AdjustStrategy>());
        // A 剩 1000-400=600, B 剩 800-0=800 → 回摊 1400。
        Assert.Equal(1400, r.RolledShortfallM3, 6);
        Assert.Equal(4, r.FromHour, 6);
        Assert.Contains(r.Notes, n => n.Contains("A") && n.Contains("600"));
    }

    [Fact]
    public void AddTrucks_fills_to_recommended()
    {
        var strat = new Dictionary<string, AdjustStrategy> { ["A"] = AdjustStrategy.AddTrucks };
        var r = TaskRescheduler.Reschedule(Base(), Done("A", 0), 0, strat);
        // A 荐 5 车、原 3 → 补 2。重排任务的编组带 5 车。
        var aLoad = r.Plan.Tasks.First(t => t.WorkZone == "A" && t.Process == ProcessType.Load);
        Assert.Equal(5, aLoad.Group.Trucks.Count);
        Assert.Contains(r.Notes, n => n.Contains("补 2 辆"));
    }

    [Fact]
    public void SwitchFace_moves_remaining_to_other_face()
    {
        var strat = new Dictionary<string, AdjustStrategy> { ["A"] = AdjustStrategy.SwitchFace };
        var r = TaskRescheduler.Reschedule(Base(), Done("A", 0), 0, strat);
        // A 缺料 → A 剩 1000 切至 B(B 变 800+1000=1800); A 无采装任务。
        Assert.Contains(r.Notes, n => n.Contains("切至") && n.Contains("B"));
        Assert.DoesNotContain(r.Plan.Tasks, t => t.WorkZone == "A" && t.Process == ProcessType.Load && t.TargetVolumeM3 > 0);
    }

    [Fact]
    public void ReduceCapacity_applies_global_derate()
    {
        var strat = new Dictionary<string, AdjustStrategy> { ["A"] = AdjustStrategy.ReduceCapacity };
        var r = TaskRescheduler.Reschedule(Base(), Done("A", 0), 0, strat);
        Assert.Contains(r.Notes, n => n.Contains("全盘降效"));
        // 降效后编组班产 200×0.8=160 → A 剩 1000 需 1000/160=6.25h(早班 8h 内完成), 计划工时反映降效。
        var aLoad = r.Plan.Tasks.First(t => t.WorkZone == "A" && t.Process == ProcessType.Load);
        Assert.True(aLoad.PlannedHours > 5.5);   // 降效前 1000/200=5h, 降效后 >5.5h
    }
}
