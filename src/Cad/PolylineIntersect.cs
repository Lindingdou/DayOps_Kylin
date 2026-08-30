using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 两线交点纯几何核（对应原 CAD 端 POLYINTERSECT 算子，原走内核, 此为托管重算）：
/// 求两条折线(/线段)各段两两的真交点(含端点接触), 平行/共线跳过, 按容差去重。纯函数、可单测。
/// </summary>
public static class PolylineIntersect
{
    /// <summary>线段 p1p2 与 p3p4 的真交点(参数均在 [0,1])；平行/共线返回 null。</summary>
    internal static (double x, double y)? SegSeg(
        (double x, double y) p1, (double x, double y) p2, (double x, double y) p3, (double x, double y) p4)
    {
        double rx = p2.x - p1.x, ry = p2.y - p1.y;
        double sx = p4.x - p3.x, sy = p4.y - p3.y;
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-12) return null;          // 平行或退化
        double qpx = p3.x - p1.x, qpy = p3.y - p1.y;
        double t = (qpx * sy - qpy * sx) / denom;
        double u = (qpx * ry - qpy * rx) / denom;
        const double eps = 1e-9;
        if (t < -eps || t > 1 + eps || u < -eps || u > 1 + eps) return null;
        return (p1.x + t * rx, p1.y + t * ry);
    }

    /// <summary>折线 a 与折线 b 所有段两两交点, 按 tol 去重。</summary>
    public static List<(double x, double y)> Between(
        IReadOnlyList<(double x, double y)> a, bool aClosed,
        IReadOnlyList<(double x, double y)> b, bool bClosed, double tol)
    {
        var pts = new List<(double x, double y)>();
        if (a == null || b == null || a.Count < 2 || b.Count < 2) return pts;
        int na = a.Count, nb = b.Count;
        int sa = aClosed ? na : na - 1, sb = bClosed ? nb : nb - 1;
        for (int i = 0; i < sa; i++)
        {
            var a0 = a[i]; var a1 = a[(i + 1) % na];
            for (int j = 0; j < sb; j++)
            {
                var b0 = b[j]; var b1 = b[(j + 1) % nb];
                var hit = SegSeg(a0, a1, b0, b1);
                if (hit.HasValue) AddUnique(pts, hit.Value, tol);
            }
        }
        return pts;
    }

    private static void AddUnique(List<(double x, double y)> list, (double x, double y) p, double tol)
    {
        double t2 = tol * tol;
        foreach (var q in list)
        {
            double dx = q.x - p.x, dy = q.y - p.y;
            if (dx * dx + dy * dy <= t2) return;
        }
        list.Add(p);
    }
}
