// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ChainHandoffTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 业务链各环的**交接契约**。
///
/// <para>
/// 前面几组判据钉的都是单环内部的对错（闸门、状态机、撤回语义…），
/// 而这一轮改动的实质是把断掉的环接起来：派工→派车单/任务书/看板、车次→回执流水、
/// 车次→实绩对账。这些地方最容易出的错不是算错，而是<b>交接时少带了一样东西</b>——
/// 少带一个键，下游就永远筛不到，界面上却一切正常（同 [[always-firing-warning-is-a-dead-path]]）。
/// </para>
/// <para>
/// 故本组只判"带没带全、对不对得上号"，不重复判各环内部的算法。
/// </para>
/// </summary>
public class ChainHandoffTests
{
    // ═════════════════════════════════════════════════════════════════════════
    //  L1 车次 → 回执流水
    // ═════════════════════════════════════════════════════════════════════════

    private static DispatchOrder Trip() => new()
    {
        OrderId = "DO-2026-08-11-中-T-01-003",
        TruckId = "T-01", ShovelId = "WK-10", TripNo = 3,
        TaskId = "D0811-WK10-中", StableKey = "TK-2026-08-11-中-abc123",
        InstanceId = "inst-xyz",
        PlanDate = "2026-08-11 周二", Shift = "中班",
        SinkId = "CR-1", SinkName = "1号破碎站",
        PlannedLoadHour = 9.2, PlannedDumpHour = 9.44,
        PayloadT = 100, PayloadInSituM3 = 74,
    };

    /// <summary>
    /// 车次回执必须把**三个键**都带上：任务下达的流水面板选中任务时按 StableKey 筛、
    /// 不选时按 Shift 筛；OrderId 是这条回执指向哪一趟的唯一线索。
    /// 少带任何一个，这条回执在界面上就永远看不到——而它确实写进了盘子。
    /// </summary>
    [Fact]
    public void L1_车次回执带全三个键()
    {
        var o = Trip();
        var r = DispatchReceipt.For(o, ReceiptKind.Finish, "张调度", "标记卸载 T-01 第3趟");

        Assert.Equal(o.StableKey, r.StableKey);     // 按任务筛得到
        Assert.Equal(o.Shift, r.Shift);             // 按班次筛得到
        Assert.Equal(o.OrderId, r.OrderId);         // 指得回那一趟
        Assert.Equal(o.TaskId, r.TaskId);
        Assert.Equal(o.InstanceId, r.InstanceId);
        Assert.Equal(ReceiptKind.Finish, r.Kind);
        Assert.False(string.IsNullOrWhiteSpace(r.By));
    }

    /// <summary>关闸：任务级回执不该带 OrderId（它不指向任何一趟），否则按趟追溯会串。</summary>
    [Fact]
    public void L1b_任务级回执不带车次号()
    {
        var inst = new TaskInstance
        {
            TaskId = "D0811-WK10-中", StableKey = "TK-2026-08-11-中-abc123",
            PlanDate = "2026-08-11 周二", Shift = "中班",
        };
        var r = DispatchReceipt.For(inst, ReceiptKind.Issue, "张调度", "下达");

        Assert.Equal("", r.OrderId);
        Assert.Equal(inst.StableKey, r.StableKey);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  L2 派工 → 派车单 / 任务书 / 看板
    // ═════════════════════════════════════════════════════════════════════════

    private static CrewAssignment Assigned(bool explicitPairs) => new()
    {
        PlanDate = "2026-08-11 周二", Shift = "中班",
        MainEquipment = "WK-10",
        Operator = "张建国", OperatorId = "P-001",
        Drivers = new List<string> { "孙宝山", "吴建军" },
        TruckDrivers = explicitPairs
            ? new List<TruckDriver>
              {
                  new() { TruckId = "T-01", Name = "孙宝山", PersonId = "P-101" },
                  new() { TruckId = "T-02", Name = "吴建军", PersonId = "P-102" },
              }
            : new List<TruckDriver>(),
        CertNote = "✓ 持证齐全", AttendNote = "全勤",
    };

    // ── L2：显式配对时，车号直接取得到司机 ──────────────────────────────────────
    [Fact]
    public void L2_按车号取司机()
    {
        var cs = new CrewShift(new[] { Assigned(explicitPairs: true) });

        Assert.False(cs.IsEmpty);
        Assert.True(cs.FromExplicitPairing);
        Assert.Equal("孙宝山", cs.DriverOf("T-01"));
        Assert.Equal("吴建军", cs.DriverOf("t-02"));      // 车号大小写不敏感
        Assert.Equal("张建国", cs.OperatorOf("WK-10"));
        Assert.Equal("✓ 持证齐全", cs.CertNoteOf("WK-10"));
        Assert.Equal("全勤", cs.AttendNoteOf("WK-10"));
        Assert.Contains("张建国", cs.CaptionOf("WK-10"));
        Assert.Contains("孙宝山", cs.CaptionOf("WK-10"));
    }

    // ── L2b：旧档案只有名字列表 ⇒ 按配车顺序兜底，且**如实标注是推定的** ────────────
    [Fact]
    public void L2b_旧档案按顺序兜底并标注()
    {
        var cs = new CrewShift(new[] { Assigned(explicitPairs: false) });

        Assert.False(cs.FromExplicitPairing, "按下标推出来的配对必须标出来，不能冒充派工时定的");

        var trucks = new[] { "T-01", "T-02" };
        Assert.Equal("孙宝山", cs.DriverOf("T-01", "WK-10", trucks));
        Assert.Equal("吴建军", cs.DriverOf("T-02", "WK-10", trucks));

        // 不给编组上下文就取不到 —— 宁可空着，也不要瞎猜一个名字挂到别的车上
        Assert.Equal("", cs.DriverOf("T-01"));
    }

    // ── L3：没派工就是没派工，不许回落成假名字 ────────────────────────────────────
    [Fact]
    public void L3_没派工返回空不编名字()
    {
        var empty = new CrewShift(Array.Empty<CrewAssignment>());

        Assert.True(empty.IsEmpty);
        Assert.Equal("", empty.OperatorOf("WK-10"));
        Assert.Equal("", empty.DriverOf("T-01"));
        Assert.Equal("", empty.CaptionOf("WK-10"));      // 任务书据此显示"—"
        Assert.Empty(empty.DriversOf("WK-10"));

        // 派了工但这台设备不在其中 —— 同样是空，不能把别的编组的人拿过来
        var other = new CrewShift(new[] { Assigned(true) });
        Assert.Equal("", other.OperatorOf("WK-01"));
        Assert.Equal("", other.DriverOf("T-99"));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  L4 车次 → 实绩对账
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 对账的口径是<b>实方</b>：吨与松方都不是作业量口径，拿它们跟实绩 m³ 比就是拿两把尺子量。
    /// 而且只有**已卸**的车次算数——装了没卸的料还在车上。
    /// </summary>
    [Fact]
    public void L4_对账只数已卸车次的实方()
    {
        var trips = Enumerable.Range(0, 4).Select(_ => Trip()).ToList();
        trips[0].MarkDumped(9.5);
        trips[1].MarkDumped(9.8);
        trips[2].MarkLoaded(10.0);        // 装了没卸
        trips[3].Cancel();

        double byTrip = trips.Where(o => o.Status == DispatchOrderStatus.Dumped).Sum(o => o.PayloadInSituM3);
        Assert.Equal(148, byTrip, 1);      // 74 × 2

        // 关闸：一趟没标记时对账口径是 0（这正是修之前的恒定值），而不是把计划量当实绩
        var untouched = Enumerable.Range(0, 4).Select(_ => Trip()).ToList();
        Assert.Equal(0, untouched.Where(o => o.Status == DispatchOrderStatus.Dumped).Sum(o => o.PayloadInSituM3), 1);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  L5 实绩 → 质量评价
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 三项煤质各自独立：只测了灰分，热值/硫分保持 null，
    /// <b>不能写成 0</b> —— 0 热值会让 <see cref="CoalQuality.MeetsTarget"/> 判定全线不达标。
    /// </summary>
    [Fact]
    public void L5_煤质三项互不牵连()
    {
        var rec = new ActualRecord { TaskId = "t", AshPct = 11.2 };

        Assert.Equal(11.2, rec.AshPct!.Value, 2);
        Assert.Null(rec.CalorificMJkg);
        Assert.Null(rec.SulfurPct);

        // 回灌时只写测了的那项：目标 灰≤12 热≥22 硫≤0.6，只测灰分不该把这条判成不达标
        var target = new CoalQuality { AshPct = 12, CalorificMJkg = 22, SulfurPct = 0.6 };
        var actual = new CoalQuality { CalorificMJkg = 22, SulfurPct = 0.6 };   // 未测项先按目标占位
        if (rec.AshPct is > 0) actual.AshPct = rec.AshPct.Value;

        Assert.True(actual.MeetsTarget(target));

        // 关闸：真把热值写成 0，就该判不达标——证明这条判据不是恒真的
        var zeroCv = new CoalQuality { AshPct = 11.2, CalorificMJkg = 0, SulfurPct = 0.6 };
        Assert.False(zeroCv.MeetsTarget(target));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  L7 穿孔量：台账 → 盘子 → 任务 → 实绩 → 进度
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 穿孔的量是<b>延米</b>，全链必须带得住。
    /// 断在任何一环，「工序进度跟踪」就只能退回 <c>Status == Done</c> 二值判 ——
    /// 而那个状态在实绩录入收穿孔之前<b>没有任何入口能置位</b>，于是穿孔进度恒 0%。
    /// </summary>
    [Fact]
    public void L7_穿孔量按延米算达成()
    {
        // 装箱下沉：台账 12 孔 × 14 m/孔 = 168 m
        var d = new DrillQuantity { PlanHoles = 12, PlanMeters = 168 };
        Assert.True(d.HasPlan);
        Assert.Null(d.AttainmentPct);                 // 还没录实绩 ⇒ null，不是 0

        d.ActualMeters = 126;
        Assert.Equal(75, d.AttainmentPct!.Value, 0);  // 126/168

        // 只有孔数没有延米时按孔数判（台账两项不一定都录）
        var byHoles = new DrillQuantity { PlanHoles = 10, ActualHoles = 9 };
        Assert.Equal(90, byHoles.AttainmentPct!.Value, 0);

        // 关闸：两项都没有 ⇒ 无计划量，进度只能退回"完成/未完成"二值
        var none = new DrillQuantity();
        Assert.False(none.HasPlan);
        Assert.Null(none.AttainmentPct);
    }

    /// <summary>穿孔实绩不许写进 <c>ActualVolumeM3</c> —— 那个字段一路被剥采比/库容/运输功当实方用。</summary>
    [Fact]
    public void L7b_穿孔量不污染实方()
    {
        var t = new ProductionTask
        {
            Id = "D0811-KY01-早", Process = ProcessType.Drill, Shift = "早班",
            Drill = new DrillQuantity { PlanHoles = 12, PlanMeters = 168, ActualMeters = 168 },
        };

        Assert.Equal(0, t.TargetVolumeM3, 3);
        Assert.Equal(0, t.ActualVolumeM3, 3);
        Assert.Equal(0, t.OreVolumeM3, 3);            // 不该混进采出量
        Assert.Equal(0, t.WasteVolumeM3, 3);          // 也不该混进剥离量（否则剥采比就错了）
        Assert.Equal(100, t.Drill!.AttainmentPct!.Value, 0);

        // 落盘的实绩记录同样分开装
        var rec = new ActualRecord
        {
            TaskId = t.Id, Process = t.Process,
            PlanDrillMeters = t.Drill.PlanMeters, ActualDrillMeters = t.Drill.ActualMeters,
        };
        Assert.Equal(168, rec.ActualDrillMeters!.Value, 1);
        Assert.Equal(0, rec.ActualVolumeM3, 3);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  L6 停机分账 → 实绩
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>计划检修与非计划故障各走各的账，且都按与任务时段的重叠摊。</summary>
    [Fact]
    public void L6_停机按类别分账()
    {
        var faults = new List<FaultEvent>
        {
            new() { EquipId = "WK-10", StartHour = 9, EstimatedHours = 2, IsPlanned = false },  // 故障 09–11
            new() { EquipId = "WK-10", StartHour = 13, EstimatedHours = 2, IsPlanned = true },  // 检修 13–15
        };

        // 任务时段 08–16：两段都全落在里面
        double fault = faults.Where(f => !f.IsPlanned).Sum(f => f.OverlapHours(8, 16));
        double maint = faults.Where(f => f.IsPlanned).Sum(f => f.OverlapHours(8, 16));
        Assert.Equal(2, fault, 2);
        Assert.Equal(2, maint, 2);

        // 任务时段 08–12：故障摊 2h、检修一点都不该摊进来
        Assert.Equal(2, faults.Where(f => !f.IsPlanned).Sum(f => f.OverlapHours(8, 12)), 2);
        Assert.Equal(0, faults.Where(f => f.IsPlanned).Sum(f => f.OverlapHours(8, 12)), 2);
    }
}
