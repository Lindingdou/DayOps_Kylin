using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 闭合线精确裁剪 2.5D 三角网（忠实移植原内核 <c>laslib::recon::clip_tin_by_polygon</c>，
/// 原 MESHCLIP / CLIP「闭合线裁剪面」按钮最终调的就是它）。跨界三角**沿多边形边切开**，
/// 而不是按质心整块取舍——否则边界呈锯齿、尖刺伸出裁刀线（旧实现的毛病）。
/// <list type="bullet">
/// <item>保留圈内：裁刀多边形耳切成凸子三角 → 每个 TIN 三角对每个子三角做 Sutherland-Hodgman 求交 →
///   交集扇形剖分，Z 按源三角重心插值。输出恰好以多边形边为界(算量无 1–2% 边界误差)。</item>
/// <item>保留圈外：全外三角整块保留、全内整块删除；跨界三角以「角点 + 边界交点 + 落在三角内的多边形顶点」
///   重做 Delaunay，子三角按质心剔除内侧。退化时保留整块(保守：不崩不丢)。</item>
/// </list>
/// 多边形须简单(不自交)，凹凸皆可。输出按坐标焊接成索引网。纯逻辑、可单测。
/// </summary>
public static class MeshPolygonClip
{
    public readonly record struct Result(List<(double x, double y, double z)> Verts, List<(int a, int b, int c)> Tris);

    /// <summary>
    /// 原 ClipTriangleMeshByLoop 的非 2.5D 守卫：面法向上/下两侧各占 &gt;10% ⇒ 封闭实体/大面积悬挑，
    /// 闭合线裁剪仅支持单值高程面。返回 false 表示应拒绝裁剪。
    /// </summary>
    public static bool IsSingleValuedSurface(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        int up = 0, down = 0, n = 0;
        foreach (var (a, b, c) in tris)
        {
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
            n++;
            var pa = verts[a]; var pb = verts[b]; var pc = verts[c];
            double ex = pb.x - pa.x, ey = pb.y - pa.y, ez = pb.z - pa.z;
            double fx = pc.x - pa.x, fy = pc.y - pa.y, fz = pc.z - pa.z;
            double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl > 0) nz /= nl;
            if (nz > 0.02) up++; else if (nz < -0.02) down++;
        }
        return !(up * 10 > n && down * 10 > n);
    }

    /// <summary>
    /// 按闭合多边形(XY)裁剪三角网。keepInside=true 保留圈内。多边形显式闭合的重复末点自动去掉。
    /// 返回 null 表示输入不可用(多边形 &lt;3 顶点 / 耳切失败)；结果三角为空表示保留侧无面。
    /// </summary>
    public static Result? Clip(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris,
        IReadOnlyList<(double x, double y)> polygon, bool keepInside)
    {
        var poly = StripClosingPoint(polygon);
        if (poly.Count < 3) return null;

        double pxmn = poly[0].x, pxmx = poly[0].x, pymn = poly[0].y, pymx = poly[0].y;
        for (int i = 1; i < poly.Count; i++)
        {
            pxmn = Math.Min(pxmn, poly[i].x); pxmx = Math.Max(pxmx, poly[i].x);
            pymn = Math.Min(pymn, poly[i].y); pymx = Math.Max(pymx, poly[i].y);
        }
        double extent = 0;
        foreach (var v in verts) extent = Math.Max(extent, Math.Max(Math.Abs(v.x), Math.Abs(v.y)));
        extent = Math.Max(extent, Math.Max(Math.Max(Math.Abs(pxmn), Math.Abs(pxmx)), Math.Max(Math.Abs(pymn), Math.Abs(pymx))));
        var sink = new Sink(Math.Max(1e-9, extent * 1e-10));

        List<(int a, int b, int c)>? polyTris = null;
        List<SubTri>? subTris = null;
        if (keepInside)
        {
            polyTris = new List<(int a, int b, int c)>();
            EarClip(poly, polyTris);
            if (polyTris.Count == 0) return null;   // 退化多边形
            subTris = new List<SubTri>(polyTris.Count);
            foreach (var t in polyTris) subTris.Add(SubTri.Make(poly[t.a], poly[t.b], poly[t.c]));
        }

        var subj = new List<(double x, double y)>(16); var tmp = new List<(double x, double y)>(16);
        foreach (var (ia, ib, ic) in tris)
        {
            if (ia < 0 || ib < 0 || ic < 0 || ia >= verts.Count || ib >= verts.Count || ic >= verts.Count) continue;
            var v0 = verts[ia]; var v1 = verts[ib]; var v2 = verts[ic];
            double xmn = Math.Min(v0.x, Math.Min(v1.x, v2.x)), xmx = Math.Max(v0.x, Math.Max(v1.x, v2.x));
            double ymn = Math.Min(v0.y, Math.Min(v1.y, v2.y)), ymx = Math.Max(v0.y, Math.Max(v1.y, v2.y));
            bool bboxDisjoint = xmx < pxmn || xmn > pxmx || ymx < pymn || ymn > pymx;

            if (!keepInside)
            {
                if (bboxDisjoint) { sink.Add(v0, v1, v2); continue; }   // 整块在多边形包围盒外
                TriMinusPoly(v0, v1, v2, poly, sink);
                continue;
            }

            if (bboxDisjoint) continue;
            var bary = Bary.Make(v0, v1, v2);
            if (bary == null) continue;   // 退化(竖直)三角
            foreach (var st in subTris!)
            {
                if (xmx < st.Xmn || xmn > st.Xmx || ymx < st.Ymn || ymn > st.Ymx) continue;
                subj.Clear(); subj.Add((v0.x, v0.y)); subj.Add((v1.x, v1.y)); subj.Add((v2.x, v2.y));
                for (int k = 0; k < 3; k++)
                {
                    ShClipOneEdge(subj, tmp, st.Nx[k], st.Ny[k], st.C[k]);
                    (subj, tmp) = (tmp, subj);
                    if (subj.Count == 0) break;
                }
                if (subj.Count < 3) continue;
                var sa = bary.Value.Sample(subj[0].x, subj[0].y);
                for (int i = 1; i + 1 < subj.Count; i++)
                    sink.Add(sa, bary.Value.Sample(subj[i].x, subj[i].y), bary.Value.Sample(subj[i + 1].x, subj[i + 1].y));
            }
        }
        return new Result(sink.Verts, sink.Tris);
    }

    // ── 保留圈外：单个三角减去多边形 ──────────────────────────────────────────
    private static void TriMinusPoly((double x, double y, double z) v0, (double x, double y, double z) v1, (double x, double y, double z) v2,
        List<(double x, double y)> poly, Sink sink)
    {
        var bary = Bary.Make(v0, v1, v2);
        if (bary == null) return;   // 退化源三角
        double tdx = Math.Max(v0.x, Math.Max(v1.x, v2.x)) - Math.Min(v0.x, Math.Min(v1.x, v2.x));
        double tdy = Math.Max(v0.y, Math.Max(v1.y, v2.y)) - Math.Min(v0.y, Math.Min(v1.y, v2.y));
        double dedupTol = Math.Max(1e-9, 1e-6 * Math.Sqrt(tdx * tdx + tdy * tdy));

        var trx = new[] { v0.x, v1.x, v2.x }; var try_ = new[] { v0.y, v1.y, v2.y };
        var sites = new List<(double x, double y)>(16) { (v0.x, v0.y), (v1.x, v1.y), (v2.x, v2.y) };

        bool anyCross = false;
        int pn = poly.Count;
        for (int e = 0; e < 3; e++)
        {
            double ax = trx[e], ay = try_[e], bx = trx[(e + 1) % 3], by = try_[(e + 1) % 3];
            for (int p = 0; p < pn; p++)
            {
                int q = (p + 1) % pn;
                if (SegIntersect(ax, ay, bx, by, poly[p].x, poly[p].y, poly[q].x, poly[q].y, out double ix, out double iy))
                { sites.Add((ix, iy)); anyCross = true; }
            }
        }
        bool anyPolyInT = false;
        for (int p = 0; p < pn; p++)
            if (PointInTri(poly[p].x, poly[p].y, trx, try_)) { sites.Add(poly[p]); anyPolyInT = true; }

        if (!anyCross && !anyPolyInT)
        {
            // 与多边形边界不相遇 → 整块在内或在外
            if (LineMath.PointInPolygon(v0.x, v0.y, poly)) return;   // 内 → 删
            sink.Add(v0, v1, v2);                                     // 外 → 整块留
            return;
        }

        // 近重合站点去重(Delaunay 不喜欢重复点)
        var uniq = new List<(double x, double y)>(sites.Count);
        foreach (var s in sites)
        {
            bool dup = false;
            foreach (var u in uniq) if (Math.Abs(u.x - s.x) < dedupTol && Math.Abs(u.y - s.y) < dedupTol) { dup = true; break; }
            if (!dup) uniq.Add(s);
        }
        if (uniq.Count < 3) { sink.Add(v0, v1, v2); return; }

        List<(int a, int b, int c)> sub;
        try { sub = Delaunay.Triangulate(uniq); }
        catch { sink.Add(v0, v1, v2); return; }   // 保守回退
        if (sub.Count == 0) { sink.Add(v0, v1, v2); return; }

        bool triCcw = bary.Value.Denom > 0;
        foreach (var (a, b, c) in sub)
        {
            var pa = uniq[a]; var pb = uniq[b]; var pc = uniq[c];
            double mx = (pa.x + pb.x + pc.x) / 3, my = (pa.y + pb.y + pc.y) / 3;
            if (LineMath.PointInPolygon(mx, my, poly)) continue;   // 内 → 跳过
            var ma = bary.Value.Sample(pa.x, pa.y); var mb = bary.Value.Sample(pb.x, pb.y); var mc = bary.Value.Sample(pc.x, pc.y);
            double micro = (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
            if ((micro > 0) == triCcw) sink.Add(ma, mb, mc); else sink.Add(ma, mc, mb);   // 保持源三角绕向
        }
    }

    // ── 几何小件 ──────────────────────────────────────────────────────────────
    private static List<(double x, double y)> StripClosingPoint(IReadOnlyList<(double x, double y)> polygon)
    {
        var poly = new List<(double x, double y)>(polygon ?? Array.Empty<(double, double)>());
        if (poly.Count >= 2)
        {
            var f = poly[0]; var l = poly[^1];
            if (Math.Abs(f.x - l.x) < 1e-4 && Math.Abs(f.y - l.y) < 1e-4) poly.RemoveAt(poly.Count - 1);
        }
        return poly;
    }

    private static bool SegIntersect(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, out double ox, out double oy)
    {
        ox = oy = 0;
        double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-20) return false;   // 平行/退化
        double t = ((cx - ax) * sy - (cy - ay) * sx) / den;
        double u = ((cx - ax) * ry - (cy - ay) * rx) / den;
        if (t < -1e-6 || t > 1 + 1e-6 || u < -1e-6 || u > 1 + 1e-6) return false;
        ox = ax + t * rx; oy = ay + t * ry;
        return true;
    }

    private static double Cross(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

    private static bool PointInTri(double x, double y, double[] tx, double[] ty)
    {
        double d1 = Cross(tx[0], ty[0], tx[1], ty[1], x, y);
        double d2 = Cross(tx[1], ty[1], tx[2], ty[2], x, y);
        double d3 = Cross(tx[2], ty[2], tx[0], ty[0], x, y);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>简单多边形耳切(凹凸皆可, CW/CCW 皆可, 输出沿输入绕向)。退化输入提前退出、不死循环。</summary>
    private static void EarClip(List<(double x, double y)> p, List<(int a, int b, int c)> outTris)
    {
        int n = p.Count;
        double sa = 0;
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; sa += p[i].x * p[j].y - p[j].x * p[i].y; }
        bool ccw = sa > 0;
        var ring = new List<int>(n);
        for (int i = 0; i < n; i++) ring.Add(ccw ? i : n - 1 - i);

        long guard = 2L * n * n + 16;
        while (ring.Count >= 3 && guard-- > 0)
        {
            bool clipped = false; int m = ring.Count;
            for (int i = 0; i < m; i++)
            {
                int ia = ring[(i + m - 1) % m], ib = ring[i], ic = ring[(i + 1) % m];
                var a = p[ia]; var b = p[ib]; var c = p[ic];
                if (Cross(a.x, a.y, b.x, b.y, c.x, c.y) <= 0) continue;   // 非凸角
                bool anyInside = false;
                for (int k = 0; k < m; k++)
                {
                    int ik = ring[k];
                    if (ik == ia || ik == ib || ik == ic) continue;
                    var q = p[ik];
                    double d1 = Cross(q.x, q.y, a.x, a.y, b.x, b.y), d2 = Cross(q.x, q.y, b.x, b.y, c.x, c.y), d3 = Cross(q.x, q.y, c.x, c.y, a.x, a.y);
                    bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                    if (!(neg && pos)) { anyInside = true; break; }
                }
                if (anyInside) continue;
                outTris.Add(ccw ? (ia, ib, ic) : (ic, ib, ia));
                ring.RemoveAt(i); clipped = true; break;
            }
            if (!clipped) break;
        }
    }

    /// <summary>Sutherland-Hodgman：用半平面 {nx·x + ny·y + c &gt; 0} 裁 subj 到 out。</summary>
    private static void ShClipOneEdge(List<(double x, double y)> subj, List<(double x, double y)> outp, double nx, double ny, double c)
    {
        outp.Clear();
        if (subj.Count == 0) return;
        bool Inside(double x, double y) => nx * x + ny * y + c > 0;
        (double, double) Isect(double ax, double ay, double bx, double by)
        {
            double da = nx * ax + ny * ay + c, db = nx * bx + ny * by + c, t = da / (da - db);
            return (ax + t * (bx - ax), ay + t * (by - ay));
        }
        var (px, py) = subj[^1]; bool pi = Inside(px, py);
        foreach (var (cx, cy) in subj)
        {
            bool ci = Inside(cx, cy);
            if (ci) { if (!pi) outp.Add(Isect(px, py, cx, cy)); outp.Add((cx, cy)); }
            else if (pi) outp.Add(Isect(px, py, cx, cy));
            px = cx; py = cy; pi = ci;
        }
    }

    /// <summary>裁刀凸子三角：强制 CCW 后三条边的内向单位法线半平面 + 包围盒。</summary>
    private readonly struct SubTri
    {
        public readonly double[] Nx, Ny, C;
        public readonly double Xmn, Xmx, Ymn, Ymx;
        private SubTri(double[] nx, double[] ny, double[] c, double xmn, double xmx, double ymn, double ymx)
        { Nx = nx; Ny = ny; C = c; Xmn = xmn; Xmx = xmx; Ymn = ymn; Ymx = ymx; }
        public static SubTri Make((double x, double y) a, (double x, double y) b, (double x, double y) c)
        {
            double sa = a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y);
            if (sa < 0) (b, c) = (c, b);
            var vx = new[] { a.x, b.x, c.x }; var vy = new[] { a.y, b.y, c.y };
            var nx = new double[3]; var ny = new double[3]; var cc = new double[3];
            for (int k = 0; k < 3; k++)
            {
                int kn = (k + 1) % 3;
                double dx = vx[kn] - vx[k], dy = vy[kn] - vy[k];
                double n0 = -dy, n1 = dx, len = Math.Sqrt(n0 * n0 + n1 * n1);
                if (len > 0) { n0 /= len; n1 /= len; }
                nx[k] = n0; ny[k] = n1; cc[k] = -(n0 * vx[k] + n1 * vy[k]);
            }
            return new SubTri(nx, ny, cc,
                Math.Min(a.x, Math.Min(b.x, c.x)), Math.Max(a.x, Math.Max(b.x, c.x)),
                Math.Min(a.y, Math.Min(b.y, c.y)), Math.Max(a.y, Math.Max(b.y, c.y)));
        }
    }

    /// <summary>源三角重心基：任意 (x,y) 采样 Z(权重钳到 [0,1] 再归一, 与原版一致)。</summary>
    private readonly struct Bary
    {
        private readonly (double x, double y, double z) _v0, _v1, _v2;
        private readonly double _dx10, _dy10, _dx20, _dy20, _invD;
        public readonly double Denom;
        private Bary((double x, double y, double z) v0, (double x, double y, double z) v1, (double x, double y, double z) v2, double denom)
        {
            _v0 = v0; _v1 = v1; _v2 = v2; Denom = denom;
            _dx10 = v1.x - v0.x; _dy10 = v1.y - v0.y; _dx20 = v2.x - v0.x; _dy20 = v2.y - v0.y; _invD = 1.0 / denom;
        }
        public static Bary? Make((double x, double y, double z) v0, (double x, double y, double z) v1, (double x, double y, double z) v2)
        {
            double denom = (v1.x - v0.x) * (v2.y - v0.y) - (v2.x - v0.x) * (v1.y - v0.y);
            return Math.Abs(denom) < 1e-12 ? null : new Bary(v0, v1, v2, denom);
        }
        public (double x, double y, double z) Sample(double x, double y)
        {
            double wx = x - _v0.x, wy = y - _v0.y;
            double u = (wx * _dy20 - _dx20 * wy) * _invD;   // v1 权
            double v = (_dx10 * wy - wx * _dy10) * _invD;   // v2 权
            double w = 1 - u - v;                            // v0 权
            if (u < 0) u = 0; if (v < 0) v = 0; if (w < 0) w = 0;
            double s = u + v + w; if (s > 0) { u /= s; v /= s; w /= s; }
            return (x, y, w * _v0.z + u * _v1.z + v * _v2.z);
        }
    }

    /// <summary>输出汇：按坐标量化焊接成索引网(原版输出平铺三元组, 这里焊起来以便后续网格操作)；焊后重合退化三角丢弃。</summary>
    private sealed class Sink
    {
        public readonly List<(double x, double y, double z)> Verts = new();
        public readonly List<(int a, int b, int c)> Tris = new();
        private readonly Dictionary<(long, long, long), int> _map = new();
        private readonly double _q;
        public Sink(double quantum) { _q = quantum; }
        private int Index((double x, double y, double z) p)
        {
            var key = ((long)Math.Round(p.x / _q), (long)Math.Round(p.y / _q), (long)Math.Round(p.z / _q));
            if (!_map.TryGetValue(key, out int i)) { i = Verts.Count; Verts.Add(p); _map[key] = i; }
            return i;
        }
        public void Add((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
        {
            int ia = Index(a), ib = Index(b), ic = Index(c);
            if (ia == ib || ib == ic || ia == ic) return;
            Tris.Add((ia, ib, ic));
        }
    }
}
