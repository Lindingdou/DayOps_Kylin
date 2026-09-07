using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 闭合线裁剪（原 MeshEditLib「线编辑 → 闭合线裁剪」POLYCLIP 与「面编辑 → 删除三角面」的平面判定核）：
/// 多段线按闭合多边形裁剪, 保留内侧或外侧部分(在多边形边处精确打断, z 按参数插值); 三角形按质心是否在多边形内选取。
/// 多边形可凹(射线法), 与 PolygonClip(凸多边形 Sutherland-Hodgman 求交集) 分工不同。
/// </summary>
public static class PolylineClipper
{
    /// <summary>点在多边形内(射线法, 边上算内)。</summary>
    public static bool Contains(IReadOnlyList<(double x, double y)> poly, double x, double y)
    {
        bool inside = false; int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if (OnSegment(a, b, x, y)) return true;
            if ((a.y > y) != (b.y > y))
            {
                double xi = a.x + (y - a.y) * (b.x - a.x) / (b.y - a.y);
                if (x < xi) inside = !inside;
            }
        }
        return inside;
    }

    private static bool OnSegment((double x, double y) a, (double x, double y) b, double x, double y)
    {
        double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
        if (l2 < 1e-18) return Math.Abs(x - a.x) < 1e-9 && Math.Abs(y - a.y) < 1e-9;
        double t = ((x - a.x) * dx + (y - a.y) * dy) / l2;
        if (t < -1e-9 || t > 1 + 1e-9) return false;
        double px = a.x + t * dx, py = a.y + t * dy;
        return (px - x) * (px - x) + (py - y) * (py - y) < 1e-14 * Math.Max(1, l2);
    }

    /// <summary>线段 (p,q) 与多边形各边的交点参数 t(开区间 0..1)，升序去重。</summary>
    public static List<double> EdgeCrossings(IReadOnlyList<(double x, double y)> poly, (double x, double y) p, (double x, double y) q)
    {
        var ts = new List<double>();
        int n = poly.Count;
        double rx = q.x - p.x, ry = q.y - p.y;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = poly[j]; var b = poly[i];
            double sx = b.x - a.x, sy = b.y - a.y;
            double den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-15) continue;
            double t = ((a.x - p.x) * sy - (a.y - p.y) * sx) / den;
            double u = ((a.x - p.x) * ry - (a.y - p.y) * rx) / den;
            if (t > 1e-9 && t < 1 - 1e-9 && u >= -1e-9 && u <= 1 + 1e-9) ts.Add(t);
        }
        ts.Sort();
        var res = new List<double>();
        foreach (var t in ts) if (res.Count == 0 || t - res[^1] > 1e-9) res.Add(t);
        return res;
    }

    /// <summary>
    /// 把一条多段线(可带逐点 z)按多边形裁剪, 返回保留侧的若干子线(每条 ≥2 点)。keepInside=true 留内侧。
    /// 每段在与多边形边的交点处切成子段, 子段按中点归属判定, 相邻保留子段接成一条。
    /// </summary>
    public static List<List<(double x, double y, double z)>> ClipPolyline(
        IReadOnlyList<(double x, double y)> poly, IReadOnlyList<(double x, double y, double z)> line, bool closed, bool keepInside)
    {
        var pieces = new List<List<(double x, double y, double z)>>();
        if (line.Count < 2 || poly.Count < 3) return pieces;
        int n = line.Count, segs = closed ? n : n - 1;
        List<(double x, double y, double z)>? cur = null;
        void Keep((double x, double y, double z) a, (double x, double y, double z) b)
        {
            if (cur != null && Dist2(cur[^1], a) < 1e-18) cur.Add(b);
            else { if (cur != null && cur.Count >= 2) pieces.Add(cur); cur = new List<(double x, double y, double z)> { a, b }; }
        }
        void Drop() { if (cur != null && cur.Count >= 2) pieces.Add(cur); cur = null; }
        for (int s = 0; s < segs; s++)
        {
            var p = line[s]; var q = line[(s + 1) % n];
            var ts = EdgeCrossings(poly, (p.x, p.y), (q.x, q.y));
            double t0 = 0;
            for (int k = 0; k <= ts.Count; k++)
            {
                double t1 = k < ts.Count ? ts[k] : 1;
                var a = Lerp(p, q, t0); var b = Lerp(p, q, t1);
                var mid = Lerp(p, q, (t0 + t1) / 2);
                bool keep = Contains(poly, mid.x, mid.y) == keepInside;
                if (keep) Keep(a, b); else Drop();
                t0 = t1;
            }
        }
        Drop();
        return pieces;
    }

    private static (double x, double y, double z) Lerp((double x, double y, double z) p, (double x, double y, double z) q, double t)
        => (p.x + (q.x - p.x) * t, p.y + (q.y - p.y) * t, p.z + (q.z - p.z) * t);
    private static double Dist2((double x, double y, double z) a, (double x, double y, double z) b)
        => (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y);

    /// <summary>质心落在多边形内的三角索引集合。</summary>
    public static List<int> TrianglesInside(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris, IReadOnlyList<(double x, double y)> poly)
    {
        var res = new List<int>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            if (a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
            double cx = (verts[a].x + verts[b].x + verts[c].x) / 3, cy = (verts[a].y + verts[b].y + verts[c].y) / 3;
            if (Contains(poly, cx, cy)) res.Add(i);
        }
        return res;
    }

    /// <summary>删除给定三角后压缩未引用顶点。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) RemoveTriangles(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris, IEnumerable<int> remove)
    {
        var rm = new HashSet<int>(remove);
        var kept = new List<(int a, int b, int c)>();
        for (int i = 0; i < tris.Count; i++) if (!rm.Contains(i)) kept.Add(tris[i]);
        var map = new int[verts.Count]; Array.Fill(map, -1);
        var nv = new List<(double x, double y, double z)>();
        var nt = new List<(int a, int b, int c)>(kept.Count);
        int Map(int i) { if (map[i] < 0) { map[i] = nv.Count; nv.Add(verts[i]); } return map[i]; }
        foreach (var (a, b, c) in kept) nt.Add((Map(a), Map(b), Map(c)));
        return (nv, nt);
    }
}
