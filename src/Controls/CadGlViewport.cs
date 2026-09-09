using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using PitMine3D.Kylin.Cad.Draw;
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
public partial class CadGlViewport : OpenGlControlBase
{
    private const int GL_LINES = 0x0001;   // GlConsts 未定义，本地补
    private const int GL_TRIANGLES = 0x0004;

    private readonly GlRenderer _renderer = new();
    private readonly Camera _camera = new();
    private GlExtras _ext = null!;
    private bool _isGles;

    // 静态网格：地面网格+轴 / 示例实体 / 罗盘
    private GlRenderer.Mesh _grid, _cube, _gizmo;
    private bool _hasGrid;                    // 自适应格网已上传(随缩放/平移重建)
    private GridPlan _gridPlan;
    private bool _hasGridPlan, _gridIs2D;
    private double _gridWpp;                  // 建网时的 世界长度/像素(挡位判定)

    // 公告板文字(始终朝屏幕, 忠实原版 screenFacing 注记): 按相机基向量重建, 相机没动就复用
    private GlRenderer.Mesh _billboards, _billboardFills;
    private bool _hasBillboards, _hasBillboardFills;
    private List<BillboardText>? _pendingBillboards;
    private bool _billboardsDirty;
    private double _bbYaw = double.NaN, _bbPitch, _bbTx, _bbTy, _bbTz;
    private bool _bb2D;

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

    // 托管场景着色三角面(三角网面模型, GL_TRIANGLES)
    private GlRenderer.Mesh _faces;
    private bool _hasFaces;
    private float[]? _pendingFaces;
    private bool _facesDirty;
    private double _sceneZc;          // 场景几何高程中心(供 ZE/FitBounds 把注视点放到模型高度)

    // 点云(GL_POINTS): 一份场景可有多份点云, 合成一条通道整体上传
    private GlRenderer.Mesh _cloud;
    private bool _hasCloud;
    private float[]? _pendingCloud;
    private bool _cloudDirty;
    private float _cloudPx = 2f;      // 点径(像素)
    private double[]? _cloudBounds;   // 点云 XY 包围盒(点云不进线段通道, ZE 得单独并进来)
    private const int GL_POINTS = 0x0000;   // Avalonia 的 GlConsts 没导出它

    // 选中高亮的着色面(选中三角网表面盖高亮色, GL_TRIANGLES)
    private GlRenderer.Mesh _highlightFaces;
    private bool _hasHighlightFaces;
    private float[]? _pendingHighlightFaces;
    private bool _highlightFacesDirty;

    // 图层显隐：图层名 → 该层几何；隐藏集
    private Dictionary<string, float[]>? _layerGeom;
    private readonly HashSet<string> _hiddenLayers = new();

    private double[]? _lastBounds;   // 最近导入的包围盒，供 ZE 重新范围缩放
    private double[]? _sceneBounds;  // 当前绘制场景几何的世界 XY 包围盒，供 ZE 框住手绘图元
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

    // 进行中的绘制/编辑预览（橡皮筋、拖拽跟随、滑动采样）——鼠标每动一次就换一遍。
    // 与静态场景分通道: 混在场景缓冲里的话, 每动一次鼠标就要把整篇场景重算包围盒并整体重传 GPU,
    // 图元一多光标就拖不动。预览就那么几条线, 复用同一 VBO 重灌即可。
    private GlRenderer.Mesh _preview;
    private bool _hasPreview;
    private float[]? _pendingPreview;
    private bool _previewDirty;

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
    /// <summary>帧时间采样(每秒一次)：(fps, 平均帧时 ms)。UI 线程回调。</summary>
    public event Action<double, double>? FrameStats;
    private int _frameCount;
    private long _statT0;

    /// <summary>GL 初始化失败(老驱动/无 GLX/无 GPU)：不再抛出把程序带崩, 记原因并停掉本视口的绘制。</summary>
    public bool GlFailed { get; private set; }   // 置位后本视口停绘, 程序继续
    public string GlFailReason { get; private set; } = "";

    private bool _firstFrameLogged;

    /// <summary>已请求但尚未绘出的帧(重绘去重, 见 SetCursorScreen)。</summary>
    private bool _renderQueued;

    /// <summary>PITMINE_NO_CURSOR=1: 不画十字光标(应急开关, 见 EnsureCursor 处说明)。</summary>
    private static readonly bool NoCursor =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PITMINE_NO_CURSOR"));

    /// <summary>PITMINE_NO_GL=1: 完全不初始化三维视口(降级最后一档 —— 图形反复崩时保住程序可用)。</summary>
    private static readonly bool NoGl =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PITMINE_NO_GL"));

    protected override void OnOpenGlInit(GlInterface gl)
    {
        if (NoGl)
        {
            GlFailed = true;
            GlFailReason = "已按 PITMINE_NO_GL 停用三维视口(图形驱动反复崩溃后的自动降级)";
            PitMine3D.Kylin.CrashLog.Write("GL", GlFailReason);
            return;
        }
        try { OnOpenGlInitCore(gl); }
        catch (Exception ex)
        {
            GlFailed = true;
            GlFailReason = ex.Message;
            PitMine3D.Kylin.CrashLog.Write("GL-FAIL", ex.ToString());
            // 不 rethrow —— 抛出会让 Avalonia 拆掉窗口, 表现为"打开即闪退"; 宁可无三维视图也让程序留得住, 便于看提示与日志。
            Dispatcher.UIThread.Post(() => GlReady?.Invoke($"OpenGL 初始化失败: {ex.Message}"));
        }
    }

    private void OnOpenGlInitCore(GlInterface gl)
    {
        PitMine3D.Kylin.CrashLog.Write("GL", $"上下文: {GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}");
        _ext = new GlExtras(gl);
        _isGles = GlVersion.Type == GlProfileType.OpenGLES;

        string backend = $"{(_isGles ? "OpenGL ES" : "OpenGL")} {GlVersion.Major}.{GlVersion.Minor}";
        Dispatcher.UIThread.Post(() => GlReady?.Invoke(backend));

        _renderer.Init(gl, _ext, _isGles, GlVersion.Major, GlVersion.Minor);
        // 驱动实况落盘: 原生崩溃(SIGSEGV)抓不到堆栈, 这几行是判断"崩在哪类驱动"的第一手材料
        PitMine3D.Kylin.CrashLog.Write("GL", $"渲染器就绪, 着色器方言={_renderer.ShaderProfile}, 扩展 {_ext!.Resolved}");
        try
        {
            string renderer = gl.GetString(0x1F01) ?? "", vendor = gl.GetString(0x1F00) ?? "";
            PitMine3D.Kylin.CrashLog.Write("GL", $"GL_VERSION={gl.GetString(0x1F02)} · GL_RENDERER={renderer} · GL_VENDOR={vendor}");
            PitMine3D.Kylin.CrashLog.Write("GL", $"GLSL={gl.GetString(0x8B8C)}");
            // 注: 曾把 Glenfly/Arise 列为"已知坏驱动"直接标记降级, 后经日志证实首帧能正常画出
            // (2005x955), 崩溃其实来自 fcitx 输入法那条 DBus 通路 —— 故撤掉黑名单, 不再误伤硬件渲染。
            // 真崩了仍有启动器的退出码阶梯兜底(崩一次降一档)。
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("GL", "取驱动字符串失败: " + ex.Message); }
        _hasGrid = false; _hasGridPlan = false;   // 上下文重建 → 格网下一帧按当前视图重建
        _cube = _renderer.Upload(BuildCube());
        _gizmo = _renderer.Upload(BuildGizmo());

        // GL 上下文(重)建后, 旧上下文里上传的网格句柄已失效——从保留的托管源重新入队, 下一帧重传。
        // 切换文档标签时 Avalonia 会 Deinit/Init 该视口(销毁并重建上下文), 有此重传各文档几何才不丢/不空白。
        if (_pendingImport is { Length: > 0 }) _importedDirty = true;
        if (_pendingScene != null) _sceneDirty = true;
        if (_pendingHighlight != null) _highlightDirty = true;
        if (_pendingFaces != null) _facesDirty = true;
        if (_pendingCloud != null) _cloudDirty = true;
        if (_pendingHighlightFaces != null) _highlightFacesDirty = true;
        if (_pendingSnap != null) _snapDirty = true;
        if (_pendingPreview != null) _previewDirty = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (GlFailed) return;

        if (_hasGrid) _renderer.DeleteMesh(_grid);
        _renderer.DeleteMesh(_cube);
        _renderer.DeleteMesh(_gizmo);
        if (_hasImported) _renderer.DeleteMesh(_imported);
        if (_hasHighlight) _renderer.DeleteMesh(_highlight);
        if (_hasSnap) _renderer.DeleteMesh(_snap);
        if (_hasPreview) _renderer.DeleteMesh(_preview);
        if (_hasScene) _renderer.DeleteMesh(_scene);
        if (_hasFaces) _renderer.DeleteMesh(_faces);
        if (_hasCloud) _renderer.DeleteMesh(_cloud);
        if (_hasHighlightFaces) _renderer.DeleteMesh(_highlightFaces);
        if (_hasBillboards) _renderer.DeleteMesh(_billboards);
        if (_hasBillboardFills) _renderer.DeleteMesh(_billboardFills);
        if (_hasCursor) _renderer.DeleteMesh(_cursor);
        _renderer.Deinit();
        // 上下文销毁——句柄失效, 复位标志; 否则重建后旧 id 可能撞上新网格(grid/cube/gizmo)致误删/花屏。
        // 保留的托管源(_pendingImport/_pendingScene/…)不动, OnOpenGlInit 会据此重新入队重传。
        _hasImported = _hasScene = _hasHighlight = _hasSnap = _hasFaces = _hasHighlightFaces = _hasPreview = _hasCloud = false;
        _hasGrid = false; _hasGridPlan = false;
        _hasBillboards = false; _hasBillboardFills = false; _bbYaw = double.NaN;
        if (_pendingFaces != null) _facesDirty = true;
        if (_pendingCloud != null) _cloudDirty = true;
        if (_pendingHighlightFaces != null) _highlightFacesDirty = true;
        _hasCursor = false; _curBuiltSx = double.NaN;   // 十字光标下次移动即按当前上下文重建
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        // 渲染回调里的托管异常此前会直接把进程带走。改为记下原因、停掉本视口绘制,
        // 窗口/面板/命令行都还在 —— 报错而不是崩溃。(原生段错误仍拦不住, 那是信号不是异常。)
        try { RenderCore(gl, fb); }
        catch (Exception ex)
        {
            GlFailed = true;
            GlFailReason = ex.Message;
            PitMine3D.Kylin.CrashLog.Write("GL", "渲染失败, 已停止本视口绘制(程序继续): " + ex);
        }
    }

    private void RenderCore(GlInterface gl, int fb)
    {
        _renderQueued = false;   // 本帧开画, 允许下一次移动再排一帧
        if (GlFailed) return;   // GL 不可用: 空转(窗口/面板/命令行仍可用)

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
            // 捕捉标记同十字光标: 鼠标一动就换一次, 复用同一 VBO 重灌, 不再每次删一个缓冲再建一个
            _snapDirty = false;
            _renderer.UpdateMesh(ref _snap, Localize(_pendingSnap!));
            _hasSnap = !_snap.IsEmpty;
        }

        if (_previewDirty)
        {
            // 同捕捉标记: 鼠标一动就换一次, 复用同一 VBO 重灌, 不删缓冲不重建
            _previewDirty = false;
            _renderer.UpdateMesh(ref _preview, Localize(_pendingPreview!));
            _hasPreview = !_preview.IsEmpty;
        }

        if (_facesDirty)
        {
            _facesDirty = false;
            if (_hasFaces) _renderer.DeleteMesh(_faces);
            _faces = _renderer.Upload(Localize(_pendingFaces!));
            _hasFaces = !_faces.IsEmpty;
        }

        if (_cloudDirty)
        {
            _cloudDirty = false;
            if (_hasCloud) _renderer.DeleteMesh(_cloud);
            _cloud = _renderer.Upload(Localize(_pendingCloud!));
            _hasCloud = !_cloud.IsEmpty;
        }

        if (_highlightFacesDirty)
        {
            _highlightFacesDirty = false;
            if (_hasHighlightFaces) _renderer.DeleteMesh(_highlightFaces);
            _highlightFaces = _renderer.Upload(Localize(_pendingHighlightFaces!));
            _hasHighlightFaces = !_highlightFaces.IsEmpty;
        }

        if (_sceneDirty)
        {
            _sceneDirty = false;
            if (_hasScene) _renderer.DeleteMesh(_scene);
            _scene = _renderer.Upload(Localize(_pendingScene!));
            _hasScene = !_scene.IsEmpty;
        }

        // CAD 十字光标：光标位/视口尺寸变了才重建满屏横竖两线(NDC, 屏幕对齐, 与几何无关不受相机影响)。
        // 它是「鼠标每动一次就删一个 GL 缓冲再传一个」的唯一路径 —— 驱动有问题时最先崩在这，
        // 故留 PITMINE_NO_CURSOR=1 应急开关: 关掉光标即可判断闪退是不是这条路引起的。
        if (!NoCursor && _showCursor && Bounds.Width > 0 && Bounds.Height > 0 &&
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
            // 复用同一个 VBO 重灌(顶点数恒定 12)，不再每次移动都删一个缓冲再建一个
            PitMine3D.Kylin.CrashLog.Trace("光标更新 前");
            _renderer.UpdateMesh(ref _cursor, verts);
            _hasCursor = !_cursor.IsEmpty;
            PitMine3D.Kylin.CrashLog.Trace("光标更新 后");
        }

        float[] vp = _camera.ViewProj(aspect);

        // 逐步断点(PITMINE_TRACE=1 才开)：原生崩溃没有堆栈, 靠最后一条 TRACE 定位崩在哪一趟
        var T = PitMine3D.Kylin.CrashLog.TraceOn;
        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 公告板");
        EnsureBillboards();   // 公告板文字: 相机转了才重建
        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 格网");
        EnsureGrid(aspect);   // 自适应格网: 缩放换挡/平移出界时重建(必须在 GL 线程)

        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 开始");
        _renderer.BeginFrame(w, h, 0.13f, 0.14f, 0.16f);
        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 格网趟");
        GridPass(vp);
        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 场景趟");
        ScenePass(vp);
        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 高亮趟");
        HighlightPass(vp);
        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 叠加趟");
        OverlayPass(w, h);
        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 光标趟");
        CursorPass();
        _renderer.EndFrame();
        if (T) PitMine3D.Kylin.CrashLog.Trace("帧 结束");

        // 首帧无条件记一条(不需要 PITMINE_TRACE): 日志停在 GL 初始化之后、这条之前,
        // 就说明连一帧都没画完就死了; 有这条则崩在后续交互。判断范围一下子缩一半。
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            PitMine3D.Kylin.CrashLog.Write("GL", $"首帧完成 {w}x{h}");
        }

        // 帧时间采样：每秒汇总一次 FPS / 平均帧时(ms) → 状态栏 Performance 项(同原版 FrameProfiler)
        _frameCount++;
        long now = _clock.ElapsedMilliseconds;
        if (now - _statT0 >= 1000)
        {
            double fps = _frameCount * 1000.0 / (now - _statT0);
            double ms = (now - _statT0) / (double)_frameCount;
            _frameCount = 0; _statT0 = now;
            if (FrameStats != null) Dispatcher.UIThread.Post(() => FrameStats?.Invoke(fps, ms), DispatcherPriority.Background);
        }

        // 连续动画：请求下一帧
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    // ---------- 三趟管线（对应内核 RenderGridPass / RenderScenePass / RenderOverlayPass）----------

    /// <summary>地面自适应网格 + XYZ 轴。深度关 —— 作背景，永远在实体之后。</summary>
    private void GridPass(float[] vp)
    {
        if (!_showGrid || !_hasGrid) return;
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
        if (_hasFaces) { _renderer.SetPolygonOffset(true); _renderer.Draw(_faces, GL_TRIANGLES, vp); _renderer.SetPolygonOffset(false); }   // 着色面先画, 深度偏移让边线浮在面上
        if (_hasCloud)
        {
            _renderer.SetPointSize(_cloudPx);
            _renderer.SetDepthLessEqual(true);    // 同深度后画的赢: 算子产物(与源点重合)浮在源点云之上
            _renderer.Draw(_cloud, GL_POINTS, vp);
            _renderer.SetDepthLessEqual(false);
        }
        if (_hasImported) _renderer.Draw(_imported, GL_LINES, vp);
        if (_hasScene) _renderer.Draw(_scene, GL_LINES, vp);
        if (_hasPreview) _renderer.Draw(_preview, GL_LINES, vp);   // 进行中的橡皮筋/拖拽预览, 紧跟场景之后(与合缓冲时同序)
        // 注记始终朝屏幕: 实心字形先画三角(忠实原版 fillTris), 缺字回退的简笔画再画线
        if (_hasBillboardFills) _renderer.Draw(_billboardFills, GL_TRIANGLES, vp);
        if (_hasBillboards) _renderer.Draw(_billboards, GL_LINES, vp);
        _renderer.EndPass();
    }

    /// <summary>选择高亮：先在选中三角网表面盖一层高亮色面(深度开 + 负偏移压住原面)，再把高亮线画在最上层(深度关)。</summary>
    private void HighlightPass(float[] vp)
    {
        if (_hasHighlightFaces)
        {
            _renderer.BeginPass(depthTest: true);
            _renderer.SetPolygonOffset(true, -1.5f, -1.5f);   // 负偏移: 高亮面压在原面之前(否则 z-fighting 闪烁)
            _renderer.Draw(_highlightFaces, GL_TRIANGLES, vp);
            _renderer.SetPolygonOffset(false);
            _renderer.EndPass();
        }
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
        // X11 一秒能派上百个移动事件, 逐个触发整帧重绘在软件渲染(麒麟常是 Mesa llvmpipe)上就是肉眼可见的卡顿。
        // 十字光标按整像素画, 位置没跨过一个像素就没有可见变化 —— 直接不重绘。
        bool moved = (int)sx != (int)_cursorSx || (int)sy != (int)_cursorSy;
        _cursorSx = sx; _cursorSy = sy;
        bool wasHidden = !_showCursor;
        _showCursor = true;
        if (NoCursor) return;                       // 关了十字光标就没有重绘理由
        if (!moved && !wasHidden) return;
        // 已经有一帧在排队就别再排：软件渲染下一帧要几十毫秒, 而 X11 一秒派上百个移动事件,
        // 逐个排队会让输入越积越多 —— 表现就是"鼠标拖泥带水"。丢掉多余请求即可, 光标
        // 本来就只需跟上实际帧率。
        if (_renderQueued) return;
        _renderQueued = true;
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
        _sceneBounds = ComputeXYBounds(_pendingScene);   // 手绘/编辑后更新 ZE 目标包围盒
        _sceneZc = ComputeZCenter(_pendingScene);
        _sceneDirty = true;
        RequestNextFrameRendering();
    }

    // 从 P3_C3 顶点缓冲(stride 6, XY 在 0/1，世界坐标)算 XY 包围盒；空缓冲返回 null。
    private static double[]? ComputeXYBounds(float[] v)
    {
        if (v == null || v.Length < 6) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i + 5 < v.Length; i += 6)
        {
            double x = v[i], y = v[i + 1];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        return new[] { minX, minY, maxX, maxY };
    }

    // 两包围盒求并；任一为 null 返回另一个。
    private static double[]? UnionBounds(double[]? a, double[]? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return new[] { Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Max(a[2], b[2]), Math.Max(a[3], b[3]) };
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
        // 框住 导入几何 ∪ 手绘场景几何；无任何几何才不动。(LocalizeBounds 按当前原点态一致换算)
        var b = UnionBounds(UnionBounds(_lastBounds, _sceneBounds), _cloudBounds);
        if (b == null) return;
        var lb = LocalizeBounds(b)!; _camera.FitBounds(lb[0], lb[1], lb[2], lb[3], _sceneZc);
        RequestNextFrameRendering();
    }

    /// <summary>框住指定包围盒 [minX,minY,maxX,maxY]（供绘制/点导入等非导入几何缩放），并记为 ZE 目标。</summary>
    public void FitBounds(double[] bounds)
    {
        if (bounds == null || bounds.Length < 4) return;
        _lastBounds = bounds;
        EnsureOrigin(bounds);
        var lb2 = LocalizeBounds(bounds)!; _camera.FitBounds(lb2[0], lb2[1], lb2[2], lb2[3], _sceneZc);
        RequestNextFrameRendering();
    }

    /// <summary>切换地面网格 / 轴显隐（GRID）。</summary>
    public void ToggleGrid()
    {
        _showGrid = !_showGrid;
        RequestNextFrameRendering();
    }

    /// <summary>高亮一组几何（P3_C3 位置，默认重着色为高亮色；recolor=false 保留自带颜色，供夹点冷/热/选中配色）；null/空 → 清除高亮。</summary>
    public void SetHighlight(float[]? geom, bool recolor = true)
    {
        _pendingHighlight = (geom == null || geom.Length == 0) ? Array.Empty<float>() : (recolor ? Recolor(geom, 1f, 0.9f, 0.2f) : geom);
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

    /// <summary>
    /// 设置进行中的绘制/编辑预览几何（P3_C3，已含颜色）；null/空 → 清除。
    /// 独立于场景几何: 预览随光标每帧变，场景不重传；ZE 的包围盒也因此只按落地的图元算(橡皮筋不再撑大范围)。
    /// </summary>
    public void SetPreviewGeometry(float[]? verts)
    {
        _pendingPreview = (verts == null || verts.Length == 0) ? Array.Empty<float>() : verts;
        _previewDirty = true;
        RequestNextFrameRendering();
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

    /// <summary>世界点 → 屏幕像素(相机后方 null)。</summary>
    public (double sx, double sy)? WorldToScreen(double x, double y, double z)
        => _camera.WorldToScreen(x - _ox, y - _oy, z, Bounds.Width, Bounds.Height);

    /// <summary>当前相机的世界→屏幕投影函数(矩阵只算一次, 供 3D 框选逐顶点投影)。</summary>
    public Func<double, double, double, (double sx, double sy)?> WorldToScreenProjector()
    {
        var f = _camera.MakeProjector(Bounds.Width, Bounds.Height);
        double ox = _ox, oy = _oy;
        return (x, y, z) => f(x - ox, y - oy, z);
    }

    /// <summary>带深度的投影函数(3D 点选面命中取最前者)。</summary>
    public Func<double, double, double, (double sx, double sy, double depth)?> WorldToScreenDepthProjector()
    {
        var f = _camera.MakeProjectorDepth(Bounds.Width, Bounds.Height);
        double ox = _ox, oy = _oy;
        return (x, y, z) => f(x - ox, y - oy, z);
    }

    /// <summary>屏幕点 → 视平面(过注视点、垂直视线)上的世界三维点；2D 时为 Z=0 平面点。供 3D 选框/拾取落点。</summary>
    public (double x, double y, double z)? ScreenToViewPlane(double sx, double sy)
    {
        var p = _camera.ScreenToViewPlane(sx, sy, Bounds.Width, Bounds.Height);
        return p == null ? null : (p.Value.x + _ox, p.Value.y + _oy, p.Value.z);
    }

    // ---------- 几何（示例内容；接入内核后由 AcDb worldDraw 提供）----------
    /// <summary>
    /// 按当前可见范围重建自适应格网(AutoCAD 式无限栅格)：随缩放 1-2-5 换挡、每 5 格一条主线、
    /// 主线对齐世界原点、铺满视口看不到边界；X/Y 轴单独着色。间距或可见区超出已覆盖范围时才重建。
    /// 几何用渲染局部坐标(世界 − 原点), 与其它几何同一坐标系。
    /// </summary>
    private void EnsureGrid(float aspect)
    {
        if (!_showGrid || Bounds.Height < 1) return;
        bool is2D = _camera.Is2D;
        // 2D 正交: 半高 = Dist; 3D: 取注视平面上的可见范围(与平移/缩放同口径)
        double halfH = is2D ? _camera.Dist : _camera.Dist * Math.Tan(Math.PI / 8);
        double viewH = halfH * 2, viewW = viewH * aspect;
        double wpp = viewH / Bounds.Height;                    // 世界长度/像素
        double cxL = _camera.Target[0], cyL = _camera.Target[1];   // 局部坐标中心
        double cx = cxL + _ox, cy = cyL + _oy;                 // 世界坐标中心(主线要对齐世界原点)
        double coverage = is2D ? 1.15 : 3.0;                   // 3D 多铺一些, 边界落到视野外

        if (_hasGridPlan && Math.Abs(_gridWpp - wpp) < _gridWpp * 0.15 && _gridIs2D == is2D
            && GridPlanner.Covers(_gridPlan, cx, cy, viewW, viewH))
            return;                                            // 缩放挡位未变且仍罩得住 → 复用

        var plan = GridPlanner.For(wpp, viewW, viewH, cx, cy, minPx: 10, majorEvery: 5, coverage: coverage);
        _gridPlan = plan; _gridWpp = wpp; _gridIs2D = is2D; _hasGridPlan = true;

        var v = new List<float>((plan.TotalLines + 4) * 12);
        void Line(double x0, double y0, double x1, double y1, float r, float g, float b)
        {
            v.Add((float)(x0 - _ox)); v.Add((float)(y0 - _oy)); v.Add(0); v.Add(r); v.Add(g); v.Add(b);
            v.Add((float)(x1 - _ox)); v.Add((float)(y1 - _oy)); v.Add(0); v.Add(r); v.Add(g); v.Add(b);
        }
        // 细线 / 主线(主线更亮)；轴线单独画在最后, 盖住同位置的格线
        const float mnR = 0.185f, mnG = 0.200f, mnB = 0.225f;   // 细线: 比背景(0.13,0.14,0.16)略亮
        const float mjR = 0.300f, mjG = 0.325f, mjB = 0.360f;   // 主线
        for (int i = 0; i < plan.VerticalLines; i++)
        {
            double x = plan.X0 + i * plan.Minor;
            bool major = GridPlanner.IsMajor(x, plan.Major);
            Line(x, plan.Y0, x, plan.Y1, major ? mjR : mnR, major ? mjG : mnG, major ? mjB : mnB);
        }
        for (int j = 0; j < plan.HorizontalLines; j++)
        {
            double y = plan.Y0 + j * plan.Minor;
            bool major = GridPlanner.IsMajor(y, plan.Major);
            Line(plan.X0, y, plan.X1, y, major ? mjR : mnR, major ? mjG : mnG, major ? mjB : mnB);
        }
        // 世界坐标轴(在视野内才画)：X 红 / Y 绿
        if (plan.Y0 <= 0 && plan.Y1 >= 0) Line(plan.X0, 0, plan.X1, 0, 0.62f, 0.26f, 0.26f);
        if (plan.X0 <= 0 && plan.X1 >= 0) Line(0, plan.Y0, 0, plan.Y1, 0.26f, 0.55f, 0.30f);

        if (_hasGrid) _renderer.DeleteMesh(_grid);
        _grid = _renderer.Upload(v.ToArray());
        _hasGrid = !_grid.IsEmpty;
    }

    /// <summary>
    /// 公告板文字：把每条注记的笔画放到「相机右向量 × 上向量」张成的平面上, 锚点为世界点 ——
    /// 忠实原版 PmbiWriter.WriteText(screenFacing: true)：三维里注记不随模型倾倒, 永远正对观察者。
    /// 相机朝向/注视点没变就复用已上传的几何(旋转/缩放时才重建)。
    /// </summary>
    private void EnsureBillboards()
    {
        var src = _pendingBillboards;
        if (src == null || src.Count == 0)
        {
            if (_billboardsDirty)
            {
                if (_hasBillboards) { _renderer.DeleteMesh(_billboards); _hasBillboards = false; }
                if (_hasBillboardFills) { _renderer.DeleteMesh(_billboardFills); _hasBillboardFills = false; }
            }
            _billboardsDirty = false;
            return;
        }
        // 2D 正交俯视: 屏幕 X/Y 就是世界 X/Y(ViewAxes 按球坐标算, 2D 下会把字斜过来甚至镜像); 3D 才随相机转
        var (rx, ry, rz, ux, uy, uz) = _camera.Is2D ? (1.0, 0.0, 0.0, 0.0, 1.0, 0.0) : _camera.ViewAxes();
        bool camMoved = double.IsNaN(_bbYaw) || Math.Abs(_camera.Yaw - _bbYaw) > 1e-6 || Math.Abs(_camera.Pitch - _bbPitch) > 1e-6
                        || _bbTx != _camera.Target[0] || _bbTy != _camera.Target[1] || _bbTz != _camera.Target[2]
                        || _bb2D != _camera.Is2D;
        if (!_billboardsDirty && !camMoved) return;
        _billboardsDirty = false;
        _bbYaw = _camera.Yaw; _bbPitch = _camera.Pitch;
        _bbTx = _camera.Target[0]; _bbTy = _camera.Target[1]; _bbTz = _camera.Target[2]; _bb2D = _camera.Is2D;

        var v = new List<float>();
        var f = new List<float>();
        foreach (var t in src)
        {
            double ax = t.X - _ox, ay = t.Y - _oy, az = t.Z;
            if (t.Fills != null)
                foreach (var (x0, y0, x1, y1, x2, y2) in t.Fills)
                {
                    Pt(f, ax, ay, az, x0, y0, t); Pt(f, ax, ay, az, x1, y1, t); Pt(f, ax, ay, az, x2, y2, t);
                }
            foreach (var (x0, y0, x1, y1) in t.Strokes)
            {
                // 局部 (x,y) → 世界: 锚点 + x·右 + y·上
                v.Add((float)(ax + x0 * rx + y0 * ux)); v.Add((float)(ay + x0 * ry + y0 * uy)); v.Add((float)(az + x0 * rz + y0 * uz));
                v.Add(t.Cr); v.Add(t.Cg); v.Add(t.Cb);
                v.Add((float)(ax + x1 * rx + y1 * ux)); v.Add((float)(ay + x1 * ry + y1 * uy)); v.Add((float)(az + x1 * rz + y1 * uz));
                v.Add(t.Cr); v.Add(t.Cg); v.Add(t.Cb);
            }
        }
        if (_hasBillboards) _renderer.DeleteMesh(_billboards);
        _billboards = _renderer.Upload(v.ToArray());
        _hasBillboards = !_billboards.IsEmpty;
        if (_hasBillboardFills) _renderer.DeleteMesh(_billboardFills);
        _billboardFills = _renderer.Upload(f.ToArray());
        _hasBillboardFills = !_billboardFills.IsEmpty;

        // 局部 (x,y) → 世界: 锚点 + x·右 + y·上
        void Pt(List<float> o, double ax, double ay, double az, double x, double y, BillboardText t)
        {
            o.Add((float)(ax + x * rx + y * ux)); o.Add((float)(ay + x * ry + y * uy)); o.Add((float)(az + x * rz + y * uz));
            o.Add(t.Cr); o.Add(t.Cg); o.Add(t.Cb);
        }
    }

    /// <summary>设置公告板文字(始终朝屏幕的注记)；空/null → 清除。</summary>
    public void SetBillboards(List<BillboardText>? texts)
    {
        _pendingBillboards = texts;
        _billboardsDirty = true;
        _bbYaw = double.NaN;
        RequestNextFrameRendering();
    }

    /// <summary>当前格网挡位说明(状态栏/自检用)：细线间距 · 主线间距 · 线数。</summary>
    public string GridInfo => _hasGridPlan
        ? $"minor={_gridPlan.Minor:0.###} major={_gridPlan.Major:0.###} lines={_gridPlan.TotalLines} cover=({_gridPlan.X0:0}..{_gridPlan.X1:0})"
        : "(未建)";

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

public partial class CadGlViewport
{
    /// <summary>设置选中高亮的着色面（P3_C3, GL_TRIANGLES）；空/null → 清除。</summary>
    public void SetHighlightFaces(float[]? tris)
    {
        _pendingHighlightFaces = (tris == null || tris.Length == 0) ? Array.Empty<float>() : tris;
        _highlightFacesDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>设置托管场景的着色三角面（P3_C3, GL_TRIANGLES）；空 → 清除。</summary>
    public void SetSceneFaces(float[] tris)
    {
        _pendingFaces = tris ?? Array.Empty<float>();
        _facesDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// 设置点云顶点(交错 P3_C3, GL_POINTS)与点径(像素)。空数组 = 场景无点云。
    /// 点云单独一条通道: 它既不是线也不是面, 且一份点云的顶点数常是其余图元的百倍,
    /// 混进线段缓冲会让每次重画都重传整份点云。
    /// </summary>
    public void SetSceneCloud(float[] verts, float pointPixels = 2f)
    {
        _pendingCloud = verts ?? Array.Empty<float>();
        _cloudBounds = ComputeXYBounds(_pendingCloud);
        // 场景里只有点云时, 注视点高程得取点云的 —— 否则相机盯着 z=0, 而矿区点云在 +1200m,
        // 三维视图下"加载完什么都看不见"。有其它几何时以它们为准(RefreshScene 先设线段几何再设点云)。
        if (_pendingScene == null || _pendingScene.Length == 0) _sceneZc = ComputeZCenter(_pendingCloud);
        _cloudPx = pointPixels > 0 ? pointPixels : 2f;
        _cloudDirty = true;
        RequestNextFrameRendering();
    }

    // 顶点缓冲 Z 的中值近似(极值中点)，供注视点落到模型高度；空缓冲为 0。
    private static double ComputeZCenter(float[] v)
    {
        if (v == null || v.Length < 6) return 0;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        for (int i = 2; i < v.Length; i += 6) { double z = v[i]; if (z < minZ) minZ = z; if (z > maxZ) maxZ = z; }
        return minZ == double.MaxValue ? 0 : (minZ + maxZ) / 2;
    }
}

public partial class CadGlViewport
{
    /// <summary>相机/包围盒调试串(自检用)。</summary>
    public string CameraDebug()
    {
        var s = _camera.Snapshot();
        string B(double[]? b) => b == null ? "null" : $"[{b[0]:0.#},{b[1]:0.#},{b[2]:0.#},{b[3]:0.#}]";
        return $"yaw={s.Yaw:0.##} pitch={s.Pitch:0.##} dist={s.Dist:0.#} target=({s.Tx:0.#},{s.Ty:0.#},{s.Tz:0.#}) 2D={s.Is2D} origin=({_ox:0.#},{_oy:0.#},{_originSet}) scene={B(_sceneBounds)} last={B(_lastBounds)} zc={_sceneZc:0.#} bounds={Bounds.Width:0}x{Bounds.Height:0}";
    }
}
