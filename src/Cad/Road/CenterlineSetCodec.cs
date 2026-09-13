// 忠实移植自原 PitMine3D Modules/RoadLib/Network/CenterlineSetCodec.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 中心线集合的打包 / 解包（一批扁平折线 ↔ base64 字符串），供「中心线管理」的存档落库。
///
/// 布局（小端，BinaryWriter 平台无关；整体再 GZip 后 base64）：
///   int32 magic('RCL1') · int32 version(1) · int32 lineCount
///   逐条：int32 vertCount · vertCount×3 float64（扁平 x,y,z）
///
/// <b>为什么不是 JSON</b>：一次提取动辄上千条、每条上百顶点 —— 存成 JSON 文本是几十 MB 的
/// 数字串，写库/读库都要按字符解析一遍。二进制 + GZip 后通常只有原始的三成，且解包是纯
/// 内存拷贝。与 <c>virtual_drill_surface.geometry_b64</c> 同一套路（那张表存的是三角网）。
///
/// <b>坐标不做锚点平移</b>：这里是 float64 全程，现场 620000/4380000 那个量级也还有 10 位小数，
/// 不像内核那条 float32 的路需要先减基点（见 <c>PitMine_CarveStripOnSeam</c> 的注释）。
/// </summary>
public static class CenterlineSetCodec
{
    private const int Magic = 0x52434C31;   // 'RCL1'
    private const int Version = 1;

    /// <summary>把一批中线（逐条扁平 [x,y,z,...]，顶点 &lt; 2 的条目跳过）打包成 base64。无有效线返回空串。</summary>
    public static string Pack(IReadOnlyList<double[]>? lines)
    {
        if (lines == null || lines.Count == 0) return "";

        var keep = new List<double[]>(lines.Count);
        foreach (var l in lines)
            if (l != null && l.Length >= 6 && l.Length % 3 == 0) keep.Add(l);
        if (keep.Count == 0) return "";

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(keep.Count);
            foreach (var l in keep)
            {
                w.Write(l.Length / 3);
                for (int i = 0; i < l.Length; i++) w.Write(l[i]);
            }
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>解包 base64 → 中线表。任何损坏 / 越界都判失败（不抛），调用方按返回值退路。</summary>
    public static bool TryUnpack(string? b64, out List<double[]> lines)
    {
        lines = new List<double[]>();
        if (string.IsNullOrEmpty(b64)) return false;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch { return false; }
        if (bytes.Length < 8) return false;

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);

            if (r.ReadInt32() != Magic) return false;
            if (r.ReadInt32() != Version) return false;
            int count = r.ReadInt32();
            if (count < 0 || count > 5_000_000) return false;

            var outLines = new List<double[]>(Math.Min(count, 4096));
            for (int i = 0; i < count; i++)
            {
                int n = r.ReadInt32();
                if (n < 2 || n > 20_000_000) return false;   // 坏数据：别按它去分配
                var flat = new double[n * 3];
                for (int k = 0; k < flat.Length; k++) flat[k] = r.ReadDouble();
                outLines.Add(flat);
            }
            lines = outLines;
            return true;
        }
        catch { return false; }   // EndOfStream / 压缩流损坏都在这儿收口
    }

    /// <summary>统计一批中线的条数 / 顶点数 / 三维总长 (m) / 包围盒，供存档列表免解包直接显示。</summary>
    public static CenterlineSetStats Stats(IReadOnlyList<double[]>? lines)
    {
        var s = new CenterlineSetStats();
        if (lines == null || lines.Count == 0) return s;

        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var l in lines)
        {
            if (l == null || l.Length < 6) continue;
            s.LineCount++;
            s.VertexCount += l.Length / 3;
            s.LengthM += CenterlinePick.Length3d(l);
            for (int i = 0; i + 2 < l.Length; i += 3)
            {
                double x = l[i], y = l[i + 1], z = l[i + 2];
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
                if (z < z0) z0 = z; if (z > z1) z1 = z;
            }
        }
        if (s.LineCount > 0)
        {
            s.MinX = x0; s.MinY = y0; s.MinZ = z0;
            s.MaxX = x1; s.MaxY = y1; s.MaxZ = z1;
        }
        return s;
    }
}

/// <summary>中心线集合的汇总量（存档行的冗余列，列表直接显示，不必解包几何）。</summary>
public sealed class CenterlineSetStats
{
    public int LineCount;
    public int VertexCount;
    public double LengthM;
    public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

    public double LengthKm => LengthM / 1000.0;
}
