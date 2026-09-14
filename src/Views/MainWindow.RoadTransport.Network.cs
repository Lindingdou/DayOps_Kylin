using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Views.Road;
using Point3d = PitMine3D.Kylin.Cad.Road.Point3d;
using RoadGraph = PitMine3D.Kylin.Cad.Road.RoadGraph;
using RoadNode = PitMine3D.Kylin.Cad.Road.RoadNode;
using RoadEdge = PitMine3D.Kylin.Cad.Road.RoadEdge;
using RoadNodeType = PitMine3D.Kylin.Cad.Road.RoadNodeType;
using RoadEdgeStatus = PitMine3D.Kylin.Cad.Road.RoadEdgeStatus;
using RoadGraphBuilder = PitMine3D.Kylin.Cad.Road.RoadGraphBuilder;
using RoadTopology = PitMine3D.Kylin.Cad.Road.RoadTopology;
using RoadSegmentClass = PitMine3D.Kylin.Cad.Road.RoadSegmentClass;
using RoadNodeClass = PitMine3D.Kylin.Cad.Road.RoadNodeClass;
using RoadChange = PitMine3D.Kylin.Cad.Road.RoadChange;
using RoadChangeKind = PitMine3D.Kylin.Cad.Road.RoadChangeKind;
using RoadEditSession = PitMine3D.Kylin.Cad.Road.RoadEditSession;
using RoadEvolutionResult = PitMine3D.Kylin.Cad.Road.RoadEvolutionResult;
using RoadEvolutionClass = PitMine3D.Kylin.Cad.Road.RoadEvolutionClass;
using EvolutionSide = PitMine3D.Kylin.Cad.Road.EvolutionSide;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 道路运输系统 · 路网构建 / 维护与延拓：基础道路网络构建 · 路网预览 · 路网更新 · 增量增删边 · 边状态 · 时段快照 · 路网存档 ·
/// 演化对比 · 分色显示 · 破碎站位置设置（接线部分）。忠实原 RoadLibPlugin 对应命令。
/// </summary>
public partial class MainWindow
{
    // ═══════════════════════ 基础道路网络构建 ═══════════════════════

    /// <summary>「基础道路网络构建」：配参数(吸附/桥接/立交)+命名 → 把所有道路中心线整合成连通的可规划/寻径路网 → 设为会话图并按名直接随工程存储。</summary>
    private async Task RoadBuildNetworkAsync()
    {
        var lines = ReadRoadCenterlines();
        var haulRoads = ReadLandedHaulRoads();
        lines.AddRange(HaulRoadImporter.ToPolylines(haulRoads));
        if (lines.Count == 0)
        { EditEcho("未找到道路中线。请先「提取道路中心线」/「坑线落地」，或绘制/选中中线。", EchoLevel.Warn); return; }

        var dlg = new BuildNetworkDialog($"路网 {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (!await dlg.AskAsync(this)) return;

        var graph = RoadGraphBuilder.FromPolylines(lines, out var nrep,
            snapToleranceM: dlg.SnapToleranceM, gradeSeparationM: dlg.GradeSeparationM, bridgeGapM: dlg.BridgeGapM);
        StampHaulRoads(graph, haulRoads);
        RS.Graph = graph;

        var rep = graph.Validate();
        EditEcho($"路网图：中线 {nrep.InputLines}→{nrep.OutputSegments} 段（交叉打断 X{nrep.CrossSplits}/T{nrep.TeeSplits}"
                 + (nrep.DuplicateEdgesRemoved > 0 ? $"，去重叠 {nrep.DuplicateEdgesRemoved}" : "") + "）；"
                 + $"{graph.NodeCount} 节点 / {graph.EdgeCount} 边，{RoadBridgeText(nrep, rep)}",
            rep.IsFullyConnected ? EchoLevel.Success : EchoLevel.Warn);

        // 建网当场报分类结果：中线之间到底关联成了什么（R-T1~R-T3）。「节点 N 个」会骗人 —— 一半以上是打断留下的接缝。
        var topo = RoadTopology.Analyze(graph);
        int iso = topo.SegmentCountByClass[(int)RoadSegmentClass.Isolated];
        EditEcho($"　拓扑分类：{topo.Summary}（{topo.SeamCount} 个接缝不是路口，只是中线打断处的缝）", iso == 0 ? EchoLevel.Info : EchoLevel.Warn);
        if (iso > 0)
            EditEcho($"　⚠ {iso} 条路段两端都悬挂（孤立段，图上红色）—— 这些中线谁也没接上，寻径永远走不到。可加大桥接距离重建，或「增量增删边」手工接。", EchoLevel.Warn);
        if (!rep.IsFullyConnected)
            EditEcho($"⚠ 路网仍未全连通（{rep.ComponentCount} 个独立片）：剩余缺口超 {dlg.BridgeGapM:F0}m 桥接距离或跨标高。可加大桥接距离重建，或「手动标定线路」补连接。", EchoLevel.Warn);

        RoadPersistNetwork(dlg.NetworkName, graph);
        StatusMsg.Text = $"基础道路网络构建：{graph.NodeCount} 节点 / {graph.EdgeCount} 边 / 连通片 {rep.ComponentCount}（「{dlg.NetworkName}」）";
    }

    /// <summary>把路网图按名称随工程存储（同名覆盖，否则新建）。工程库未连接则仅会话有效。</summary>
    private void RoadPersistNetwork(string name, RoadGraph g)
    {
        var conn = _geoDb?.Connection;
        if (conn == null)
        {
            EditEcho("（工程库未连接，本次未随工程存储；路网仅会话内有效。连上库后重点「基础道路网络构建」即可存档）", EchoLevel.Warn);
            RS.CurrentNetworkName = name;
            return;
        }
        try
        {
            string json = RoadGraphSerializer.ToJson(g);
            double km = RoadGraphSerializer.TotalKm(g);
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var existing = Data.RoadNetworkStore.List(conn).FirstOrDefault(e => e.Name == name);
            if (existing is not null)
            {
                existing.GraphJson = json; existing.NodeCount = g.NodeCount; existing.EdgeCount = g.EdgeCount; existing.LengthKm = km; existing.CapturedAt = now;
                Data.RoadNetworkStore.Update(conn, existing);
                EditEcho($"✓ 已更新路网存档「{name}」（{g.NodeCount} 节点 / {g.EdgeCount} 边 / {km:F2} km，随工程保存）。可在「路网存档」载入/对比。", EchoLevel.Success);
            }
            else
            {
                long id = Data.RoadNetworkStore.Insert(conn, new Data.RoadNetworkRecord
                { Name = name, CapturedAt = now, GraphJson = json, NodeCount = g.NodeCount, EdgeCount = g.EdgeCount, LengthKm = km });
                EditEcho($"✓ 路网已存储「{name}」#{id}（{g.NodeCount} 节点 / {g.EdgeCount} 边 / {km:F2} km，随工程保存）。可在「路网存档」载入/对比。", EchoLevel.Success);
            }
            RS.CurrentNetworkName = name;
        }
        catch (Exception ex) { EditEcho($"路网存储失败：{ex.Message}", EchoLevel.Error); }
    }

    // ═══════════════════════ 路网预览 ═══════════════════════

    /// <summary>「路网预览」：把构建好的路网图按拓扑分类诊断式画进视口（干线绿/支线灰蓝/孤立段红、路口白环、悬挂端点红环、源/汇图元）；再点一次关闭。</summary>
    private void RoadNetworkPreviewCmd()
    {
        if (RoadLayerCount(RoadNetworkPreviewLayer) > 0)
        { BeginChange(); RoadClearLayer(RoadNetworkPreviewLayer); RefreshScene(); EditEcho("路网预览：已关闭。"); StatusMsg.Text = "路网预览已关闭"; return; }

        RoadGraph g;
        var rs = RS;
        if (rs.Graph != null && rs.Graph.EdgeCount > 0)
        {
            g = rs.Graph;
            EditEcho($"路网预览：展示当前已载入/已构建的路网图（{g.NodeCount} 节点 / {g.EdgeCount} 边），与寻径/运距计算同源。如需按图层全部中心线重建,请先「基础道路网络构建」。");
        }
        else
        {
            var lines = ReadRoadCenterlines();
            var haulRoads = ReadLandedHaulRoads();
            lines.AddRange(HaulRoadImporter.ToPolylines(haulRoads));
            if (lines.Count == 0)
            { EditEcho("路网预览：未找到道路中线（图层「点云_道路中心线」/「运输坑线_预览」/「坑线落地」共享中线或选集为空）。先「提取道路中心线」。", EchoLevel.Warn); return; }
            g = RoadGraphBuilder.FromPolylines(lines, snapToleranceM: RoadDefaultSnapToleranceM);
            StampHaulRoads(g, haulRoads);
            rs.Graph = g;
        }
        if (g.NodeCount < 2) { EditEcho("路网预览：中线退化，未能建出路网。", EchoLevel.Warn); return; }
        BeginChange();
        RoadRenderNetworkPreview(g);
    }

    /// <summary>把一张路网图按拓扑分类画进「路网_预览」图层（先清旧再画）。供「路网预览」命令与「路网存档」载入后自动展示共用。</summary>
    private void RoadRenderNetworkPreview(RoadGraph g)
    {
        RoadClearLayer(RoadNetworkPreviewLayer);
        var topo = RoadTopology.Analyze(g);
        RoadEnsureLayer(RoadNetworkPreviewLayer, RoadSymbology.Trunk, RoadSymbology.NetworkTrunkLineweight);

        foreach (var s in topo.Segments)
        {
            var ln = s.Polyline;
            if (ln.Count < 2) continue;
            RoadAddPoly(RoadNetworkPreviewLayer, ln, RoadSymbology.ColorOf(s.Class));
        }
        foreach (var n in g.Nodes)
        {
            double x = n.Position.X, y = n.Position.Y, z = n.Position.Z;
            if (n.Type == RoadNodeType.Loading) { RoadWriteMarkerGlyph(RoadNetworkPreviewLayer, "source", x, y, z, 14, RoadSymbology.Loading); continue; }
            if (n.Type == RoadNodeType.Unloading) { RoadWriteMarkerGlyph(RoadNetworkPreviewLayer, "dump", x, y, z, 14, RoadSymbology.Unloading); continue; }
            switch (topo.NodeClass.GetValueOrDefault(n.Id))
            {
                case RoadNodeClass.Tee:
                case RoadNodeClass.Multi:
                    RoadAddPoly(RoadNetworkPreviewLayer, RoadRingFlat(x, y, z, 7, 12), RoadSymbology.Junction, true);
                    break;
                case RoadNodeClass.Endpoint:
                case RoadNodeClass.Isolated:
                    RoadAddPoly(RoadNetworkPreviewLayer, RoadRingFlat(x, y, z, 11, 14), RoadSymbology.Gap, true);
                    RoadAddPoint(RoadNetworkPreviewLayer, x, y, z, RoadSymbology.Gap);
                    break;
            }
        }
        RefreshScene();
        EditEcho($"✓ 路网预览已开：{topo.Summary}（{g.EdgeCount} 条碎边在 {topo.SeamCount} 个接缝处串成 {topo.Segments.Count} 条路段）");
        EditEcho("   图例：" + string.Join(" · ", Enum.GetValues<RoadSegmentClass>().Select(c =>
                     $"{RoadTopology.TextOf(c)}={RoadSegLegend(c)} {topo.SegmentLengthByClass[(int)c] / 1000:F1}km"
                     + $"({topo.SegmentLengthByClass[(int)c] / Math.Max(1e-9, topo.TotalLengthM) * 100:F0}%)"))
                 + " · 路口白环 · 悬挂端点红环（接缝不标）。再点一次关闭。",
            topo.ComponentCount <= 1 && topo.DangleCount == 0 ? EchoLevel.Success : EchoLevel.Warn);
        StatusMsg.Text = $"路网预览：{topo.Summary}";
    }

    private static string RoadSegLegend(RoadSegmentClass c) => c switch { RoadSegmentClass.Trunk => "绿", RoadSegmentClass.Spur => "灰蓝", _ => "红" };

    private void RoadClearNetworkPreview() => RoadClearLayer(RoadNetworkPreviewLayer);

    // ═══════════════════════ 路网更新（演化锁定） ═══════════════════════

    /// <summary>「路网更新」：吃「演化对比」结果——延拓+保持中线并入、废除丢弃——重建新一期会话路网并存快照。</summary>
    private async Task RoadUpdateNetworkAsync()
    {
        var rs = RS;
        if (rs.Evolution is null || rs.Evolution.Routes.Count == 0)
        {
            EditEcho("路网更新：先用「演化对比」算出两期差异,再更新。", EchoLevel.Warn);
            await RoadInfoAsync("路网更新", "路网更新需要先有「演化对比」结果。\n请先「时段快照」存两期 → 「演化对比」 → 再回来「路网更新」。");
            return;
        }
        // RE12 锁定口径：只并入本期侧的段（保持 / 移位 / 延拓 / 新建入网）；上期侧的段（截短 / 已废除）是"没了的那一截"。
        var lines = new List<IReadOnlyList<Point3d>>();
        int ext = 0, keep = 0, shift = 0, shorten = 0, abol = 0;
        double extM = 0, goneM = 0;
        foreach (var r in rs.Evolution.Routes)
        {
            if (r.Class is RoadEvolutionClass.Abolish or RoadEvolutionClass.Shorten)
            {
                if (r.Class == RoadEvolutionClass.Abolish) abol++; else shorten++;
                goneM += r.SegLenM;
                continue;
            }
            if (r.Side != EvolutionSide.Curr) continue;
            if (r.Class == RoadEvolutionClass.Extend) { ext++; extM += r.SegLenM; }
            else if (r.Class == RoadEvolutionClass.Shift) shift++;
            else keep++;
            if (r.DisplayCenterline.Count >= 2) lines.Add(r.DisplayCenterline);
        }
        if (lines.Count == 0) { EditEcho("路网更新：没有可保留的中线(全废除?)。", EchoLevel.Warn); return; }

        var graph = RoadGraphBuilder.FromPolylines(lines, snapToleranceM: RoadDefaultSnapToleranceM);
        var injectRep = RoadInjectLoadUnloadIntoGraph(graph);
        if (injectRep.Length > 0) EditEcho("　" + injectRep);
        var repNew = graph.Validate();
        string oldComp = rs.Graph != null ? rs.Graph.Validate().ComponentCount.ToString() : "-";
        string summary = $"锁定演化：延拓 {ext} 段(+{extM:F0} m) / 移位 {shift} 段 / 保持 {keep} 段；移除 截短 {shorten} 段 + 废除 {abol} 条(−{goneM:F0} m) → {graph.NodeCount} 节点 / {graph.EdgeCount} 边；连通分量 {oldComp} → {repNew.ComponentCount}";
        summary += Environment.NewLine + rs.Evolution.Ledger.Text;
        var topoNew = RoadTopology.Analyze(graph);
        summary += $"\n新网拓扑：{topoNew.Summary}";
        if (rs.Graph != null)
        {
            string d = RoadTopology.DescribeDelta(RoadTopology.Analyze(rs.Graph), topoNew);
            if (d.Length > 0) summary += $"\n相对当前网：{d}";
        }
        string? warning = repNew.IsFullyConnected ? null : $"锁定后路网未全连通（{repNew.ComponentCount} 个独立片）。";
        if (await RoadApplyNetworkUpdateAsync(graph, rs.CurrentNetworkName, "演化", summary, warning))
            EditEcho($"✓ {summary}。寻径/运距即基于新网。", EchoLevel.Success);
    }

    /// <summary>统一提交闸门：弹 UpdateNetworkDialog → 当前路网更新（同名覆盖，旧网转快照留底）/ 增量路网更新（另存新命名）。提交成功设为当前图、入快照、重绘正式预览。</summary>
    private async Task<bool> RoadApplyNetworkUpdateAsync(RoadGraph result, string? baseArchiveName, string kindTag, string summary, string? warning)
    {
        string defName = (baseArchiveName ?? "路网") + $" +{kindTag} {DateTime.Now:MM-dd HH:mm}";
        var dlg = new UpdateNetworkDialog(summary, baseArchiveName, defName, warning);
        if (!await dlg.AskAsync(this)) return false;
        var rs = RS;
        if (rs.Graph != null)
            rs.Snapshots.Add(new RoadSnapshot($"{baseArchiveName ?? "路网"} 更新前 {DateTime.Now:MM-dd HH:mm}", rs.Graph.Clone()));
        rs.Graph = result;
        string committedName;
        if (dlg.Mode == NetworkUpdateMode.UpdateCurrent)
        {
            committedName = baseArchiveName ?? "路网";
            if (baseArchiveName != null) RoadPersistNetwork(baseArchiveName, result);
        }
        else
        {
            committedName = dlg.NewName;
            RoadPersistNetwork(dlg.NewName, result);
        }
        rs.CurrentNetworkName = committedName;
        rs.Snapshots.Add(new RoadSnapshot(committedName, result.Clone()));
        if (result.NodeCount >= 2) { BeginChange(); RoadRenderNetworkPreview(result); }
        return true;
    }

    // ═══════════════════════ 增量增删边 / 边状态（暂存式编辑） ═══════════════════════

    private void RoadEditEdgeCmd() => _ = RoadEnsureEditSessionAsync();

    /// <summary>「边状态」：进入暂存编辑 → 取一点标记最近路段状态（开放/检修/封闭循环）。</summary>
    private async Task RoadEdgeStatusCmdAsync()
    {
        if (!await RoadEnsureEditSessionAsync()) return;
        await RoadEditSetStatusAsync();
    }

    /// <summary>确保处于暂存编辑状态：已在编辑则激活面板；否则按当前网新建会话+开面板。返回是否就绪。</summary>
    private async Task<bool> RoadEnsureEditSessionAsync()
    {
        var rs = RS;
        if (rs.EditWin != null) { rs.EditWin.Activate(); return rs.Edit != null; }
        var graph = TryBuildRoadGraph();
        if (graph is null || graph.NodeCount < 1)
        { EditEcho("路网编辑：当前无路网。先「基础道路网络构建」或「路网存档」载入。", EchoLevel.Warn); await Task.CompletedTask; return false; }

        rs.EditJustCommitted = false;
        rs.Edit = new RoadEditSession(graph, rs.CurrentNetworkName);
        var handlers = new RoadEditHandlers
        {
            AddEdge = () => _ = RoadEdEditAddEdgeAsync(),
            RemoveEdge = () => _ = RoadEditRemoveEdgeAsync(),
            SplitEdge = () => _ = RoadEditSplitEdgeAsync(),
            SetStatus = () => _ = RoadEditSetStatusAsync(),
            SetRoadClass = () => _ = RoadEditSetRoadClassAsync(),
            Undo = () => { if (RS.Edit?.Undo() == true) { RoadRenderEditDiff(); RS.EditWin?.RefreshFromSession(); EditEcho("↶ 已撤销上一步"); RoadEchoTopologyDelta(); } },
            Clear = () => { RS.Edit?.Clear(); RoadRenderEditDiff(); RS.EditWin?.RefreshFromSession(); EditEcho("已清空暂存变更（拓扑已回到进编辑时的样子）"); },
            Commit = () => _ = RoadEdEditCommitAsync(),
        };
        var win = new RoadEditWindow(rs.Edit, handlers);
        rs.EditWin = win;
        win.Exited += () =>
        {
            var s = RS;
            s.EditWin = null; s.Edit = null;
            RoadClearEditDiff();
            if (!s.EditJustCommitted) EditEcho("路网编辑：已退出（未提交的变更已放弃）。");
            s.EditJustCommitted = false;
        };
        win.Show(this);
        EditEcho("路网编辑：已进入暂存编辑。选工具→在视口操作；变更先暂存，点「更新路网」才锁定。");
        return true;
    }

    private async Task RoadEdEditAddEdgeAsync()
    {
        var s = RS.Edit; if (s is null) return;
        var pq = await RoadPickTwoPointsAsync("加边");
        if (pq == null) return;
        var a = s.Draft.NearestNode(pq.Value.a, 30.0);
        var b = s.Draft.NearestNode(pq.Value.b, 30.0);
        if (a is null || b is null || a.Id == b.Id) { EditEcho("加边：两点须落在不同的已有节点附近(≤30m)。", EchoLevel.Warn); return; }
        var id = s.NewEdgeId();
        s.Apply(new RoadChange { Kind = RoadChangeKind.AddEdge, EdgeId = id, FromNodeId = a.Id, ToNodeId = b.Id, Centerline = new[] { a.Position, b.Position }, Title = $"{id}（{a.Id}—{b.Id}）" });
        RoadAfterEdit($"✓ 暂存·加边 {id}（{a.Id}—{b.Id}）");
    }

    private async Task RoadEditRemoveEdgeAsync()
    {
        var s = RS.Edit; if (s is null) return;
        var p = await RoadPickOnePointAsync("删边：点路段（≤30 m，Esc 取消）");
        if (p == null) return;
        var e = s.Draft.NearestEdge(p.Value, 30.0);
        if (e is null) { EditEcho("删边：附近无路段(≤30m)。", EchoLevel.Warn); return; }
        s.Apply(new RoadChange { Kind = RoadChangeKind.RemoveEdge, EdgeId = e.Id, Title = e.Id });
        RoadAfterEdit($"✓ 暂存·删边 {e.Id}");
    }

    private async Task RoadEditSplitEdgeAsync()
    {
        var s = RS.Edit; if (s is null) return;
        var p = await RoadPickOnePointAsync("插交叉口：点路段上要打断的位置（≤30 m，Esc 取消）");
        if (p == null) return;
        var e = s.Draft.NearestEdge(p.Value, 30.0);
        if (e is null) { EditEcho("插交叉口：附近无路段(≤30m)。", EchoLevel.Warn); return; }
        var nid = s.NewNodeId();
        s.Apply(new RoadChange { Kind = RoadChangeKind.SplitEdge, EdgeId = e.Id, At = p.Value, NewNodeId = nid, Title = $"{e.Id}→{nid}" });
        RoadAfterEdit($"✓ 暂存·在 {e.Id} 插交叉口 {nid}");
    }

    private async Task RoadEditSetStatusAsync()
    {
        var s = RS.Edit; if (s is null) return;
        var p = await RoadPickOnePointAsync("改状态：点路段（开放→检修→封闭 循环，Esc 取消）");
        if (p == null) return;
        var e = s.Draft.NearestEdge(p.Value, 30.0);
        if (e is null) { EditEcho("改状态：附近无路段(≤30m)。", EchoLevel.Warn); return; }
        var next = e.Status switch { RoadEdgeStatus.Open => RoadEdgeStatus.Maintenance, RoadEdgeStatus.Maintenance => RoadEdgeStatus.Closed, _ => RoadEdgeStatus.Open };
        s.Apply(new RoadChange { Kind = RoadChangeKind.SetStatus, EdgeId = e.Id, Status = next, Title = $"{e.Id}→{RoadStatusText(next)}" });
        RoadAfterEdit($"✓ 暂存·{e.Id} 状态→{RoadStatusText(next)}");
    }

    /// <summary>改线路类型（R-T7 人工改判）：找到边所属的整条路段 → 自动/干线/支线/孤立段 循环切换，落到该路段每一条边。</summary>
    private async Task RoadEditSetRoadClassAsync()
    {
        var s = RS.Edit; if (s is null) return;
        var p = await RoadPickOnePointAsync("线路类型：点路段（自动→干线→支线→孤立段 循环，Esc 取消）");
        if (p == null) return;
        var e = s.Draft.NearestEdge(p.Value, 30.0);
        if (e is null) { EditEcho("改线路类型：附近无路段(≤30m)。", EchoLevel.Warn); return; }
        var topo = s.DraftTopology();
        if (!topo.SegmentByEdge.TryGetValue(e.Id, out var seg)) { EditEcho($"改线路类型：边 {e.Id} 未归入任何路段（图退化？）。", EchoLevel.Warn); return; }
        RoadSegmentClass? next = e.RoadClass switch
        {
            null => RoadSegmentClass.Trunk,
            RoadSegmentClass.Trunk => RoadSegmentClass.Spur,
            RoadSegmentClass.Spur => RoadSegmentClass.Isolated,
            _ => null,
        };
        s.Apply(new RoadChange
        {
            Kind = RoadChangeKind.SetRoadClass, EdgeId = e.Id, EdgeIds = seg.EdgeIds, RoadClass = next,
            Title = $"{seg.Id}（{seg.EdgeIds.Count} 边 / {seg.LengthM:F0}m）→{RoadClassTextR(next)}",
        });
        RoadAfterEdit($"✓ 暂存·路段 {seg.Id} 线路类型→{RoadClassTextR(next)}（{seg.EdgeIds.Count} 条边 / {seg.LengthM:F0}m；自动判据为「{RoadTopology.TextOf(seg.AutoClass)}」）");
    }

    private void RoadAfterEdit(string msg)
    {
        RoadRenderEditDiff();
        RS.EditWin?.RefreshFromSession();
        EditEcho(msg, EchoLevel.Success);
        RoadEchoTopologyDelta();
    }

    private void RoadEchoTopologyDelta()
    {
        var s = RS.Edit;
        if (s is null) return;
        string structural = s.TopologyDelta();
        if (structural.Length > 0) EditEcho($"   拓扑（结构）：{structural}");
        string passable = s.TopologyDelta(passableOnly: true);
        if (passable.Length > 0) EditEcho($"   拓扑（可通行，检修/封闭按不通算）：{passable}", EchoLevel.Warn);
    }

    /// <summary>「更新路网」（编辑提交）：算 diff 摘要 → 走统一提交闸门锁定 → 关编辑窗。</summary>
    private async Task RoadEdEditCommitAsync()
    {
        var s = RS.Edit; if (s is null) return;
        if (!s.HasChanges) { EditEcho("更新路网：没有可提交的变更。", EchoLevel.Warn); return; }
        var diff = s.ComputeDiff();
        var repNew = s.Draft.Validate();
        var repOld = s.BaseSnapshot.Validate();
        string summary = $"+{diff.Added.Count} 边 / −{diff.Removed.Count} 边 / {diff.StatusChanged.Count} 改状态 / {diff.Modified.Count} 改线 / {diff.ClassChanged.Count} 改类型；连通分量 {repOld.ComponentCount} → {repNew.ComponentCount}";
        string topoStruct = s.TopologyDelta();
        string topoPass = s.TopologyDelta(passableOnly: true);
        if (topoStruct.Length > 0) summary += $"\n拓扑（结构）：{topoStruct}";
        if (topoPass.Length > 0) summary += $"\n拓扑（可通行，检修/封闭按不通算）：{topoPass}";
        string? warning = repNew.IsFullyConnected ? null : $"提交后路网未全连通（{repNew.ComponentCount} 个独立片）。";
        if (!await RoadApplyNetworkUpdateAsync(s.Draft, s.BaseArchiveName, "编辑", summary, warning)) return;
        EditEcho($"✓ 路网编辑已锁定（{summary}）。寻径/运距即基于新网。", EchoLevel.Success);
        RS.EditJustCommitted = true;
        RS.EditWin?.Close();
    }

    /// <summary>把会话 diff 画进「路网_编辑预览」层（新增绿＋ / 删除红× / 改状态黄 / 改线橙 / 改判=改成的类别色◇）。先清旧。</summary>
    private void RoadRenderEditDiff()
    {
        RoadClearEditDiff();
        var s = RS.Edit;
        if (s is null) return;
        var diff = s.ComputeDiff();
        if (diff.IsEmpty) { RefreshScene(); return; }
        RoadEnsureLayer(RoadSymbology.EditDiffLayer, RoadSymbology.DiffAdd, 2.2f);
        foreach (var e in diff.Added) RoadWriteDiffEdge(e, RoadSymbology.DiffAdd, '+');
        foreach (var e in diff.Removed) RoadWriteDiffEdge(e, RoadSymbology.DiffRemove, 'x');
        foreach (var e in diff.StatusChanged) RoadWriteDiffEdge(e, RoadSymbology.DiffStatus, '\0');
        foreach (var e in diff.Modified) RoadWriteDiffEdge(e, RoadSymbology.DiffModify, '\0');
        foreach (var e in diff.ClassChanged) RoadWriteDiffEdge(e, e.RoadClass is { } rc ? RoadSymbology.ColorOf(rc) : RoadSymbology.DiffKeep, 'd');
        RefreshScene();
    }

    private void RoadWriteDiffEdge(RoadEdge e, Rgb rgb, char marker)
    {
        var c = e.Centerline;
        if (c.Count < 2) return;
        string layer = RoadSymbology.EditDiffLayer;
        RoadAddPoly(layer, c, rgb);
        var m = c[c.Count / 2];
        const double s = 6.0;
        if (marker == 'x')
        {
            RoadAddLine(layer, m.X - s, m.Y - s, m.Z, m.X + s, m.Y + s, m.Z, rgb);
            RoadAddLine(layer, m.X - s, m.Y + s, m.Z, m.X + s, m.Y - s, m.Z, rgb);
        }
        else if (marker == '+')
        {
            RoadAddLine(layer, m.X - s, m.Y, m.Z, m.X + s, m.Y, m.Z, rgb);
            RoadAddLine(layer, m.X, m.Y - s, m.Z, m.X, m.Y + s, m.Z, rgb);
        }
        else if (marker == 'd')
            RoadAddPoly(layer, new[] { new Point3d(m.X, m.Y - s, m.Z), new Point3d(m.X + s, m.Y, m.Z), new Point3d(m.X, m.Y + s, m.Z), new Point3d(m.X - s, m.Y, m.Z) }, rgb, true);
    }

    private void RoadClearEditDiff()
    {
        if (RoadClearLayer(RoadSymbology.EditDiffLayer) > 0) RefreshScene();
    }

    // ═══════════════════════ 时段快照 / 路网存档 ═══════════════════════

    /// <summary>「时段快照」(W7)：开管理窗——存当前为快照 / 改名 / 转为当前路网 / 对比两期差异 / 删除。</summary>
    private void RoadSnapshotCmd()
    {
        var win = new SnapshotManagerWindow(RS.Snapshots, RoadSnapshotCurrent, RoadLoadSnapshotAsCurrent);
        win.Show(this);
        StatusMsg.Text = $"时段快照：已有 {RS.Snapshots.Count} 期（存当前 / 改名 / 转为当前路网 / 对比两期 / 删除）";
    }

    /// <summary>把当前会话路网图深拷贝存为一期命名快照，返回新快照（无图返 null）。</summary>
    private RoadSnapshot? RoadSnapshotCurrent()
    {
        var graph = TryBuildRoadGraph();
        if (graph is null) return null;
        var rs = RS;
        var snap = new RoadSnapshot($"快照 {rs.Snapshots.Count + 1}", graph.Clone());
        rs.Snapshots.Add(snap);
        EditEcho($"✓ 已存时段快照「{snap.Name}」（{snap.Graph.NodeCount} 节点 / {snap.Graph.EdgeCount} 边）", EchoLevel.Success);
        return snap;
    }

    /// <summary>「转为当前路网」：把某期快照的图（深拷贝，保持快照冻结）回灌为会话图并重画预览。</summary>
    private void RoadLoadSnapshotAsCurrent(RoadSnapshot snap)
    {
        if (snap is null) return;
        var graph = snap.Graph.Clone();
        RS.Graph = graph;
        BeginChange();
        if (graph.NodeCount >= 2) RoadRenderNetworkPreview(graph);
        else { RoadClearNetworkPreview(); RefreshScene(); }
        EditEcho($"✓ 已把快照「{snap.Name}」转为当前路网：{graph.NodeCount} 节点 / {graph.EdgeCount} 边。寻径/运距均基于此图。", EchoLevel.Success);
    }

    /// <summary>「路网存档」：非模态管理窗，列出各时期持久化路网，载入为当前图（并入快照供演化对比）/改名/删除。</summary>
    private void RoadArchiveCmd()
    {
        var db = EnsureGeoDb();
        if (db == null) { EditEcho("路网存档：工程库未连接（连上后自动重跑本命令）。", EchoLevel.Warn); StatusMsg.Text = "路网存档：工程库未连接，连上后自动重开"; return; }
        var win = new RoadNetworkArchiveWindow(() => _geoDb?.Connection, entity =>
        {
            var graph = RoadGraphSerializer.FromJson(entity.GraphJson);
            var rs = RS;
            rs.Graph = graph;
            rs.CurrentNetworkName = entity.Name;
            rs.Snapshots.Add(new RoadSnapshot(entity.Name, graph.Clone()));
            EditEcho($"✓ 已载入路网「{entity.Name}」[{entity.CapturedAt}]：{graph.NodeCount} 节点 / {graph.EdgeCount} 边，设为当前图并入快照 #{rs.Snapshots.Count}。寻径/运距均基于此图。", EchoLevel.Success);
            BeginChange();
            if (graph.NodeCount >= 2) RoadRenderNetworkPreview(graph);
            else { RoadClearNetworkPreview(); RefreshScene(); }
        });
        win.Show(this);
        StatusMsg.Text = "路网存档：列出随工程持久化的各期路网（载入为当前图 / 改名 / 删除）";
    }

    // ═══════════════════════ 演化对比 / 分色显示 ═══════════════════════

    /// <summary>「演化对比」(算)：选两期路网快照 → 几何匹配判延拓/废除/保持 → 对比清单 + 改判 + 导出。结果存会话供「分色显示」上图。</summary>
    private async Task RoadEvolutionCompareAsync()
    {
        var rs = RS;
        if (rs.Snapshots.Count < 2)
        {
            RoadSnapshotCurrent();
            string msg = $"演化对比需要 ≥2 期路网快照。\n当前已有 {rs.Snapshots.Count} 期。请在不同开采期分别「时段快照」（上期、本期），再来对比。";
            EditEcho(msg.Replace("\n", " "), EchoLevel.Warn);
            await RoadInfoAsync("演化对比", msg);
            return;
        }
        var evoOpt = RoadEvolutionOptionsFromSettings();
        var win = new EvolutionCompareWindow(rs.Snapshots, evoOpt, result =>
        {
            RS.Evolution = result;
            EditEcho($"✓ 演化对比完成：{result.Summary}。用「分色显示」上图查看。", EchoLevel.Success);
        });
        win.Show(this);
    }

    /// <summary>演化阈值以 W8「延拓触发设置」的持久默认为初值（原 ExtendTriggerSettings.ToEvolutionOptions；Kylin 的设置类在 Cad.Transport，此处转成 Road 侧的选项）。</summary>
    private static PitMine3D.Kylin.Cad.Road.RoadEvolutionOptions RoadEvolutionOptionsFromSettings()
    {
        var s = Cad.Transport.ExtendTriggerSettings.Load();
        double rad = s.AdvanceAzimuthDeg * Math.PI / 180.0;
        return new PitMine3D.Kylin.Cad.Road.RoadEvolutionOptions
        {
            SampleStepM = s.SampleStepM, MatchToleranceM = s.MatchToleranceM, MatchAngleDeg = s.MatchAngleDeg,
            NewCoverFrac = s.NewCoverFrac, GoneCoverFrac = s.GoneCoverFrac, GrowMinLenM = s.GrowMinLenM, ShiftThresholdM = s.ShiftThresholdM,
            AdvanceDirXY = s.UseAdvanceDir ? (Math.Sin(rad), Math.Cos(rad)) : null,
        };
    }

    /// <summary>「分色显示」(看)：读「演化对比」会话结果，按勾选类别上图/清除视口分色 overlay。</summary>
    private void RoadEvolutionDisplayCmd()
    {
        var win = new EvolutionDisplayWindow(() => RS.Evolution, RoadDrawEvolutionOverlay, () => { BeginChange(); RoadClearEvolutionOverlay(); RefreshScene(); });
        win.Show(this);
    }

    /// <summary>把演化判定结果按类别分色画到 overlay 图层（延拓绿＋ / 截短红橙× / 废除红× / 移位橙◇ / 保持灰细线），先清旧。</summary>
    private void RoadDrawEvolutionOverlay(RoadEvolutionResult result, ISet<RoadEvolutionClass> show)
    {
        BeginChange();
        RoadClearEvolutionOverlay();
        RoadEnsureLayer(RoadEvoExtendLayer, RoadSymbology.DiffAdd, 1.8f);
        RoadEnsureLayer(RoadEvoShortenLayer, RoadSymbology.DiffShorten, 1.5f);
        RoadEnsureLayer(RoadEvoAbolishLayer, RoadSymbology.DiffRemove, 1.2f);
        RoadEnsureLayer(RoadEvoShiftLayer, RoadSymbology.DiffModify, 1.5f);
        RoadEnsureLayer(RoadEvoKeepLayer, RoadSymbology.DiffKeep, 0.3f);
        foreach (var r in result.Routes)
        {
            if (!show.Contains(r.Class)) continue;
            var cl = r.DisplayCenterline;
            if (cl.Count < 2) continue;
            var (layer, color) = r.Class switch
            {
                RoadEvolutionClass.Extend => (RoadEvoExtendLayer, RoadSymbology.DiffAdd),
                RoadEvolutionClass.Shorten => (RoadEvoShortenLayer, RoadSymbology.DiffShorten),
                RoadEvolutionClass.Abolish => (RoadEvoAbolishLayer, RoadSymbology.DiffRemove),
                RoadEvolutionClass.Shift => (RoadEvoShiftLayer, RoadSymbology.DiffModify),
                _ => (RoadEvoKeepLayer, RoadSymbology.DiffKeep),
            };
            RoadAddPoly(layer, cl, color);
            var m = cl[cl.Count / 2];
            const double sz = 7.0;
            if (r.Class == RoadEvolutionClass.Extend)
            {
                RoadAddLine(layer, m.X - sz, m.Y, m.Z, m.X + sz, m.Y, m.Z, color);
                RoadAddLine(layer, m.X, m.Y - sz, m.Z, m.X, m.Y + sz, m.Z, color);
            }
            else if (r.Class is RoadEvolutionClass.Abolish or RoadEvolutionClass.Shorten)
            {
                RoadAddLine(layer, m.X - sz, m.Y - sz, m.Z, m.X + sz, m.Y + sz, m.Z, color);
                RoadAddLine(layer, m.X - sz, m.Y + sz, m.Z, m.X + sz, m.Y - sz, m.Z, color);
            }
            else if (r.Class == RoadEvolutionClass.Shift)
                RoadAddPoly(layer, new[] { new Point3d(m.X, m.Y - sz, m.Z), new Point3d(m.X + sz, m.Y, m.Z), new Point3d(m.X, m.Y + sz, m.Z), new Point3d(m.X - sz, m.Y, m.Z) }, color, true);
        }
        RefreshScene();
        EditEcho($"演化分色显示（增删高亮·未变退背景）：{result.Summary}（仅勾选类别）", EchoLevel.Success);
    }

    private void RoadClearEvolutionOverlay()
    {
        foreach (var ly in new[] { RoadEvoExtendLayer, RoadEvoShortenLayer, RoadEvoAbolishLayer, RoadEvoShiftLayer, RoadEvoKeepLayer }) RoadClearLayer(ly);
    }

    // ═══════════════════════ 破碎站位置设置 · 保存后的接线（原第②③步） ═══════════════════════

    /// <summary>
    /// 「破碎站位置设置」窗保存后调用：② 共享 user 设置按【全表】重建（供运量驱动布线读）③ 会话图按【全表】整组替换源/汇节点（接入支线）+ 视口旗标。
    /// 库不可用时拿共享设置里的点当底，只做增/改不做删。（窗口本身与第①步落库在 Views/GeoDb/CrusherStationWindow + Data/CrusherStore。）
    /// </summary>
    private void RoadAfterCrusherSaved(IReadOnlyList<(string Id, double X, double Y, double Z, double Tph)> rows, int ins, int upd, int del, bool persisted)
    {
        var rs = RS;
        var graph = rs.Graph;
        if (graph is null) { graph = new RoadGraph(); rs.Graph = graph; EditEcho("破碎站位置设置：当前无路网图，已新建空图录入。"); }

        var all = Data.LoadUnloadPointStore.LoadAll(_geoDb?.Connection);
        if (all.Count == 0)
        {
            LoadUnloadPointSet? stored = null;
            try { stored = UserSettings.Current.Get<LoadUnloadPointSet>(LoadUnloadPointSet.SettingsKey); } catch { }
            if (stored != null)
                foreach (var p in stored.Points)
                    all.Add(new Data.LoadUnloadRecord { Name = p.Id, Kind = p.IsLoading ? "loading" : "unloading", X = p.X, Y = p.Y, Z = p.Z, ThroughputTph = p.ThroughputTph });
            foreach (var r in rows)
            {
                var hit = all.FirstOrDefault(p => string.Equals(p.Name, r.Id, StringComparison.Ordinal));
                if (hit != null) { hit.Kind = "unloading"; hit.UnloadSub = "crusher"; hit.X = r.X; hit.Y = r.Y; hit.Z = r.Z; hit.ThroughputTph = r.Tph; }
                else all.Add(new Data.LoadUnloadRecord { Name = r.Id, Kind = "unloading", UnloadSub = "crusher", X = r.X, Y = r.Y, Z = r.Z, ThroughputTph = r.Tph });
            }
        }

        // ② 共享 user 设置：按全表重建
        var set = new LoadUnloadPointSet();
        foreach (var p in all) set.Points.Add(new LoadUnloadPoint { Id = p.Name, IsLoading = p.IsLoading, X = p.X, Y = p.Y, Z = p.Z, ThroughputTph = p.ThroughputTph });
        try { UserSettings.Current.Set(LoadUnloadPointSet.SettingsKey, set); UserSettings.Current.Flush(); } catch { }

        // ③ 会话图：按全表整组替换源/汇节点
        var oldIds = graph.Nodes.Where(n => n.Type == RoadNodeType.Loading || n.Type == RoadNodeType.Unloading).Select(n => n.Id).ToList();
        foreach (var id in oldIds) graph.RemoveNode(id);
        int src = 0, sink = 0, crusher = 0, attached = 0, isolated = 0;
        var longStubs = new List<string>();
        foreach (var p in all)
        {
            var type = p.IsLoading ? RoadNodeType.Loading : RoadNodeType.Unloading;
            double stub = RoadAttachLoadUnload(graph, new Point3d(p.X, p.Y, p.Z), p.Name, type, p.ThroughputTph, snapTolM: 5.0, maxStubM: DepotMaxStubM);
            if (stub < 0) isolated++;
            else { attached++; if (stub > DepotWarnStubM) longStubs.Add($"{p.Name}({stub:F0}m)"); }
            if (p.IsLoading) src++;
            else { sink++; if (Data.CrusherStore.IsCrusher(p.Kind, p.UnloadSub)) crusher++; }
        }
        EditEcho($"✓ 破碎站已保存：{rows.Count} 座（新增 {ins} / 更新 {upd} / 删除 {del}）。路网现有源汇 {src} 采剥点 / {sink} 卸载点（其中破碎站 {crusher}），接入 {attached}"
                 + (isolated > 0 ? $"，孤立 {isolated}（附近无路·校验会标出）" : "")
                 + (persisted ? "。已随工程持久化" : "。⚠ 未入库（工程库未连接）")
                 + (src == 0 ? "。提示：尚无采剥点(源)，寻径模式①与运量驱动布线仍缺源" : ""),
            persisted && isolated == 0 ? EchoLevel.Success : EchoLevel.Warn);
        if (longStubs.Count > 0)
            EditEcho($"⚠ {longStubs.Count} 个点离最近道路较远（>{DepotWarnStubM:F0}m），已建较长接入支线，请核实位置：{string.Join("、", longStubs)}", EchoLevel.Warn);
        BeginChange();
        RoadHighlightLoadUnloadPoints(all);
    }
}
