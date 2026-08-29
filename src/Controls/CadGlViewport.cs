using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using static Avalonia.OpenGL.GlConsts;

namespace PitMine3D.Kylin.Controls;

/// <summary>
/// 最小 OpenGL 视口 —— 对应内核 xllAcGi 从 D3D11 迁到 OpenGL 的落点。
/// 渲染：地面网格 + XYZ 轴 + 一个缓慢自转的立方体（代表实体），鼠标可轨道 / 滚轮缩放。
/// 着色器同时兼容 GLES 3.00（麒麟国产 GPU / ANGLE）与桌面 GL 3.30。
/// </summary>
public class CadGlViewport : OpenGlControlBase
{
    private const int GL_LINES = 0x0001;   // GlConsts 未定义，本地补

    // ---- GL 对象 ----
    private int _program;
    private int _gridVbo, _gridCount;
    private int _cubeVbo, _cubeCount;
    private int _vao;
    private int _uMvp;
    private GlExtras _ext = null!;
    private bool _isGles;

    // ---- 相机（Z 轴向上的 CAD 约定）----
    private double _yaw = 0.9;      // 绕 Z
    private double _pitch = 0.55;   // 抬头
    private double _dist = 12.0;
    private readonly float[] _target = { 0f, 0f, 1f };

    private Point _lastPointer;
    private bool _dragging;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>OpenGL 上下文就绪后回报后端版本串给界面。</summary>
    public event Action<string>? GlReady;

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            OnOpenGlInitCore(gl);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[GLINIT-FAIL] " + ex);
            throw;
        }
    }

    private unsafe void OnOpenGlInitCore(GlInterface gl)
    {
        Console.Error.WriteLine($"[GLINIT] called. type={GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}");
        _ext = new GlExtras(gl);
        _isGles = GlVersion.Type == GlProfileType.OpenGLES;

        string backend = $"{(_isGles ? "OpenGL ES" : "OpenGL")} {GlVersion.Major}.{GlVersion.Minor}";
        Dispatcher.UIThread.Post(() => GlReady?.Invoke(backend));

        string header = _isGles
            ? "#version 300 es\nprecision highp float;\n"
            : "#version 330 core\n";

        string vs = header + @"
in vec3 aPos;
in vec3 aColor;
uniform mat4 uMVP;
out vec3 vColor;
void main() {
    vColor = aColor;
    gl_Position = uMVP * vec4(aPos, 1.0);
}";

        string fs = header + @"
in vec3 vColor;
out vec4 oColor;
void main() {
    oColor = vec4(vColor, 1.0);
}";

        int vsh = gl.CreateShader(GL_VERTEX_SHADER);
        string vlog = gl.CompileShaderAndGetError(vsh, vs);
        if (!string.IsNullOrEmpty(vlog)) Console.WriteLine("[VS] " + vlog);

        int fsh = gl.CreateShader(GL_FRAGMENT_SHADER);
        string flog = gl.CompileShaderAndGetError(fsh, fs);
        if (!string.IsNullOrEmpty(flog)) Console.WriteLine("[FS] " + flog);

        _program = gl.CreateProgram();
        gl.AttachShader(_program, vsh);
        gl.AttachShader(_program, fsh);
        gl.BindAttribLocationString(_program, 0, "aPos");
        gl.BindAttribLocationString(_program, 1, "aColor");
        string plog = gl.LinkProgramAndGetError(_program);
        if (!string.IsNullOrEmpty(plog)) Console.WriteLine("[LINK] " + plog);

        _uMvp = gl.GetUniformLocationString(_program, "uMVP");

        // 一个 VAO 复用（每次 draw 前重设 attrib 指针到对应 VBO）
        _vao = _ext.GenVertexArray();
        _ext.BindVertexArray(_vao);

        float[] grid = BuildGrid(10, 1f);
        _gridCount = grid.Length / 6;
        _gridVbo = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _gridVbo);
        fixed (float* p = grid)
            gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(grid.Length * sizeof(float)), new IntPtr(p), GL_STATIC_DRAW);

        float[] cube = BuildCube();
        _cubeCount = cube.Length / 6;
        _cubeVbo = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _cubeVbo);
        fixed (float* p = cube)
            gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(cube.Length * sizeof(float)), new IntPtr(p), GL_STATIC_DRAW);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_program != 0) gl.DeleteProgram(_program);
        gl.DeleteBuffer(_gridVbo);
        gl.DeleteBuffer(_cubeVbo);
        _ext.DeleteVertexArray(_vao);
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        double scale = VisualRoot?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scale));
        int h = Math.Max(1, (int)(Bounds.Height * scale));
        gl.Viewport(0, 0, w, h);

        gl.ClearColor(0.13f, 0.14f, 0.16f, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        gl.Enable(GL_DEPTH_TEST);

        gl.UseProgram(_program);
        _ext.BindVertexArray(_vao);

        float aspect = h == 0 ? 1f : (float)w / h;
        float[] proj = Mat4.Perspective(MathF.PI / 4f, aspect, 0.1f, 200f);

        // 相机位置（球坐标，Z 上）
        float cy = MathF.Cos((float)_pitch);
        float[] eye =
        {
            _target[0] + (float)(_dist * cy * Math.Cos(_yaw)),
            _target[1] + (float)(_dist * cy * Math.Sin(_yaw)),
            _target[2] + (float)(_dist * Math.Sin(_pitch))
        };
        float[] view = Mat4.LookAt(eye, _target, new[] { 0f, 0f, 1f });
        float[] vp = Mat4.Mul(proj, view);

        // 网格 + 轴：model = 单位
        DrawBuffer(gl, _gridVbo, vp, GL_LINES, _gridCount);

        // 立方体：缓慢自转 + 抬到网格上方
        float angle = (float)_clock.Elapsed.TotalSeconds * 0.6f;
        float[] model = Mat4.Mul(Mat4.Translate(0f, 0f, 1.6f), Mat4.RotateZ(angle));
        float[] mvp = Mat4.Mul(vp, model);
        DrawBuffer(gl, _cubeVbo, mvp, GL_TRIANGLES, _cubeCount);

        // 连续动画
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    private void DrawBuffer(GlInterface gl, int vbo, float[] mvp, int mode, int count)
    {
        gl.BindBuffer(GL_ARRAY_BUFFER, vbo);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 6 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, 6 * sizeof(float), new IntPtr(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        _ext.UniformMatrix4fv(_uMvp, 1, 0, mvp);
        gl.DrawArrays(mode, 0, count);
    }

    // ---------- 交互 ----------
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _yaw -= (p.X - _lastPointer.X) * 0.01;
        _pitch += (p.Y - _lastPointer.Y) * 0.01;
        _pitch = Math.Clamp(_pitch, -1.4, 1.4);
        _lastPointer = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _dist *= e.Delta.Y > 0 ? 0.9 : 1.1;
        _dist = Math.Clamp(_dist, 2.0, 80.0);
    }

    // ---------- 几何 ----------
    private static float[] BuildGrid(int n, float step)
    {
        var v = new System.Collections.Generic.List<float>();
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
        var v = new System.Collections.Generic.List<float>();
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
}
