using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        /// <summary>每顶点 float 数：6 = P3_C3(线/点/普通面)，9 = P3_C3_N3(材质面, 多一条法线)。</summary>
        public readonly int Stride;
        public Mesh(int vbo, int count, int stride = 6) { Vbo = vbo; Count = count; Stride = stride; }
        public bool IsEmpty => Count == 0;
    }

    private GlInterface _gl = null!;
    private GlExtras _ext = null!;
    private int _program;
    private int _uMvp;
    private int _uPointSize;
    private int _uLightBg;   // 浅底开关(片元着色器压白几何)
    private int _vao;
    private bool _programPointSize;
    private delegate void LineWidthProc(float width);
    private LineWidthProc? _lineWidthProc;
    private float _maxLineWidth = 6f;

    // ── 材质程序(PBR / 贴图 / 透明度) ──
    // 单独一个 program 而不是往主程序里塞分支：主程序线/点/面都走，塞进去等于每个片元都多背一段
    // PBR 代码；且国产 GPU 上主程序有 110/ESSL100 回退档，改动它风险最大。材质程序编不出来就
    // 退回普通面(MaterialReady=false)，主管线一行不受影响。
    private int _matProgram;
    private int _muMvp, _muMode, _muAlpha, _muMetallic, _muRoughness, _muTexScale, _muCam, _muTex;
    private string _header = "";
    private bool _modern;   // 桌面 GL 核心档需显式开 GL_PROGRAM_POINT_SIZE, 顶点着色器写的 gl_PointSize 才生效

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
        _lineWidthProc = null;
        try
        {
            var p = gl.GetProcAddress("glLineWidth");
            if (p != IntPtr.Zero) _lineWidthProc = Marshal.GetDelegateForFunctionPointer<LineWidthProc>(p);
        }
        catch { _lineWidthProc = null; _maxLineWidth = 6f; }

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
        _uLightBg = gl.GetUniformLocationString(_program, "uLightBg");
        _programPointSize = !isGles;   // GLES 恒按 gl_PointSize 走; 桌面档要 Enable 才认

        _vao = _ext.GenVertexArray();   // 老驱动无 VAO 扩展时返回 0, BindVertexArray 变空操作(每次 Draw 都重设属性指针, 不依赖 VAO)
        _ext.BindVertexArray(_vao);

        BuildMaterialProgram();
    }

    /// <summary>材质程序是否可用（编译成功 + 混合入口齐）。false 时材质面退回普通面, 不透明。</summary>
    public bool MaterialReady { get; private set; }

    /// <summary>材质程序不可用时的原因(诊断/状态栏用)。</summary>
    public string MaterialFailReason { get; private set; } = "";

    private void BuildMaterialProgram()
    {
        if (!_ext.HasBlend) { MaterialFailReason = "驱动无 glBlendFunc/glDepthMask"; return; }
        if (!TryBuildMaterialProgram(out string err)) { MaterialFailReason = err; return; }
        _muMvp = _gl.GetUniformLocationString(_matProgram, "uMVP");
        _muMode = _gl.GetUniformLocationString(_matProgram, "uMode");
        _muAlpha = _gl.GetUniformLocationString(_matProgram, "uAlpha");
        _muMetallic = _gl.GetUniformLocationString(_matProgram, "uMetallic");
        _muRoughness = _gl.GetUniformLocationString(_matProgram, "uRoughness");
        _muTexScale = _gl.GetUniformLocationString(_matProgram, "uTexScale");
        _muCam = _gl.GetUniformLocationString(_matProgram, "uCam");
        _muTex = _gl.GetUniformLocationString(_matProgram, "uTex");
        MaterialReady = true;
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
        // uLightBg: 视口背景为浅色时(选项·视口背景色), 近白色的几何(AutoCAD 色号 7 "白/黑随背景")压成深灰,
        // 否则白线白字在白底上直接消失。只动 min(r,g,b) > 0.85 的颜色, 彩色实体不受影响。
        const string WhiteOnLight = "if (uLightBg > 0.5 && min(vColor.r, min(vColor.g, vColor.b)) > 0.85) c = vec3(0.08);";
        string fs = modern
            ? header + "in vec3 vColor;\nuniform float uLightBg;\nout vec4 oColor;\nvoid main() { vec3 c = vColor; " + WhiteOnLight + " oColor = vec4(c, 1.0); }"
            : header + "varying vec3 vColor;\nuniform float uLightBg;\nvoid main() { vec3 c = vColor; " + WhiteOnLight + " gl_FragColor = vec4(c, 1.0); }";

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
        _header = header; _modern = modern;   // 材质程序照用同一方言(同一驱动, 没道理换)
        return true;
    }

    public void Deinit()
    {
        if (_program != 0) _gl.DeleteProgram(_program);
        if (_matProgram != 0) _gl.DeleteProgram(_matProgram);
        _ext.DeleteVertexArray(_vao);
        // 句柄必须归零：切文档标签会销毁再重建 GL 上下文, 新上下文里的名字从头编号——
        // 若留着旧 id, 下次 Init 里 TryBuildProgram 的 "先删旧程序" 会把**刚链接好的新程序**(同名 3)删掉,
        // 之后 UseProgram 用的就是个已删除的程序, 每帧只剩清屏色: 切回来的文档一片黑、网格都没有(实测)。
        _program = 0; _matProgram = 0; _vao = 0;
        _lineWidthProc = null;
        MaterialReady = false; MaterialFailReason = "";
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
        SetLineWidth(1f);
        _gl.ClearColor(r, g, b, 1f);
        _gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        _gl.UseProgram(_program);
        // 背景亮度 > 0.5 视为浅底 → 片元着色器把近白几何压深(见 TryBuildProgram 的 uLightBg)
        _lightBg = 0.2126f * r + 0.7152f * g + 0.0722f * b > 0.5f;
        SetWhiteRemap(true);
        _ext.BindVertexArray(_vao);
    }

    private bool _lightBg;
    /// <summary>浅底压白开/关：格网趟关(格网线本就是按背景推的浅灰, 压了就成黑线), 场景/高亮/光标趟开。深底下恒为 0。</summary>
    public void SetWhiteRemap(bool on)
    {
        if (_uLightBg >= 0) _ext.Uniform1f(_uLightBg, on && _lightBg ? 1f : 0f);
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

    /// <summary>设置 GL_LINES 的像素宽度；驱动不提供入口时安全退化为 1px。</summary>
    public void SetLineWidth(float px)
    {
        if (_lineWidthProc == null) return;
        _lineWidthProc(Math.Clamp(px, 1f, _maxLineWidth));
    }

    // ---- 材质面（PBR / 贴图 / 透明度）----

    private const int GL_TEXTURE_2D_ = 0x0DE1, GL_TEXTURE0_ = 0x84C0;
    private const int GL_RGBA_ = 0x1908, GL_UNSIGNED_BYTE_ = 0x1401;
    private const int GL_TEX_MIN_ = 0x2801, GL_TEX_MAG_ = 0x2800, GL_TEX_WRAP_S_ = 0x2802, GL_TEX_WRAP_T_ = 0x2803;
    private const int GL_REPEAT_ = 0x2901, GL_LINEAR_ = 0x2601, GL_LINEAR_MIPMAP_LINEAR_ = 0x2703;
    private const int GL_BLEND_ = 0x0BE2, GL_SRC_ALPHA_ = 0x0302, GL_ONE_MINUS_SRC_ALPHA_ = 0x0303;

    /// <summary>材质档编号，与原 Lit.hlsl 的 ShadingMode 对齐：0=顶点色(只叠透明) 4=贴图 6=PBR。</summary>
    public const int MatPlain = 0, MatTextured = 4, MatPbr = 6;

    /// <summary>
    /// Cook-Torrance（GGX + Smith + Schlick）+ 程序化环境项 —— 逐式照抄原 <c>Lit.hlsl</c> 的
    /// ComputePbrColor/SampleEnv（HLSL→GLSL：float3→vec3 · saturate→clamp · lerp→mix）。
    /// 没有 cubemap/IBL，环境项就是原版那套 Z-up 天空→地平→地面三段渐变，粗糙度越高越趋灰。
    /// </summary>
    private const string PbrGlsl = @"
const float PBR_PI = 3.14159265359;
// 与托管侧 MeshEntity.Light 同向(normalize(0.35,0.25,0.9))；GLSL 1.10 的 const 初始化式不许调函数, 故写死。
const vec3 LDIR = vec3(0.35088, 0.25063, 0.90226);
float DistributionGGX(vec3 N, vec3 H, float rough) {
    float a = rough * rough; float a2 = a * a;
    float NdotH = clamp(dot(N, H), 0.0, 1.0);
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / max(PBR_PI * denom * denom, 1e-5);
}
float GeometrySchlickGGX(float NdotX, float rough) {
    float r = rough + 1.0; float k = (r * r) / 8.0;
    return NdotX / (NdotX * (1.0 - k) + k);
}
float GeometrySmith(float NdotV, float NdotL, float rough) {
    return GeometrySchlickGGX(NdotV, rough) * GeometrySchlickGGX(NdotL, rough);
}
vec3 FresnelSchlick(float cosTheta, vec3 F0) {
    return F0 + (vec3(1.0) - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}
vec3 FresnelSchlickRoughness(float cosTheta, vec3 F0, float rough) {
    vec3 Fr = max(vec3(1.0 - rough), F0);
    return F0 + (Fr - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}
vec3 SampleEnv(vec3 dir, float rough) {
    float up = dir.z;
    vec3 sky = vec3(0.45, 0.65, 1.15);
    vec3 horiz = vec3(0.58, 0.58, 0.62);
    vec3 ground = vec3(0.12, 0.10, 0.09);
    vec3 env = (up > 0.0) ? mix(horiz, sky, clamp(up * 1.3, 0.0, 1.0))
                          : mix(horiz, ground, clamp(-up * 1.3, 0.0, 1.0));
    return mix(env, horiz, rough * 0.7);
}
vec3 ComputePbr(vec3 N, vec3 worldPos, vec3 albedo, float metallic, float rough) {
    vec3 V = normalize(uCam - worldPos);
    vec3 L = LDIR;
    vec3 H = normalize(V + L);
    float NdotV = max(dot(N, V), 1e-4);
    float NdotL = max(dot(N, L), 0.0);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    float NDF = DistributionGGX(N, H, rough);
    float G = GeometrySmith(NdotV, NdotL, rough);
    vec3 F = FresnelSchlick(clamp(dot(H, V), 0.0, 1.0), F0);
    vec3 spec = (NDF * G * F) / max(4.0 * NdotV * NdotL, 1e-4);
    vec3 kd = (vec3(1.0) - F) * (1.0 - metallic);
    vec3 Lo = (kd * albedo / PBR_PI + spec) * NdotL;
    vec3 R = reflect(-V, N);
    vec3 kSenv = FresnelSchlickRoughness(NdotV, F0, rough);
    vec3 envSpec = SampleEnv(R, rough) * kSenv;
    vec3 envDiff = SampleEnv(N, 1.0) * albedo * (1.0 - metallic);
    vec3 ambient = envDiff * (vec3(1.0) - kSenv) + envSpec * 1.6;
    vec3 color = ambient + Lo;
    return color / (color + vec3(1.0));
}
";

    private bool TryBuildMaterialProgram(out string error)
    {
        string aQ = _modern ? "in" : "attribute";
        string oV = _modern ? "out" : "varying";
        string iV = _modern ? "in" : "varying";
        string tex = _modern ? "texture" : "texture2D";
        string frag = _modern ? "oColor" : "gl_FragColor";
        const string NL = "\n";

        string vs = _header
            + aQ + " vec3 aPos;" + NL + aQ + " vec3 aColor;" + NL + aQ + " vec3 aNormal;" + NL
            + "uniform mat4 uMVP;" + NL
            + oV + " vec3 vColor;" + NL + oV + " vec3 vPos;" + NL + oV + " vec3 vN;" + NL
            + "void main() { vColor = aColor; vPos = aPos; vN = aNormal; gl_Position = uMVP * vec4(aPos, 1.0); }";

        string fs = _header
            + iV + " vec3 vColor;" + NL + iV + " vec3 vPos;" + NL + iV + " vec3 vN;" + NL
            + (_modern ? "out vec4 oColor;" + NL : "")
            + "uniform int uMode;" + NL + "uniform float uAlpha;" + NL + "uniform float uMetallic;" + NL
            + "uniform float uRoughness;" + NL + "uniform float uTexScale;" + NL
            + "uniform vec3 uCam;" + NL + "uniform sampler2D uTex;" + NL
            + PbrGlsl
            + "void main() {" + NL
            + "  vec3 N = normalize(vN);" + NL
            + "  if (uMode == 4) {" + NL
            + "    float sc = (uTexScale > 0.001) ? uTexScale : 50.0;" + NL
            // 三平面权重(归一化): 面朝哪一轴, 就多取哪个投影, 不需要 mesh 自带 UV
            + "    vec3 bw = abs(N); bw /= (bw.x + bw.y + bw.z + 1e-5);" + NL
            + "    vec3 cx = " + tex + "(uTex, vPos.yz / sc).rgb;" + NL
            + "    vec3 cy = " + tex + "(uTex, vPos.xz / sc).rgb;" + NL
            + "    vec3 cz = " + tex + "(uTex, vPos.xy / sc).rgb;" + NL
            + "    vec3 albedo = (cx * bw.x + cy * bw.y + cz * bw.z) * vColor;" + NL   // 实体色作 tint, 同原版
            + "    float k = 0.42 + 0.58 * abs(dot(N, LDIR));" + NL                    // 与其余面同一条打光口径
            + "    " + frag + " = vec4(albedo * k, uAlpha);" + NL
            + "  } else if (uMode == 6) {" + NL
            + "    " + frag + " = vec4(ComputePbr(N, vPos, vColor, clamp(uMetallic, 0.0, 1.0), clamp(uRoughness, 0.04, 1.0)), uAlpha);" + NL
            + "  } else {" + NL
            + "    " + frag + " = vec4(vColor, uAlpha);" + NL                          // 顶点色已在 CPU 侧打好光
            + "  }" + NL + "}";

        error = "";
        int vsh = _gl.CreateShader(GL_VERTEX_SHADER);
        string vlog = _gl.CompileShaderAndGetError(vsh, vs);
        if (!string.IsNullOrEmpty(vlog)) { error = "材质 VS: " + vlog.Trim(); return false; }
        int fsh = _gl.CreateShader(GL_FRAGMENT_SHADER);
        string flog = _gl.CompileShaderAndGetError(fsh, fs);
        if (!string.IsNullOrEmpty(flog)) { error = "材质 FS: " + flog.Trim(); return false; }
        int prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vsh);
        _gl.AttachShader(prog, fsh);
        _gl.BindAttribLocationString(prog, 0, "aPos");
        _gl.BindAttribLocationString(prog, 1, "aColor");
        _gl.BindAttribLocationString(prog, 2, "aNormal");
        string plog = _gl.LinkProgramAndGetError(prog);
        if (!string.IsNullOrEmpty(plog)) { error = "材质 LINK: " + plog.Trim(); _gl.DeleteProgram(prog); return false; }
        _matProgram = prog;
        return true;
    }

    /// <summary>上传材质面（交错 P3_C3_N3，9 float/顶点）。</summary>
    public unsafe Mesh UploadMat(float[] p3c3n3)
    {
        int vbo = _gl.GenBuffer();
        _gl.BindBuffer(GL_ARRAY_BUFFER, vbo);
        fixed (float* p = p3c3n3)
            _gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(p3c3n3.Length * sizeof(float)), new IntPtr(p), GL_STATIC_DRAW);
        return new Mesh(vbo, p3c3n3.Length / 9, 9);
    }

    /// <summary>
    /// 半透明混合开/关。开时同时**关深度写** —— 半透面之间不互相挡，也不会把它后面的东西挡没
    /// (不做逐三角排序的标准近似)。
    /// </summary>
    public void SetBlend(bool on)
    {
        if (on) { _gl.Enable(GL_BLEND_); _ext.BlendFunc(GL_SRC_ALPHA_, GL_ONE_MINUS_SRC_ALPHA_); _ext.DepthMask(false); }
        else { _gl.Disable(GL_BLEND_); _ext.DepthMask(true); }
    }

    /// <summary>RGBA8 像素 → GL 纹理(重复平铺 + mipmap)。驱动没有纹理入口时返回 0。</summary>
    public unsafe int CreateTexture(byte[] rgba, int w, int h)
    {
        if (!_ext.HasTexture || w <= 0 || h <= 0 || rgba.Length < w * h * 4) return 0;
        int t = _ext.GenTexture();
        if (t == 0) return 0;
        _ext.ActiveTexture(GL_TEXTURE0_);
        _ext.BindTexture(GL_TEXTURE_2D_, t);
        fixed (byte* p = rgba)
            _ext.TexImage2D(GL_TEXTURE_2D_, 0, GL_RGBA_, w, h, 0, GL_RGBA_, GL_UNSIGNED_BYTE_, new IntPtr(p));
        _ext.TexParameteri(GL_TEXTURE_2D_, GL_TEX_WRAP_S_, GL_REPEAT_);
        _ext.TexParameteri(GL_TEXTURE_2D_, GL_TEX_WRAP_T_, GL_REPEAT_);
        _ext.TexParameteri(GL_TEXTURE_2D_, GL_TEX_MAG_, GL_LINEAR_);
        _ext.TexParameteri(GL_TEXTURE_2D_, GL_TEX_MIN_, GL_LINEAR_MIPMAP_LINEAR_);
        _ext.GenerateMipmap(GL_TEXTURE_2D_);
        return t;
    }

    public void DeleteTexture(int tex) => _ext.DeleteTexture(tex);

    /// <summary>
    /// 画一批材质面。mode = <see cref="MatPlain"/>/<see cref="MatTextured"/>/<see cref="MatPbr"/>；
    /// cam 是**渲染局部系**的相机位(与顶点同系, 见 RenderOrigin)，PBR 的视线方向由它来。
    /// 画完还原主程序 —— 后面各趟都是按主程序写的。
    /// </summary>
    public void DrawMaterial(in Mesh m, float[] mvp, int mode, float alpha,
                             float metallic, float roughness, float texScale, float[] cam, int texture)
    {
        if (m.IsEmpty || !MaterialReady) return;
        _gl.UseProgram(_matProgram);
        _gl.BindBuffer(GL_ARRAY_BUFFER, m.Vbo);
        int st = m.Stride * sizeof(float);
        _gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, st, IntPtr.Zero);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, st, new IntPtr(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        if (m.Stride >= 9)
        {
            _gl.VertexAttribPointer(2, 3, GL_FLOAT, 0, st, new IntPtr(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(2);
        }
        else _ext.ConstAttrib3f(2, 0f, 0f, 1f);
        _ext.UniformMatrix4fv(_muMvp, 1, 0, mvp);
        _ext.Uniform1i(_muMode, mode);
        _ext.Uniform1f(_muAlpha, alpha);
        _ext.Uniform1f(_muMetallic, metallic);
        _ext.Uniform1f(_muRoughness, roughness);
        _ext.Uniform1f(_muTexScale, texScale);
        if (cam.Length >= 3) _ext.Uniform3f(_muCam, cam[0], cam[1], cam[2]);
        if (mode == MatTextured && texture != 0)
        {
            _ext.ActiveTexture(GL_TEXTURE0_);
            _ext.BindTexture(GL_TEXTURE_2D_, texture);
            _ext.Uniform1i(_muTex, 0);
        }
        _gl.DrawArrays(GL_TRIANGLES, 0, m.Count);
        // 关掉法线属性数组再回主程序: 留着它指向本 VBO, 缓冲一删就是悬垂指针(某些驱动直接段错误)
        _ext.ConstAttrib3f(2, 0f, 0f, 1f);
        _gl.UseProgram(_program);
    }

    /// <summary>用给定 MVP 画一块网格。primMode = GL_LINES / GL_TRIANGLES / GL_POINTS。</summary>
    public void Draw(in Mesh m, int primMode, float[] mvp) => Draw(m, primMode, mvp, 0, m.Count);

    /// <summary>只画缓冲里 [first, first+count) 这段顶点(同一 VBO 分实体多次下发, 各段可配不同深度偏移)。</summary>
    public void Draw(in Mesh m, int primMode, float[] mvp, int first, int count)
    {
        if (m.IsEmpty || count <= 0 || first < 0 || first + count > m.Count) return;
        _gl.BindBuffer(GL_ARRAY_BUFFER, m.Vbo);
        _gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 6 * sizeof(float), IntPtr.Zero);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, 6 * sizeof(float), new IntPtr(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _ext.UniformMatrix4fv(_uMvp, 1, 0, mvp);
        _gl.DrawArrays(primMode, first, count);
    }
}
