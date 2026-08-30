using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 修剪/延伸辅助 —— 多段线目标：把离点击较近的一端沿其末段方向移到与边界(任意实体镶嵌段)的最近交点。
/// 交点在末段内=修剪(缩短)，在末段延长线上=延伸(拉长)。纯逻辑、可单测。
/// </summary>
public static class TrimTools
{
    public static PolylineEntity? TrimExtendPolylineEnd(PolylineEntity target, SceneEntity boundary, double clickX, double clickY)
    {
        var pts = target.Points;
        if (pts.Count < 2) return null;

        double D2(double ax, double ay, double bx, double by) { double dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }
        bool atStart = D2(clickX, clickY, pts[0].x, pts[0].y) < D2(clickX, clickY, pts[^1].x, pts[^1].y);
        var end = atStart ? pts[0] : pts[^1];
        var adj = atStart ? pts[1] : pts[^2];

        var bo = new List<float>();
        boundary.Tessellate(bo);
        (double x, double y)? best = null; double bestD = double.MaxValue;
        for (int j = 0; j + 11 < bo.Count; j += 12)
        {
            var p = LineMath.IntersectInfiniteWithSegment(adj.x, adj.y, end.x, end.y, bo[j], bo[j + 1], bo[j + 6], bo[j + 7]);
            if (p == null) continue;
            double d = D2(p.Value.x, p.Value.y, end.x, end.y);
            if (d < bestD) { bestD = d; best = p; }
        }
        if (best == null) return null;

        var r = new PolylineEntity { Closed = target.Closed, Cr = target.Cr, Cg = target.Cg, Cb = target.Cb, LayerName = target.LayerName };
        for (int i = 0; i < pts.Count; i++)
        {
            if ((atStart && i == 0) || (!atStart && i == pts.Count - 1)) r.Points.Add((best.Value.x, best.Value.y));
            else r.Points.Add(pts[i]);
        }
        return r;
    }

    /// <summary>圆弧目标修剪/延伸：近点击端沿其外接圆移到与边界的最近圆-线交点（中点保持，故仍在同圆上）。</summary>
    public static ArcEntity? TrimExtendArc(ArcEntity a, SceneEntity boundary, double clickX, double clickY)
    {
        var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
        if (cc == null) return null;
        var (cx, cy, r) = cc.Value;

        var bo = new List<float>();
        boundary.Tessellate(bo);
        var hits = new List<(double x, double y)>();
        for (int j = 0; j + 11 < bo.Count; j += 12)
        {
            double sx0 = bo[j], sy0 = bo[j + 1], sx1 = bo[j + 6], sy1 = bo[j + 7];
            foreach (var p in LineMath.IntersectLineCircle(sx0, sy0, sx1, sy1, cx, cy, r))
            {
                double ddx = sx1 - sx0, ddy = sy1 - sy0, len2 = ddx * ddx + ddy * ddy;
                double t = len2 < 1e-12 ? 0 : ((p.x - sx0) * ddx + (p.y - sy0) * ddy) / len2;
                if (t >= -1e-9 && t <= 1 + 1e-9) hits.Add(p);   // 交点在边界段内
            }
        }
        if (hits.Count == 0) return null;

        double D2(double ax, double ay, double bx, double by) { double dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }
        bool moveStart = D2(clickX, clickY, a.X1, a.Y1) < D2(clickX, clickY, a.X3, a.Y3);
        double ex = moveStart ? a.X1 : a.X3, ey = moveStart ? a.Y1 : a.Y3;
        (double x, double y)? best = null; double bestD = double.MaxValue;
        foreach (var h in hits) { double d = D2(h.x, h.y, ex, ey); if (d < bestD) { bestD = d; best = h; } }
        if (best == null) return null;

        var r2 = new ArcEntity { X1 = a.X1, Y1 = a.Y1, X2 = a.X2, Y2 = a.Y2, X3 = a.X3, Y3 = a.Y3, Cr = a.Cr, Cg = a.Cg, Cb = a.Cb, LayerName = a.LayerName, Segments = a.Segments };
        if (moveStart) { r2.X1 = best.Value.x; r2.Y1 = best.Value.y; } else { r2.X3 = best.Value.x; r2.Y3 = best.Value.y; }
        return r2;
    }
}

