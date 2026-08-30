using System;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 色带（colormap）—— 移植自原 `PointCloudLib.Shading.TinColormap` 的锚点色 + 线性插值核（脱 WPF Color）。
/// 供按高程/属性着色：t∈[0,1] 在等距锚点间线性插值。纯逻辑、可单测。
/// </summary>
public static class Colormap
{
    /// <summary>地形（默认，最贴合高程）。</summary>
    public static readonly (byte r, byte g, byte b)[] Terrain =
        { (0, 97, 71), (124, 194, 66), (255, 255, 128), (191, 128, 64), (255, 255, 255) };
    /// <summary>Jet。</summary>
    public static readonly (byte r, byte g, byte b)[] Jet =
        { (0, 0, 131), (0, 100, 255), (0, 255, 255), (255, 255, 0), (255, 0, 0), (128, 0, 0) };
    /// <summary>灰度。</summary>
    public static readonly (byte r, byte g, byte b)[] Grayscale = { (0, 0, 0), (255, 255, 255) };

    /// <summary>t∈[0,1] 在锚点色间线性插值（等距锚点）；越界夹到端点。</summary>
    public static (byte r, byte g, byte b) Sample((byte r, byte g, byte b)[] stops, double t)
    {
        if (stops == null || stops.Length == 0) return (255, 255, 255);
        if (t <= 0) return stops[0];
        if (t >= 1) return stops[^1];
        int n = stops.Length;
        double seg = t * (n - 1);
        int s0 = (int)Math.Floor(seg);
        if (s0 > n - 2) s0 = n - 2;
        if (s0 < 0) s0 = 0;
        double u = seg - s0;
        var a = stops[s0]; var c = stops[s0 + 1];
        return ((byte)(a.r + (c.r - a.r) * u), (byte)(a.g + (c.g - a.g) * u), (byte)(a.b + (c.b - a.b) * u));
    }
}
