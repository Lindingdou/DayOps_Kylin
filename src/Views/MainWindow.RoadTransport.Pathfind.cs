using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Views.Road;
using Point3d = PitMine3D.Kylin.Cad.Road.Point3d;
using RoadGraph = PitMine3D.Kylin.Cad.Road.RoadGraph;
using RoadNode = PitMine3D.Kylin.Cad.Road.RoadNode;
using RoadEdge = PitMine3D.Kylin.Cad.Road.RoadEdge;
using RoadNodeType = PitMine3D.Kylin.Cad.Road.RoadNodeType;
using RoadConnectivity = PitMine3D.Kylin.Cad.Road.RoadConnectivity;
using DijkstraPathSolver = PitMine3D.Kylin.Cad.Road.DijkstraPathSolver;
using TransportIndicatorsBuilder = PitMine3D.Kylin.Cad.Road.TransportIndicatorsBuilder;

namespace PitMine3D.Kylin.Views;

/// <summary>道路运输系统 · 寻径与运距：点对点寻径 · 等效运距 · 运输指标报表。忠实原 RoadLibPlugin 对应命令（口径统一走 HaulCaliper / 解算统一走 HaulSolveKernel）。</summary>
public partial class MainWindow
{
    /// <summary>「点对点寻径」：弹模式窗（口径可选）→①按装卸点选起终点 或 ②视口取两点 → 往返两条腿 → 明细窗 + 双色高亮。</summary>
    private async Task RoadPointToPointAsync()
    {
        var graph = TryBuildRoadGraph();
        if (graph is null || graph.NodeCount < 2)
        {
            EditEcho("点对点寻径：当前没有可用路网图。", EchoLevel.Warn);
            await RoadInfoAsync("点对点寻径",
                "还没有可以寻径的路网。\n\n路网不会自动从工程里载入，请先选一条：\n"
                + "  ·「路网存档」→ 选一期载入为当前路网（已建好的直接用）；\n"
                + "  ·「基础道路网络构建」→ 从图上的道路中心线现建一张；\n"
                + "  ·「提取道路中心线」/「手动标定线路」→ 图上还没有中线时先把线画出来。");
            return;
        }
        var loads = graph.Nodes.Where(n => n.Type == RoadNodeType.Loading).Select(n => n.Id).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var unloads = graph.Nodes.Where(n => n.Type == RoadNodeType.Unloading).Select(n => n.Id).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var cal0 = RoadCaliper();
        var dlg = new PathSearchModeWindow(loads, unloads, cal0);
        await dlg.ShowDialog(this);
        var cal = cal0.WithMode(dlg.Mode);
        switch (dlg.Result)
        {
            case PathSearchModeWindow.PickMode.ByLoadUnload:
                void SolveByIds() => RoadRunAndShowHaulPair(RS.Graph ?? graph, dlg.FromId!, dlg.ToId!, cal, SolveByIds);
                SolveByIds();
                break;
            case PathSearchModeWindow.PickMode.ByViewportPoints:
                var pq = await RoadPickTwoPointsAsync("点对点寻径");
                if (pq != null) RoadRunPointToPointFromPicks(pq.Value.a, pq.Value.b, cal);
                break;
        }
    }

    /// <summary>视口取到两点后的整条链路：克隆会话图 → 两端各投影到路面插临时点 → 解算 → 出窗 + 上图。补完缺口后原样再走一遍。</summary>
    private void RoadRunPointToPointFromPicks(Point3d p, Point3d q, HaulCaliper cal)
    {
        void Solve()
        {
            var session = RS.Graph;
            if (session is null || session.NodeCount < 2) return;
            var work = session.Clone();   // 临时起终点绝不留进会话图
            BeginChange();
            RoadClearPathPreviewLayer();
            var a = RoadSnapToRoad(work, p, cal, "起点", "PICK_A");
            var b = RoadSnapToRoad(work, q, cal, "终点", "PICK_B");
            if (a is null || b is null) return;
            if (a.Id == b.Id)
            {
                EditEcho("起点和终点吸附到了路面上同一处，请把两点分开一些。", EchoLevel.Warn);
                _ = RoadInfoAsync("点对点寻径", "起点和终点吸附到了路面上的同一处，量不出运距。请把两点分开一些。");
                return;
            }
            RoadRunAndShowHaulPair(work, a.Id, b.Id, cal, Solve);
        }
        Solve();
    }

    /// <summary>
    /// 视口点 → 可当起终点的路网节点：投影到最近的边，在投影脚就地打断插一个临时节点（路网节点只长在中线端点上，"吸到最近节点"十次里八次吸不上）。
    /// <paramref name="work"/> 必须是会话图的副本。
    /// </summary>
    private RoadNode? RoadSnapToRoad(RoadGraph work, in Point3d p, HaulCaliper cal, string what, string tempNodeId)
    {
        var e = work.NearestEdgeForPick(p, cal.SnapRadiusM, RoadPickZBandM, out var foot, out double dh, out double dz, out bool zUsed);
        if (e is null)
        {
            work.NearestEdgeForPick(p, double.MaxValue, RoadPickZBandM, out var far, out double dFar, out _, out _);
            string msg = double.IsInfinity(dFar)
                ? $"{what}：路网里一条边都没有。"
                : $"{what}：平面上离最近的路面 {dFar:F0}m，超过吸附上限 {cal.SnapRadiusM:F0}m —— 未吸附。";
            EditEcho(msg + "请点在路上，或用「手动标定线路」把路补到这里。", EchoLevel.Warn);
            if (!double.IsInfinity(dFar)) RoadMarkPickMiss(p, far, dFar, what);
            _ = RoadInfoAsync("点对点寻径", msg + "\n\n已在图上用黄叉标出你点的位置、并连到最近的一段路。\n请点在路面上；如果那里本来就该有路，用「手动标定线路」把它补出来。");
            return null;
        }
        if (!zUsed)
            EditEcho($"{what}：取点 Z={p.Z:F0}m 不在路网标高带内（最近路面 {foot.Z:F0}m，差 {dz:F0}m）——视口取点没命中地形面时拿不到真实标高，本次按**平面位置**吸附。上下叠置的路这一次分不开，若吸错了请把地形/三角网打开再取点。", EchoLevel.Warn);
        if (work.GetNode(e.FromId) is { } na && na.Position.HorizontalDistanceTo(foot) <= RoadNodeReuseTolM) return na;
        if (work.GetNode(e.ToId) is { } nb && nb.Position.HorizontalDistanceTo(foot) <= RoadNodeReuseTolM) return nb;
        var node = work.SplitEdgeAtNearest(e.Id, foot, tempNodeId);
        EditEcho($"{what}：吸附到路段 {e.Id} 的路面上（平面距 {dh:F1}m" + (zUsed ? $"、高差 {dz:F1}m" : "") + $"），就地插临时点 {tempNodeId}。");
        return node;
    }

    /// <summary>解一对源汇的往返 → 命令行汇总 + 明细窗 + 双色高亮。判"物理不连通"时额外算缺口链并上图；全是平接缺口时提供一键补边，补完调 resolveAfterBridge 重解。</summary>
    private void RoadRunAndShowHaulPair(RoadGraph graph, string fromId, string toId, HaulCaliper cal, Action? resolveAfterBridge = null)
    {
        var solver = new DijkstraPathSolver(graph);
        var pair = HaulSolveKernel.Solve(graph, solver, fromId, toId, cal);
        if (pair.Feasible)
            EditEcho($"✓ 寻径成功（{cal.ModeText}）：去程 {pair.Outbound.Path.EdgeIds.Count} 段 · 里程 {pair.Outbound.Path.LengthM:F0}m · 等效 {pair.Outbound.Path.EquivM:F0}m | "
                     + $"往返等效 {pair.RoundTripEquivM:F0}m · 循环 {pair.CycleTimeMin:F1}min · 成本 {(pair.CostPerTripYuan is { } c ? $"{c:F0} 元/趟" : "—（未设单价）")}", EchoLevel.Success);
        else
            EditEcho($"✗ {fromId} ⇄ {toId}：{pair.StatusText} —— {pair.PrimaryDiagnosis?.Text}", EchoLevel.Warn);

        bool disconnected = !pair.Feasible && pair.PrimaryDiagnosis?.Cause == HaulBlockCause.Disconnected;
        RoadBridgePlan? plan = disconnected ? RoadConnectivity.PlanBridge(graph, fromId, toId, RoadMaxBridgeGapM) : null;

        TransportResultWindow.ShowResult(this, $"点对点寻径 {fromId}⇄{toId}",
            "【点对点寻径】" + Environment.NewLine + HaulTextReport.PairBlock(graph, pair, cal) + (plan is null ? "" : Environment.NewLine + RoadBridgePlanBlock(plan)),
            () => { BeginChange(); RoadClearPathHighlight(); },
            new List<(string, Action)> { ("排到哪个卸点最划算", () => RoadOpenEquivHaulWindow(graph, fromId, cal)) });

        BeginChange();
        if (plan is null) RoadHighlightHaulPair(graph, pair);
        else RoadHighlightDisconnected(graph, fromId, toId, plan);
        StatusMsg.Text = pair.Feasible ? $"寻径完成：{fromId}⇄{toId} 往返等效 {pair.RoundTripEquivM:F0}m" : $"寻径：{fromId}⇄{toId} {pair.StatusText}";

        if (plan is { Reachable: true } && plan.AllFlat(RoadBridgeZSepM) && resolveAfterBridge is not null && RS.Graph is not null)
        {
            _ = RoadOfferBridgeAsync(plan, resolveAfterBridge);
        }
        else if (plan is { Reachable: true })
        {
            var steep = plan.Gaps.Where(g => !g.IsFlat(RoadBridgeZSepM)).ToList();
            if (steep.Count > 0)
                EditEcho($"这条断口链里有 {steep.Count} 处是跨标高的（最大高差 {steep.Max(g => Math.Abs(g.DzM)):F0}m），缺的是坡道而不是一段平路 —— 请用「手动标定线路」把坡道画出来。", EchoLevel.Warn);
        }
        else if (plan is not null)
            EditEcho($"起终点分属第 #{plan.SrcComp} / #{plan.DstComp} 片，{RoadMaxBridgeGapM:F0}m 以内找不到任何接法 —— 这两块地之间是真的没有路，得先修一条。", EchoLevel.Warn);
    }

    private async Task RoadOfferBridgeAsync(RoadBridgePlan plan, Action resolveAfterBridge)
    {
        bool yes = await RoadConfirmAsync("点对点寻径 · 路网断口",
            $"起终点分属 {plan.ComponentCount} 个连通片中的第 #{plan.SrcComp} 片和第 #{plan.DstComp} 片，中间断了 {plan.Gaps.Count} 处。\n\n"
            + $"这几处缺口都是平接的（最宽 {plan.BottleneckM:F0}m，高差都在 {RoadBridgeZSepM:F0}m 内），可以直接补上连接边。\n\n"
            + "补完立即重算这条路。要现在补吗？\n（连接边只加进当前会话路网；要留住请用「基础道路网络构建」重建后存档，或用「手动标定线路」把这几段画成真中线。）", "补上并重算", "不补");
        if (!yes || RS.Graph is null) return;
        int n = RoadApplyBridges(RS.Graph, plan);
        if (n > 0) resolveAfterBridge();
    }

    /// <summary>缺口链的文本块（进结果窗，跟在诊断后面）。</summary>
    private static string RoadBridgePlanBlock(RoadBridgePlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("断在哪（连通片缺口链，按「最宽的一处最窄」求得）：");
        sb.AppendLine($"  全图 {plan.ComponentCount} 个连通片；起点在 #{plan.SrcComp}，终点在 #{plan.DstComp}。");
        if (!plan.Reachable) { sb.AppendLine($"  {RoadMaxBridgeGapM:F0}m 以内找不到任何接法 —— 两块地之间是真的没有路。"); return sb.ToString(); }
        sb.AppendLine($"  接上这 {plan.Gaps.Count} 处即可通（最宽一处 {plan.BottleneckM:F0}m）：");
        sb.AppendLine($"    {"#",-3}{"从片",6}{"到片",6}{"间隙",9}{"水平",9}{"高差",9}  位置(X, Y, Z)");
        int i = 1;
        foreach (var g in plan.Gaps)
            sb.AppendLine($"    {i++,-3}{"#" + g.FromComp,6}{"#" + g.ToComp,6}{g.GapM,8:F0}m{g.HorizGapM,8:F0}m{g.DzM,8:+0;-0}m"
                          + $"  ({g.From.X:F0}, {g.From.Y:F0}, {g.From.Z:F0}) → ({g.To.X:F0}, {g.To.Y:F0}, {g.To.Z:F0})"
                          + (g.IsFlat(RoadBridgeZSepM) ? "" : "   ← 跨标高，缺的是坡道"));
        return sb.ToString();
    }

    /// <summary>把缺口链按几何位置补进会话图：两端各投影到路面（必要时打断插节点），中间加一条 BRX* 连接边。返回真加上的条数。</summary>
    private int RoadApplyBridges(RoadGraph session, RoadBridgePlan plan)
    {
        var rs = RS;
        int added = 0;
        foreach (var gap in plan.Gaps)
        {
            var a = RoadAttachToRoad(session, gap.From);
            var b = RoadAttachToRoad(session, gap.To);
            if (a is null || b is null || a.Id == b.Id) continue;
            string id = $"BRX{rs.BridgeSeq++}";
            while (session.GetEdge(id) is not null) id = $"BRX{rs.BridgeSeq++}";
            session.AddEdge(new RoadEdge(id, a.Id, b.Id, new[] { a.Position, b.Position }) { SourceRef = RoadEdge.SourceManualGapFix });
            added++;
        }
        EditEcho(added > 0
                ? $"✓ 已补 {added} 条连接边进当前会话路网（连通片 {plan.ComponentCount} → {session.Validate().ComponentCount}）。要留住这几条，请「基础道路网络构建」重建后存档，或用「手动标定线路」画成真中线。"
                : "缺口两端没能落到路面上，一条也没补（路网刚被改过？）。",
            added > 0 ? EchoLevel.Success : EchoLevel.Warn);
        return added;
    }

    /// <summary>把一个落在中线上的点接进图：贴着已有节点就用它，否则就地打断插一个 BRH* 节点。</summary>
    private RoadNode? RoadAttachToRoad(RoadGraph g, in Point3d p)
    {
        var e = g.NearestEdgeWithFoot(p, RoadNodeReuseTolM * 2, out var foot, out _);
        if (e is null) return null;
        if (g.GetNode(e.FromId) is { } a && a.Position.DistanceTo(foot) <= RoadNodeReuseTolM) return a;
        if (g.GetNode(e.ToId) is { } b && b.Position.DistanceTo(foot) <= RoadNodeReuseTolM) return b;
        var rs = RS;
        string id = $"BRH{rs.BridgeSeq++}";
        while (g.GetNode(id) is not null) id = $"BRH{rs.BridgeSeq++}";
        return g.SplitEdgeAtNearest(e.Id, foot, id);
    }

    // ═══════════════════════ 等效运距（一源多汇） ═══════════════════════

    /// <summary>「等效运距」：一源多汇。选一个源 → 对每个卸载点各解一组往返 → 按往返等效升序排出"排到哪个卸点最划算"。</summary>
    private void RoadEquivHaulCmd()
    {
        var graph = TryBuildRoadGraph();
        if (graph is null || graph.NodeCount < 2) return;
        RoadOpenEquivHaulWindow(graph, initialSrcId: null, RoadCaliper());
    }

    /// <summary>打开「等效运距」窗。整个窗口生命周期共用一张工作副本（临时点递增编号），不污染会话图。</summary>
    private void RoadOpenEquivHaulWindow(RoadGraph graph, string? initialSrcId, HaulCaliper cal)
    {
        var work = (RS.Graph ?? graph).Clone();
        int pickSeq = 0;
        var sinks = work.Nodes.Where(n => n.Type == RoadNodeType.Unloading).Select(n => n.Id).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var sources = work.Nodes.Where(n => n.Type == RoadNodeType.Loading).Select(n => n.Id).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (initialSrcId is not null && !sources.Contains(initialSrcId, StringComparer.Ordinal)) sources.Insert(0, initialSrcId);
        if (sinks.Count == 0)
            EditEcho("等效运距：路网里还没有卸载点（汇）。窗里用「视口加候选卸点…」在图上点几个（与点对点寻径同一套吸附），或先去「破碎站位置设置」/「去向台账」录台账。", EchoLevel.Warn);

        var win = new EquivHaulWindow(sources, sinks, cal,
            pickSource: onPicked => _ = RoadPickForEquiv(work, cal, "源点", () => $"PICK_SRC{++pickSeq}", onPicked),
            pickSink: onPicked => _ = RoadPickForEquiv(work, cal, "候选卸点", () => $"PICK_SINK{++pickSeq}", onPicked),
            solve: (srcId, sinkIds, caliper) =>
            {
                var solver = new DijkstraPathSolver(work);
                return sinkIds.Select(k => HaulSolveKernel.Solve(work, solver, srcId, k, caliper)).ToList();
            },
            highlight: pair => { BeginChange(); RoadHighlightHaulPair(work, pair); },
            clearHighlight: () => { BeginChange(); RoadClearPathHighlight(); },
            openInPointToPoint: (srcId, dstId, caliper) => RoadRunAndShowHaulPair(work, srcId, dstId, caliper),
            saveCandidates: ids => RoadSaveSinkCandidatesAsync(work, ids));
        win.Show(this);
        if (initialSrcId is not null)
        {
            win.SelectSource(initialSrcId);
            EditEcho($"等效运距：已带着源 {initialSrcId} 和当前口径打开，勾上要比的卸载点后点「计算」。");
        }
    }

    private async Task RoadPickForEquiv(RoadGraph work, HaulCaliper cal, string what, Func<string> nextId, Action<string> onPicked)
    {
        var p = await RoadPickOnePointAsync($"等效运距：在视口点{what}（Esc 取消）");
        if (p == null) return;
        BeginChange();
        var n = RoadSnapToRoad(work, p.Value, cal, what, nextId());
        if (n is not null) onPicked(n.Id);
    }

    /// <summary>把「等效运距」里的候选卸点补上身份 → 写进 load_unload_point 台账 → 按坐标接进会话路网。返回真正入账成功的候选 Id。</summary>
    private async Task<IReadOnlyList<string>> RoadSaveSinkCandidatesAsync(RoadGraph work, IReadOnlyList<string> candidateIds)
    {
        var empty = Array.Empty<string>();
        var conn = _geoDb?.Connection;
        if (conn is null)
        {
            EditEcho("存候选卸点：工程库未连接，无法入账。", EchoLevel.Error);
            await RoadInfoAsync("把候选存进台账", "工程库未连接，候选没法入账。");
            return empty;
        }
        var rows = new List<SaveSinkCandidatesDialog.Row>();
        foreach (var id in candidateIds)
            if (work.GetNode(id) is { } n)
                rows.Add(new SaveSinkCandidatesDialog.Row { CandidateId = id, X = n.Position.X, Y = n.Position.Y, Z = n.Position.Z });
        if (rows.Count == 0) return empty;
        var sites = Data.LoadUnloadPointStore.ActiveDumpSites(conn);
        var dlg = new SaveSinkCandidatesDialog(rows, sites);
        if (await dlg.ShowDialog<bool?>(this) != true || dlg.Result.Count == 0) return empty;

        Data.SinkSaveResult res;
        try { res = Data.LoadUnloadPointStore.SaveScoped(conn, dlg.Result); }
        catch (Exception ex)
        {
            EditEcho($"存候选卸点失败：{ex.Message}", EchoLevel.Error);
            await RoadInfoAsync("把候选存进台账", "落库失败：" + ex.Message);
            return empty;
        }
        int attached = 0, isolated = 0;
        var longStubs = new List<string>();
        if (RS.Graph is { } session)
            foreach (var s in dlg.Result)
            {
                if (session.GetNode(s.Name) is not null) continue;
                double stub = RoadAttachLoadUnload(session, new Point3d(s.X, s.Y, s.Z), s.Name, RoadNodeType.Unloading, s.ThroughputTph, snapTolM: 5.0, maxStubM: DepotMaxStubM);
                if (stub < 0) isolated++;
                else { attached++; if (stub > DepotWarnStubM) longStubs.Add($"{s.Name}({stub:F0}m)"); }
            }
        EditEcho($"✓ 候选入账：新增 {res.Inserted} / 更新 {res.Updated}（{string.Join("、", res.Names)}）；接进路网 {attached}"
                 + (isolated > 0 ? $"，孤立 {isolated}（附近 {DepotMaxStubM:F0}m 内没路，寻径过不去，位置要重指）" : "")
                 + "。它们现在是真卸载点，运输指标报表与运量驱动布线都会统计到。", isolated == 0 ? EchoLevel.Success : EchoLevel.Warn);
        if (longStubs.Count > 0) EditEcho($"⚠ 接入支线偏长（>{DepotWarnStubM:F0}m），请核实位置：{string.Join("、", longStubs)}", EchoLevel.Warn);
        foreach (var id in candidateIds) if (work.GetNode(id) is { } n) n.Type = RoadNodeType.Unloading;
        return candidateIds;
    }

    // ═══════════════════════ 运输指标报表 ═══════════════════════

    /// <summary>「运输指标报表」：与寻径/等效运距同一份口径 → OD 矩阵 / 运距指标 / 运能与瓶颈 / 分期趋势。</summary>
    private void RoadIndicatorsReportCmd()
    {
        var graph = TryBuildRoadGraph();
        if (graph is null || graph.NodeCount < 2) return;
        var cal = RoadCaliper();
        var snapGraphs = RS.Snapshots.Select(s => s.Graph).ToList();
        var ind = TransportIndicatorsBuilder.Compute(graph, snapGraphs, cal);
        EditEcho($"✓ 运输指标（{cal.ModeText}）：平均等效运距 {ind.AvgEquivM:F0}m · 理论运能上界 {ind.TheoreticalCapacityTph:F0}t/h · 瓶颈段 {ind.Bottlenecks.Count}（详见报表窗）"
                 + (ind.UsedAllNodesFallback ? "（未设装卸点,按全节点估算）" : ""), EchoLevel.Success);
        var win = new TransportIndicatorsWindow(ind, () =>
        {
            var g = TryBuildRoadGraph(echo: false);
            var fresh = RoadCaliper();
            return g != null ? TransportIndicatorsBuilder.Compute(g, RS.Snapshots.Select(s => s.Graph).ToList(), fresh) : ind;
        });
        win.Show(this);
        StatusMsg.Text = $"运输指标报表：{ind.NodeCount} 节点 / {ind.EdgeCount} 边 · 平均等效运距 {ind.AvgEquivM:F0}m";
    }
}
