using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网剔面 —— 忠实原 PointCloudLib「剔面(三角网): 按离地高/坡度丢弃三角面」。
/// 删陡面(点云空洞被 TIN 长边桥接出的假地面 / 障碍物竖壁)或高出基准面的三角。只删面不删点。
/// 坡度=面法向与竖直夹角(水平面 0°, 竖直面 90°)。纯逻辑、可单测。
/// </summary>
public static class MeshFaceCull
{
    /// <summary>保留坡度 ≤ maxSlopeDeg 的三角(删更陡的)。maxSlopeDeg∈[0,90]。</summary>
    public static List<(int a, int b, int c)> BySlope(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris, double maxSlopeDeg)
    {
        var kept = new List<(int a, int b, int c)>();
        if (verts == null || tris == null) return kept;
        double lim = Math.Clamp(maxSlopeDeg, 0, 90);
        foreach (var (a, b, c) in tris)
        {
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
            if (SlopeDeg(verts[a], verts[b], verts[c], out double s) && s <= lim + 1e-9) kept.Add((a, b, c));
        }
        return kept;
    }

    /// <summary>保留形心离基准面高 ≤ maxHeight 的三角(删高出的, 如障碍物顶)。datumZ 默认=最低顶点 z。</summary>
    public static List<(int a, int b, int c)> ByHeight(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris,
        double maxHeight, double? datumZ = null)
    {
        var kept = new List<(int a, int b, int c)>();
        if (verts == null || tris == null || verts.Count == 0) return kept;
        double datum = datumZ ?? MinZ(verts);
        foreach (var (a, b, c) in tris)
        {
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
            double cz = (verts[a].z + verts[b].z + verts[c].z) / 3.0;
            if (cz - datum <= maxHeight + 1e-9) kept.Add((a, b, c));
        }
        return kept;
    }

    /// <summary>
    /// 剔局部高Z倒刺（原 pc_remove_obs「剔除车辆/设备/植被/高Z倒刺」的孤立尖刺部分）：顶点 z 比其
    /// **最高**边邻接顶点还高出 &gt; heightTol 者判为孤立倒刺，删含倒刺顶点的三角。
    /// 用"高于最高邻居"而非中位/均值，才**坡度无关**（均匀斜坡上高处顶点其上坡邻居 ≥ 之，不误判）；
    /// 区别于 <see cref="ByHeight"/>(绝对基准, 坡上局部隆起漏检) 与 GroundFilter.LowestPerCell(整格抽最低, 抽稀)。
    /// 只删面不删点。纯逻辑、可单测。
    /// </summary>
    public static List<(int a, int b, int c)> BySpike(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris, double heightTol)
    {
        var kept = new List<(int a, int b, int c)>();
        if (verts == null || tris == null || verts.Count == 0) return kept;
        var nbr = new Dictionary<int, List<int>>();
        void Link(int u, int w) { if (!nbr.TryGetValue(u, out var l)) { l = new List<int>(); nbr[u] = l; } if (!l.Contains(w)) l.Add(w); }
        foreach (var (a, b, c) in tris)
        {
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
            Link(a, b); Link(b, a); Link(b, c); Link(c, b); Link(c, a); Link(a, c);
        }
        var spike = new bool[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            if (!nbr.TryGetValue(i, out var ns) || ns.Count == 0) continue;
            double maxNbrZ = double.MinValue;
            foreach (int j in ns) if (verts[j].z > maxNbrZ) maxNbrZ = verts[j].z;   // 最高邻居 z
            if (verts[i].z - maxNbrZ > heightTol) spike[i] = true;   // 高于最高邻居→孤立倒刺(坡度无关)
        }
        foreach (var (a, b, c) in tris)
        {
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
            if (spike[a] || spike[b] || spike[c]) continue;   // 含倒刺顶点的三角丢弃
            kept.Add((a, b, c));
        }
        return kept;
    }

    /// <summary>三角坡度(度)：面法向与竖直的夹角 = acos(|nz|/|n|)。退化三角返回 false。</summary>
    public static bool SlopeDeg((double x, double y, double z) p0, (double x, double y, double z) p1, (double x, double y, double z) p2, out double deg)
    {
        deg = 0;
        double e1x = p1.x - p0.x, e1y = p1.y - p0.y, e1z = p1.z - p0.z;
        double e2x = p2.x - p0.x, e2y = p2.y - p0.y, e2z = p2.z - p0.z;
        double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-15) return false;          // 退化(共线/零面积)
        double cos = Math.Abs(nz) / len;        // 法向与竖直夹角余弦
        if (cos > 1) cos = 1;
        deg = Math.Acos(cos) * 180.0 / Math.PI;
        return true;
    }

    private static double MinZ(IReadOnlyList<(double x, double y, double z)> v)
    {
        double m = double.MaxValue;
        foreach (var p in v) if (p.z < m) m = p.z;
        return m;
    }
}
