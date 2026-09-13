// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/DispatchStateLink.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 与 System.Threading.Tasks.TaskStatus 撞名（隐式 using）

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  单据回灌 —— 把「下达没有」接到共享盘子上
//
//  ══ 这条路以前不存在 ══
//
//  盘子（ProductionPlanContext）每次重建都回灌**实绩**（ApplyPersistedActuals），
//  却从不回灌**单据**。已下达/已撤回只在「任务下达」窗口里就地打在共享任务对象上
//  （TaskDispatchWindow.BuildRows，那行注释自己写着"落盘状态是权威"）——
//  于是这份权威只在**开着那个窗**时存在：六处 Invalidate()（编制配置 / 周计划 /
//  检修 / 爆破 / 班历 / 作业区）任一个就把它抹掉，而抹掉时没有任何一处会报错。
//
//  后果是「任务下达」变成一场仪式：
//    · 执行调度三个窗（看板 / 工序进度 / 实绩录入）按原始计划算 ——
//      撤回的任务照样在看板上"在干"、照样在实绩录入里等人填；
//    · 评价分析三个窗（达成度 / 产量统计 / 质量）的分母是**重算的计划** ——
//      撤回一条任务，分母纹丝不动。
//
//  签发本该是三组的分界线。分界线得落在盘子上，不能落在某个窗口的 OnLoad 里。
//
//  ══ 口径（2026-08-20 用户定）══
//
//    达成度 / 产量统计 / 质量分析的分母 **只算下达过的**。
//    已排未下达的不进分母 —— 但必须在顶栏把条数写出来，否则这个口径会把
//    "根本没下达" 藏成高达成度。报数的地方一律用 <see cref="Stat.Caption"/>。
//
//  ══ 判据 DL1–DL8 ══
//    DL1 已下达 → Dispatch=Issued，且 Status 从 Planned 抬到 Dispatched
//    DL2 下达过又撤回 → Dispatch=Withdrawn（**不是** 塌回 Planned）
//    DL3 没有单据 → Dispatch=NotIssued，Status 不动
//    DL4 对不上号的单据要计数并报出来（任务改过 ⇒ 稳定键变了）
//    DL5 实绩回灌在本步**之后**跑：有实绩的 Done/Partial 覆盖 Dispatched（执行轴更强）
//    DL6 已撤回却有实绩 → 冲突，出 Warn（不静默改数）
//    DL7 Idle 任务不参与（它不是活）
//    DL8 读盘失败只降级不抛：全按未下达，并在文案里说清楚
//    DL9 **运输笔跟随源采装笔**（2026-08-20 用户定：运输随采装连带、排土独立）——
//        它不落自己的单据，挂不上源笔时按未下达处理并计数，绝不默认继承"已下达"
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 从 <c>task_instances/</c> 把单据状态回灌到当日盘子。
/// <b>本类是 <see cref="ProductionTask.Dispatch"/> 的唯一写方</b> —— 窗口不许自己就地打。
/// </summary>
public static class DispatchStateLink
{
    /// <summary>一次回灌的计数。顶栏文案一律从这里取，别各窗各算一遍。</summary>
    public readonly struct Stat
    {
        public int Issued { get; init; }
        public int Withdrawn { get; init; }
        public int NotIssued { get; init; }
        /// <summary>落盘单据里对不上当日任务的条数（任务改过 ⇒ 稳定键变了）。</summary>
        public int Orphan { get; init; }
        /// <summary>读盘失败时的说明；成功为空。</summary>
        public string Failure { get; init; }

        public int Total => Issued + Withdrawn + NotIssued;

        /// <summary>
        /// 来源文案。**未下达的条数必须出现在这里** —— 分母只算下达过的，
        /// 不写出来就等于把"根本没下达"藏成了高达成度。
        /// </summary>
        public string Caption =>
            !string.IsNullOrEmpty(Failure)
                ? $"单据：读盘失败（{Failure}），全部按未下达处理 —— 下面的分母不可信"
          : Total == 0
                ? "单据：本盘没有任务"
          : Issued == 0 && Withdrawn == 0
                ? $"单据：本日 {Total} 条任务**一条都没下达** —— 按「只算下达过的」口径，分母为 0"
                : $"单据：已下达 {Issued} 条"
                  + (Withdrawn > 0 ? $" · 已撤回 {Withdrawn} 条（不进分母）" : "")
                  + (NotIssued > 0 ? $" · **已排未下达 {NotIssued} 条（不进分母）**" : "")
                  + (Orphan > 0 ? $" · {Orphan} 条单据对不上当日任务（任务改过）" : "");
    }

    /// <summary>
    /// 逐条对号回灌。<b>永不抛</b> —— 读盘失败只降级成"全部未下达"并在 <see cref="Stat.Failure"/> 说明。
    /// 必须排在实绩回灌**之前**（DL5：实绩置出的 Done/Partial 是执行轴，比 Dispatched 强）。
    /// </summary>
    public static Stat ApplyPersistedInstances(List<ProductionTask> tasks, string dateLabel)
    {
        if (tasks == null || tasks.Count == 0) return default;

        List<TaskInstance> saved;
        try { saved = TaskPersistence.LoadInstancesOfDay(dateLabel); }
        catch (Exception ex)                                          // DL8
        {
            foreach (var t in tasks) t.Dispatch = DispatchState.NotIssued;
            return new Stat { NotIssued = tasks.Count(t => t.Process != ProcessType.Idle),
                              Failure = ex.GetType().Name };
        }
        return Apply(tasks, dateLabel, saved);
    }

    /// <summary>
    /// 纯逻辑重载：单据由调用方给，<b>不碰磁盘</b>。
    /// 判据走这一个 —— 让 DL1–DL7 与"落盘目录里恰好有什么"无关
    /// （否则判据会因为跑判据的这台机器上有没有真单据而时绿时红）。
    /// </summary>
    public static Stat Apply(List<ProductionTask> tasks, string dateLabel, IEnumerable<TaskInstance> instances)
    {
        if (tasks == null || tasks.Count == 0) return default;
        var saved = instances?.ToList() ?? new List<TaskInstance>();

        var byKey = new Dictionary<string, TaskInstance>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in saved)
            if (!string.IsNullOrWhiteSpace(it.StableKey))
                // 同键多版本：留最后一条（AppendInstances 只增不改，后写的是最新版本）
                byKey[it.StableKey] = it;

        int issued = 0, withdrawn = 0, notIssued = 0;
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tasks)
        {
            if (t.Process == ProcessType.Idle) continue;   // DL7
            if (t.Process == ProcessType.Haul) continue;   // DL9：随源采装笔走，见下面 FollowSource

            string key = TaskKey.Of(t, dateLabel);
            if (!byKey.TryGetValue(key, out var inst))
            {
                t.Dispatch = DispatchState.NotIssued;      // DL3
                notIssued++;
                continue;
            }
            matched.Add(key);

            if (inst.IsIssued)
            {
                t.Dispatch = DispatchState.Issued;         // DL1
                // 执行轴只在还没开工时抬一格；已经有实绩的（Running/Done/Partial）不动。
                if (t.Status == TaskStatus.Planned) t.Status = TaskStatus.Dispatched;
                issued++;
            }
            else if (inst.WasIssued)
            {
                t.Dispatch = DispatchState.Withdrawn;      // DL2
                withdrawn++;
            }
            else
            {
                // 建过单据但从没下达过（草稿态）——与"没有单据"同义
                t.Dispatch = DispatchState.NotIssued;
                notIssued++;
            }
        }

        // DL9 派生笔跟随源笔 —— 必须在主循环**之后**（那时源采装笔的单据轴才定下来）
        var follow = FollowSource(tasks);
        issued += follow.Issued;
        withdrawn += follow.Withdrawn;
        notIssued += follow.NotIssued;

        return new Stat
        {
            Issued = issued,
            Withdrawn = withdrawn,
            NotIssued = notIssued,
            Orphan = byKey.Keys.Count(k => !matched.Contains(k)),   // DL4
            Failure = "",
        };
    }

    /// <summary>
    /// DL9：**运输笔的单据轴跟随它的源采装笔**（口径由用户 2026-08-20 定：运输随采装连带，排土独立）。
    ///
    /// <para><b>为什么不给它自己的单据</b>：运输笔是 <c>HaulDumpDeriver</c> 从采装那一笔推出来的
    /// （HD1），"设备"是车队而不是一台可以签收的车（HD2，定车制）。它不是另外排的一件活，
    /// 而是同一个承诺的下半截 —— 派车单才是它的交付物。给它一份能单独签发的单据，
    /// 立刻就能造出「这一班的料挖了、但没人下达把它拉走」这种自相矛盾的状态，
    /// 而下游（看板、工序进度、达成度）没有任何一处判得出来。</para>
    ///
    /// <para><b>挂法</b>：<see cref="ProductionTask.SourceTaskId"/> → 源采装笔的 <c>Id</c>。
    /// 挂不上的（源笔被筛掉了 / 派生链断了）一律按**未下达**处理并计数，
    /// <b>不默认继承"已下达"</b> —— 猜出来的已下达会让人以为签发过了。</para>
    /// </summary>
    private static (int Issued, int Withdrawn, int NotIssued) FollowSource(List<ProductionTask> tasks)
    {
        var derived = tasks.Where(t => t != null && t.Process == ProcessType.Haul).ToList();
        if (derived.Count == 0) return (0, 0, 0);

        var byId = new Dictionary<string, ProductionTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
            if (t != null && t.Process == ProcessType.Load && !string.IsNullOrWhiteSpace(t.Id))
                byId[t.Id] = t;

        int issued = 0, withdrawn = 0, notIssued = 0;
        foreach (var h in derived)
        {
            h.Dispatch = !string.IsNullOrWhiteSpace(h.SourceTaskId)
                      && byId.TryGetValue(h.SourceTaskId, out var src)
                ? src.Dispatch
                : DispatchState.NotIssued;

            // 执行轴同样只在还没开工时抬一格（与主循环 DL1 同一条）
            if (h.Dispatch == DispatchState.Issued && h.Status == TaskStatus.Planned)
                h.Status = TaskStatus.Dispatched;

            switch (h.Dispatch)
            {
                case DispatchState.Issued: issued++; break;
                case DispatchState.Withdrawn: withdrawn++; break;
                default: notIssued++; break;
            }
        }
        return (issued, withdrawn, notIssued);
    }

    /// <summary>
    /// 这条任务能不能**单独签发**。假 = 它跟着别人走，界面上不该给勾选框
    /// （勾了也没用，反而会让人以为漏签了）。
    /// </summary>
    public static bool IsSelfIssuable(ProductionTask? t)
        => t != null && t.Process != ProcessType.Idle && t.Process != ProcessType.Haul;

    /// <summary>运输笔的单据来源说明（任务下达表的状态列走它，不另写一份文案）。</summary>
    public static string FollowerCaption(ProductionTask? t, IReadOnlyList<ProductionTask>? all)
    {
        if (t == null || t.Process != ProcessType.Haul) return "";
        var src = all?.FirstOrDefault(x => x != null && x.Process == ProcessType.Load
                                        && string.Equals(x.Id, t.SourceTaskId, StringComparison.OrdinalIgnoreCase));
        string who = src != null
            ? $"{src.Group.MainEquipment}·{src.WorkZone}"
            : (string.IsNullOrWhiteSpace(t.SourceTaskId) ? "（源采装笔已不在本盘）" : t.SourceTaskId);
        return t.Dispatch switch
        {
            DispatchState.Issued => $"✓ 随采装下达（{who}）",
            DispatchState.Withdrawn => $"随采装撤回（{who}）",
            _ => $"待下达 · 随采装 {who}",
        };
    }

    /// <summary>本条告警的稳定前缀 —— 重跑时先按它清旧的，否则每签发一次就多一条重复告警。</summary>
    private const string ConflictMark = "【撤回却有实绩】";

    /// <summary>
    /// DL6：已撤回却有实绩 —— 这是真冲突，报出来，<b>不替谁做主改数</b>。
    /// 必须在实绩回灌**之后**调（那时才知道谁有实绩）。
    /// </summary>
    public static void CheckConflicts(List<ProductionTask> tasks, ExploderResult result)
    {
        if (tasks == null || result == null) return;

        // 幂等：RefreshDispatch 每次下达/撤回都会调到这里，不清旧的就会越堆越多。
        result.Violations.RemoveAll(v => v?.Message != null && v.Message.StartsWith(ConflictMark, StringComparison.Ordinal));

        var bad = tasks.Where(t => t.Process != ProcessType.Idle
                                && t.IsWithdrawn
                                && t.ActualVolumeM3 > 1e-6).ToList();
        if (bad.Count == 0) return;

        result.Violations.Add(new PlanViolation
        {
            Severity = ViolationSeverity.Warn,
            Code = ViolationCodes.DispatchIssued,
            Message = ConflictMark + $"**{bad.Count} 条任务已撤回，却录了实绩** —— "
                    + string.Join("、", bad.Take(4).Select(t => $"{t.Group.MainEquipment}·{t.WorkZone}"))
                    + (bad.Count > 4 ? " 等" : "")
                    + "。按「只算下达过的」口径，这些量**不进达成度分母**，但活确实干了。"
                    + "两种可能：撤回撤晚了（人已经开工），或实绩录到了错的任务上。"
                    + "请到「任务下达」核对单据流水 —— 这里不替任何一方改数。",
        });
    }

    /// <summary>
    /// <b>实绩侧</b>口径（用户 2026-08-20 定：**只算下达过的**）。
    /// 达成率的分子分母、质量达标率的分母一律走这一个 judge，别各窗写各的 Where。
    ///
    /// <para><b>它只管实绩侧</b>（EV9，2026-08-20 补）：计划量、计划剥采比、配煤目标、
    /// 任务清单这些**计划侧**的数走 <see cref="CountsForPlan"/>。
    /// 原先三个评价窗把这一个口径套在整窗上，于是"一条都没下达"会让
    /// **已经排好的 225 条任务、16 万方计划量**在界面上一并消失 ——
    /// 而那些数与下达与否毫无关系，它们是排出来的，就在盘子上。
    /// 空窗只说明没人签发，却长得像"没排计划"。</para>
    /// </summary>
    public static bool CountsForEvaluation(ProductionTask t)
        => t != null && t.Process != ProcessType.Idle && t.IsIssued;

    /// <summary>
    /// <b>计划侧</b>口径（EV9）：盘子上"还要干的活"—— 未下达的算，已撤回的不算。
    /// <para>与 <see cref="IsLive"/> 同判据，另起一个名字是因为**用途不同**：
    /// 那个是执行期看板用的，这个是评价窗的计划侧分母。判据合并成一个函数，
    /// 名字分开，改口径时才不会以为动的只是看板。</para>
    /// </summary>
    public static bool CountsForPlan(ProductionTask t) => IsLive(t);

    /// <summary>
    /// 评价三窗空态时的「为什么空」。
    ///
    /// <para>分母口径改成「只算下达过的」之后，这几个窗多出**第三种空**：
    /// 排了、但一条都没下达。而原来的空态文案写的是"计划还没排出来" ——
    /// 照着它去查会查错方向（跑去看作业面台账，而实际上该去签发）。</para>
    /// </summary>
    /// <param name="stat">本盘单据计数。</param>
    /// <param name="fallback">不是这种空时照原样说的那句话。</param>
    public static string EmptyScopeReason(Stat stat, string fallback)
        => stat.Total > 0 && stat.Issued == 0
            ? $"**本日排了 {stat.Total} 条任务，但一条都没下达** —— "
              + "本窗分母只算下达过的，所以这里是空的（不是没排计划）。"
              + "补法：到「任务下达」签发本班任务。"
            : fallback;

    /// <summary>
    /// 执行期口径：撤回的不再是"要干的活" —— 看板 / 工序进度 / 实绩录入用它。
    /// <b>与分母口径不同</b>：未下达的活还在盘子上（调度看得见它排了），只是不进评价分母。
    /// </summary>
    public static bool IsLive(ProductionTask t)
        => t != null && t.Process != ProcessType.Idle && !t.IsWithdrawn;
}
