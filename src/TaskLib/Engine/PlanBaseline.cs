// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/PlanBaseline.cs（逐行对应；仅命名空间/依赖适配）
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  「当时排的是什么」—— 达成度与动态调整的**基准**。
//
//  ══ 为什么不能拿现算的计划当基准 ══
//  `ProductionPlanContext.Config()` **每次调用都重新装配**（台账随时被别的窗口改，
//  缓存会让"改完看不见"重新长出来 —— 那一条本身是对的）。
//  可这意味着「计划量」是个**活的数**：
//    · 早上排了 3000，中午有人把单元量改成 2500，下午再看达成度 ——
//      分母悄悄从 3000 变成 2500，**达成率凭空涨了 20%**，而没有任何东西报错；
//    · 昨天的达成度今天再打开，也已经不是昨天看到的那个数了。
//  达成度评的是「照当时那份计划干得怎么样」，所以分母必须是**当时那份**，
//  而不是"现在重算一遍会是多少"。
//
//  ══ 基准从哪来 ══
//  「生产任务编制」的**保存本日计划**会把 `ExploderResult` 落成快照
//  （`task_snapshots/任务计划_{日期}.json`）。有快照就用快照；
//  **没有快照就明说没有**，不拿现算的顶替 —— 顶替出来的达成度看着完全正常。
//
//  ══ 一条纪律 ══
//  **基准与实绩必须按同一把钥匙对号**：`TaskKey.Compose(日期,班次,主设备,工序,作业区)`。
//  按下标或按名字对，会在"今天多排了一台铲"那天整体错位，而每一行看着都对。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>基准是哪来的。</summary>
public enum BaselineSource
{
    /// <summary>没有基准 —— 那一天没保存过计划。</summary>
    None = 0,
    /// <summary>当日保存的计划快照（<b>唯一正确的基准</b>）。</summary>
    Snapshot = 1,
    /// <summary>现算的计划 —— <b>不是基准</b>，只是"现在重算一遍会是多少"。</summary>
    Live = 2,
}

/// <summary>一次取基准的结果。</summary>
public sealed class PlanBaselineResult
{
    public BaselineSource Source;
    public List<ProductionTask> Tasks = new();
    /// <summary>基准的说明（界面直接显示）。</summary>
    public string Label = "";
    /// <summary>快照是什么时候存的（<see cref="BaselineSource.Snapshot"/> 时有值）。</summary>
    public DateTime? SavedAt;

    /// <summary>这是**真基准**（快照），达成度算得准。</summary>
    public bool IsBaseline => Source == BaselineSource.Snapshot;
}

/// <summary>取「当时排的是什么」。永不抛。</summary>
public static class PlanBaseline
{
    /// <summary>
    /// 取某一天的计划基准。
    /// </summary>
    /// <param name="dateLabel">日期标签（与快照文件名同一口径，如 <c>2026-08-18 周二</c>）。</param>
    /// <param name="fallbackToLive">
    /// 取不到快照时是否退回现算的计划。
    /// <para><b>缺省 false</b>：达成度宁可说"没有基准"，也不要一个会随台账漂移的分母。</para>
    /// </param>
    public static PlanBaselineResult For(string? dateLabel, bool fallbackToLive = false)
    {
        var res = new PlanBaselineResult();
        string key = TaskKey.DateTag(dateLabel ?? "");

        try
        {
            var hit = FindSnapshot(key);
            if (hit != null)
            {
                var snap = TaskPersistence.Load(hit);
                if (snap != null && snap.Tasks.Count > 0)
                {
                    res.Source = BaselineSource.Snapshot;
                    res.Tasks = snap.Tasks;
                    try { res.SavedAt = File.GetLastWriteTime(hit); } catch { }
                    res.Label = $"基准：**当日保存的计划快照**"
                              + (res.SavedAt.HasValue ? $"（存于 {res.SavedAt:MM-dd HH:mm}）" : "")
                              + $" · {snap.Tasks.Count} 条";
                    return res;
                }
            }
        }
        catch { }

        if (!fallbackToLive)
        {
            res.Source = BaselineSource.None;
            res.Label = "◆ **这一天没有计划快照 —— 达成度算不了。**"
                      + "达成度评的是「照**当时那份**计划干得怎么样」，"
                      + "而现算的计划是个活的数：早上排 3000、中午有人把单元量改成 2500，"
                      + "下午再看分母就成了 2500，**达成率凭空涨 20%**，没有任何东西报错。"
                      + "补法：到「生产任务编制」点一次「保存本日计划」，那一天就有基准了。";
            return res;
        }

        try
        {
            res.Tasks = ProductionPlanContext.Day();
            res.Source = BaselineSource.Live;
            res.Label = "⚠ 用的是**现算的计划**，不是当日基准 —— "
                      + "台账一改这个分母就跟着变，昨天的达成度今天再看已经不是同一个数。"
                      + "到「生产任务编制」保存一次本日计划即可有真基准。";
        }
        catch (Exception ex) { res.Label = $"计划取不到（{ex.GetType().Name}）。"; }
        return res;
    }

    /// <summary>
    /// 基准 × 实绩 → 逐条对账。<b>按稳定键对号</b>（不按下标、不按名字）。
    /// </summary>
    /// <param name="baseline">当时那份计划。</param>
    /// <param name="actual">现在这份（带实绩回灌）。</param>
    public static List<(ProductionTask Plan, ProductionTask? Now)> Reconcile(
        IReadOnlyList<ProductionTask>? baseline, IReadOnlyList<ProductionTask>? actual)
    {
        var list = new List<(ProductionTask, ProductionTask?)>();
        var now = new Dictionary<string, ProductionTask>(StringComparer.Ordinal);
        foreach (var t in actual ?? Array.Empty<ProductionTask>())
            if (!string.IsNullOrWhiteSpace(t.Id)) now[t.Id] = t;

        foreach (var b in baseline ?? Array.Empty<ProductionTask>())
            list.Add((b, !string.IsNullOrWhiteSpace(b.Id) && now.TryGetValue(b.Id, out var m) ? m : null));
        return list;
    }

    /// <summary>
    /// 基准里有、现在没有的那些 —— <b>「计划了却消失了」</b>。
    /// <para>这些最容易被漏掉：现算的计划里它不存在，于是任何"遍历当前任务"的统计都碰不到它，
    /// 而它明明是当天排过的活。</para>
    /// </summary>
    public static List<ProductionTask> Vanished(
        IReadOnlyList<ProductionTask>? baseline, IReadOnlyList<ProductionTask>? actual)
        => Reconcile(baseline, actual).Where(x => x.Now == null).Select(x => x.Plan).ToList();

    private static string? FindSnapshot(string dateTag)
    {
        if (string.IsNullOrWhiteSpace(dateTag)) return null;
        foreach (var f in TaskPersistence.ListSnapshots())
        {
            string name = Path.GetFileNameWithoutExtension(f) ?? "";
            // 文件名形如「任务计划_2026-08-18 周二」—— 取第一个下划线之后的整串再比日期段
            int i = name.IndexOf('_');
            string tail = i >= 0 ? name[(i + 1)..] : name;
            if (TaskKey.DateTag(tail).Equals(dateTag, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }
}
