// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/VirtualDrill/VirtualDrillGeometry.cs（逐行对应；仅命名空间适配）
using System;
using System.IO;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 虚拟钻孔地质模型三角网几何的打包 / 解包(顶点 + 三角索引 ↔ base64)。
///
/// 布局(小端,BinaryWriter 平台无关):
///   int32 magic(0x56445431 'VDT1') · int32 version(1) · int32 vCount · int32 tCount
///   vCount×3 float64 顶点(扁平 x,y,z) · tCount×3 int32 三角索引
/// 存 <c>virtual_drill_surface.geometry_b64</c>(opaque)。几何完整保留节点坐标与连接顺序。
/// </summary>
internal static class VirtualDrillGeometry
{
    private const int Magic = 0x56445431;   // 'VDT1'
    private const int Version = 1;

    /// <summary>把顶点(扁平 [x,y,z,...])+ 三角索引打包成 base64。非法输入返回空串。</summary>
    public static string Pack(double[] verts, int[] tris)
    {
        if (verts == null || tris == null) return "";
        if (verts.Length < 9 || verts.Length % 3 != 0) return "";
        if (tris.Length < 3 || tris.Length % 3 != 0) return "";

        int vCount = verts.Length / 3;
        int tCount = tris.Length / 3;

        using var ms = new MemoryStream(16 + verts.Length * 8 + tris.Length * 4);
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(vCount);
            w.Write(tCount);
            for (int i = 0; i < verts.Length; i++) w.Write(verts[i]);
            for (int i = 0; i < tris.Length; i++) w.Write(tris[i]);
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>解包 base64 → 顶点 + 三角索引。任何损坏 / 越界都判失败(不抛)。</summary>
    public static bool TryUnpack(string? b64, out double[] verts, out int[] tris)
    {
        verts = Array.Empty<double>();
        tris = Array.Empty<int>();
        if (string.IsNullOrEmpty(b64)) return false;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch { return false; }
        if (bytes.Length < 16) return false;

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(ms);
            if (r.ReadInt32() != Magic) return false;
            if (r.ReadInt32() != Version) return false;
            int vCount = r.ReadInt32();
            int tCount = r.ReadInt32();
            if (vCount < 3 || tCount < 1) return false;
            long need = 16L + (long)vCount * 3 * 8 + (long)tCount * 3 * 4;
            if (bytes.Length < need) return false;

            var v = new double[vCount * 3];
            for (int i = 0; i < v.Length; i++) v[i] = r.ReadDouble();
            var t = new int[tCount * 3];
            for (int i = 0; i < t.Length; i++)
            {
                int idx = r.ReadInt32();
                if (idx < 0 || idx >= vCount) return false;   // 索引越界 = 坏数据
                t[i] = idx;
            }
            verts = v;
            tris = t;
            return true;
        }
        catch { return false; }
    }

    /// <summary>算顶点包围盒(空/非法返回全 0)。</summary>
    public static void ComputeBounds(double[]? verts,
        out double minX, out double minY, out double minZ,
        out double maxX, out double maxY, out double maxZ)
    {
        minX = minY = minZ = 0; maxX = maxY = maxZ = 0;
        if (verts == null || verts.Length < 3) return;
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        for (int i = 0; i + 2 < verts.Length; i += 3)
        {
            double x = verts[i], y = verts[i + 1], z = verts[i + 2];
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
            if (z < z0) z0 = z; if (z > z1) z1 = z;
        }
        minX = x0; minY = y0; minZ = z0; maxX = x1; maxY = y1; maxZ = z1;
    }
}
