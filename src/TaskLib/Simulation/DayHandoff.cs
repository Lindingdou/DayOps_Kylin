// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/DayHandoff.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>衔接的种类 —— 一天里"上一个活怎么接到下一个活"只有这么几种接法。</summary>
public enum HandoffKind
{
    /// <summary>工序链：同一个面上 穿孔 → 爆破 → 采装 → 排土 的先后。</summary>
    Process,
    /// <summary>同设备接续：同一台主设备干完这个接下一个（换面就是转场）。</summary>
    Equipment,
    /// <summary>同面接续：同一个作业面上前后两个活（设备可能换人）。</summary>
    Face,
}

/// <summary>一条衔接：从哪个活接到哪个活，隔多久，接得上接不上。</summary>
public sealed class HandoffLink
{
    public HandoffKind Kind { get; init; }
    public string FromTaskId { get; init; } = "";
    public string ToTaskId { get; init; } = "";
    public ProcessType FromProcess { get; init; }
    public ProcessType ToProcess { get; init; }
    public string FromZone { get; init; } = "";
    public string ToZone { get; init; } = "";
    public string FromShift { get; init; } = "";
    public string ToShift { get; init; } = "";
    public string Equipment { get; init; } = "";
    public double FromEndH { get; init; }
    public double ToStartH { get; init; }

    /// <summary>间隔小时。负数 = 后序在前序完工前就开了。</summary>
    public double GapH => ToStartH - FromEndH;

    /// <summary>跨班交接：两端不在同一个班上 —— 这就是交班时要交代的那件事。</summary>
    public bool CrossShift => FromShift.Length > 0 && ToShift.Length > 0
                           && !string.Equals(FromShift, ToShift, StringComparison.Ordinal);

    /// <summary>
    /// 换面：同一台设备从一个面挪到另一个面 —— 转场，要走行、要时间。
    ///
    /// <para><b>两端都得有面名</b>。只判"两个字符串不相等"的话，那条 <c>WorkZone</c> 为空的
    /// 「检修/空闲·未记位置」任务会让它的设备凭空多出一次转场 ——
    /// 实测就是这样：信息栏上打出来是「WK-10 ⇒ 主采面·东（采煤）」，箭头前面是空的。
    /// 没记面不等于换了面，那是**缺数据**，不是转场。</para>
    /// </summary>
    public bool MovesFace => Kind == HandoffKind.Equipment
                          && FromZone.Length > 0 && ToZone.Length > 0
                          && !string.Equals(FromZone, ToZone, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 接不上。
    /// <para>工序链与同设备链上，后序开工早于前序完工就是**真冲突**：
    /// 前者是"炮还没放就开始装"，后者是"一台设备同时在两个地方"。</para>
    /// <para>同面链（Face）不算 —— 一个面上两台设备并排干本来就允许。</para>
    /// </summary>
    public bool Conflict => Kind != HandoffKind.Face && GapH < -1e-9;

    public string Label => Kind switch
    {
        HandoffKind.Process => $"{FromProcess.Label()}→{ToProcess.Label()}",
        HandoffKind.Equipment => Equipment.Length > 0 ? $"{Equipment} 接续" : "同设备接续",
        _ => "同面接续",
    };
}

/// <summary>一个作业面一天的工序链（按时间首尾排开）。</summary>
public sealed class FaceChain
{
    public string Zone { get; init; } = "";
    public List<ProductionTask> Tasks { get; } = new();
    public List<HandoffLink> Links { get; } = new();

    public double FirstStartH => Tasks.Count == 0 ? double.NaN : Tasks.Min(t => t.StartHour);
    public double LastEndH => Tasks.Count == 0 ? double.NaN : Tasks.Max(t => t.EndHour);

    /// <summary>这个面一天里被几个班动过。</summary>
    public int ShiftCount => Tasks.Select(t => t.Shift ?? "").Where(s => s.Length > 0)
                                  .Distinct(StringComparer.Ordinal).Count();

    /// <summary>链上的空档合计（相邻两活之间的正间隔）—— 面在等人的时间。</summary>
    public double IdleGapH => Links.Where(l => l.Kind == HandoffKind.Face && l.GapH > 0).Sum(l => l.GapH);
}

/// <summary>
/// 一天的**任务衔接**。
///
/// <para><b>为什么单独做这一层</b>：日班档一开始是把月度那套（块体沿运输线搬、排土场逐层长高）
/// 按班压缩了演一遍 —— 期长换成 8 小时，别的没变。可一天的调度关心的根本不是那个：
/// 关心的是<b>活跟活怎么接上</b> —— 炮放完了采装才能进场、一台铲干完这个面挪到哪个面、
/// 交班时哪个面上还剩多少没干完要交代给下一班。那是链，不是量。</para>
///
/// <para>这一层只回答"接"的问题，量的口径一律不碰（还在原来那几层里）。</para>
/// </summary>
public sealed class DayHandoffPlan
{
    public List<FaceChain> Faces { get; } = new();
    public List<HandoffLink> Links { get; } = new();
    public List<string> Notes { get; } = new();

    public IEnumerable<HandoffLink> Conflicts => Links.Where(l => l.Conflict);
    public IEnumerable<HandoffLink> CrossShift => Links.Where(l => l.CrossShift);
    public IEnumerable<HandoffLink> Moves => Links.Where(l => l.MovesFace);

    /// <summary>工序链的先后：号越小越靠前。不在链上的（检修/空闲/运输）返回 -1。</summary>
    internal static int ChainOrder(ProcessType p) => p switch
    {
        ProcessType.Drill => 0,
        ProcessType.Blast => 1,
        ProcessType.Load => 2,
        ProcessType.Dump => 3,
        _ => -1,
    };

    /// <summary>
    /// 从当日盘子建衔接。
    ///
    /// <para><b>口径</b>：</para>
    /// <list type="bullet">
    ///   <item><b>H1 工序链</b>：同一个面上，按 穿孔→爆破→采装→排土 的先后，
    ///   相邻两级各连一条。后序开工早于前序完工 ⇒ 冲突。</item>
    ///   <item><b>H2 同设备接续</b>：同一台主设备按时间排开，相邻两活连一条。
    ///   重叠 ⇒ 冲突（一台设备不能同时在两处）；换面 ⇒ 转场。</item>
    ///   <item><b>H3 同面接续</b>：同一个面上按时间排开的相邻两活连一条 ——
    ///   答"这个面一天是怎么被推进的"。同面并排作业不算冲突。</item>
    /// </list>
    ///
    /// <para><b>检修/空闲不进工序链</b>（ChainOrder = -1）：它不推进任何东西。
    /// 但它**进**同设备链 —— "这台铲这个班在检修"正是下一班要接的信息。</para>
    /// </summary>
    public static DayHandoffPlan Build(IReadOnlyList<ProductionTask>? tasks)
    {
        var plan = new DayHandoffPlan();
        if (tasks == null || tasks.Count == 0)
        {
            plan.Notes.Add("当日盘子为空 —— 没有任务就没有衔接可言（不是漏算）。");
            return plan;
        }

        // ── H3 同面接续 + 面链 ──
        foreach (var g in tasks.Where(t => !string.IsNullOrWhiteSpace(t.WorkZone))
                               .GroupBy(t => t.WorkZone, StringComparer.OrdinalIgnoreCase)
                               .OrderBy(g => g.Min(t => t.StartHour)))
        {
            var chain = new FaceChain { Zone = g.Key };
            chain.Tasks.AddRange(g.OrderBy(t => t.StartHour).ThenBy(t => t.EndHour));
            for (int i = 0; i + 1 < chain.Tasks.Count; i++)
                chain.Links.Add(Link(HandoffKind.Face, chain.Tasks[i], chain.Tasks[i + 1]));
            plan.Faces.Add(chain);
            plan.Links.AddRange(chain.Links);
        }

        // ── H1 工序链（面内，按工序级别相邻）──
        foreach (var chain in plan.Faces)
        {
            var staged = chain.Tasks.Where(t => ChainOrder(t.Process) >= 0)
                                    .OrderBy(t => ChainOrder(t.Process)).ThenBy(t => t.StartHour)
                                    .ToList();
            for (int i = 0; i + 1 < staged.Count; i++)
            {
                if (ChainOrder(staged[i].Process) == ChainOrder(staged[i + 1].Process)) continue;
                plan.Links.Add(Link(HandoffKind.Process, staged[i], staged[i + 1]));
            }
        }

        // ── H2 同设备接续 ──
        foreach (var g in tasks.Where(t => !string.IsNullOrWhiteSpace(t.Group?.MainEquipment))
                               .GroupBy(t => t.Group!.MainEquipment, StringComparer.OrdinalIgnoreCase))
        {
            var seq = g.OrderBy(t => t.StartHour).ThenBy(t => t.EndHour).ToList();
            for (int i = 0; i + 1 < seq.Count; i++)
                plan.Links.Add(Link(HandoffKind.Equipment, seq[i], seq[i + 1]));
        }

        // ── 说明：断链（有采装但当天没有它的前序爆破）──
        foreach (var chain in plan.Faces)
        {
            bool hasLoad = chain.Tasks.Any(t => t.Process == ProcessType.Load);
            bool hasBlast = chain.Tasks.Any(t => t.Process == ProcessType.Blast);
            if (hasLoad && !hasBlast)
                plan.Notes.Add($"「{chain.Zone}」当天有采装但**没有前序爆破** —— "
                             + "要么吃的是前几天留下的爆堆（正常），要么爆破没排进来（缺口）。"
                             + "这一层分不出是哪种，得看爆堆存量台账。");
        }

        int conflicts = plan.Links.Count(l => l.Conflict);
        plan.Notes.Add(conflicts == 0
            ? $"衔接自洽：{plan.Links.Count} 条接续（面 {plan.Faces.Count} 个），无前后颠倒、无一机两地。"
            : $"⚠ {conflicts} 条接续**接不上**：后序开工早于前序完工。工序链上是「炮没放就装」，"
            + "同设备链上是「一台设备同时在两个地方」—— 两种都是排产要改的，不是显示问题。");

        return plan;
    }

    private static HandoffLink Link(HandoffKind kind, ProductionTask a, ProductionTask b) => new()
    {
        Kind = kind,
        FromTaskId = a.Id ?? "", ToTaskId = b.Id ?? "",
        FromProcess = a.Process, ToProcess = b.Process,
        FromZone = a.WorkZone ?? "", ToZone = b.WorkZone ?? "",
        FromShift = a.Shift ?? "", ToShift = b.Shift ?? "",
        Equipment = kind == HandoffKind.Equipment ? (a.Group?.MainEquipment ?? "") : "",
        FromEndH = a.EndHour, ToStartH = b.StartHour,
    };
}
