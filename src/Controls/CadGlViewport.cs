using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using static Avalonia.OpenGL.GlConsts;

namespace PitMine3D.Kylin.Controls;

/// <summary>
/// CAD OpenGL 视口 —— 对应内核 xllAcGi 的 Viewport（D3D11 → OpenGL 迁移落点）。
/// 渲染管线照内核三趟结构还原：
///   BeginFrame → GridPass(地面网格/轴, 深度关) → ScenePass(实体, 深度开)
///              → OverlayPass(左下角坐标罗盘, 深度关) → EndFrame。
/// 绘制经 <see cref="GlRenderer"/>(对应内核 Renderer) 抽象，相机经 <see cref="Camera"/>。
/// 着色器兼容 GLES 3.00(麒麟国产 GPU / ANGLE) 与桌面 GL 3.30 —— 同一份代码。
/// </summary>
public class CadGlViewport : OpenGlControlBase
{
    private const int GL_LINES = 0x0001;   // GlConsts 未定义，本地补

    private readonly GlRenderer _renderer = new();
    private readonly Camera _camera = new();
    private GlExtras _ext = null!;
    private bool _isGles;

    // 静态网格：地面网格+轴 / 示例实体 / 罗盘
    private GlRenderer.Mesh _grid, _cube, _gizmo;

    // 导入的图纸线框（世界坐标 P3_C3 线段）。上传须在 GL 线程，故 UI 线程只挂起数据，下一帧消费。
    // _pendingImport 保留世界坐标源(不清空)——切换标签会销毁并重建 GL 上下文, 需据此重传, 否则线框丢失。
    private GlRenderer.Mesh _imported;
    private bool _hasImported;
    private float[]? _pendingImport;
    private bool _importedDirty;
    private double[]? _pendingBounds;

    // 托管绘制场景几何（Home 绘制命令画的实体）
    private GlRenderer.Mesh _scene;
    private bool _hasScene;
    private float[]? _pendingScene;
    private bool _sceneDirty;

    // 图层显隐：图层名 → 该层几何；隐藏集
    private Dictionary<string, float[]>? _layerGeom;
    private readonly HashSet<string> _hiddenLayers = new();

    private double[]? _lastBounds;   // 最近导入的包围盒，供 ZE 重新范围缩放
    private bool _showGrid = true;   // 网格/轴显隐

    // 渲染局部原点(世界 XY)：CGCS2000 等大坐标(X~5e5、Y~4e6)直接进 float32 矩阵会灾难性抵消，
    // 旋转时几何被变换到 NaN/视锥外而"消失"。故把几何与相机整体平移到近原点渲染，屏幕读数再加回。
    // 首次拿到有效包围盒时定原点并锁定(整篇文档稳定)，ClearImported 复位。仅 XY 需要(Z 本就小)。
    private double _ox, _oy;
    private bool _originSet;

    // 选择高亮（对象树选类型 → 高亮其几何）
    private GlRenderer.Mesh _highlight;
    private bool _hasHighlight;
    private float[]? _pendingHighlight;
    private bool _highlightDirty;

    // 对象捕捉标记（光标吸附点的十字）
    private GlRenderer.Mesh _snap;
    private bool _hasSnap;
    private float[]? _pendingSnap;
    private bool _snapDirty;

    // CAD 十字光标（满视口横竖两线 + 中心拾取框, 随光标移动; 屏幕对齐, NDC 直接画）。对应内核光标(GetCursorPos)。
    private const double CursorBoxPx = 7;                 // 中心拾取框半边长(像素)
    private GlRenderer.Mesh _cursor;
    private bool _hasCursor;
    private double _cursorSx, _cursorSy;                 // 光标屏幕坐标(DIP)
    private bool _showCursor;                             // 光标在视口内 → 显示
    private double _curBuiltSx = double.NaN, _curBuiltSy;// 上次建网格的光标位/尺寸(变了才重建)
    private double _curBuiltW, _curBuiltH;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>OpenGL 上下文就绪后回报后端版本串给界面。</summary>
    public event Action<string>? GlReady;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try { OnOpenGlInitCore(gl); }
        catch (Exception ex) { Console.Error.WriteLine("[GLINIT-FAIL] " + ex); throw; }
    }

    private void OnOpenGlInitCore(GlInterface gl)
    {
        Console.Error.WriteLine($"[GLINIT] called. type={GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}");
        _ext = new GlExtras(gl);
        _isGles = GlVersion.Type == GlProfileType.OpenGLES;

        string backend = $"{(_isGles ? "OpenGL ES" : "OpenGL")} {GlVersion.Major}.{GlVersion.Minor}";
        Dispatcher.UIThread.Post(() => GlReady?.Invoke(backend));

        _renderer.Init(gl, _ext, _isGles);
        _grid = _renderer.Upload(BuildGrid(10, 1f));
        _cube = _renderer.Upload(BuildCube());
        _gizmo = _renderer.Upload(BuildGizmo());

        // GL 上下文(重)建后, 旧上下文里上传的网格句柄已失效——从保留的托管源重新入队, 下一帧重传。
        // 切换文档标签时 Avalonia 会 Deinit/Init 该视口(销毁并重建上下文), 有此重传各文档几何才不丢/不空白。
        if (_pendingImport is { Length: > 0 }) _importedDirty = true;
        if (_pendingScene != null) _sceneDirty = true;
        if (_pendingHighlight != null) _highlightDirty = true;
        if (_pendingSnap != null) _snapDirty = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer.DeleteMesh(_grid);
        _renderer.DeleteMesh(_cube);
        _renderer.DeleteMesh(_gizmo);
        if (_hasImported) _renderer.DeleteMesh(_imported);
        if (_hasHighlight) _renderer.DeleteMesh(_highlight);
        if (_hasSnap) _renderer.DeleteMesh(_snap);
        if (_hasScene) _renderer.DeleteMesh(_scene);
        if (_hasCursor) _renderer.DeleteMesh(_cursor);
        _renderer.Deinit();
        // 上下文销毁——句柄失效, 复位标志; 否则重建后旧 id 可能撞上新网格(grid/cube/gizmo)致误删/花屏。
        // 保留的托管源(_pendingImport/_pendingScene/…)不动, OnOpenGlInit 会据此重新入队重传。
        _hasImported = _hasScene = _hasHighlight = _hasSnap = false;
        _hasCursor = false; _curBuiltSx = double.NaN;   // 十字光标下次移动即按当前上下文重建
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        double scale = VisualRoot?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scale));
        int h = Math.Max(1, (int)(Bounds.Height * scale));
        float aspect = h == 0 ? 1f : (float)w / h;

        // 消费待上传的导入几何（必须在 GL 线程 = 本回调内）。源保留于 _pendingImport 供上下文重建后重传。
        if (_importedDirty)
        {
            _importedDirty = false;
            var src = _pendingImport ?? Array.Empty<float>();
            EnsureOrigin(_pendingBounds);
            if (_hasImported) _renderer.DeleteMesh(_imported);
            _imported = _renderer.Upload(Localize(src));
            _hasImported = !_imported.IsEmpty;
            // 缩放一次：仅导入/分层时 _pendingBounds 非空才对准; 之后置空, 切换重传不再动相机。
            if (_pendingBounds != null) { _camera.FitBounds(LocalizeBounds(_pendingBounds)); _pendingBounds = null; }
        }

        if (_highlightDirty)
        {
            _highlightDirty = false;
            if (_hasHighlight) _renderer.DeleteMesh(_highlight);
            _highlight = _renderer.Upload(Localize(_pendingHighlight!));
            _hasHighlight = !_highlight.IsEmpty;
        }

        if (_snapDirty)
        {
            _snapDirty = false;
            if (_hasSnap) _renderer.DeleteMesh(_snap);
            _snap = _renderer.Upload(Localize(_pendingSnap!));
            _hasSnap = !_snap.IsEmpty;
        }

        if (_sceneDirty)
        {
            _sceneDirty = false;
            if (_hasScene) _renderer.DeleteMesh(_scene);
            _scene = _renderer.Upload(Localize(_pendingScene!));
            _hasScene = !_scene.IsEmpty;
        }

        // CAD 十字光标：光标位/视口尺寸变了才重建满屏横竖两线(NDC, 屏幕对齐, 与几何无关不受相机影响)。
        if (_showCursor && Bounds.Width > 0 && Bounds.Height > 0 &&
            (_cursorSx != _curBuiltSx || _cursorSy != _curBuiltSy || Bounds.Width != _curBuiltW || Bounds.Height != _curBuiltH))
        {
            _curBuiltSx = _cursorSx; _curBuiltSy = _cursorSy; _curBuiltW = Bounds.Width; _curBuiltH = Bounds.Height;
            float nx = (float)(2.0 * _cursorSx / Bounds.Width - 1.0);
            float ny = (float)(1.0 - 2.0 * _cursorSy / Bounds.Height);
            float hx = (float)(CursorBoxPx * 2.0 / Bounds.Width);    // 中心拾取框半宽(px→NDC)
            float hy = (float)(CursorBoxPx * 2.0 / Bounds.Height);
            const float r = 1f, g = 1f, b = 1f;   // 白色十字光标(醒目, 区别于红/绿轴与灰网格)
            float[] verts = {
                nx, -1f, 0f, r, g, b,   nx, 1f, 0f, r, g, b,        // 竖线(满屏)
                -1f, ny, 0f, r, g, b,   1f, ny, 0f, r, g, b,        // 横线(满屏)
                nx - hx, ny - hy, 0f, r,g,b,   nx + hx, ny - hy, 0f, r,g,b,   // 拾取框: 下
                nx + hx, ny - hy, 0f, r,g,b,   nx + hx, ny + hy, 0f, r,g,b,   // 右
                nx + hx, ny + hy, 0f, r,g,b,   nx - hx, ny + hy, 0f, r,g,b,   // 上
                nx - hx, ny + hy, 0f, r,g,b,   nx - hx, ny - hy, 0f, r,g,b,   // 左
            };
            if (_hasCursor) _renderer.DeleteMesh(_cursor);
            _cursor = _renderer.Upload(verts);
            _hasCursor = !_cursor.IsEmpty;
        }

        float[] vp = _camera.ViewProj(aspect);

        _renderer.BeginFrame(w, h, 0.13f, 0.14f, 0.16f);
        GridPass(vp);
        ScenePass(vp);
        HighlightPass(vp);
        OverlayPass(w, h);
        CursorPass();
        _renderer.EndFrame();

        // 连续动画：请求下一帧
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    // ---------- 三趟管线（对应内核 RenderGridPass / RenderScenePass / RenderOverlayPass）----------

    /// <summary>地面自适应网格 + XYZ 轴。深度关 —— 作背景，永远在实体之后。</summary>
    private void GridPass(float[] vp)
    {
        if (!_showGrid) return;
        _renderer.BeginPass(depthTest: false);
        _renderer.Draw(_grid, GL_LINES, vp);
        _renderer.EndPass();
    }

    /// <summary>
    /// 场景实体。已导入图纸时画其线框（世界坐标）；否则画示例自转立方体。
    /// 将来接内核 AcDb 几何后由内核渲染路径取代。深度开。
    /// </summary>
    private void ScenePass(float[] vp)
    {
        _renderer.BeginPass(depthTest: true);
        if (_hasImported) _renderer.Draw(_imported, GL_LINES, vp);
        if (_hasScene) _renderer.Draw(_scene, GL_LINES, vp);
        _renderer.EndPass();
    }

    /// <summary>选择高亮：选中类型的几何用高亮色画在最上层。深度关。</summary>
    private void HighlightPass(float[] vp)
    {
        if (!_hasHighlight && !_hasSnap) return;
        _renderer.BeginPass(depthTest: false);
        if (_hasHighlight) _renderer.Draw(_highlight, GL_LINES, vp);
        if (_hasSnap) _renderer.Draw(_snap, GL_LINES, vp);
        _renderer.EndPass();
    }

    /// <summary>屏幕空间叠加层：左下角坐标罗盘（随相机朝向）。深度关。对应内核 UCS 指示器。</summary>
    private void OverlayPass(int w, int h)
    {
        _renderer.BeginPass(depthTest: false);
        int g = Math.Clamp(Math.Min(w, h) / 6, 64, 120);
        _renderer.SetViewport(12, 12, g, g);
        _renderer.Draw(_gizmo, GL_LINES, _camera.GizmoViewProj());
        _renderer.SetViewport(0, 0, w, h);   // 还原全屏视口
        _renderer.EndPass();
    }

    /// <summary>CAD 十字光标：满视口横竖两线（屏幕对齐，NDC 直接画，恒定 Identity 矩阵）。深度关，最上层。</summary>
    private void CursorPass()
    {
        if (!_showCursor || !_hasCursor) return;
        _renderer.BeginPass(depthTest: false);
        _renderer.Draw(_cursor, GL_LINES, Mat4.Identity());
        _renderer.EndPass();
    }

    // ---------- 交互 API（供宿主 Panel 转发）----------
    // OpenGlControlBase 自身命中测试不可靠（无背景时部分后端收不到指针），
    // 交互统一由宿主 Panel（可命中）转发到相机。
    /// <summary>轨道旋转（增量已由宿主换算好）。</summary>
    public void Orbit(double dYaw, double dPitch) => _camera.Orbit(dYaw, dPitch);

    /// <summary>缩放。factor &lt;1 拉近，&gt;1 拉远。</summary>
    public void Zoom(double factor) => _camera.Zoom(factor);

    /// <summary>屏幕拖拽平移（光标抓取的世界点跟随光标；2D/3D 通用）。</summary>
    public void Pan(double sx0, double sy0, double sx1, double sy1)
    {
        _camera.PanScreen(sx0, sy0, sx1, sy1, Bounds.Width, Bounds.Height);
        RequestNextFrameRendering();
    }

    /// <summary>朝光标缩放（缩放后光标下的点不动）。</summary>
    public void ZoomAt(double sx, double sy, double factor)
    {
        _camera.ZoomAtScreen(sx, sy, Bounds.Width, Bounds.Height, factor);
        RequestNextFrameRendering();
    }

    /// <summary>更新 CAD 十字光标屏幕位置（DIP）并显示；宿主 Panel 的 PointerMoved 转发。</summary>
    public void SetCursorScreen(double sx, double sy)
    {
        _cursorSx = sx; _cursorSy = sy; _showCursor = true;
        RequestNextFrameRendering();
    }

    /// <summary>隐藏十字光标（光标离开视口）。</summary>
    public void HideCursor()
    {
        if (!_showCursor) return;
        _showCursor = false;
        RequestNextFrameRendering();
    }

    /// <summary>设置托管绘制场景几何（P3_C3）；空 → 清除。</summary>
    public void SetSceneGeometry(float[] verts)
    {
        _pendingScene = verts ?? Array.Empty<float>();
        _sceneDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// 显示导入的线框几何（世界坐标交错 P3_C3 线段）并范围缩放到其包围盒。
    /// UI 线程调用；实际 GL 上传延到下一帧渲染回调（GL 线程）执行。
    /// </summary>
    public void ShowImportedGeometry(float[] lineVertices, double[] bounds)
    {
        EnsureOrigin(bounds);
        _pendingImport = lineVertices;
        _pendingBounds = bounds;
        _lastBounds = bounds;
        _importedDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>按图层显示导入几何（可分层显隐）+ 范围缩放。</summary>
    public void ShowImportedLayers(Dictionary<string, float[]> layerGeom, double[] bounds)
    {
        _layerGeom = layerGeom;
        _hiddenLayers.Clear();
        EnsureOrigin(bounds);
        _pendingImport = ConcatVisible();
        _pendingBounds = bounds;
        _lastBounds = bounds;
        _importedDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>清除导入几何（新建/关闭文档）。</summary>
    public void ClearImported()
    {
        _layerGeom = null;
        _hiddenLayers.Clear();
        _pendingImport = Array.Empty<float>();
        _importedDirty = true;
        _originSet = false; _ox = 0; _oy = 0;   // 新文档：复位渲染局部原点
        RequestNextFrameRendering();
    }

    /// <summary>切换某图层显隐并重建可见几何。</summary>
    public void SetLayerVisible(string layer, bool visible)
    {
        if (_layerGeom == null) return;
        if (visible) _hiddenLayers.Remove(layer); else _hiddenLayers.Add(layer);
        _pendingImport = ConcatVisible();
        _pendingBounds = null;                 // 显隐不重新缩放
        _importedDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>范围缩放到最近导入几何（ZE / ZOOMEXTENTS）。</summary>
    public void ZoomExtents()
    {
        if (_lastBounds == null) return;
        _camera.FitBounds(LocalizeBounds(_lastBounds));
        RequestNextFrameRendering();
    }

    /// <summary>框住指定包围盒 [minX,minY,maxX,maxY]（供绘制/点导入等非导入几何缩放），并记为 ZE 目标。</summary>
    public void FitBounds(double[] bounds)
    {
        if (bounds == null || bounds.Length < 4) return;
        _lastBounds = bounds;
        EnsureOrigin(bounds);
        _camera.FitBounds(LocalizeBounds(bounds));
        RequestNextFrameRendering();
    }

    /// <summary>切换地面网格 / 轴显隐（GRID）。</summary>
    public void ToggleGrid()
    {
        _showGrid = !_showGrid;
        RequestNextFrameRendering();
    }

    /// <summary>高亮一组几何（P3_C3 位置，重着色为高亮色）；null/空 → 清除高亮。</summary>
    public void SetHighlight(float[]? geom)
    {
        _pendingHighlight = (geom == null || geom.Length == 0) ? Array.Empty<float>() : Recolor(geom, 1f, 0.9f, 0.2f);
        _highlightDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>把交错 P3_C3 几何整体重着色（位置不变，颜色替换）。</summary>
    internal static float[] Recolor(float[] src, float r, float g, float b)
    {
        var dst = (float[])src.Clone();
        for (int i = 0; i + 5 < dst.Length; i += 6) { dst[i + 3] = r; dst[i + 4] = g; dst[i + 5] = b; }
        return dst;
    }

    /// <summary>设置对象捕捉标记几何（P3_C3，已含颜色）；null/空 → 清除。</summary>
    public void SetSnapMarker(float[]? cross)
    {
        _pendingSnap = (cross == null || cross.Length == 0) ? Array.Empty<float>() : cross;
        _snapDirty = true;
        RequestNextFrameRendering();
    }

    // 首次拿到有效包围盒时锁定渲染局部原点(XY 中心)，整篇文档稳定。
    private void EnsureOrigin(double[]? bounds)
    {
        if (_originSet || bounds == null || bounds.Length < 4) return;
        if (bounds[2] <= bounds[0] || bounds[3] <= bounds[1]) return;   // 空/退化包围盒不定原点
        _ox = (bounds[0] + bounds[2]) * 0.5;
        _oy = (bounds[1] + bounds[3]) * 0.5;
        _originSet = true;
    }

    // 世界 P3_C3 → 渲染局部(仅减 XY 原点；Z/颜色不动)。原点未定或空则原样返回。
    // 减法在 float 内近距抵消(Sterbenz)精确，仅保留上传时已有的量化；结果近原点 → 矩阵不再抵消。
    private float[] Localize(float[]? world)
    {
        if (!_originSet || world == null || world.Length == 0) return world ?? Array.Empty<float>();
        var v = (float[])world.Clone();
        float ox = (float)_ox, oy = (float)_oy;
        for (int i = 0; i + 5 < v.Length; i += 6) { v[i] -= ox; v[i + 1] -= oy; }
        return v;
    }

    // 包围盒 [minX,minY,maxX,maxY] 减原点，供相机在局部空间 FitBounds。
    private double[]? LocalizeBounds(double[]? b)
    {
        if (b == null || b.Length < 4 || !_originSet) return b;
        return new[] { b[0] - _ox, b[1] - _oy, b[2] - _ox, b[3] - _oy };
    }

    // 拼接所有可见图层的几何为一段连续缓冲
    private float[] ConcatVisible()
    {
        if (_layerGeom == null) return Array.Empty<float>();
        int total = 0;
        foreach (var kv in _layerGeom)
            if (!_hiddenLayers.Contains(kv.Key)) total += kv.Value.Length;
        var buf = new float[total];
        int off = 0;
        foreach (var kv in _layerGeom)
            if (!_hiddenLayers.Contains(kv.Key)) { Array.Copy(kv.Value, 0, buf, off, kv.Value.Length); off += kv.Value.Length; }
        return buf;
    }

    /// <summary>切换 2D 平面 / 3D 轨道视图。</summary>
    private readonly System.Collections.Generic.List<Camera.State> _viewHistory = new();
    private void PushView()
    {
        _viewHistory.Add(_camera.Snapshot());
        if (_viewHistory.Count > 20) _viewHistory.RemoveAt(0);
    }

    /// <summary>上一视图：恢复到最近一次视图变更前的相机状态。</summary>
    public bool PrevView()
    {
        if (_viewHistory.Count == 0) return false;
        _camera.Restore(_viewHistory[^1]);
        _viewHistory.RemoveAt(_viewHistory.Count - 1);
        RequestNextFrameRendering();
        return true;
    }

    public void SetViewMode(bool is2D)
    {
        PushView();
        _camera.SetMode(is2D);
        RequestNextFrameRendering();
    }

    /// <summary>标准视图预设(Z 上约定)：top/bottom/front/back/left/right/sw/se/ne/nw。俯/仰视走 2D 正交。</summary>
    public void SetView(string preset)
    {
        PushView();
        const double iso = 0.61547971;   // atan(1/√2) ≈ 35.26°
        const double pi = System.Math.PI;
        switch (preset)
        {
            case "top": _camera.SetMode(true); break;                       // 俯视 = 正交俯视
            case "bottom": _camera.SetOrientation(-pi / 2, -1.4); break;    // 仰视
            case "front": _camera.SetOrientation(-pi / 2, 0); break;        // 主视(看向 +Y)
            case "back": _camera.SetOrientation(pi / 2, 0); break;          // 后视
            case "left": _camera.SetOrientation(pi, 0); break;             // 左视(看向 +X)
            case "right": _camera.SetOrientation(0, 0); break;             // 右视
            case "sw": _camera.SetOrientation(5 * pi / 4, iso); break;      // 西南等轴测
            case "se": _camera.SetOrientation(-pi / 4, iso); break;         // 东南等轴测
            case "ne": _camera.SetOrientation(pi / 4, iso); break;          // 东北等轴测
            case "nw": _camera.SetOrientation(3 * pi / 4, iso); break;      // 西北等轴测
            default: _camera.SetMode(false); break;
        }
        RequestNextFrameRendering();
    }

    /// <summary>当前是否 2D 平面视图。</summary>
    public bool Is2DView => _camera.Is2D;

    /// <summary>屏幕像素（相对本控件）→ Z=0 平面世界坐标，供状态栏坐标读数。相机在局部空间求解，读数加回原点得绝对世界坐标。</summary>
    public (double x, double y)? ScreenToWorld(double sx, double sy)
    {
        var p = _camera.ScreenToWorldOnZPlane(sx, sy, Bounds.Width, Bounds.Height);
        return p == null ? null : (p.Value.x + _ox, p.Value.y + _oy);
    }

    // ---------- 几何（示例内容；接入内核后由 AcDb worldDraw 提供）----------
    private static float[] BuildGrid(int n, float step)
    {
        var v = new List<float>();
        float ext = n * step;
        void Line(float x0, float y0, float x1, float y1, float r, float g, float b)
        {
            v.AddRange(new[] { x0, y0, 0f, r, g, b, x1, y1, 0f, r, g, b });
        }
        for (int i = -n; i <= n; i++)
        {
            float c = i == 0 ? 0.30f : 0.24f;
            Line(i * step, -ext, i * step, ext, c, c, c);
            Line(-ext, i * step, ext, i * step, c, c, c);
        }
        // 轴：X 红 Y 绿 Z 蓝
        v.AddRange(new[] { 0f, 0f, 0f, 0.85f, 0.20f, 0.20f, ext, 0f, 0f, 0.85f, 0.20f, 0.20f });
        v.AddRange(new[] { 0f, 0f, 0f, 0.25f, 0.75f, 0.25f, 0f, ext, 0f, 0.25f, 0.75f, 0.25f });
        v.AddRange(new[] { 0f, 0f, 0f, 0.30f, 0.50f, 0.95f, 0f, 0f, ext * 0.5f, 0.30f, 0.50f, 0.95f });
        return v.ToArray();
    }

    private static float[] BuildCube()
    {
        // 单位立方体 [-1,1]^3，每面一色。pos(3)+color(3)。
        (float r, float g, float b)[] faceCol =
        {
            (0.90f, 0.55f, 0.20f), (0.80f, 0.42f, 0.14f),
            (0.55f, 0.62f, 0.72f), (0.42f, 0.50f, 0.60f),
            (0.72f, 0.72f, 0.30f), (0.58f, 0.58f, 0.22f)
        };
        float[][] faces =
        {
            new[] { -1f,-1f, 1f,  1f,-1f, 1f,  1f, 1f, 1f, -1f, 1f, 1f }, // +Z
            new[] { -1f,-1f,-1f, -1f, 1f,-1f,  1f, 1f,-1f,  1f,-1f,-1f }, // -Z
            new[] {  1f,-1f,-1f,  1f, 1f,-1f,  1f, 1f, 1f,  1f,-1f, 1f }, // +X
            new[] { -1f,-1f,-1f, -1f,-1f, 1f, -1f, 1f, 1f, -1f, 1f,-1f }, // -X
            new[] { -1f, 1f,-1f, -1f, 1f, 1f,  1f, 1f, 1f,  1f, 1f,-1f }, // +Y
            new[] { -1f,-1f,-1f,  1f,-1f,-1f,  1f,-1f, 1f, -1f,-1f, 1f }  // -Y
        };
        var v = new List<float>();
        for (int f = 0; f < 6; f++)
        {
            var q = faces[f];
            var (r, g, b) = faceCol[f];
            int[] idx = { 0, 1, 2, 0, 2, 3 };
            foreach (int i in idx)
            {
                v.Add(q[i * 3]); v.Add(q[i * 3 + 1]); v.Add(q[i * 3 + 2]);
                v.Add(r); v.Add(g); v.Add(b);
            }
        }
        return v.ToArray();
    }

    private static float[] BuildGizmo() => new float[]
    {
        0,0,0, 0.90f,0.30f,0.30f,  1,0,0, 0.90f,0.30f,0.30f, // X 红
        0,0,0, 0.30f,0.85f,0.35f,  0,1,0, 0.30f,0.85f,0.35f, // Y 绿
        0,0,0, 0.35f,0.55f,0.95f,  0,0,1, 0.35f,0.55f,0.95f  // Z 蓝
    };
}
