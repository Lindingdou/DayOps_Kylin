using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;   // ProcessType, CoalQuality（复用；调度任务用 ShiftTask 避与量核算 ProductionTask 重名）

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

// ─────────────────────────── 调度域模型（忠实移植原 TaskLib.Domain / Engine.ExploderConfig）───────────────────────────

/// <summary>任务状态。</summary>
public enum TaskStatus { Planned, Dispatched, Running, Done, Partial, Failed }

/// <summary>欠产/空闲原因码。</summary>
public enum SchedReason { Fault, Maintenance, Weather, BlastWait, ProcessWait, TruckShortage, OreShortage, RoadCongestion, Absence, OverPlanned, OverAchieved }

/// <summary>校核违规等级。</summary>
public enum ViolationSeverity { Info, Warn, Error }

/// <summary>设备编组：主设备 + 配属卡车/辅助 + 编组班产。</summary>
public sealed class EquipmentGroup
{
    public string MainEquipment { get; set; } = "";
    public List<string> Trucks { get; set; } = new();
    public List<string> Aux { get; set; } = new();
    public int RecommendedTrucks { get; set; }
    public double GroupCapacityM3PerH { get; set; }   // 编组班产 = min(铲装能力, 车队运力)
}

public sealed class ShiftWindow
{
    public string Name { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public ShiftWindow() { }
    public ShiftWindow(string name, double start, double end) { Name = name; Start = start; End = end; }
}

public sealed class FaceInput
{
    public string Zone { get; set; } = "";
    public double BenchElevationM { get; set; }
    public string EngineeringPositionId { get; set; } = "";
    public string Material { get; set; } = "";
    public double DayTargetM3 { get; set; }
    public CoalQuality? Quality { get; set; }
    public EquipmentGroup Group { get; set; } = new();
    public ProcessType Process { get; set; } = ProcessType.Load;
}

public sealed class DrillInput
{
    public string EquipId { get; set; } = "";
    public string Zone { get; set; } = "";
    public double BenchElevationM { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
}

public sealed class MaintenanceWindow
{
    public string EquipId { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Label { get; set; } = "检修";
}

public sealed class BlendStandard
{
    public double MaxAshPct { get; set; } = 12.8;
    public double MinCalorificMJkg { get; set; } = 21.5;
    public double EffHoursPerDay { get; set; } = 20;   // 面日产能 = 班产×此（配煤移量的产能上限）
}

public sealed class ExploderConfig
{
    public string DateLabel { get; set; } = "";
    public string IdPrefix { get; set; } = "D";
    public double NowHour { get; set; }
    public double FromHour { get; set; }
    public double BlastStart { get; set; }
    public double BlastEnd { get; set; }
    public double HandoverRampH { get; set; } = 0.5;
    public List<ShiftWindow> Shifts { get; set; } = new();
    public List<FaceInput> Faces { get; set; } = new();
    public List<DrillInput> Drills { get; set; } = new();
    public List<MaintenanceWindow> Maintenance { get; set; } = new();
    public BlendStandard? Blend { get; set; }
}

/// <summary>调度任务（原 ProductionTask；此处 ShiftTask 避与 <see cref="Tasks.ProductionTask"/> 量核算模型重名）。</summary>
public sealed class ShiftTask
{
    public string Id { get; set; } = "";
    public ProcessType Process { get; set; }
    public EquipmentGroup Group { get; set; } = new();
    public string WorkZone { get; set; } = "";
    public double BenchElevationM { get; set; }
    public string EngineeringPositionId { get; set; } = "";
    public string Material { get; set; } = "";
    public double TargetVolumeM3 { get; set; }
    public CoalQuality? QualityTarget { get; set; }
    public string Shift { get; set; } = "";
    public double StartHour { get; set; }
    public double EndHour { get; set; }
    public double PlannedHours { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Planned;
    public List<SchedReason> Reasons { get; set; } = new();
    // 实绩回灌(达成度评价/滚动重排用)
    public double ActualVolumeM3 { get; set; }
    public double ActualHours { get; set; }
    public int TrucksOnSite { get; set; }
    public CoalQuality? QualityActual { get; set; }
    public double AttainmentPct => TargetVolumeM3 > 1e-6 ? Math.Round(ActualVolumeM3 / TargetVolumeM3 * 100, 0) : 0;
}

public sealed class PlanViolation
{
    public ViolationSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ExploderResult
{
    public List<ShiftTask> Tasks { get; set; } = new();
    public List<PlanViolation> Violations { get; set; } = new();
}

// ─────────────────────────── 裂解装箱引擎（忠实移植原 TaskLib.Engine.TaskExploder，纯托管）───────────────────────────

/// <summary>
/// 把各作业面当日目标量按班次有效时窗 × 编组班产 装箱成班次任务，备采用尽则空闲、当日能力不足则报欠产(守恒回摊),
/// 并校核运力/设备双占/工序接续 + 综合配煤约束。产物 = ShiftTask[] + PlanViolation[]。纯逻辑、可单测。
/// </summary>
public static class TaskExploder
{
    public static ExploderResult Explode(ExploderConfig cfg)
    {
        var res = new ExploderResult();
        ApplyBlendConstraint(cfg, res);
        foreach (var face in cfg.Faces) ExplodeFace(cfg, face, res);

        foreach (var d in cfg.Drills)
            res.Tasks.Add(new ShiftTask
            {
                Id = $"{cfg.IdPrefix}-{d.EquipId}-穿", Process = ProcessType.Drill,
                Group = new EquipmentGroup { MainEquipment = d.EquipId },
                WorkZone = d.Zone, BenchElevationM = d.BenchElevationM, Material = d.Zone,
                Shift = ShiftOf(cfg, d.Start), StartHour = d.Start, EndHour = d.End, PlannedHours = Math.Round(d.End - d.Start, 1),
                Status = TaskStatus.Planned,
            });

        foreach (var mw in cfg.Maintenance)
            res.Tasks.Add(new ShiftTask
            {
                Id = $"{cfg.IdPrefix}-{mw.EquipId}-检", Process = ProcessType.Idle,
                Group = new EquipmentGroup { MainEquipment = mw.EquipId },
                Material = mw.Label, Shift = ShiftOf(cfg, mw.Start), StartHour = mw.Start, EndHour = mw.End,
                Status = TaskStatus.Planned, Reasons = new() { SchedReason.Maintenance },
            });

        CheckConstraints(cfg, res);
        return res;
    }

    private static void ExplodeFace(ExploderConfig cfg, FaceInput face, ExploderResult res)
    {
        double cap = Math.Max(1e-6, face.Group.GroupCapacityM3PerH);
        if (face.Process == ProcessType.Load && face.Group.Trucks.Count < face.Group.RecommendedTrucks)
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = "运力不足",
                Message = $"{face.Zone} {face.Group.MainEquipment} 配 {face.Group.Trucks.Count} 车 < 荐 {face.Group.RecommendedTrucks}，铲将待车",
            });

        double remaining = face.DayTargetM3;
        foreach (var sh in cfg.Shifts)
        {
            var (ws, we) = WorkWindow(cfg, face.Group.MainEquipment, sh);
            double avail = Math.Max(0, we - ws);
            if (remaining <= 1)
            {
                if (avail >= 0.5) res.Tasks.Add(IdleTask(cfg, face, ws, we, sh, "空闲", SchedReason.OreShortage));
                continue;
            }
            if (avail < 0.5) continue;

            double maxVol = cap * avail;
            double vol = Math.Min(maxVol, remaining);
            double hours = vol / cap;
            double end = ws + hours;
            res.Tasks.Add(new ShiftTask
            {
                Id = $"{cfg.IdPrefix}-{face.Group.MainEquipment}-{ShiftShort(sh.Name)}",
                Process = face.Process, Group = Clone(face.Group),
                WorkZone = face.Zone, BenchElevationM = face.BenchElevationM, EngineeringPositionId = face.EngineeringPositionId,
                Material = face.Material, TargetVolumeM3 = Math.Round(vol), QualityTarget = face.Quality,
                Shift = sh.Name, StartHour = Math.Round(ws, 2), EndHour = Math.Round(end, 2), PlannedHours = Math.Round(hours, 1),
                Status = TaskStatus.Planned,
            });
            remaining -= vol;
        }

        if (remaining > 1)
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = "当日欠产",
                Message = $"{face.Zone} 当日能力不足，欠 {remaining:0} m³ 需回摊次日",
            });
    }

    private static void ApplyBlendConstraint(ExploderConfig cfg, ExploderResult res)
    {
        if (cfg.Blend is not { } std) return;
        var loads = cfg.Faces.Where(f => f.Process == ProcessType.Load && f.Quality != null && f.DayTargetM3 > 0).ToList();
        if (loads.Count < 2) return;

        double Blend() { double q = loads.Sum(f => f.DayTargetM3); return q > 1e-6 ? loads.Sum(f => f.DayTargetM3 * f.Quality!.AshPct) / q : 0; }
        double Cap(FaceInput f) => f.Group.GroupCapacityM3PerH * std.EffHoursPerDay;

        double before = Blend();
        if (before <= std.MaxAshPct + 0.01)
        {
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Info, Code = "配煤达标", Message = $"综合灰分 {before:0.0}% ≤ 上限 {std.MaxAshPct:0.0}%" });
            return;
        }

        var orig = loads.ToDictionary(f => f, f => f.DayTargetM3);
        for (int guard = 0; guard < 200 && Blend() > std.MaxAshPct; guard++)
        {
            var hi = loads.OrderByDescending(f => f.Quality!.AshPct).First();
            var lo = loads.Where(f => f != hi).OrderBy(f => f.Quality!.AshPct).First();
            double step = Math.Min(Math.Min(Cap(lo) - lo.DayTargetM3, hi.DayTargetM3), 50);
            if (step <= 1) break;
            hi.DayTargetM3 -= step;
            lo.DayTargetM3 += step;
        }
        foreach (var f in loads) f.DayTargetM3 = Math.Round(f.DayTargetM3);
        double after = Blend();

        string moves = string.Join("、", loads.Where(f => Math.Abs(f.DayTargetM3 - orig[f]) >= 1).Select(f => $"{f.Zone} {orig[f]:0}→{f.DayTargetM3:0}"));
        if (after <= std.MaxAshPct + 0.05)
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Info, Code = "配煤调整", Message = $"综合灰分 {before:0.00}→{after:0.00}% 达标（上限 {std.MaxAshPct:0.0}）：{moves}" });
        else
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Warn, Code = "配煤不达标", Message = $"综合灰分 {after:0.00}% 仍 > 上限 {std.MaxAshPct:0.0}%（低灰面产能见顶）" });
    }

    /// <summary>某设备某班的有效作业时窗：扣检修（班首）+ 爆破清场 + 交接班损失 + 滚动重排起点。</summary>
    private static (double ws, double we) WorkWindow(ExploderConfig cfg, string equipId, ShiftWindow sh)
    {
        double ws = sh.Start, we = sh.End;
        foreach (var mw in cfg.Maintenance)
            if (mw.EquipId == equipId && mw.Start < sh.End && mw.End > sh.Start)
                ws = Math.Max(ws, mw.End);
        if (cfg.BlastStart < sh.End && cfg.BlastEnd > sh.Start) we = Math.Min(we, cfg.BlastStart);
        if (sh.Start > 0) ws += cfg.HandoverRampH;
        if (cfg.FromHour > 0) ws = Math.Max(ws, cfg.FromHour);
        return (ws, Math.Max(ws, we));
    }

    private static ShiftTask IdleTask(ExploderConfig cfg, FaceInput face, double ws, double we, ShiftWindow sh, string label, SchedReason reason)
        => new()
        {
            Id = $"{cfg.IdPrefix}-{face.Group.MainEquipment}-{ShiftShort(sh.Name)}空",
            Process = ProcessType.Idle, Group = Clone(face.Group),
            WorkZone = face.Zone, Material = label, Shift = sh.Name,
            StartHour = Math.Round(ws, 2), EndHour = Math.Round(we, 2), Status = TaskStatus.Planned, Reasons = new() { reason },
        };

    private static void CheckConstraints(ExploderConfig cfg, ExploderResult res)
    {
        foreach (var g in res.Tasks.Where(t => t.Process != ProcessType.Idle).GroupBy(t => t.Group.MainEquipment))
        {
            var ordered = g.OrderBy(t => t.StartHour).ToList();
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].StartHour < ordered[i - 1].EndHour - 0.01)
                    res.Violations.Add(new PlanViolation
                    {
                        Severity = ViolationSeverity.Error, Code = "设备双占", TaskId = ordered[i].Id,
                        Message = $"{g.Key} 时段重叠：{ordered[i - 1].Id} ∩ {ordered[i].Id}",
                    });
        }
        bool hasLoad = res.Tasks.Any(t => t.Process == ProcessType.Load);
        bool hasDrill = res.Tasks.Any(t => t.Process == ProcessType.Drill);
        if (hasLoad && !hasDrill)
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Warn, Code = "工序接续", Message = "有采装但无穿孔任务，备采接续存疑" });
    }

    private static EquipmentGroup Clone(EquipmentGroup g) => new()
    {
        MainEquipment = g.MainEquipment, Trucks = new List<string>(g.Trucks), Aux = new List<string>(g.Aux),
        RecommendedTrucks = g.RecommendedTrucks, GroupCapacityM3PerH = g.GroupCapacityM3PerH,
    };

    private static string ShiftShort(string name) => name.StartsWith("早") ? "早" : name.StartsWith("中") ? "中" : name.StartsWith("夜") ? "夜" : name;
    private static string ShiftOf(ExploderConfig cfg, double hour) => cfg.Shifts.FirstOrDefault(s => hour >= s.Start && hour < s.End)?.Name ?? "";
}
