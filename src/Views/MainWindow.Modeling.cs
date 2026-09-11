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

    /// <summary>约束剖分的顶点上限；超过即退为无约束(同原版 kCdt2D5Threshold)。</summary>
    /// <summary>
    /// 走完整约束剖分的顶点上限。超过才退为「体素抽稀 + 无约束」。
    ///
    /// 这个值原来是 10 万(照原版 kCdt2D5Threshold)，因为当时嵌一条约束要扫全网、代价随网大小暴涨。
    /// 加了三角形网格索引之后实测: 9.8 万顶点 281 ms、49.2 万顶点 1231 ms，均**全部约束嵌入、零跳过**。
    /// 抽稀会按米级格子丢点、挪点，与毫米级建网直接冲突，所以门槛抬到二百万 ——
    /// 常规测绘数据一律走全精度，抽稀只作为极端兜底(否则近百万三角形的渲染缓冲会顶不住)。
    /// </summary>
    private const int TinConstraintVertexLimit = 2_000_000;
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

    /// <summary>
    /// 由已有多段线派生一条新多段线（裁剪/加密/抽稀/连接产生的碎片都走这里）。
    ///
    /// 两件事必须一起做，少一件属性就走样：
    ///   ① 样式全随源 —— 图层 / 颜色 / 线型 / 线宽 / 透明度 / 可见 / 标高（<see cref="SceneEntity.CopyStyleFrom"/>）；
    ///   ② 逐点 Z 按源标高**回基** —— <c>ZAt = Zs[i] + Elevation</c>，把绝对 Z 直接塞进 Zs
    ///      会把标高再加一遍（标高 50 的三维线裁完变成 100+，用户看到的就是"命令改了图元属性"）。
    /// 源是平面线（Has3D=false）时不给 Zs，高程仍由 Elevation 单独承载。
    /// </summary>
    private static PolylineEntity DerivePolyline(
        PolylineEntity src, IReadOnlyList<(double x, double y, double z)> pts3, bool closed)
    {
        var pl = new PolylineEntity { Closed = closed };
        pl.CopyStyleFrom(src);
        foreach (var p in pts3) pl.Points.Add((p.x, p.y));
        if (src.Has3D)
        {
            pl.Zs = new List<double>(pts3.Count);
            foreach (var p in pts3) pl.Zs.Add(p.z - src.Elevation);
        }
        return pl;
    }

    /// <summary>同上，但新点表只有 XY（加密/抽稀/连接）：逐点 Z 沿源线重采后再回基。</summary>
    private static PolylineEntity DerivePolyline(
        PolylineEntity src, IReadOnlyList<(double x, double y)> pts2, bool closed,
        PolylineEntity? zSource = null)
    {
        var zsrc = zSource ?? src;
        var pl = new PolylineEntity { Closed = closed };
        pl.CopyStyleFrom(src);
        pl.Points.AddRange(pts2);
        if (zsrc.Has3D)
        {
            var abs = PolylineEdit.SampleZAlong(zsrc.Points,
                Enumerable.Range(0, zsrc.Points.Count).Select(zsrc.ZAt).ToList(), pts2);
            pl.Zs = PolylineEdit.RebaseZ(abs, pl.Elevation);
        }
        return pl;
    }

    private string NewMeshName(string prefix)
    {
        var names = new HashSet<string>(_scene.Entities.OfType<MeshEntity>().Select(m => m.Name));
        if (!names.Contains(prefix)) return prefix;
        for (int i = 2; ; i++) if (!names.Contains($"{prefix}{i}")) return $"{prefix}{i}";
    }


    /// <summary>
    /// 由收集好的顶点/约束建三角网并入场景 —— 创建三角网的几条输入通路(选中的线 / 场景全部可见线 /
    /// 导入线框)共用这一段收尾，口径对齐原版：
    ///   · 先预清洗约束(剔退化/重复/交叉)并把剔除数报给用户；
    ///   · 顶点超上限退为无约束剖分(原版同样在十万点处分流)；
    ///   · 结果落图层「格网」、用原版那支橙(ACI 30)；
    ///   · 不按闭合线裁边界(原版 close_convex_hull=true, 结果填满凸包; 裁剪另用「闭合线裁剪面」)。
    /// </summary>
    private bool BuildTinFromConstraints(PolylineTin.Result col, string source, Action<string>? log = null)
    {
        log ??= _ => { };
        if (col.Verts.Count < 3) { StatusMsg.Text = $"创建三角网：{source}的顶点不足 3 个"; log("止步: 顶点不足 3 个"); return false; }

        var cons = PolylineTin.Clean(col.Verts, col.Constraints, out var clean);
        log($"预清洗: 约束 {col.Constraints.Count} → {cons.Count} (重复{clean.Duplicate}/交叉{clean.Crossing}/退化{clean.Degenerate})");

        // 顶点超上限: 照原版先做体素抽稀再无约束剖分。不抽稀的话地形图 50 万顶点会剖出近百万三角形,
        // 剖分本身很快, 但接下来镶嵌/算边线/传 GL 缓冲就把界面卡死了。
        var verts = col.Verts;
        bool dropped = verts.Count > TinConstraintVertexLimit;
        double voxel = 0;
        if (dropped)
        {
            int before = verts.Count;
            verts = PolylineTin.Downsample(verts, TinConstraintVertexLimit, out voxel);
            log($"体素抽稀(超 {TinConstraintVertexLimit:N0} 顶点的极端兜底, 会损失毫米精度): " +
                $"顶点 {before} → {verts.Count} (格边长 {voxel:0.##})");
        }
        var xy = verts.Select(v => (v.x, v.y)).ToList();
        log($"开始剖分: 顶点={xy.Count} 约束={(dropped ? 0 : cons.Count)}{(dropped ? " (已抽稀, 退无约束)" : "")}");

        int insCnt = 0, skipCnt = 0;
        var tris = dropped ? Delaunay.Triangulate(xy)
                           : Delaunay.TriangulateConstrained(xy, cons, out insCnt, out skipCnt);
        log($"剖分完成: 三角={tris.Count} 约束嵌入={insCnt} 跳过={skipCnt}");
        if (tris.Count == 0) { StatusMsg.Text = $"创建三角网：{source}的顶点共线，无法剖分"; log("止步: 剖分结果为空(共线?)"); return false; }

        tris = OrientUpPlanar(verts, tris);   // 平面 TIN: 按有向面积统一朝上(见方法说明, 走通用版会卡 37 秒)(法线朝上), 边界环/成体/内外判定都依赖一致绕向
        var me = new MeshEntity(NewMeshName("三角网"), verts, tris)
        {
            LayerName = TinLayerName,
            Cr = 0xFF / 255f, Cg = 0x7F / 255f, Cb = 0x00,   // 原版结果色: ACI 30 橙
        };
        log($"入场景: 图层={TinLayerName} 顶点={verts.Count} 三角={tris.Count}");
        _layers.EnsureImported(TinLayerName, 1f, 0x7F / 255f, 0f);   // 结果落「格网」层(同原版), 用同一支橙
        AddMesh(me, true);
        SelectEntities(new SceneEntity[] { me });

        var st = TinSurface.Describe(verts, tris);
        StatusMsg.Text = $"创建三角网「{me.Name}」：{source} {verts.Count} 顶点"
                       + (dropped ? $"(超 {TinConstraintVertexLimit:N0}，已按 {voxel:0.##} m 体素抽稀至 {verts.Count:N0} 点并退为无约束剖分)"
                                  : $"，{cons.Count} 段约束")
                       + (clean.Total > 0 ? $"，已清洗剔除 {clean.Total} 处问题约束(重复 {clean.Duplicate}/交叉 {clean.Crossing}/退化 {clean.Degenerate})" : "")
                       + $" → {tris.Count} 三角 · 投影面积 {st.ProjectedAreaXY:0.#} · 高程 {st.ZMin:0.#}~{st.ZMax:0.#}";
        return true;
    }

    /// <summary>三角网结果图层名（同原版 Layer="格网"）。</summary>
    private const string TinLayerName = "格网";

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
    /// <summary>
    /// 三维地质建模命令派发。整体兜底：这里被 async void 的 OnRibbonCommand 调用，
    /// 抛异常会被静默吞掉，用户看到的就是"点了没反应"——改为把原因写到状态栏与日志。
    /// </summary>
    private async Task<bool> TryModelingCommandAsync(string cmd)
    {
        try { return await TryModelingCommandCoreAsync(cmd); }
        catch (System.Exception ex)
        {
            StatusMsg.Text = $"「{cmd}」执行出错：{ex.Message}";
            PitMine3D.Kylin.CrashLog.Write("建模", $"{cmd} 失败: {ex}");
            return true;   // 已处理(报了错), 不再往下当未知命令回显
        }
    }

    private async Task<bool> TryModelingCommandCoreAsync(string cmd)
    {
        if (ModelingWindowFactory.TryOpen(this, MdlCtx(), cmd)) return true;   // 已登记独立窗口的功能项优先走窗口
        if (await TryEditGroupCommandAsync(cmd)) return true;                  // 「编辑」组(点/线/面/体/工具)忠实移植见 MainWindow.EditGroup.cs
        switch (cmd)
        {
            // 建模
            case "创建三角网": case "转化为三角格网": case "三角网": case "2.5D TIN": case "转三角网": await MdlCreateTinAsync(cmd); return true;
            case "约束三角网": case "约束Delaunay": await MdlEmbedPolylineAsync(); return true;
            case "分割三角网": await EdMeshSplitAlongAsync(); return true;
            case "裁剪三角网": case "三角网裁剪": await MdlClipMeshByLoopAsync(); return true;
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
            // 线编辑
            // 面编辑
            case "格网质量检测": case "网格诊断": await MdlDiagnoseAsync(); return true;
            case "补洞": await MdlHoleFillAsync(); return true;
            case "网格光顺": case "光顺": await MdlSmoothAsync(); return true;
            // 体编辑
            case "两期三角网算量": await MdlCutFillAsync(); return true;
            case "三角网体积": case "网格度量": await MdlMeshVolumeAsync(); return true;
            case "体素格网体积": await MdlVoxelVolumeAsync(); return true;
            case "实体转块体": case "离散化模型": await MdlEntityToBlocksAsync(); return true;
            // 工具
            case "构建等值线": await MdlContourAsync(); return true;
            case "创建剖面": case "网格剖面": await MdlSectionAsync(); return true;
            case "实时曲面坐标": MdlToggleSurfaceCoord(); return true;
            // 渲染配置(开始页 视图组)：三角网显示模式 线框/着色面/着色面+线框, 高程着色
            case "渲染配置": case "显示模式": await MdlRenderConfigAsync(); return true;
            case "线框显示": case "线框模式": SetMeshDisplay(MeshEntity.DisplayMode.Wireframe, null); return true;
            case "着色显示": case "面模型": case "实体显示": SetMeshDisplay(MeshEntity.DisplayMode.Shaded, null); return true;
            case "面加线框": case "着色加线框": SetMeshDisplay(MeshEntity.DisplayMode.ShadedWireframe, null); return true;
            case "高程着色面": SetMeshDisplay(null, !MeshEntity.ColorByElevation); return true;
            // 采矿模型
            case "体积算量": await MdlMeshVolumeAsync(); return true;
            case "采矿模型": StatusMsg.Text = "采矿模型：条带划分(CarveStrip)走 IPitDesignCapability 内核，本机记录为受阻"; return true;
        }
        return false;
    }

    // ═══════════════════ 建模 ═══════════════════
    private async Task MdlCreateTinAsync(string cmd)
    {
        var tinSw = System.Diagnostics.Stopwatch.StartNew();
        void TinLog(string m) => PitMine3D.Kylin.CrashLog.Write("三角网", $"[{tinSw.ElapsedMilliseconds}ms] {m}");
        TinLog("开始");

        var selPts = SelectedPoints();
        var selLines = SelectedPolylines().Where(l => l.Points.Count >= 2).ToList();
        // 直线实体同样作约束（原版按 AcDbLine 收成 2 点开链约束），零长退化线剔除
        var selSegs = _selected.OfType<LineEntity>()
            .Where(l => System.Math.Abs(l.X1 - l.X0) > 1e-9 || System.Math.Abs(l.Y1 - l.Y0) > 1e-9).ToList();

        // 什么都没选 → 用场景里**全部可见的线**。等值线图动辄上千条, 逐条框选不现实;
        // 此前没选就直接去弹选点 CSV 的文件框, 用户把框一关, 看起来就是"点了没反应"。
        bool usingAll = false;
        if (selPts.Count == 0 && selLines.Count == 0 && selSegs.Count == 0)
        {
            selLines = _scene.Entities.OfType<PolylineEntity>()
                .Where(l => l.Visible && l.Points.Count >= 2 && _layers.IsShown(l.LayerName)).ToList();
            selSegs = _scene.Entities.OfType<LineEntity>()
                .Where(l => l.Visible && _layers.IsShown(l.LayerName)
                            && (System.Math.Abs(l.X1 - l.X0) > 1e-9 || System.Math.Abs(l.Y1 - l.Y0) > 1e-9)).ToList();
            usingAll = selLines.Count > 0 || selSegs.Count > 0;
        }
        TinLog($"输入清点: 选中点={selPts.Count} 多段线={selLines.Count} 直线={selSegs.Count} " +
               $"取全部可见线={usingAll} 场景实体={_scene.Entities.Count} " +
               $"导入线框段数={(_lastImport?.LineVertices.Length ?? 0) / 12}");

        // ① 选了多段线(等值线建面)：线的顶点当输入点、线段当**约束边**。
        //    此前这条路走不通 —— 只收点实体, 多段线仅被当作裁剪边界, 于是最常见的
        //    "选一堆等值线建面"只会提示"需 ≥3 个点"。
        //    约束边不能省: 等值线是地形结构线, 只当散点做无约束 Delaunay 会连出
        //    跨山脊/沟谷的三角形, 面就糊了。
        if (selLines.Count > 0 || selSegs.Count > 0)
        {
            var input = selLines.Select(l => new PolylineTin.Line
            {
                Points = l.Points,
                Z = l.Has3D ? Enumerable.Range(0, l.Points.Count).Select(l.ZAt).ToList() : null,
                FlatZ = l.Elevation,
                Closed = l.Closed,
            }).ToList();
            input.AddRange(selSegs.Select(l => new PolylineTin.Line
            {
                Points = new[] { (l.X0, l.Y0), (l.X1, l.Y1) },
                FlatZ = l.Elevation,
                Closed = false,
            }));
            var col = PolylineTin.Collect(input);
            TinLog($"通路①线建面: 收集顶点={col.Verts.Count} 约束={col.Constraints.Count} 合并重合点={col.MergedVertices}");

            // 同时选中的散点一并作为输入(点线混选; 原版 AcDbPoint 走 StandalonePoint)
            foreach (var pe in selPts) col.Verts.Add((pe.X, pe.Y, pe.Elevation));

            string src = (usingAll ? "未选中对象, 取场景全部可见线 " : "") + $"{selLines.Count + selSegs.Count} 条线";
            if (!BuildTinFromConstraints(col, src, TinLog)) return;
            return;
        }

        // ② 什么都没选, 但有「显示态线框」导入(.3dm/OFF 等值线走的是显示通道, 不是场景实体、
        //    选不中) —— 直接拿这份线段建面, 否则这类图纸永远"建不出三角网"。
        if (selPts.Count == 0 && _lastImport is { LineVertices.Length: >= 12 })
        {
            var col = PolylineTin.CollectSegments(_lastImport.LineVertices);
            TinLog($"通路②导入线框: 收集顶点={col.Verts.Count} 约束={col.Constraints.Count} 合并重合点={col.MergedVertices}");
            if (col.Verts.Count >= 3) { BuildTinFromConstraints(col, "取自导入图形", TinLog); return; }
        }

        // ③ 只有点：散点 Delaunay
        if (selPts.Count == 0)
        {
            TinLog("通路③: 场景里既没有可用的线, 也没有导入线框 —— 无输入");
            // 场景里既没有线也没有导入线框, 才谈得上"要点" —— 明说找不到什么, 别默默弹文件框
            StatusMsg.Text = "创建三角网：场景里没有可用的线或点。请先导入/绘制等值线(或高程点)，"
                           + "或选中若干点后再执行；也可从 CSV 导入点。";
        }
        var pts = await PickPointsAsync("创建三角网", 3);
        if (pts.Count < 3) { StatusMsg.Text = "创建三角网：需 ≥3 个点或 ≥1 条线(先在场景中选中, 或从 CSV 导入点)"; return; }
        var p3 = Pts3(pts);
        var p2 = p3.Select(p => (p.x, p.y)).ToList();
        List<(int a, int b, int c)> tris = Delaunay.Triangulate(p2);
        if (tris.Count == 0) { StatusMsg.Text = "创建三角网：点太少或共线，无法剖分"; return; }
        tris = OrientUp(p3, tris);   // 统一绕向(法线朝上), 边界环/成体/内外判定都依赖一致绕向
        var me = AddMesh(new MeshEntity(NewMeshName("三角网"), p3, tris), true);
        SelectEntities(new SceneEntity[] { me });
        var st = TinSurface.Describe(p3, tris);
        StatusMsg.Text = $"创建三角网「{me.Name}」：{p3.Count} 点 → {tris.Count} 三角 · 投影面积 {st.ProjectedAreaXY:0.#} · 高程 {st.ZMin:0.#}~{st.ZMax:0.#}";
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
        bool closed = lines[0].Closed || lines[1].Closed;   // 照原版 SIDEMESH：任一闭合即按环形侧壁
        var (v, t) = SideSurface.Loft(a, b, closed, false);
        if (t.Count == 0) { StatusMsg.Text = "侧面三角网：放样失败(线太短或退化)"; return; }
        var me = AddMesh(new MeshEntity(NewMeshName("侧面"), v, t) { Cr = 0.8f, Cg = 0.7f, Cb = 0.4f }, false);
        SelectEntities(new SceneEntity[] { me });
        StatusMsg.Text = $"侧面三角网「{me.Name}」：顶线 {a.Count} 点 / 底线 {b.Count} 点 → {t.Count} 三角({(closed ? "闭合环壁" : "开放侧面")}·最短横档放样)";
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
        PromptValues? dlg;
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

    private double? MeshZ(MeshEntity? m, double x, double y)
    {
        if (m == null) return null;
        if (!_surfGrids.TryGetValue(m, out var g)) { var (fv, ft) = m.Flatten(); var b = m.Bounds; g = new SurfaceVolume.TriGrid(fv, ft, Math.Max(b.maxX - b.minX, b.maxY - b.minY) / 200 + 1e-9); _surfGrids[m] = g; }
        return g.Sample(x, y);
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
        await MdlContourFromMeshAsync(meshes[0], "构建等值线", null);
    }

    /// <summary>
    /// 从一张三角网按等高距抽等值线入库（「构建等值线」与点云组「等高线生产」共用）。
    /// defaultDz 为 null 时按网格高差的 1/10 取默认；点云组按原版给 5m。
    /// </summary>
    private async Task MdlContourFromMeshAsync(MeshEntity m, string title, double? defaultDz)
    {
        var b = m.Bounds;
        double zr = b.maxZ - b.minZ;
        double dzDef = defaultDz ?? (zr > 0 ? Math.Max(Math.Round(zr / 10, 1), 0.1) : 1);
        var dlg = await PromptDialog.AskAsync(this, title, new[] { new PromptDialog.Field("dz", "等高距", dzDef.ToString("0.###", Inv), "m"), new PromptDialog.Field("cell", "采样格距", (Math.Max(b.maxX - b.minX, b.maxY - b.minY) / 200).ToString("0.###", Inv), "m") }, $"从三角网「{m.Name}」采样栅格提取等值线");
        if (dlg == null) return;
        await MdlContourCoreAsync(m, title, Math.Max(dlg.D("dz"), 1e-6), Math.Max(dlg.D("cell"), 1e-6));
    }

    /// <summary>等值线抽取本体（对话框已取好参数）：栅格采样 + Marching Squares + 连段入库。</summary>
    private async Task MdlContourCoreAsync(MeshEntity m, string title, double dz, double cell)
    {
        await Task.Yield();
        var b = m.Bounds;
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
        StatusMsg.Text = $"{title}「{m.Name}」：{levels.Count} 层(等高距 {dz:0.##}) → {added.Count} 条三维等值线({segsN} 段, 格距 {cell:0.##})";
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

    /// <summary>
    /// 实时曲面坐标（开关）：开启后鼠标在三角网上移动时，光标旁浮动气泡实时显示落点的真实三维坐标。
    /// 状态栏那份仍是投影平面坐标 —— 两者不是一回事，原版特意区分开。
    /// </summary>
    private void MdlToggleSurfaceCoord()
    {
        _surfCoordOn = !_surfCoordOn;
        if (!_surfCoordOn)
        {
            _surfGrids.Clear();
            HideSurfaceCoordTip();
        }
        StatusMsg.Text = _surfCoordOn
            ? "实时曲面坐标：开（光标移到三角网上，旁边气泡显示落点真实 X/Y/Z；再点一次关闭）"
            : "实时曲面坐标：关";
    }

    private void HideSurfaceCoordTip()
    {
        var tip = _active.SurfTip;
        if (tip == null || tip.Opacity == 0) return;
        tip.Opacity = 0;
        (tip.Parent as Avalonia.Controls.Control)?.InvalidateVisual();
    }

    /// <summary>
    /// 指针移动时刷新曲面坐标气泡：取光标 XY 落点所在三角形、Z 最高的那张网的曲面高程。
    /// 关着、或光标不在任何三角网上，就把气泡收起来。
    /// </summary>
    private void UpdateSurfaceCoordTip(Avalonia.Point screen, (double x, double y)? world)
    {
        var tip = _active.SurfTip;
        if (tip == null) return;
        if (!_surfCoordOn || world == null) { HideSurfaceCoordTip(); return; }

        double bestZ = double.NegativeInfinity;
        MeshEntity? hit = null;
        foreach (var m in AllMeshes())
        {
            var b = m.Bounds;
            if (world.Value.x < b.minX || world.Value.x > b.maxX || world.Value.y < b.minY || world.Value.y > b.maxY) continue;
            var z = MeshZ(m, world.Value.x, world.Value.y);
            if (z.HasValue && z.Value > bestZ) { bestZ = z.Value; hit = m; }
        }
        if (hit == null) { HideSurfaceCoordTip(); return; }

        ((Avalonia.Controls.TextBlock)tip.Child!).Text =
            $"X {world.Value.x:0.00}  Y {world.Value.y:0.00}  Z {bestZ:0.00}   [{hit.Name}]";
        tip.Margin = new Avalonia.Thickness(screen.X + 18, Math.Max(0, screen.Y - 30), 0, 0);
        tip.Opacity = 1;
        (tip.Parent as Avalonia.Controls.Control)?.InvalidateVisual();
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
    /// <summary>
    /// 平面三角网(2D Delaunay 出来的 TIN)的统一绕向：直接按 XY 有向面积把每个三角形摆正，法线朝上。
    ///
    /// 不能走 <see cref="MeshOrient.MakeConsistent"/>：那套是按邻接关系逐个三角形传播绕向，
    /// 给任意曲面(可能折叠、可能多连通)用的，代价很大 —— 实测 19.6 万三角形要 **37 秒**，
    /// 用户看到的"建网卡死"就卡在这一步(日志正好断在剖分完成之后)。
    /// 平面剖分的结果不可能折叠, 每个三角形自己的有向面积就唯一决定了朝向, 一遍扫完即可。
    /// </summary>
    private static List<(int a, int b, int c)> OrientUpPlanar(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var r = new List<(int a, int b, int c)>(t.Count);
        foreach (var (a, b, c) in t)
        {
            if (a >= v.Count || b >= v.Count || c >= v.Count) continue;
            double cross = (v[b].x - v[a].x) * (v[c].y - v[a].y) - (v[c].x - v[a].x) * (v[b].y - v[a].y);
            r.Add(cross < 0 ? (a, c, b) : (a, b, c));   // 统一成逆时针(法线朝上)
        }
        return r;
    }

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

public partial class MainWindow
{
    /// <summary>渲染配置：三角网显示模式 + 高程着色（原「渲染配置」实体/线框着色管线的托管等价）。</summary>
    private async Task MdlRenderConfigAsync()
    {
        string cur = MeshEntity.RenderMode switch { MeshEntity.DisplayMode.Wireframe => "线框", MeshEntity.DisplayMode.Shaded => "着色面", _ => "着色面+线框" };
        var dlg = await PromptDialog.AskAsync(this, "渲染配置", new[]
        {
            new PromptDialog.Field("mode", "三角网显示", cur, null, "线框=只画边线; 着色面=平行光着色的面模型; 着色面+线框=面上叠压暗边线", false, new[] { "线框", "着色面", "着色面+线框" }),
            new PromptDialog.Field("elev", "面着色依据", MeshEntity.ColorByElevation ? "高程色带" : "实体颜色", null, "高程色带: 低绿→黄→棕→高白", false, new[] { "实体颜色", "高程色带" }),
        }, "作用于场景中全部三角网(含地质体/块体外的面模型)，改完即时重绘");
        if (dlg == null) return;
        var mode = dlg.S("mode") switch { "线框" => MeshEntity.DisplayMode.Wireframe, "着色面" => MeshEntity.DisplayMode.Shaded, _ => MeshEntity.DisplayMode.ShadedWireframe };
        SetMeshDisplay(mode, dlg.S("elev") == "高程色带");
    }

    private void SetMeshDisplay(MeshEntity.DisplayMode? mode, bool? byElevation)
    {
        if (mode.HasValue) MeshEntity.RenderMode = mode.Value;
        if (byElevation.HasValue) MeshEntity.ColorByElevation = byElevation.Value;
        RefreshScene();
        string m = MeshEntity.RenderMode switch { MeshEntity.DisplayMode.Wireframe => "线框", MeshEntity.DisplayMode.Shaded => "着色面", _ => "着色面+线框" };
        StatusMsg.Text = $"渲染配置：三角网 {m} · 面色 {(MeshEntity.ColorByElevation ? "高程色带" : "实体颜色")}（{AllMeshes().Count} 张网）";
    }
}

public partial class MainWindow
{
    /// <summary>3DMine .3dm → 每张网一个 MeshEntity(名=网格标签, 色=网格色, 图层=名) 入场景, 面模型显示; 失败回退旧线框显示通道。</summary>
    private bool ImportTdmAsMeshes(string path)
    {
        var r = Cad.TdmImportService.LoadMeshes(path);
        if (!r.Success) return false;
        BeginChange();
        var made = new List<MeshEntity>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int tris = 0;
        foreach (var m in r.Meshes)
        {
            var verts = new List<(double x, double y, double z)>(m.Vx.Length);
            for (int i = 0; i < m.Vx.Length; i++) verts.Add((m.Vx[i], m.Vy[i], m.Vz[i]));
            var tl = new List<(int a, int b, int c)>(m.Indices.Length / 3);
            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                int a = m.Indices[t], b = m.Indices[t + 1], c = m.Indices[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
                tl.Add((a, b, c));
            }
            if (tl.Count == 0) continue;
            var me = new MeshEntity(NewMeshName(m.Name), verts, tl);
            if (m.HasColor) { me.Cr = m.R / 255f; me.Cg = m.G / 255f; me.Cb = m.B / 255f; }
            var l = _layers.Get(m.Name) ?? _layers.EnsureImported(m.Name, me.Cr, me.Cg, me.Cb);
            me.LayerName = l.Name;
            _scene.Add(me); made.Add(me); tris += tl.Count;
            var bb = me.Bounds; minX = Math.Min(minX, bb.minX); minY = Math.Min(minY, bb.minY); maxX = Math.Max(maxX, bb.maxX); maxY = Math.Max(maxY, bb.maxY);
        }
        if (made.Count == 0) return false;
        PopulateDrawingLayers();
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        string warn = r.Warnings.Count > 0 ? " · " + r.Warnings[^1] : "";
        StatusMsg.Text = $"已导入 {System.IO.Path.GetFileName(path)}：{made.Count} 张三角网 · {tris} 三角（场景对象, 面模型显示; 渲染配置可切线框）{warn}";
        return true;
    }
}
