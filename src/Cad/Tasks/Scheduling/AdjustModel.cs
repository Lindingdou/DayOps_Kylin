using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>动态调整表的一行（一条任务）。</summary>
public sealed class AdjustRow
{
    public ShiftTask Task { get; init; } = new();
    public string Shift { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Zone { get; set; } = "";
    public string Process { get; set; } = "";
    public string Equip { get; set; } = "";
    public string Span { get; set; } = "";

    public double PlanM3 { get; set; }
    /// <summary>实绩量 m³。<b>null = 没录</b>（不是 0）—— 没录时达成度判不了。</summary>
    public double? ActualM3 { get; set; }

    public string Plan => PlanM3 > 1e-9 ? PlanM3.ToString("N0", CultureInfo.InvariantCulture) + " m³" : "—";
    public string Actual => ActualM3 is { } a ? a.ToString("N0", CultureInfo.InvariantCulture) + " m³" : "—";

    /// <summary>达成度 %。<b>没录实绩就是判不了</b>，不按 0 算 —— 那会让没录的面全成"欠产 100%"。</summary>
    public double? AttainmentPct => ActualM3 is { } a && PlanM3 > 1e-9 ? a / PlanM3 * 100 : null;
    public string Attainment => AttainmentPct is { } p ? p.ToString("0", CultureInfo.InvariantCulture) + "%" : "—";

    /// <summary>欠量 m³（实绩没录时判不了，不是"全欠"）。</summary>
    public double? ShortfallM3 => ActualM3 is { } a ? Math.Max(0, PlanM3 - a) : null;

    /// <summary>原因码（人选；空 = 没判）。</summary>
    public SchedReason? Reason { get; set; }
    public string ReasonText => Reason is { } r ? AdjustModel.ReasonZh(r) : "—";
    /// <summary>该原因码的调整动作建议（<b>与设计文档同一张表</b>）。</summary>
    public string Advice => Reason is { } r ? AdjustModel.AdviceOf(r) : "先判原因码，再谈怎么调";
    /// <summary>要用的策略（缺省由原因码推，可人工改）。</summary>
    public AdjustStrategy Strategy { get; set; } = AdjustStrategy.RollForward;
}

/// <summary>一次重排的结果摘要。</summary>
public sealed class AdjustResult
{
    public bool Ran;
    public double FromHour;
    public double RolledShortfallM3;
    public int TaskCount;
    public int ViolationCount;
    public List<string> Notes = new();
    public string Blocked = "";

    /// <summary>
    /// 重排出来的新计划。<b>重排走的是盘子的一份克隆</b>（<c>TaskRescheduler.CloneConfig</c>）——
    /// 原盘子不动，调整的结果只在这里。拿原盘子再装一次箱得到的还是<b>调整前</b>那一版，
    /// 而它算得出来、也不报错。
    /// </summary>
    public ExploderResult? Plan;

    public string Summary => Blocked.Length > 0 ? Blocked
        : !Ran ? "还没重排。"
        : $"自 {AdjustModel.Hm(FromHour)} 起重排：回摊剩余 {RolledShortfallM3:N0} m³，"
        + $"新计划 {TaskCount} 项，校核 {ViolationCount} 条。";
}

/// <summary>
/// 生产任务动态调整的口径（移植原 <c>TaskLib.Features.DynamicAdjustWindow</c> 的
/// <b>原因码路由</b>与<b>滚动重排 + 欠量回摊</b>两段；重排算法本身是本仓已有的
/// <see cref="TaskRescheduler"/> —— 它移过来之后<b>一直没有入口</b>）。
///
/// <para>
/// <b>原因码 → 调整动作</b>这张表照搬原版（与设计文档 §6 一致）。它的意义在于：
/// 同样是"没干够"，故障要顶设备、缺车要补车、缺料要切面、天气要全盘降效回摊 ——
/// <b>动作选错了，重排出来的计划照样排得满满的，而现场还是干不动</b>。
/// </para>
///
/// <para>
/// <b>★ 实绩没录 ≠ 实绩为 0</b>：没录时达成度与欠量一律<b>判不了</b>。
/// 按 0 算的话，没录实绩的面会全部变成"欠产 100%"，重排就会把整天的量再排一遍。
/// </para>
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item><b>跨天甘特（区域 × 工序 铺到日历天）未移</b>，以及点开一格在三维里放那一天的推进演示、
///     GeoTIFF 正射影像底图 —— 它们依赖未移植的 <c>Sim*Stage</c> 推演管线。
///     原版为此立的那条纪律（真实天实心实边 / 推算天淡色虚边、重排只动真实那一天）
///     等那条管线移过来时一并带上。</item>
///   <item><b>逐任务实绩没有台账</b>：Kylin 的实绩录入（§三四九）落的是日汇总
///     <c>daily_mine_summary</c>，到不了"这条任务干了多少"。故实绩在本窗<b>由调度员逐行录</b>，
///     没录的就是没录 —— 不去日汇总里摊一个数下来冒充逐任务实绩。</item>
/// </list>
/// </summary>
public static class AdjustModel
{
    /// <summary>原因码 → 调整动作（与设计文档同一张表；两处各写一份迟早对不上）。</summary>
    public static string AdviceOf(SchedReason r) => r switch
    {
        SchedReason.Fault => "欠量转同面其它编组 / 派维修队 / 重排剩余任务",
        SchedReason.TruckShortage => "补车 / 降铲产对齐车队 / 调邻组卡车",
        SchedReason.ProcessWait => "优先调度上游穿孔·爆破 / 该面顺延",
        SchedReason.BlastWait => "对齐爆破时窗 / 清场后复工 / 该面顺延",
        SchedReason.OreShortage => "切面（换作业面）/ 报采准不断档预警",
        SchedReason.Weather => "全盘降效回摊 / 启用保守工作历",
        SchedReason.RoadCongestion => "调卸点 / 错峰发车",
        SchedReason.Absence => "顶班 / 降编组",
        SchedReason.Maintenance => "工作历未对齐 → 修班次日历",
        SchedReason.OverPlanned => "反馈编制：收紧产能预测口径 / 削峰至产能上限",
        SchedReason.OverAchieved => "超额完成 —— 核对量口径是否记重，再谈是否上调目标",
        _ => "—",
    };

    public static string ReasonZh(SchedReason r) => r switch
    {
        SchedReason.Fault => "设备故障",
        SchedReason.Maintenance => "计划检修",
        SchedReason.Weather => "天气",
        SchedReason.BlastWait => "等爆破",
        SchedReason.ProcessWait => "等上工序",
        SchedReason.TruckShortage => "运力不足",
        SchedReason.OreShortage => "缺料",
        SchedReason.RoadCongestion => "道路拥堵",
        SchedReason.Absence => "缺勤",
        SchedReason.OverPlanned => "计划过满",
        SchedReason.OverAchieved => "超额完成",
        _ => r.ToString(),
    };

    /// <summary>界面下拉的原因码顺序（按现场常见度）。</summary>
    public static readonly SchedReason[] Reasons =
    {
        SchedReason.Fault, SchedReason.TruckShortage, SchedReason.OreShortage, SchedReason.ProcessWait,
        SchedReason.BlastWait, SchedReason.Weather, SchedReason.RoadCongestion, SchedReason.Absence,
        SchedReason.Maintenance, SchedReason.OverPlanned, SchedReason.OverAchieved,
    };

    public static string StrategyZh(AdjustStrategy s) => s switch
    {
        AdjustStrategy.RollForward => "顺延后续班次",
        AdjustStrategy.AddTrucks => "补车到推荐车数",
        AdjustStrategy.ReassignBackup => "备机顶替",
        AdjustStrategy.ReduceCapacity => "全盘降效回摊",
        AdjustStrategy.SwitchFace => "切面",
        AdjustStrategy.ReduceTarget => "削峰至产能上限",
        _ => s.ToString(),
    };

    public static readonly AdjustStrategy[] Strategies =
    {
        AdjustStrategy.RollForward, AdjustStrategy.AddTrucks, AdjustStrategy.ReassignBackup,
        AdjustStrategy.ReduceCapacity, AdjustStrategy.SwitchFace, AdjustStrategy.ReduceTarget,
    };

    /// <summary>把一班的任务折成可调整的行（空闲笔不列）。</summary>
    public static List<AdjustRow> BuildRows(IEnumerable<ShiftTask>? tasks, string shift)
        => (tasks ?? Enumerable.Empty<ShiftTask>())
            .Where(t => t != null && t.Process != ProcessType.Idle)
            .Where(t => string.IsNullOrWhiteSpace(shift) || string.Equals(t.Shift, shift, StringComparison.Ordinal))
            .OrderBy(t => t.StartHour).ThenBy(t => t.Group.MainEquipment, StringComparer.Ordinal)
            .Select(t => new AdjustRow
            {
                Task = t,
                Shift = t.Shift,
                TaskId = t.Id,
                Zone = t.WorkZone,
                Process = t.Process.Label(),
                Equip = t.Group.MainEquipment.Length > 0 ? t.Group.MainEquipment : "—",
                Span = $"{Hm(t.StartHour)}–{Hm(t.EndHour)}",
                PlanM3 = t.TargetVolumeM3,
                // 实绩带过来：装箱产物上有回灌字段，没回灌过就是 0 ⇒ 当作"没录"
                ActualM3 = t.ActualVolumeM3 > 1e-9 ? t.ActualVolumeM3 : null,
                Strategy = AdjustStrategy.RollForward,   // 判了原因码之后由 ApplyReason 换成对应策略
            })
            .ToList();

    /// <summary>判了原因码之后，把缺省策略跟着换掉（人仍可覆盖）。</summary>
    public static void ApplyReason(AdjustRow row, SchedReason reason)
    {
        if (row == null) return;
        row.Reason = reason;
        row.Strategy = TaskRescheduler.StrategyFor(reason);
    }

    /// <summary>
    /// 执行滚动重排。<paramref name="fromHour"/> 之后的量才重排；
    /// 各面按行上选的策略走（同一个面出现多行时，<b>以先判出原因码的那一行为准</b> ——
    /// 一个面同时"缺车"又"缺料"时，两种动作会互相抵消，须由人明确一个）。
    /// </summary>
    public static AdjustResult Run(ExploderConfig? cfg, IReadOnlyList<AdjustRow>? rows, double fromHour)
    {
        var res = new AdjustResult { FromHour = fromHour };
        if (cfg == null || cfg.Faces.Count == 0)
        {
            res.Blocked = "没有可重排的盘子 —— 先把当日作业面装出来。";
            return res;
        }
        var list = (rows ?? Array.Empty<AdjustRow>()).Where(r => r != null).ToList();
        if (list.Count == 0)
        {
            res.Blocked = "本班没有任务，重排无从谈起。";
            return res;
        }

        // 实绩回灌到任务上（重排按"已经干了多少"扣）
        foreach (var r in list) r.Task.ActualVolumeM3 = r.ActualM3 ?? 0;

        var byZone = new Dictionary<string, AdjustStrategy>(StringComparer.Ordinal);
        foreach (var r in list)
        {
            if (r.Reason == null || r.Zone.Length == 0) continue;
            if (!byZone.ContainsKey(r.Zone)) byZone[r.Zone] = r.Strategy;
        }

        var done = TaskRescheduler.Reschedule(cfg, list.Select(r => r.Task).ToList(), fromHour, byZone);
        res.Ran = true;
        res.Plan = done.Plan;
        res.RolledShortfallM3 = done.RolledShortfallM3;
        res.TaskCount = done.Plan.Tasks.Count(t => t.Process != ProcessType.Idle);
        res.ViolationCount = done.Plan.Violations.Count;
        res.Notes = new List<string>(done.Notes);
        if (res.Notes.Count == 0)
            res.Notes.Add("没有需要调整的量 —— 各面实绩都已达计划（或实绩还没录）。");
        return res;
    }

    internal static string Hm(double hh)
    {
        int h = (int)hh;
        int m = (int)Math.Round((hh - h) * 60);
        if (m == 60) { h++; m = 0; }
        return $"{h:00}:{m:00}";
    }
}
