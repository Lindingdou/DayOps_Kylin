// 忠实移植自原 PitMine3D Modules/RoadLib/Editing/RoadChange.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>变更类型（手动编辑与演化对比共用这一套）。</summary>
public enum RoadChangeKind
{
    /// <summary>加边（两端为草稿图中已有节点，直线/给定中线）。</summary>
    AddEdge,
    /// <summary>删边。</summary>
    RemoveEdge,
    /// <summary>设边状态（开放/检修/封闭…）。</summary>
    SetStatus,
    /// <summary>在某边上打断插交叉口节点（底层 SplitEdgeAtNearest）。</summary>
    SplitEdge,
    /// <summary>替换边中线（延长/缩短/改线 reroute，端点不变）。</summary>
    ModifyCenterline,
    /// <summary>人工改判线路类型（干线/支线/孤立段；null=恢复自动判据）。按<b>路段</b>下达，落到该路段每条边。</summary>
    SetRoadClass,
}

/// <summary>变更来源：人工交互编辑 / 演化对比自动检测。</summary>
public enum RoadChangeSource { Manual, Evolution }

/// <summary>
/// 一条待提交变更——「暂存式编辑 + 提交闸门」体系的统一单元。
/// 「增量增删边」「边状态」「演化对比」三个功能全部产出本类型；「更新路网」逐条锁定（提交）。
/// <see cref="Apply"/> 前向作用到草稿图；撤销由会话用克隆栈实现，本类型不存逆操作。
/// </summary>
public sealed class RoadChange
{
    public RoadChangeKind Kind { get; init; }
    public RoadChangeSource Source { get; init; } = RoadChangeSource.Manual;

    /// <summary>目标/新建边 id（草稿图内）。</summary>
    public string? EdgeId { get; init; }

    // ── AddEdge / SplitEdge / ModifyCenterline 载荷 ──
    public string? FromNodeId { get; init; }
    public string? ToNodeId { get; init; }
    public IReadOnlyList<Point3d>? Centerline { get; init; }
    public Point3d? At { get; init; }          // SplitEdge 打断处
    public string? NewNodeId { get; init; }     // SplitEdge 新交叉口节点 id

    // ── SetStatus 载荷 ──
    public RoadEdgeStatus Status { get; init; }

    // ── SetRoadClass 载荷 ──
    /// <summary>改判目标类型；<c>null</c> = 恢复自动判据。</summary>
    public RoadSegmentClass? RoadClass { get; init; }
    /// <summary>要落改判的边集（= 该路段串起来的每一条边）。路段是派生对象存不住，故按边落。</summary>
    public IReadOnlyList<string>? EdgeIds { get; init; }

    // ── 复核 / 证据（演化用；手动编辑默认满置信、已选中） ──
    /// <summary>清单显示标题。</summary>
    public string Title { get; init; } = "";
    /// <summary>判定置信度 0..1（演化：由匹配裕度算；手动：1）。</summary>
    public double Confidence { get; init; } = 1.0;
    /// <summary>「为什么这么判」的证据文字（演化用，可空）。</summary>
    public string? Evidence { get; init; }
    /// <summary>是否勾选锁定（提交时只应用选中的）。</summary>
    public bool Selected { get; set; } = true;

    /// <summary>前向应用到草稿图（失败抛 InvalidOperationException）。</summary>
    public void Apply(RoadGraph g)
    {
        switch (Kind)
        {
            case RoadChangeKind.AddEdge:
                if (EdgeId is null || FromNodeId is null || ToNodeId is null)
                    throw new InvalidOperationException("AddEdge 缺少 EdgeId/FromNodeId/ToNodeId。");
                g.AddEdge(new RoadEdge(EdgeId, FromNodeId, ToNodeId, Centerline));
                break;

            case RoadChangeKind.RemoveEdge:
                if (EdgeId is null) throw new InvalidOperationException("RemoveEdge 缺少 EdgeId。");
                g.RemoveEdge(EdgeId);
                break;

            case RoadChangeKind.SetStatus:
                if (EdgeId is null) throw new InvalidOperationException("SetStatus 缺少 EdgeId。");
                {
                    var e = g.GetEdge(EdgeId);
                    if (e != null) e.Status = Status;
                }
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
                if (EdgeIds is null || EdgeIds.Count == 0)
                    throw new InvalidOperationException("SetRoadClass 缺少 EdgeIds（改判按整条路段落到每条边）。");
                foreach (var id in EdgeIds)
                {
                    var edge = g.GetEdge(id);
                    if (edge != null) edge.RoadClass = RoadClass;   // null = 恢复自动
                }
                break;
        }
    }

    /// <summary>类别中文（清单/日志显示）。</summary>
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
