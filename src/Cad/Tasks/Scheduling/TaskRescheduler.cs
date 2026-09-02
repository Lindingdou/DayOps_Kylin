using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;   // ProcessType

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

// 动态调整 = 实绩回灌 → 达成度评价 → 滚动重排 闭环(忠实移植原 TaskLib.Engine.TaskRescheduler, 纯托管)。
// 以 fromHour 为界扣各面已完成实绩得剩余量, 按原因码策略改写 Config(补车/降产/切面/降效/备机顶替), 再跑 TaskExploder。

/// <summary>原因码 → 调整策略。</summary>
public enum AdjustStrategy
{
    RollForward,      // 剩余量滚到后续班次
    AddTrucks,        // 补车到推荐车数
    ReduceTarget,     // 削目标到剩余产能
    ReassignBackup,   // 备机顶替主设备
    ReduceCapacity,   // 全盘降效
    SwitchFace,       // 换面(缺料转有料面)
}

public sealed class RescheduleResult
{
    public ExploderResult Plan { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public double RolledShortfallM3 { get; set; }
    public double FromHour { get; set; }
    public string Summary => Notes.Count == 0
        ? "无可重排的剩余量"
        : $"自 {Hm(FromHour)} 起重排：回摊剩余 {RolledShortfallM3:0} m³，新计划 {Plan.Tasks.Count(t => t.Process != ProcessType.Idle)} 项，校核 {Plan.Violations.Count} 条。";

    private static string Hm(double hh) { int h = (int)hh; int m = (int)Math.Round((hh - h) * 60); return $"{h:00}:{m:00}"; }
}

public static class TaskRescheduler
{
    /// <summary>原因码 → 默认策略。</summary>
    public static AdjustStrategy StrategyFor(SchedReason r) => r switch
    {
        SchedReason.Fault => AdjustStrategy.ReassignBackup,
        SchedReason.TruckShortage => AdjustStrategy.AddTrucks,
        SchedReason.OreShortage => AdjustStrategy.SwitchFace,
        SchedReason.Weather => AdjustStrategy.ReduceCapacity,
        SchedReason.Absence => AdjustStrategy.ReduceCapacity,
        SchedReason.OverPlanned => AdjustStrategy.ReduceTarget,
        _ => AdjustStrategy.RollForward,
    };

    /// <summary>从 fromHour 起对各面剩余工作量滚动重排；zoneStrategy 指定个别面策略(缺省 RollForward)。</summary>
    public static RescheduleResult Reschedule(
        ExploderConfig baseCfg,
        IReadOnlyList<ShiftTask> currentTasks,
        double fromHour,
        IReadOnlyDictionary<string, AdjustStrategy> zoneStrategy)
    {
        var res = new RescheduleResult { FromHour = fromHour };

        var doneByZone = currentTasks
            .Where(t => t.Process is ProcessType.Load or ProcessType.Dump)
            .GroupBy(t => t.WorkZone)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.ActualVolumeM3));

        var cfg = CloneConfig(baseCfg);
        cfg.FromHour = fromHour;
        cfg.IdPrefix = baseCfg.IdPrefix + "R";

        bool globalDerate = false;
        var loadFaces = cfg.Faces.Where(f => f.Process == ProcessType.Load).ToList();

        foreach (var f in cfg.Faces)
        {
            double done = doneByZone.TryGetValue(f.Zone, out var d) ? d : 0;
            double remain = Math.Max(0, Math.Round(f.DayTargetM3 - done));
            f.DayTargetM3 = remain;
            res.RolledShortfallM3 += remain;

            var strat = zoneStrategy.TryGetValue(f.Zone, out var s) ? s : AdjustStrategy.RollForward;
            switch (strat)
            {
                case AdjustStrategy.AddTrucks:
                    int add = Math.Max(0, f.Group.RecommendedTrucks - f.Group.Trucks.Count);
                    for (int i = 0; i < add; i++) f.Group.Trucks.Add($"T+借{i + 1}");
                    if (add > 0) res.Notes.Add($"{f.Zone}：补 {add} 辆卡车至推荐 {f.Group.RecommendedTrucks} 辆，解运力不足。");
                    break;

                case AdjustStrategy.ReassignBackup:
                    string old = f.Group.MainEquipment;
                    f.Group.MainEquipment = old + "·备";
                    res.Notes.Add($"{f.Zone}：主设备 {old} 故障，{f.Group.MainEquipment} 顶替接管剩余 {remain:0} m³。");
                    break;

                case AdjustStrategy.ReduceCapacity:
                    globalDerate = true;
                    break;

                case AdjustStrategy.SwitchFace:
                    var target = loadFaces.FirstOrDefault(o => o != f && o.DayTargetM3 > 0);
                    if (f.Process == ProcessType.Load && target != null && remain > 0)
                    {
                        target.DayTargetM3 += remain;
                        res.Notes.Add($"{f.Zone}：缺料 → 剩余 {remain:0} m³ 切至「{target.Zone}」。");
                        f.DayTargetM3 = 0;
                    }
                    break;

                case AdjustStrategy.ReduceTarget:
                    double cap = AvailableCapacityM3(cfg, f);
                    if (remain > cap && cap > 0)
                    {
                        res.Notes.Add($"{f.Zone}：计划过满 → 目标由 {remain:0} 削至产能上限 {cap:0} m³，余量转次日。");
                        f.DayTargetM3 = Math.Round(cap);
                    }
                    break;

                case AdjustStrategy.RollForward:
                default:
                    if (remain > 0) res.Notes.Add($"{f.Zone}：剩余 {remain:0} m³ 顺延至后续班次。");
                    break;
            }
        }

        if (globalDerate)
        {
            const double k = 0.8;
            foreach (var f in cfg.Faces) f.Group.GroupCapacityM3PerH *= k;
            res.Notes.Add($"全盘降效系数 {k:0.0}（天气/缺勤）：各编组班产下调 {(1 - k) * 100:0}%。");
        }

        res.Plan = TaskExploder.Explode(cfg);
        return res;
    }

    /// <summary>某面在 fromHour..24 时窗内、扣检修/爆破后的产能上限(ReduceTarget 削峰用)。</summary>
    private static double AvailableCapacityM3(ExploderConfig cfg, FaceInput f)
    {
        double cap = Math.Max(1e-6, f.Group.GroupCapacityM3PerH), total = 0;
        foreach (var sh in cfg.Shifts)
        {
            double ws = Math.Max(sh.Start, cfg.FromHour), we = sh.End;
            if (cfg.BlastStart < sh.End && cfg.BlastEnd > sh.Start) we = Math.Min(we, cfg.BlastStart);
            if (sh.Start > 0) ws += cfg.HandoverRampH;
            total += Math.Max(0, we - ws) * cap;
        }
        return total;
    }

    private static ExploderConfig CloneConfig(ExploderConfig s) => new()
    {
        DateLabel = s.DateLabel, IdPrefix = s.IdPrefix, NowHour = s.NowHour, FromHour = s.FromHour,
        BlastStart = s.BlastStart, BlastEnd = s.BlastEnd, HandoverRampH = s.HandoverRampH,
        Shifts = s.Shifts.Select(x => new ShiftWindow(x.Name, x.Start, x.End)).ToList(),
        // 忠实原 CloneConfig: 不克隆 Blend(重排不再套配煤——配煤已在原始计划中平衡)
        Faces = s.Faces.Select(f => new FaceInput
        {
            Zone = f.Zone, BenchElevationM = f.BenchElevationM, EngineeringPositionId = f.EngineeringPositionId,
            Material = f.Material, DayTargetM3 = f.DayTargetM3, Quality = f.Quality, Process = f.Process,
            Group = new EquipmentGroup
            {
                MainEquipment = f.Group.MainEquipment, Trucks = new List<string>(f.Group.Trucks),
                Aux = new List<string>(f.Group.Aux), RecommendedTrucks = f.Group.RecommendedTrucks,
                GroupCapacityM3PerH = f.Group.GroupCapacityM3PerH,
            },
        }).ToList(),
        Drills = s.Drills.Select(x => new DrillInput { EquipId = x.EquipId, Zone = x.Zone, BenchElevationM = x.BenchElevationM, Start = x.Start, End = x.End }).ToList(),
        Maintenance = s.Maintenance.Select(x => new MaintenanceWindow { EquipId = x.EquipId, Start = x.Start, End = x.End, Label = x.Label }).ToList(),
    };
}
