using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views.Modeling;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「驱动量」（量驱动斜面模板）交互入口（忠实原 <c>InclineTemplateRunner.RunInteractive</c>）：
/// 弹参数面板采集 <see cref="InclineTemplateInput"/>；确认后 预处理(bbox/现状面采样) → 角部搭接 → 建剖面 → Stage1(年产量逼近)
/// → Stage2(逐层增量) → 前界单调体检 → 斜面约束块体 → 结果区 → 构面+交线入图(驱动量_斜面) → Stage2 前界(驱动量_Stage2)
/// → 推进调整联动 / 添加控制线。
/// 【登记的差异】Kylin 无实体 handle / PMBI：产物直接作 MeshEntity/PolylineEntity 入场景，自记实体引用整层替换；
/// 块体是单元表（无八叉树细分）；「生成采区台阶面」由下一单元（台阶生成器）接入，本单元只回显未接入。
/// </summary>
public partial class MainWindow
{
    private InclineTemplateWindow? _inclineWin;
    private readonly List<SceneEntity> _inclineOwnedSurf = new(), _inclineOwnedS2 = new(), _inclineOwnedBench = new();

    private void InclineTemplateCmd()
    {
        if (_inclineWin != null)
        {
            try { _inclineWin.Activate(); EditEcho("驱动量：面板已在，切到它。"); return; }
            catch { _inclineWin = null; }
        }
        EditEcho("驱动量：打开参数面板…");
        Modeling.BlockModelStore.Adopt(MdlCtx());   // 主窗口松散导入的块体也收进块体仓，面板才列得到
        var h = new InclineTemplateHandlers
        {
            FirstSelectedMesh = excl => _selected.OfType<MeshEntity>().FirstOrDefault(m => !excl.Contains(m) && m.Tris.Count > 0),
            FirstSelectedWorkLine = excl =>
            {
                var wl = _selected.OfType<PolylineEntity>().FirstOrDefault(p => !excl.Contains(p) && p.LayerName == WorkLineLayer && p.Points.Count >= 2)
                      ?? _selected.OfType<PolylineEntity>().FirstOrDefault(p => !excl.Contains(p) && p.Points.Count >= 2);
                if (wl == null) return (null, null, "未选中工作线");
                return (WorkLineSamplesOf(wl), wl, wl.LayerName == WorkLineLayer ? "工作线" : "选中多段线（左法向推进）");
            },
            RefreshWorkLine = wl => WorkLineSamplesOf(wl),
            FirstSelectedPolyline = excl => _selected.OfType<PolylineEntity>().FirstOrDefault(p => !excl.Contains(p) && p.Points.Count >= 2),
            SetVisible = (e, v) => { e.Visible = v; RefreshScene(); },
            DeleteEntities = ents => { BeginChange(); foreach (var e in ents) { _scene.Remove(e); _selected.Remove(e); } RefreshScene(); },
            SetLayerEntitiesVisible = (lay, v) => { foreach (var e in _scene.Entities) if (e.LayerName == lay) e.Visible = v; RefreshScene(); },
            RemoveLayerEntities = lay => { int n = _scene.Entities.RemoveAll(e => e.LayerName == lay); if (n > 0) { _selected.RemoveAll(e => e.LayerName == lay); _inclineOwnedS2.RemoveAll(e => e.LayerName == lay); RefreshScene(); } return n; },
            RegionNames = () =>
            {
                var l = new List<string>();
                try { if (_geoDb != null) foreach (var r in Data.MineableRegions.List(_geoDb.Connection)) if (!string.IsNullOrEmpty(r.Name)) l.Add(r.Name); } catch { }
                return l;
            },
            GeoDbSeams = () =>
            {
                var l = new List<(string, int, double[], int[], double[], int[])>();
                try
                {
                    if (_geoDb == null) return l;
                    var conn = _geoDb.Connection;
                    var all = Data.GeoDbViews.VdAllSurfaces(conn);
                    foreach (var g in all.Where(s => (s.Role == "roof" || s.Role == "floor") && !string.IsNullOrWhiteSpace(s.SeamName)).GroupBy(s => s.SeamName))
                    {
                        var roof = g.FirstOrDefault(x => x.Role == "roof"); var floor = g.FirstOrDefault(x => x.Role == "floor");
                        if (roof == null || floor == null) continue;
                        if (!Data.GeoDbViews.VdTryGetGeometry(conn, roof.Id, out var rv, out var rt) || rv.Length < 9) continue;
                        if (!Data.GeoDbViews.VdTryGetGeometry(conn, floor.Id, out var fv, out var ft) || fv.Length < 9) continue;
                        l.Add((g.Key, g.Min(x => x.SeamOrder), rv, rt, fv, ft));
                    }
                }
                catch (Exception ex) { EditEcho("驱动量：读地质库煤层面失败 —— " + ex.Message, EchoLevel.Warn); }
                return l;
            },
            Confirmed = OnInclineConfirmed,
            Echo = (m, warn) => EditEcho(m, warn ? EchoLevel.Warn : EchoLevel.Info),
        };
        var win = new InclineTemplateWindow(h);
        _inclineWin = win;
        win.Closed += (_, _) => { if (ReferenceEquals(_inclineWin, win)) _inclineWin = null; };
        win.Show(this);
    }

    /// <summary>工作线实体 → 投影几何（基线 + 同位置结束线 / 回转中心；非「工作线」层的多段线按左法向推进兜底）。</summary>
    private WorkLineSamples? WorkLineSamplesOf(PolylineEntity wl)
    {
        if (wl.Points.Count < 2) return null;
        PolylineEntity? endLine = null; PointEntity? pivot = null;
        if (wl.LayerName == WorkLineLayer)
            foreach (var e in _scene.Entities)
            {
                if (e is PolylineEntity p2 && p2.LayerName == WorkLineLayer + "_结束线" && NearWorkLine(p2, wl)) endLine ??= p2;
                else if (e is PointEntity pt && pt.LayerName == WorkLineLayer + "_回转中心" && NearWorkLine(pt, wl)) pivot ??= pt;
            }
        return WorkLineSamples.FromWorkLine(wl.Points, wl.Elevation, pivot != null, endLine?.Points, pivot != null ? (pivot.X, pivot.Y) : null,
                                            fallbackDir: (-(wl.Points[^1].y - wl.Points[0].y), wl.Points[^1].x - wl.Points[0].x), closed: wl.Closed);
    }

    /// <summary>整层替换（先入新 → 再删旧 = 自记 ∪ 层查询，排除本次新入）：保证每条工作线任何时刻只有一张模板。</summary>
    private void InclineReplaceLayer(string layer, List<SceneEntity> owned, IReadOnlyList<SceneEntity> fresh, float lr, float lg, float lb, bool keepLayers = false)
    {
        _layers.EnsureImported(layer, lr, lg, lb);
        var del = new HashSet<SceneEntity>(owned);
        foreach (var e in _scene.Entities) if (e.LayerName == layer) del.Add(e);
        foreach (var e in fresh) del.Remove(e);
        foreach (var e in del) { _scene.Remove(e); _selected.Remove(e); }
        foreach (var e in fresh) { if (!keepLayers) e.LayerName = layer; _scene.Add(e); }
        owned.Clear(); owned.AddRange(fresh);
        RefreshScene();
    }

    private static PolylineEntity InclinePoly(IReadOnlyList<(double X, double Y, double Z)> line, (byte r, byte g, byte b) c, string layer)
    {
        var pl = new PolylineEntity { LayerName = layer, Elevation = line[0].Z, Zs = new List<double>(line.Count), Cr = c.r / 255f, Cg = c.g / 255f, Cb = c.b / 255f };
        foreach (var p in line) { pl.Points.Add((p.X, p.Y)); pl.Zs.Add(p.Z - line[0].Z); }
        return pl;
    }

    /// <summary>斜面三角网(iris) + 坡顶/坡底线 + 各层交线(pink)。角部闭合 = 构面时端部已延长、两斜面直接相交。</summary>
    private static List<SceneEntity> InclineSurfaceEntities(IReadOnlyList<InclineSurface> surfaces)
    {
        var iris = ((byte)130, (byte)122, (byte)196); var edge = ((byte)120, (byte)140, (byte)200); var pink = ((byte)212, (byte)83, (byte)126);
        var l = new List<SceneEntity>();
        int k = 0;
        foreach (var s in surfaces)
        {
            k++;
            if (s.Indices.Count >= 3)
            {
                var (v, t) = s.ToWorldMesh();
                l.Add(new MeshEntity($"驱动量_斜面_{k}", v, t) { LayerName = InclineLayers.Surface, Cr = iris.Item1 / 255f, Cg = iris.Item2 / 255f, Cb = iris.Item3 / 255f });
            }
            if (s.Crest.Count > 1) l.Add(InclinePoly(s.Crest, edge, InclineLayers.Surface));
            if (s.Toe.Count > 1) l.Add(InclinePoly(s.Toe, edge, InclineLayers.Surface));
            foreach (var si in s.Intersections)
            {
                if (si.RoofLine.Count > 1) l.Add(InclinePoly(si.RoofLine, pink, InclineLayers.Surface));
                if (si.FloorLine.Count > 1) l.Add(InclinePoly(si.FloorLine, pink, InclineLayers.Surface));
            }
        }
        return l;
    }

    /// <summary>Stage2 各层前界交线（每层 advance=d_seam，分色）。端部延长口径同主斜面。</summary>
    private static List<SceneEntity> InclineStage2Entities(Stage2Result s2, IReadOnlyList<WorkLineSamples> wls, double alphaDeg, double zTop, double zFloor,
                                                          IReadOnlyList<SeamSurfaces> seams, IReadOnlyList<WorkLineCornerJoiner.CornerLink> cornerLinks)
    {
        var palette = new (byte r, byte g, byte b)[] { (212, 83, 126), (29, 158, 117), (216, 90, 48), (85, 150, 230), (180, 120, 40), (150, 80, 180) };
        var l = new List<SceneEntity>();
        var dArr = new double[wls.Count];
        for (int m = 0; m < s2.Seams.Count && m < seams.Count; m++)
        {
            var c = palette[m % palette.Length];
            double dseam = s2.Seams[m].Stage2Advance;
            var oneSeam = new List<SeamSurfaces> { seams[m] };
            for (int wi = 0; wi < wls.Count; wi++) dArr[wi] = dseam;
            var ext = WorkLineCornerJoiner.ComputeEndExtensions(wls, cornerLinks, alphaDeg, zTop, zFloor, dArr);
            for (int wi = 0; wi < wls.Count; wi++)
            {
                var surf = InclineSurfaceBuilder.BuildForWorkLine(wls[wi], alphaDeg, zTop, zFloor, dseam, oneSeam, ext[wi].Start, ext[wi].End);
                if (!surf.Success) continue;
                foreach (var si in surf.Intersections)
                {
                    if (si.RoofLine.Count > 1) l.Add(InclinePoly(si.RoofLine, c, InclineLayers.Stage2));
                    if (si.FloorLine.Count > 1) l.Add(InclinePoly(si.FloorLine, c, InclineLayers.Stage2));
                }
            }
        }
        return l;
    }

    // 上次「确认」的会话态（推进调整 / 控制线 / 台阶生成共用）
    private sealed class InclineSession
    {
        public InclineTemplateInput Input = null!;
        public InclinePreprocessResult Pre = null!;
        public List<WorkLineSamples> Wls = new();
        public List<WorkLineCornerJoiner.CornerLink> CornerLinks = new();
        public CoalProfile? Prof;
        public Stage2Result? S2;
        public double Advance, ZTopBig, ZFloorBig;
    }
    private InclineSession? _inclineSession;
    internal bool InclineHasSession => _inclineSession != null;
    internal double InclineAdvance => _inclineSession?.Advance ?? 0;
    internal RockProfile? InclineRockProfile => _inclineSession?.Prof?.Rock;

    private void OnInclineConfirmed(InclineTemplateInput input, InclineTemplateWindow dlg)
    {
        EditEcho($"> 驱动量（{input.Summary}）：输入已采集。", EchoLevel.Success);
        int i = 1;
        foreach (var s in input.Seams)
            EditEcho($"  层{i++}「{s.Name}」: 顶板{(s.RoofVerts != null ? $"{s.RoofSource}({s.RoofVerts.Length / 3}点)" : "未选")} / 底板{(s.FloorVerts != null ? $"{s.FloorSource}({s.FloorVerts.Length / 3}点)" : "未选")} / 属性「{(string.IsNullOrEmpty(s.Attribute) ? "未选" : s.Attribute)}」· 类别「{(string.IsNullOrEmpty(s.Category) ? "未选" : s.Category)}」/ 容重 {s.Density:0.##} / 增量 {s.IncrementWt:0.#}万t");

        // 第 2 步：预处理
        var pre = InclineTemplatePreprocess.Run(input);
        if (!pre.Success) { EditEcho($"  预处理失败：{pre.Error}。", EchoLevel.Warn); return; }
        double cx = (pre.MinX + pre.MaxX) * 0.5, cy = (pre.MinY + pre.MaxY) * 0.5;
        string surfZ = pre.CurrentSurface!.TrySampleZ(cx, cy, out double sz) ? $"中心处现状面 Z≈{sz:0.##}" : "中心处现状面无覆盖(洞/外侧)";
        EditEcho($"  预处理✓：合并包围盒 z_top={pre.ZTop:0.##} / z_floor={pre.ZFloor:0.##}（厚 {pre.ZTop - pre.ZFloor:0.##}m）| XY[{pre.MinX:0.#}~{pre.MaxX:0.#}, {pre.MinY:0.#}~{pre.MaxY:0.#}] | 就绪层 {pre.ReadySeamCount}/{pre.Seams.Count} | {surfZ}。", EchoLevel.Success);

        // 工作线（一次，供 Stage1 + 构面共用）—— 角部搭接会改基线，故拷贝一份
        var wls = new List<WorkLineSamples>();
        int wlIdx = 0;
        foreach (var src in input.WorkLines)
        {
            wlIdx++;
            if (src == null || !src.Success) { EditEcho($"  工作线{wlIdx} 无效：{src?.Error}。", EchoLevel.Warn); continue; }
            var wl = new WorkLineSamples { Success = true, AdvanceMode = src.AdvanceMode, DirMode = src.DirMode, Closed = src.Closed, HasFanParams = src.HasFanParams, RotDir = src.RotDir, PivotX = src.PivotX, PivotY = src.PivotY, PivotZ = src.PivotZ };
            wl.Baseline.AddRange(src.Baseline); wl.Samples.AddRange(src.Samples);
            wls.Add(wl);
            double adx = 0, ady = 0; foreach (var s in wl.Samples) { adx += s.Dx; ady += s.Dy; }
            double al = Math.Sqrt(adx * adx + ady * ady); if (al > 1e-9) { adx /= al; ady /= al; }
            double bcx = 0, bcy = 0; foreach (var b in wl.Baseline) { bcx += b.X; bcy += b.Y; }
            if (wl.Baseline.Count > 0) { bcx /= wl.Baseline.Count; bcy /= wl.Baseline.Count; }
            double deg = Math.Atan2(ady, adx) * 180.0 / Math.PI;
            EditEcho($"  工作线{wlIdx} 诊断：基线中心({bcx:0.#},{bcy:0.#}) 推进方向 di=({adx:0.###},{ady:0.###}) 方位{deg:0.#}° 段数{wl.Samples.Count} 方式{(wl.AdvanceMode == 2 ? "扇形" : "平行")}/{(wl.DirMode == 1 ? "逐段" : "统一")}。");
        }
        if (wls.Count == 0) { EditEcho("  没有有效工作线。", EchoLevel.Warn); return; }

        var cornerLinks = new List<WorkLineCornerJoiner.CornerLink>();
        if (wls.Count >= 2)
        {
            cornerLinks = WorkLineCornerJoiner.JoinCorners(wls);
            if (cornerLinks.Count > 0) EditEcho($"  角部搭接：{cornerLinks.Count} 处成角工作线末端外推到交点，两斜面端部将直接延长相交闭合角部。");
        }

        // 第 4–5 步：Stage1 + Stage2
        double advance = 0;
        Stage2Result? s2 = null;
        CoalProfile? prof = null;
        var blk = Modeling.BlockModelStore.Models.FirstOrDefault(m => m.Name == input.BlockModelName);
        if (blk == null) EditEcho($"  Stage1/2 跳过：未找到块体「{input.BlockModelName}」→ 只画基准位 d=0。", EchoLevel.Warn);
        else
        {
            var bsrc = InclineBlockSource.FromMeta(blk);
            prof = InclineVolumeEngine.BuildProfile(bsrc, wls, pre.CurrentSurface, pre.Seams, input.SlopeAngleDeg, input.CoalMode, input.CoalThreshold, input.BenchHeight > 0 ? input.BenchHeight : 15.0);
            if (!prof.Success) EditEcho($"  Stage1/2 跳过：{prof.Error} → 只画基准位 d=0。", EchoLevel.Warn);
            else
            {
                foreach (var n in prof.Notes) EditEcho("  " + n, n.StartsWith("◆") ? EchoLevel.Error : EchoLevel.Warn);
                if (prof.Rock is { Success: true } rk) EditEcho("  " + rk.Summary(), rk.UnclassifiedCells > 0 || rk.CrossedColumns > 0 ? EchoLevel.Warn : EchoLevel.Info);

                double tolPct = input.ApproachThresholdPct > 0 ? input.ApproachThresholdPct : 5.0;
                var s1 = InclineVolumeEngine.SolveStage1(prof, input.AnnualProductionWt, tolPct);
                advance = s1.Advance;
                EditEcho($"  Stage1✓（Δ={s1.SliceWidth:0.#}m）：统一推进 d={advance:0.#}m 时 采出煤 {s1.TotalCoalWt:0.0}万t（目标 {input.AnnualProductionWt:0.0}万t，偏差 {s1.DeviationPct:+0.0;-0.0}%，容差±{tolPct:0.#}%）{(s1.WithinTol ? " ✓达标" : $" ⚠超差(可采上限 {s1.MaxCoalWt:0.0}万t)")}（煤 {s1.CoalCellCount:N0} cell）。",
                    s1.WithinTol ? EchoLevel.Success : EchoLevel.Warn);
                s2 = InclineVolumeEngine.SolveStage2(prof, advance, tolPct, input.RecoveryTotalWt);
                foreach (var ss in s2.Seams)
                    EditEcho($"    Stage2 层「{ss.Name}」: 前界 d_seam={ss.Stage2Advance:0.#}m（+{ss.Stage2Advance - advance:0.#}m）→ 增量 {ss.AchievedWt:0.0}/{ss.IncrementWt:0.0}万t（偏差 {ss.DeviationPct:+0.0;-0.0}%）{(ss.WithinTol ? " ✓" : " ⚠超±" + tolPct.ToString("0.#") + "%")}"
                             + (ss.Reached ? "" : "【该层前界之外的煤已推到底仍不够，达成量就是这一层的上限】"), ss.WithinTol ? EchoLevel.Info : EchoLevel.Warn);
                EditEcho("    " + s2.TotalCheckText, s2.SeamTargetsMatchTotal ? EchoLevel.Info : EchoLevel.Warn);
                {
                    double mrx = 0, mry = 0; int mrn = 0;
                    foreach (var wl0 in wls) foreach (var b0 in wl0.Baseline) { mrx += b0.X; mry += b0.Y; mrn++; }
                    if (mrn > 0) { mrx /= mrn; mry /= mrn; }
                    var mono = InclineVolumeEngine.CheckFrontMonotonicity(prof, s2, pre.Seams, mrx, mry, advance);
                    EditEcho("    " + mono.Text, mono.Ok ? EchoLevel.Info : EchoLevel.Warn);
                }

                // 斜面约束块体
                if (input.ConstrainBlockModel) ApplyInclineConstraint(blk, bsrc, wls, pre, input, advance, s2);
                else if (blk.LastInclineUndo != null)
                {
                    int n = InclineConstraint.Undo(blk);
                    Modeling.BlockModelStore.RefreshDisplay(MdlCtx(), blk);
                    EditEcho($"  斜面约束已撤销（勾掉「约束块体」重跑）：恢复 {n:N0} 块。");
                }

                try { dlg.ShowResults(s1, s2, input, advance); }
                catch (Exception rex) { EditEcho($"  采出量表显示失败：{rex.Message}", EchoLevel.Warn); }
            }
        }

        // 第 3 步：在 Stage1 的 d 处构面 + 交线（基准 = 煤层顶底板合并包围盒略放）
        double topRef = pre.ZTop, botRef = pre.ZFloor;
        double zMargin = Math.Max(2.0, 0.05 * (topRef - botRef));
        double zTopBig = topRef + zMargin, zFloorBig = botRef - zMargin;
        var advByLine = new double[wls.Count];
        for (int wi = 0; wi < wls.Count; wi++) advByLine[wi] = advance;
        var cornerExt = WorkLineCornerJoiner.ComputeEndExtensions(wls, cornerLinks, input.SlopeAngleDeg, zTopBig, zFloorBig, advByLine);
        var surfaces = new List<InclineSurface>();
        for (int wi = 0; wi < wls.Count; wi++)
        {
            var surf = InclineSurfaceBuilder.BuildForWorkLine(wls[wi], input.SlopeAngleDeg, zTopBig, zFloorBig, advance, pre.Seams, cornerExt[wi].Start, cornerExt[wi].End);
            if (surf.Success) surfaces.Add(surf);
        }
        if (surfaces.Count == 0) { EditEcho("  构面：没产出任何斜面。", EchoLevel.Warn); return; }
        int triTotal = 0, xlineTotal = 0;
        foreach (var s in surfaces)
        {
            triTotal += s.Indices.Count / 3;
            foreach (var si in s.Intersections) xlineTotal += (si.RoofLine.Count > 1 ? 1 : 0) + (si.FloorLine.Count > 1 ? 1 : 0);
        }
        BeginChange();
        InclineReplaceLayer(InclineLayers.Surface, _inclineOwnedSurf, InclineSurfaceEntities(surfaces), 130 / 255f, 122 / 255f, 196 / 255f);
        EditEcho($"  Stage1 斜面✓(layer『{InclineLayers.Surface}』): {surfaces.Count} 张（{triTotal} 三角）+ {xlineTotal} 条交线入图（推进 d={advance:0.#}m，前进指向坡顶）。", EchoLevel.Success);

        if (s2 != null && s2.Success && s2.Seams.Count > 0)
        {
            InclineReplaceLayer(InclineLayers.Stage2, _inclineOwnedS2, InclineStage2Entities(s2, wls, input.SlopeAngleDeg, zTopBig, zFloorBig, pre.Seams, cornerLinks), 212 / 255f, 83 / 255f, 126 / 255f);
            EditEcho($"  Stage2 前界✓(layer『{InclineLayers.Stage2}』): {s2.Seams.Count} 层各自前界交线入图（分色，d_seam ≥ d）。", EchoLevel.Success);
        }

        var session = new InclineSession { Input = input, Pre = pre, Wls = wls, CornerLinks = cornerLinks, Prof = prof, S2 = s2, Advance = advance, ZTopBig = zTopBig, ZFloorBig = zFloorBig };
        _inclineSession = session;

        // 「推进调整」
        if (prof != null && prof.Success)
        {
            var initD = new double[wls.Count];
            for (int wi2 = 0; wi2 < wls.Count; wi2++) initD[wi2] = advance;
            dlg.SetupAdjust(prof, input.AnnualProductionWt, initD, input.ApproachThresholdPct > 0 ? input.ApproachThresholdPct : 5.0);
            dlg.OnAdvanceAdjusted = perLineD =>
            {
                try
                {
                    var dAdj = new double[wls.Count];
                    for (int wi2 = 0; wi2 < wls.Count; wi2++) dAdj[wi2] = (wi2 < perLineD.Length) ? perLineD[wi2] : advance;
                    var extAdj = WorkLineCornerJoiner.ComputeEndExtensions(wls, cornerLinks, input.SlopeAngleDeg, zTopBig, zFloorBig, dAdj);
                    var surfs2 = new List<InclineSurface>();
                    for (int wi2 = 0; wi2 < wls.Count; wi2++)
                    {
                        var su = InclineSurfaceBuilder.BuildForWorkLine(wls[wi2], input.SlopeAngleDeg, zTopBig, zFloorBig, dAdj[wi2], pre.Seams, extAdj[wi2].Start, extAdj[wi2].End);
                        if (su.Success) surfs2.Add(su);
                    }
                    if (surfs2.Count > 0) InclineReplaceLayer(InclineLayers.Surface, _inclineOwnedSurf, InclineSurfaceEntities(surfs2), 130 / 255f, 122 / 255f, 196 / 255f);
                }
                catch (Exception ex) { EditEcho("  推进调整重画失败：" + ex.Message, EchoLevel.Warn); }
            };
        }

        // 「添加控制线」：为下一个空槽位（线1末端→线1首端→线2末端…）生成一条蓝色控制线
        dlg.OnAddControlLineRequested = () =>
        {
            var perLine = new int[wls.Count];
            foreach (var bb in dlg.BoundaryBindings) if (bb.Line >= 0 && bb.Line < perLine.Length) perLine[bb.Line]++;
            int target = -1; bool atEnd = true;
            for (int li = 0; li < wls.Count && target < 0; li++)
            {
                if (perLine[li] == 0) { target = li; atEnd = true; }
                else if (perLine[li] == 1) { target = li; atEnd = false; }
            }
            if (target < 0) { EditEcho("  每条工作线两端都已有控制线（可在视口直接编辑，或「清空」后重加）。", EchoLevel.Warn); return; }
            var twl = wls[target];
            int bn = twl.Baseline.Count;
            var anchor = atEnd ? twl.Baseline[bn - 1] : twl.Baseline[0];
            var smp = atEnd ? twl.Samples[twl.Samples.Count - 1] : twl.Samples[0];
            double ddx = smp.Dx, ddy = smp.Dy;
            double dl2 = Math.Sqrt(ddx * ddx + ddy * ddy);
            if (dl2 < 1e-9) { ddx = 1; ddy = 0; } else { ddx /= dl2; ddy /= dl2; }
            double tanW2 = Math.Tan(Math.Max(1.0, Math.Min(45.0, input.FinalSlopeAngleDeg)) * Math.PI / 180.0);
            double fwd = advance + (pre.ZTop - pre.ZFloor) / tanW2 + 200.0, back = 100.0;
            BeginChange();
            _layers.EnsureImported(InclineLayers.Control, 43 / 255f, 95 / 255f, 217 / 255f);
            var pl = InclinePoly(new[] { (anchor.X - back * ddx, anchor.Y - back * ddy, anchor.Z), (anchor.X + fwd * ddx, anchor.Y + fwd * ddy, anchor.Z) }, (43, 95, 217), InclineLayers.Control);
            _scene.Add(pl); RefreshScene();
            dlg.RegisterBoundary(pl, target, created: true);
            EditEcho($"  控制线✓：已为 工作线{target + 1}·{(atEnd ? "末端" : "首端")} 添加（蓝色·沿推进方向过端点·绑定该线）。拖夹点=旋转/伸缩，整体可平移；生成台阶时严格按它裁剪。", EchoLevel.Success);
        };

        // 「生成采区台阶面」（创建工程位置·块1）：用前界(advance)+煤层顶底板，沿煤层自下而上生成台阶线。
        dlg.OnGenerateBenchTemplate = bp => GenerateBenchTemplate(dlg, session, bp);
    }

    /// <summary>@驱动量示例：合成两层煤块体（块体仓「自检块体」，属性「岩性」0岩/1煤A/2煤B 带类别名表）+ 顶底板/现状面三角网 + 工作线，开面板按示例填好并确认（合成几何，非业务数据）。</summary>
    private void SelftestSampleIncline()
    {
        const double sz = 10;
        var blocks = new List<BlockModel.Block>(); var lith = new List<double>();
        for (int i = 0; i < 20; i++) for (int j = 0; j < 10; j++) for (int k = 0; k < 5; k++)
        {
            double z = 1000 + k * sz + sz / 2;
            double code = z < 1010 ? 1 : (z >= 1020 && z < 1030 ? 2 : 0);
            blocks.Add(new BlockModel.Block { X = i * sz + sz / 2, Y = j * sz + sz / 2, Z = z, Size = sz, Grade = code });
            lith.Add(code);
        }
        var old = Modeling.BlockModelStore.Models.FirstOrDefault(m => m.Name == "自检块体");
        if (old != null) Modeling.BlockModelStore.Remove(MdlCtx(), old);
        var meta = BlockModelMeta.FromBlocks("自检块体", blocks, new Dictionary<string, double[]> { ["岩性"] = lith.ToArray() });
        var col = meta.FindColumn("岩性")!; col.IsCategorical = true; col.CategoryLabels = new[] { "岩", "煤A", "煤B" };
        meta.ActiveColormapAttribute = "岩性";
        var cc = meta.DisplayStyle.EnsureCategoryColors("岩性"); cc[0] = (0x8B, 0x93, 0xA1); cc[1] = (0xD9, 0x82, 0x2B); cc[2] = (0x3F, 0xA8, 0x7F);
        var err = Modeling.BlockModelStore.Create(MdlCtx(), meta);
        if (err != null) { StatusMsg.Text = "自检：块体入仓失败 " + err; return; }

        BeginChange();
        MeshEntity Plane(string name, string layer, double z, float r, float g, float b)
        {
            var me = new MeshEntity(name, new[] { (-50.0, -50.0, z), (250.0, -50.0, z), (250.0, 250.0, z), (-50.0, 250.0, z) }, new[] { (0, 1, 2), (0, 2, 3) }) { LayerName = layer, Cr = r, Cg = g, Cb = b };
            _scene.Add(me); return me;
        }
        _layers.EnsureImported("煤层面", 0.85f, 0.55f, 0.2f); _layers.EnsureImported("现状面", 0.45f, 0.7f, 0.45f);
        var roofA = Plane("煤A_顶板", "煤层面", 1010, 0.85f, 0.55f, 0.2f); var floorA = Plane("煤A_底板", "煤层面", 1000, 0.75f, 0.45f, 0.15f);
        var roofB = Plane("煤B_顶板", "煤层面", 1030, 0.3f, 0.7f, 0.5f); var floorB = Plane("煤B_底板", "煤层面", 1020, 0.25f, 0.6f, 0.4f);
        var surf = Plane("现状面", "现状面", 1060, 0.45f, 0.7f, 0.45f);
        var wl = new PolylineEntity { LayerName = WorkLineLayer, Elevation = 1040, Cr = 0.2f, Cg = 0.8f, Cb = 0.95f };
        wl.Points.Add((0, 0)); wl.Points.Add((0, 100));
        var end = new PolylineEntity { LayerName = WorkLineLayer + "_结束线", Elevation = 1040, Cr = 0.2f, Cg = 0.8f, Cb = 0.95f, Dash = new[] { 4.0, 2.0 } };
        end.Points.Add((50, 0)); end.Points.Add((50, 100));
        _scene.Add(wl); _scene.Add(end);
        _selected.Clear();
        RefreshScene();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Viewport.FitBounds(new[] { -60.0, -60.0, 260.0, 260.0 }), Avalonia.Threading.DispatcherPriority.Background);

        InclineTemplateCmd();
        var win = _inclineWin!;
        win.SelftestSetSeams("煤A", "煤B");
        win.SelftestSelectBlock("自检块体");
        win.SelftestSetSeamGeometry(0, roofA, floorA); win.SelftestSetSeamRule(0, "岩性", "煤A", 1.3, 3);
        win.SelftestSetSeamGeometry(1, roofB, floorB); win.SelftestSetSeamRule(1, "岩性", "煤B", 1.3, 1);
        win.SelftestSetCurrentSurface(surf);
        win.SelftestAddWorkLine(WorkLineSamplesOf(wl)!, wl);
        // 每 10m 切片 = 10 列 × 2 层 × 1000m³ × 1.3 = 2.6 万t → 年产量 10.4 万t；回采增量 3+1=4
        win.SelftestSetGlobals(annual: 10.4, recovery: 4, tol: 5, coalMode: 0, coalThr: 0, benchH: 10, berm: 20, constrain: true);
        win.SelftestConfirm();
        StatusMsg.Text = $"自检：驱动量示例已确认（块体 {blocks.Count} 块 · 2 层煤 · 工作线 1 条）｜{(_inclineSession != null ? $"d={_inclineSession.Advance:0.#}m · α={win.AlphaText}°" : "未解出")}";
    }

    /// <summary>「生成采区台阶面」：Build → dump 诊断 JSON / 离线用例 → 按控制线裁剪 → 落交接单 → 端帮预判（只算不落地）→ 入图 台阶_*。</summary>
    private void GenerateBenchTemplate(InclineTemplateWindow dlg, InclineSession ss, BenchTemplateParams bp)
    {
        try
        {
            var input = ss.Input; var pre = ss.Pre; var wls = ss.Wls; double advance = ss.Advance;
            var dSeamBySeam = new double[pre.Seams.Count];
            for (int m = 0; m < dSeamBySeam.Length; m++) dSeamBySeam[m] = advance;
            if (ss.S2 != null && ss.S2.Success)
                for (int m = 0; m < ss.S2.Seams.Count && m < dSeamBySeam.Length; m++) dSeamBySeam[m] = ss.S2.Seams[m].Stage2Advance;

            double zdMax = double.NegativeInfinity, zdMin = double.PositiveInfinity;
            foreach (var wl0 in wls) if (wl0?.Baseline is { Count: > 0 })
            { double zd = 0; foreach (var b in wl0.Baseline) zd += b.Z; zd /= wl0.Baseline.Count; if (zd > zdMax) zdMax = zd; if (zd < zdMin) zdMin = zd; }
            double tanAlpha = Math.Tan(Math.Max(1.0, Math.Min(89.0, input.SlopeAngleDeg)) * Math.PI / 180.0);
            double bandDown = (zdMax - pre.ZFloor) / Math.Max(1e-6, tanAlpha);
            EditEcho($"  台阶诊断：煤层 bbox z[{pre.ZFloor:0.#},{pre.ZTop:0.#}]，工作线标高 z[{zdMin:0.#},{zdMax:0.#}]，现状面 z[{(pre.CurrentSurface?.MinZ ?? 0):0.#},{(pre.CurrentSurface?.MaxZ ?? 0):0.#}]；α={input.SlopeAngleDeg:0.#}° → 工作帮纵向跨度≈{bandDown:0.#}m（帮高{zdMax - pre.ZFloor:0.#}m/tanα）。");

            var res = BenchTemplateBuilder.Build(wls, pre.Seams, pre.CurrentSurface, advance, dSeamBySeam, input.SlopeAngleDeg, ss.ZTopBig, ss.ZFloorBig, bp, ss.CornerLinks);
            if (!res.Success) { EditEcho($"  生成采区台阶面失败：{res.Error}。", EchoLevel.Warn); return; }
            if (!string.IsNullOrEmpty(res.Diag)) EditEcho("  " + res.Diag);
            DumpBenchesJson(res, wls, input.SlopeAngleDeg, advance, dSeamBySeam, bp, ss.ZTopBig, ss.ZFloorBig);
            EditEcho($"  台阶已 dump → {System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pitmine_benchdump", "REAL_latest.json")}");

            // 边界/控制线：按实体直读活几何（用户可能刚用夹点调过），带工作线绑定
            var boundPairs = new List<(double[] Xyz, int Line)>();
            var boundEnts = new HashSet<SceneEntity>();
            foreach (var (ent, line) in dlg.BoundaryBindings)
            {
                boundEnts.Add(ent);
                if (ent.Points.Count < 2) continue;
                var xyz = new double[ent.Points.Count * 3];
                for (int i = 0; i < ent.Points.Count; i++) { xyz[i * 3] = ent.Points[i].x; xyz[i * 3 + 1] = ent.Points[i].y; xyz[i * 3 + 2] = ent.ZAt(i); }
                boundPairs.Add((xyz, line));
            }

            // 离线用例：这一跑的全部输入写盘（只写文件、不入图、异常全吞）
            {
                var kase = new InclineCase
                {
                    Note = $"REAL · {input.Summary}", CurrentSurface = pre.CurrentSurface, AlphaDeg = input.SlopeAngleDeg, DStage1 = advance,
                    ZTop = ss.ZTopBig, ZFloor = ss.ZFloorBig, Bench = bp, Profile = ss.Prof,
                    AnnualProductionWt = input.AnnualProductionWt, RecoveryTotalWt = input.RecoveryTotalWt, TolPct = input.ApproachThresholdPct > 0 ? input.ApproachThresholdPct : 5.0,
                };
                kase.WorkLines.AddRange(wls); kase.Seams.AddRange(pre.Seams); kase.DSeamBySeam.AddRange(dSeamBySeam);
                kase.CornerLinks.AddRange(ss.CornerLinks); kase.Boundaries.AddRange(boundPairs);
                string casePath = InclineCaseFile.DefaultDumpPath;
                bool okCase = InclineCaseFile.TrySave(casePath, kase, out string caseErr);
                EditEcho(okCase ? $"  离线用例已 dump → {casePath}（同名 .txt 是摘要；InclineCaseFile.TryLoad 即可脱 GUI 复跑）" : $"  离线用例 dump 失败：{caseErr}", okCase ? EchoLevel.Info : EchoLevel.Warn);
            }

            if (boundPairs.Count > 0)
            {
                EndWallJoiner.ClipBenchesByBoundaries(res, boundPairs, wls);
                EditEcho($"  控制线/边界线 {boundPairs.Count} 条：模板台阶线已按其严格裁剪（煤 {res.CoalCount} + 岩 {res.RockCount} 条保留）。");
            }

            // 工程位置交接单（裁剪之后才装；进程内一次性对象、不落盘）
            {
                var ho = EngineeringPositionHandoff.Build(res, wls, bp, advance, dSeamBySeam, input.SlopeAngleDeg, ss.ZTopBig, ss.ZFloorBig, boundPairs, input.Summary, pre.Seams);
                EngineeringPositionHandoff.Publish(ho);
                EditEcho(ho != null
                    ? $"  工程位置交接单✓：{ho.Summary}。到「创建工程位置」→④预览替换即可按标高格逐级换掉老台阶线。"
                    : "  工程位置交接单未生成（台阶结果为空）——「创建工程位置」将退回按图层解析（做不了逐级替换）。", ho != null ? EchoLevel.Success : EchoLevel.Warn);
                _epWin?.TryAdoptHandoff(quiet: true);
            }

            // 端帮预判（只算不落地）：视图多段线（排除本功能自身输出 + 面板已拾取实体 + 控制线）
            if (bp.JoinEndWall)
            {
                try
                {
                    var excl = new HashSet<SceneEntity>(dlg.PickedEntities);
                    foreach (var e in _inclineOwnedSurf) excl.Add(e);
                    foreach (var e in _inclineOwnedS2) excl.Add(e);
                    foreach (var e in _inclineOwnedBench) excl.Add(e);
                    foreach (var e in boundEnts) excl.Add(e);
                    var exclLayers = new HashSet<string> { InclineLayers.BenchTemplate, InclineLayers.Surface, InclineLayers.Stage2, InclineLayers.Control };
                    var sources = new List<(double[] Xyz, bool Closed)>();
                    foreach (var e in _scene.Entities)
                    {
                        if (e is not PolylineEntity pl || pl.Points.Count < 2 || excl.Contains(pl) || exclLayers.Contains(pl.LayerName) || pl.LayerName.StartsWith("台阶_", StringComparison.Ordinal)) continue;
                        if (!pl.Visible || !_layers.IsShown(pl.LayerName)) continue;
                        var xyz = new double[pl.Points.Count * 3];
                        for (int i = 0; i < pl.Points.Count; i++) { xyz[i * 3] = pl.Points[i].x; xyz[i * 3 + 1] = pl.Points[i].y; xyz[i * 3 + 2] = pl.ZAt(i); }
                        sources.Add((xyz, pl.Closed));
                    }
                    double zTol = Math.Max(2.0, bp.RockBenchH * 0.5);
                    double joinR = Math.Max(50.0, bp.MinBerm * 1.5);
                    var jw = EndWallJoiner.Run(sources, wls, advance, res, zTol, joinR, boundPairs);
                    EditEcho(jw.Success
                        ? $"  端帮预判（只算不落地）：视图多段线 {jw.SourceCount} 条，跨模板边界 {jw.ClippedLineCount} 条（保留段 {jw.Clipped.Count}、可衔接 {jw.JoinCount} 处）。裁剪与删除请到「创建工程位置」→④⑤执行。"
                        : $"  端帮预判跳过：{jw.Error}。", jw.Success ? EchoLevel.Info : EchoLevel.Warn);
                }
                catch (Exception jx) { EditEcho($"  端帮预判异常：{jx.Message}", EchoLevel.Warn); }
            }

            // 入图：按层分图层 台阶_岩台阶 / 台阶_煤名 / 台阶_搭接线（露煤绿、煤琥珀、岩青灰、搭接紫）；坡底线、坡顶线各自独立开放折线
            BeginChange();
            var fresh = new List<SceneEntity>();
            foreach (var bl in res.Benches)
            {
                string ln = EngineeringPositionHandoff.LayerOf(bl);
                (byte r, byte g, byte b) c = bl.Kind == 3 ? ((byte)0xB0, (byte)0x6A, (byte)0xD4) : bl.Kind == 1 ? ((byte)0x8B, (byte)0x93, (byte)0xA1) : bl.Kind == 2 ? ((byte)0x63, (byte)0x99, (byte)0x22) : ((byte)0xD9, (byte)0x82, (byte)0x2B);
                _layers.EnsureImported(ln, c.r / 255f, c.g / 255f, c.b / 255f);
                if (bl.Crest.Count > 1) fresh.Add(InclinePoly(bl.Crest, c, ln));
                if (bl.Toe.Count > 1) fresh.Add(InclinePoly(bl.Toe, c, ln));
            }
            InclineReplaceLayer(InclineLayers.BenchTemplate, _inclineOwnedBench, fresh, 0xD9 / 255f, 0x82 / 255f, 0x2B / 255f, keepLayers: true);
            EditEcho($"  生成采区台阶面✓(layer『台阶_*』): 自下而上走位——煤台阶 {res.CoalCount} 段（按煤台阶高分层·煤坡角）+ 露煤平盘 {res.ExposureCount} 段（各分层顶板均摊 w=d_seam−d）+ 层间岩台阶 {res.RockCount} 段（每级 rockH 岩坡角 + 工作平盘 max(最小平盘,α自然平盘)·碰上层煤底板尖灭·9之上不建）。", EchoLevel.Success);
        }
        catch (Exception gx) { EditEcho($"  生成采区台阶面异常：{gx.GetType().Name}: {gx.Message}", EchoLevel.Error); }
    }

    /// <summary>【诊断用】把台阶结果 dump 成 JSON 到临时目录（结构与原版离线 harness 一致）。只写文件、异常全吞。</summary>
    private static void DumpBenchesJson(BenchTemplateResult res, List<WorkLineSamples> wls, double alphaDeg, double advance, double[] dSeam, BenchTemplateParams bp, double zTop, double zFloor)
    {
        try
        {
            var sb = new System.Text.StringBuilder(1 << 20);
            string F(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            void Pts(IReadOnlyList<(double X, double Y, double Z)> ln)
            {
                sb.Append('[');
                for (int i = 0; i < ln.Count; i++) { if (i > 0) sb.Append(','); sb.Append('[').Append(F(ln[i].X)).Append(',').Append(F(ln[i].Y)).Append(',').Append(F(ln[i].Z)).Append(']'); }
                sb.Append(']');
            }
            sb.Append("{\"scenario\":\"REAL\",\"ok\":").Append(res.Success ? "true" : "false")
              .Append(",\"error\":\"").Append(res.Error.Replace("\"", "'")).Append('"')
              .Append(",\"counts\":{\"coal\":").Append(res.CoalCount).Append(",\"rock\":").Append(res.RockCount).Append(",\"exposure\":").Append(res.ExposureCount).Append('}')
              .Append(",\"params\":{\"alphaDeg\":").Append(F(alphaDeg)).Append(",\"dStage1\":").Append(F(advance)).Append(",\"dSeam\":[");
            for (int i = 0; i < dSeam.Length; i++) { if (i > 0) sb.Append(','); sb.Append(F(dSeam[i])); }
            sb.Append("],\"rockH\":").Append(F(bp.RockBenchH)).Append(",\"rockFaceDeg\":").Append(F(bp.RockFaceDeg))
              .Append(",\"coalFaceDeg\":").Append(F(bp.CoalFaceDeg)).Append(",\"minBerm\":").Append(F(bp.MinBerm))
              .Append(",\"zTop\":").Append(F(zTop)).Append(",\"zFloor\":").Append(F(zFloor)).Append('}')
              .Append(",\"worklines\":[");
            for (int w = 0; w < wls.Count; w++)
            {
                if (w > 0) sb.Append(',');
                sb.Append("{\"baseline\":"); Pts(wls[w]?.Baseline ?? new List<(double, double, double)>()); sb.Append('}');
            }
            sb.Append("],\"benches\":[");
            for (int i = 0; i < res.Benches.Count; i++)
            {
                var b = res.Benches[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"kind\":").Append(b.Kind).Append(",\"seam\":\"").Append(b.SeamName.Replace("\"", "'")).Append('"')
                  .Append(",\"seamIndex\":").Append(b.SeamIndex).Append(",\"level\":").Append(b.Level)
                  .Append(",\"rockLevel\":").Append(b.RockLevel).Append(",\"line\":").Append(b.LineIndex)
                  .Append(",\"firstVi\":").Append(b.FirstVi).Append(",\"lastVi\":").Append(b.LastVi)
                  .Append(",\"toe\":"); Pts(b.Toe); sb.Append(",\"crest\":"); Pts(b.Crest); sb.Append('}');
            }
            sb.Append("]}");
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pitmine_benchdump");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "REAL_latest.json"), sb.ToString());
        }
        catch { }
    }

    /// <summary>斜面约束块体：把 Stage1/2 前界做成 keepFn 切块体；重跑前先精确回退上次（幂等不叠加）；着色两阶段。</summary>
    private void ApplyInclineConstraint(BlockModelMeta blk, InclineBlockSource bsrc, List<WorkLineSamples> wls, InclinePreprocessResult pre, InclineTemplateInput input, double advance, Stage2Result? s2)
    {
        try
        {
            IReadOnlyList<double>? dseam = (s2 != null && s2.Success && s2.Seams.Count > 0) ? s2.Seams.Select(ss => ss.Stage2Advance).ToList() : null;
            var r = InclineConstraint.Apply(blk, bsrc, wls, pre.CurrentSurface, pre.Seams, input.SlopeAngleDeg, input.CoalMode, input.CoalThreshold, advance, dseam);
            blk.IsVisible = true;
            Modeling.BlockModelStore.Active = blk;
            Modeling.BlockModelStore.RefreshDisplay(MdlCtx(), blk);
            EditEcho($"  斜面约束✓（{r.ModeNote}）：删岩 {r.RockDeleted:N0} + 挖采出区外的煤 {r.CoalDeleted:N0} cell{(r.RestoredPrevious > 0 ? $"（先回退上次 {r.RestoredPrevious:N0}）" : "")}，只显示采出煤 {r.Live:N0}（阶段1琥珀/阶段2青绿）。勾掉「约束块体」重跑 = 撤销本次。", EchoLevel.Success);
        }
        catch (Exception ex) { EditEcho($"  斜面约束异常：{ex.GetType().Name}: {ex.Message}", EchoLevel.Error); }
    }
}
