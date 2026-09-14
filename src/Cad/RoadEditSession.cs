using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>变更类型（手动编辑与演化对比共用这一套）。忠实原 RoadLib.Editing.RoadChangeKind。</summary>
public enum RoadChangeKind { AddEdge, RemoveEdge, SetStatus, SplitEdge, ModifyCenterline, SetRoadClass }

/// <summary>变更来源：人工交互编辑 / 演化对比自动检测。</summary>
public enum RoadChangeSource { Manual, Evolution }

/// <summary>
/// 一条待提交变更——「暂存式编辑 + 提交闸门」体系的统一单元（忠实原 RoadChange）。
/// <see cref="Apply"/> 前向作用到草稿图；撤销由会话用克隆栈实现，本类型不存逆操作。
/// </summary>
public sealed class RoadChange
{
    public RoadChangeKind Kind { get; init; }
    public RoadChangeSource Source { get; init; } = RoadChangeSource.Manual;
    public string? EdgeId { get; init; }
    public string? FromNodeId { get; init; }
    public string? ToNodeId { get; init; }
    public IReadOnlyList<Point3d>? Centerline { get; init; }
    public Point3d? At { get; init; }
    public string? NewNodeId { get; init; }
    public RoadEdgeStatus Status { get; init; }
    /// <summary>改判目标类型；null = 恢复自动判据。</summary>
    public RoadSegmentClass? RoadClass { get; init; }
    /// <summary>要落改判的边集（= 该路段串起来的每一条边）。</summary>
    public IReadOnlyList<string>? EdgeIds { get; init; }
    public string Title { get; init; } = "";
    public double Confidence { get; init; } = 1.0;
    public string? Evidence { get; init; }
    public bool Selected { get; set; } = true;

    public void Apply(RoadGraph g)
    {
        switch (Kind)
        {
            case RoadChangeKind.AddEdge:
                if (EdgeId is null || FromNodeId is null || ToNodeId is null) throw new InvalidOperationException("AddEdge 缺少 EdgeId/FromNodeId/ToNodeId。");
                g.AddEdge(new RoadEdge(EdgeId, FromNodeId, ToNodeId, Centerline));
                break;
            case RoadChangeKind.RemoveEdge:
                if (EdgeId is null) throw new InvalidOperationException("RemoveEdge 缺少 EdgeId。");
                g.RemoveEdge(EdgeId);
                break;
            case RoadChangeKind.SetStatus:
                if (EdgeId is null) throw new InvalidOperationException("SetStatus 缺少 EdgeId。");
                { var e = g.GetEdge(EdgeId); if (e != null) e.Status = Status; }
                break;
            case RoadChangeKind.SplitEdge:
                if (EdgeId is null || At is null) throw new InvalidOperationException("SplitEdge 缺少 EdgeId/At。");
                g.SplitEdgeAtNearest(EdgeId, At.Value, NewNodeId ?? $"{EdgeId}_j");
                break;
            case RoadChangeKind.ModifyCenterline:
                if (EdgeId is null || Centerline is null) throw new InvalidOperationException("ModifyCenterline 缺少 EdgeId/Centerline。");
                g.GetEdge(EdgeId)?.SetCenterline(Centerline);
                break;
            case RoadChangeKind.SetRoadClass:
                if (EdgeIds is null || EdgeIds.Count == 0) throw new InvalidOperationException("SetRoadClass 缺少 EdgeIds（改判按整条路段落到每条边）。");
                foreach (var id in EdgeIds) { var edge = g.GetEdge(id); if (edge != null) edge.RoadClass = RoadClass; }
                break;
        }
    }

    public string KindText => Kind switch
    {
        RoadChangeKind.AddEdge => "加边",
        RoadChangeKind.RemoveEdge => "删边",
        RoadChangeKind.SetStatus => "改状态",
        RoadChangeKind.SplitEdge => "插交叉口",
        RoadChangeKind.ModifyCenterline => "改线",
        RoadChangeKind.SetRoadClass => "改线路类型",
        _ => Kind.ToString(),
    };
}

/// <summary>草稿图相对基准图的差异（diff 预览 + 图例用）。</summary>
public sealed class RoadGraphDiff
{
    public List<RoadEdge> Added { get; } = new();
    public List<RoadEdge> Removed { get; } = new();
    public List<RoadEdge> StatusChanged { get; } = new();
    public List<RoadEdge> Modified { get; } = new();
    public List<RoadEdge> ClassChanged { get; } = new();
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && StatusChanged.Count == 0 && Modified.Count == 0 && ClassChanged.Count == 0;
}

/// <summary>
/// 路网编辑会话（「暂存式编辑 + 提交闸门」核心引擎，忠实原 RoadEditSession）。
/// 持有一张活草稿图（进入时克隆当前网），每条变更直接改草稿；撤销靠每步前克隆草稿压栈；
/// 提交前当前网保持不动 —— 只有「更新路网」拿 Draft 去锁定。
/// </summary>
public sealed class RoadEditSession
{
    public RoadGraph BaseSnapshot { get; }
    public string? BaseArchiveName { get; }
    public RoadGraph Draft { get; private set; }
    /// <summary>进编辑那一刻的基准拓扑（结构轴：所有边都算）。</summary>
    public RoadTopologyReport BaseTopology { get; }
    /// <summary>同上，可通行轴（检修/封闭的边按不存在算）。</summary>
    public RoadTopologyReport BasePassableTopology { get; }

    private readonly List<RoadChange> _journal = new();
    private readonly Stack<RoadGraph> _undo = new();
    private int _seq;

    public IReadOnlyList<RoadChange> Journal => _journal;
    public bool HasChanges => _journal.Count > 0;
    public bool CanUndo => _undo.Count > 0;

    public RoadEditSession(RoadGraph current, string? baseArchiveName = null)
    {
        BaseSnapshot = current.Clone();
        BaseArchiveName = baseArchiveName;
        Draft = current.Clone();
        BaseTopology = RoadTopology.Analyze(BaseSnapshot);
        BasePassableTopology = RoadTopology.Analyze(BaseSnapshot, passableOnly: true);
    }

    /// <summary>草稿的当前拓扑。每次现算 —— 草稿每步都在变，缓存必然过期。</summary>
    public RoadTopologyReport DraftTopology(bool passableOnly = false) => RoadTopology.Analyze(Draft, passableOnly);

    /// <summary>本次编辑累计的拓扑变化（相对进编辑时的基准），没动返回空串。撤销/清空之后这个数会自己走回去。</summary>
    public string TopologyDelta(bool passableOnly = false)
        => RoadTopology.DescribeDelta(passableOnly ? BasePassableTopology : BaseTopology, DraftTopology(passableOnly));

    public string NewEdgeId() { string id; do { id = $"E{++_seq}"; } while (Draft.GetEdge(id) != null); return id; }
    public string NewNodeId() { string id; do { id = $"N{++_seq}"; } while (Draft.GetNode(id) != null); return id; }

    /// <summary>应用一条变更（统一通道：压撤销栈 → 改草稿 → 记日志）。</summary>
    public void Apply(RoadChange c)
    {
        _undo.Push(Draft.Clone());
        c.Apply(Draft);
        _journal.Add(c);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        Draft = _undo.Pop();
        if (_journal.Count > 0) _journal.RemoveAt(_journal.Count - 1);
        return true;
    }

    public void Clear()
    {
        Draft = BaseSnapshot.Clone();
        _journal.Clear();
        _undo.Clear();
    }

    /// <summary>草稿 vs 基准的差异（按边 id 比对：新增/删除/改状态/改线/改类型，三档各自独立判）。</summary>
    public RoadGraphDiff ComputeDiff()
    {
        var diff = new RoadGraphDiff();
        var baseById = new Dictionary<string, RoadEdge>();
        foreach (var e in BaseSnapshot.Edges) baseById[e.Id] = e;
        var draftIds = new HashSet<string>();
        foreach (var e in Draft.Edges)
        {
            draftIds.Add(e.Id);
            if (!baseById.TryGetValue(e.Id, out var b)) { diff.Added.Add(e); continue; }
            if (e.Status != b.Status) diff.StatusChanged.Add(e);
            if (CenterlineChanged(b, e)) diff.Modified.Add(e);
            if (e.RoadClass != b.RoadClass) diff.ClassChanged.Add(e);
        }
        foreach (var b in BaseSnapshot.Edges) if (!draftIds.Contains(b.Id)) diff.Removed.Add(b);
        return diff;
    }

    /// <summary>中线是否实质改变：逐坐标比（整体横移也算变）。</summary>
    private static bool CenterlineChanged(RoadEdge a, RoadEdge b)
    {
        if (a.Centerline.Count != b.Centerline.Count) return true;
        for (int i = 0; i < a.Centerline.Count; i++)
        {
            var p = a.Centerline[i]; var q = b.Centerline[i];
            if (!p.X.Equals(q.X) || !p.Y.Equals(q.Y) || !p.Z.Equals(q.Z)) return true;
        }
        return false;
    }
}
