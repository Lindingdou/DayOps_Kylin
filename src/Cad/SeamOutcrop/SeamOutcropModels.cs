using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad.SeamOutcrop;

/// <summary>
/// 单层煤的定稿配置（不可变）。移植 <c>MineAssLib.SeamOutcrop.SeamSpec</c>。
///
/// <b>登记的差异</b>：原版的 <c>RoofHandle</c>/<c>FloorHandle</c> 是内核实体 handle（ulong），
/// 颜色是 <c>System.Windows.Media.Color</c>。Kylin 侧顶/底板就是场景里的 <c>MeshEntity</c>，
/// 按**图元名**指认（与图层/选择一致的口径）；颜色一律用**打包 RGB**（0xRRGGBB），
/// 因为引擎写进逐顶点色数组的本来就是这个打包值 —— 中间再套一层平台色类型只会多一处换算。
/// </summary>
public sealed class SeamSpec
{
    /// <summary>煤层名（显示 + 统计用）。</summary>
    public string Name { get; init; } = "";

    /// <summary>顶板面图元名。</summary>
    public string RoofName { get; init; } = "";

    /// <summary>底板面图元名。</summary>
    public string FloorName { get; init; } = "";

    /// <summary>着色 RGB，打包成 0xRRGGBB。</summary>
    public uint PackedRgb { get; init; }

    /// <summary>"#RRGGBB" 文本（界面显示 / 存盘用）。</summary>
    public string Hex => "#" + PackedRgb.ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>把 "#RRGGBB"（# 可省）解析成打包 RGB；格式不对返回 null（**不猜一个颜色出来**）。</summary>
    public static uint? ParseHex(string? hex)
    {
        string s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length != 6) return null;
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : null;
    }

    /// <summary>三分量 → 打包 RGB。</summary>
    public static uint Pack(byte r, byte g, byte b) => (uint)((r << 16) | (g << 8) | b);

    /// <summary>打包 RGB → 三分量。</summary>
    public static (byte R, byte G, byte B) Unpack(uint rgb)
        => ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
}

/// <summary>
/// 点「确定」后产出的配置：现状面 + 可采范围 + N 层煤 + 重剖参数。供露头着色引擎消费。
/// 移植 <c>MineAssLib.SeamOutcrop.SeamOutcropResult</c>。
/// </summary>
public sealed class SeamOutcropRequest
{
    /// <summary>现状面（地表 TIN）图元名。</summary>
    public string TerrainName { get; init; } = "";

    /// <summary>可采范围名（空 = 不裁，全部可采区域）；露头带横向裁到该范围。</summary>
    public string RegionName { get; init; } = "";

    public IReadOnlyList<SeamSpec> Seams { get; init; } = Array.Empty<SeamSpec>();

    /// <summary>露头判定容差 m：底板−ε ≤ 现状Z ≤ 顶板+ε 视为露头。</summary>
    public double SnapEps { get; init; } = 0.05;

    /// <summary>
    /// true = 沿「现状面 ∩ 顶/底板」的交线切开现状网再逐面着色（露头边界=真实交线，薄露头不漏）；
    /// false = 只按原有节点染色（边界锯齿≈一个三角边长，窄带可能整片漏染）。
    /// </summary>
    public bool RefineOnIntersection { get; init; } = true;
}

/// <summary>一层煤的着色结果统计（逐层回显，让"染了多少/漏了多少"当场看得见）。</summary>
public sealed class SeamOutcropLayerStat
{
    public string Name = "";
    public uint PackedRgb;
    /// <summary>本层新标记的顶点数。</summary>
    public int Marked;
    /// <summary>顶/底板没盖到（nodata）的顶点数。</summary>
    public int NoData;
    /// <summary>本层露头带穿过、但三个顶点都没被任何层认领的三角面数（漏染估算）。</summary>
    public int UncoveredTriangles;
}

/// <summary>整次着色的结果。</summary>
public sealed class SeamOutcropReport
{
    /// <summary>逐顶点色（0xRRGGBB）；<see cref="Assigned"/> 为 false 的位置**没有意义**，保持原色。</summary>
    public uint[] Color = Array.Empty<uint>();

    /// <summary>逐顶点是否被某层认领。</summary>
    public bool[] Assigned = Array.Empty<bool>();

    public List<SeamOutcropLayerStat> Layers = new();

    /// <summary>被认领的顶点总数。</summary>
    public int TotalMarked
    {
        get { int n = 0; foreach (var l in Layers) n += l.Marked; return n; }
    }

    /// <summary>漏染估算合计（明显不为 0 就说明该按交线重剖分）。</summary>
    public int TotalUncovered
    {
        get { int n = 0; foreach (var l in Layers) n += l.UncoveredTriangles; return n; }
    }

    /// <summary>为什么一个顶点都没染上（成功时为空串）。</summary>
    public string Why = "";
}
