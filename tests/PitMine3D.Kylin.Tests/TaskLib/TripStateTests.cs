// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/TripStateTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 车次状态机的判据。
///
/// <para>
/// 要挡的是**恒定值死路**：<see cref="DispatchOrderStatus"/> 定义了六个状态，
/// 而在这组判据出现之前全仓只有 <c>DispatchEngine</c> 写过一次 <c>Planned</c>，
/// <see cref="DispatchOrder.ActualLoadHour"/> / <see cref="DispatchOrder.ActualDumpHour"/> 零写入。
/// 现象是三处数字永远不动：看板「车次执行」已卸数恒 0、派车单「状态」列恒"计划"、
/// 三维运输动画拿不到实绩时刻 —— 而界面看着完全正常。
/// </para>
/// <para>
/// 所以每条判据都成对写：<b>标记要真的改，撤销要真的退回去</b>。
/// </para>
/// </summary>
public class TripStateTests
{
    /// <summary>一趟计划 09:12 装、09:26 卸的车次。</summary>
    private static DispatchOrder Trip(double load = 9.2, double dump = 9.44) => new()
    {
        OrderId = "DO-2026-08-11-中-T-01-003",
        TruckId = "T-01", ShovelId = "WK-10", TripNo = 3,
        PlanDate = "2026-08-11 周二", Shift = "中班", WorkZone = "主采面·东",
        MaterialCode = MaterialCatalog.Coal,
        SinkId = "CR-1", SinkName = "1号破碎站", SinkKind = SinkKind.Crusher,
        PlannedLoadHour = load, PlannedDumpHour = dump,
        PayloadT = 100, HaulKm = 2.6,
    };

    // ── T1：标记装车真的改状态并回填时刻（开闸）+ 未标记时是 null 不是 0（关闸）──────
    [Fact]
    public void T1_标记装车()
    {
        var o = Trip();

        // 关闸：没标记过就是"没有这回事"，不能拿 0 冒充"00:00 装的车"
        Assert.Equal(DispatchOrderStatus.Planned, o.Status);
        Assert.Null(o.ActualLoadHour);
        Assert.Null(o.LoadDelayMin);
        Assert.False(o.HasActual);

        // 开闸：09:30 才装上（计划 09:12）
        Assert.True(o.MarkLoaded(9.5));
        Assert.Equal(DispatchOrderStatus.Loading, o.Status);
        Assert.Equal(9.5, o.ActualLoadHour!.Value, 3);
        Assert.Equal(18, o.LoadDelayMin!.Value, 0);      // 迟了 18 min
        Assert.True(o.HasActual);
    }

    // ── T2：标记卸载 —— 没标过装车也能直接卸，且**不倒推**一个装车时刻出来充数 ─────────
    [Fact]
    public void T2_没标装车也能直接卸且不倒推()
    {
        var o = Trip();
        Assert.True(o.MarkDumped(9.6));

        Assert.Equal(DispatchOrderStatus.Dumped, o.Status);
        Assert.Equal(9.6, o.ActualDumpHour!.Value, 3);
        Assert.Equal(9.6, (9.44 + o.DumpDelayMin!.Value / 60), 3);

        // ★ 装车时刻仍然是 null —— 现场没点那一下，就是不知道，不编
        Assert.Null(o.ActualLoadHour);
        Assert.Null(o.LoadDelayMin);
    }

    // ── T3：跨零点 —— 计划 23:40 装、实际 00:10，是迟了 30min 不是提前 23.5h ────────────
    [Fact]
    public void T3_跨零点的延误不回绕()
    {
        var o = Trip(load: 23.667, dump: 23.9);        // 23:40 装 / 23:54 卸

        o.MarkLoaded(0.167);                            // 次日 00:10
        Assert.Equal(30, o.LoadDelayMin!.Value, 0);
        Assert.True(o.ActualLoadHour!.Value > 24, "跨零点的实际时刻要摆到与计划同一天（>24），不能回绕成 0.167");

        o.MarkDumped(0.4);                              // 次日 00:24
        Assert.Equal(30, o.DumpDelayMin!.Value, 0);
    }

    // ── T4：取消是终态 —— 标记被拒；撤销之后又能标（成对，否则"拒绝"可能是恒真的）────────
    [Fact]
    public void T4_取消后拒绝标记撤销后恢复()
    {
        var o = Trip();
        o.Cancel();
        Assert.Equal(DispatchOrderStatus.Cancelled, o.Status);
        Assert.True(o.IsClosed);

        // 开闸：已取消的车次不接受回填
        Assert.False(o.MarkLoaded(9.5));
        Assert.False(o.MarkDumped(9.6));
        Assert.Equal(DispatchOrderStatus.Cancelled, o.Status);
        Assert.Null(o.ActualLoadHour);

        // 关闸：撤销之后必须真的能再标（否则上面那两条 False 可能只是"永远拒绝"）
        o.ClearActual();
        Assert.Equal(DispatchOrderStatus.Planned, o.Status);
        Assert.True(o.MarkLoaded(9.5));
    }

    // ── T5：撤销标记清空两个时刻，并按有无下达单据退回 计划 / 已下达 ─────────────────────
    [Fact]
    public void T5_撤销标记退回正确的态()
    {
        var a = Trip();
        a.MarkLoaded(9.5); a.MarkDumped(9.7);
        a.ClearActual();
        Assert.Null(a.ActualLoadHour);
        Assert.Null(a.ActualDumpHour);
        Assert.Equal(DispatchOrderStatus.Planned, a.Status);      // 没挂实例 → 计划

        var b = Trip();
        b.InstanceId = "inst-abc";
        b.MarkDumped(9.7);
        b.ClearActual();
        Assert.Equal(DispatchOrderStatus.Issued, b.Status);       // 挂了实例 → 退回已下达，不是计划
    }

    // ── T6：MarkIssued 只把计划态往前推一格，不把已经跑起来的车次拉回去 ───────────────────
    [Fact]
    public void T6_下达不回退已经跑起来的车次()
    {
        var a = Trip();
        a.MarkIssued();
        Assert.Equal(DispatchOrderStatus.Issued, a.Status);

        var b = Trip();
        b.MarkDumped(9.7);
        b.MarkIssued();                                            // 再对号一次实例
        Assert.Equal(DispatchOrderStatus.Dumped, b.Status);        // 不许被拉回"已下达"
    }

    // ── T7：看板读的那个数会动 —— 「已卸 / 应卸」不再恒为 0/N ────────────────────────────
    [Fact]
    public void T7_已卸计数随回填变化()
    {
        var trips = Enumerable.Range(1, 5).Select(i => Trip(9 + i * 0.25, 9.2 + i * 0.25)).ToList();

        // 关闸：一趟没标时就是 0（这正是修之前的**恒定**值）
        Assert.Equal(0, trips.Count(o => o.Status == DispatchOrderStatus.Dumped));

        // 开闸：标两趟，计数必须变成 2
        trips[0].MarkDumped(9.3);
        trips[1].MarkDumped(9.6);
        Assert.Equal(2, trips.Count(o => o.Status == DispatchOrderStatus.Dumped));

        // 「应卸」= 计划卸车时刻已过且未取消。计划卸车 9.45 / 9.70 / 9.95 / 10.20 / 10.45，
        // 取 10.5 让五趟都到点，这样"取消"是否真的把那趟从分母里退出去才判得出来。
        double now = 10.5;
        Assert.Equal(5, trips.Count(o => o.PlannedDumpHour <= now && o.Status != DispatchOrderStatus.Cancelled));

        trips[4].Cancel();
        int due = trips.Count(o => o.PlannedDumpHour <= now && o.Status != DispatchOrderStatus.Cancelled);
        Assert.Equal(4, due);          // 取消的那趟不再算"应卸"
    }
}
