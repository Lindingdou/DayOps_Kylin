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
    private GlRenderer.Mesh _imported;
    private bool _hasImported;
    private float[]? _pendingImport;
    private double[]? _pendingBounds;

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
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer.DeleteMesh(_grid);
        _renderer.DeleteMesh(_cube);
        _renderer.DeleteMesh(_gizmo);
        if (_hasImported) _renderer.DeleteMesh(_imported);
        _renderer.Deinit();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        double scale = VisualRoot?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scale));
        int h = Math.Max(1, (int)(Bounds.Height * scale));
        float aspect = h == 0 ? 1f : (float)w / h;

        // 消费待上传的导入几何（必须在 GL 线程 = 本回调内）
        var pending = _pendingImport;
        if (pending != null)
        {
            _pendingImport = null;
            if (_hasImported) _renderer.DeleteMesh(_imported);
            _imported = _renderer.Upload(pending);
            _hasImported = !_imported.IsEmpty;
            _camera.FitBounds(_pendingBounds);
        }

        float[] vp = _camera.ViewProj(aspect);

        _renderer.BeginFrame(w, h, 0.13f, 0.14f, 0.16f);
        GridPass(vp);
        ScenePass(vp);
        OverlayPass(w, h);
        _renderer.EndFrame();

        // 连续动画：请求下一帧
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    // ---------- 三趟管线（对应内核 RenderGridPass / RenderScenePass / RenderOverlayPass）----------

    /// <summary>地面自适应网格 + XYZ 轴。深度关 —— 作背景，永远在实体之后。</summary>
    private void GridPass(float[] vp)
    {
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
        if (_hasImported)
        {
            _renderer.Draw(_imported, GL_LINES, vp);
        }
        else
        {
            float angle = (float)_clock.Elapsed.TotalSeconds * 0.6f;
            float[] model = Mat4.Mul(Mat4.Translate(0f, 0f, 1.6f), Mat4.RotateZ(angle));
            _renderer.Draw(_cube, GL_TRIANGLES, Mat4.Mul(vp, model));
        }
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

    // ---------- 交互 API（供宿主 Panel 转发）----------
    // OpenGlControlBase 自身命中测试不可靠（无背景时部分后端收不到指针），
    // 交互统一由宿主 Panel（可命中）转发到相机。
    /// <summary>轨道旋转（增量已由宿主换算好）。</summary>
    public void Orbit(double dYaw, double dPitch) => _camera.Orbit(dYaw, dPitch);

    /// <summary>缩放。factor &lt;1 拉近，&gt;1 拉远。</summary>
    public void Zoom(double factor) => _camera.Zoom(factor);

    /// <summary>
    /// 显示导入的线框几何（世界坐标交错 P3_C3 线段）并范围缩放到其包围盒。
    /// UI 线程调用；实际 GL 上传延到下一帧渲染回调（GL 线程）执行。
    /// </summary>
    public void ShowImportedGeometry(float[] lineVertices, double[] bounds)
    {
        _pendingImport = lineVertices;
        _pendingBounds = bounds;
        RequestNextFrameRendering();
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
