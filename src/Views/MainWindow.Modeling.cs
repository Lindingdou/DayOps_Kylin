using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views.Modeling;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 三维地质建模(原 MeshEditLib + BlockModelLib)接入系统：三角网/点/多段线是场景对象, 建模·编辑·算量命令直接消费「选中对象」
/// 并把结果放回场景(可撤销/可选中/可存档), 不再经 OFF/CSV 文件中转(无选中时仍可从文件导入为场景对象)。
/// </summary>
public partial class MainWindow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private ModelingContext? _mdlCtx;
    private bool _surfCoordOn;                                                    // 实时曲面坐标开关
    private readonly Dictionary<MeshEntity, SurfaceVolume.TriGrid> _surfGrids = new();

    // ═══════════════════ 场景对象访问 ═══════════════════
    private List<MeshEntity> AllMeshes() => _scene.Entities.OfType<MeshEntity>().Where(m => m.Visible).ToList();
    private List<MeshEntity> SelectedMeshes() => _selected.OfType<MeshEntity>().ToList();
    private List<PointEntity> SelectedPoints() => _selected.OfType<PointEntity>().ToList();
    private List<PolylineEntity> SelectedPolylines() => _selected.OfType<PolylineEntity>().ToList();
    private static List<(double x, double y, double z)> Pts3(IEnumerable<PointEntity> pts) => pts.Select(p => (p.X, p.Y, p.Elevation)).ToList();
    private static List<(double x, double y, double z)> Line3(PolylineEntity pl)
    {
        var r = new List<(double x, double y, double z)>(pl.Points.Count);
        for (int i = 0; i < pl.Points.Count; i++) r.Add((pl.Points[i].x, pl.Points[i].y, pl.ZAt(i)));
        return r;
    }
    private static PolylineEntity Poly3(IReadOnlyList<(double x, double y, double z)> pts, bool closed, float r, float g, float b)
    {
        var pl = new PolylineEntity { Closed = closed, Zs = new List<double>(pts.Count), Cr = r, Cg = g, Cb = b };
        foreach (var p in pts) { pl.Points.Add((p.x, p.y)); pl.Zs.Add(p.z); }
        return pl;
    }

    private string NewMeshName(string prefix)
    {
        var names = new HashSet<string>(_scene.Entities.OfType<MeshEntity>().Select(m => m.Name));
        if (!names.Contains(prefix)) return prefix;
        for (int i = 2; ; i++) if (!names.Contains($"{prefix}{i}")) return $"{prefix}{i}";
    }

    private MeshEntity AddMesh(MeshEntity me, bool fit, bool beginChange = true)
    {
        if (beginChange) BeginChange();
        if (string.IsNullOrEmpty(me.LayerName) || me.LayerName == "0") AssignLayer(me);
        _scene.Add(me);
        RefreshScene();
        if (fit) { var b = me.Bounds; if (b.maxX > b.minX && b.maxY > b.minY) Viewport.FitBounds(new[] { b.minX, b.minY, b.maxX, b.maxY }); }
        return me;
    }

    private void ReplaceMesh(MeshEntity old, MeshEntity nu)
    {
        BeginChange();
        nu.CopyStyleFrom(old);
        int idx = _scene.Entities.IndexOf(old);
        if (idx >= 0) _scene.Entities[idx] = nu; else _scene.Add(nu);
        int si = _selected.IndexOf(old); if (si >= 0) _selected[si] = nu;
        _surfGrids.Remove(old);
        RefreshScene(); HighlightSelection();
    }

    private void SelectEntities(IReadOnlyList<SceneEntity> ents)
    {
        _selected.Clear(); _selected.AddRange(ents); HighlightSelection();
    }

    private double[]? CurrentViewBounds()
    {
        double w = ViewportHost.Bounds.Width, h = ViewportHost.Bounds.Height;
        var a = Viewport.ScreenToWorld(0, h); var b = Viewport.ScreenToWorld(w, 0);
        if (a == null || b == null) return null;
        return new[] { Math.Min(a.Value.x, b.Value.x), Math.Min(a.Value.y, b.Value.y), Math.Max(a.Value.x, b.Value.x), Math.Max(a.Value.y, b.Value.y) };
    }

    private Task<(double x, double y)?> PickWorldPointAsync(string prompt)
    {
        var tcs = new TaskCompletionSource<(double x, double y)?>();
        _oneShotPick = (x, y) => tcs.TrySetResult(double.IsNaN(x) ? null : (x, y));
        StatusMsg.Text = string.IsNullOrEmpty(prompt) ? "在视口中点击拾取位置（Esc 取消）" : prompt;
        Activate();
        return tcs.Task;
    }

    private async Task<SceneEntity?> PickEntityAsync(string prompt, Func<SceneEntity, bool>? filter)
    {
        var p = await PickWorldPointAsync(prompt);
        if (p == null) return null;
        double tol = SnapTolWorld(_lastPointer) * 2;
        SceneEntity? best = null; double bd = tol;
        foreach (var e in _scene.Entities)
        {
            if (!e.Visible || (filter != null && !filter(e)) || !_layers.IsSelectable(e.LayerName)) continue;
            double d = e.DistanceTo(p.Value.x, p.Value.y);
            if (d <= bd) { bd = d; best = e; }
        }
        return best;
    }

    /// <summary>从 OFF 文件导入若干三角网为场景对象(名=文件名)。</summary>
    private async Task<List<MeshEntity>> ImportOffAsMeshesAsync(string title, bool multiple)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = title, AllowMultiple = multiple, FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } } });
        var res = new List<MeshEntity>();
        if (files.Count == 0) return res;
        BeginChange();
        foreach (var f in files)
        {
            string text;
            try { text = System.IO.File.ReadAllText(f.Path.LocalPath); } catch { continue; }
            var (v, t) = MeshMetrics.ParseOff(text);
            if (t.Count == 0) continue;
            var me = new MeshEntity(NewMeshName(System.IO.Path.GetFileNameWithoutExtension(f.Path.LocalPath)), v, t);
            AssignLayer(me); _scene.Add(me); res.Add(me);
        }
        RefreshScene();
        if (res.Count > 0)
        {
            var b = res[0].Bounds; Viewport.FitBounds(new[] { b.minX, b.minY, b.maxX, b.maxY });
            SelectEntities(res);
        }
        return res;
    }

    /// <summary>取输入三角网：选中的优先；不足 min 张则提示从 OFF 导入(导入即成场景对象并选中)。</summary>
    private async Task<List<MeshEntity>> PickMeshesAsync(string title, int min, int max = int.MaxValue)
    {
        var sel = SelectedMeshes();
        if (sel.Count >= min) return sel.Take(max).ToList();
        if (sel.Count == 0 && AllMeshes().Count >= min && min == 1 && AllMeshes().Count == 1) return AllMeshes();
        StatusMsg.Text = $"{title}：未选中三角网 → 从 OFF 文件导入为场景三角网";
        var imp = await ImportOffAsMeshesAsync($"{title}：选 OFF 三角网（需 {min} 份）", min > 1 || max > 1);
        return imp.Count >= min ? imp.Take(max).ToList() : new List<MeshEntity>();
    }

    /// <summary>取输入高程点：选中的 PointEntity；无则从 CSV 导入为场景点(带高程)并选中。</summary>
    private async Task<List<PointEntity>> PickPointsAsync(string title, int min)
    {
        var sel = SelectedPoints();
        if (sel.Count >= min) return sel;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = $"{title}：未选中点 → 选点 CSV (x,y[,z])", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } } });
        if (files.Count == 0) return new List<PointEntity>();
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"{title}：点导入失败 {r.Error}"; return new List<PointEntity>(); }
        BeginChange();
        var list = new List<PointEntity>();
        foreach (var (x, y, z) in r.Points) { var pe = new PointEntity { X = x, Y = y, Elevation = z }; AssignLayer(pe); _scene.Add(pe); list.Add(pe); }
        RefreshScene(); Viewport.FitBounds(r.Bounds); SelectEntities(list);
        return list;
    }

    private ModelingContext MdlCtx() => _mdlCtx ??= new ModelingContext
    {
        Owner = this,
        Status = s => StatusMsg.Text = s,
        Conn = () => EnsureGeoDb()?.Connection,
        Meshes = () => AllMeshes(),
        SelectedMeshes = () => SelectedMeshes(),
        Points = () => _scene.Entities.OfType<PointEntity>().Where(p => p.Visible).ToList(),
        SelectedPoints = () => SelectedPoints(),
        Polylines = () => _scene.Entities.OfType<PolylineEntity>().Where(p => p.Visible).ToList(),
        SelectedPolylines = () => SelectedPolylines(),
        LayerNames = () => _layers.Layers.Select(l => l.Name).ToList(),
        AddMesh = (m, fit) => AddMesh(m, fit),
        ReplaceMesh = ReplaceMesh,
        AddEntities = (ents, layer, bounds) =>
        {
            if (ents.Count == 0) return;
            BeginChange();
            if (!string.IsNullOrEmpty(layer)) { var l = _layers.Get(layer!) ?? _layers.EnsureImported(layer!, 0.3f, 0.75f, 0.95f); foreach (var e in ents) e.LayerName = l.Name; }
            else foreach (var e in ents) AssignLayer(e);
            foreach (var e in ents) _scene.Add(e);
            RefreshScene();
            if (bounds != null && bounds.Length == 4 && bounds[2] > bounds[0] && bounds[3] > bounds[1]) Viewport.FitBounds(bounds);
        },
        RemoveEntities = ents => { BeginChange(); foreach (var e in ents) { _scene.Remove(e); _selected.Remove(e); if (e is MeshEntity me) _surfGrids.Remove(me); } RefreshScene(); HighlightSelection(); },
        RemoveLayerEntities = layer => { int n = _scene.Entities.RemoveAll(e => e.LayerName == layer); if (n > 0) { _selected.RemoveAll(e => e.LayerName == layer); RefreshScene(); } return n; },
        RefreshScene = () => RefreshScene(),
        Select = ents => SelectEntities(ents),
        PickPointAsync = PickWorldPointAsync,
        PickEntityAsync = PickEntityAsync,
        ViewBounds = CurrentViewBounds,
        FitBounds = b => Viewport.FitBounds(b),
        Blocks = () => _lastBlocks,
        BlockAttrs = () => _blockAttrs,
        SetBlocks = (blocks, attrs) =>
        {
            BeginChange();
            _lastBlocks = blocks; _blockAttrs = attrs;
            if (blocks == null || blocks.Count == 0) { RenderBlocks(new List<BlockModel.Block>()); RefreshScene(); return; }
            _blockGmin = blocks.Min(b => b.Grade); _blockGmax = blocks.Max(b => b.Grade); if (_blockGmax <= _blockGmin) _blockGmax = _blockGmin + 1;
            RenderBlocks(blocks); RefreshScene();
        },
        ShowBlocks = sub => { RenderBlocks(sub); RefreshScene(); },
        SaveTextAsync = async (title, name, content) =>
        {
            string ext = System.IO.Path.GetExtension(name).TrimStart('.'); if (string.IsNullOrEmpty(ext)) ext = "csv";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, DefaultExtension = ext, SuggestedFileName = name, FileTypeChoices = new[] { new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = new[] { "*." + ext } } } });
            if (file == null) return null;
            try { System.IO.File.WriteAllText(file.Path.LocalPath, content, new System.Text.UTF8Encoding(true)); return file.Path.LocalPath; }
            catch (Exception ex) { StatusMsg.Text = $"{title}：写入失败 {ex.Message}"; return null; }
        },
        OpenFileAsync = async (title, patterns) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType(string.Join("/", patterns)) { Patterns = patterns } } });
            return files.Count == 0 ? null : files[0].Path.LocalPath;
        },
        OpenFilesAsync = async (title, patterns) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true, FileTypeFilter = new[] { new FilePickerFileType(string.Join("/", patterns)) { Patterns = patterns } } });
            return files.Select(f => f.Path.LocalPath).ToList();
        },
        RunCommand = cmd => DispatchRibbon(cmd),
    };

    // ═══════════════════ 命令派发（Ribbon Tag / 中文别名 → 场景版处理器）═══════════════════
    private async Task<bool> TryModelingCommandAsync(string cmd)
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), cmd)) return true;   // 已登记独立窗口的功能项优先走窗口
        switch (cmd)
        {
            // 建模
            case "创建三角网": case "转化为三角格网": case "三角网": case "2.5D TIN": case "转三角网": await MdlCreateTinAsync(cmd); return true;
            case "多段线嵌入三角网": case "约束三角网": case "约束Delaunay": await MdlEmbedPolylineAsync(); return true;
            case "裁剪三角网": case "闭合线裁剪面": case "裁剪面": case "三角网裁剪": await MdlClipMeshByLoopAsync(); return true;
            case "固化成体": await MdlSolidifyAsync(); return true;
            case "侧面三角网": await MdlSideSurfaceAsync(); return true;
            case "快速建模": await MdlQuickModelAsync(); return true;
            case "地质体建模": await MdlGeoModelAsync(); return true;
            case "展点": await MdlShowPointsAsync(); return true;
            case "立方体": case "球体": case "圆柱": await MdlPrimitiveAsync(cmd); return true;
            case "导入三角网": case "导入OFF": case "导入网格": await ImportOffAsMeshesAsync("导入三角网(OFF)", true); return true;
            case "导出三角网": case "导出OFF": case "导出网格": await MdlExportOffAsync(); return true;
            // 倾斜摄影
            case "加载倾斜摄影": StatusMsg.Text = "加载倾斜摄影：OSGB 倾斜摄影模型解析/纹理需原生渲染内核，本机记录为受阻；可先用「展点」/「导入三角网」加载点或三角网"; return true;
            // 点编辑
            case "修改高程点": case "修改高程": await MdlSetPointZAsync(); return true;
            case "修改点样式": case "点样式": await MdlPointStyleAsync(); return true;
            case "赋节点高程": case "点落到面上": await MdlDrapePointsAsync(cmd); return true;
            case "顶点焊接": case "网格焊接": await MdlWeldAsync(); return true;
            // 线编辑
            case "闭合线裁剪": await MdlClipLinesByLoopAsync(); return true;
            case "统一线高程": await MdlUnifyLineZAsync(); return true;
            case "抽稀等值线": SimplifyPolyline(); return true;
            case "标识起点": case "标识线序": StatusMsg.Text = $"{cmd}：原程序为功能预留(占位)，未实现"; return true;
            case "线落到面上": await MdlDrapePolylinesAsync(); return true;
            // 面编辑
            case "生成三角网边界": case "网格边界": await MdlMeshBoundaryAsync(); return true;
            case "沿线分割三角网": case "分割三角网": await MdlSplitAlongLineAsync(); return true;
            case "合并三角网": await MdlMergeMeshesAsync(); return true;
            case "面交线": case "两网交线": case "网格交线": await MdlIntersectMeshesAsync(); return true;
            case "修复拓扑关系": case "网格修复": await MdlRepairAsync(); return true;
            case "删除三角面": await MdlDeleteTrisAsync(); return true;
            case "格网质量检测": case "网格诊断": await MdlDiagnoseAsync(); return true;
            case "补洞": await MdlHoleFillAsync(); return true;
            case "网格光顺": case "光顺": await MdlSmoothAsync(); return true;
            // 体编辑
            case "两期三角网算量": await MdlCutFillAsync(); return true;
            case "分割地质体": StatusMsg.Text = "分割地质体：闭合体被任意曲面切分需鲁棒网格布尔(内核)，记录为受阻；竖直面切分请用「沿线分割三角网」"; return true;
            case "布尔-并集": case "布尔-交集": case "布尔-差集": case "布尔-补集": StatusMsg.Text = $"{cmd}：网格布尔运算需 C++ 内核(精确谓词+重网格)，本机记录为受阻"; return true;
            case "三角网体积": case "网格度量": await MdlMeshVolumeAsync(); return true;
            case "体素格网体积": await MdlVoxelVolumeAsync(); return true;
            case "实体转块体": case "离散化模型": await MdlEntityToBlocksAsync(); return true;
            // 工具
            case "构建等值线": await MdlContourAsync(); return true;
            case "创建剖面": case "网格剖面": await MdlSectionAsync(); return true;
            case "实时曲面坐标": MdlToggleSurfaceCoord(); return true;
            // 采矿模型
            case "体积算量": await MdlMeshVolumeAsync(); return true;
            case "采矿模型": StatusMsg.Text = "采矿模型：条带划分(CarveStrip)走 IPitDesignCapability 内核，本机记录为受阻"; return true;
        }
        return false;
    }

    // ═══════════════════ 建模 ═══════════════════
    private async Task MdlCreateTinAsync(string cmd)
    {
        var pts = await PickPointsAsync("创建三角网", 3);
        if (pts.Count < 3) { StatusMsg.Text = "创建三角网：需 ≥3 个点(先选中场景中的点, 或从 CSV 导入)"; return; }
        var p3 = Pts3(pts);
        var p2 = p3.Select(p => (p.x, p.y)).ToList();
        var loops = SelectedPolylines().Where(l => l.Closed && l.Points.Count >= 3).ToList();
        List<(int a, int b, int c)> tris = loops.Count > 0 ? Delaunay.TriangulateClipped(p2, loops[0].Points) : Delaunay.Triangulate(p2);
        if (tris.Count == 0) { StatusMsg.Text = "创建三角网：点太少或共线，无法剖分"; return; }
        tris = OrientUp(p3, tris);   // 统一绕向(法线朝上), 边界环/成体/内外判定都依赖一致绕向
        var me = AddMesh(new MeshEntity(NewMeshName("三角网"), p3, tris), true);
        SelectEntities(new SceneEntity[] { me });
        var st = TinSurface.Describe(p3, tris);
        StatusMsg.Text = $"创建三角网「{me.Name}」：{p3.Count} 点 → {tris.Count} 三角{(loops.Count > 0 ? "(按闭合线裁边)" : "")} · 投影面积 {st.ProjectedAreaXY:0.#} · 高程 {st.ZMin:0.#}~{st.ZMax:0.#}";
    }

    private async Task MdlEmbedPolylineAsync()
    {
        var lines = SelectedPolylines().Where(l => l.Points.Count >= 2).ToList();
        if (lines.Count == 0) { StatusMsg.Text = "多段线嵌入三角网：请先选中 ≥1 条约束线(可同时选中三角网, 否则用选中的点)"; return; }
        var meshes = SelectedMeshes();
        var p3 = meshes.Count > 0 ? meshes[0].Verts.ToList() : Pts3(SelectedPoints());
        if (p3.Count < 3) { StatusMsg.Text = "多段线嵌入三角网：需选中一张三角网或 ≥3 个点"; return; }
        var cons = new List<(int u, int v)>();
        foreach (var l in lines)
        {
            int start = p3.Count;
            for (int i = 0; i < l.Points.Count; i++) p3.Add((l.Points[i].x, l.Points[i].y, l.Has3D ? l.ZAt(i) : (MeshZ(meshes.Count > 0 ? meshes[0] : null, l.Points[i].x, l.Points[i].y) ?? l.Elevation)));
            for (int i = 0; i + 1 < l.Points.Count; i++) cons.Add((start + i, start + i + 1));
            if (l.Closed && l.Points.Count > 2) cons.Add((start + l.Points.Count - 1, start));
        }
        var tris = Delaunay.TriangulateConstrained(p3.Select(p => (p.x, p.y)).ToList(), cons);
        if (tris.Count == 0) { StatusMsg.Text = "多段线嵌入三角网：剖分失败"; return; }
        tris = OrientUp(p3, tris);
        var me = new MeshEntity(meshes.Count > 0 ? meshes[0].Name : NewMeshName("约束三角网"), p3, tris);
        if (meshes.Count > 0) ReplaceMesh(meshes[0], me); else { AddMesh(me, true); SelectEntities(new SceneEntity[] { me }); }
        StatusMsg.Text = $"多段线嵌入三角网「{me.Name}」：嵌入 {lines.Count} 条线 {cons.Count} 段约束 → {tris.Count} 三角";
    }

    private async Task MdlClipMeshByLoopAsync()
    {
        var loops = SelectedPolylines().Where(l => l.Closed && l.Points.Count >= 3).ToList();
        var meshes = await PickMeshesAsync("闭合线裁剪面", 1, 1);
        if (meshes.Count == 0) return;
        if (loops.Count == 0) { StatusMsg.Text = "闭合线裁剪面：请同时选中一条闭合多段线作裁剪边界"; return; }
        var dlg = await PromptDialog.AskAsync(this, "闭合线裁剪面", new[] { new PromptDialog.Field("side", "保留", "内侧", null, null, false, new[] { "内侧", "外侧" }) }, "按闭合线裁剪选中三角网(按三角质心判内外)");
        if (dlg == null) return;
        bool inside = dlg.S("side") == "内侧";
        var m = meshes[0];
        var (pin, pout) = MeshBoundarySplit.ByPolygon(m.Verts, m.Tris, loops[0].Points);
        var keep = inside ? pin : pout;
        if (keep.Tris.Count == 0) { StatusMsg.Text = "闭合线裁剪面：保留侧无三角"; return; }
        ReplaceMesh(m, new MeshEntity(m.Name, keep.Verts, keep.Tris));
        StatusMsg.Text = $"闭合线裁剪面「{m.Name}」：保留{(inside ? "内" : "外")}侧 {keep.Tris.Count} 三角，去除 {(inside ? pout : pin).Tris.Count}";
    }

    private async Task MdlSolidifyAsync()
    {
        var meshes = await PickMeshesAsync("固化成体", 1);
        if (meshes.Count == 0) return;
        var (verts, tris) = MeshWeld.Concat(meshes.Select(m => ((IReadOnlyList<(double x, double y, double z)>)m.Verts, (IReadOnlyList<(int a, int b, int c)>)m.Tris)).ToList());
        var mm = MeshMetrics.Compute(verts, tris);
        double diag = Math.Sqrt((mm.MaxX - mm.MinX) * (mm.MaxX - mm.MinX) + (mm.MaxY - mm.MinY) * (mm.MaxY - mm.MinY) + (mm.MaxZ - mm.MinZ) * (mm.MaxZ - mm.MinZ));
        var w = MeshWeld.Weld(verts, tris, diag > 0 ? diag * 1e-4 : 1e-6, dropDuplicateTris: false);
        var d = MeshDiagnose.Analyze(w.Verts, w.Tris);
        bool watertight = d.BoundaryEdges == 0 && d.NonManifoldEdges == 0 && w.OutputTris > 0;
        var me = AddMesh(new MeshEntity(NewMeshName("固化体"), w.Verts, w.Tris) { Cr = 0.9f, Cg = 0.6f, Cb = 0.3f }, false);
        SelectEntities(new SceneEntity[] { me });
        StatusMsg.Text = $"固化成体「{me.Name}」：{meshes.Count} 网焊成 {w.OutputTris} 三角 · {(watertight ? "水密(闭合实体)" : $"非水密(开放边 {d.BoundaryEdges}·非流形 {d.NonManifoldEdges})")} · 体积 {MeshMetrics.RobustVolume(w.Verts, w.Tris):0.#}";
    }

    private async Task MdlSideSurfaceAsync()
    {
        var lines = SelectedPolylines().Where(l => l.Points.Count >= 2).ToList();
        if (lines.Count < 2) { StatusMsg.Text = "侧面三角网：请选中 2 条多段线(顶线与底线, 高程取逐点 z 或线标高)"; return; }
        var a = Line3(lines[0]); var b = Line3(lines[1]);
        if (LayerSolid.MeanZ(a) < LayerSolid.MeanZ(b)) (a, b) = (b, a);
        var (v, t) = SideSurface.Loft(a, b, lines[0].Closed && lines[1].Closed, false);
        if (t.Count == 0) { StatusMsg.Text = "侧面三角网：放样失败(线太短或退化)"; return; }
        var me = AddMesh(new MeshEntity(NewMeshName("侧面"), v, t) { Cr = 0.8f, Cg = 0.7f, Cb = 0.4f }, false);
        SelectEntities(new SceneEntity[] { me });
        StatusMsg.Text = $"侧面三角网「{me.Name}」：顶线 {a.Count} 点 / 底线 {b.Count} 点 → {t.Count} 三角(弧长拉链放样)";
    }

    private async Task MdlQuickModelAsync()
    {
        var meshes = await PickMeshesAsync("快速建模", 2, 2);
        if (meshes.Count < 2) { if (SelectedMeshes().Count > 2) StatusMsg.Text = "快速建模：只选顶/底板面 2 张；连续多层请用「地质体建模」"; return; }
        var top = meshes[0]; var bot = meshes[1];
        if (LayerSolid.MeanZ(top.Verts) < LayerSolid.MeanZ(bot.Verts)) (top, bot) = (bot, top);
        var solid = LayerSolid.FromSurfaces(top.Verts, OrientUp(top.Verts, top.Tris), bot.Verts, OrientUp(bot.Verts, bot.Tris));
        if (solid == null) { StatusMsg.Text = "快速建模：顶/底面需为有开边的开放面（取其边界环放样侧壁）"; return; }
        var (wv, wt) = solid.Value;
        var d = MeshDiagnose.Analyze(wv, wt);
        var me = AddMesh(new MeshEntity(NewMeshName($"{top.Name}-{bot.Name} 体"), wv, wt) { Cr = 0.85f, Cg = 0.55f, Cb = 0.35f }, false);
        SelectEntities(new SceneEntity[] { me });
        StatusMsg.Text = $"快速建模「{me.Name}」：顶+底+侧壁焊成 {wt.Count} 三角 · {(d.IsClosed ? "水密(闭合地质体)" : $"非水密(开放边 {d.BoundaryEdges})")} · 体积 {MeshMetrics.RobustVolume(wv, wt):0.#}";
    }

    private async Task MdlGeoModelAsync()
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), "地质体建模")) return;   // 有独立窗口则走窗口
        var meshes = await PickMeshesAsync("地质体建模", 2);
        if (meshes.Count < 2) return;
        var ordered = meshes.OrderByDescending(m => LayerSolid.MeanZ(m.Verts)).ToList();
        var layers = LayerSolid.MultiLayer(ordered.Select(m => ((IReadOnlyList<(double x, double y, double z)>)m.Verts, (IReadOnlyList<(int a, int b, int c)>)OrientUp(m.Verts, m.Tris))).ToList());
        int ok = 0; BeginChange();
        var made = new List<SceneEntity>();
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i] == null) continue;
            var (v, t) = layers[i]!.Value;
            var me = new MeshEntity(NewMeshName($"地质体{i + 1}"), v, t) { Cr = 0.85f, Cg = (float)(0.4 + 0.15 * (i % 3)), Cb = 0.3f };
            AssignLayer(me); _scene.Add(me); made.Add(me); ok++;
        }
        RefreshScene(); SelectEntities(made);
        StatusMsg.Text = $"地质体建模：{ordered.Count} 张层位面(自上而下) → {ok}/{layers.Count} 层闭合体入场景";
    }

    private async Task MdlShowPointsAsync()
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), "展点")) return;
        var pts = await PickPointsAsync("展点", 1);
        if (pts.Count > 0) StatusMsg.Text = $"展点：{pts.Count} 点在场景(带高程, 可选中/创建三角网)";
    }

    private async Task MdlPrimitiveAsync(string kind)
    {
        var vb = CurrentViewBounds();
        double span = vb != null ? Math.Max(vb[2] - vb[0], vb[3] - vb[1]) : 100;
        double def = Math.Max(1, Math.Round(span / 10));
        PromptDialog? dlg;
        if (kind == "立方体") dlg = await PromptDialog.AskAsync(this, "基本几何体 · 立方体", new[] { new PromptDialog.Field("sx", "长 X", def.ToString(Inv), "m"), new PromptDialog.Field("sy", "宽 Y", def.ToString(Inv), "m"), new PromptDialog.Field("sz", "高 Z", def.ToString(Inv), "m"), new PromptDialog.Field("z", "底面高程", "0", "m") });
        else if (kind == "球体") dlg = await PromptDialog.AskAsync(this, "基本几何体 · 球体", new[] { new PromptDialog.Field("r", "半径", (def / 2).ToString(Inv), "m"), new PromptDialog.Field("z", "球心高程", "0", "m") });
        else dlg = await PromptDialog.AskAsync(this, "基本几何体 · 圆柱", new[] { new PromptDialog.Field("r", "半径", (def / 2).ToString(Inv), "m"), new PromptDialog.Field("h", "高", def.ToString(Inv), "m"), new PromptDialog.Field("z", "底面高程", "0", "m") });
        if (dlg == null) return;
        var c = await PickWorldPointAsync($"{kind}：在视口点击放置位置（Esc 取消）");
        if (c == null) { StatusMsg.Text = $"{kind}：已取消"; return; }
        (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) g = kind switch
        {
            "立方体" => PrimitiveBodies.Box(c.Value.x - dlg.D("sx") / 2, c.Value.y - dlg.D("sy") / 2, dlg.D("z"), dlg.D("sx"), dlg.D("sy"), dlg.D("sz")),
            "球体" => PrimitiveBodies.Sphere(c.Value.x, c.Value.y, dlg.D("z"), dlg.D("r"), 16, 24),
            _ => PrimitiveBodies.Cylinder(c.Value.x, c.Value.y, dlg.D("z"), dlg.D("r"), dlg.D("h"), 24),
        };
        var me = AddMesh(new MeshEntity(NewMeshName(kind), g.v, g.t) { Cr = 0.6f, Cg = 0.8f, Cb = 0.6f }, false);
        SelectEntities(new SceneEntity[] { me });
        StatusMsg.Text = $"{kind}「{me.Name}」：{g.v.Count} 顶点 / {g.t.Count} 三角 · 体积 {MeshMetrics.RobustVolume(g.v, g.t):0.##}";
    }

    private async Task MdlExportOffAsync()
    {
        var meshes = SelectedMeshes(); if (meshes.Count == 0) meshes = AllMeshes();
        if (meshes.Count == 0) { StatusMsg.Text = "导出三角网：场景无三角网"; return; }
        var (v, t) = meshes.Count == 1 ? (meshes[0].Verts, meshes[0].Tris) : MeshWeld.Concat(meshes.Select(m => ((IReadOnlyList<(double x, double y, double z)>)m.Verts, (IReadOnlyList<(int a, int b, int c)>)m.Tris)).ToList());
        var name = await SaveCsvAsync("导出三角网 (OFF)", (meshes.Count == 1 ? meshes[0].Name : "meshes") + ".off", MeshWeld.ToOff(v, t));
        if (name != null) StatusMsg.Text = $"导出三角网：{meshes.Count} 网 {t.Count} 三角 → {name}";
    }

    // ═══════════════════ 点/线编辑 ═══════════════════
    private async Task MdlSetPointZAsync()
    {
        var pts = SelectedPoints();
        var ents = _selected.Where(e => e is not MeshEntity).ToList();
        if (ents.Count == 0) { StatusMsg.Text = "修改高程点：请先选中点(或其它平面实体)"; return; }
        var dlg = await PromptDialog.AskAsync(this, "修改高程点", new[] { new PromptDialog.Field("z", "高程 Z", (pts.Count > 0 ? pts[0].Elevation : ents[0].Elevation).ToString("0.###", Inv), "m") }, $"把选中的 {ents.Count} 个实体的高程统一设为指定值");
        if (dlg == null) return;
        BeginChange();
        foreach (var e in ents) { e.Elevation = dlg.D("z"); if (e is PolylineEntity pl) pl.Zs = null; }
        RefreshScene();
        StatusMsg.Text = $"修改高程点：{ents.Count} 个实体高程 → {dlg.D("z"):0.###}";
    }

    private async Task MdlPointStyleAsync()
    {
        var pts = SelectedPoints();
        if (pts.Count == 0) { StatusMsg.Text = "修改点样式：请先选中点"; return; }
        var dlg = await PromptDialog.AskAsync(this, "修改点样式", new[] { new PromptDialog.Field("style", "点样式(0..4)", pts[0].Style.ToString(Inv)), new PromptDialog.Field("size", "点大小", pts[0].Size.ToString("0.##", Inv), "世界单位") });
        if (dlg == null) return;
        BeginChange();
        foreach (var p in pts) { p.Style = Math.Clamp(dlg.I("style"), 0, 4); if (dlg.D("size") > 0) p.Size = dlg.D("size"); }
        RefreshScene();
        StatusMsg.Text = $"修改点样式：{pts.Count} 点 → 样式 {Math.Clamp(dlg.I("style"), 0, 4)} · 大小 {dlg.D("size"):0.##}";
    }

    private double? MeshZ(MeshEntity? m, double x, double y)
    {
        if (m == null) return null;
        if (!_surfGrids.TryGetValue(m, out var g)) { var (fv, ft) = m.Flatten(); var b = m.Bounds; g = new SurfaceVolume.TriGrid(fv, ft, Math.Max(b.maxX - b.minX, b.maxY - b.minY) / 200 + 1e-9); _surfGrids[m] = g; }
        return g.Sample(x, y);
    }

    private async Task MdlDrapePointsAsync(string cmd)
    {
        var pts = SelectedPoints();
        if (pts.Count == 0) { StatusMsg.Text = $"{cmd}：请先选中点(可同时选中目标三角网)"; return; }
        var meshes = SelectedMeshes(); if (meshes.Count == 0) meshes = await PickMeshesAsync(cmd, 1, 1);
        if (meshes.Count == 0) return;
        BeginChange();
        int hit = 0;
        foreach (var p in pts) { var z = MeshZ(meshes[0], p.X, p.Y); if (z.HasValue) { p.Elevation = z.Value; hit++; } }
        RefreshScene();
        StatusMsg.Text = $"{cmd}：{hit}/{pts.Count} 点取「{meshes[0].Name}」面高程{(hit < pts.Count ? $"，{pts.Count - hit} 点在面外未动" : "")}";
    }

    private async Task MdlWeldAsync()
    {
        var meshes = await PickMeshesAsync("顶点焊接", 1);
        if (meshes.Count == 0) return;
        var dlg = await PromptDialog.AskAsync(this, "顶点焊接", new[] { new PromptDialog.Field("tol", "焊接容差", "0", "m(0=自动 包围盒对角×1e-4)") });
        if (dlg == null) return;
        int totalIn = 0, totalOut = 0;
        foreach (var m in meshes)
        {
            var b = m.Bounds; double diag = Math.Sqrt((b.maxX - b.minX) * (b.maxX - b.minX) + (b.maxY - b.minY) * (b.maxY - b.minY) + (b.maxZ - b.minZ) * (b.maxZ - b.minZ));
            double tol = dlg.D("tol") > 0 ? dlg.D("tol") : (diag > 0 ? diag * 1e-4 : 1e-6);
            var w = MeshWeld.Weld(m.Verts, m.Tris, tol, dropDuplicateTris: true);
            totalIn += w.InputVerts; totalOut += w.OutputVerts;
            ReplaceMesh(m, new MeshEntity(m.Name, w.Verts, w.Tris));
        }
        StatusMsg.Text = $"顶点焊接：{meshes.Count} 网 顶点 {totalIn} → {totalOut}";
    }

    private async Task MdlClipLinesByLoopAsync()
    {
        var lines = SelectedPolylines();
        var loops = lines.Where(l => l.Closed && l.Points.Count >= 3).ToList();
        var targets = lines.Where(l => !loops.Contains(l)).ToList();
        if (loops.Count == 0 || targets.Count == 0) { StatusMsg.Text = "闭合线裁剪：请选中 1 条闭合多段线(边界) + 若干被裁多段线"; return; }
        var dlg = await PromptDialog.AskAsync(this, "闭合线裁剪", new[] { new PromptDialog.Field("side", "保留", "内侧", null, null, false, new[] { "内侧", "外侧" }) });
        if (dlg == null) return;
        bool inside = dlg.S("side") == "内侧";
        BeginChange();
        int made = 0;
        var added = new List<SceneEntity>();
        foreach (var l in targets)
        {
            var pieces = PolylineClipper.ClipPolyline(loops[0].Points, Line3(l), l.Closed, inside);
            _scene.Remove(l);
            foreach (var pc in pieces) { var pl = Poly3(pc, false, l.Cr, l.Cg, l.Cb); pl.CopyStyleFrom(l); if (!l.Has3D) { pl.Zs = null; } _scene.Add(pl); added.Add(pl); made++; }
        }
        RefreshScene(); SelectEntities(added);
        StatusMsg.Text = $"闭合线裁剪：{targets.Count} 条线保留{(inside ? "内" : "外")}侧 → {made} 段";
    }

    private async Task MdlUnifyLineZAsync()
    {
        var lines = SelectedPolylines();
        if (lines.Count == 0) { StatusMsg.Text = "统一线高程：请先选中多段线"; return; }
        var dlg = await PromptDialog.AskAsync(this, "统一线高程", new[] { new PromptDialog.Field("z", "高程 Z", lines[0].Elevation.ToString("0.###", Inv), "m") });
        if (dlg == null) return;
        BeginChange();
        foreach (var l in lines) { l.Elevation = dlg.D("z"); l.Zs = null; }
        RefreshScene();
        StatusMsg.Text = $"统一线高程：{lines.Count} 条线 → {dlg.D("z"):0.###}";
    }

    private async Task MdlDrapePolylinesAsync()
    {
        var lines = SelectedPolylines();
        if (lines.Count == 0) { StatusMsg.Text = "线落到面上：请先选中多段线(可同时选中目标三角网)"; return; }
        var meshes = SelectedMeshes(); if (meshes.Count == 0) meshes = await PickMeshesAsync("线落到面上", 1, 1);
        if (meshes.Count == 0) return;
        BeginChange();
        int miss = 0, tot = 0;
        foreach (var l in lines)
        {
            var zs = new List<double>(l.Points.Count);
            foreach (var (x, y) in l.Points) { var z = MeshZ(meshes[0], x, y); tot++; if (z.HasValue) zs.Add(z.Value); else { zs.Add(l.ZAt(zs.Count)); miss++; } }
            l.Zs = zs; l.Elevation = 0;
        }
        RefreshScene();
        StatusMsg.Text = $"线落到面上：{lines.Count} 条线 {tot} 顶点落到「{meshes[0].Name}」{(miss > 0 ? $"，{miss} 点在面外保持原高程" : "")}";
    }

    // ═══════════════════ 面/体编辑 ═══════════════════
    private async Task MdlMeshBoundaryAsync()
    {
        var meshes = await PickMeshesAsync("生成三角网边界", 1);
        if (meshes.Count == 0) return;
        BeginChange();
        var added = new List<SceneEntity>(); int loopsN = 0;
        foreach (var m in meshes)
        {
            var loops = MeshBoundaryLoops.Extract(m.Verts, OrientUp(m.Verts, m.Tris));
            foreach (var lp in loops) { if (lp.Count < 2) continue; var pl = Poly3(lp, true, 0.95f, 0.5f, 0.2f); pl.LayerName = m.LayerName; _scene.Add(pl); added.Add(pl); loopsN++; }
        }
        RefreshScene(); SelectEntities(added);
        StatusMsg.Text = $"生成三角网边界：{meshes.Count} 网 → {loopsN} 条边界环(三维多段线, 含断层缝/空洞)";
    }

    private async Task MdlSplitAlongLineAsync()
    {
        var lines = SelectedPolylines().Where(l => l.Points.Count >= 2).ToList();
        var lineEnts = _selected.OfType<LineEntity>().ToList();
        var meshes = await PickMeshesAsync("沿线分割三角网", 1, 1);
        if (meshes.Count == 0) return;
        (double x0, double y0, double x1, double y1)? seg = null;
        if (lineEnts.Count > 0) seg = (lineEnts[0].X0, lineEnts[0].Y0, lineEnts[0].X1, lineEnts[0].Y1);
        else if (lines.Count > 0) { var l = lines[0]; seg = (l.Points[0].x, l.Points[0].y, l.Points[^1].x, l.Points[^1].y); if (l.Points.Count > 2) StatusMsg.Text = "沿线分割三角网：多段折线按首尾两点的竖直面切分(逐段折线切分需内核)"; }
        if (seg == null) { StatusMsg.Text = "沿线分割三角网：请同时选中一条直线/多段线作分割线"; return; }
        var m = meshes[0];
        var (left, right) = MeshPlaneSplit.Split(m.Verts, m.Tris, seg.Value.x0, seg.Value.y0, seg.Value.x1, seg.Value.y1);
        if (left.t.Count == 0 || right.t.Count == 0) { StatusMsg.Text = "沿线分割三角网：分割线未穿过三角网"; return; }
        BeginChange();
        _scene.Remove(m); _selected.Remove(m);
        var a = new MeshEntity(NewMeshName(m.Name + "-左"), left.v, left.t); a.CopyStyleFrom(m);
        var b = new MeshEntity(NewMeshName(m.Name + "-右"), right.v, right.t); b.CopyStyleFrom(m); b.Cg = Math.Min(1, b.Cg + 0.15f);
        _scene.Add(a); _scene.Add(b); RefreshScene(); SelectEntities(new SceneEntity[] { a, b });
        StatusMsg.Text = $"沿线分割三角网「{m.Name}」：左 {left.t.Count} / 右 {right.t.Count} 三角(面积守恒)";
    }

    private async Task MdlMergeMeshesAsync()
    {
        var meshes = await PickMeshesAsync("合并三角网", 2);
        if (meshes.Count < 2) { StatusMsg.Text = "合并三角网：请选中 ≥2 张三角网"; return; }
        var (verts, tris) = MeshWeld.Concat(meshes.Select(m => ((IReadOnlyList<(double x, double y, double z)>)m.Verts, (IReadOnlyList<(int a, int b, int c)>)m.Tris)).ToList());
        var mm = MeshMetrics.Compute(verts, tris);
        double diag = Math.Sqrt((mm.MaxX - mm.MinX) * (mm.MaxX - mm.MinX) + (mm.MaxY - mm.MinY) * (mm.MaxY - mm.MinY) + (mm.MaxZ - mm.MinZ) * (mm.MaxZ - mm.MinZ));
        var w = MeshWeld.Weld(verts, tris, diag > 0 ? diag * 1e-4 : 1e-6, dropDuplicateTris: true);
        BeginChange();
        foreach (var m in meshes) { _scene.Remove(m); _selected.Remove(m); _surfGrids.Remove(m); }
        var me = new MeshEntity(NewMeshName("合并网"), w.Verts, w.Tris); me.CopyStyleFrom(meshes[0]);
        _scene.Add(me); RefreshScene(); SelectEntities(new SceneEntity[] { me });
        var d = MeshDiagnose.Analyze(w.Verts, w.Tris);
        StatusMsg.Text = $"合并三角网「{me.Name}」：{meshes.Count} 网 → {w.OutputTris} 三角(焊接去重 {w.DuplicateTris}) · 开放边 {d.BoundaryEdges} · 非流形 {d.NonManifoldEdges}";
    }

    private async Task MdlIntersectMeshesAsync()
    {
        var meshes = await PickMeshesAsync("面交线", 2, 2);
        if (meshes.Count < 2) { StatusMsg.Text = "面交线：请选中 2 张三角网"; return; }
        var segs = MeshIntersect.IntersectionSegments(meshes[0].Verts, meshes[0].Tris, meshes[1].Verts, meshes[1].Tris);
        if (segs.Count == 0) { StatusMsg.Text = "面交线：两网不相交"; return; }
        var s2 = segs.Select(s => (s.A.x, s.A.y, s.B.x, s.B.y)).ToList();
        var b = meshes[0].Bounds; double tol = Math.Max(b.maxX - b.minX, b.maxY - b.minY) * 1e-6;
        var chains = Contour.LinkSegments(s2, tol);
        var zLookup = new Dictionary<(long, long), double>();
        foreach (var s in segs) { zLookup[((long)Math.Round(s.A.x / tol), (long)Math.Round(s.A.y / tol))] = s.A.z; zLookup[((long)Math.Round(s.B.x / tol), (long)Math.Round(s.B.y / tol))] = s.B.z; }
        BeginChange();
        var added = new List<SceneEntity>();
        foreach (var ch in chains)
        {
            if (ch.Count < 2) continue;
            var pts = ch.Select(p => (p.x, p.y, zLookup.TryGetValue(((long)Math.Round(p.x / tol), (long)Math.Round(p.y / tol)), out var z) ? z : (MeshZ(meshes[0], p.x, p.y) ?? 0))).ToList();
            var pl = Poly3(pts, false, 0.95f, 0.3f, 0.3f); AssignLayer(pl); _scene.Add(pl); added.Add(pl);
        }
        RefreshScene(); SelectEntities(added);
        StatusMsg.Text = $"面交线「{meshes[0].Name}」∩「{meshes[1].Name}」：{segs.Count} 段 → {added.Count} 条三维交线";
    }

    private async Task MdlRepairAsync()
    {
        var meshes = await PickMeshesAsync("修复拓扑关系", 1);
        if (meshes.Count == 0) return;
        var parts = new List<string>();
        foreach (var m in meshes)
        {
            var r = MeshRepair.Repair(m.Verts, m.Tris);
            ReplaceMesh(m, new MeshEntity(m.Name, r.Verts, r.Tris));
            parts.Add($"{m.Name}: 顶点 {r.VertsBefore}→{r.VertsAfter} · 补洞 {r.FilledHoles} · 开放边 {r.BoundaryBefore}→{r.BoundaryAfter}");
        }
        StatusMsg.Text = "修复拓扑关系：" + string.Join(" ; ", parts);
    }

    private async Task MdlDeleteTrisAsync()
    {
        var loops = SelectedPolylines().Where(l => l.Closed && l.Points.Count >= 3).ToList();
        var meshes = await PickMeshesAsync("删除三角面", 1, 1);
        if (meshes.Count == 0) return;
        if (loops.Count == 0) { StatusMsg.Text = "删除三角面：请同时选中一条闭合多段线圈定要删的三角(按质心判定)"; return; }
        var m = meshes[0];
        var inside = PolylineClipper.TrianglesInside(m.Verts, m.Tris, loops[0].Points);
        if (inside.Count == 0) { StatusMsg.Text = "删除三角面：闭合线内无三角"; return; }
        var (nv, nt) = PolylineClipper.RemoveTriangles(m.Verts, m.Tris, inside);
        ReplaceMesh(m, new MeshEntity(m.Name, nv, nt));
        StatusMsg.Text = $"删除三角面「{m.Name}」：删除 {inside.Count} 三角，余 {nt.Count}";
    }

    private async Task MdlDiagnoseAsync()
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), "格网质量检测")) return;
        var meshes = await PickMeshesAsync("格网质量检测", 1);
        if (meshes.Count == 0) return;
        var parts = new List<string>();
        foreach (var m in meshes)
        {
            var d = MeshDiagnose.Analyze(m.Verts, m.Tris);
            parts.Add($"{m.Name}: {d.TriangleCount} 三角/{d.EdgeCount} 边 · 开放边 {d.BoundaryEdges} · 非流形 {d.NonManifoldEdges} · 退化 {d.DegenerateTriangles} · 洞 {d.BoundaryLoops} · 孤立点 {d.IsolatedVertices} · 重复点 {d.DuplicateVertices} · 自交 {d.SelfIntersectTriangles} · {(d.IsClosed ? "闭合" : "开放")}");
        }
        StatusMsg.Text = "格网质量检测：" + string.Join(" ; ", parts);
    }

    private async Task MdlHoleFillAsync()
    {
        var meshes = await PickMeshesAsync("补洞", 1);
        if (meshes.Count == 0) return;
        int holes = 0;
        foreach (var m in meshes) { var (v, t, h) = MeshHoleFill.Fill(m.Verts, m.Tris); holes += h; if (h > 0) ReplaceMesh(m, new MeshEntity(m.Name, v, t)); }
        StatusMsg.Text = $"补洞：{meshes.Count} 网 补 {holes} 个洞";
    }

    private async Task MdlSmoothAsync()
    {
        var meshes = await PickMeshesAsync("网格光顺", 1);
        if (meshes.Count == 0) return;
        var dlg = await PromptDialog.AskAsync(this, "网格光顺 (Laplacian)", new[] { new PromptDialog.Field("it", "迭代次数", "3"), new PromptDialog.Field("lam", "步长 λ", "0.5", "0..1") });
        if (dlg == null) return;
        foreach (var m in meshes) ReplaceMesh(m, new MeshEntity(m.Name, MeshSmooth.Laplacian(m.Verts, m.Tris, Math.Max(1, dlg.I("it")), Math.Clamp(dlg.D("lam"), 0.01, 1), true), m.Tris));
        StatusMsg.Text = $"网格光顺：{meshes.Count} 网 · {dlg.I("it")} 次迭代 λ={dlg.D("lam"):0.##}(固定边界)";
    }

    private async Task MdlCutFillAsync()
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), "两期三角网算量")) return;
        var meshes = await PickMeshesAsync("两期三角网算量", 2, 2);
        if (meshes.Count < 2) { StatusMsg.Text = "两期三角网算量：请选中 2 张三角网(前期、后期)"; return; }
        var (va, ta) = meshes[0].Flatten(); var (vb, tb) = meshes[1].Flatten();
        var r = SurfaceVolume.CutFill(va, ta, vb, tb);
        if (r.ValidCells == 0) { StatusMsg.Text = "两期三角网算量：两面 XY 无重叠"; return; }
        StatusMsg.Text = $"两期三角网算量「{meshes[0].Name}」→「{meshes[1].Name}」：挖方 {r.CutM3:0.#} m³ · 填方 {r.FillM3:0.#} m³ · 净 {r.NetM3:+0.#;-0.#} m³ · 重叠 {r.OverlapAreaM2:0.#} m²(格距 {r.CellSize:0.##}, {r.ValidCells} 格) · dz {r.MinDz:0.##}~{r.MaxDz:0.##}";
    }

    private async Task MdlMeshVolumeAsync()
    {
        var meshes = await PickMeshesAsync("三角网体积", 1);
        if (meshes.Count == 0) return;
        var parts = new List<string>();
        foreach (var m in meshes)
        {
            var mm = MeshMetrics.Compute(m.Verts, m.Tris);
            var d = MeshDiagnose.Analyze(m.Verts, m.Tris);
            parts.Add($"{m.Name}: {mm.VertexCount} 顶点/{mm.TriangleCount} 三角 · 表面积 {mm.SurfaceArea:0.##} · 投影面积 {MeshMetrics.HullAreaXY(m.Verts):0.##} · {(d.IsClosed ? $"体积 {MeshMetrics.RobustVolume(m.Verts, m.Tris):0.##}" : "开放面(无封闭体积)")} · Z {mm.MinZ:0.#}~{mm.MaxZ:0.#}");
        }
        StatusMsg.Text = "三角网体积：" + string.Join(" ; ", parts);
    }

    private async Task MdlVoxelVolumeAsync()
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), "体素格网体积")) return;
        var meshes = await PickMeshesAsync("体素格网体积", 1, 1);
        if (meshes.Count == 0) return;
        var m = meshes[0]; var b = m.Bounds;
        double diag = Math.Sqrt((b.maxX - b.minX) * (b.maxX - b.minX) + (b.maxY - b.minY) * (b.maxY - b.minY) + (b.maxZ - b.minZ) * (b.maxZ - b.minZ));
        var dlg = await PromptDialog.AskAsync(this, "体素格网体积", new[] { new PromptDialog.Field("cell", "格边长", Math.Max(diag / 40, 1e-6).ToString("0.###", Inv), "m") }, $"对闭合三角网「{m.Name}」按广义缠绕数逐格判内外");
        if (dlg == null) return;
        var (fv, ft) = m.Flatten();
        var r = MeshVoxelizer.Voxelize(fv, ft, dlg.D("cell"));
        if (r.TooLarge) { StatusMsg.Text = $"体素格网体积：格数 {r.TotalCells} 超上限，请加大格边长"; return; }
        double exact = MeshMetrics.RobustVolume(m.Verts, m.Tris);
        double vox = r.Centers.Count * r.CellSize * r.CellSize * r.CellSize;
        StatusMsg.Text = $"体素格网体积「{m.Name}」：格边 {r.CellSize:0.###} · {r.Nx}×{r.Ny}×{r.Nz} 格 · 占用 {r.Centers.Count} 格 → {vox:0.#} m³（散度定理精确 {exact:0.#}，偏差 {(exact > 0 ? (vox - exact) / exact * 100 : 0):+0.#;-0.#}%）";
    }

    private async Task MdlEntityToBlocksAsync()
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), "实体转块体")) return;
        var meshes = await PickMeshesAsync("实体转块体", 1, 1);
        if (meshes.Count == 0) return;
        var m = meshes[0];
        var mt = MeshOrient.MakeConsistent(m.Verts, m.Tris);
        var fv = new double[m.Verts.Count * 3];
        for (int i = 0; i < m.Verts.Count; i++) { fv[i * 3] = m.Verts[i].x; fv[i * 3 + 1] = m.Verts[i].y; fv[i * 3 + 2] = m.Verts[i].z; }
        var ft = new int[mt.Count * 3];
        for (int i = 0; i < mt.Count; i++) { ft[i * 3] = mt[i].a; ft[i * 3 + 1] = mt[i].b; ft[i * 3 + 2] = mt[i].c; }
        WindingNumberTester wn;
        try { wn = new WindingNumberTester(fv, ft); } catch (Exception ex) { StatusMsg.Text = $"实体转块体：建测试器失败 {ex.Message}"; return; }
        double dx = wn.MaxX - wn.MinX, dy = wn.MaxY - wn.MinY, dz = wn.MaxZ - wn.MinZ;
        double diag = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var dlg = await PromptDialog.AskAsync(this, "实体转块体", new[] { new PromptDialog.Field("cell", "块边长", (diag > 0 ? diag / 20 : 1).ToString("0.###", Inv), "m") }, $"把闭合三角网「{m.Name}」离散为规则块体(广义缠绕数判内外)");
        if (dlg == null) return;
        double cell = Math.Max(dlg.D("cell"), 1e-6);
        while (dx / cell * (dy / cell) * (dz / cell) > 200000) cell *= 1.5;
        var blocks = new List<BlockModel.Block>();
        for (double z = wn.MinZ + cell * 0.5; z <= wn.MaxZ; z += cell)
            for (double y = wn.MinY + cell * 0.5; y <= wn.MaxY; y += cell)
                for (double x = wn.MinX + cell * 0.5; x <= wn.MaxX; x += cell)
                    if (wn.IsInsideClosed(x, y, z)) blocks.Add(new BlockModel.Block { X = x, Y = y, Z = z, Size = cell, Grade = 0 });
        if (blocks.Count == 0) { StatusMsg.Text = "实体转块体：无占用块体(网格可能非闭合)"; return; }
        _lastBlocks = blocks; _blockAttrs = null; _blockGmin = 0; _blockGmax = 1;
        BeginChange(); RenderBlocks(blocks); RefreshScene();
        StatusMsg.Text = $"实体转块体「{m.Name}」：块边 {cell:0.##} · {blocks.Count} 块 · 体积 {blocks.Count * cell * cell * cell:0.#} m³（已入场景, 可着色/筛选/报告）";
    }

    // ═══════════════════ 工具 ═══════════════════
    private async Task MdlContourAsync()
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), "构建等值线")) return;
        var meshes = await PickMeshesAsync("构建等值线", 1, 1);
        if (meshes.Count == 0) return;
        var m = meshes[0]; var b = m.Bounds;
        double zr = b.maxZ - b.minZ;
        var dlg = await PromptDialog.AskAsync(this, "构建等值线", new[] { new PromptDialog.Field("dz", "等高距", (zr > 0 ? Math.Max(Math.Round(zr / 10, 1), 0.1) : 1).ToString("0.###", Inv), "m"), new PromptDialog.Field("cell", "采样格距", (Math.Max(b.maxX - b.minX, b.maxY - b.minY) / 200).ToString("0.###", Inv), "m") }, $"从三角网「{m.Name}」采样栅格提取等值线");
        if (dlg == null) return;
        double cell = Math.Max(dlg.D("cell"), 1e-6), dz = Math.Max(dlg.D("dz"), 1e-6);
        var (fv, ft) = m.Flatten();
        var g = new SurfaceVolume.TriGrid(fv, ft, cell);
        int nx = (int)Math.Ceiling((b.maxX - b.minX) / cell) + 1, ny = (int)Math.Ceiling((b.maxY - b.minY) / cell) + 1;
        while ((long)nx * ny > 4_000_000) { cell *= 1.5; nx = (int)Math.Ceiling((b.maxX - b.minX) / cell) + 1; ny = (int)Math.Ceiling((b.maxY - b.minY) / cell) + 1; }
        var grid = new double[nx, ny];
        for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) grid[i, j] = g.Sample(b.minX + i * cell, b.minY + j * cell) ?? double.NaN;
        var levels = Contour.Levels(b.minZ, b.maxZ, dz);
        BeginChange();
        var added = new List<SceneEntity>(); int segsN = 0;
        foreach (var lv in levels)
        {
            var segs = Contour.MarchingSquares(grid, b.minX, b.minY, cell, cell, lv);
            segsN += segs.Count;
            foreach (var ch in Contour.LinkSegments(segs, cell * 1e-3))
            {
                if (ch.Count < 2) continue;
                var pl = new PolylineEntity { Zs = ch.Select(_ => lv).ToList(), Cr = 0.4f, Cg = 0.55f, Cb = 0.3f };
                pl.Points.AddRange(ch); pl.LayerName = m.LayerName; _scene.Add(pl); added.Add(pl);
            }
        }
        RefreshScene(); SelectEntities(added);
        StatusMsg.Text = $"构建等值线「{m.Name}」：{levels.Count} 层(等高距 {dz:0.##}) → {added.Count} 条三维等值线({segsN} 段, 格距 {cell:0.##})";
    }

    private async Task MdlSectionAsync()
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), "创建剖面")) return;
        var meshes = await PickMeshesAsync("创建剖面", 1);
        if (meshes.Count == 0) return;
        (double x0, double y0, double x1, double y1)? seg = null;
        var le = _selected.OfType<LineEntity>().FirstOrDefault(); var pl0 = SelectedPolylines().FirstOrDefault(l => l.Points.Count >= 2);
        if (le != null) seg = (le.X0, le.Y0, le.X1, le.Y1);
        else if (pl0 != null) seg = (pl0.Points[0].x, pl0.Points[0].y, pl0.Points[^1].x, pl0.Points[^1].y);
        else
        {
            var a = await PickWorldPointAsync("创建剖面：点击剖面线起点（Esc 取消）"); if (a == null) return;
            var bp = await PickWorldPointAsync("创建剖面：点击剖面线终点（Esc 取消）"); if (bp == null) return;
            seg = (a.Value.x, a.Value.y, bp.Value.x, bp.Value.y);
        }
        BeginChange();
        var added = new List<SceneEntity>();
        var parts = new List<string>();
        double ux = seg.Value.x1 - seg.Value.x0, uy = seg.Value.y1 - seg.Value.y0, len = Math.Sqrt(ux * ux + uy * uy); if (len < 1e-9) { StatusMsg.Text = "创建剖面：剖面线长度为 0"; return; }
        foreach (var m in meshes)
        {
            var prof = MeshPlaneSection.Profile(m.Verts, m.Tris, (seg.Value.x0, seg.Value.y0), (seg.Value.x1, seg.Value.y1));
            if (prof.Count < 2) continue;
            var pts = prof.Select(p => (seg.Value.x0 + ux / len * p.dist, seg.Value.y0 + uy / len * p.dist, p.z)).ToList();
            var pl = Poly3(pts, false, 0.95f, 0.35f, 0.6f); pl.LayerName = m.LayerName; _scene.Add(pl); added.Add(pl);
            parts.Add($"{m.Name} {prof.Count} 点 Z {prof.Min(p => p.z):0.#}~{prof.Max(p => p.z):0.#}");
        }
        RefreshScene(); SelectEntities(added);
        StatusMsg.Text = added.Count == 0 ? "创建剖面：剖面线未穿过三角网" : $"创建剖面(长 {len:0.#})：{string.Join(" ; ", parts)}（三维剖面线已入场景）";
    }

    private void MdlToggleSurfaceCoord()
    {
        _surfCoordOn = !_surfCoordOn;
        if (!_surfCoordOn) _surfGrids.Clear();
        StatusMsg.Text = _surfCoordOn ? "实时曲面坐标：开（光标处三角网高程显示在坐标栏）" : "实时曲面坐标：关";
    }

    /// <summary>指针移动时(开关开)采样光标下三角网高程, 附到坐标栏。</summary>
    private string SurfaceCoordSuffix((double x, double y)? w)
    {
        if (!_surfCoordOn || w == null) return "";
        foreach (var m in AllMeshes())
        {
            var b = m.Bounds; if (w.Value.x < b.minX || w.Value.x > b.maxX || w.Value.y < b.minY || w.Value.y > b.maxY) continue;
            var z = MeshZ(m, w.Value.x, w.Value.y);
            if (z.HasValue) return $"  Z {z.Value:0.00} [{m.Name}]";
        }
        return "  Z —";
    }
}

/// <summary>建模窗口工厂钩子：独立窗口就绪后在此登记（名称 → 打开）；未登记的命令走场景版直算。</summary>
public static class ModelingWindowFactory
{
    public static readonly Dictionary<string, Action<MainWindow, ModelingContext>> Openers = new();
    public static bool TryOpen(MainWindow owner, ModelingContext ctx, string name)
    {
        if (!Openers.TryGetValue(name, out var open)) return false;
        open(owner, ctx);
        return true;
    }
}

public partial class MainWindow
{
    /// <summary>OFF 文件 → MeshEntity 入场景(开始页「导入」与拖放共用)。</summary>
    private void ImportOffAsMesh(string path)
    {
        string text;
        try { text = System.IO.File.ReadAllText(path); }
        catch (Exception ex) { StatusMsg.Text = $"导入失败：{ex.Message}"; return; }
        var (v, t) = MeshMetrics.ParseOff(text);
        if (t.Count == 0) { StatusMsg.Text = $"导入失败：{System.IO.Path.GetFileName(path)} 未解析到三角网"; return; }
        var me = AddMesh(new MeshEntity(NewMeshName(System.IO.Path.GetFileNameWithoutExtension(path)), v, t), true);
        SelectEntities(new SceneEntity[] { me });
        var d = MeshDiagnose.Analyze(v, t);
        StatusMsg.Text = $"已导入三角网「{me.Name}」：{v.Count} 顶点 · {t.Count} 三角 · {(d.IsClosed ? "闭合体" : $"开放面(开放边 {d.BoundaryEdges})")} · 图层「{me.LayerName}」";
    }
}

public partial class MainWindow
{
    /// <summary>统一三角绕向且法线朝上(XY 投影有向面积和为正)。开放面用；闭合体请用 MeshOrient.MakeConsistent(外向)。</summary>
    private static List<(int a, int b, int c)> OrientUp(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var r = MeshOrient.MakeConsistent(v, t);
        double s = 0;
        foreach (var (a, b, c) in r)
        {
            if (a >= v.Count || b >= v.Count || c >= v.Count) continue;
            s += (v[b].x - v[a].x) * (v[c].y - v[a].y) - (v[c].x - v[a].x) * (v[b].y - v[a].y);
        }
        if (s < 0) for (int i = 0; i < r.Count; i++) r[i] = (r[i].a, r[i].c, r[i].b);
        return r;
    }
}
