using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>网格焊接结果。</summary>
public sealed class MeshWeldResult
{
    public List<(double x, double y, double z)> Verts = new();
    public List<(int a, int b, int c)> Tris = new();
    public int InputVerts;         // 焊接前顶点
    public int InputTris;          // 输入合法三角
    public int DroppedDegenerate;  // 焊后三点塌成线/点而丢弃的三角
    public int DuplicateTris;      // 顶点三元组重合而丢弃(仅 dropDuplicateTris=true)
    public int OutputVerts => Verts.Count;
    public int OutputTris => Tris.Count;
}

/// <summary>
/// 网格顶点焊接（忠实移植原 <c>MeshEditLib.Tools.MeshWeldMerge.Weld</c> 空间哈希核）——
/// 按容差把重合顶点合并为同一个：格边长 H=2×容差、每点只探 8 邻格(数学完备, 比 27 邻域省 ~3.4×)，
/// 重映射三角索引、剔除焊后退化三角、可选去掉完全重合(不分缠绕)的三角。纯几何、可单测。
/// </summary>
public static class MeshWeld
{
    public static MeshWeldResult Weld(IReadOnlyList<(double x, double y, double z)> verts,
        IReadOnlyList<(int a, int b, int c)> tris, double tolerance, bool dropDuplicateTris = false)
    {
        var result = new MeshWeldResult();
        if (verts == null || tris == null || verts.Count == 0) return result;
        result.InputVerts = verts.Count;

        double tol = tolerance > 1e-9 ? tolerance : 1e-9;
        double tol2 = tol * tol;

        var outVerts = new List<(double x, double y, double z)>(verts.Count);
        var outTris = new List<(int, int, int)>(tris.Count);
        var seenTris = dropDuplicateTris ? new HashSet<(int, int, int)>(tris.Count) : null;

        // 空间哈希：格边长 H=2×tol → 半径 tol 的球至多跨 2 格/轴；每点探本格 + 偏向侧邻格(8 格)。
        double H = 2.0 * tol, invH = 1.0 / H;
        var grid = new Dictionary<(long, long, long), List<int>>(verts.Count);

        int WeldVertex(double x, double y, double z)
        {
            double fx = x * invH, fy = y * invH, fz = z * invH;
            long cx = (long)Math.Floor(fx), cy = (long)Math.Floor(fy), cz = (long)Math.Floor(fz);
            long nx = (fx - cx) < 0.5 ? cx - 1 : cx + 1;
            long ny = (fy - cy) < 0.5 ? cy - 1 : cy + 1;
            long nz = (fz - cz) < 0.5 ? cz - 1 : cz + 1;
            for (int ai = 0; ai < 2; ai++)
            {
                long gx = ai == 0 ? cx : nx;
                for (int aj = 0; aj < 2; aj++)
                {
                    long gy = aj == 0 ? cy : ny;
                    for (int ak = 0; ak < 2; ak++)
                    {
                        long gz = ak == 0 ? cz : nz;
                        if (!grid.TryGetValue((gx, gy, gz), out var bucket)) continue;
                        foreach (int ri in bucket)
                        {
                            var rp = outVerts[ri];
                            double ex = rp.x - x, ey = rp.y - y, ez = rp.z - z;
                            if (ex * ex + ey * ey + ez * ez <= tol2) return ri;
                        }
                    }
                }
            }
            int ni = outVerts.Count;
            outVerts.Add((x, y, z));
            var home = (cx, cy, cz);
            if (!grid.TryGetValue(home, out var lst)) grid[home] = lst = new List<int>(1);
            lst.Add(ni);
            return ni;
        }

        int vCount = verts.Count;
        var local2global = new int[vCount];
        for (int j = 0; j < vCount; j++)
            local2global[j] = WeldVertex(verts[j].x, verts[j].y, verts[j].z);

        foreach (var (a, b, c) in tris)
        {
            if (a < 0 || b < 0 || c < 0 || a >= vCount || b >= vCount || c >= vCount) continue;
            result.InputTris++;
            int ga = local2global[a], gb = local2global[b], gc = local2global[c];
            if (ga == gb || gb == gc || ga == gc) { result.DroppedDegenerate++; continue; }  // 焊后塌陷

            if (seenTris != null)
            {
                // 升序三元组：同顶点不论缠绕都判同一三角
                int s0 = ga, s1 = gb, s2 = gc;
                if (s0 > s1) (s0, s1) = (s1, s0);
                if (s1 > s2) (s1, s2) = (s2, s1);
                if (s0 > s1) (s0, s1) = (s1, s0);
                if (!seenTris.Add((s0, s1, s2))) { result.DuplicateTris++; continue; }
            }
            outTris.Add((ga, gb, gc));
        }

        result.Verts = outVerts;
        result.Tris = outTris;
        return result;
    }

    /// <summary>把 (verts, tris) 序列化为 OFF 文本（供焊接结果落盘）。</summary>
    public static string ToOff(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("OFF\n").Append(verts.Count).Append(' ').Append(tris.Count).Append(" 0\n");
        foreach (var v in verts)
            sb.Append(v.x.ToString("R", inv)).Append(' ').Append(v.y.ToString("R", inv)).Append(' ').Append(v.z.ToString("R", inv)).Append('\n');
        foreach (var (a, b, c) in tris)
            sb.Append("3 ").Append(a).Append(' ').Append(b).Append(' ').Append(c).Append('\n');
        return sb.ToString();
    }
}
