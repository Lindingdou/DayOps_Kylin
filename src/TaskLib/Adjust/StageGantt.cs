// 忠实移植自原 PitMine3D Modules/TaskLib/Adjust/StageGantt.cs（逐行对应；仅命名空间/依赖适配） —— 只取模型四类（StageGanttCell/Lane/Group/Model）；StageGanttRenderer 是 WPF Canvas 画法，Kylin 侧改在 Avalonia 窗口里按同一套色/几何重画（见 DynamicAdjustWindow）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Adjust;

// ─────────────────────────────────────────────────────────────────────────────
//  跨天·环节甘特 —— 横轴日历天，纵轴「区域 × 工序」。
//
//  与 TaskLib.Gantt.GanttRenderer 的分工（两张图别混）：
//    · GanttRenderer  ：一天之内，横轴 0–24h，行 = 设备。回答「今天谁几点干什么」。
//    · 本图           ：一个月之内，横轴日历天，行 = 区域的一道工序。
//                       回答「哪个区域的哪道工序，哪天在干、干多少、哪天出了岔子」。
//  横轴单位不同（小时 vs 天），行的含义也不同（设备 vs 区域×工序），所以是两张图不是两种皮肤。
//
//  ── 一格 = 一次可点开的演示 ──
//  每个格子挂着那天那道工序的 DayStage 列表，点开即进三维演示。格子是本窗口唯一的导航入口，
//  所以「有量但画不出格子」是致命的：量的高度做了下限钳（MinBarH），
//  再小的量也留一根看得见、点得中的条。
//
//  配色沿用 GanttRenderer 的工序色（穿孔紫/爆破红/采装蓝/运输青/排土琥珀/检修灰），
//  同一道工序在两张图上是同一个颜色 —— 这是跨窗口认色的前提。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>甘特一格：某天 × 某区域 × 某工序。</summary>
public sealed class StageGanttCell
{
    public DateTime Date { get; set; }
    public int DayIndex { get; set; }
    public string Region { get; set; } = "";
    public ProcessType Process { get; set; }
    /// <summary>该格的环节（一天内可能跨多班 ⇒ 多条）。空 = 该天该工序不作业。</summary>
    public List<DayStage> Stages { get; set; } = new();

    public bool IsWorkday { get; set; } = true;
    /// <summary><b>计划侧</b>是推算的（非当日盘子那天）。</summary>
    public bool Projected { get; set; }
    /// <summary><b>实绩侧</b>来自已落盘的班末实绩。与 <see cref="Projected"/> 独立：可以计划推算、实绩真录。</summary>
    public bool ActualFromLedger { get; set; }
    /// <summary>带原因码的异常格。</summary>
    public bool Abnormal { get; set; }

    /// <summary>原计划量（不含跨天顺延进来的）。</summary>
    public double TargetVolumeM3 => Stages.Sum(s => s.TargetVolumeM3);
    public double ActualVolumeM3 => Stages.Sum(s => s.ActualVolumeM3);
    /// <summary>由跨天顺延追加进来的量 m³。</summary>
    public double RolledInM3 => Stages.Sum(s => s.RolledInM3);
    /// <summary>顺延后的计划量 = 原计划 + 顺延。</summary>
    public double PlannedTotalM3 => TargetVolumeM3 + RolledInM3;

    /// <summary>
    /// 画条高用的量 = max(顺延后计划, 实绩)。
    /// <para>三类格子只有这么取才画得出来：计划外作业（计划 0、实绩不为 0）、
    /// 超产日（实绩 > 计划）、以及顺延补量日（计划被追加抬高）——
    /// 而这三类正是「动态调整」最该被看见的。</para>
    /// </summary>
    public double DisplayVolumeM3 => Math.Max(PlannedTotalM3, ActualVolumeM3);

    public bool HasWork => Stages.Count > 0;
    /// <summary>量型工序（采装/排土）才有体积口径；穿孔/爆破/检修按「有没有排」画。</summary>
    public bool IsVolumeProcess => Process is ProcessType.Load or ProcessType.Dump;

    public string Tooltip { get; set; } = "";
}

/// <summary>一条环节线（某区域的某道工序），横跨全部天。</summary>
public sealed class StageGanttLane
{
    public string Region { get; set; } = "";
    public ProcessType Process { get; set; }
    public List<StageGanttCell> Cells { get; set; } = new();

    /// <summary>本线全期<b>计划</b>合计量（量型工序）。</summary>
    public double TotalM3 => Cells.Sum(c => c.TargetVolumeM3);
    /// <summary>本线全期<b>实绩</b>合计量（只有已落盘的天有数）。</summary>
    public double ActualTotalM3 => Cells.Sum(c => c.ActualVolumeM3);
    /// <summary>本线全期由跨天顺延追加进来的量。</summary>
    public double RolledInTotalM3 => Cells.Sum(c => c.RolledInM3);
    /// <summary>本线单天峰值（画条高的分母）。按 max(计划,实绩) 取，超产那天才不会被削平。</summary>
    public double PeakM3 => Cells.Count == 0 ? 0 : Cells.Max(c => c.DisplayVolumeM3);
    public int WorkDayCount => Cells.Count(c => c.HasWork);
}

/// <summary>一个区域分组：组头 + 其下各道工序线。</summary>
public sealed class StageGanttGroup
{
    public string Region { get; set; } = "";
    public List<StageGanttLane> Lanes { get; set; } = new();
    /// <summary>区域全期采装实方 + 排土占容（与 DayPlan.PlanM3 同口径）。</summary>
    public double TotalM3 => Lanes.Where(l => l.Process is ProcessType.Load or ProcessType.Dump)
                                  .Sum(l => l.TotalM3);
    public bool HasAbnormal => Lanes.Any(l => l.Cells.Any(c => c.Abnormal));
    /// <summary>区域全期实绩合计（口径同 <see cref="TotalM3"/>）。</summary>
    public double ActualTotalM3 => Lanes.Where(l => l.Process is ProcessType.Load or ProcessType.Dump)
                                        .Sum(l => l.ActualTotalM3);
    /// <summary>区域全期顺延补量合计。</summary>
    public double RolledInTotalM3 => Lanes.Where(l => l.Process is ProcessType.Load or ProcessType.Dump)
                                          .Sum(l => l.RolledInTotalM3);

    /// <summary>本组有排土线。</summary>
    public bool HasDump => Lanes.Any(l => l.Process == ProcessType.Dump);
    /// <summary>本组有采装线。</summary>
    public bool HasLoad => Lanes.Any(l => l.Process == ProcessType.Load);
    /// <summary>排土场类区域（只排不采）——排序时排在采场之后，与物料流方向一致。</summary>
    public bool IsDumpSite => HasDump && !HasLoad;

    /// <summary>
    /// 组头的量口径。采装是**实方**、排土是**占容方**，两者不是一个东西，
    /// 所以只有真的两样都有时才写「实方 + 占容」——纯采场组写「实方 + 占容」是误导。
    /// </summary>
    public string TotalCaption
    {
        get
        {
            string unit = HasDump && HasLoad ? "（采装实方 + 排土占容）" : HasDump ? " 占容方" : " 实方";

            // 计划为 0 但确实干了活（计划外作业）：只写「全期 0 万m³」是把真实作业量抹掉了。
            if (TotalM3 <= 1e-6)
                return ActualTotalM3 <= 1e-6 ? ""
                     : $"计划外作业：实绩 {ActualTotalM3 / 1e4:0.##} 万m³{unit}（本区无计划量）";

            string s = $"全期计划 {TotalM3 / 1e4:0.##} 万m³{unit}";
            if (RolledInTotalM3 > 1e-6)
                s += $"　＋顺延补 {RolledInTotalM3 / 1e4:0.##} 万m³";
            if (ActualTotalM3 > 1e-6)
                s += $"　实绩 {ActualTotalM3 / 1e4:0.##} 万m³（{ActualTotalM3 / TotalM3 * 100:0}%，仅计已落盘的天）";
            return s;
        }
    }
}

/// <summary>跨天环节甘特的完整模型。</summary>
public sealed class StageGanttModel
{
    public List<DayPlan> Days { get; set; } = new();
    public List<StageGanttGroup> Groups { get; set; } = new();
    public string SourceLabel { get; set; } = "";
    public int ActualDayIndex { get; set; } = -1;

    public bool IsEmpty => Groups.Count == 0 || Days.Count == 0;
    /// <summary>行数 = 组头 + 各线（渲染高度按它定）。</summary>
    public int RowCount => Groups.Count + Groups.Sum(g => g.Lanes.Count);

    /// <summary>从日级时间轴投影。</summary>
    public static StageGanttModel From(DayStageTimeline tl)
    {
        var m = new StageGanttModel
        {
            Days = tl.Days,
            SourceLabel = tl.SourceLabel,
            ActualDayIndex = tl.ActualDayIndex,
        };
        if (tl.Days.Count == 0) return m;

        foreach (var lane in tl.Lanes().GroupBy(x => x.Region, StringComparer.OrdinalIgnoreCase))
        {
            var grp = new StageGanttGroup { Region = lane.Key };
            foreach (var (_, proc) in lane.OrderBy(x => (int)x.Process))
            {
                var ln = new StageGanttLane { Region = lane.Key, Process = proc };
                for (int i = 0; i < tl.Days.Count; i++)
                {
                    var d = tl.Days[i];
                    var stages = d.Stages
                        .Where(s => string.Equals(s.RegionName, lane.Key, StringComparison.OrdinalIgnoreCase)
                                 && s.Process == proc)
                        .ToList();
                    var cell = new StageGanttCell
                    {
                        Date = d.Date,
                        DayIndex = i,
                        Region = lane.Key,
                        Process = proc,
                        Stages = stages,
                        IsWorkday = d.IsWorkday,
                        Projected = stages.Count > 0 && stages.All(s => s.Projected),
                        ActualFromLedger = stages.Any(s => s.ActualFromLedger),
                        Abnormal = stages.Any(s => s.IsAbnormal),
                    };
                    cell.Tooltip = BuildTooltip(cell, d);
                    ln.Cells.Add(cell);
                }
                grp.Lanes.Add(ln);
            }
            m.Groups.Add(grp);
        }
        // 组序：采场在前、排土在后（物料流方向），组内按全期量降序。
        // 单纯按量降序会把最大的排土场顶到采场前面 —— 图上读起来就是"料先落地再挖出来"。
        m.Groups = m.Groups.OrderBy(g => g.IsDumpSite ? 1 : 0)
                           .ThenByDescending(g => g.TotalM3)
                           .ThenBy(g => g.Region, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        return m;
    }

    private static string BuildTooltip(StageGanttCell c, DayPlan d)
    {
        if (!c.IsWorkday) return $"{d.Label}　非作业日（班次日历上没有排班）";
        if (!c.HasWork) return $"{d.Label}　{c.Region} · {c.Process.Label()}：本日不作业";

        string s = $"{d.Label}　{c.Region} · {c.Process.Label()}";
        // 计划侧与实绩侧各标各的 —— 一条环节可以「计划是推算的、实绩是真录的」
        s += c.Projected ? "　计划【推算】" : "　计划【当日盘子·真任务】";
        if (c.ActualFromLedger) s += "　实绩【已落盘】";
        if (c.Stages.Any(x => x.ActualOnly)) s += "　⊕含计划外作业";
        if (c.IsVolumeProcess)
        {
            string unit = c.Process == ProcessType.Dump ? "m³占容" : "m³实方";
            s += $"\n计划 {c.TargetVolumeM3:N0} {unit}";
            if (c.RolledInM3 > 1e-6)
                s += $"　＋顺延补 {c.RolledInM3:N0} ⇒ 合计 {c.PlannedTotalM3:N0}";
            if (c.ActualVolumeM3 > 1e-6)
                s += $"　实绩 {c.ActualVolumeM3:N0}（{c.ActualVolumeM3 / Math.Max(1e-6, c.TargetVolumeM3) * 100:0}%）";
        }
        double fault = c.Stages.Sum(x => x.FaultHours);
        if (fault > 1e-6) s += $"\n故障工时 {fault:0.#} h";
        var equips = c.Stages.Select(x => x.MainEquip).Where(x => x.Length > 0).Distinct().ToList();
        if (equips.Count > 0) s += $"\n设备 {string.Join("、", equips)}";
        var shifts = c.Stages.Select(x => x.Shift).Where(x => x.Length > 0).Distinct().ToList();
        if (shifts.Count > 0) s += $"　班次 {string.Join("/", shifts)}";
        var dest = c.Stages.Select(x => x.DestinationName).Where(x => x.Length > 0).Distinct().ToList();
        if (dest.Count > 0) s += $"\n去向 {string.Join("、", dest)}";
        if (c.Abnormal)
            s += "\n⚠ " + string.Join("、", c.Stages.SelectMany(x => x.Reasons)
                                            .Where(r => r != IncompleteReason.OverAchieved)
                                            .Distinct().Select(r => r.Label()));
        s += "\n（点击打开本日本环节的三维演示）";
        return s;
    }
}
