using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 两三角网求交线 —— 逐三角对求 三角∩三角 交段(标准几何: 各三角与对方所在平面求弦，两弦同在
/// 平面交线 L 上取区间重叠)。忠实原 MeshEditLib「求两个三角网的交线」的几何核(标准 tri-tri 相交，
/// 非变体敏感，可托管重算)。典型用途：现状面∩煤层顶/底板 → 煤层露头线。纯逻辑、可单测。
/// </summary>
public static class MeshIntersect
{
    public readonly record struct Seg((double x, double y, double z) A, (double x, double y, double z) B);

    /// <summary>两网(顶点+三角索引)所有交段。含 AABB 预筛加速；退化/共面三角跳过。</summary>
    public static List<Seg> IntersectionSegments(
        IReadOnlyList<(double x, double y, double z)> v1, IReadOnlyList<(int a, int b, int c)> t1,
        IReadOnlyList<(double x, double y, double z)> v2, IReadOnlyList<(int a, int b, int c)> t2)
    {
        var segs = new List<Seg>();
        // 预算 mesh2 各三角 AABB
        var box2 = new (double x0, double y0, double z0, double x1, double y1, double z1)[t2.Count];
        for (int j = 0; j < t2.Count; j++) box2[j] = TriBox(v2, t2[j]);
        foreach (var ta in t1)
        {
            var ba = TriBox(v1, ta);
            for (int j = 0; j < t2.Count; j++)
            {
                var bb = box2[j];
                if (ba.x1 < bb.x0 || bb.x1 < ba.x0 || ba.y1 < bb.y0 || bb.y1 < ba.y0 || ba.z1 < bb.z0 || bb.z1 < ba.z0) continue;   // AABB 不交
                if (TriTri(v1[ta.a], v1[ta.b], v1[ta.c], v2[t2[j].a], v2[t2[j].b], v2[t2[j].c], out var s)) segs.Add(s);
            }
        }
        return segs;
    }

    private static (double x0, double y0, double z0, double x1, double y1, double z1) TriBox(
        IReadOnlyList<(double x, double y, double z)> v, (int a, int b, int c) t)
    {
        var A = v[t.a]; var B = v[t.b]; var C = v[t.c];
        return (Math.Min(A.x, Math.Min(B.x, C.x)), Math.Min(A.y, Math.Min(B.y, C.y)), Math.Min(A.z, Math.Min(B.z, C.z)),
                Math.Max(A.x, Math.Max(B.x, C.x)), Math.Max(A.y, Math.Max(B.y, C.y)), Math.Max(A.z, Math.Max(B.z, C.z)));
    }

    // 三角 A(a0,a1,a2) ∩ 三角 B(b0,b1,b2) → 交段。无交/共面/退化 → false。
    private static bool TriTri(
        (double x, double y, double z) a0, (double x, double y, double z) a1, (double x, double y, double z) a2,
        (double x, double y, double z) b0, (double x, double y, double z) b1, (double x, double y, double z) b2,
        out Seg seg)
    {
        seg = default;
        var nB = Cross(Sub(b1, b0), Sub(b2, b0));
        var nA = Cross(Sub(a1, a0), Sub(a2, a0));
        // A 各顶点到 平面B 的符号距离
        double da0 = Dot(nB, Sub(a0, b0)), da1 = Dot(nB, Sub(a1, b0)), da2 = Dot(nB, Sub(a2, b0));
        if (SameSide(da0, da1, da2)) return false;                 // A 全在 平面B 一侧
        double db0 = Dot(nA, Sub(b0, a0)), db1 = Dot(nA, Sub(b1, a0)), db2 = Dot(nA, Sub(b2, a0));
        if (SameSide(db0, db1, db2)) return false;                 // B 全在 平面A 一侧
        var D = Cross(nA, nB);                                       // 交线方向
        double dl = Math.Sqrt(Dot(D, D));
        if (dl < 1e-15) return false;                               // 近共面
        D = (D.x / dl, D.y / dl, D.z / dl);
        // A 与 平面B 的弦(A 边跨平面B 的两交点), 投到 D 得区间；同理 B 与 平面A
        if (!ChordInterval(a0, a1, a2, da0, da1, da2, D, out double aMin, out double aMax, out var aPmin, out var aPmax)) return false;
        if (!ChordInterval(b0, b1, b2, db0, db1, db2, D, out double bMin, out double bMax, out var bPmin, out var bPmax)) return false;
        double lo = Math.Max(aMin, bMin), hi = Math.Min(aMax, bMax);
        if (lo >= hi - 1e-12) return false;                        // 区间不重叠 → 无实交段
        // 交段端点 = 沿 D 在 [lo,hi] 处（用 A 弦端点线性定位）
        (double x, double y, double z) P(double t)
        {
            double s = (t - aMin) / (aMax - aMin);
            return (aPmin.x + s * (aPmax.x - aPmin.x), aPmin.y + s * (aPmax.y - aPmin.y), aPmin.z + s * (aPmax.z - aPmin.z));
        }
        seg = new Seg(P(lo), P(hi));
        return true;
    }

    // 三角(p0,p1,p2) 与其对方平面的弦：取符号距 d 异号的两边交点，投到方向 D 得 [min,max] 及对应端点。
    private static bool ChordInterval(
        (double x, double y, double z) p0, (double x, double y, double z) p1, (double x, double y, double z) p2,
        double d0, double d1, double d2, (double x, double y, double z) D,
        out double tMin, out double tMax, out (double x, double y, double z) pMin, out (double x, double y, double z) pMax)
    {
        tMin = tMax = 0; pMin = pMax = default;
        var pts = new List<(double x, double y, double z)>(2);
        void Edge((double x, double y, double z) u, (double x, double y, double z) v, double du, double dv)
        { if ((du > 0) != (dv > 0)) { double s = du / (du - dv); pts.Add((u.x + s * (v.x - u.x), u.y + s * (v.y - u.y), u.z + s * (v.z - u.z))); } }
        Edge(p0, p1, d0, d1); Edge(p1, p2, d1, d2); Edge(p2, p0, d2, d0);
        if (pts.Count < 2) return false;
        double t0 = Dot(D, pts[0]), t1 = Dot(D, pts[1]);
        if (t0 <= t1) { tMin = t0; tMax = t1; pMin = pts[0]; pMax = pts[1]; }
        else { tMin = t1; tMax = t0; pMin = pts[1]; pMax = pts[0]; }
        return tMax > tMin + 1e-15;
    }

    private static bool SameSide(double a, double b, double c)
    {
        const double e = 1e-12;
        return (a > e && b > e && c > e) || (a < -e && b < -e && c < -e);
    }
    private static (double x, double y, double z) Sub((double x, double y, double z) a, (double x, double y, double z) b) => (a.x - b.x, a.y - b.y, a.z - b.z);
    private static (double x, double y, double z) Cross((double x, double y, double z) a, (double x, double y, double z) b) => (a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    private static double Dot((double x, double y, double z) a, (double x, double y, double z) b) => a.x * b.x + a.y * b.y + a.z * b.z;
}
