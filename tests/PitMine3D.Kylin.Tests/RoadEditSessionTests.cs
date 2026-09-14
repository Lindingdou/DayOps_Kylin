using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 增量增删边（§三六九）：路网暂存式编辑会话（忠实原 RoadEditSession / RoadChange / RoadTopology(RoadGraph)）。
/// 口径：草稿图克隆自当前网，每条变更直接改草稿；撤销靠克隆栈；diff 三档独立判；拓扑变化对基准算累计（结构轴 / 可通行轴分开）。
/// </summary>
public class RoadEditSessionTests
{
    /// <summary>T 形：A(0,0)-(100,0)-(200,0) 与 B(100,0)-(100,80) 接在 (100,0)；C(230,0)-(330,0) 断开。</summary>
    private static RoadGraph Sample()
    {
        var polys = new List<IReadOnlyList<Point3d>>
        {
            new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 0), new Point3d(200, 0, 0) },
            new[] { new Point3d(100, 0, 0), new Point3d(100, 80, 0) },
            new[] { new Point3d(230, 0, 0), new Point3d(330, 0, 0) },
        };
        return RoadGraphBuilder.FromPolylines(polys, snapToleranceM: 2.0, gradeSeparationM: 4.0, bridgeGapM: 0);
    }

    [Fact]
    public void 拓扑_按图分析_路段附带边集_可通行轴把检修封闭当不存在()
    {
        var g = Sample();
        var t = RoadTopology.Analyze(g);
        Assert.Equal(2, t.ComponentCount);
        Assert.Equal(g.EdgeCount, t.SegmentByEdge.Count);                  // 每条边都归入了某一路段
        Assert.All(t.Segments, s => Assert.NotEmpty(s.EdgeIds));
        Assert.All(t.Segments, s => Assert.Equal(s.Class, s.AutoClass));   // 没改判 ⇒ 显示类别 = 自动判据
        Assert.Equal(1, t.SegmentCountByClass[(int)RoadSegmentClass.Isolated]);   // C 段两头悬空

        // 封掉 B ⇒ 可通行轴上它消失；结构轴不动
        var b = g.Edges.First(e => Math.Abs(e.Centerline[0].X - 100) < 1e-6 && Math.Abs(e.Centerline[^1].Y - 80) < 1e-6 || Math.Abs(e.Centerline[^1].X - 100) < 1e-6 && Math.Abs(e.Centerline[0].Y - 80) < 1e-6);
        b.Status = RoadEdgeStatus.Closed;
        var pass = RoadTopology.Analyze(g, passableOnly: true);
        Assert.Equal(4, t.Segments.Count);                                  // A1 / A2 / B / C
        Assert.Equal(2, pass.Segments.Count);                               // B 不通 ⇒ 路口退成接缝，A1+A2 并成一段；C 照旧
        Assert.Equal(t.TotalLengthM - 80, pass.TotalLengthM, 6);
        Assert.Equal(0, pass.JunctionCount);
        Assert.Equal(4, RoadTopology.Analyze(g).Segments.Count);           // 结构轴不受状态影响
    }

    [Fact]
    public void 会话_加边接通两片_撤销后拓扑走回去()
    {
        var s = new RoadEditSession(Sample(), "T 形");
        Assert.Equal(2, s.BaseTopology.ComponentCount);
        var a = s.Draft.NearestNode(new Point3d(200, 0, 0), 30)!;
        var b = s.Draft.NearestNode(new Point3d(230, 0, 0), 30)!;
        Assert.NotEqual(a.Id, b.Id);
        string id = s.NewEdgeId();
        s.Apply(new RoadChange { Kind = RoadChangeKind.AddEdge, EdgeId = id, FromNodeId = a.Id, ToNodeId = b.Id, Centerline = new[] { a.Position, b.Position }, Title = id });
        Assert.True(s.HasChanges); Assert.True(s.CanUndo);
        Assert.Contains("连通片 2→1", s.TopologyDelta());
        var diff = s.ComputeDiff();
        Assert.Single(diff.Added); Assert.Empty(diff.Removed);
        Assert.Equal(30, s.Draft.GetEdge(id)!.LengthM, 6);
        // 基准图没动
        Assert.Equal(Sample().EdgeCount, s.BaseSnapshot.EdgeCount);
        Assert.True(s.Undo());
        Assert.Equal("", s.TopologyDelta());
        Assert.False(s.HasChanges);
        Assert.True(s.ComputeDiff().IsEmpty);
        Assert.False(s.Undo());
    }

    [Fact]
    public void 会话_改状态只在可通行轴现形_删边插交叉口改线三档独立()
    {
        var s = new RoadEditSession(Sample());
        var e = s.Draft.NearestEdge(new Point3d(150, 0, 0), 30)!;
        s.Apply(new RoadChange { Kind = RoadChangeKind.SetStatus, EdgeId = e.Id, Status = RoadEdgeStatus.Maintenance });
        Assert.Equal("", s.TopologyDelta());                                    // 结构轴没动
        Assert.NotEqual("", s.TopologyDelta(passableOnly: true));               // 可通行轴变了
        Assert.Single(s.ComputeDiff().StatusChanged);

        // 同一条边再改线：改状态与改线两档同时报，不互相吃掉
        var moved = e.Centerline.Select(p => new Point3d(p.X, p.Y + 10, p.Z)).ToList();
        s.Apply(new RoadChange { Kind = RoadChangeKind.ModifyCenterline, EdgeId = e.Id, Centerline = moved });
        var d = s.ComputeDiff();
        Assert.Single(d.StatusChanged); Assert.Single(d.Modified);

        // 插交叉口：原边没了、两半继承状态
        var c = s.Draft.NearestEdge(new Point3d(280, 0, 0), 30)!;
        s.Apply(new RoadChange { Kind = RoadChangeKind.SplitEdge, EdgeId = c.Id, At = new Point3d(280, 0, 0), NewNodeId = s.NewNodeId() });
        Assert.Null(s.Draft.GetEdge(c.Id));
        Assert.NotNull(s.Draft.GetEdge(c.Id + "_a")); Assert.NotNull(s.Draft.GetEdge(c.Id + "_b"));
        Assert.Equal(50, s.Draft.GetEdge(c.Id + "_a")!.LengthM, 6);
        d = s.ComputeDiff();
        Assert.Equal(2, d.Added.Count); Assert.Single(d.Removed);

        // 删边
        s.Apply(new RoadChange { Kind = RoadChangeKind.RemoveEdge, EdgeId = c.Id + "_b" });
        Assert.Null(s.Draft.GetEdge(c.Id + "_b"));
        Assert.Equal(4, s.Journal.Count);
        s.Clear();
        Assert.Empty(s.Journal); Assert.True(s.ComputeDiff().IsEmpty);
    }

    [Fact]
    public void 会话_线路类型改判按整条路段落到每条边_显示类别随改判()
    {
        var s = new RoadEditSession(Sample());
        var e = s.Draft.NearestEdge(new Point3d(50, 0, 0), 30)!;
        var topo = s.DraftTopology();
        Assert.True(topo.SegmentByEdge.TryGetValue(e.Id, out var seg));
        Assert.Equal(RoadSegmentClass.Spur, seg!.AutoClass);                    // (0,0) 悬空 ⇒ 支线
        s.Apply(new RoadChange { Kind = RoadChangeKind.SetRoadClass, EdgeId = e.Id, EdgeIds = seg.EdgeIds, RoadClass = RoadSegmentClass.Trunk, Title = seg.Id });
        Assert.All(seg.EdgeIds, id => Assert.Equal(RoadSegmentClass.Trunk, s.Draft.GetEdge(id)!.RoadClass));
        var after = s.DraftTopology();
        Assert.True(after.SegmentByEdge.TryGetValue(e.Id, out var seg2));
        Assert.Equal(RoadSegmentClass.Trunk, seg2!.Class);
        Assert.Equal(RoadSegmentClass.Spur, seg2.AutoClass);
        Assert.Contains("干线 0→1", s.TopologyDelta());
        Assert.Single(s.ComputeDiff().ClassChanged);
        // 恢复自动
        s.Apply(new RoadChange { Kind = RoadChangeKind.SetRoadClass, EdgeId = e.Id, EdgeIds = seg.EdgeIds, RoadClass = null });
        Assert.Empty(s.ComputeDiff().ClassChanged);
        // 克隆保住改判
        s.Apply(new RoadChange { Kind = RoadChangeKind.SetRoadClass, EdgeId = e.Id, EdgeIds = seg.EdgeIds, RoadClass = RoadSegmentClass.Isolated });
        Assert.Equal(RoadSegmentClass.Isolated, s.Draft.Clone().GetEdge(e.Id)!.RoadClass);
    }

    [Fact]
    public void 最近边_按中线水平距_超容差返回空()
    {
        var g = Sample();
        Assert.NotNull(g.NearestEdge(new Point3d(150, 5, 0), 30));
        Assert.Null(g.NearestEdge(new Point3d(150, 50, 0), 30));
        Assert.NotNull(g.NearestEdge(new Point3d(100, 40, 999), 30));           // Z 不参与
        Assert.Throws<InvalidOperationException>(() => new RoadChange { Kind = RoadChangeKind.AddEdge, EdgeId = "x" }.Apply(g));
    }
}
