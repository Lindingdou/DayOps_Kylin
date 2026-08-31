using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网(TIN)竖直求交采高 —— 在 (qx,qy) 竖直线与三角网面求交，重心插值得高程 Z。
/// 对应原 GeoDataBase「煤层顶/底板三角网竖直求交算高程」的纯几何核（原吃内核存库 TIN，此吃点集+三角）。
/// 三角由 <see cref="Delaunay.Triangulate"/> 得(2D 索引)，Z 取自各顶点。纯逻辑、可单测。
/// </summary>
public static class TinSampler
{
    /// <summary>在 (qx,qy) 处对三角网采高：命中含该点的三角形→重心插值 Z；无命中返回 null。</summary>
    public static double? SampleZ(IReadOnlyList<(double x, double y, double z)> pts, IReadOnlyList<(int a, int b, int c)> tris, double qx, double qy, double eps = 1e-9)
    {
        foreach (var (ia, ib, ic) in tris)
        {
            if (ia < 0 || ib < 0 || ic < 0 || ia >= pts.Count || ib >= pts.Count || ic >= pts.Count) continue;
            var p0 = pts[ia]; var p1 = pts[ib]; var p2 = pts[ic];
            double denom = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
            if (Math.Abs(denom) < 1e-15) continue;   // 退化三角
            double u = ((p1.y - p2.y) * (qx - p2.x) + (p2.x - p1.x) * (qy - p2.y)) / denom;
            double v = ((p2.y - p0.y) * (qx - p2.x) + (p0.x - p2.x) * (qy - p2.y)) / denom;
            double w = 1 - u - v;
            if (u >= -eps && v >= -eps && w >= -eps)   // 点在三角内(含边)
                return u * p0.z + v * p1.z + w * p2.z;
        }
        return null;   // 落在三角网外
    }

    /// <summary>便捷：对点集自动三角剖分后采高。点少于 3 或落网外返回 null。</summary>
    public static double? SampleZ(IReadOnlyList<(double x, double y, double z)> pts, double qx, double qy)
    {
        if (pts.Count < 3) return null;
        var xy = new List<(double x, double y)>(pts.Count);
        foreach (var p in pts) xy.Add((p.x, p.y));
        var tris = Delaunay.Triangulate(xy);
        return SampleZ(pts, tris, qx, qy);
    }
}
