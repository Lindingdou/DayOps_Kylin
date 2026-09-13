// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/PointToPointRoutingTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 「点对点寻径」两条口径的判据：**吸附吸的是路面**（R-P1~R-P5）、**不可达要答得出断在哪**（R-C1~R-C6）。
///
/// 这两条都是从现场那句"点了没反应、也指不出线路"倒推出来的：
///   · 吸附原来找的是<b>节点</b>，而路网节点只长在中线端点上（真实网中位 67m 一个），
///     50m 上限下十次有八次吸不上 —— 命令直接退出，看起来就是钮坏了。
///   · 判到"物理不连通"就只回一句"两片之间没有任何路"，用户下一步该去哪儿补线仍然无从下手。
///
/// 纪律照旧：**关掉规则闸必须真的红** —— R-P1b 用同一个点证明"按节点吸"确实吸不上，
/// R-C6 用 A/B 对照证明不补缺口就是不通（同 [[incline-shape-guards]] 的 F0）。
/// </summary>
public sealed class PointToPointRoutingTests
{
    // ── 造图小工具 ──

    /// <summary>一条直路：从 (x0,y,z) 到 (x1,y,z)，中间不设节点（真实中线就是这样：只有首末两个节点）。</summary>
    private static RoadGraph Straight(double x0, double x1, double y = 0, double z = 0)
    {
        var g = new RoadGraph();
        g.AddNode("A", RoadNodeType.Junction, new Point3d(x0, y, z));
        g.AddNode("B", RoadNodeType.Junction, new Point3d(x1, y, z));
        g.AddEdge(new RoadEdge("E0", "A", "B", new[] { new Point3d(x0, y, z), new Point3d(x1, y, z) }));
        return g;
    }

    /// <summary>往图里加一条独立的直边（自带节点），返回边 Id。</summary>
    private static string AddSegment(RoadGraph g, string tag, Point3d a, Point3d b)
    {
        g.AddNode(tag + "a", RoadNodeType.Junction, a);
        g.AddNode(tag + "b", RoadNodeType.Junction, b);
        g.AddEdge(new RoadEdge("E" + tag, tag + "a", tag + "b", new[] { a, b }));
        return "E" + tag;
    }

    // ── R-P：吸附口径 ──

    [Fact]
    public void P1_吸的是路面不是节点_点在两节点正中也吸得上()
    {
        var g = Straight(0, 400);                                  // 400m 一条边，只有两端有节点
        var mid = new Point3d(200, 3, 0);                           // 站在路正中、离路边 3m

        // R-P1：投影到边 → 吸得上，投影脚落在中线上。
        var e = g.NearestEdgeWithFoot(mid, 50.0, out var foot, out double d);
        Assert.NotNull(e);
        Assert.Equal(3.0, d, 3);
        Assert.Equal(200.0, foot.X, 3);
        Assert.Equal(0.0, foot.Y, 3);

        // R-P1b（关掉规则闸必须真的红）：同一个点按**节点**吸，50m 上限下吸不上 —— 这正是老实现的失败点。
        Assert.Null(g.NearestNode(mid, 50.0));
    }

    [Fact]
    public void P2_投影脚贴着已有节点时复用该节点_不打断出零长边()
    {
        var g = Straight(0, 400);
        var nearEnd = new Point3d(1.0, 0.5, 0);                     // 离 A 点 1.1m

        g.NearestEdgeWithFoot(nearEnd, 50.0, out var foot, out _);
        var a = g.GetNode("A")!;
        Assert.True(a.Position.DistanceTo(foot) <= 2.0);            // R-P2：调用方据此复用 A，不 Split

        // 真去打断的话就会留下一条零长边 —— 这条断言把"为什么要复用"钉住。
        var work = g.Clone();
        work.SplitEdgeAtNearest("E0", foot, "PICK");
        Assert.Contains(work.Edges, x => x.LengthM < 2.0);
    }

    [Fact]
    public void P3_上下叠置的两条路按三维距分辨_不吸到脚下那条()
    {
        // 露天矿的路是叠着的。造一个水平判据必然判错的局面：
        //   上台阶路 y=5 / z=30（用户就站在它上面），下台阶路 y=2 / z=0（水平上更近，但在脚下 30m）。
        var g = new RoadGraph();
        AddSegment(g, "Up", new Point3d(0, 5, 30), new Point3d(400, 5, 30));
        AddSegment(g, "Dn", new Point3d(0, 2, 0), new Point3d(400, 2, 0));

        var onUpper = new Point3d(200, 3, 30);      // 水平：离下台阶 1m、离上台阶 2m；三维：离上台阶 2m、离下台阶 30m

        var hit = g.NearestEdgeWithFoot(onUpper, 50.0, out var foot, out double d);
        Assert.Equal("EUp", hit!.Id);                               // R-P3：吸的是脚下这条路，不是它下面那条
        Assert.Equal(30.0, foot.Z, 3);
        Assert.Equal(2.0, d, 3);

        // 关掉规则闸必须真的红：按水平判据（NearestEdge）会判到下台阶那条 —— 差了一整个台阶。
        Assert.Equal("EDn", g.NearestEdge(onUpper, 50.0)!.Id);
    }

    [Fact]
    public void P4_打断只发生在副本上_会话图节点边数一个不变()
    {
        var g = Straight(0, 400);
        int n0 = g.NodeCount, e0 = g.EdgeCount;

        var work = g.Clone();
        work.NearestEdgeWithFoot(new Point3d(200, 3, 0), 50.0, out var foot, out _);
        work.SplitEdgeAtNearest("E0", foot, "PICK_A");

        Assert.Equal(n0 + 1, work.NodeCount);
        Assert.Equal(e0 + 1, work.EdgeCount);
        Assert.Equal(n0, g.NodeCount);                              // R-P4：原图一动不动
        Assert.Equal(e0, g.EdgeCount);
        Assert.Null(g.GetNode("PICK_A"));
    }

    [Fact]
    public void P5_超吸附上限就是不吸_不静默吸到远处()
    {
        var g = Straight(0, 400);
        Assert.Null(g.NearestEdgeWithFoot(new Point3d(200, 120, 0), 50.0, out _, out _));   // R-P5

        // 不限距时才报得出实距（用来给用户说"离最近路面 120m"）。
        Assert.NotNull(g.NearestEdgeWithFoot(new Point3d(200, 120, 0), double.MaxValue, out _, out double far));
        Assert.Equal(120.0, far, 3);
    }

    [Fact]
    public void P6_打断后两点真能解出一条路_里程等于两点间距()
    {
        var g = Straight(0, 400).Clone();
        g.NearestEdgeWithFoot(new Point3d(100, 2, 0), 50.0, out var f1, out _);
        g.SplitEdgeAtNearest("E0", f1, "PICK_A");
        var e2 = g.NearestEdgeWithFoot(new Point3d(300, 2, 0), 50.0, out var f2, out _)!;
        g.SplitEdgeAtNearest(e2.Id, f2, "PICK_B");

        var cal = HaulCaliper.From(null, WeightMode.Distance);
        var pair = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "PICK_A", "PICK_B", cal);
        Assert.True(pair.Feasible);
        Assert.Equal(200.0, pair.Outbound.Path.LengthM, 1);
    }

    [Fact]
    public void P7_取点Z不可信时按平面距吸附_并如实报出高差()
    {
        // 现场实拍：视口取点靠 PickWorldOnGeometry 对三角网射线求交拿 Z，图上没有可命中的地形面时它给 Z=0，
        // 而矿区路面在 1128~1515m —— 老口径（三维距）于是报"离最近路面 1239m"，用户明明点在路上。
        var g = Straight(0, 400, y: 0, z: 1239);
        var pickNoZ = new Point3d(200, 3, 0);                       // 平面上离路 3m，Z 差 1239m

        // R-P7a（关掉规则闸必须真的红）：按三维距，50m 上限下必然吸不上。
        Assert.Null(g.NearestEdgeWithFoot(pickNoZ, 50.0, out _, out _));

        // R-P7b：按取点口径 —— 平面距选路，吸得上；zUsed=false 表示这次没用 Z，dz 如实报出 1239m。
        var e = g.NearestEdgeForPick(pickNoZ, 50.0, 30.0, out var foot, out double dh, out double dz, out bool zUsed);
        Assert.NotNull(e);
        Assert.False(zUsed);
        Assert.Equal(3.0, dh, 3);
        Assert.Equal(1239.0, dz, 3);
        Assert.Equal(1239.0, foot.Z, 3);                            // 投影脚取的是**路面**标高，不是取点那个 0
    }

    [Fact]
    public void P8_取点Z可信时仍按三维分辨上下台阶()
    {
        // Z 靠谱（命中了地形面）时不许退化：上下叠置的两条路照样分得开。
        var g = new RoadGraph();
        AddSegment(g, "Up", new Point3d(0, 5, 30), new Point3d(400, 5, 30));
        AddSegment(g, "Dn", new Point3d(0, 2, 0), new Point3d(400, 2, 0));

        var onUpper = new Point3d(200, 3, 30);                      // 平面上离下台阶更近（1m vs 2m）
        var hit = g.NearestEdgeForPick(onUpper, 50.0, 30.0, out _, out _, out double dz, out bool zUsed);
        Assert.Equal("EUp", hit!.Id);                               // R-P8：Z 在带内 → 用三维距，判到上台阶
        Assert.True(zUsed);
        Assert.Equal(0.0, dz, 3);

        var onLower = new Point3d(200, 3, 0);
        Assert.Equal("EDn", g.NearestEdgeForPick(onLower, 50.0, 30.0, out _, out _, out _, out bool used2)!.Id);
        Assert.True(used2);
    }

    [Fact]
    public void P9_平面上真的点歪了还是不吸_报的是平面距()
    {
        var g = Straight(0, 400, y: 0, z: 1239);
        Assert.Null(g.NearestEdgeForPick(new Point3d(200, 120, 0), 50.0, 30.0, out _, out _, out _, out _));   // R-P9

        // 不限距时报得出平面实距（给用户说"平面上离最近路面 120m"），而不是那个 1245m 的斜距。
        Assert.NotNull(g.NearestEdgeForPick(new Point3d(200, 120, 0), double.MaxValue, 30.0,
            out _, out double dh, out _, out _));
        Assert.Equal(120.0, dh, 3);
    }

    // ── R-C：断口链 ──

    [Fact]
    public void C1_同一片时缺口链为空且判可达()
    {
        var g = Straight(0, 400);
        var plan = RoadConnectivity.PlanBridge(g, "A", "B");
        Assert.True(plan.Reachable);                                // R-C1
        Assert.Empty(plan.Gaps);
        Assert.Equal(plan.SrcComp, plan.DstComp);
    }

    [Fact]
    public void C2_断开一处时给出那一处_宽度取两片最近点距()
    {
        var g = new RoadGraph();
        AddSegment(g, "L", new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        AddSegment(g, "R", new Point3d(140, 0, 0), new Point3d(240, 0, 0));   // 中间断 40m

        var plan = RoadConnectivity.PlanBridge(g, "La", "Rb");
        Assert.True(plan.Reachable);                                // R-C2
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal(40.0, gap.GapM, 1);
        Assert.Equal(40.0, gap.HorizGapM, 1);
        Assert.Equal(0.0, gap.DzM, 3);
        Assert.True(gap.IsFlat(4.0));
        Assert.Equal(2, plan.ComponentCount);
    }

    [Fact]
    public void C3_隔着中间片时按顺序给全整条链()
    {
        var g = new RoadGraph();
        AddSegment(g, "L", new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        AddSegment(g, "M", new Point3d(130, 0, 0), new Point3d(230, 0, 0));   // 断 30m
        AddSegment(g, "R", new Point3d(280, 0, 0), new Point3d(380, 0, 0));   // 再断 50m

        var plan = RoadConnectivity.PlanBridge(g, "La", "Rb");
        Assert.True(plan.Reachable);
        Assert.Equal(2, plan.Gaps.Count);                           // R-C3
        Assert.Equal(30.0, plan.Gaps[0].GapM, 1);                   // 顺序：起点那头在前
        Assert.Equal(50.0, plan.Gaps[1].GapM, 1);
        Assert.Equal(50.0, plan.BottleneckM, 1);                    // 瓶颈 = 最宽那处
    }

    [Fact]
    public void C4_取的是最宽一处最窄的链_不是总长最短的链()
    {
        // 直连一跳 90m；绕中间片两跳 50+50=100m（更长）但最宽只有 50m。
        // R-C4：用户要补的是"最长那一段"，所以取瓶颈最小的绕行链，而不是总和最短的直连。
        var g = new RoadGraph();
        AddSegment(g, "L", new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        AddSegment(g, "R", new Point3d(190, 0, 0), new Point3d(290, 0, 0));         // 与 L 直接断 90m
        AddSegment(g, "M", new Point3d(150, 50, 0), new Point3d(150, 150, 0));      // 与 L、R 各断 ~50m

        var plan = RoadConnectivity.PlanBridge(g, "La", "Rb");
        Assert.True(plan.Reachable);
        Assert.Equal(2, plan.Gaps.Count);
        Assert.True(plan.BottleneckM < 90.0, $"瓶颈应当小于直连的 90m，实际 {plan.BottleneckM:F1}m");
    }

    [Fact]
    public void C5_缺口超上限就判接不通_不许编一条接法出来()
    {
        var g = new RoadGraph();
        AddSegment(g, "L", new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        AddSegment(g, "R", new Point3d(600, 0, 0), new Point3d(700, 0, 0));   // 断 500m

        var plan = RoadConnectivity.PlanBridge(g, "La", "Rb", maxGapM: 300.0);
        Assert.False(plan.Reachable);                               // R-C5
        Assert.Empty(plan.Gaps);
        Assert.NotEqual(plan.SrcComp, plan.DstComp);
    }

    [Fact]
    public void C6_跨标高的缺口标成非平接_不进一键补边()
    {
        var g = new RoadGraph();
        AddSegment(g, "L", new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        AddSegment(g, "R", new Point3d(130, 0, 15), new Point3d(230, 0, 15));   // 水平 30m、高差 15m

        var plan = RoadConnectivity.PlanBridge(g, "La", "Rb");
        Assert.True(plan.Reachable);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal(15.0, Math.Abs(gap.DzM), 1);
        Assert.False(gap.IsFlat(4.0));                              // R-C6：缺的是坡道，不是一段平路
        Assert.False(plan.AllFlat(4.0));
    }

    [Fact]
    public void C7_补上缺口才通_不补就是不通()
    {
        var g = new RoadGraph();
        AddSegment(g, "L", new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        AddSegment(g, "R", new Point3d(140, 0, 0), new Point3d(240, 0, 0));
        var cal = HaulCaliper.From(null, WeightMode.Distance);

        // A 组（关掉规则闸）：不补 → 必须真的不通，且主因是"物理不连通"。
        var before = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "La", "Rb", cal);
        Assert.False(before.Feasible);
        Assert.Equal(HaulBlockCause.Disconnected, before.PrimaryDiagnosis!.Cause);

        // B 组：按缺口链补一条连接边 → 通，且里程含那 40m。
        var plan = RoadConnectivity.PlanBridge(g, "La", "Rb");
        foreach (var gap in plan.Gaps)
        {
            var na = Attach(g, gap.From, "H" + gap.FromComp);
            var nb = Attach(g, gap.To, "H" + gap.ToComp);
            g.AddEdge(new RoadEdge($"BRX{gap.FromComp}_{gap.ToComp}", na.Id, nb.Id,
                new[] { na.Position, nb.Position }));
        }
        var after = HaulSolveKernel.Solve(g, new DijkstraPathSolver(g), "La", "Rb", cal);
        Assert.True(after.Feasible);                                // R-C7
        Assert.Equal(240.0, after.Outbound.Path.LengthM, 1);
    }

    /// <summary>把落在中线上的点接进图（贴着已有节点就用它）—— 与插件里 <c>AttachToRoad</c> 同口径。</summary>
    private static RoadNode Attach(RoadGraph g, Point3d p, string id)
    {
        var e = g.NearestEdgeWithFoot(p, 4.0, out var foot, out _)!;
        if (g.GetNode(e.FromId) is { } a && a.Position.DistanceTo(foot) <= 2.0) return a;
        if (g.GetNode(e.ToId) is { } b && b.Position.DistanceTo(foot) <= 2.0) return b;
        return g.SplitEdgeAtNearest(e.Id, foot, id);
    }
}
