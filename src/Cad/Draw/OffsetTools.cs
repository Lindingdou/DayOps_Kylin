using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 偏移(OFFSET)结果的自交清理。
///
/// 逐段平移 + 相邻段求交(miter)的等距偏移，在【凹侧】只要偏移量越过局部曲率中心就会自身折叠：
/// 折回的那几个顶点跑到前面去了，画出来就是一个小环（用户看到的"偏移后自相交"）。
///
/// 口径照原版 PitDesignLib/OffsetEngine：
/// · 开口线 —— <c>FilterFoldedOpenOffset</c>：把每个偏移点投到【源线】的弧长参数上，只保留参数
///   单调递增的点；折回点的参数会倒退，据此丢弃（"不够条件的那段干脆不偏移"，而不是整条不做）。
/// · 闭合线 —— 原版交给 Clipper2 做自交剔除/尖灭/分裂；Kylin 没有这个依赖，故：同样做单调筛，
///   再按原版的兜底判据（签名面积反号或缩到原始 5% 以下）认定尖灭，由调用方拒绝本次偏移。
/// 最后统一补一遍 <see cref="RemoveSelfLoops"/> —— 原版由 Clipper2 兜底的那部分，这里自己收尾。
///
/// 纯逻辑、可单测。
/// </summary>
public static class OffsetTools
{
    /// <summary>
    /// 去折叠：偏移点按在源线上的弧长参数单调递增筛（原版 FilterFoldedOpenOffset 的口径）。
    /// 闭合源线的参数是环形的，从第一个点的参数起绕一圈算。
    /// </summary>
    public static List<(double x, double y)> FilterFolded(
        IReadOnlyList<(double x, double y)> src, IReadOnlyList<(double x, double y)> ring, bool closed)
    {
        int ns = src.Count;
        if (ns < 2 || ring.Count < 2) return new List<(double x, double y)>(ring);

        int segN = closed ? ns : ns - 1;
        var cum = new double[segN + 1];
        for (int i = 0; i < segN; i++)
        {
            var a = src[i]; var b = src[(i + 1) % ns];
            cum[i + 1] = cum[i] + Math.Sqrt(D2(a.x, a.y, b.x, b.y));
        }
        double total = cum[segN];
        if (total < 1e-12) return new List<(double x, double y)>(ring);

        double Proj(double qx, double qy)
        {
            double bd = double.MaxValue, bs = 0;
            for (int i = 0; i < segN; i++)
            {
                var a = src[i]; var b = src[(i + 1) % ns];
                double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-18 ? 0 : Math.Clamp(((qx - a.x) * dx + (qy - a.y) * dy) / l2, 0, 1);
                double px = a.x + t * dx, py = a.y + t * dy, d = D2(qx, qy, px, py);
                if (d < bd) { bd = d; bs = cum[i] + t * (cum[i + 1] - cum[i]); }
            }
            return bs;
        }

        double eps = Math.Max(1e-9, total * 1e-9);   // 阈值随图纸尺度走：矿区坐标百万级，定值会误判
        var outPts = new List<(double x, double y)>(ring.Count);
        double s0 = Proj(ring[0].x, ring[0].y), last = double.NegativeInfinity;
        foreach (var q in ring)
        {
            double s = Proj(q.x, q.y);
            if (closed && s < s0) s += total;        // 环形参数：绕过接缝后接着往上加
            if (s > last + eps) { outPts.Add(q); last = s; }
        }
        return outPts;
    }

    /// <summary>
    /// 去残环：仍然自交的地方，把两段交点之间那一圈整体去掉，交点补成一个顶点。
    /// （原版这一步由 Clipper2 承担；Kylin 无该依赖，自己收尾。跨接缝的环不动，留给面积判据。）
    /// </summary>
    public static List<(double x, double y)> RemoveSelfLoops(IReadOnlyList<(double x, double y)> ring, bool closed)
    {
        var pts = new List<(double x, double y)>(ring);
        for (int guard = 0; guard < 64; guard++)
        {
            int segN = closed ? pts.Count : pts.Count - 1;
            if (segN < 3) break;
            bool cut = false;
            for (int i = 0; i < segN - 2 && !cut; i++)
                for (int j = i + 2; j < segN; j++)
                {
                    if (closed && i == 0 && j == segN - 1) continue;   // 首尾两段本就相接
                    var p = SegSeg(pts[i], pts[(i + 1) % pts.Count], pts[j], pts[(j + 1) % pts.Count]);
                    if (p == null) continue;
                    // [i+1 .. j] 这一圈是折出来的环：整段丢掉，只留交点
                    pts.RemoveRange(i + 1, j - i);
                    pts.Insert(i + 1, p.Value);
                    cut = true;
                    break;
                }
            if (!cut) break;
        }
        return pts;
    }

    /// <summary>
    /// 把源线的逐顶点高程搬到偏移结果上：每个偏移点投到源线的弧长参数，按该参数在源 Zs 间线性插值。
    /// 折叠筛掉的点、去环补出来的交点都能取到值 —— 等高线/台阶线偏移后不该掉成平面线。
    /// </summary>
    public static List<double> MapZ(IReadOnlyList<(double x, double y)> src, IReadOnlyList<double> srcZ,
                                    bool closed, IReadOnlyList<(double x, double y)> outPts)
    {
        var zs = new List<double>(outPts.Count);
        int ns = src.Count;
        int segN = closed ? ns : ns - 1;
        foreach (var q in outPts)
        {
            double bd = double.MaxValue, bz = srcZ.Count > 0 ? srcZ[0] : 0;
            for (int i = 0; i < segN; i++)
            {
                var a = src[i]; var b = src[(i + 1) % ns];
                double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-18 ? 0 : Math.Clamp(((q.x - a.x) * dx + (q.y - a.y) * dy) / l2, 0, 1);
                double px = a.x + t * dx, py = a.y + t * dy, d = D2(q.x, q.y, px, py);
                if (d >= bd) continue;
                bd = d;
                double z0 = srcZ[i], z1 = srcZ[(i + 1) % ns];
                bz = z0 + (z1 - z0) * t;
            }
            zs.Add(bz);
        }
        return zs;
    }

    /// <summary>闭合偏移是否已尖灭（签名面积反号或缩到原始 5% 以下）—— 原版无 Clipper2 时的同一判据。</summary>
    public static bool ClosedDegenerate(IReadOnlyList<(double x, double y)> src, IReadOnlyList<(double x, double y)> ring)
    {
        if (src.Count < 3 || ring.Count < 3) return true;
        double a0 = SignedArea(src), a1 = SignedArea(ring);
        return Math.Abs(a1) < Math.Abs(a0) * 0.05 || a0 * a1 < 0;
    }

    /// <summary>
    /// 自交检测（供单测/诊断用：偏移结果不该再有自交）。
    ///
    /// 不能只看"两段真交叉"：折叠出来的常常是【压在自己身上的尖刺】—— 折回段与来路共线重叠、
    /// 或折回段的端点正好落在来路内部，两者都不产生内部交点，却是同一个毛病（用户看到的那个小环）。
    /// 故三种都算：真交叉 / 共线重叠 / 端点落在非邻段内部。
    /// </summary>
    public static bool HasSelfIntersection(IReadOnlyList<(double x, double y)> pts, bool closed)
    {
        int n = pts.Count;
        int segN = closed ? n : n - 1;
        double scale = 0;
        for (int i = 0; i < segN; i++) scale = Math.Max(scale, Math.Sqrt(D2(pts[i].x, pts[i].y, pts[(i + 1) % n].x, pts[(i + 1) % n].y)));
        double tol = Math.Max(1e-9, scale * 1e-9);
        for (int i = 0; i < segN - 2; i++)
            for (int j = i + 2; j < segN; j++)
            {
                if (closed && i == 0 && j == segN - 1) continue;
                var a0 = pts[i]; var a1 = pts[(i + 1) % n];
                var b0 = pts[j]; var b1 = pts[(j + 1) % n];
                if (SegSeg(a0, a1, b0, b1) != null) return true;                       // 真交叉
                if (PointOnSeg(b0, a0, a1, tol) || PointOnSeg(b1, a0, a1, tol) ||      // 端点压在别段身上
                    PointOnSeg(a0, b0, b1, tol) || PointOnSeg(a1, b0, b1, tol)) return true;
            }
        return false;
    }

    // 点是否落在线段【内部】(含端点重合以外的贴合)；共线重叠会由两端点各自命中
    private static bool PointOnSeg((double x, double y) p, (double x, double y) a, (double x, double y) b, double tol)
    {
        double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
        if (l2 < 1e-18) return false;
        double t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2;
        if (t <= tol || t >= 1 - tol) return false;
        double px = a.x + t * dx, py = a.y + t * dy;
        return D2(p.x, p.y, px, py) <= tol * tol * l2 + 1e-18;
    }

    public static double SignedArea(IReadOnlyList<(double x, double y)> pts)
    {
        double a = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i]; var q = pts[(i + 1) % pts.Count];
            a += p.x * q.y - q.x * p.y;
        }
        return a * 0.5;
    }

    // 线段-线段真交点(端点相接不算)；不交返回 null
    private static (double x, double y)? SegSeg((double x, double y) a0, (double x, double y) a1,
                                                (double x, double y) b0, (double x, double y) b1)
    {
        double d1x = a1.x - a0.x, d1y = a1.y - a0.y, d2x = b1.x - b0.x, d2y = b1.y - b0.y;
        double den = d1x * d2y - d1y * d2x;
        if (Math.Abs(den) < 1e-15) return null;
        double t = ((b0.x - a0.x) * d2y - (b0.y - a0.y) * d2x) / den;
        double u = ((b0.x - a0.x) * d1y - (b0.y - a0.y) * d1x) / den;
        const double e = 1e-9;
        if (t <= e || t >= 1 - e || u <= e || u >= 1 - e) return null;
        return (a0.x + t * d1x, a0.y + t * d1y);
    }

    private static double D2(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }
}
