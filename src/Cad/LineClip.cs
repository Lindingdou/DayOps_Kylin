using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 用闭合边界多边形裁剪线对象(开放/闭合多段线) —— 保留落在界内(或界外)的线段, 断成若干段。
/// 忠实原 MeshEdit「用闭合多段线裁剪其它线对象(POLYCLIP)」。区别于 PolygonClip(多边形∩凸包):
/// 此吃开放线、支持非凸边界(逐点 PointInPolygon)、可保内或保外。纯 2D 逻辑、可单测。
/// </summary>
public static class LineClip
{
    /// <summary>
    /// 裁剪。line=被裁多段线(closed 指是否闭合), boundary=闭合边界多边形, keepInside=保内(true)/保外(false)。
    /// 返回若干段(各为点串, ≥2 点); 每段在同一侧连续。
    /// </summary>
    public static List<List<(double x, double y)>> ByPolygon(
        IReadOnlyList<(double x, double y)> line, bool closed,
        IReadOnlyList<(double x, double y)> boundary, bool keepInside)
    {
        var pieces = new List<List<(double x, double y)>>();
        if (line == null || line.Count < 2 || boundary == null || boundary.Count < 3) return pieces;

        // 1) 沿多段线构造「增广点序」：各段起点 + 与边界的交点(按 t 升序) + …，末端补终点。
        var seq = new List<(double x, double y)>();
        seq.Add(line[0]);
        int segCount = closed ? line.Count : line.Count - 1;
        for (int i = 0; i < segCount; i++)
        {
            var a = line[i];
            var b = line[(i + 1) % line.Count];
            var ts = new List<double>();
            for (int j = 0; j < boundary.Count; j++)
            {
                var c = boundary[j];
                var d = boundary[(j + 1) % boundary.Count];
                if (SegCross(a, b, c, d, out double t)) ts.Add(t);
            }
            ts.Sort();
            foreach (var t in ts)
                seq.Add((a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t));
            seq.Add(b);
        }

        // 2) 连续走增广点序：每相邻对取中点判内外, 同侧则续入当前段, 翻面则收段。
        var current = new List<(double x, double y)>();
        for (int k = 0; k + 1 < seq.Count; k++)
        {
            var p = seq[k]; var q = seq[k + 1];
            double mx = (p.x + q.x) / 2, my = (p.y + q.y) / 2;
            bool inside = LineMath.PointInPolygon(mx, my, boundary);
            if (inside == keepInside)
            {
                if (current.Count == 0) current.Add(p);
                current.Add(q);
            }
            else
            {
                if (current.Count >= 2) pieces.Add(current);
                current = new List<(double x, double y)>();
            }
        }
        if (current.Count >= 2) pieces.Add(current);
        return pieces;
    }

    /// <summary>段[a,b]与段[c,d]相交且交点严格在 [a,b] 内部(t∈(0,1))、落在 [c,d] 上(u∈[0,1])→ out t。</summary>
    private static bool SegCross((double x, double y) a, (double x, double y) b,
        (double x, double y) c, (double x, double y) d, out double t)
    {
        t = 0;
        double rx = b.x - a.x, ry = b.y - a.y, sx = d.x - c.x, sy = d.y - c.y;
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-12) return false;        // 平行/退化
        double qpx = c.x - a.x, qpy = c.y - a.y;
        double tt = (qpx * sy - qpy * sx) / denom;
        double uu = (qpx * ry - qpy * rx) / denom;
        if (tt > 1e-9 && tt < 1 - 1e-9 && uu >= -1e-9 && uu <= 1 + 1e-9) { t = tt; return true; }
        return false;
    }
}
