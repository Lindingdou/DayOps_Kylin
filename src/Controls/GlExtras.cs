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

    private readonly GlGenVertexArrays? _gen;
    private readonly GlBindVertexArray? _bind;
    private readonly GlDeleteVertexArrays? _del;
    private readonly GlUniformMatrix4fv _uniformMatrix4fv;
    private readonly GlPolygonOffset? _polygonOffset;

    public GlExtras(GlInterface gl)
    {
        _gen = Load<GlGenVertexArrays>(gl, "glGenVertexArrays", "glGenVertexArraysOES");
        _bind = Load<GlBindVertexArray>(gl, "glBindVertexArray", "glBindVertexArrayOES");
        _del = Load<GlDeleteVertexArrays>(gl, "glDeleteVertexArrays", "glDeleteVertexArraysOES");
        _uniformMatrix4fv = Load<GlUniformMatrix4fv>(gl, "glUniformMatrix4fv")
                            ?? throw new InvalidOperationException("glUniformMatrix4fv 不可用");
        _polygonOffset = Load<GlPolygonOffset>(gl, "glPolygonOffset");
    }

    /// <summary>多边形深度偏移(着色面后退, 让边线/高亮浮在面上)；GL/GLES 均有。</summary>
    public void PolygonOffset(float factor, float units) => _polygonOffset?.Invoke(factor, units);

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
