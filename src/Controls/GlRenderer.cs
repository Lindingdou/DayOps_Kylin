using System;
using System.Collections.Generic;
using Avalonia.OpenGL;
using static Avalonia.OpenGL.GlConsts;

namespace PitMine3D.Kylin.Controls;

/// <summary>
/// 最小 OpenGL 渲染器 —— 对应内核 xllAcGi 的 Renderer。
/// 职责：着色器程序（P3_C3：位置+颜色）、顶点缓冲上传、每趟 pass 的 GL 状态（深度）。
/// 提供 BeginFrame/EndFrame + BeginPass/EndPass + Upload/Draw，供 Viewport 的三趟管线调用。
/// 着色器同时兼容 GLES 3.00（麒麟国产 GPU / ANGLE）与桌面 GL 3.30 —— 同一份代码。
/// </summary>
internal sealed class GlRenderer
{
    /// <summary>一块静态顶点缓冲（P3_C3）。对应内核 EntityGpuCache 的 IMMUTABLE VB 落点。</summary>
    public readonly struct Mesh
    {
        public readonly int Vbo;
        public readonly int Count;
        public Mesh(int vbo, int count) { Vbo = vbo; Count = count; }
        public bool IsEmpty => Count == 0;
    }

    private GlInterface _gl = null!;
    private GlExtras _ext = null!;
    private int _program;
    private int _uMvp;
    private int _uPointSize;
    private int _vao;
    private bool _programPointSize;   // 桌面 GL 核心档需显式开 GL_PROGRAM_POINT_SIZE, 顶点着色器写的 gl_PointSize 才生效

    /// <summary>着色器实际采用的方言(诊断用)：330 core / 300 es / 110 兼容(老驱动回退)。</summary>
    public string ShaderProfile { get; private set; } = "(未初始化)";

    /// <summary>
    /// 按实际协商到的 GL 版本挑着色器方言并编译(逐个尝试, 第一个成功的为准)：
    ///   桌面 ≥3.3 → 330 core · 3.0~3.2 → 130 · 2.x → 110 兼容(attribute/varying/gl_FragColor)
    ///   GLES ≥3.0 → 300 es · 2.0 → ESSL 100 兼容
    /// 信创整机的国产 GPU 驱动常只到 GL 2.1 / GLES 2.0, 兼容方言那条就是为它们留的。
    /// </summary>
    public void Init(GlInterface gl, GlExtras ext, bool isGles, int major = 3, int minor = 3)
    {
        _gl = gl;
        _ext = ext;

        var attempts = new List<(string header, bool modern, string name)>();
        if (isGles)
        {
            if (major >= 3) attempts.Add(("#version 300 es\nprecision highp float;\n", true, "GLES 300"));
            attempts.Add(("precision highp float;\n", false, "GLES 100 兼容"));
        }
        else
        {
            if (major > 3 || (major == 3 && minor >= 3)) attempts.Add(("#version 330 core\n", true, "GLSL 330"));
            if (major >= 3) attempts.Add(("#version 130\n", true, "GLSL 130"));
            attempts.Add(("#version 110\n", false, "GLSL 110 兼容"));
        }

        var errs = new List<string>();
        foreach (var (header, modern, name) in attempts)
        {
            if (TryBuildProgram(gl, header, modern, out string err)) { ShaderProfile = name; break; }
            errs.Add($"{name}: {err}");
        }
        if (_program == 0)
            throw new InvalidOperationException("着色器全部方言均编译失败 —— " + string.Join(" | ", errs));
        if (errs.Count > 0) Console.Error.WriteLine($"[GL] 回退到 {ShaderProfile}(前面失败: {string.Join(" | ", errs)})");

        _uMvp = gl.GetUniformLocationString(_program, "uMVP");
        _uPointSize = gl.GetUniformLocationString(_program, "uPointSize");
        _programPointSize = !isGles;   // GLES 恒按 gl_PointSize 走; 桌面档要 Enable 才认

        _vao = _ext.GenVertexArray();   // 老驱动无 VAO 扩展时返回 0, BindVertexArray 变空操作(每次 Draw 都重设属性指针, 不依赖 VAO)
        _ext.BindVertexArray(_vao);
    }

    private bool TryBuildProgram(GlInterface gl, string header, bool modern, out string error)
    {
        // gl_PointSize: 点云走 GL_POINTS, 点径必须由顶点着色器给(GLES 无 glPointSize; 桌面核心档也只认这个)。
        // 画线/画面时它被忽略, 故同一份着色器通吃 线/面/点 三种图元。
        string vs = modern
            ? header + "in vec3 aPos;\nin vec3 aColor;\nuniform mat4 uMVP;\nuniform float uPointSize;\nout vec3 vColor;\n" +
                       "void main() { vColor = aColor; gl_PointSize = uPointSize; gl_Position = uMVP * vec4(aPos, 1.0); }"
            : header + "attribute vec3 aPos;\nattribute vec3 aColor;\nuniform mat4 uMVP;\nuniform float uPointSize;\nvarying vec3 vColor;\n" +
                       "void main() { vColor = aColor; gl_PointSize = uPointSize; gl_Position = uMVP * vec4(aPos, 1.0); }";
        string fs = modern
            ? header + "in vec3 vColor;\nout vec4 oColor;\nvoid main() { oColor = vec4(vColor, 1.0); }"
            : header + "varying vec3 vColor;\nvoid main() { gl_FragColor = vec4(vColor, 1.0); }";

        error = "";
        int vsh = gl.CreateShader(GL_VERTEX_SHADER);
        string vlog = gl.CompileShaderAndGetError(vsh, vs);
        if (!string.IsNullOrEmpty(vlog)) { error = "VS: " + vlog.Trim(); return false; }

        int fsh = gl.CreateShader(GL_FRAGMENT_SHADER);
        string flog = gl.CompileShaderAndGetError(fsh, fs);
        if (!string.IsNullOrEmpty(flog)) { error = "FS: " + flog.Trim(); return false; }

        int prog = gl.CreateProgram();
        gl.AttachShader(prog, vsh);
        gl.AttachShader(prog, fsh);
        gl.BindAttribLocationString(prog, 0, "aPos");
        gl.BindAttribLocationString(prog, 1, "aColor");
        string plog = gl.LinkProgramAndGetError(prog);
        if (!string.IsNullOrEmpty(plog)) { error = "LINK: " + plog.Trim(); gl.DeleteProgram(prog); return false; }

        if (_program != 0) _gl.DeleteProgram(_program);
        _program = prog;
        return true;
    }

    public void Deinit()
    {
        if (_program != 0) _gl.DeleteProgram(_program);
        _ext.DeleteVertexArray(_vao);
    }

    /// <summary>上传一块静态网格（交错 P3_C3，6 float/顶点）。</summary>
    public unsafe Mesh Upload(float[] p3c3)
    {
        int vbo = _gl.GenBuffer();
        _gl.BindBuffer(GL_ARRAY_BUFFER, vbo);
        fixed (float* p = p3c3)
            _gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(p3c3.Length * sizeof(float)), new IntPtr(p), GL_STATIC_DRAW);
        return new Mesh(vbo, p3c3.Length / 6);
    }

    public void DeleteMesh(in Mesh m)
    {
        if (m.Vbo != 0) _gl.DeleteBuffer(m.Vbo);
    }

    /// <summary>
    /// 就地更新已上传的网格：复用同一个 VBO 重灌数据，不再「删一个再建一个」。
    /// 十字光标每次鼠标移动都要更新，按原来的删+建等于每动一下就申请/释放一次 GL 缓冲 ——
    /// 既浪费，某些国产 GPU 驱动上还会在缓冲频繁增删时原生崩溃(段错误, 托管侧抓不到)。
    /// </summary>
    public unsafe void UpdateMesh(ref Mesh m, float[] p3c3)
    {
        if (m.Vbo == 0) { m = Upload(p3c3); return; }
        if (p3c3.Length == 0) { m = new Mesh(m.Vbo, 0); return; }   // 清空: 留着缓冲下次再用
        _gl.BindBuffer(GL_ARRAY_BUFFER, m.Vbo);
        fixed (float* p = p3c3)
            _gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(p3c3.Length * sizeof(float)), new IntPtr(p), GL_STATIC_DRAW);
        m = new Mesh(m.Vbo, p3c3.Length / 6);
    }

    // ---- 帧 / 趟（对应 Renderer::BeginFrame/EndFrame/BeginPass/EndPass）----

    public void BeginFrame(int w, int h, float r, float g, float b)
    {
        _gl.Viewport(0, 0, w, h);
        _gl.ClearColor(r, g, b, 1f);
        _gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        _gl.UseProgram(_program);
        _ext.BindVertexArray(_vao);
    }

    public void EndFrame() { }

    /// <summary>进入一趟渲染，设深度测试状态。深度关时不写深度缓冲（作背景/叠加）。</summary>
    public void BeginPass(bool depthTest)
    {
        if (depthTest) _gl.Enable(GL_DEPTH_TEST);
        else _gl.Disable(GL_DEPTH_TEST);
    }

    public void EndPass() { }

    private const int GL_POLYGON_OFFSET_FILL = 0x8037;
    /// <summary>着色面深度偏移开/关：面向后偏移一点, 同一位置的边线/高亮线不被面吃掉(z-fighting)。</summary>
    public void SetPolygonOffset(bool on) => SetPolygonOffset(on, 1.0f, 1.0f);

    /// <summary>多边形深度偏移。正值把面推远(让线浮在面上)；负值把面拉近(选中高亮面盖住原面)。</summary>
    public void SetPolygonOffset(bool on, float factor, float units)
    {
        if (on) { _gl.Enable(GL_POLYGON_OFFSET_FILL); _ext.PolygonOffset(factor, units); }
        else _gl.Disable(GL_POLYGON_OFFSET_FILL);
    }

    /// <summary>叠加层用：临时改视口（如左下角罗盘区）。</summary>
    public void SetViewport(int x, int y, int w, int h) => _gl.Viewport(x, y, w, h);

    private const int GL_PROGRAM_POINT_SIZE = 0x8642;
    private const int GL_DEPTH_LESS = 0x0201, GL_DEPTH_LEQUAL = 0x0203;

    /// <summary>
    /// 深度比较：默认 LESS；点云那一趟切 LEQUAL —— 各点云算子都是非破坏的，产物点与源点
    /// 坐标完全重合，LESS 下先画的(源点云)恒赢，着色结果被压在下面，看着就像"命令没生效"。
    /// LEQUAL 让同深度时后画的赢，最新的点云自然浮在最上层。
    /// </summary>
    public void SetDepthLessEqual(bool on) => _ext.DepthFunc(on ? GL_DEPTH_LEQUAL : GL_DEPTH_LESS);

    /// <summary>点云点径(像素)。GL_POINTS 那一趟前调用；其它图元不受影响。</summary>
    public void SetPointSize(float px)
    {
        if (_programPointSize) _gl.Enable(GL_PROGRAM_POINT_SIZE);
        _ext.Uniform1f(_uPointSize, px);
    }

    /// <summary>用给定 MVP 画一块网格。primMode = GL_LINES / GL_TRIANGLES / GL_POINTS。</summary>
    public void Draw(in Mesh m, int primMode, float[] mvp)
    {
        if (m.IsEmpty) return;
        _gl.BindBuffer(GL_ARRAY_BUFFER, m.Vbo);
        _gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 6 * sizeof(float), IntPtr.Zero);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, 6 * sizeof(float), new IntPtr(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _ext.UniformMatrix4fv(_uMvp, 1, 0, mvp);
        _gl.DrawArrays(primMode, 0, m.Count);
    }
}
