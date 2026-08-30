using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>面积/周长测量 —— 鞋带公式面积 + 折线周长。纯逻辑、可单测。</summary>
public static class GeomMeasure
{
    /// <summary>多边形面积（鞋带公式，取绝对值，与绕向无关）。</summary>
    public static double Area(IReadOnlyList<(double x, double y)> poly)
    {
        int n = poly.Count;
        if (n < 3) return 0;
        double s = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
            s += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
        return Math.Abs(s) * 0.5;
    }

    /// <summary>折线周长；closed=true 时含闭合段。</summary>
    public static double Perimeter(IReadOnlyList<(double x, double y)> pts, bool closed)
    {
        double p = 0;
        for (int i = 0; i + 1 < pts.Count; i++) p += Dist(pts[i], pts[i + 1]);
        if (closed && pts.Count > 1) p += Dist(pts[^1], pts[0]);
        return p;
    }

    private static double Dist((double x, double y) a, (double x, double y) b)
    {
        double dx = a.x - b.x, dy = a.y - b.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
