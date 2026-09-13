// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/HaulCaliperKernelTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 口径接线（③ → 求解器）+ 往返对内核 + 不可达逐级放宽诊断 的判据。
///
/// 每条都对着一个**曾经真的错了**的地方：限坡给了值却从不生效、空车腿方向写反、
/// 空车不可达时拿重车时间顶替、未设单价按 0 报 0 元/趟、限坡建在整段平均坡上。
/// 判据都做成"关掉修复必须变红"的形式（改动前跑必然失败），不是跟着实现走的同义反复。
/// </summary>
public class HaulCaliperKernelTests
{
    // ── 口径映射：③ → TruckProfile / MaxGradePct ──

    [Fact]
    public void Caliper_MapsSharedConstraints_PayloadGradeSpeedCost()
    {
        var cal = HaulCaliper.From(new TransportConstraintMirror
        {
            TruckClass = "150t级",
            TruckPayload = 150,
            TruckClimbPct = 10,
            MaxGradePct = 8,
            DesignSpeedKmh = 30,
            HaulUnitCost = 2.5,
        });
        Assert.True(cal.FromSharedSettings);
        Assert.Equal(150, cal.Truck.PayloadT, 3);
        Assert.Equal(8, cal.MaxGradePct, 3);
        Assert.Equal(30, cal.Truck.FlatSpeedLoadedKph, 3);
        Assert.True(cal.Truck.FlatSpeedEmptyKph > cal.Truck.FlatSpeedLoadedKph);   // 空车更快
        Assert.Equal(2.5, cal.UnitHaulCostPerTonKm!.Value, 3);
    }

    [Fact]
    public void Caliper_NoSettings_KeepsGradeUnlimitedAndSaysSo()
    {
        var cal = HaulCaliper.From(null);
        Assert.False(cal.FromSharedSettings);
        Assert.Equal(0, cal.MaxGradePct, 3);           // 没有约束就不假装有限坡
        Assert.Null(cal.UnitHaulCostPerTonKm);
        Assert.Contains(cal.Notes, n => n.Contains("未读到共享运输约束"));
    }

    [Fact]
    public void Caliper_ZeroUnitCost_IsNullNotZero()
    {
        // ③ 的 HaulUnitCost 默认就是 0（用户没填），按 0 算会静默报出"0 元/趟"。
        var cal = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, MaxGradePct = 8, DesignSpeedKmh = 25, HaulUnitCost = 0 });
        Assert.Null(cal.UnitHaulCostPerTonKm);
        Assert.Contains(cal.Notes, n => n.Contains("未填运输单价"));
    }

    [Fact]
    public void Caliper_Hash_IsStableByValueAndChangesWithCaliper()
    {
        var a = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, MaxGradePct = 8, DesignSpeedKmh = 25 });
        var b = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, MaxGradePct = 8, DesignSpeedKmh = 25 });
        var c = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, MaxGradePct = 6, DesignSpeedKmh = 25 });
        Assert.Equal(a.Hash(), b.Hash());          // 按值，不按引用
        Assert.NotEqual(a.Hash(), c.Hash());       // 限坡变了 → 指纹必须变
        Assert.NotEqual(a.Hash(), a.WithMode(WeightMode.Distance).Hash());
    }

    // ── 限坡：分段最大坡，且真的生效 ──

    /// <summary>
    /// 直线两段：前 100m 平、后 100m 陡（升 24m ≈ 24%）。整段平均只有 12%，
    /// 按平均判 8% 限坡 → 12% &gt; 8 也会被挡；所以再造一条更长的"平均被稀释"的边来验分段口径。
    /// </summary>
    private static RoadGraph GraphWithHiddenSteepSegment()
    {
        var g = new RoadGraph();
        g.AddNode("A", RoadNodeType.Loading, new Point3d(0, 0, 0));
        g.AddNode("B", RoadNodeType.Unloading, new Point3d(1000, 0, 20));
        // 1000m 里藏一段 20m 长、升 6m（30%）的陡坎，其余平：整段平均 = 20/1000 = 2%。
        var line = new List<Point3d>
        {
            new(0, 0, 0), new(480, 0, 0), new(500, 0, 6), new(1000, 0, 20),
        };
        g.AddEdge(new RoadEdge("steep", "A", "B", line));
        return g;
    }

    [Fact]
    public void MaxAbsSegGrade_CatchesSteepSegmentHiddenInLongEdge()
    {
        var e = GraphWithHiddenSteepSegment().GetEdge("steep")!;
        Assert.InRange(e.GradePct, 1.9, 2.1);            // 整段平均：2%，看着完全合规
        Assert.True(e.MaxAbsSegGradePct > 8.0,
            $"分段最大坡应抓到藏着的陡坎，实测 {e.MaxAbsSegGradePct:F1}%");
    }

    [Fact]
    public void GradeLimit_BlocksEdgeWhoseAverageLooksFine()
    {
        var g = GraphWithHiddenSteepSegment();
        var solver = new DijkstraPathSolver(g);
        var cal = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, MaxGradePct = 8, DesignSpeedKmh = 25 });

        var blocked = solver.FindPath("A", "B", cal.Query(loaded: true));
        Assert.False(blocked.Feasible);                 // 按分段坡：被挡

        var unlimited = solver.FindPath("A", "B", HaulCaliper.From(null).Query(loaded: true));
        Assert.True(unlimited.Feasible);                // 不限坡：能过 —— 说明确实是限坡在起作用
    }

    [Fact]
    public void GradeLimit_ZeroMeansUnlimited_LibraryDefaultStaysNeutral()
        => Assert.Equal(0.0, PathQuery.Default.MaxGradePct, 6);

    // ── 限载：只卡重车，空车照走 ──

    /// <summary>两条并行路：短路限载 60t（重车过不去），长路不限。</summary>
    private static RoadGraph GraphWithLoadLimit()
    {
        var g = new RoadGraph();
        g.AddNode("S", RoadNodeType.Loading, new Point3d(0, 0, 0));
        g.AddNode("M", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddNode("T", RoadNodeType.Unloading, new Point3d(200, 0, 0));
        g.AddNode("D", RoadNodeType.Junction, new Point3d(100, 400, 0));   // 绕行点
        g.AddEdge(new RoadEdge("short1", "S", "M") { MaxLoadT = 60 });
        g.AddEdge(new RoadEdge("short2", "M", "T"));
        g.AddEdge(new RoadEdge("long1", "S", "D"));
        g.AddEdge(new RoadEdge("long2", "D", "T"));
        return g;
    }

    [Fact]
    public void LoadLimit_LoadedDetours_EmptyDoesNot()
    {
        var g = GraphWithLoadLimit();
        var solver = new DijkstraPathSolver(g);
        var cal = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, MaxGradePct = 0, DesignSpeedKmh = 25 },
            WeightMode.Distance);

        var pair = HaulSolveKernel.Solve(g, solver, "S", "T", cal);
        Assert.True(pair.Feasible);
        Assert.DoesNotContain("short1", pair.Outbound.Path.EdgeIds);   // 重车绕行
        Assert.Contains("short1", pair.Return.Path.EdgeIds);           // 空车不受限载，走短路
    }

    // ── 往返对：方向、不顶替、成本 ──

    [Fact]
    public void ReturnLeg_GoesFromSinkBackToSource()
    {
        var g = GraphWithLoadLimit();
        var pair = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "S", "T", HaulCaliper.From(null, WeightMode.Distance));
        Assert.Equal("S", pair.Outbound.FromId);
        Assert.Equal("T", pair.Outbound.ToId);
        Assert.Equal("T", pair.Return.FromId);      // 回程必须 dst→src
        Assert.Equal("S", pair.Return.ToId);
        Assert.False(pair.Return.Loaded);           // 且是空车
    }

    [Fact]
    public void OneWayLoop_ReturnLegTakesADifferentRoad()
    {
        // 单行环线：S→T 只能走上路，T→S 只能走下路。把去程反过来当回程会给出错的运距。
        var g = new RoadGraph();
        g.AddNode("S", RoadNodeType.Loading, new Point3d(0, 0, 0));
        g.AddNode("T", RoadNodeType.Unloading, new Point3d(300, 0, 0));
        g.AddNode("R", RoadNodeType.Junction, new Point3d(150, 500, 0));
        g.AddEdge(new RoadEdge("up", "S", "T") { OneWay = true });
        g.AddEdge(new RoadEdge("back1", "T", "R") { OneWay = true });
        g.AddEdge(new RoadEdge("back2", "R", "S") { OneWay = true });

        var pair = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "S", "T", HaulCaliper.From(null, WeightMode.Distance));
        Assert.True(pair.Feasible);
        Assert.Equal(new[] { "up" }, pair.Outbound.Path.EdgeIds);
        Assert.Equal(new[] { "back1", "back2" }, pair.Return.Path.EdgeIds);
        Assert.True(pair.Return.Path.LengthM > pair.Outbound.Path.LengthM);   // 回程更远，反转去程会低报
    }

    [Fact]
    public void ReturnUnreachable_MetricsAreNull_NotSubstitutedByOutbound()
    {
        // 只有单向去程，回不来。
        var g = new RoadGraph();
        g.AddNode("S", RoadNodeType.Loading, new Point3d(0, 0, 0));
        g.AddNode("T", RoadNodeType.Unloading, new Point3d(300, 0, 0));
        g.AddEdge(new RoadEdge("only", "S", "T") { OneWay = true });

        var pair = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "S", "T", HaulCaliper.From(null, WeightMode.Distance));
        Assert.True(pair.Outbound.Feasible);
        Assert.False(pair.Return.Feasible);
        Assert.False(pair.Feasible);
        Assert.Null(pair.RoundTripEquivM);       // 不拿去程值凑往返
        Assert.Null(pair.CycleTimeMin);          // 不拿去程时间当回程时间
        Assert.Equal("回程不可达", pair.StatusText);
        Assert.Equal(HaulBlockCause.OneWay, pair.PrimaryDiagnosis!.Cause);   // 原因归诊断，不塞进状态文案
    }

    [Fact]
    public void Cost_IsNullWhenUnitPriceMissing_AndComputedWhenSet()
    {
        var g = GraphWithLoadLimit();
        var solver = new DijkstraPathSolver(g);

        var noPrice = HaulSolveKernel.Solve(g, solver, "S", "T",
            HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, DesignSpeedKmh = 25, HaulUnitCost = 0 }, WeightMode.Distance));
        Assert.Null(noPrice.CostPerTripYuan);    // 未设单价 → —，绝不是 0

        var priced = HaulSolveKernel.Solve(g, solver, "S", "T",
            HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, DesignSpeedKmh = 25, HaulUnitCost = 1.5 }, WeightMode.Distance));
        Assert.NotNull(priced.CostPerTripYuan);
        Assert.True(priced.CostPerTripYuan > 0);
    }

    [Fact]
    public void CycleTime_UsesCaliperLoadAndDumpTimes()
    {
        var g = GraphWithLoadLimit();
        var pair = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "S", "T", HaulCaliper.From(null, WeightMode.Distance));
        double drive = pair.Outbound.Path.TimeMin + pair.Return.Path.TimeMin;
        Assert.Equal(drive + 3.0 + 1.5, pair.CycleTimeMin!.Value, 3);
    }

    // ── 不可达诊断：5 级，逐级放宽 ──

    private static RoadGraph TwoRoads()
    {
        var g = new RoadGraph();
        g.AddNode("S", RoadNodeType.Loading, new Point3d(0, 0, 0));
        g.AddNode("T", RoadNodeType.Unloading, new Point3d(200, 0, 0));
        g.AddEdge(new RoadEdge("e", "S", "T"));
        return g;
    }

    [Fact]
    public void Diagnose_ClosedEdge_ReportsStatusAndNamesIt()
    {
        var g = TwoRoads();
        g.GetEdge("e")!.Status = RoadEdgeStatus.Closed;
        var pair = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "S", "T", HaulCaliper.From(null, WeightMode.Distance));
        var d = pair.PrimaryDiagnosis!;
        Assert.Equal(HaulBlockCause.Status, d.Cause);
        Assert.Contains("e", d.EdgeIds);
        Assert.Contains("关闭", d.Text);
    }

    [Fact]
    public void Diagnose_SteepEdge_ReportsGradeWithActualSteepness()
    {
        var g = GraphWithHiddenSteepSegment();
        var cal = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, MaxGradePct = 8, DesignSpeedKmh = 25 }, WeightMode.Distance);
        var pair = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "A", "B", cal);
        var d = pair.PrimaryDiagnosis!;
        Assert.Equal(HaulBlockCause.Grade, d.Cause);
        Assert.Contains("steep", d.EdgeIds);
    }

    [Fact]
    public void Diagnose_OverloadedEdge_ReportsLoadNotDisconnected()
    {
        var g = TwoRoads();
        g.GetEdge("e")!.MaxLoadT = 60;                       // 车 90t 过不去，且没有别的路
        var cal = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, DesignSpeedKmh = 25 }, WeightMode.Distance);
        var d = HaulSolveKernel.Diagnose(g, new DijkstraPathSolver(g), "S", "T", cal, loaded: true);
        Assert.Equal(HaulBlockCause.Load, d.Cause);
        Assert.Contains("e", d.EdgeIds);
    }

    [Fact]
    public void Diagnose_OneWayAgainstDirection_IsNotReportedAsDisconnected()
    {
        // 这一条是第 4 级存在的全部理由：单向逆行与物理不连通在求解器眼里都是 Unreachable，
        // 但一个改属性就能解决、另一个要补一条真路。合并了就会让人去查根本不存在的断点。
        var g = new RoadGraph();
        g.AddNode("S", RoadNodeType.Loading, new Point3d(0, 0, 0));
        g.AddNode("T", RoadNodeType.Unloading, new Point3d(200, 0, 0));
        g.AddEdge(new RoadEdge("one", "T", "S") { OneWay = true });   // 只能 T→S

        var d = HaulSolveKernel.Diagnose(g, new DijkstraPathSolver(g), "S", "T", HaulCaliper.From(null, WeightMode.Distance), loaded: true);
        Assert.Equal(HaulBlockCause.OneWay, d.Cause);
        Assert.Contains("one", d.EdgeIds);
    }

    [Fact]
    public void Diagnose_TrulyDisconnected_ReportsComponentNumbers()
    {
        var g = new RoadGraph();
        g.AddNode("S", RoadNodeType.Loading, new Point3d(0, 0, 0));
        g.AddNode("S2", RoadNodeType.Junction, new Point3d(50, 0, 0));
        g.AddNode("T", RoadNodeType.Unloading, new Point3d(9000, 0, 0));
        g.AddNode("T2", RoadNodeType.Junction, new Point3d(9050, 0, 0));
        g.AddEdge(new RoadEdge("a", "S", "S2"));
        g.AddEdge(new RoadEdge("b", "T", "T2"));

        var d = HaulSolveKernel.Diagnose(g, new DijkstraPathSolver(g), "S", "T", HaulCaliper.From(null, WeightMode.Distance), loaded: true);
        Assert.Equal(HaulBlockCause.Disconnected, d.Cause);
        Assert.Contains("连通片", d.Text);
    }

    [Fact]
    public void Diagnose_IsDeterministic_SameGraphSameCaliperSameAnswer()
    {
        var g = TwoRoads();
        g.GetEdge("e")!.Status = RoadEdgeStatus.Maintenance;
        var cal = HaulCaliper.From(null, WeightMode.Distance);
        var solver = new DijkstraPathSolver(g);
        var d1 = HaulSolveKernel.Diagnose(g, solver, "S", "T", cal, loaded: true);
        var d2 = HaulSolveKernel.Diagnose(g, solver, "S", "T", cal, loaded: true);   // 同一 solver：缓存不得串味
        Assert.Equal(d1.Cause, d2.Cause);
        Assert.Equal(d1.Text, d2.Text);
    }

    // ── 吸附上限 ──

    [Fact]
    public void NearestNode_RespectsSnapRadius()
    {
        var g = TwoRoads();
        var far = new Point3d(0, 500, 0);                       // 离最近节点 500m
        Assert.Null(g.NearestNode(far, 50.0));                  // 超上限：不吸附
        Assert.NotNull(g.NearestNode(far));                     // 不限时才吸得到（老行为）
    }

    // ── 报表：口径与确定性 ──

    [Fact]
    public void Report_CarriesCaliperAndModeText()
    {
        var g = GraphWithLoadLimit();
        var cal = HaulCaliper.From(new TransportConstraintMirror { TruckPayload = 90, MaxGradePct = 8, DesignSpeedKmh = 25 });
        var ind = TransportIndicatorsBuilder.Compute(g, new List<RoadGraph>(), cal);
        Assert.Contains("限坡=8.0%", ind.CaliperSummary);
        Assert.Equal("时间最短", ind.ModeText);
    }

    [Fact]
    public void Report_BottleneckOrderIsDeterministic()
    {
        var g = GraphWithLoadLimit();
        var cal = HaulCaliper.From(null, WeightMode.Distance);
        var a = TransportIndicatorsBuilder.Compute(g, new List<RoadGraph>(), cal).Bottlenecks.Select(b => b.EdgeId).ToList();
        var b2 = TransportIndicatorsBuilder.Compute(g, new List<RoadGraph>(), cal).Bottlenecks.Select(b => b.EdgeId).ToList();
        Assert.Equal(a, b2);
    }

    [Fact]
    public void Report_ThroughputWeightedAvg_IsNullWhenNoThroughputRecorded()
    {
        var g = GraphWithLoadLimit();   // 节点未录 ThroughputTph
        var ind = TransportIndicatorsBuilder.Compute(g, new List<RoadGraph>(), HaulCaliper.From(null, WeightMode.Distance));
        Assert.Null(ind.ThroughputWeightedAvgEquivM);   // 权全为 0 → 不退化成算术平均冒充"加权"
    }
}
