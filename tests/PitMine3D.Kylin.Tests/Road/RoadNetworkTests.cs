// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/RoadNetworkTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>路网图 / 抽图 / 运距公式 的纯逻辑测试。</summary>
public class RoadNetworkTests
{
    // ── RoadEdge 几何自动算 ──

    [Fact]
    public void RoadEdge_ComputesLengthAndGradeFromCenterline()
    {
        // 水平 100m、升 10m → 坡度 10%，三维长 ≈100.5m
        var e = new RoadEdge("E", "a", "b", new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 10) });
        Assert.InRange(e.GradePct, 9.9, 10.1);
        Assert.InRange(e.LengthM, 100.4, 100.6);
    }

    // ── RoadGraph 邻接：单/双向 ──

    [Fact]
    public void TwoWayEdge_TraversableBothDirections()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddEdge(new RoadEdge("E", "a", "b"));
        Assert.Single(g.EdgesFrom("a"));
        Assert.Single(g.EdgesFrom("b"));   // 双向 → b 也能出发
    }

    [Fact]
    public void OneWayEdge_OnlyForward()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddEdge(new RoadEdge("E", "a", "b") { OneWay = true });
        Assert.Single(g.EdgesFrom("a"));
        Assert.Empty(g.EdgesFrom("b"));    // 单向 → b 出不去
    }

    // ── 校验 ──

    [Fact]
    public void Validate_DetectsDisconnectedComponents()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(10, 0, 0));
        g.AddNode("c", RoadNodeType.Junction, new Point3d(500, 0, 0));
        g.AddNode("d", RoadNodeType.Junction, new Point3d(510, 0, 0));
        g.AddEdge(new RoadEdge("E1", "a", "b"));
        g.AddEdge(new RoadEdge("E2", "c", "d"));   // 与 a-b 不连通
        var rep = g.Validate();
        Assert.False(rep.IsFullyConnected);
        Assert.Equal(2, rep.ComponentCount);
    }

    [Fact]
    public void Validate_FlagsOverGradeAndIsolatedNode()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 20)); // 20% 坡
        g.AddNode("lonely", RoadNodeType.Junction, new Point3d(9, 9, 9)); // 孤立
        g.AddEdge(new RoadEdge("E", "a", "b"));
        var rep = g.Validate(maxGradePct: 10);
        Assert.False(rep.Ok);
        Assert.Contains("lonely", rep.IsolatedNodeIds);
        Assert.Contains(rep.Issues, s => s.Contains("纵坡"));
    }

    [Fact]
    public void NearestNode_ReturnsClosest()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        Assert.Equal("b", g.NearestNode(new Point3d(95, 3, 0))!.Id);
        Assert.Null(g.NearestNode(new Point3d(95, 3, 0), maxDistM: 1.0)); // 超容差
    }

    // ── 抽图 RoadGraphBuilder ──

    [Fact]
    public void Builder_SharedEndpoints_MergeToOneNode()
    {
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0),   new Point3d(100, 0, 5) },
            new[] { new Point3d(100, 0, 5), new Point3d(200, 50, 12) }, // 共享 (100,0,5)
        };
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(3, g.NodeCount);   // 不是 4：共享端点并成一个
        Assert.Equal(2, g.EdgeCount);
    }

    [Fact]
    public void Builder_StackedSameXyDifferentZ_NotMerged()
    {
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0),  new Point3d(100, 0, 0) },
            new[] { new Point3d(100, 0, 0), new Point3d(100, 0, 30) }, // 同 XY、Z 差 30 → 不并
        };
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(3, g.NodeCount);
    }

    [Fact]
    public void Builder_ProducesTraversableGraph()
    {
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0),   new Point3d(100, 0, 5) },
            new[] { new Point3d(100, 0, 5), new Point3d(200, 50, 12) },
        };
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        var start = g.NearestNode(new Point3d(0, 0, 0))!;
        var end = g.NearestNode(new Point3d(200, 50, 12))!;
        var r = new DijkstraPathSolver(g).FindPath(start.Id, end.Id, PathQuery.Default);
        Assert.True(r.Feasible);
        Assert.Equal(2, r.EdgeIds.Count);
    }

    // ── 交叉口打断 noding（中心线 → 连通一期路网） ──

    [Fact]
    public void Noding_CrossIntersection_SplitsIntoFourEdges()
    {
        // 十字交叉、同标高 → 两线各打断成 2 段，交点并成 1 个共享节点。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(-50, 0, 10), new Point3d(50, 0, 10) },
            new[] { new Point3d(0, -50, 10), new Point3d(0, 50, 10) },
        };
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(4, g.EdgeCount);
        Assert.Equal(5, g.NodeCount);                  // 4 端点 + 1 交点
        Assert.Equal(1, g.Validate().ComponentCount);  // 连通
    }

    [Fact]
    public void Noding_TJunction_SplitsThroughLine()
    {
        // T 丁字：B 的端点落在 A 的中段 → A 打断成 2，B 保持 1，三边一节点连通。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(-50, 0, 10), new Point3d(50, 0, 10) },
            new[] { new Point3d(0, 0, 10),   new Point3d(0, 50, 10) },
        };
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(3, g.EdgeCount);
        Assert.Equal(4, g.NodeCount);
        Assert.Equal(1, g.Validate().ComponentCount);
    }

    [Fact]
    public void Noding_Overpass_NotSplit()
    {
        // 立交：XY 相交但标高差 30m → 不打断，保持两条不连通的边。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(-50, 0, 0),  new Point3d(50, 0, 0) },
            new[] { new Point3d(0, -50, 30), new Point3d(0, 50, 30) },
        };
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 2.0);
        Assert.Equal(2, g.EdgeCount);
        Assert.Equal(4, g.NodeCount);
        Assert.Equal(2, g.Validate().ComponentCount);  // 上下层不连通
    }

    [Fact]
    public void Noding_CollinearOverlap_DedupsDuplicateEdge()
    {
        // B 完全压在 A 的中段上 → A 在 30/70 打断成 3 段，中段与 B 重复 → 去重后中段只剩 1 条。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0),  new Point3d(100, 0, 0) },
            new[] { new Point3d(30, 0, 0), new Point3d(70, 0, 0) },
        };
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 2.0);
        Assert.Equal(1, rep.DuplicateEdgesRemoved);
        Assert.Equal(3, g.EdgeCount);                  // 0-30 / 30-70 / 70-100，无重复
        Assert.Equal(4, g.NodeCount);
        Assert.Equal(1, g.Validate().ComponentCount);
    }

    // ── 缺口桥接：能联通的尽量联通 ──

    [Fact]
    public void Bridge_ConnectsNearGapBetweenComponents()
    {
        // 两条共线但首尾隔 15m 的路（超 5m 吸附容差）→ 建图时桥接成一条连通网。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0),  new Point3d(100, 0, 0) },
            new[] { new Point3d(115, 0, 0), new Point3d(200, 0, 0) },   // 缺口 15m
        };
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(2, rep.ComponentsBeforeBridge);
        Assert.Equal(1, rep.BridgesAdded);
        Assert.Equal(1, g.Validate().ComponentCount);   // 桥接后全连通
    }

    [Fact]
    public void Bridge_SkipsGapAcrossElevation()
    {
        // 缺口 15m 但两端标高差 30m（立交/不同台阶）→ 不桥（应走坡道）。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0),   new Point3d(100, 0, 0) },
            new[] { new Point3d(115, 0, 30), new Point3d(200, 0, 30) },
        };
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(0, rep.BridgesAdded);
        Assert.Equal(2, g.Validate().ComponentCount);
    }

    [Fact]
    public void Bridge_RespectsGapLimit()
    {
        // 缺口 40m 超过桥接距离 25m → 不桥，保持两片。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0),   new Point3d(100, 0, 0) },
            new[] { new Point3d(140, 0, 0), new Point3d(200, 0, 0) },
        };
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(0, rep.BridgesAdded);
        Assert.Equal(2, g.Validate().ComponentCount);
    }

    [Fact]
    public void Noding_JunctionJustBeyondOldTolerance_WeldsAndKillsTheDetour()
    {
        // 现场那 1080 条中线，段对最近距 ≤2m 有 1261 对、2~5m 有 260 对、**5~10m 还有 229 对**。
        // 老容差 5m 正好卡在分布的腰上，5~10m 那批全部漏网 —— 不改变连通性（绕一圈还是通的），
        // 只是逼着车绕远，所以连通片数一直看着正常。这条判据把那个局面钉死。
        //
        // 造型：一条南北干线 + 一条东西支线，支线端点离干线 8m（超老容差、在新容差内）。
        // 支线另一头绕一大圈回到干线北端 —— 接不上时"绕远"，接上时直接拐过去。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0), new Point3d(0, 400, 0) },                     // 干线：南北
            new[] { new Point3d(8, 200, 0), new Point3d(300, 200, 0) },                 // 支线：端点离干线 8m
            new[] { new Point3d(300, 200, 0), new Point3d(300, 600, 0),                 // 绕行大环，回到干线北端
                    new Point3d(0, 600, 0), new Point3d(0, 400, 0) },
        };
        var cal = HaulCaliper.From(null, WeightMode.Distance);

        // A 组（关掉规则闸 = 老容差 5m）：8m 那处接不上 → 只能绕大环。
        var gOld = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 5.0, bridgeGapM: 0.0);
        var a0 = gOld.NearestNode(new Point3d(0, 200, 0), 1.0);                          // 干线中点没有节点
        Assert.Null(a0);
        var oldPath = new DijkstraPathSolver(gOld).FindPath("N0", gOld.Nodes
            .First(n => n.Position.DistanceTo(new Point3d(300, 200, 0)) < 1.0).Id, cal.Query(loaded: true));
        Assert.True(oldPath.Feasible);
        Assert.True(oldPath.LengthM > 900, $"老容差下本该绕大环，实际只有 {oldPath.LengthM:F0}m");

        // B 组（新容差 10m）：8m 那处焊上 → 干线被打断出路口，直接拐过去。
        var gNew = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 10.0, bridgeGapM: 0.0);
        var hub = gNew.NearestNode(new Point3d(0, 200, 0), 10.0);
        Assert.NotNull(hub);                                                             // 干线上真的多了个路口
        var newPath = new DijkstraPathSolver(gNew).FindPath("N0", gNew.Nodes
            .First(n => n.Position.DistanceTo(new Point3d(300, 200, 0)) < 1.0).Id, cal.Query(loaded: true));
        Assert.True(newPath.Feasible);
        Assert.True(newPath.LengthM < 550, $"新容差下该直接拐过去，实际 {newPath.LengthM:F0}m");
        Assert.True(newPath.LengthM < oldPath.LengthM * 0.6,
            $"绕远没被治掉：老 {oldPath.LengthM:F0}m → 新 {newPath.LengthM:F0}m");
    }

    [Fact]
    public void Noding_NearMissAcrossBenches_StillDoesNotWeld()
    {
        // 容差放宽不能把上下台阶并到一起：吸附判的是**三维**距，台阶间距 15m > 10m 容差。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0), new Point3d(0, 400, 0) },
            new[] { new Point3d(8, 200, 15), new Point3d(300, 200, 15) },   // 平面上 8m，但高 15m
        };
        var g = RoadGraphBuilder.FromPolylines(plines, snapToleranceM: 10.0, bridgeGapM: 0.0);
        Assert.Equal(2, g.Validate().ComponentCount);   // 各走各的，没并
    }

    [Fact]
    public void Bridge_FragmentNearestAtItsOwnMidSpan_ConnectsToTrunk()
    {
        // 碎片离干线最近的地方在**它自己的半腰上**（两端反而更远）——
        // 第一遍「悬挂端点桥接」只从度≤1 的端点出发，这种局面它永远接不上；第二遍按片间最近点对接才连得起来。
        // 现场那张网上这类占大头：桥接距离调到 50m 时连通片 69→37，最大片里程却纹丝不动（见 Tests P4）。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0), new Point3d(200, 0, 0) },                              // 干线，沿 y=0
            new[] { new Point3d(60, 60, 0), new Point3d(100, 12, 0), new Point3d(140, 60, 0) },  // 碎片：V 形，谷底离干线 12m，两端 60m
        };
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);

        Assert.Equal(2, rep.ComponentsBeforeBridge);
        Assert.Equal(0, rep.BridgesAdded);                  // A 组（关掉规则闸）：第一遍确实一条都接不上
        Assert.Equal(1, rep.GapBridgesAdded);               // B 组：第二遍接上了
        Assert.Equal(1, g.Validate().ComponentCount);

        // 补出来的边必须**自报家门**：逐段明细靠 IsSynthetic 把"这一段图上没有真路"标出来。
        var bridge = Assert.Single(g.Edges.Where(e => e.IsSynthetic));
        Assert.Equal(RoadEdge.SourceAutoBridge, bridge.SourceRef);
        Assert.All(g.Edges.Where(e => !e.Id.StartsWith("BR", StringComparison.Ordinal)),
            e => Assert.False(e.IsSynthetic));              // 真实中线不许被误标
    }

    [Fact]
    public void Bridge_ComponentGapPass_KeepsElevationAndDistanceGates()
    {
        // 第二遍不许比第一遍松：跨标高的不接（缺的是坡道）、超桥接距离的不接。
        var acrossZ = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0), new Point3d(200, 0, 0) },
            new[] { new Point3d(60, 60, 30), new Point3d(100, 12, 30), new Point3d(140, 60, 30) },
        };
        var g1 = RoadGraphBuilder.FromPolylines(acrossZ, out var r1, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(0, r1.TotalBridges);
        Assert.Equal(2, g1.Validate().ComponentCount);

        var tooFar = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0), new Point3d(200, 0, 0) },
            new[] { new Point3d(60, 90, 0), new Point3d(100, 40, 0), new Point3d(140, 90, 0) },   // 谷底离干线 40m
        };
        var g2 = RoadGraphBuilder.FromPolylines(tooFar, out var r2, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(0, r2.TotalBridges);
        Assert.Equal(2, g2.Validate().ComponentCount);
    }

    [Fact]
    public void Bridge_ComponentGapPass_AddsNoRedundantEdgeInsideOneComponent()
    {
        // 本来就连通的一张网：第二遍一条边都不许加（同片冗余边会凭空造出捷径，运距全错）。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0), new Point3d(200, 0, 0) },
            new[] { new Point3d(100, 0, 0), new Point3d(100, 80, 0) },     // T 形接在干线中段
        };
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 50.0);
        Assert.Equal(1, rep.ComponentsBeforeBridge);
        Assert.Equal(0, rep.TotalBridges);
    }

    [Fact]
    public void Bridge_SpurToTrunkMiddle_SplitsAndConnects()
    {
        // 支线端点离干线中段 12m（T 形缺口）→ 干线打断插节点 + 桥接，三段连通。
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(0, 0, 0),  new Point3d(100, 0, 0) },   // 干线
            new[] { new Point3d(50, 12, 0), new Point3d(50, 60, 0) },  // 支线，端点离干线 12m
        };
        var g = RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 5.0, bridgeGapM: 25.0);
        Assert.Equal(1, rep.BridgesAdded);
        Assert.Equal(1, g.Validate().ComponentCount);
        Assert.Equal(4, g.EdgeCount);   // 干线断成 2 + 支线 1 + 桥接 1
    }

    [Fact]
    public void NodingReport_TalliesSplits()
    {
        var plines = new IReadOnlyList<Point3d>[]
        {
            new[] { new Point3d(-50, 0, 10), new Point3d(50, 0, 10) },
            new[] { new Point3d(0, -50, 10), new Point3d(0, 50, 10) },
        };
        RoadGraphBuilder.FromPolylines(plines, out var rep, snapToleranceM: 2.0);
        Assert.Equal(2, rep.InputLines);
        Assert.Equal(1, rep.CrossSplits);              // 一个十字交点
        Assert.Equal(4, rep.OutputSegments);
    }

    [Fact]
    public void SplitEdgeAtNearest_BreaksEdgeAndInsertsNode()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddEdge(new RoadEdge("AB", "a", "b"));
        var node = g.SplitEdgeAtNearest("AB", new Point3d(50, 5, 0), "M");
        Assert.Null(g.GetEdge("AB"));              // 原边删除
        Assert.NotNull(g.GetEdge("AB_a"));         // 两半
        Assert.NotNull(g.GetEdge("AB_b"));
        Assert.Equal(3, g.NodeCount);
        Assert.InRange(node.Position.X, 49.0, 51.0);
        Assert.Equal(2, g.EdgesFrom("M").Count);   // 双向：M 接两条半边，可两向出发
    }

    // ── 路网持久化序列化（基础道路网络构建命名直接存 / 路网存档） ──

    [Fact]
    public void Serializer_RoundTripsGraph()
    {
        var g = new RoadGraph();
        g.AddNode(new RoadNode("L1", RoadNodeType.Loading, new Point3d(0, 0, 0)) { ThroughputTph = 1200, RefId = "h42" });
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 5));
        g.AddEdge(new RoadEdge("E", "L1", "b", new[] { new Point3d(0, 0, 0), new Point3d(50, 0, 2), new Point3d(100, 0, 5) })
        {
            LaneCount = 2, OneWay = true, Pavement = "gravel", Status = RoadEdgeStatus.Maintenance, IsTemporary = true,
        });

        var g2 = RoadGraphSerializer.FromJson(RoadGraphSerializer.ToJson(g));

        Assert.Equal(2, g2.NodeCount);
        Assert.Equal(1, g2.EdgeCount);
        var l1 = g2.GetNode("L1")!;
        Assert.Equal(RoadNodeType.Loading, l1.Type);
        Assert.Equal(1200, l1.ThroughputTph, 6);
        Assert.Equal("h42", l1.RefId);
        var e = g2.GetEdge("E")!;
        Assert.Equal(3, e.Centerline.Count);
        Assert.Equal(2, e.LaneCount);
        Assert.True(e.OneWay);
        Assert.Equal("gravel", e.Pavement);
        Assert.Equal(RoadEdgeStatus.Maintenance, e.Status);
        Assert.True(e.IsTemporary);
        Assert.InRange(e.LengthM, 100.0, 101.0);   // 中线里程保留
    }

    [Fact]
    public void Serializer_EmptyOrGarbage_ReturnsEmptyGraph()
    {
        Assert.Equal(0, RoadGraphSerializer.FromJson(null).NodeCount);
        Assert.Equal(0, RoadGraphSerializer.FromJson("").NodeCount);
        Assert.Equal(0, RoadGraphSerializer.FromJson("not json {").NodeCount);
    }

    // ── 运距公式 HaulMetrics ──

    [Fact]
    public void EquivalentLength_UphillLongerThanFlatLongerThanDownhill()
    {
        var t = TruckProfile.Default;
        double up = HaulMetrics.EquivalentLengthM(100, +8, t, loaded: true);
        double flat = HaulMetrics.EquivalentLengthM(100, 0, t, loaded: true);
        double down = HaulMetrics.EquivalentLengthM(100, -8, t, loaded: true);
        Assert.True(up > flat);
        Assert.True(flat > down);
        Assert.Equal(100, flat, 6);   // 平路等效=实距
    }

    [Fact]
    public void WeightedAverageHaul_WeightsByTonnage()
    {
        // 1000m@10t 与 3000m@30t → 加权 = (1000*10+3000*30)/40 = 2500
        double wavg = HaulMetrics.WeightedAverageHaulM(new[] { (1000.0, 10.0), (3000.0, 30.0) });
        Assert.Equal(2500, wavg, 3);
        Assert.Equal(3000, HaulMetrics.MaxHaulM(new[] { 1000.0, 3000.0, 1500.0 }), 6);
    }

    // ── 持久图（时段快照 / 取边） ──

    [Fact]
    public void Clone_IsIndependentDeepCopy()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddEdge(new RoadEdge("E", "a", "b"));
        var snap = g.Clone();
        g.RemoveEdge("E");                 // 改原图
        Assert.Equal(0, g.EdgeCount);
        Assert.Equal(1, snap.EdgeCount);   // 快照不受影响
    }

    [Fact]
    public void RemoveNode_AlsoRemovesIncidentEdges()
    {
        // 装卸点重录入：删节点须连带删掉挂在它上面的边，避免悬挂边。
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddNode("c", RoadNodeType.Junction, new Point3d(0, 100, 0));
        g.AddEdge(new RoadEdge("AB", "a", "b"));
        g.AddEdge(new RoadEdge("AC", "a", "c"));
        g.AddEdge(new RoadEdge("BC", "b", "c"));
        Assert.True(g.RemoveNode("a"));    // 删 a → AB、AC 连带删，BC 保留
        Assert.Equal(2, g.NodeCount);
        Assert.Equal(1, g.EdgeCount);
        Assert.NotNull(g.GetEdge("BC"));
        Assert.Empty(g.EdgesFrom("a"));    // 邻接已重建，a 不再出现
        Assert.False(g.RemoveNode("a"));   // 再删返回 false
    }

    [Fact]
    public void NearestEdge_ReturnsClosest()
    {
        var g = new RoadGraph();
        g.AddNode("a", RoadNodeType.Junction, new Point3d(0, 0, 0));
        g.AddNode("b", RoadNodeType.Junction, new Point3d(100, 0, 0));
        g.AddNode("c", RoadNodeType.Junction, new Point3d(0, 100, 0));
        g.AddEdge(new RoadEdge("AB", "a", "b"));
        g.AddEdge(new RoadEdge("AC", "a", "c"));
        Assert.Equal("AB", g.NearestEdge(new Point3d(50, 3, 0))!.Id);   // 贴近 X 轴边
        Assert.Equal("AC", g.NearestEdge(new Point3d(3, 50, 0))!.Id);   // 贴近 Y 轴边
    }
}
