using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 多段线简化（Douglas-Peucker）—— 在容差 eps 内减少顶点、保留形状。纯逻辑、可单测。
/// </summary>
public static class PolylineSimplify
{
    public static List<(double x, double y)> DouglasPeucker(IReadOnlyList<(double x, double y)> pts, double eps)
    {
        int n = pts.Count;
        if (n < 3 || eps <= 0)
        {
            var copy = new List<(double x, double y)>(); copy.AddRange(pts); return copy;
        }
        var keep = new bool[n];
        keep[0] = true; keep[n - 1] = true;
        Recurse(pts, 0, n - 1, eps, keep);
        var res = new List<(double x, double y)>();
        for (int i = 0; i < n; i++) if (keep[i]) res.Add(pts[i]);
        return res;
    }

    private static void Recurse(IReadOnlyList<(double x, double y)> pts, int i, int j, double eps, bool[] keep)
    {
        if (j <= i + 1) return;
        double maxD = 0; int idx = -1;
        var a = pts[i]; var b = pts[j];
        double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
        for (int k = i + 1; k < j; k++)
        {
            var p = pts[k];
            double d = len < 1e-12
                ? Math.Sqrt((p.x - a.x) * (p.x - a.x) + (p.y - a.y) * (p.y - a.y))
                : Math.Abs(dx * (p.y - a.y) - dy * (p.x - a.x)) / len;   // 点到直线垂距
            if (d > maxD) { maxD = d; idx = k; }
        }
        if (maxD > eps && idx > 0)
        {
            keep[idx] = true;
            Recurse(pts, i, idx, eps, keep);
            Recurse(pts, idx, j, eps, keep);
        }
    }
}
