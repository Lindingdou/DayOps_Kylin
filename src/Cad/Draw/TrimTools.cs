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
}
