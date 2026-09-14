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
    /// <summary>Viridis（感知均匀, 色盲友好, 现代数据可视化标准）。</summary>
    public static readonly (byte r, byte g, byte b)[] Viridis =
        { (68, 1, 84), (59, 82, 139), (33, 145, 140), (94, 201, 98), (253, 231, 37) };
    /// <summary>Turbo（改进彩虹, 感知均匀）。</summary>
    public static readonly (byte r, byte g, byte b)[] Turbo =
        { (48, 18, 59), (65, 69, 217), (26, 183, 225), (122, 209, 81), (251, 155, 6), (122, 4, 3) };
    /// <summary>Magma（感知均匀, 黑→紫→粉→白）。</summary>
    public static readonly (byte r, byte g, byte b)[] Magma =
        { (0, 0, 4), (81, 18, 124), (183, 55, 121), (252, 137, 97), (252, 253, 191) };
    /// <summary>Plasma（感知均匀, 蓝→紫→橙→黄）。</summary>
    public static readonly (byte r, byte g, byte b)[] Plasma =
        { (13, 8, 135), (126, 3, 168), (204, 71, 120), (248, 149, 64), (240, 249, 33) };
    /// <summary>RdYlBu（矿业惯用发散色带: 蓝=低 → 米黄=中 → 红=高）。</summary>
    public static readonly (byte r, byte g, byte b)[] RdYlBu =
        { (49, 54, 149), (116, 173, 209), (255, 255, 191), (244, 109, 67), (165, 0, 38) };

    /// <summary>
    /// 「渲染配置」色带下拉的预设表 —— 名称与顺序照原 RenderConfigDialog 的 s_colormaps。
    /// </summary>
    public static readonly (string Name, (byte r, byte g, byte b)[] Stops)[] Presets =
    {
        ("Viridis", Viridis),
        ("Magma", Magma),
        ("Jet", Jet),
        ("Turbo", Turbo),
        ("RdYlBu 矿业", RdYlBu),
        ("Terrain 地形", Terrain),
        ("Grayscale", Grayscale),
    };

    /// <summary>按名取色带（不区分大小写）；未知返回 Terrain。</summary>
    public static (byte r, byte g, byte b)[] ByName(string name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "jet" => Jet,
        "grayscale" or "gray" or "灰度" => Grayscale,
        "viridis" => Viridis,
        "turbo" => Turbo,
        "magma" => Magma,
        "plasma" => Plasma,
        "rdylbu" or "rdylbu 矿业" => RdYlBu,
        _ => Terrain,
    };

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
