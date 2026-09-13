// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/PlanDrift.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  「计划被改过没有」—— 基准 × 现盘的逐条对账。
//
//  ══ 这是给谁用的 ══
//  动态调整里的**欠量** = 计划量 − 实绩量。实绩量是死的（干了多少就是多少），
//  计划量却是**活的**：`ProductionPlanContext` 每次调用都重新装配，
//  台账被谁改一下，计划量就跟着走。于是：
//    · 早上排 3000、干了 2000 ⇒ 欠量 1000；
//    · 中午有人把单元量改成 2000 ⇒ 下午再看**欠量变成 0**，
//      「今天没欠」，而那 1000 方谁也没干。
//  重排本身拿现盘是对的（只有存在的任务才排得动），
//  但**欠量这把尺子必须说清楚是对着哪一份计划量的**。
//
//  ══ 三类漂移，缺一不可 ══
//  改量 / 消失 / 新增。只查「改量」会漏掉最狠的那类 ——
//  基准里有、现盘里没有的任务，**任何"遍历当前任务"的统计都碰不到它**，
//  于是它的欠量是 0，而它明明是当天排过的活。
//
//  ══ 一条纪律 ══
//  净差额 ≈ 0 **不等于**没改。加一条减一条互相抵消时净差额是 0，
//  而两个作业面的活全变了。所以判「有没有漂移」看的是**条数**，不是净差额。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一条被改过量的任务。</summary>
public sealed class PlanDriftRow
{
    public string Id = "";
    /// <summary>人能看懂的说法（作业面 · 班次 · 工序）。</summary>
    public string Label = "";
    /// <summary>基准里的计划量。</summary>
    public double BaseM3;
    /// <summary>现盘里的计划量。</summary>
    public double NowM3;
    public double DeltaM3 => NowM3 - BaseM3;
}

/// <summary>一次对账的结果。</summary>
public sealed class PlanDriftResult
{
    public BaselineSource Source;
    /// <summary>界面直接显示的一句话。</summary>
    public string Headline = "";
    /// <summary>基准本身的说明（快照存于何时 / 为什么没有）。</summary>
    public string BaselineLabel = "";

    /// <summary>量被改过的。</summary>
    public List<PlanDriftRow> Changed = new();
    /// <summary>基准里有、现盘里没有的 —— <b>最容易漏掉的一类</b>。</summary>
    public List<ProductionTask> Vanished = new();
    /// <summary>现盘里有、基准里没有的。</summary>
    public List<ProductionTask> Added = new();

    public double BaseTotalM3;
    public double NowTotalM3;
    public double NetDeltaM3 => NowTotalM3 - BaseTotalM3;

    /// <summary>
    /// 对账**做成了**（有基准可对）。
    /// <para>没有基准时它是 false —— 此时 <see cref="HasDrift"/> 也是 false，
    /// 但那是「<b>判不了</b>」，不是「没改过」。两者在界面上必须说成两句话。</para>
    /// </summary>
    public bool Comparable => Source == BaselineSource.Snapshot;

    /// <summary>动过。<b>看条数，不看净差额</b>（一加一减会把净差额抵成 0）。</summary>
    public bool HasDrift => Changed.Count > 0 || Vanished.Count > 0 || Added.Count > 0;
}

/// <summary>基准 × 现盘对账。永不抛。</summary>
public static class PlanDrift
{
    /// <summary>量的对比容差（m³）—— 浮点噪声不算改动。</summary>
    public const double ToleranceM3 = 1.0;

    /// <summary>只有量型工序谈体积；穿孔/爆破没有 m³，拿它们比量是无中生有。</summary>
    private static bool IsVolume(ProductionTask t)
        => t.Process is ProcessType.Load or ProcessType.Dump;

    private static string LabelOf(ProductionTask t)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.WorkZone)) parts.Add(t.WorkZone);
        if (!string.IsNullOrWhiteSpace(t.Shift)) parts.Add(t.Shift + "班");
        parts.Add(t.Process.Label());
        if (!string.IsNullOrWhiteSpace(t.Group?.MainEquipment)) parts.Add(t.Group!.MainEquipment);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 基准 × 现盘 → 漂移。<b>按稳定键对号</b>（<see cref="ProductionTask.Id"/>），
    /// 不按下标、不按名字 —— 按下标对会在"今天多排了一台铲"那天整体错位，而每一行看着都对。
    /// </summary>
    public static PlanDriftResult Compare(
        PlanBaselineResult? baseline, IReadOnlyList<ProductionTask>? now, double tolM3 = ToleranceM3)
    {
        var r = new PlanDriftResult();
        r.Source = baseline?.Source ?? BaselineSource.None;
        r.BaselineLabel = baseline?.Label ?? "";

        var nowList = (now ?? Array.Empty<ProductionTask>()).Where(t => t != null && IsVolume(t)).ToList();
        r.NowTotalM3 = nowList.Sum(t => t.TargetVolumeM3);

        if (!r.Comparable)
        {
            // ★ 这里**不能**说"计划没被改过"。没有基准就是判不了 ——
            //   把"判不了"说成"没变"，是这一整块最容易犯的错。
            r.Headline = "◆ 这一天没有计划快照 ⇒ **欠量对着的是现算的计划**，"
                       + "台账一改欠量就跟着变（早上排 3000 干了 2000 欠 1000；"
                       + "中午把量改成 2000，下午再看**欠量就成了 0**）。"
                       + "到「生产任务编制」点一次「保存本日计划」即可有基准。";
            return r;
        }

        var baseList = baseline!.Tasks.Where(t => t != null && IsVolume(t)).ToList();
        r.BaseTotalM3 = baseList.Sum(t => t.TargetVolumeM3);

        var nowById = new Dictionary<string, ProductionTask>(StringComparer.Ordinal);
        foreach (var t in nowList) if (!string.IsNullOrWhiteSpace(t.Id)) nowById[t.Id] = t;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in baseList)
        {
            if (string.IsNullOrWhiteSpace(b.Id) || !nowById.TryGetValue(b.Id, out var n))
            { r.Vanished.Add(b); continue; }

            seen.Add(b.Id);
            if (Math.Abs(n.TargetVolumeM3 - b.TargetVolumeM3) > tolM3)
                r.Changed.Add(new PlanDriftRow
                {
                    Id = b.Id, Label = LabelOf(b),
                    BaseM3 = b.TargetVolumeM3, NowM3 = n.TargetVolumeM3,
                });
        }
        foreach (var t in nowList)
            if (string.IsNullOrWhiteSpace(t.Id) || !seen.Contains(t.Id)) r.Added.Add(t);

        r.Headline = Describe(r);
        return r;
    }

    private static string Describe(PlanDriftResult r)
    {
        if (!r.HasDrift)
            return $"基准：当日快照 · 计划量 {r.BaseTotalM3:N0} m³ 与快照一致 ⇒ **欠量口径成立**。";

        var bits = new List<string>();
        if (r.Changed.Count > 0) bits.Add($"{r.Changed.Count} 条量被改过");
        if (r.Vanished.Count > 0) bits.Add($"{r.Vanished.Count} 条**消失**");
        if (r.Added.Count > 0) bits.Add($"{r.Added.Count} 条新增");

        string s = $"⚠ 计划自快照以来动过：{string.Join("、", bits)}"
                 + $"（快照合计 {r.BaseTotalM3:N0} → 现盘 {r.NowTotalM3:N0} m³，"
                 + $"净差 {r.NetDeltaM3:+#,0;-#,0;0} m³）。";

        // 净差 ≈ 0 时**尤其**要说清楚 —— 这时最容易被当成"没改"。
        if (Math.Abs(r.NetDeltaM3) <= ToleranceM3)
            s += "净差看着是 0，但那是**一加一减抵掉了**，不是没改。";
        if (r.Vanished.Count > 0)
            s += $"消失的那 {r.Vanished.Count} 条最要命：现盘里它不存在，"
               + "于是它的欠量恒为 0，而它明明是当天排过的活。";
        s += "⇒ 下面的**欠量对着的是现盘计划**，与快照不是同一把尺子。";
        return s;
    }
}
