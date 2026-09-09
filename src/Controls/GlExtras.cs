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

    private readonly GlGenVertexArrays? _gen;
    private readonly GlBindVertexArray? _bind;
    private readonly GlDeleteVertexArrays? _del;
    private readonly GlUniformMatrix4fv _uniformMatrix4fv;
    private readonly GlPolygonOffset? _polygonOffset;
    private readonly GlUniform1f? _uniform1f;
    private readonly GlDepthFunc? _depthFunc;

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
    }

    /// <summary>各扩展入口点是否解析到(原生崩溃排查用: 空指针被调用就是段错误)。</summary>
    public string Resolved =>
        $"VAO={( _gen != null && _bind != null && _del != null ? "有" : "无")}" +
        $" glPolygonOffset={(_polygonOffset != null ? "有" : "无")}" +
        $" glUniform1f={(_uniform1f != null ? "有" : "无")}" +
        $" glDepthFunc={(_depthFunc != null ? "有" : "无")}";

    /// <summary>多边形深度偏移(着色面后退, 让边线/高亮浮在面上)；GL/GLES 均有。</summary>
    public void PolygonOffset(float factor, float units) => _polygonOffset?.Invoke(factor, units);

    /// <summary>标量 uniform(点云的屏幕点径 uPointSize)；老驱动解析不到时为空操作(点径退回 1 像素)。</summary>
    public void Uniform1f(int loc, float v) { if (loc >= 0) _uniform1f?.Invoke(loc, v); }

    /// <summary>深度比较函数(点云那一趟改 LEQUAL, 同深度时后画的赢)；GL/GLES 均有。</summary>
    public void DepthFunc(int func) => _depthFunc?.Invoke(func);

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
