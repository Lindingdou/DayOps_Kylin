using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 多段线等分 —— 定数等分(DIVIDE)按弧长均分放 n−1 个内部点；定距等分(MEASURE)每隔 d 放点。纯逻辑、可单测。
/// </summary>
public static class PolylineDivide
{
    private static (double[] cum, double total) Lengths(IReadOnlyList<(double x, double y)> pts)
    {
        var cum = new double[pts.Count];
        double total = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].x - pts[i - 1].x, dy = pts[i].y - pts[i - 1].y;
            total += Math.Sqrt(dx * dx + dy * dy);
            cum[i] = total;
        }
        return (cum, total);
    }

    private static (double x, double y) At(IReadOnlyList<(double x, double y)> pts, double[] cum, double s)
    {
        for (int i = 1; i < pts.Count; i++)
        {
            if (s <= cum[i] || i == pts.Count - 1)
            {
                double seg = cum[i] - cum[i - 1];
                double t = seg < 1e-12 ? 0 : (s - cum[i - 1]) / seg;
                t = Math.Clamp(t, 0, 1);
                return (pts[i - 1].x + (pts[i].x - pts[i - 1].x) * t, pts[i - 1].y + (pts[i].y - pts[i - 1].y) * t);
            }
        }
        return pts[^1];
    }

    /// <summary>定数等分：n 段 → n−1 个内部分点。</summary>
    public static List<(double x, double y)> Divide(IReadOnlyList<(double x, double y)> pts, int n)
    {
        var res = new List<(double x, double y)>();
        if (pts.Count < 2 || n < 2) return res;
        var (cum, total) = Lengths(pts);
        if (total < 1e-9) return res;
        for (int k = 1; k < n; k++) res.Add(At(pts, cum, total * k / n));
        return res;
    }

    /// <summary>定距等分：每隔 d 放点(不含起点)。</summary>
    public static List<(double x, double y)> Measure(IReadOnlyList<(double x, double y)> pts, double d)
    {
        var res = new List<(double x, double y)>();
        if (pts.Count < 2 || d <= 1e-9) return res;
        var (cum, total) = Lengths(pts);
        for (double s = d; s <= total + 1e-9; s += d) res.Add(At(pts, cum, s));
        return res;
    }
}
