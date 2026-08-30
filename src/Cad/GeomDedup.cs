using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 去重纯几何核（对应原 CAD 端 POINTDEDUPE / POLYDEDUPE 算子，原走内核, 此为托管重算）：
/// 点集按容差去重(首现保留)、两折线是否同一几何(等长且正/反向逐点重合)。纯函数、可单测。
/// </summary>
public static class GeomDedup
{
    /// <summary>点集按容差去重, 返回保留的下标(首现优先)。</summary>
    public static List<int> KeepAfterDedup(IReadOnlyList<(double x, double y)> pts, double tol)
    {
        var keep = new List<int>();
        if (pts == null) return keep;
        double t2 = tol * tol;
        var kept = new List<(double x, double y)>();
        for (int i = 0; i < pts.Count; i++)
        {
            bool dup = false;
            foreach (var q in kept)
            {
                double dx = q.x - pts[i].x, dy = q.y - pts[i].y;
                if (dx * dx + dy * dy <= t2) { dup = true; break; }
            }
            if (!dup) { keep.Add(i); kept.Add(pts[i]); }
        }
        return keep;
    }

    /// <summary>两折线是否同一几何：等长、闭合标记一致, 且正向或反向逐点在容差内重合。</summary>
    public static bool SamePolyline(
        IReadOnlyList<(double x, double y)> a, bool aClosed,
        IReadOnlyList<(double x, double y)> b, bool bClosed, double tol)
    {
        if (a == null || b == null || a.Count != b.Count || aClosed != bClosed) return false;
        int n = a.Count;
        double t2 = tol * tol;
        bool Match(Func<int, int> map)
        {
            for (int k = 0; k < n; k++)
            {
                var p = a[k]; var q = b[map(k)];
                double dx = p.x - q.x, dy = p.y - q.y;
                if (dx * dx + dy * dy > t2) return false;
            }
            return true;
        }
        return Match(k => k) || Match(k => n - 1 - k);   // 正向 或 反向
    }
}
