using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Views.Road;
using Point3d = PitMine3D.Kylin.Cad.Road.Point3d;
using RoadEdgeStatus = PitMine3D.Kylin.Cad.Road.RoadEdgeStatus;
using CenterlineJunctions = PitMine3D.Kylin.Cad.Road.CenterlineJunctions;
using CenterlineJunctionSet = PitMine3D.Kylin.Cad.Road.CenterlineJunctionSet;
using CenterlineJunction = PitMine3D.Kylin.Cad.Road.CenterlineJunction;
using JunctionKind = PitMine3D.Kylin.Cad.Road.JunctionKind;
using StructurePavement = PitMine3D.Kylin.Cad.Road.StructurePavement;

namespace PitMine3D.Kylin.Views;

/// <summary>道路运输系统 · 手动标定线路 · 中心线管理 · 路况显示 · 结构路面。忠实原 RoadLibPlugin 对应命令。</summary>
public partial class MainWindow
{
    private static readonly Rgb CenterlineAmber = new(242, 165, 23);

    // ═══════════════════════ 手动标定线路 ═══════════════════════

    /// <summary>
    /// 「手动标定线路」：把补充线接进「点云_道路中心线」。两条进料口：选中已画好的多段线 / 视口现画（无可接入选集时自动进入，左键逐点、Esc/右键收线并接入）。
    /// 选中的线若已在中心线层上，则只做打标。
    /// </summary>
    private async Task RoadMarkRouteAsync()
    {
        var drawn = new List<double[]>();
        var others = new List<PolylineEntity>();
        foreach (var e in _selected)
            if (e is PolylineEntity pl && pl.Points.Count >= 2)
            {
                if (pl.LayerName == RoadCenterlineLayer) others.Add(pl); else drawn.Add(PolylineToFlatXyz(pl));
            }

        if (drawn.Count == 0 && others.Count == 0)
        {
            EditEcho(_selected.Count == 0
                ? "手动标定线路：未选中线 → 进入视口画线（也可先选好多段线再点本按钮）。"
                : $"手动标定线路：选中的 {_selected.Count} 个实体里没有可用多段线(顶点≥2) → 进入视口画线。");
            var line = await RoadPickRouteLineAsync();
            if (line != null) RoadMergeManualRoutes(new List<double[]> { line }, "视口画");
            return;
        }

        if (drawn.Count == 0)
        {
            // 没有新画的线 → 旧行为：仅把选中多段线打标进层（不连通）。
            BeginChange();
            var layer = RoadEnsureLayer(RoadCenterlineLayer, CenterlineAmber, 0.5f);
            foreach (var pl in others) { pl.LayerName = layer.Name; pl.Cr = CenterlineAmber.R / 255f; pl.Cg = CenterlineAmber.G / 255f; pl.Cb = CenterlineAmber.B / 255f; }
            RefreshScene();
            RS.Graph = null;
            var g0 = TryBuildRoadGraph();
            EditEcho($"✓ 手动标定线路：已打标 {others.Count} 条进「点云_道路中心线」并纳入路网" + (g0 != null ? $"（现 {g0.NodeCount} 节点 / {g0.EdgeCount} 边）" : ""), EchoLevel.Success);
            return;
        }
        // 选中的新画线接入：接入后原实体收走（几何已并进中心线层）。
        BeginChange();
        foreach (var e in _selected.ToList()) if (e is PolylineEntity pl && pl.LayerName != RoadCenterlineLayer && pl.Points.Count >= 2) _scene.Remove(pl);
        _selected.Clear();
        RoadMergeManualRoutes(drawn, "选中");
    }

    /// <summary>
    /// 视口画一条中线：左键逐点，Esc/右键 = 收线并接入（≥2 点才成线）。
    /// 就近捕捉交点（本功能自带、无开关）：进场把图上建网会打出节点的位置全算出来并标进视口，每次点击把落点吸到半径内最近的那个上（半径按屏幕像素折世界米）。
    /// 先挑路口、没有才退接缝；吸过之后另画一条琥珀折线（真正会落地的走向）；标记走预览几何，收工即抹。
    /// </summary>
    private async Task<double[]?> RoadPickRouteLineAsync()
    {
        var junc = RoadBuildJunctionSet();
        if (junc is { Count: > 0 })
        {
            RoadShowJunctionMarkers(junc);
            EditEcho($"就近捕捉交点：图上标出 {junc.Count} 个可捕捉点（{junc.Summary}）；白圈=路口(X形/多路汇合)、绿圈=T形、蓝圈=半腰焊、灰方=接缝、橙叉=立交（吸得到但建网不在此连通）；半径内先挑路口，没有路口才退给接缝；离得远的地方点哪儿是哪儿。");
        }
        else
        {
            junc = null;
            EditEcho("就近捕捉交点：图上算不出交点（中线太少 / 彼此都没搭上）—— 本次按点哪儿是哪儿。", EchoLevel.Warn);
        }
        EditEcho("手动标定线路：在视口左键逐点画出中心线走向，画完按 Esc（或右键）收线并接入路网");

        var pts = new List<double>();
        int snapped = 0;
        try
        {
            while (true)
            {
                int n0 = pts.Count / 3;
                var p = await RoadPickRoutePointAsync(n0 == 0 ? "手动标定线路：点第 1 点（Esc 结束）" : $"手动标定线路：点第 {n0 + 1} 点（已 {n0} 点，Esc/右键 收线并接入）", n0 >= 1);
                if (p == null) break;   // Esc / 右键 = 收线
                double x = p.Value.X, y = p.Value.Y, z = p.Value.Z;
                double sx = x, sy = y, sz = z;
                CenterlineJunction? hit = null;
                double dist = 0;
                if (junc != null)
                {
                    hit = junc.Nearest(x, y, RoadJunctionSnapRadiusM(x, y, z), out dist);
                    if (hit != null) { sx = hit.X; sy = hit.Y; sz = hit.ZAt(z); snapped++; }
                }
                pts.Add(sx); pts.Add(sy); pts.Add(sz);
                int n = pts.Count / 3;
                RoadShowSnappedPath(junc, pts);
                string tail = hit == null ? "" :
                    $" ←已捕捉{hit.KindLabel}（离点击 {dist:F1}m" + (hit.GapM > 0.05 ? $"·两线缝宽 {hit.GapM:F1}m" : "")
                    + (hit.IsRealJunction ? $"·{hit.LineCount} 条中线在此碰头" : "·不是路口，是两条中线的接头")
                    + (hit.GradeSeparated ? $"·⚠这是立交 Δz {hit.DzM:F1}m，取了 z={sz:F0} 那一层，建网不会在此连通" : "") + "）";
                EditEcho($"中线第 {n} 点：({sx:F1}, {sy:F1}){tail}" + (n >= 2 ? " —— Esc/右键 收线并接入路网" : " —— 继续点，Esc/右键 结束"),
                    hit?.GradeSeparated == true ? EchoLevel.Warn : EchoLevel.Info);
            }
        }
        finally
        {
            Viewport.SetPreviewGeometry(null);
        }
        if (pts.Count < 6) { EditEcho($"手动标定线路：已退出（只点了 {pts.Count / 3} 点，画不成线）"); return null; }
        if (snapped > 0) EditEcho($"就近捕捉交点：本条线 {pts.Count / 3} 点里有 {snapped} 点吸到了交点上（接入用的就是吸过的坐标）。");
        return pts.ToArray();
    }

    /// <summary>画线取点：左键 = 一点；Esc = 结束（null）；有 ≥1 点后右键也 = 结束。</summary>
    private Task<Point3d?> RoadPickRoutePointAsync(string prompt, bool rightClickFinishes)
    {
        var tcs = new TaskCompletionSource<Point3d?>();
        _oneShotPick = (x, y) =>
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) { tcs.TrySetResult(null); return; }
            tcs.TrySetResult(new Point3d(x, y, RoadSurfaceZAt(x, y)));
        };
        _pickConfirmable = rightClickFinishes;   // 右键/回车 = 收线（与原版 Esc/右键 同义）
        _pickCursor = Controls.CadGlViewport.CursorMode.CrosshairOnly;
        _pickPrompt = prompt;
        StatusMsg.Text = prompt;
        return tcs.Task;
    }

    /// <summary>读「点云_道路中心线」→ 算出全部可捕捉交点（容差刻意传建网实际用的那两个）。图上没有中线时返回 null。</summary>
    private CenterlineJunctionSet? RoadBuildJunctionSet()
    {
        var pool = ReadCenterlinePool();
        if (pool.Count == 0) return null;
        var lines = new List<double[]>(pool.Count);
        foreach (var p in pool) lines.Add(p.Xyz);
        return CenterlineJunctions.Build(lines, contactTolM: RoadDefaultSnapToleranceM, gradeSeparationM: RoadBridgeZSepM);
    }

    /// <summary>捕捉半径 m：拿视口比例尺折算（24px），夹到 [2, 40] m。每次点击都重算 —— 画线中途缩放是常事。</summary>
    private double RoadJunctionSnapRadiusM(double x, double y, double z)
    {
        try
        {
            double perPx = Viewport.WorldPerPixelAt(x, y, z);
            if (perPx > 1e-9) return Math.Min(MaxJunctionSnapM, Math.Max(MinJunctionSnapM, perPx * JunctionSnapPixels));
        }
        catch { }
        return 12.0;
    }

    private CenterlineJunctionSet? _roadJunctionMarkers;

    /// <summary>把可捕捉交点整组标进视口（预览几何）：X形白 / T形绿 / 半腰焊蓝 / 接缝灰小方 / 立交橙叉。</summary>
    private void RoadShowJunctionMarkers(CenterlineJunctionSet set)
    {
        _roadJunctionMarkers = set;
        RoadShowSnappedPath(set, null);
    }

    /// <summary>预览几何 = 交点标记 + 吸过之后真正会落地的那条走向（琥珀）。</summary>
    private void RoadShowSnappedPath(CenterlineJunctionSet? set, List<double>? pts)
    {
        var o = new List<float>();
        double ox = RenderOrigin.X, oy = RenderOrigin.Y;
        void Seg(double x0, double y0, double z0, double x1, double y1, double z1, float r, float g, float b)
        {
            o.Add((float)(x0 - ox)); o.Add((float)(y0 - oy)); o.Add((float)z0); o.Add(r); o.Add(g); o.Add(b);
            o.Add((float)(x1 - ox)); o.Add((float)(y1 - oy)); o.Add((float)z1); o.Add(r); o.Add(g); o.Add(b);
        }
        set ??= _roadJunctionMarkers;
        if (set != null)
        {
            double s = 0;
            try { s = Viewport.WorldPerPixelAt(set.All.Count > 0 ? set.All[0].X : 0, set.All.Count > 0 ? set.All[0].Y : 0, 0) * 8; } catch { }
            if (s <= 0) s = 6;
            foreach (var j in set.All)
            {
                double z = j.GradeSeparated ? Math.Max(j.ZA, j.ZB) : (j.ZA + j.ZB) * 0.5;
                float r, g, b; int style;
                if (j.GradeSeparated) { r = 0.96f; g = 0.62f; b = 0.04f; style = 0; }          // 橙 ×
                else if (!j.IsRealJunction) { r = 0.61f; g = 0.64f; b = 0.69f; style = 2; }   // 灰 □
                else switch (j.Kind)
                {
                    case JunctionKind.Tee: r = 0.20f; g = 0.83f; b = 0.60f; style = 3; break;     // 绿 ○
                    case JunctionKind.MidWeld: r = 0.38f; g = 0.65f; b = 0.98f; style = 3; break; // 蓝 ○
                    default: r = 0.91f; g = 0.93f; b = 0.95f; style = 3; break;                   // 白 ○
                }
                double rad = s * (style == 2 ? 0.6 : 1.0);
                if (style == 0)
                {
                    Seg(j.X - rad, j.Y - rad, z, j.X + rad, j.Y + rad, z, r, g, b);
                    Seg(j.X - rad, j.Y + rad, z, j.X + rad, j.Y - rad, z, r, g, b);
                }
                else if (style == 2)
                {
                    Seg(j.X - rad, j.Y - rad, z, j.X + rad, j.Y - rad, z, r, g, b); Seg(j.X + rad, j.Y - rad, z, j.X + rad, j.Y + rad, z, r, g, b);
                    Seg(j.X + rad, j.Y + rad, z, j.X - rad, j.Y + rad, z, r, g, b); Seg(j.X - rad, j.Y + rad, z, j.X - rad, j.Y - rad, z, r, g, b);
                }
                else
                {
                    const int n = 12;
                    for (int k = 0; k < n; k++)
                    {
                        double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n;
                        Seg(j.X + rad * Math.Cos(a0), j.Y + rad * Math.Sin(a0), z, j.X + rad * Math.Cos(a1), j.Y + rad * Math.Sin(a1), z, r, g, b);
                    }
                }
            }
        }
        if (pts != null)
            for (int k = 0; k + 5 < pts.Count; k += 3)
                Seg(pts[k], pts[k + 1], pts[k + 2], pts[k + 3], pts[k + 4], pts[k + 5], 0.95f, 0.65f, 0.09f);   // 琥珀，与中心线实体同色
        Viewport.SetPreviewGeometry(o.Count > 0 ? o.ToArray() : null);
        if (pts == null && set == null) _roadJunctionMarkers = null;
    }

    /// <summary>
    /// 补充线接入路网：读现有中心线网 + 现状地形(TIN/台阶线) → 补充线按地形铺贴 + 焊接/桥接成 T 形节点 → 增量写回「点云_道路中心线」→ 重建会话图。
    /// 写成功后才删旧线。台阶线只收画线包围盒 +200m 内的（BenchZField IDW 半径 60m，盒外的线一个采样点都够不着）。
    /// </summary>
    private void RoadMergeManualRoutes(List<double[]> drawn, string origin)
    {
        _roadJunctionMarkers = null;
        Viewport.SetPreviewGeometry(null);
        var pool = ReadCenterlinePool();
        var before = new List<(ulong Handle, double[] Xyz)>(pool.Count);
        var byHandle = new List<PolylineEntity>(pool.Count);
        var network = new List<double[]>(pool.Count);
        for (int i = 0; i < pool.Count; i++) { before.Add(((ulong)i, pool[i].Xyz)); byHandle.Add(pool[i].H); network.Add(pool[i].Xyz); }

        var box = RoadBBoxOf(drawn, RoadTerrainMarginM);
        var bench = new List<double[]>();
        foreach (var e in _scene.Entities)
        {
            if (e is not PolylineEntity pl || pl.Points.Count < 2) continue;
            if (pl.LayerName == RoadCenterlineLayer || pl.LayerName == "等高线") continue;
            if (box != null && !RoadPolyIntersectsBox(pl, box)) continue;
            bench.Add(PolylineToFlatXyz(pl));
        }
        TryReadBestRoadTin(out var mv, out var mtr);
        IRoadZSampler? sampler = RoadTerrainSampler.BuildSampler(mv!, mtr!, bench);

        var opt = new RoadConnectOptions { SnapTol = 4.0, ConnectDist = 0.0, ManualConnectDist = 80.0, MaxBridgeSlopeDeg = 14.0, DrapeSpacing = 3.0 };
        var cr = RoadNetworkConnector.Connect(network, drawn, opt, sampler);
        if (cr.Lines.Count == 0) { EditEcho("手动标定线路：接入后无有效中线（画的线退化了？），原有中线未动。", EchoLevel.Warn); return; }

        var diff = CenterlineLayerDiff.Compute(before, cr.Lines);
        BeginChange();
        var layer = RoadEnsureLayer(RoadCenterlineLayer, CenterlineAmber, 0.5f);
        foreach (var cl in diff.WriteLines) RoadAddFlat(layer.Name, cl, CenterlineAmber);
        foreach (var h in diff.DeleteHandles) { var pl = byHandle[(int)h]; _scene.Remove(pl); _selected.Remove(pl); }
        RefreshScene();

        RS.Graph = null;
        var graph = TryBuildRoadGraph();
        string drape = sampler != null ? "已按现状地形(TIN/台阶线)铺贴平稳过渡" : "无 TIN/台阶线未铺贴(画的线保持原标高)";
        EditEcho($"✓ 手动标定线路：已接入 {drawn.Count} 条补充线（{origin}·{cr.Summary}·{drape}·{diff.Summary}）" + (graph != null ? $"，现 {graph.NodeCount} 节点 / {graph.EdgeCount} 边" : ""), EchoLevel.Success);
        StatusMsg.Text = $"手动标定线路：已接入 {drawn.Count} 条补充线";
        RS.CenterlineWin?.Reload();
    }

    private static double[]? RoadBBoxOf(IReadOnlyList<double[]> lines, double marginM)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        bool any = false;
        foreach (var f in lines)
        {
            if (f is not { Length: >= 6 }) continue;
            for (int i = 0; i + 2 < f.Length; i += 3)
            {
                any = true;
                if (f[i] < x0) x0 = f[i]; if (f[i] > x1) x1 = f[i];
                if (f[i + 1] < y0) y0 = f[i + 1]; if (f[i + 1] > y1) y1 = f[i + 1];
            }
        }
        return any ? new[] { x0 - marginM, y0 - marginM, x1 + marginM, y1 + marginM } : null;
    }

    private static bool RoadPolyIntersectsBox(PolylineEntity pl, double[] box)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
        foreach (var (x, y) in pl.Points) { if (x < mnx) mnx = x; if (x > mxx) mxx = x; if (y < mny) mny = y; if (y > mxy) mxy = y; }
        return !(mxx < box[0] || mnx > box[2] || mxy < box[1] || mny > box[3]);
    }

    // ═══════════════════════ 中心线管理 ═══════════════════════

    /// <summary>「中心线管理」：开非模态清单窗（清单化 + 批量删 + 存档持久化）。</summary>
    private void RoadCenterlineManagerCmd()
    {
        var rs = RS;
        if (rs.CenterlineWin is { IsVisible: true }) { rs.CenterlineWin.Activate(); rs.CenterlineWin.Reload(); return; }
        var win = new CenterlineManagerWindow(new RoadCenterlineHost(this));
        rs.CenterlineWin = win;
        win.Closed += (_, _) => { if (ReferenceEquals(RS.CenterlineWin, win)) RS.CenterlineWin = null; };
        win.Show(this);
    }

    /// <summary>整批删中线 → 按剩下的中线重建会话图。返回真正删掉的条数。</summary>
    private int RoadDeleteCenterlinesAndRebuild(IReadOnlyList<PolylineEntity> handles, double totalLenM, string origin)
    {
        if (handles.Count == 0) return 0;
        BeginChange();
        int n = 0;
        foreach (var h in handles) if (_scene.Remove(h)) { n++; _selected.Remove(h); }
        RefreshScene();
        if (n == 0) { EditEcho("中心线管理：一条都没删掉（实体已不在图上？）", EchoLevel.Warn); return 0; }
        RS.Graph = null;
        var graph = TryBuildRoadGraph();
        EditEcho($"✓ 中心线管理：已删 {n}/{handles.Count} 条（{origin}·总长 {totalLenM:F0}m）"
                 + (graph != null ? $"，现 {graph.NodeCount} 节点 / {graph.EdgeCount} 边 / {graph.Validate().ComponentCount} 连通片" : "，图上已无中线（路网已空）"), EchoLevel.Success);
        return n;
    }

    /// <summary>把一批中线写回「点云_道路中心线」：append=false 时先清空本层。返回写进去的条数。</summary>
    private int RoadWriteCenterlineLayer(IReadOnlyList<double[]> lines, bool append, string origin)
    {
        var keep = lines.Where(l => l is { Length: >= 6 }).ToList();
        if (keep.Count == 0) { EditEcho($"中心线管理：{origin} 无有效中线，图上未动。", EchoLevel.Warn); return 0; }
        BeginChange();
        int old = append ? 0 : RoadClearLayer(RoadCenterlineLayer);
        var layer = RoadEnsureLayer(RoadCenterlineLayer, CenterlineAmber, 0.5f);
        foreach (var l in keep) RoadAddFlat(layer.Name, l, CenterlineAmber);
        RefreshScene();
        RS.Graph = null;
        var graph = TryBuildRoadGraph();
        EditEcho($"✓ 中心线管理：{origin} 已写入 {keep.Count} 条中线（{(append ? "追加" : $"覆盖，收走旧线 {old} 条")}）" + (graph != null ? $"，现 {graph.NodeCount} 节点 / {graph.EdgeCount} 边" : ""), EchoLevel.Success);
        return keep.Count;
    }

    /// <summary>视口点中线：左键点中 → 命中半径内最近的那条进清单并红色高亮，再点同一条即取消；Esc/右键 = 收工。</summary>
    private async Task RoadPickCenterlinesInViewportAsync(List<(PolylineEntity H, double[] Xyz)> pool, IReadOnlyList<PolylineEntity> preMarked, Action<List<PolylineEntity>> onDone)
    {
        var geo = pool.Select(p => p.Xyz).ToList();
        var marked = new List<int>();
        var want = new HashSet<PolylineEntity>(preMarked);
        for (int i = 0; i < pool.Count; i++) if (want.Contains(pool[i].H)) marked.Add(i);
        void Highlight() => RoadHighlightCenterlines(marked.Select(i => pool[i].Xyz).ToList());
        EditEcho($"中心线管理：在视口左键点中线（命中半径 {CenterlinePickRadiusM:F0}m，红色=已选中，再点一次取消），点完按 Esc（或右键）收工，选中项回填清单");
        while (true)
        {
            var p = await RoadPickRoutePointAsync($"中心线管理：点中线（已选 {marked.Count} 条，Esc/右键 收工）", true);
            if (p == null) break;
            int i = CenterlinePick.NearestIndex(p.Value.X, p.Value.Y, geo, CenterlinePickRadiusM, out double d);
            if (i < 0) { EditEcho($"（{p.Value.X:F0}, {p.Value.Y:F0}）{CenterlinePickRadiusM:F0}m 内没有中心线，换个位置再点（Esc 收工）", EchoLevel.Warn); continue; }
            if (marked.Remove(i)) EditEcho($"已取消选中（长 {CenterlinePick.Length3d(pool[i].Xyz):F0}m）；当前选中 {marked.Count} 条");
            else
            {
                marked.Add(i);
                EditEcho($"点中：长 {CenterlinePick.Length3d(pool[i].Xyz):F0}m（距点击 {d:F1}m）；当前选中 {marked.Count} 条 —— 再点它可取消，Esc/右键 收工");
            }
            Highlight();
        }
        var list = marked.Select(i => pool[i].H).ToList();
        if (list.Count == 0) EditEcho("中心线管理：已退出（没点中任何中线）");
        onDone(list);
    }

    /// <summary>整组高亮中线（禁止红，预览几何：不建实体、不占 Undo 栈）；空表 = 抹掉。</summary>
    private void RoadHighlightCenterlines(IReadOnlyList<double[]> lines)
    {
        if (lines.Count == 0) { Viewport.SetHighlight(null); RedrawHighlight(); return; }
        var o = new List<float>();
        double ox = RenderOrigin.X, oy = RenderOrigin.Y;
        foreach (var f in lines)
        {
            if (f is not { Length: >= 6 }) continue;
            for (int s = 0; s + 5 < f.Length; s += 3)
            {
                o.Add((float)(f[s] - ox)); o.Add((float)(f[s + 1] - oy)); o.Add((float)f[s + 2]); o.Add(0.86f); o.Add(0.15f); o.Add(0.15f);
                o.Add((float)(f[s + 3] - ox)); o.Add((float)(f[s + 4] - oy)); o.Add((float)f[s + 5]); o.Add(0.86f); o.Add(0.15f); o.Add(0.15f);
            }
        }
        Viewport.SetHighlight(o.ToArray(), recolor: false);
    }

    /// <summary>选集变更钩子（HighlightSelection 里调）：「批量选择：只选中心线」开着时把非中心线实体从选集里剔掉，再把留下的回给管理窗。</summary>
    private void RoadSelectionHook()
    {
        var f = RS.SelectionFilter;
        if (f == null || f.Busy) return;
        var keep = new List<PolylineEntity>();
        foreach (var e in _selected) if (e is PolylineEntity pl && f.Allow.Contains(pl)) keep.Add(pl);
        if (keep.Count != _selected.Count)
        {
            f.Busy = true;
            int total = _selected.Count;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try { _selected.Clear(); _selected.AddRange(keep); HighlightSelection(); }
                finally { f.Busy = false; }
            });
            EditEcho($"批量选择：框中 {total} 个实体 → 留下 {keep.Count} 条中心线（剔掉 {total - keep.Count} 个非中线实体）");
        }
        f.OnChanged?.Invoke(keep);
    }

    /// <summary>「中心线管理」窗的宿主适配器：真正碰图纸/数据库的动作全落回主窗自己的方法。</summary>
    private sealed class RoadCenterlineHost : ICenterlineManagerHost
    {
        private readonly MainWindow _w;
        public RoadCenterlineHost(MainWindow w) { _w = w; }

        public IReadOnlyList<(PolylineEntity Handle, double[] Xyz)> ReadCenterlines() => _w.ReadCenterlinePool();

        public void SelectInViewport(IReadOnlyList<PolylineEntity> handles)
        {
            _w._selected.Clear();
            foreach (var h in handles) _w._selected.Add(h);
            _w.HighlightSelection();
        }

        public void Highlight(IReadOnlyList<double[]> lines) => _w.RoadHighlightCenterlines(lines);

        public int DeleteCenterlines(IReadOnlyList<PolylineEntity> handles, double totalLengthM) => _w.RoadDeleteCenterlinesAndRebuild(handles, totalLengthM, "清单选中");

        public void PickInViewport(IReadOnlyList<PolylineEntity> preSelected, Action<IReadOnlyList<PolylineEntity>> onPicked)
        {
            var pool = _w.ReadCenterlinePool();
            if (pool.Count == 0) { Echo($"「{RoadCenterlineLayer}」上没有中线可点。", warn: true); onPicked(Array.Empty<PolylineEntity>()); return; }
            _ = _w.RoadPickCenterlinesInViewportAsync(pool, preSelected, list => onPicked(list));
        }

        public void SetCenterlineOnlySelection(bool enabled, IReadOnlyList<PolylineEntity> centerlineHandles, Action<IReadOnlyList<PolylineEntity>> onSelectionChanged)
        {
            var rs = _w.RS;
            rs.SelectionFilter = null;
            if (!enabled) { Echo("中心线管理：批量选择已关闭（视口选集恢复原样）。"); return; }
            rs.SelectionFilter = new CenterlineOnlySelectionFilter { Allow = new HashSet<PolylineEntity>(centerlineHandles), OnChanged = onSelectionChanged };
            Echo($"中心线管理：批量选择已开启 —— 视口里框选/点选只会留下「{RoadCenterlineLayer}」上的 {centerlineHandles.Count} 条中线，其余实体自动移出选集并同步勾进清单。");
        }

        public int WriteCenterlines(IReadOnlyList<double[]> lines, bool append) => _w.RoadWriteCenterlineLayer(lines, append, append ? "追加载入" : "载入存档");

        public bool ArchiveAvailable => _w._geoDb?.Connection != null;

        public IReadOnlyList<Data.CenterlineSetRecord> ListArchives() => Data.CenterlineSetStore.List(_w._geoDb?.Connection);

        public long SaveArchive(string name, string? note)
        {
            var conn = _w._geoDb?.Connection;
            if (conn == null) return 0;
            var pool = _w.ReadCenterlinePool();
            var lines = pool.Select(p => p.Xyz).ToList();
            if (lines.Count == 0) return 0;
            string b64 = CenterlineSetCodec.Pack(lines);
            if (b64.Length == 0) return 0;
            var st = CenterlineSetCodec.Stats(lines);
            long id = Data.CenterlineSetStore.Insert(conn, new Data.CenterlineSetRecord
            {
                Name = name, CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Source = "中心线管理", GeometryB64 = b64,
                LineCount = st.LineCount, VertexCount = st.VertexCount, LengthKm = st.LengthKm,
                MinX = st.MinX, MinY = st.MinY, MinZ = st.MinZ, MaxX = st.MaxX, MaxY = st.MaxY, MaxZ = st.MaxZ,
                Note = string.IsNullOrWhiteSpace(note) ? null : note,
            });
            Echo($"✓ 已存中心线存档「{name}」：{st.LineCount} 条 / {st.LengthKm:F2} km（随工程持久化，可随时载回图层）。");
            return id;
        }

        public IReadOnlyList<double[]>? LoadArchiveGeometry(Data.CenterlineSetRecord entity)
        {
            var fresh = Data.CenterlineSetStore.Get(_w._geoDb?.Connection, entity.Id) ?? entity;
            return CenterlineSetCodec.TryUnpack(fresh.GeometryB64, out var lines) ? lines : null;
        }

        public void RenameArchive(Data.CenterlineSetRecord entity, string name)
        {
            var conn = _w._geoDb?.Connection;
            if (conn == null) return;
            entity.Name = name;
            Data.CenterlineSetStore.UpdateMeta(conn, entity);
        }

        public void DeleteArchive(long id)
        {
            var conn = _w._geoDb?.Connection;
            if (conn != null) Data.CenterlineSetStore.Delete(conn, id);
        }

        public void Echo(string message, bool warn = false) => _w.EditEcho(message, warn ? EchoLevel.Warn : EchoLevel.Info);
    }

    // ═══════════════════════ 路况显示 ═══════════════════════

    /// <summary>「路况显示」：中心线渲成带路况语义的 overlay（逐段纵坡分档着色，检修/封闭整条压状态色，白向标）；再点一次关闭。</summary>
    private void RoadConditionDisplayCmd()
    {
        if (RoadLayerCount(RoadConditionLayer) > 0)
        { BeginChange(); RoadClearLayer(RoadConditionLayer); RefreshScene(); EditEcho("路况显示：已关闭。"); StatusMsg.Text = "路况显示已关闭"; return; }

        var roads = new List<(IReadOnlyList<Point3d> Line, RoadEdgeStatus Status)>();
        var rs = RS;
        bool fromGraph = rs.Graph != null && rs.Graph.EdgeCount > 0;
        if (fromGraph)
            foreach (var e in rs.Graph!.Edges) if (e.Centerline.Count >= 2) roads.Add((e.Centerline, e.Status));
        if (roads.Count == 0)
        {
            fromGraph = false;
            foreach (var ln in ReadRoadCenterlines()) if (ln.Count >= 2) roads.Add((ln, RoadEdgeStatus.Open));
        }
        if (roads.Count == 0) { EditEcho("路况显示：未找到道路中线。先「提取道路中心线」或「基础道路网络构建」。", EchoLevel.Warn); return; }

        BeginChange();
        RoadEnsureLayer(RoadConditionLayer, new Rgb(90, 96, 105), 1.6f);
        var chevronCol = RoadSymbology.ConditionChevron;
        var lenByClass = new double[RoadConditionSymbology.ClassCount];
        double openLen = 0, bannedLen = 0, maxAbsGrade = 0;
        int maintCount = 0, closedCount = 0, runCount = 0;
        foreach (var (ln, status) in roads)
        {
            if (status != RoadEdgeStatus.Open)
            {
                var sc = status == RoadEdgeStatus.Maintenance ? RoadSymbology.StatusMaintenance : RoadSymbology.StatusClosed;
                RoadAddPoly(RoadConditionLayer, ln, sc);
                if (status == RoadEdgeStatus.Maintenance) maintCount++; else closedCount++;
                bannedLen += RoadLength3d(ln, 0, ln.Count - 1);
                RoadDrawDirectionChevrons(ln, 25.0, 4.0, 3.0, chevronCol);
                continue;
            }
            foreach (var r in RoadConditionSymbology.Classify(ln))
            {
                RoadAddPoly(RoadConditionLayer, RoadRange(ln, r.StartIndex, r.EndIndex), RoadConditionSymbology.ColorOf(r.Band, r.Sense));
                lenByClass[RoadConditionSymbology.ClassIndex(r.Band, r.Sense)] += r.LengthM;
                openLen += r.LengthM;
                runCount++;
                double ag = Math.Abs(r.AvgGradePct);
                if (ag > maxAbsGrade) maxAbsGrade = ag;
            }
            RoadDrawDirectionChevrons(ln, 25.0, 4.0, 3.0, chevronCol);
        }
        RefreshScene();

        var sb = new StringBuilder();
        sb.Append($"✓ 路况显示已开（{roads.Count} 条中线 / 共 {openLen + bannedLen:F0}m·").Append(fromGraph ? "会话路网,与寻径同源" : "按图层中线,无通行状态").Append("）：");
        int shown = 0;
        for (int i = 0; i < RoadConditionSymbology.ClassCount; i++)
        {
            if (lenByClass[i] <= 0) continue;
            var (band, sense) = RoadConditionSymbology.ClassAt(i);
            sb.Append($"{RoadConditionSymbology.TextOf(band, sense)} {lenByClass[i] / Math.Max(1e-9, openLen) * 100:F0}%·");
            shown++;
        }
        if (shown > 0) sb.Length -= 1; else sb.Append("无可通行路段");
        sb.Append($"；最大纵坡 {maxAbsGrade:F1}%（分档 {RoadConditionSymbology.BandMildPct:F0}/{RoadConditionSymbology.BandSteepPct:F0}/{RoadConditionSymbology.BandOverPct:F0}%·{runCount} 个同档段）");
        if (maintCount + closedCount > 0) sb.Append($"；检修 {maintCount} 条(黄) / 封闭 {closedCount} 条(红)，共 {bannedLen:F0}m 整条压色·寻径禁行");
        sb.Append("。再点一次关闭。");
        bool alarm = closedCount > 0
            || lenByClass[RoadConditionSymbology.ClassIndex(GradeBand.Over, GradeSense.Up)] > 0
            || lenByClass[RoadConditionSymbology.ClassIndex(GradeBand.Over, GradeSense.Down)] > 0;
        EditEcho(sb.ToString(), alarm ? EchoLevel.Warn : EchoLevel.Success);
        StatusMsg.Text = $"路况显示：{roads.Count} 条中线，最大纵坡 {maxAbsGrade:F1}%";
    }

    private static IReadOnlyList<Point3d> RoadRange(IReadOnlyList<Point3d> ln, int i0, int i1)
    {
        var o = new Point3d[i1 - i0 + 1];
        for (int i = 0; i < o.Length; i++) o[i] = ln[i0 + i];
        return o;
    }

    private static double RoadLength3d(IReadOnlyList<Point3d> ln, int i0, int i1)
    {
        double s = 0;
        for (int i = i0 + 1; i <= i1; i++) s += ln[i].DistanceTo(ln[i - 1]);
        return s;
    }

    /// <summary>沿中线按弧长 step 放前向 chevron(&gt;形指行进方向)，臂长 L、半宽 wd。</summary>
    private void RoadDrawDirectionChevrons(IReadOnlyList<Point3d> ln, double step, double L, double wd, Rgb c)
    {
        double acc = step;
        for (int i = 1; i < ln.Count; i++)
        {
            double x0 = ln[i - 1].X, y0 = ln[i - 1].Y, z0 = ln[i - 1].Z;
            double x1 = ln[i].X, y1 = ln[i].Y, z1 = ln[i].Z;
            double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            double seg = Math.Sqrt(dx * dx + dy * dy);
            if (seg < 1e-6) continue;
            double ux = dx / seg, uy = dy / seg, px = -uy, py = ux;
            double t = acc;
            while (t <= seg)
            {
                double f = t / seg;
                double cx = x0 + dx * f, cy = y0 + dy * f, cz = z0 + dz * f;
                double bx = cx - ux * L, by = cy - uy * L;
                RoadAddLine(RoadConditionLayer, cx, cy, cz, bx + px * wd, by + py * wd, cz, c);
                RoadAddLine(RoadConditionLayer, cx, cy, cz, bx - px * wd, by - py * wd, cz, c);
                t += step;
            }
            acc = t - seg;
        }
    }

    // ═══════════════════════ 结构路面 ═══════════════════════

    /// <summary>「结构路面」：中心线等宽外扩成闭合 ribbon overlay（24m），再点一次关闭。取中线：优先会话图边中线，否则读图层。</summary>
    private void RoadStructurePavementCmd()
    {
        if (RoadLayerCount(RoadStructurePavementLayer) > 0)
        { BeginChange(); RoadClearLayer(RoadStructurePavementLayer); RefreshScene(); EditEcho("结构路面：已关闭。"); StatusMsg.Text = "结构路面已关闭"; return; }
        var lines = new List<IReadOnlyList<Point3d>>();
        var rs = RS;
        if (rs.Graph != null && rs.Graph.EdgeCount > 0)
            foreach (var e in rs.Graph.Edges) if (e.Centerline.Count >= 2) lines.Add(e.Centerline);
        if (lines.Count == 0) lines = ReadRoadCenterlines();
        if (lines.Count == 0) { EditEcho("结构路面：未找到道路中线。先「提取道路中心线」或「基础道路网络构建」。", EchoLevel.Warn); return; }
        const double widthM = 24.0;
        var ribbons = StructurePavement.BuildRibbons(lines, widthM);
        if (ribbons.Count == 0) { EditEcho("结构路面：未生成路面带（中线退化？）。", EchoLevel.Warn); return; }
        BeginChange();
        var grey = new Rgb(120, 130, 145);
        RoadEnsureLayer(RoadStructurePavementLayer, grey, 0.5f);
        foreach (var rb in ribbons) RoadAddFlat(RoadStructurePavementLayer, rb, grey, true);
        RefreshScene();
        EditEcho($"✓ 结构路面已开（{ribbons.Count} 条路面带·{widthM:F0}m 宽）。再点一次关闭。", EchoLevel.Success);
        StatusMsg.Text = $"结构路面：{ribbons.Count} 条路面带（{widthM:F0}m 宽）";
    }
}
