using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网导出到通用格式 —— 忠实原 MeshEditLib 网格导出(OBJ/PLY/STL/glTF)的公开文本格式部分。
/// Kylin 原只导 OFF; 补 OBJ/PLY/STL(公开、文本、可 round-trip 验)。glTF(JSON+二进制 buffer)复杂, 记录。纯逻辑、可单测。
/// </summary>
public static class MeshExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Wavefront OBJ：v x y z / f i j k(1 基索引)。</summary>
    public static string ToObj(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var sb = new StringBuilder();
        sb.Append("# PitMine3D.Kylin OBJ\n");
        foreach (var p in v)
            sb.Append("v ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
        foreach (var f in t)
            sb.Append("f ").Append(f.a + 1).Append(' ').Append(f.b + 1).Append(' ').Append(f.c + 1).Append('\n');   // OBJ 1 基
        return sb.ToString();
    }

    /// <summary>Stanford PLY(ASCII)：头 + 顶点 x y z + 面 "3 i j k"(0 基)。</summary>
    public static string ToPly(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var sb = new StringBuilder();
        sb.Append("ply\nformat ascii 1.0\ncomment PitMine3D.Kylin\n");
        sb.Append("element vertex ").Append(v.Count).Append('\n');
        sb.Append("property float x\nproperty float y\nproperty float z\n");
        sb.Append("element face ").Append(t.Count).Append('\n');
        sb.Append("property list uchar int vertex_indices\nend_header\n");
        foreach (var p in v)
            sb.Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
        foreach (var f in t)
            sb.Append("3 ").Append(f.a).Append(' ').Append(f.b).Append(' ').Append(f.c).Append('\n');   // PLY 0 基
        return sb.ToString();
    }

    /// <summary>STL(ASCII)：逐三角 facet normal + outer loop 3 vertex。法向由三角右手定则算。</summary>
    public static string ToStlAscii(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var sb = new StringBuilder();
        sb.Append("solid PitMine3D_Kylin\n");
        foreach (var f in t)
        {
            if (f.a < 0 || f.b < 0 || f.c < 0 || f.a >= v.Count || f.b >= v.Count || f.c >= v.Count) continue;
            var (nx, ny, nz) = Normal(v[f.a], v[f.b], v[f.c]);
            sb.Append("  facet normal ").Append(F(nx)).Append(' ').Append(F(ny)).Append(' ').Append(F(nz)).Append('\n');
            sb.Append("    outer loop\n");
            V(sb, v[f.a]); V(sb, v[f.b]); V(sb, v[f.c]);
            sb.Append("    endloop\n  endfacet\n");
        }
        sb.Append("endsolid PitMine3D_Kylin\n");
        return sb.ToString();
    }

    /// <summary>按扩展名(.obj/.ply/.stl)选格式导出; 未知回落 OBJ。</summary>
    public static string ByExtension(string ext,
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t) =>
        (ext ?? "").TrimStart('.').ToLowerInvariant() switch
        {
            "ply" => ToPly(v, t),
            "stl" => ToStlAscii(v, t),
            _ => ToObj(v, t),
        };

    private static void V(StringBuilder sb, (double x, double y, double z) p) =>
        sb.Append("      vertex ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');

    private static (double, double, double) Normal((double x, double y, double z) p0, (double x, double y, double z) p1, (double x, double y, double z) p2)
    {
        double ux = p1.x - p0.x, uy = p1.y - p0.y, uz = p1.z - p0.z;
        double wx = p2.x - p0.x, wy = p2.y - p0.y, wz = p2.z - p0.z;
        double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return len < 1e-15 ? (0, 0, 0) : (nx / len, ny / len, nz / len);
    }

    private static string F(double d) => d.ToString("R", Inv);
}
