// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/DispatchStateLinkTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// DL 组：**单据回灌**（<see cref="DispatchStateLink"/>）。
///
/// <para>被判的那条死路：盘子每次重建都回灌实绩，却从不回灌单据 ——
/// 「已下达/已撤回」只在「任务下达」窗口的 <c>BuildRows</c> 里就地打在共享任务对象上，
/// 于是任一处 <c>Invalidate()</c> 就把它抹掉，而执行调度、评价分析两组读到的
/// 永远是**原始计划**：撤回一条任务，看板照样显示它在干、达成度分母纹丝不动。</para>
///
/// <para>每条都按 F0 成对写：**开闸要变、关闸必须真的变回去**。
/// 尤其 DL2 —— 旧实现撤回时写回 <c>Planned</c>，
/// 于是"下达过又撤回"与"从没下达过"在盘子上长得一模一样，
/// 任何按 <c>Status</c> 判的下游都分不出来。这一条正是那个还原自检。</para>
/// </summary>
public class DispatchStateLinkTests
{
    private const string Date = "2026-08-20 周四";

    private static ProductionTask T(string equip, string zone, ProcessType p = ProcessType.Load,
                                    string shift = "早班", double target = 1000)
        => new()
        {
            Id = $"{equip}-{zone}",
            Process = p,
            Shift = shift,
            WorkZone = zone,
            TargetVolumeM3 = target,
            Group = new EquipmentGroup { MainEquipment = equip },
            Status = TaskStatus.Planned,
        };

    /// <summary>照 <see cref="TaskKey"/> 的五元组给这条任务造一张单据。</summary>
    private static TaskInstance Inst(ProductionTask t, bool issued, bool withdrawn = false)
    {
        var it = new TaskInstance
        {
            StableKey = TaskKey.Of(t, Date),
            TaskId = t.Id,
            PlanDate = Date,
            Shift = t.Shift,
        };
        if (issued) { it.IssuedBy = "调度张"; it.IssuedAt = new DateTime(2026, 8, 20, 7, 30, 0); }
        if (withdrawn) it.Withdraw("调度李", new DateTime(2026, 8, 20, 9, 15, 0));
        return it;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DL1–DL3：三态各自落对
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DL1_已下达的任务回灌成Issued并把执行轴抬到已下达()
    {
        var t = T("WK-10", "采场1·岩1308");
        var stat = DispatchStateLink.Apply(new List<ProductionTask> { t }, Date,
                                           new[] { Inst(t, issued: true) });

        Assert.Equal(DispatchState.Issued, t.Dispatch);
        Assert.True(t.IsIssued);
        Assert.Equal(TaskStatus.Dispatched, t.Status);
        Assert.Equal(1, stat.Issued);
        Assert.Equal(0, stat.NotIssued);
    }

    [Fact]
    public void DL2_下达过又撤回落成Withdrawn而不是塌回Planned()
    {
        var t = T("WK-10", "采场1·岩1308");
        var stat = DispatchStateLink.Apply(new List<ProductionTask> { t }, Date,
                                           new[] { Inst(t, issued: true, withdrawn: true) });

        // ★ 这一条是整组的核心：旧实现在这里写 Status = Planned，
        //   于是它与"从没下达过"（DL3）在盘子上完全相同 —— 下游没有任何一处分得出来。
        Assert.Equal(DispatchState.Withdrawn, t.Dispatch);
        Assert.True(t.IsWithdrawn);
        Assert.Equal(1, stat.Withdrawn);
        Assert.Equal(0, stat.NotIssued);

        // 与 DL3 必须可区分（还原自检：若撤回态塌回 NotIssued，下面这句就会红）
        var never = T("WK-20", "采场2·煤1296");
        DispatchStateLink.Apply(new List<ProductionTask> { never }, Date, Array.Empty<TaskInstance>());
        Assert.NotEqual(never.Dispatch, t.Dispatch);
    }

    [Fact]
    public void DL3_没有单据的任务是未下达且执行轴不动()
    {
        var t = T("WK-30", "采场3·岩1320");
        var stat = DispatchStateLink.Apply(new List<ProductionTask> { t }, Date, Array.Empty<TaskInstance>());

        Assert.Equal(DispatchState.NotIssued, t.Dispatch);
        Assert.Equal(TaskStatus.Planned, t.Status);   // 没下达 ≠ 有异常，状态不该被动
        Assert.Equal(1, stat.NotIssued);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DL4：对不上号的单据要报出来
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DL4_单据对不上当日任务时计入Orphan并写进文案()
    {
        var t = T("WK-10", "采场1·岩1308");
        var ghost = T("WK-99", "已经不存在的面");           // 任务改过 ⇒ 稳定键变了

        var stat = DispatchStateLink.Apply(new List<ProductionTask> { t }, Date,
                                           new[] { Inst(t, issued: true), Inst(ghost, issued: true) });

        Assert.Equal(1, stat.Issued);
        Assert.Equal(1, stat.Orphan);
        Assert.Contains("对不上当日任务", stat.Caption);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DL5：与实绩回灌的先后 —— 执行轴比单据轴强
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DL5_已经有实绩的任务不会被单据回灌打回已下达()
    {
        var t = T("WK-10", "采场1·岩1308");
        t.ActualVolumeM3 = 1000;
        t.Status = TaskStatus.Done;                      // 实绩已把执行轴推到完成

        DispatchStateLink.Apply(new List<ProductionTask> { t }, Date, new[] { Inst(t, issued: true) });

        Assert.Equal(DispatchState.Issued, t.Dispatch);  // 单据轴照落
        Assert.Equal(TaskStatus.Done, t.Status);         // 执行轴不许被降级
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DL6：撤回却有实绩 —— 报冲突，且重复调用不许堆告警
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DL6_撤回却录了实绩要报冲突且幂等()
    {
        var t = T("WK-10", "采场1·岩1308");
        var r = new ExploderResult();
        r.Tasks.Add(t);

        DispatchStateLink.Apply(r.Tasks, Date, new[] { Inst(t, issued: true, withdrawn: true) });
        t.ActualVolumeM3 = 800;                          // 撤回了，可活确实干了

        DispatchStateLink.CheckConflicts(r.Tasks, r);
        int after1 = r.Violations.Count(v => v.Message.Contains("已撤回，却录了实绩"));
        Assert.Equal(1, after1);

        // RefreshDispatch 每次下达/撤回都会调到 CheckConflicts —— 不幂等就会越堆越多
        DispatchStateLink.CheckConflicts(r.Tasks, r);
        DispatchStateLink.CheckConflicts(r.Tasks, r);
        Assert.Equal(1, r.Violations.Count(v => v.Message.Contains("已撤回，却录了实绩")));

        // 关闸自检：实绩撤掉之后这条告警必须真的消失，不能赖着
        t.ActualVolumeM3 = 0;
        DispatchStateLink.CheckConflicts(r.Tasks, r);
        Assert.DoesNotContain(r.Violations, v => v.Message.Contains("已撤回，却录了实绩"));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DL7：Idle 不是活
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DL7_Idle任务不参与回灌也不计数()
    {
        var idle = T("WK-40", "待命", ProcessType.Idle, target: 0);
        var stat = DispatchStateLink.Apply(new List<ProductionTask> { idle }, Date, Array.Empty<TaskInstance>());

        Assert.Equal(0, stat.Total);
        Assert.False(DispatchStateLink.CountsForEvaluation(idle));
        Assert.False(DispatchStateLink.IsLive(idle));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  两个口径本身
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 分母口径（用户 2026-08-20 定）：**只算下达过的**。
    /// 未下达与已撤回都不进 —— 这两者进不进是本次改动最容易被改回去的一处。
    /// </summary>
    [Fact]
    public void DL9_评价分母只收已下达()
    {
        var issued = T("WK-10", "A");
        var notYet = T("WK-20", "B");
        var pulled = T("WK-30", "C");
        var all = new List<ProductionTask> { issued, notYet, pulled };

        DispatchStateLink.Apply(all, Date, new[]
        {
            Inst(issued, issued: true),
            Inst(pulled, issued: true, withdrawn: true),
        });

        Assert.Equal(new[] { "A" },
                     all.Where(DispatchStateLink.CountsForEvaluation).Select(t => t.WorkZone).ToArray());
    }

    /// <summary>
    /// 执行期口径与分母口径**不是同一个**：撤回的不上看板，
    /// 但未下达的仍要留着 —— 调度得看得见计划排了什么。
    /// 两个口径写成同一个，是这块最容易犯的错。
    /// </summary>
    [Fact]
    public void DL10_执行期口径只剔撤回不剔未下达()
    {
        var issued = T("WK-10", "A");
        var notYet = T("WK-20", "B");
        var pulled = T("WK-30", "C");
        var all = new List<ProductionTask> { issued, notYet, pulled };

        DispatchStateLink.Apply(all, Date, new[]
        {
            Inst(issued, issued: true),
            Inst(pulled, issued: true, withdrawn: true),
        });

        Assert.Equal(new[] { "A", "B" },
                     all.Where(DispatchStateLink.IsLive).Select(t => t.WorkZone).ToArray());
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  文案：未下达的条数不许被藏起来
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 「只算下达过的」这个口径有一个自带的坑：一条都没下达时，分母为 0，
    /// 各窗会显示成"没有数据"或"达成度 —"，看着像**没排计划**。
    /// 所以条数必须出现在文案里，空态也要说清是哪一种空。
    /// </summary>
    [Fact]
    public void DL11_未下达条数必须出现在文案与空态里()
    {
        var a = T("WK-10", "A");
        var b = T("WK-20", "B");
        var all = new List<ProductionTask> { a, b };

        var stat = DispatchStateLink.Apply(all, Date, new[] { Inst(a, issued: true) });
        Assert.Contains("未下达 1", stat.Caption);

        // 一条都没下达：空态不许再说"计划还没排出来"
        var none = DispatchStateLink.Apply(new List<ProductionTask> { T("WK-30", "C") }, Date,
                                           Array.Empty<TaskInstance>());
        string why = DispatchStateLink.EmptyScopeReason(none, "**本日没有任务** —— 是计划还没排出来。");
        Assert.Contains("一条都没下达", why);
        Assert.DoesNotContain("计划还没排出来", why);

        // 关闸自检：确实有下达时，回落原来那句
        var some = DispatchStateLink.Apply(new List<ProductionTask> { a }, Date, new[] { Inst(a, issued: true) });
        Assert.Equal("**本日没有任务** —— 是计划还没排出来。",
                     DispatchStateLink.EmptyScopeReason(some, "**本日没有任务** —— 是计划还没排出来。"));
    }
}
