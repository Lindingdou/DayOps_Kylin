using System;
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
    private int _vao;

    public void Init(GlInterface gl, GlExtras ext, bool isGles)
    {
        _gl = gl;
        _ext = ext;

        string header = isGles ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        string vs = header +
            "in vec3 aPos;\nin vec3 aColor;\nuniform mat4 uMVP;\nout vec3 vColor;\n" +
            "void main() { vColor = aColor; gl_Position = uMVP * vec4(aPos, 1.0); }";
        string fs = header +
            "in vec3 vColor;\nout vec4 oColor;\n" +
            "void main() { oColor = vec4(vColor, 1.0); }";

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

        _vao = _ext.GenVertexArray();
        _ext.BindVertexArray(_vao);
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
    public void SetPolygonOffset(bool on)
    {
        if (on) { _gl.Enable(GL_POLYGON_OFFSET_FILL); _ext.PolygonOffset(1.0f, 1.0f); }
        else _gl.Disable(GL_POLYGON_OFFSET_FILL);
    }

    /// <summary>叠加层用：临时改视口（如左下角罗盘区）。</summary>
    public void SetViewport(int x, int y, int w, int h) => _gl.Viewport(x, y, w, h);

    /// <summary>用给定 MVP 画一块网格。primMode = GL_LINES / GL_TRIANGLES。</summary>
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
