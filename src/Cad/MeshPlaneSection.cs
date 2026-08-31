using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网竖直剖面 —— 网格 ∩ 过剖面线的竖直平面 → 精确剖面折线(沿线距 → 高程 z)。
/// 忠实原 MeshEditLib「沿剖面线切三角网为剖面」的几何核。区别于点采样剖面(剖面分析)：
/// 用三角面精确求交，得曲面真实断面。纯逻辑、可单测。
/// </summary>
public static class MeshPlaneSection
{
    /// <summary>沿剖面线 (p0→p1) 竖直切网格，返回按沿线距排序的 (dist, z) 剖面点。无交返回空。</summary>
    public static List<(double dist, double z)> Profile(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t,
        (double x, double y) p0, (double x, double y) p1)
    {
        var prof = new List<(double dist, double z)>();
        double dxl = p1.x - p0.x, dyl = p1.y - p0.y;
        double llen = Math.Sqrt(dxl * dxl + dyl * dyl);
        if (llen < 1e-9 || v == null || t == null) return prof;
        double ux = dxl / llen, uy = dyl / llen;      // 剖面线单位方向(平面内)
        double nx = -uy, ny = ux;                       // 竖直平面水平法向(垂直于剖面线)

        double SignedDist((double x, double y, double z) q) => (q.x - p0.x) * nx + (q.y - p0.y) * ny;
        double AlongDist(double x, double y) => (x - p0.x) * ux + (y - p0.y) * uy;

        foreach (var tri in t)
        {
            var A = v[tri.a]; var B = v[tri.b]; var C = v[tri.c];
            double da = SignedDist(A), db = SignedDist(B), dc = SignedDist(C);
            // 全在一侧(不含面)→ 不切
            if ((da > 1e-12 && db > 1e-12 && dc > 1e-12) || (da < -1e-12 && db < -1e-12 && dc < -1e-12)) continue;
            var hits = new List<(double x, double y, double z)>(2);
            void Edge((double x, double y, double z) u, (double x, double y, double z) w, double du, double dw)
            { if ((du > 0) != (dw > 0) && Math.Abs(du - dw) > 1e-15) { double s = du / (du - dw); hits.Add((u.x + s * (w.x - u.x), u.y + s * (w.y - u.y), u.z + s * (w.z - u.z))); } }
            Edge(A, B, da, db); Edge(B, C, db, dc); Edge(C, A, dc, da);
            foreach (var h in hits) prof.Add((AlongDist(h.x, h.y), h.z));
        }
        prof.Sort((a, b) => a.dist.CompareTo(b.dist));
        // 去重(相邻近距近高的重复点，三角共享边产生)
        var dedup = new List<(double dist, double z)>(prof.Count);
        foreach (var pt in prof)
            if (dedup.Count == 0 || Math.Abs(pt.dist - dedup[^1].dist) > 1e-7 || Math.Abs(pt.z - dedup[^1].z) > 1e-7)
                dedup.Add(pt);
        return dedup;
    }
}
