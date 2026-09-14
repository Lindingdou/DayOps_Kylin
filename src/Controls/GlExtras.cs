using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace PitMine3D.Kylin.Controls;

/// <summary>
/// Avalonia 11.2 的 GlInterfaceBase / 入口特性是 internal，外部无法继承。
/// 这里只用公开的 GlInterface.GetProcAddress 自行解析 VAO 与 UniformMatrix4fv。
/// 委托不标调用约定 = 默认 Winapi：Windows 上 StdCall（匹配 GL/ANGLE 的 APIENTRY），
/// Linux（麒麟）上即唯一的 Cdecl —— 两平台都对。
/// </summary>
internal sealed class GlExtras
{
    public delegate void GlGenVertexArrays(int n, int[] arrays);
    public delegate void GlBindVertexArray(int array);
    public delegate void GlDeleteVertexArrays(int n, int[] arrays);
    public delegate void GlUniformMatrix4fv(int location, int count, int transpose, float[] value);
    public delegate void GlPolygonOffset(float factor, float units);
    public delegate void GlUniform1f(int location, float v0);
    public delegate void GlDepthFunc(int func);
    public delegate void GlGetIntegerv(int pname, int[] data);
    public delegate void GlGetFramebufferAttachmentParameteriv(int target, int attachment, int pname, int[] data);
    public delegate void GlBindRenderbuffer(int target, int renderbuffer);
    public delegate void GlGetRenderbufferParameteriv(int target, int pname, int[] data);
    // ── 材质/贴图/透明度 三页要的入口(纹理 + 混合 + 标量 uniform) ──
    public delegate void GlUniform1i(int location, int v0);
    public delegate void GlUniform3f(int location, float x, float y, float z);
    public delegate void GlBlendFunc(int sfactor, int dfactor);
    public delegate void GlDepthMask(byte flag);
    public delegate void GlVertexAttrib3f(int index, float x, float y, float z);
    public delegate void GlDisableVertexAttribArray(int index);
    public delegate void GlGenTextures(int n, int[] textures);
    public delegate void GlBindTexture(int target, int texture);
    public delegate void GlDeleteTextures(int n, int[] textures);
    public delegate void GlTexImage2D(int target, int level, int internalFormat, int w, int h, int border, int format, int type, IntPtr pixels);
    public delegate void GlTexParameteri(int target, int pname, int param);
    public delegate void GlGenerateMipmap(int target);
    public delegate void GlActiveTexture(int texture);

    private readonly GlGenVertexArrays? _gen;
    private readonly GlBindVertexArray? _bind;
    private readonly GlDeleteVertexArrays? _del;
    private readonly GlUniformMatrix4fv _uniformMatrix4fv;
    private readonly GlPolygonOffset? _polygonOffset;
    private readonly GlUniform1f? _uniform1f;
    private readonly GlDepthFunc? _depthFunc;
    private readonly GlGetIntegerv? _getIntegerv;
    private readonly GlGetFramebufferAttachmentParameteriv? _getFbAttach;
    private readonly GlBindRenderbuffer? _bindRb;
    private readonly GlGetRenderbufferParameteriv? _getRbParam;
    private readonly GlUniform1i? _uniform1i;
    private readonly GlUniform3f? _uniform3f;
    private readonly GlBlendFunc? _blendFunc;
    private readonly GlDepthMask? _depthMask;
    private readonly GlVertexAttrib3f? _vertexAttrib3f;
    private readonly GlDisableVertexAttribArray? _disableAttrib;
    private readonly GlGenTextures? _genTextures;
    private readonly GlBindTexture? _bindTexture;
    private readonly GlDeleteTextures? _delTextures;
    private readonly GlTexImage2D? _texImage2D;
    private readonly GlTexParameteri? _texParameteri;
    private readonly GlGenerateMipmap? _genMipmap;
    private readonly GlActiveTexture? _activeTexture;

    public GlExtras(GlInterface gl)
    {
        _gen = Load<GlGenVertexArrays>(gl, "glGenVertexArrays", "glGenVertexArraysOES");
        _bind = Load<GlBindVertexArray>(gl, "glBindVertexArray", "glBindVertexArrayOES");
        _del = Load<GlDeleteVertexArrays>(gl, "glDeleteVertexArrays", "glDeleteVertexArraysOES");
        _uniformMatrix4fv = Load<GlUniformMatrix4fv>(gl, "glUniformMatrix4fv")
                            ?? throw new InvalidOperationException("glUniformMatrix4fv 不可用");
        _polygonOffset = Load<GlPolygonOffset>(gl, "glPolygonOffset");
        _uniform1f = Load<GlUniform1f>(gl, "glUniform1f");
        _depthFunc = Load<GlDepthFunc>(gl, "glDepthFunc");
        _getIntegerv = Load<GlGetIntegerv>(gl, "glGetIntegerv");
        _getFbAttach = Load<GlGetFramebufferAttachmentParameteriv>(gl, "glGetFramebufferAttachmentParameteriv");
        _bindRb = Load<GlBindRenderbuffer>(gl, "glBindRenderbuffer");
        _getRbParam = Load<GlGetRenderbufferParameteriv>(gl, "glGetRenderbufferParameteriv");
        _uniform1i = Load<GlUniform1i>(gl, "glUniform1i");
        _uniform3f = Load<GlUniform3f>(gl, "glUniform3f");
        _blendFunc = Load<GlBlendFunc>(gl, "glBlendFunc");
        _depthMask = Load<GlDepthMask>(gl, "glDepthMask");
        _vertexAttrib3f = Load<GlVertexAttrib3f>(gl, "glVertexAttrib3f");
        _disableAttrib = Load<GlDisableVertexAttribArray>(gl, "glDisableVertexAttribArray");
        _genTextures = Load<GlGenTextures>(gl, "glGenTextures");
        _bindTexture = Load<GlBindTexture>(gl, "glBindTexture");
        _delTextures = Load<GlDeleteTextures>(gl, "glDeleteTextures");
        _texImage2D = Load<GlTexImage2D>(gl, "glTexImage2D");
        _texParameteri = Load<GlTexParameteri>(gl, "glTexParameteri");
        _genMipmap = Load<GlGenerateMipmap>(gl, "glGenerateMipmap", "glGenerateMipmapOES");
        _activeTexture = Load<GlActiveTexture>(gl, "glActiveTexture");
    }

    /// <summary>纹理入口是否齐(缺一个就不能走贴图档 —— 老驱动上如实退回, 不能拿空指针去调)。</summary>
    public bool HasTexture => _genTextures != null && _bindTexture != null && _texImage2D != null
                              && _texParameteri != null && _activeTexture != null && _uniform1i != null;

    /// <summary>混合入口是否齐(透明度档要它)。</summary>
    public bool HasBlend => _blendFunc != null && _depthMask != null;

    /// <summary>各扩展入口点是否解析到(原生崩溃排查用: 空指针被调用就是段错误)。</summary>
    public string Resolved =>
        $"VAO={( _gen != null && _bind != null && _del != null ? "有" : "无")}" +
        $" glPolygonOffset={(_polygonOffset != null ? "有" : "无")}" +
        $" glUniform1f={(_uniform1f != null ? "有" : "无")}" +
        $" glDepthFunc={(_depthFunc != null ? "有" : "无")}" +
        $" 纹理={(HasTexture ? "有" : "无")} 混合={(HasBlend ? "有" : "无")}";

    /// <summary>多边形深度偏移(着色面后退, 让边线/高亮浮在面上)；GL/GLES 均有。</summary>
    public void PolygonOffset(float factor, float units) => _polygonOffset?.Invoke(factor, units);

    /// <summary>标量 uniform(点云的屏幕点径 uPointSize)；老驱动解析不到时为空操作(点径退回 1 像素)。</summary>
    public void Uniform1f(int loc, float v) { if (loc >= 0) _uniform1f?.Invoke(loc, v); }

    /// <summary>深度比较函数(点云那一趟改 LEQUAL, 同深度时后画的赢)；GL/GLES 均有。</summary>
    public void DepthFunc(int func) => _depthFunc?.Invoke(func);

    /// <summary>当前帧缓冲某附件挂的对象名(渲染缓冲/纹理 id)；没挂或问不到返回 0。绑的是 FBO 时附件名用 GL_DEPTH_ATTACHMENT 等。</summary>
    public int FramebufferAttachmentName(int attachment)
    {
        if (_getFbAttach == null) return 0;
        var v = new int[4];
        try { _getFbAttach(0x8D40 /*GL_FRAMEBUFFER*/, attachment, 0x8CD1 /*OBJECT_NAME*/, v); } catch { return 0; }
        return v[0];
    }

    /// <summary>绑上某个渲染缓冲问它的 (宽, 高, 深度位)；问不到返回 (0,0,0)。问完把原来绑的还回去。</summary>
    public (int w, int h, int depthBits) RenderbufferInfo(int rb)
    {
        if (_bindRb == null || _getRbParam == null || rb == 0) return (0, 0, 0);
        try
        {
            int prev = GetInteger(0x8CA7 /*GL_RENDERBUFFER_BINDING*/);
            _bindRb(0x8D41, rb);
            var w = new int[4]; var h = new int[4]; var d = new int[4];
            _getRbParam(0x8D41, 0x8D42 /*WIDTH*/, w);
            _getRbParam(0x8D41, 0x8D43 /*HEIGHT*/, h);
            _getRbParam(0x8D41, 0x8D54 /*DEPTH_SIZE*/, d);
            _bindRb(0x8D41, Math.Max(0, prev));
            return (w[0], h[0], d[0]);
        }
        catch { return (0, 0, 0); }
    }

    /// <summary>读一个整型状态(GL_DEPTH_BITS 等)；解析不到返回 -1。</summary>
    public int GetInteger(int pname)
    {
        if (_getIntegerv == null) return -1;
        var v = new int[4];
        try { _getIntegerv(pname, v); } catch { return -1; }
        return v[0];
    }

    public void Uniform1i(int loc, int v) { if (loc >= 0) _uniform1i?.Invoke(loc, v); }
    public void Uniform3f(int loc, float x, float y, float z) { if (loc >= 0) _uniform3f?.Invoke(loc, x, y, z); }
    public void BlendFunc(int src, int dst) => _blendFunc?.Invoke(src, dst);
    public void DepthMask(bool on) => _depthMask?.Invoke(on ? (byte)1 : (byte)0);

    /// <summary>关掉某条属性数组并给它一个常量值 —— 线/点没有法线, 走材质程序时要喂个朝上的默认法线。</summary>
    public void ConstAttrib3f(int index, float x, float y, float z)
    {
        _disableAttrib?.Invoke(index);
        _vertexAttrib3f?.Invoke(index, x, y, z);
    }

    public int GenTexture()
    {
        if (_genTextures == null) return 0;
        var a = new int[1];
        _genTextures(1, a);
        return a[0];
    }

    public void BindTexture(int target, int tex) => _bindTexture?.Invoke(target, tex);
    public void DeleteTexture(int tex) { if (_delTextures != null && tex != 0) _delTextures(1, new[] { tex }); }
    public void TexImage2D(int target, int level, int ifmt, int w, int h, int border, int fmt, int type, IntPtr px)
        => _texImage2D?.Invoke(target, level, ifmt, w, h, border, fmt, type, px);
    public void TexParameteri(int target, int pname, int param) => _texParameteri?.Invoke(target, pname, param);
    public void GenerateMipmap(int target) => _genMipmap?.Invoke(target);
    public void ActiveTexture(int unit) => _activeTexture?.Invoke(unit);

    private static T? Load<T>(GlInterface gl, params string[] names) where T : Delegate
    {
        foreach (var n in names)
        {
            IntPtr p = gl.GetProcAddress(n);
            if (p != IntPtr.Zero)
                return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        return null;
    }

    public int GenVertexArray()
    {
        if (_gen == null) return 0;   // GLES2 无 VAO 时退回默认 VAO 0
        var a = new int[1];
        _gen(1, a);
        return a[0];
    }

    public void BindVertexArray(int a) => _bind?.Invoke(a);
    public void DeleteVertexArray(int a) { if (_del != null) _del(1, new[] { a }); }
    public void UniformMatrix4fv(int loc, int count, int transpose, float[] v) => _uniformMatrix4fv(loc, count, transpose, v);
}
