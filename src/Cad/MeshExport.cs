using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网导出到通用格式 —— 忠实原 <c>MeshEditLib.ViewModels.MeshExportViewModel</c>。
///
/// **原版三个导出选项**（§三二三 补齐）：导出法线 / 翻转 Y/Z 轴 / 缩放因子。
/// 逐格式的落法照原版：OBJ 出 <c>vn</c> 且面写成 <c>f i//i</c>；PLY 头里加 <c>nx/ny/nz</c> 三个属性；
/// **缩放只乘位置、不乘法线**（法线是方向，乘上比例再归一化等于没乘，原版也只乘位置）；
/// 翻转 Y/Z 对位置与法线**都**做 —— 只翻位置的话，法线就整体指错，导到 Y-up 的浏览器里全是背光面。
///
/// 两处按原版实况登记的差异：
///   · 原版格式下拉写「STL <b>Binary</b> (*.stl)」，但 <c>ExportToStl</c> 写的是 **ASCII**（facet normal /
///     outer loop 那套）。这里照**实现**走 ASCII，并把标签写成「STL ASCII」——照抄那个名字只会误导人。
///   · 原版 glTF 是带 <c>// TODO</c> 的**桩**：出一份 JSON 骨架，buffer 的 uri 是空 base64、连索引访问器都没有，
///     任何查看器都打不开。按"原版桩忠实不移"的规矩**不移**（同 SkeletonCommand 那 13 条），记录。
/// 纯逻辑、可单测。
/// </summary>
public static class MeshExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>导出选项（对应原版「导出选项」组的三项）。</summary>
    public sealed class Options
    {
        /// <summary>写出法线（OBJ 出 vn + f i//i；PLY 头加 nx/ny/nz）。</summary>
        public bool Normals;
        /// <summary>翻转 Y/Z 轴：Z-up(矿业/测绘) → Y-up(Blender/three.js 等)。位置与法线同翻。</summary>
        public bool FlipYZ;
        /// <summary>缩放因子，只乘位置。</summary>
        public double Scale = 1.0;

        public static readonly Options Default = new();
    }

    /// <summary>按选项变换一个位置点。</summary>
    private static (double x, double y, double z) Pos((double x, double y, double z) p, Options o)
    {
        var (x, y, z) = o.FlipYZ ? (p.x, p.z, p.y) : (p.x, p.y, p.z);
        return (x * o.Scale, y * o.Scale, z * o.Scale);
    }

    /// <summary>
    /// 翻 Y/Z 是一次**反射**（行列式 -1），会把三角绕向翻过来。所以位置一翻，面的顶点顺序就要跟着倒一次，
    /// 否则导出去的网在查看器里是"里朝外"的：法线朝内、背面剔除把正面全剔掉。
    /// 原版这一档不倒绕向，STL 里写的还是**未变换**的三角法向 —— 那份文件自己跟自己对不上，这里不照抄。
    /// </summary>
    private static (int a, int b, int c) Wind((int a, int b, int c) f, Options o)
        => o.FlipYZ ? (f.a, f.c, f.b) : f;

    /// <summary>按选项变换一个方向(法线)：只翻轴、不乘比例。</summary>
    private static (double x, double y, double z) Dir((double x, double y, double z) n, Options o)
        => o.FlipYZ ? (n.x, n.z, n.y) : n;

    /// <summary>
    /// 逐顶点法线：把每个三角的**面积加权**法向累加到它的三个顶点上再归一化。
    /// 面积加权（不归一化叉积再加）是标准做法 —— 等权会让一堆碎小三角把顶点法线拽偏。
    /// </summary>
    public static List<(double x, double y, double z)> VertexNormals(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var acc = new (double x, double y, double z)[v.Count];
        foreach (var f in t)
        {
            if (f.a < 0 || f.b < 0 || f.c < 0 || f.a >= v.Count || f.b >= v.Count || f.c >= v.Count) continue;
            var p0 = v[f.a]; var p1 = v[f.b]; var p2 = v[f.c];
            double ux = p1.x - p0.x, uy = p1.y - p0.y, uz = p1.z - p0.z;
            double wx = p2.x - p0.x, wy = p2.y - p0.y, wz = p2.z - p0.z;
            // 不归一化: 叉积长度 = 2×三角面积, 天然就是面积权
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            foreach (int i in new[] { f.a, f.b, f.c })
                acc[i] = (acc[i].x + nx, acc[i].y + ny, acc[i].z + nz);
        }
        var outN = new List<(double x, double y, double z)>(v.Count);
        foreach (var n in acc)
        {
            double len = Math.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
            outN.Add(len < 1e-15 ? (0, 0, 1) : (n.x / len, n.y / len, n.z / len));   // 孤立点给 +Z, 不给 NaN
        }
        return outN;
    }

    /// <summary>Wavefront OBJ：v x y z / f i j k(1 基索引)；开「导出法线」时出 vn 并把面写成 f i//i。</summary>
    public static string ToObj(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t,
                               Options? opt = null)
    {
        var o = opt ?? Options.Default;
        var sb = new StringBuilder();
        sb.Append("# PitMine3D.Kylin OBJ\n");
        sb.Append("# Vertices: ").Append(v.Count).Append(", Triangles: ").Append(t.Count).Append('\n');
        foreach (var p0 in v)
        {
            var p = Pos(p0, o);
            sb.Append("v ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
        }
        bool wantN = o.Normals && v.Count > 0;
        if (wantN)
            foreach (var n0 in VertexNormals(v, t))
            {
                var n = Dir(n0, o);
                sb.Append("vn ").Append(F(n.x)).Append(' ').Append(F(n.y)).Append(' ').Append(F(n.z)).Append('\n');
            }
        foreach (var f0 in t)
        {
            var f = Wind(f0, o);
            int i0 = f.a + 1, i1 = f.b + 1, i2 = f.c + 1;   // OBJ 1 基索引
            if (wantN)   // 逐顶点法线, 故法线索引 = 顶点索引
                sb.Append("f ").Append(i0).Append("//").Append(i0).Append(' ')
                  .Append(i1).Append("//").Append(i1).Append(' ')
                  .Append(i2).Append("//").Append(i2).Append('\n');
            else
                sb.Append("f ").Append(i0).Append(' ').Append(i1).Append(' ').Append(i2).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Stanford PLY(ASCII)：头 + 顶点 x y z [nx ny nz] + 面 "3 i j k"(0 基)。</summary>
    public static string ToPly(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t,
                               Options? opt = null)
    {
        var o = opt ?? Options.Default;
        bool wantN = o.Normals && v.Count > 0;
        var norms = wantN ? VertexNormals(v, t) : null;
        var sb = new StringBuilder();
        sb.Append("ply\nformat ascii 1.0\ncomment PitMine3D.Kylin\n");
        sb.Append("element vertex ").Append(v.Count).Append('\n');
        sb.Append("property float x\nproperty float y\nproperty float z\n");
        if (wantN) sb.Append("property float nx\nproperty float ny\nproperty float nz\n");
        sb.Append("element face ").Append(t.Count).Append('\n');
        sb.Append("property list uchar int vertex_indices\nend_header\n");
        for (int i = 0; i < v.Count; i++)
        {
            var p = Pos(v[i], o);
            sb.Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z));
            if (wantN)
            {
                var n = Dir(norms![i], o);
                sb.Append(' ').Append(F(n.x)).Append(' ').Append(F(n.y)).Append(' ').Append(F(n.z));
            }
            sb.Append('\n');
        }
        foreach (var f0 in t)
        {
            var f = Wind(f0, o);
            sb.Append("3 ").Append(f.a).Append(' ').Append(f.b).Append(' ').Append(f.c).Append('\n');   // PLY 0 基
        }
        return sb.ToString();
    }

    /// <summary>
    /// STL(ASCII)：逐三角 facet normal + outer loop 3 vertex。法向由三角右手定则算(与「导出法线」开关无关 ——
    /// STL 的法线是格式必需字段，不是可选项)。
    /// **原版格式下拉写的是「STL Binary」，但它的 ExportToStl 写的就是 ASCII** —— 这里照实现走。
    /// </summary>
    public static string ToStlAscii(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t,
                                    Options? opt = null)
    {
        var o = opt ?? Options.Default;
        var sb = new StringBuilder();
        sb.Append("solid PitMine3D_Kylin\n");
        foreach (var f in t)
        {
            if (f.a < 0 || f.b < 0 || f.c < 0 || f.a >= v.Count || f.b >= v.Count || f.c >= v.Count) continue;
            var fw = Wind(f, o);
            var p0 = Pos(v[fw.a], o); var p1 = Pos(v[fw.b], o); var p2 = Pos(v[fw.c], o);
            // 法向按**变换且倒过绕向**的三点算 —— 与 OBJ/PLY 的顶点法线口径一致(都指向同一侧)
            var (nx, ny, nz) = Normal(p0, p1, p2);
            sb.Append("  facet normal ").Append(F(nx)).Append(' ').Append(F(ny)).Append(' ').Append(F(nz)).Append('\n');
            sb.Append("    outer loop\n");
            V(sb, p0); V(sb, p1); V(sb, p2);
            sb.Append("    endloop\n  endfacet\n");
        }
        sb.Append("endsolid PitMine3D_Kylin\n");
        return sb.ToString();
    }

    /// <summary>Geomview OFF：顶点表 + 面表，double 全精度（原版格式说明的原话）。带导出选项。</summary>
    public static string ToOff(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t,
                               Options? opt = null)
    {
        var o = opt ?? Options.Default;
        var sb = new StringBuilder();
        sb.Append("OFF\n").Append(v.Count).Append(' ').Append(t.Count).Append(" 0\n");
        foreach (var p0 in v)
        {
            var p = Pos(p0, o);
            sb.Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
        }
        foreach (var f0 in t)
        {
            var f = Wind(f0, o);
            sb.Append("3 ").Append(f.a).Append(' ').Append(f.b).Append(' ').Append(f.c).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>按扩展名(.obj/.ply/.stl/.off)选格式导出; 未知回落 OBJ。</summary>
    public static string ByExtension(string ext,
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t,
        Options? opt = null) =>
        (ext ?? "").TrimStart('.').ToLowerInvariant() switch
        {
            "ply" => ToPly(v, t, opt),
            "stl" => ToStlAscii(v, t, opt),
            "off" => ToOff(v, t, opt),
            _ => ToObj(v, t, opt),
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
