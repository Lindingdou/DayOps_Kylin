// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/LedgerSemanticsTests.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using PitMine3D.Kylin.TaskLib.Domain;
using Xunit;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 单据语义的判据 —— 这两条都是"界面看着对、账上是错的"那一类。
///
/// <list type="bullet">
/// <item><b>W 组 撤回不许抹痕</b>：原先撤回把 <c>IssuedBy/IssuedAt</c> 清空，
///   "谁在几点下达过"就此消失，而整套设计通篇宣称"单据流水只增不改"。</item>
/// <item><b>M 组 计划检修 ≠ 非计划故障</b>：<see cref="IncompleteReason"/> 里两者一直是分开的，
///   <see cref="FaultEvent"/> 原先不分，检修停机被汇总进"故障工时"——
///   设备可用率、MTTR、达成度归因全被按计划保养的时间污染。</item>
/// </list>
/// </summary>
public class LedgerSemanticsTests
{
    private static TaskInstance Issued(out DateTime at)
    {
        at = new DateTime(2026, 8, 11, 8, 5, 0);
        return new TaskInstance
        {
            TaskId = "D0811-WK10-中", StableKey = "TK-2026-08-11-中-abc123",
            PlanDate = "2026-08-11 周二", Shift = "中班", Version = 2,
            IssuedBy = "张调度", IssuedAt = at, Status = TaskStatus.Dispatched,
        };
    }

    // ── W1：撤回后"现在不算下达"，但"曾经下达过"和下达时刻必须都还在 ────────────────
    [Fact]
    public void W1_撤回留痕不抹痕()
    {
        var inst = Issued(out var issuedAt);
        inst.AckedBy = "李班长";
        inst.AckedAt = issuedAt.AddMinutes(10);

        Assert.True(inst.IsIssued);

        var wAt = issuedAt.AddHours(1);
        inst.Withdraw("王调度", wAt);

        Assert.False(inst.IsIssued);                 // 现在不算数
        Assert.True(inst.WasIssued);                 // 但确实下达过
        Assert.Equal(issuedAt, inst.IssuedAt);       // ★ 下达时刻原样保留（原先这里被清成 null）
        Assert.Equal("张调度", inst.IssuedBy);
        Assert.Equal("王调度", inst.WithdrawnBy);
        Assert.Equal(wAt, inst.WithdrawnAt);
        Assert.Equal(TaskStatus.Planned, inst.Status);

        // 撤回 ⇒ 班组的确认作废（不能留着一条"已确认"指向一份被撤回的单据）
        Assert.Equal("", inst.AckedBy);
        Assert.Null(inst.AckedAt);

        // 落款文案要把两头都说出来，否则界面上只剩"待下达"，看不出它被撤回过
        Assert.Contains("撤回", inst.IssueCaption);
        Assert.Contains("张调度", inst.IssueCaption);
    }

    // ── W2：从没下达过的实例，两个都是 false（关闸，否则 W1 可能恒真）─────────────────
    [Fact]
    public void W2_没下达过就是没下达过()
    {
        var fresh = new TaskInstance { TaskId = "x", StableKey = "k" };
        Assert.False(fresh.IsIssued);
        Assert.False(fresh.WasIssued);
        Assert.Equal("待下达", fresh.IssueCaption);
    }

    // ── M1：计划检修与非计划故障分得开 ──────────────────────────────────────────
    [Fact]
    public void M1_计划检修与故障分开()
    {
        var fault = new FaultEvent { EquipId = "WK-10", StartHour = 10, EstimatedHours = 2, IsPlanned = false };
        var maint = new FaultEvent { EquipId = "WK-01", StartHour = 10, EstimatedHours = 2, IsPlanned = true };

        Assert.False(fault.IsPlanned);
        Assert.True(maint.IsPlanned);

        // 两者的时长口径一样（都是停机），区别在于**记到哪个账上**——
        // 这一条只钉"分得开"，具体分账在实绩录入侧。
        Assert.Equal(fault.DurationHours, maint.DurationHours, 3);
    }

    // ── M2：响应/修复时长 —— 没开修就是 null，不拿 0 冒充"立刻就修了" ────────────────
    [Fact]
    public void M2_响应与修复时长()
    {
        var t0 = new DateTime(2026, 8, 11, 10, 0, 0);
        var f = new FaultEvent { EquipId = "WK-10", StartHour = 10, EstimatedHours = 3, ReportedAt = t0 };

        // 关闸：只报修、没开修 ⇒ 两个都是 null
        Assert.Null(f.ResponseHours);
        Assert.Null(f.RepairHours);
        Assert.True(f.RepairStartHour < 0, "没开修时 RepairStartHour 必须是负值——0 点是合法时刻，不能拿它当哨兵");

        // 开闸：40min 后到场开修
        f.BeginRepair(10.667, t0.AddMinutes(40));
        Assert.Equal(FaultStatus.Repairing, f.Status);
        Assert.Equal(0.667, f.ResponseHours!.Value, 2);
        Assert.Null(f.RepairHours);            // 还没修好

        // 再 1.5h 复机
        f.Resume(t0.AddMinutes(40).AddMinutes(90));
        Assert.Equal(1.5, f.RepairHours!.Value, 2);
        Assert.Equal(2.167, f.DurationHours, 2);   // 总停机 = 报修→复机
    }
}
