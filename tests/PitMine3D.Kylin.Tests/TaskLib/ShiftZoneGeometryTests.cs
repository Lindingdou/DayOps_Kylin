// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ShiftZoneGeometryTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.ShiftOps;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 班内工序推演「把任务画在它自己那块地上」的判据。
///
/// <para><b>病灶</b>：<see cref="ProcessLine"/> / <see cref="ProcessStep"/> 原先<b>只带一个点</b>，
/// 于是班内推演只能在影像上打几个标注 —— 量、节拍、瓶颈全是真的，唯独"这活在哪块地上干、
/// 范围多大"完全没有。三个采装面挤在同一处看不出区别，根因在模型缺几何，不在画法。</para>
///
/// <para><b>Z1 是这一组最要紧的一条</b>：采场轮廓是<b>强凹</b>的（帮坡一圈圈往里收）。
/// 用扇形三角化会把凹口整片填成实心 —— 画面上是"这块地比实际大一圈"，而且不报错。
/// 判据用一个 L 形环钉死：凹口里的那个点<b>不许</b>落在任何一个三角里。</para>
/// </summary>
public class ShiftZoneGeometryTests
{
    // ── Z1：凹环必须耳切，不许填成凸包 ────────────────────────────────────────
    [Fact]
    public void Z1_凹环三角化不许填满凹口()
    {
        // ★ 必须用**非星形**的凹多边形。第一版用的是 L 形 —— 它从顶点 0 能看见所有其它顶点，
        //   扇形三角化对它碰巧是对的，于是这条判据在 F0 自检里空过了（扇形版照样绿）。
        //   U 形（凹口开在中间）从任何一个顶点都看不全，扇形必然把凹口填实。
        //   U 形：外框 10×10，中间挖掉 x∈[3,7]、y∈[3,10] 的槽 ⇒ 面积 100 − 28 = 72。
        var ring = Ring((0, 0), (10, 0), (10, 10), (7, 10), (7, 3), (3, 3), (3, 10), (0, 10));

        Assert.True(RingMesh.Triangles(ring, z: 1200, out var xyz, out int nt),
                    "U 形是简单多边形，耳切必须成功");
        Assert.Equal(6, nt);                       // n−2 = 8−2
        Assert.Equal(nt * 9, xyz.Length);

        Assert.False(CoveredBy(xyz, nt, 5, 7),
            "凹槽里的点被三角覆盖了 —— 多半退成了扇形/凸包三角化，画面上这块地会比实际大一圈，而且不报错");

        // 反例组：U 形内部的点必须被盖住（否则上面那条靠"什么都没画"也能过）
        Assert.True(CoveredBy(xyz, nt, 1, 5), "U 形左腿内部的点没被盖住 ⇒ 三角化漏了一块");
        Assert.True(CoveredBy(xyz, nt, 9, 5), "U 形右腿内部的点没被盖住");
        Assert.True(CoveredBy(xyz, nt, 5, 1), "U 形底部内部的点没被盖住");

        // 面积守恒：三角面积之和 = 环面积（72）。扇形版这里会是 121，一比就露馅。
        Assert.Equal(72.0, TriArea(xyz, nt), 3);

        // 高程整体落在给定 z 上（俯视看不出来，轴测下会穿地）
        for (int i = 2; i < xyz.Length; i += 3) Assert.Equal(1200, xyz[i], 6);
    }

    // ── Z2：自交环整块不画，不许硬填一个近似形状 ──────────────────────────────
    [Fact]
    public void Z2_自交环不画并且不抛()
    {
        var bowtie = Ring((0, 0), (10, 10), (10, 0), (0, 10));    // 蝴蝶结
        Assert.False(RingMesh.Triangles(bowtie, 0, out _, out int nt));
        Assert.Equal(0, nt);

        // 退化输入一律 false，不抛（台账里空环/两点环都真实存在）
        Assert.False(RingMesh.Triangles(null, 0, out _, out _));
        Assert.False(RingMesh.Triangles(Ring((0, 0), (1, 1)), 0, out _, out _));
    }

    // ── Z3：闭合环（首尾重复点）与开口环给同一个结果 ──────────────────────────
    [Fact]
    public void Z3_首尾重复点不影响三角化()
    {
        var open = Ring((0, 0), (10, 0), (10, 10), (0, 10));
        var closed = Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0));

        Assert.True(RingMesh.Triangles(open, 0, out _, out int n1));
        Assert.True(RingMesh.Triangles(closed, 0, out _, out int n2));
        Assert.Equal(n1, n2);
        Assert.Equal(2, n1);
    }

    // ── Z4：不传几何时保持老行为（只带点），传了才带环 ────────────────────────
    [Fact]
    public void Z4_不传几何时行为不变()
    {
        var (tasks, shifts) = OneLoadTask();

        var plain = DayProcessPlan.Build(tasks, shifts, faces: null, date: new DateTime(2026, 8, 20));
        var line = plain.Shifts.SelectMany(s => s.Lines).Single();

        Assert.False(line.HasRing);
        Assert.Equal(ShiftZoneGeometry.SrcNone, line.RingSource);
        Assert.Equal(0, line.Ring.Count);
    }

    // ── Z5：配不到地时如实说「无」，不许拿别的区域凑一块 ──────────────────────
    [Fact]
    public void Z5_配不到地时如实报无()
    {
        var g = ShiftZoneGeometry.Load("2026-08", sinks: null);

        var hit = g.Resolve(ProcessType.Load, "这个面名台账里绝不会有·XYZZY");
        Assert.False(hit.Has);
        Assert.Equal(ShiftZoneGeometry.SrcNone, hit.Source);

        // 空面名同理（任务上 WorkZone 为空是合法状态）
        Assert.False(g.Resolve(ProcessType.Load, "").Has);
        Assert.False(g.Resolve(ProcessType.Load, null).Has);
    }

    // ── Z6：读不到台账也要能开窗 —— 整层降级不抛 ──────────────────────────────
    [Fact]
    public void Z6_台账不可用时降级不抛()
    {
        var g = ShiftZoneGeometry.Load("", sinks: null);          // 空期次
        Assert.False(string.IsNullOrWhiteSpace(g.SourceLabel));
        Assert.False(g.Resolve(ProcessType.Drill, "任意面").Has);

        // Build 拿着这样一份几何也必须照常出计划（班内推演不能因为图上没区域就打不开）
        var (tasks, shifts) = OneLoadTask();
        var plan = DayProcessPlan.Build(tasks, shifts, null, new DateTime(2026, 8, 20), null, g);
        Assert.Single(plan.Shifts);
        Assert.Single(plan.Shifts[0].Lines);
    }

    // ── 夹具 ─────────────────────────────────────────────────────────────────

    private static List<SimPoint> Ring(params (double X, double Y)[] pts)
        => pts.Select(p => new SimPoint(p.X, p.Y)).ToList();

    private static (List<ProductionTask>, List<ShiftWindow>) OneLoadTask()
    {
        var cfg = new ExploderConfig
        {
            DateLabel = "2026-08-20 周四", IdPrefix = "Z", EnforceMassBalance = false,
            Shifts = { new ShiftWindow("早班", 0, 8) },
        };
        cfg.Faces.Add(new FaceInput
        {
            Zone = "采场1（pit）·岩1308", Process = ProcessType.Load, Material = "岩", DayTargetM3 = 2000,
            Group = new EquipmentGroup { MainEquipment = "WK-01", GroupCapacityM3PerH = 400 },
        });
        return (TaskExploder.Explode(cfg).Tasks, cfg.Shifts);
    }

    /// <summary>点是否被任一三角覆盖（含边）。</summary>
    private static bool CoveredBy(double[] xyz, int nt, double px, double py)
    {
        for (int t = 0; t < nt; t++)
        {
            double ax = xyz[t * 9], ay = xyz[t * 9 + 1];
            double bx = xyz[t * 9 + 3], by = xyz[t * 9 + 4];
            double cx = xyz[t * 9 + 6], cy = xyz[t * 9 + 7];
            double d1 = Cross(ax, ay, bx, by, px, py);
            double d2 = Cross(bx, by, cx, cy, px, py);
            double d3 = Cross(cx, cy, ax, ay, px, py);
            bool neg = d1 < -1e-9 || d2 < -1e-9 || d3 < -1e-9;
            bool pos = d1 > 1e-9 || d2 > 1e-9 || d3 > 1e-9;
            if (!(neg && pos)) return true;
        }
        return false;
    }

    private static double TriArea(double[] xyz, int nt)
    {
        double s = 0;
        for (int t = 0; t < nt; t++)
            s += Math.Abs(Cross(xyz[t * 9], xyz[t * 9 + 1], xyz[t * 9 + 3], xyz[t * 9 + 4],
                                xyz[t * 9 + 6], xyz[t * 9 + 7])) / 2;
        return s;
    }

    private static double Cross(double ax, double ay, double bx, double by, double px, double py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
}
