using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 沿多段线 XY 投影的「垂直幕墙」分割三角网（忠实移植原内核 <c>meshlib::MeshCutter::splitByPolylineVertical</c>，
/// SPLITALONG「沿线分割三角网」最终调的就是它）。早前 Kylin 只按折线首末两点定一张竖直面切
/// (<see cref="MeshPlaneSplit"/>)，折线一拐就不再贴着线走 —— 用户看到的就是"没按多段线边界分"。
/// <list type="number">
/// <item>逐顶点定侧缓存：最近段带符号距离 &gt; 0 = 左（站在多段线走向看的左手侧），|距离| ≤ 1e-9×坐标量级 = 在线上。</item>
/// <item>Pass 1 只<b>判定</b>每个三角：整块归左 / 归右 / 被切开（多段线段裁进三角内有非零长度即被切开）。</item>
/// <item>Pass 2a 整块三角<b>共享原顶点索引</b>发出，不复制顶点、不焊接。</item>
/// <item>Pass 2b 被切开的三角：单弦横穿且两端各落一条不同边 → 直接给 3 个子三角（快路，绕向照旧）；
///   多段 / 端点落在三角内 → 约束 Delaunay 严格沿折线细分。子三角按 3 顶点符号多数派定侧（形心兜底）、
///   Z 按源三角重心插值、切口顶点按 weldTol 与已发出顶点焊接（否则切口黑缝、相邻子三角错分）。</item>
/// <item>Pass 3 闭合体：切口 3D 段追成曲线，闭合环投到 (弧长, z) 平面耳切成盖片，正反两份分别贴回左右片。</item>
/// </list>
/// 纯逻辑、可单测。
/// </summary>
public static class MeshPolylineSplit
{
    public sealed class Result
    {
        public bool Success;
        public string? Error;
        public string Log = "";
        public List<(double x, double y, double z)> LeftVerts = new();
        public List<(int a, int b, int c)> LeftTris = new();
        public List<(double x, double y, double z)> RightVerts = new();
        public List<(int a, int b, int c)> RightTris = new();
        public int CutFaces, ChordFast, CapLoops, CapTris;
        public bool HasLeft => LeftTris.Count > 0;
        public bool HasRight => RightTris.Count > 0;
    }

    /// <summary>
    /// 按多段线 XY 投影切分。tolerance = 焊接容差下限（原版 SPLITALONG 传 1e-6；实际取 max(tolerance, 包围盒对角×1e-6)）。
    /// 多段线在网格 XY 包围盒之外 → 整张网按其中心归一侧（Success=true，一侧为空），调用方据此回显"切口未横穿"。
    /// </summary>
    public static Result Split(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris,
        IReadOnlyList<(double x, double y)> polyline, double tolerance = 1e-6)
    {
        var r = new Result();
        if (polyline == null || polyline.Count < 2) { r.Error = "多段线至少需要 2 个顶点"; return r; }
        if (verts == null || tris == null || verts.Count == 0 || tris.Count == 0) { r.Error = "三角网为空"; return r; }

        // 1. 多段线 → 2D（去掉连续重合点）+ 段网格
        var poly = new List<(double x, double y)>(polyline.Count);
        foreach (var p in polyline)
            if (poly.Count == 0 || Math.Abs(p.x - poly[^1].x) > 0 || Math.Abs(p.y - poly[^1].y) > 0) poly.Add(p);
        if (poly.Count < 2) { r.Error = "多段线退化为一点"; return r; }

        // 2. 网格 XY 包围盒
        double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
        foreach (var v in verts)
        {
            if (v.x < mnx) mnx = v.x; if (v.x > mxx) mxx = v.x;
            if (v.y < mny) mny = v.y; if (v.y > mxy) mxy = v.y;
            if (v.z < mnz) mnz = v.z; if (v.z > mxz) mxz = v.z;
        }
        int nv = verts.Count;
        // "在线上"的判据要跟坐标量级走：切口顶点是求交算出来的，离直线有 ~1e-16×坐标² 的舍入误差，
        // 原版拿 1e-12 的绝对阈值判叉积，坐标到几百就已经把线上点随机判成左/右，子三角按顶点符号多数派定侧
        // 就跟着乱——实机切出来锯齿、尖刺、整块跑错侧（矿区坐标 1e6 更甚）。按 1e-9×坐标量级(1e6 → 1 mm)判在线。
        double coordMag = Math.Max(Math.Max(Math.Abs(mnx), Math.Abs(mxx)), Math.Max(Math.Abs(mny), Math.Abs(mxy)));
        foreach (var q in poly) coordMag = Math.Max(coordMag, Math.Max(Math.Abs(q.x), Math.Abs(q.y)));
        double onLineTol = 1e-9 * Math.Max(1.0, coordMag);
        var grid = new SegGrid(poly, 256, onLineTol);

        // 网格 XY 包围盒与多段线包围盒不相交 → 整张网归一侧
        if (!grid.Overlaps(mnx, mny, mxx, mxy))
        {
            int side = grid.SideAt(0.5 * (mnx + mxx), 0.5 * (mny + mxy));
            var dv = side >= 0 ? r.LeftVerts : r.RightVerts; var dt = side >= 0 ? r.LeftTris : r.RightTris;
            dv.AddRange(verts);
            foreach (var t in tris) if (Valid(t, nv)) dt.Add(t);
            r.Success = true; r.Log = "多段线在网格 XY 包围盒外；整张网归一侧";
            return r;
        }

        // 3. 焊接容差：按输入网包围盒对角
        double diag = Math.Sqrt((mxx - mnx) * (mxx - mnx) + (mxy - mny) * (mxy - mny) + (mxz - mnz) * (mxz - mnz));
        double weldTol = Math.Max(tolerance, Math.Max(1e-6, diag * 1e-6));

        // 4. 逐顶点定侧缓存
        var vertSide = new sbyte[nv];
        for (int i = 0; i < nv; i++) vertSide[i] = (sbyte)grid.SideAt(verts[i].x, verts[i].y);

        // ── Pass 1：判定每个三角 —— +1 整块归左 / -1 整块归右 / 0 被切开 ──
        int nf = tris.Count;
        var faceSide = new sbyte[nf];
        var cutFaces = new List<(int face, List<((double x, double y) a, (double x, double y) b)> segs)>();
        // 竖直三角（XY 投影退化成一条线：地质体/固化体的直立侧壁）：多段线段与那条线的交点 (x*,y*) 就是切口，
        // 记沿壁参数 s*，Pass 2c 按 s=s* 的竖直面在 3D 里直接切开。原版把它们当普通三角走 2D 裁剪，
        // 裁进退化三角的段长恒为 0 → 侧壁从不被切、整块归一侧 → 闭合体切开后两片都漏一块壁、封盖也闭不上。
        var cutWalls = new List<(int face, double ox, double oy, double dx, double dy, List<double> s)>();
        var cand = new List<int>(16);
        var stamp = new uint[Math.Max(1, grid.SegCount)];
        uint tick = 0;
        for (int fi = 0; fi < nf; fi++)
        {
            var (a, b, c) = tris[fi];
            if (!Valid(tris[fi], nv)) { faceSide[fi] = 1; continue; }   // 坏索引：Pass 2a 再按 Valid 跳过, 不发出
            var v0 = verts[a]; var v1 = verts[b]; var v2 = verts[c];
            double tx0 = Math.Min(v0.x, Math.Min(v1.x, v2.x)), tx1 = Math.Max(v0.x, Math.Max(v1.x, v2.x));
            double ty0 = Math.Min(v0.y, Math.Min(v1.y, v2.y)), ty1 = Math.Max(v0.y, Math.Max(v1.y, v2.y));

            sbyte WholeSide()
            {
                sbyte s0 = vertSide[a], s1 = vertSide[b], s2 = vertSide[c];
                if (s0 != 0 && s0 == s1 && s1 == s2) return s0;
                int s = grid.SideAt((v0.x + v1.x + v2.x) / 3.0, (v0.y + v1.y + v2.y) / 3.0);
                return (sbyte)(s >= 0 ? 1 : -1);
            }

            if (!grid.Overlaps(tx0, ty0, tx1, ty1)) { faceSide[fi] = WholeSide(); continue; }
            tick++;
            grid.Query(tx0, ty0, tx1, ty1, cand, stamp, tick);
            var T = new (double x, double y)[] { (v0.x, v0.y), (v1.x, v1.y), (v2.x, v2.y) };
            double scale2 = Math.Max((tx1 - tx0) * (tx1 - tx0), (ty1 - ty0) * (ty1 - ty0));
            if (TriArea2D(T[0], T[1], T[2]) <= 1e-12 * scale2)
            {
                // 竖直三角：沿壁方向 d 取最长 XY 边，s = (p - o)·d；多段线段与壁段 [smin,smax] 求交得 s*
                if (WallFrame(T, out double ox, out double oy, out double dx, out double dy, out double smin, out double smax))
                {
                    List<double>? ss = null;
                    double wax = ox + smin * dx, way = oy + smin * dy, wbx = ox + smax * dx, wby = oy + smax * dy;
                    double sEps = 1e-9 * (smax - smin);
                    foreach (int si in cand)
                    {
                        if (!SegSegIntersect2D(poly[si].x, poly[si].y, poly[si + 1].x, poly[si + 1].y, wax, way, wbx, wby, out _, out double u)) continue;
                        double sStar = smin + u * (smax - smin);
                        if (sStar <= smin + sEps || sStar >= smax - sEps) continue;   // 正穿壁的端点：交给相邻壁 / 不算切
                        (ss ??= new()).Add(sStar);
                    }
                    if (ss != null) { faceSide[fi] = 0; cutWalls.Add((fi, ox, oy, dx, dy, ss)); continue; }
                }
                faceSide[fi] = WholeSide(); continue;
            }
            List<((double x, double y) a, (double x, double y) b)>? segs = null;
            foreach (int si in cand)
            {
                if (ClipSegToTri2D(poly[si], poly[si + 1], T, out var ca, out var cb))
                    (segs ??= new()).Add((ca, cb));
            }
            if (segs == null) { faceSide[fi] = WholeSide(); continue; }
            faceSide[fi] = 0;
            cutFaces.Add((fi, segs));
        }
        r.CutFaces = cutFaces.Count + cutWalls.Count;

        // ── Pass 2a：整块三角 —— 共享原顶点索引发出 ──
        var mapL = new int[nv]; var mapR = new int[nv];
        Array.Fill(mapL, -1); Array.Fill(mapR, -1);
        int VertexOn(List<(double x, double y, double z)> dst, int[] map, int oi)
        {
            int m = map[oi];
            if (m < 0) { m = dst.Count; dst.Add(verts[oi]); map[oi] = m; }
            return m;
        }
        for (int fi = 0; fi < nf; fi++)
        {
            if (faceSide[fi] == 0 || !Valid(tris[fi], nv)) continue;
            bool left = faceSide[fi] > 0;
            var dv = left ? r.LeftVerts : r.RightVerts; var dt = left ? r.LeftTris : r.RightTris; var map = left ? mapL : mapR;
            var (a, b, c) = tris[fi];
            dt.Add((VertexOn(dv, map, a), VertexOn(dv, map, b), VertexOn(dv, map, c)));
        }

        // ── 切口定点去重表：只服务被切开三角产出的顶点 ──
        var bucketL = new Dictionary<(long, long, long), List<int>>();
        var bucketR = new Dictionary<(long, long, long), List<int>>();
        long Q(double v) => (long)Math.Floor(v / weldTol);
        void Seed(Dictionary<(long, long, long), List<int>> bk, List<(double x, double y, double z)> dst, int idx)
        {
            var p = dst[idx]; var key = (Q(p.x), Q(p.y), Q(p.z));
            if (!bk.TryGetValue(key, out var l)) bk[key] = l = new List<int>(2);
            l.Add(idx);
        }
        {
            var seededL = new bool[nv]; var seededR = new bool[nv];
            void SeedFace(int face)
            {
                var (a, b, c) = tris[face];
                foreach (int oi in new[] { a, b, c })
                {
                    if (mapL[oi] >= 0 && !seededL[oi]) { seededL[oi] = true; Seed(bucketL, r.LeftVerts, mapL[oi]); }
                    if (mapR[oi] >= 0 && !seededR[oi]) { seededR[oi] = true; Seed(bucketR, r.RightVerts, mapR[oi]); }
                }
            }
            foreach (var (face, _) in cutFaces) SeedFace(face);
            foreach (var w in cutWalls) SeedFace(w.face);
        }
        int Resolve(List<(double x, double y, double z)> dst, Dictionary<(long, long, long), List<int>> bk, (double x, double y, double z) p)
        {
            long cx = Q(p.x), cy = Q(p.y), cz = Q(p.z);
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
            {
                if (!bk.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) continue;
                foreach (int ci in l)
                {
                    var q = dst[ci];
                    double ex = q.x - p.x, ey = q.y - p.y, ez = q.z - p.z;
                    if (Math.Sqrt(ex * ex + ey * ey + ez * ez) < weldTol) return ci;
                }
            }
            int ni = dst.Count; dst.Add(p);
            if (!bk.TryGetValue((cx, cy, cz), out var own)) bk[(cx, cy, cz)] = own = new List<int>(2);
            own.Add(ni);
            return ni;
        }
        void Emit(bool left, (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
        {
            var dv = left ? r.LeftVerts : r.RightVerts; var dt = left ? r.LeftTris : r.RightTris; var bk = left ? bucketL : bucketR;
            int i0 = Resolve(dv, bk, a), i1 = Resolve(dv, bk, b), i2 = Resolve(dv, bk, c);
            if (i0 == i1 || i1 == i2 || i2 == i0) return;   // 退化面丢掉
            dt.Add((i0, i1, i2));
        }

        // 封口用：累积切口 3D 段（Z 落在源三角平面上）
        var cutSegments = new List<((double x, double y, double z) p0, (double x, double y, double z) p1)>(cutFaces.Count * 2 + 8);

        // ── Pass 2b：被切开的三角 —— 单弦走快路，其余交约束 Delaunay ──
        var pts2 = new List<(double x, double y)>(16); var cons = new List<(int u, int v)>(8);
        double mergeTol = Math.Max(weldTol, 1e-9);
        foreach (var (fi, segs) in cutFaces)
        {
            var (ia, ib, ic) = tris[fi];
            var v0 = verts[ia]; var v1 = verts[ib]; var v2 = verts[ic];
            (double x, double y, double z) Lift((double x, double y) p) => LiftZ(p.x, p.y, v0, v1, v2);
            foreach (var (sa, sb) in segs) cutSegments.Add((Lift(sa), Lift(sb)));

            var T = new (double x, double y)[] { (v0.x, v0.y), (v1.x, v1.y), (v2.x, v2.y) };
            // 快路：一条直弦横穿，两端各在一条边上 → 直接给 3 个子三角
            if (segs.Count == 1 && SplitTriByChord2D(T, segs[0].a, segs[0].b, out var sub))
            {
                r.ChordFast++;
                foreach (var st in sub)
                {
                    if (TriArea2D(st[0], st[1], st[2]) < 1e-9) continue;
                    int side = SideOfTri2D(grid, st[0], st[1], st[2]);
                    Emit(side >= 0, Lift(st[0]), Lift(st[1]), Lift(st[2]));
                }
                continue;
            }

            // 慢路：多段 / 端点落在三角内部 → 约束 Delaunay（三角三顶点 + 各段端点，段作约束边）
            pts2.Clear(); cons.Clear();
            pts2.Add(T[0]); pts2.Add(T[1]); pts2.Add(T[2]);
            int Site((double x, double y) p)
            {
                for (int i = 0; i < pts2.Count; i++)
                    if (Math.Abs(pts2[i].x - p.x) <= mergeTol && Math.Abs(pts2[i].y - p.y) <= mergeTol) return i;
                pts2.Add(p); return pts2.Count - 1;
            }
            foreach (var (sa, sb) in segs)
            {
                int u = Site(sa), w = Site(sb);
                if (u != w) cons.Add((u, w));
            }
            List<(int a, int b, int c)>? cdt = null;
            try { cdt = Delaunay.TriangulateConstrained(pts2, cons); } catch { cdt = null; }
            if (cdt == null || cdt.Count == 0)
            {
                // 剖分失败：整块按形心归侧，几何不丢
                int side = grid.SideAt((v0.x + v1.x + v2.x) / 3.0, (v0.y + v1.y + v2.y) / 3.0);
                Emit(side >= 0, v0, v1, v2);
                continue;
            }
            bool srcCcw = Cross(T[0], T[1], T[2]) > 0;
            foreach (var (sa, sb, sc) in cdt)
            {
                var p0 = pts2[sa]; var p1 = pts2[sb]; var p2 = pts2[sc];
                if (TriArea2D(p0, p1, p2) < 1e-9) continue;
                int side = SideOfTri2D(grid, p0, p1, p2);
                // 子三角绕向与源三角对齐（快路天然保持；剖分输出统一 CCW，遇 CW 源三角要翻回来）
                if ((Cross(p0, p1, p2) > 0) != srcCcw) (p1, p2) = (p2, p1);
                Emit(side >= 0, Lift(p0), Lift(p1), Lift(p2));
            }
        }

        // ── Pass 2c：竖直三角 —— 按 s=s* 的竖直面在 3D 里逐次切开，子多边形按 XY 形心定侧 ──
        foreach (var (fi, ox, oy, dx, dy, ss) in cutWalls)
        {
            var (ia, ib, ic) = tris[fi];
            var polys = new List<List<(double x, double y, double z)>> { new() { verts[ia], verts[ib], verts[ic] } };
            ss.Sort();
            foreach (double sStar in ss)
            {
                var next = new List<List<(double x, double y, double z)>>(polys.Count + 1);
                foreach (var pg in polys)
                {
                    var lo = new List<(double x, double y, double z)>(5); var hi = new List<(double x, double y, double z)>(5);
                    var hits = new List<(double x, double y, double z)>(2);
                    for (int i = 0; i < pg.Count; i++)
                    {
                        var p0 = pg[i]; var p1 = pg[(i + 1) % pg.Count];
                        double f0 = (p0.x - ox) * dx + (p0.y - oy) * dy - sStar, f1 = (p1.x - ox) * dx + (p1.y - oy) * dy - sStar;
                        if (f0 <= 0) lo.Add(p0);
                        if (f0 >= 0) hi.Add(p0);
                        if ((f0 < 0 && f1 > 0) || (f0 > 0 && f1 < 0))
                        {
                            double t = f0 / (f0 - f1);
                            var ip = (p0.x + t * (p1.x - p0.x), p0.y + t * (p1.y - p0.y), p0.z + t * (p1.z - p0.z));
                            lo.Add(ip); hi.Add(ip); hits.Add(ip);
                        }
                    }
                    if (hits.Count == 2) cutSegments.Add((hits[0], hits[1]));   // 封口用切口段
                    if (lo.Count >= 3) next.Add(lo);
                    if (hi.Count >= 3) next.Add(hi);
                }
                polys = next;
            }
            foreach (var pg in polys)
            {
                double cx = 0, cy = 0; foreach (var q in pg) { cx += q.x; cy += q.y; }
                int side = grid.SideAt(cx / pg.Count, cy / pg.Count);
                for (int i = 1; i + 1 < pg.Count; i++) Emit(side >= 0, pg[0], pg[i], pg[i + 1]);   // 扇形, 绕向照旧
            }
        }

        // ── Pass 3：闭合网格的切口封口 ──
        if (cutSegments.Count > 0)
        {
            double capTol = Math.Max(tolerance, 1e-6);
            var curves = TraceCurves(cutSegments, capTol);
            bool anyClosed = false;
            foreach (var cv in curves) if (CurveClosed(cv, capTol)) { anyClosed = true; break; }
            if (anyClosed && IsMeshClosed(tris, nv))
            {
                var arc = new double[poly.Count];
                for (int i = 1; i < poly.Count; i++)
                    arc[i] = arc[i - 1] + Math.Sqrt((poly[i].x - poly[i - 1].x) * (poly[i].x - poly[i - 1].x) + (poly[i].y - poly[i - 1].y) * (poly[i].y - poly[i - 1].y));
                foreach (var cv in curves)
                {
                    if (!CurveClosed(cv, capTol)) continue;
                    var ring = new List<(double x, double y)>(cv.Count);
                    for (int i = 0; i + 1 < cv.Count; i++)
                    {
                        var uv = (ArcUOf(cv[i].x, cv[i].y, poly, arc), cv[i].z);
                        if (ring.Count > 0 && Math.Abs(ring[^1].x - uv.Item1) <= capTol && Math.Abs(ring[^1].y - uv.Item2) <= capTol) continue;
                        ring.Add(uv);
                    }
                    if (ring.Count >= 2 && Math.Abs(ring[0].x - ring[^1].x) <= capTol && Math.Abs(ring[0].y - ring[^1].y) <= capTol) ring.RemoveAt(ring.Count - 1);
                    if (ring.Count < 3) continue;
                    var capTris = new List<(int a, int b, int c)>();
                    EarClip(ring, capTris);
                    if (capTris.Count == 0) continue;
                    foreach (var (a, b, c) in capTris)
                    {
                        // 盖片三角不能横跨多段线的拐点：幕墙是逐段平面拼的，(U,z) 里跨过 U=arc[k] 的三角逆映射回 3D
                        // 会抄近路斜穿拐角（原版 CDT 直接逆映射就有这毛病：折线切闭合体, 两片体积对不上）。
                        // 三角是凸的，按每条经过它的竖线 U=arc[k] 依次切开，再逐片扇形发出。
                        var pieces = new List<List<(double x, double y)>> { new() { ring[a], ring[b], ring[c] } };
                        double uMin = Math.Min(ring[a].x, Math.Min(ring[b].x, ring[c].x)), uMax = Math.Max(ring[a].x, Math.Max(ring[b].x, ring[c].x));
                        for (int k = 1; k + 1 < arc.Length; k++)
                        {
                            double u = arc[k];
                            if (u <= uMin + capTol || u >= uMax - capTol) continue;
                            var next = new List<List<(double x, double y)>>(pieces.Count + 1);
                            foreach (var pg in pieces)
                            {
                                var lo = new List<(double x, double y)>(5); var hi = new List<(double x, double y)>(5);
                                for (int i = 0; i < pg.Count; i++)
                                {
                                    var p0 = pg[i]; var p1 = pg[(i + 1) % pg.Count];
                                    double f0 = p0.x - u, f1 = p1.x - u;
                                    if (f0 <= 0) lo.Add(p0);
                                    if (f0 >= 0) hi.Add(p0);
                                    if ((f0 < 0 && f1 > 0) || (f0 > 0 && f1 < 0))
                                    {
                                        double t = f0 / (f0 - f1);
                                        var ip = (u, p0.y + t * (p1.y - p0.y));
                                        lo.Add(ip); hi.Add(ip);
                                    }
                                }
                                if (lo.Count >= 3) next.Add(lo);
                                if (hi.Count >= 3) next.Add(hi);
                            }
                            pieces = next;
                        }
                        foreach (var pg in pieces)
                        {
                            var q0 = InvMapUV(pg[0].x, pg[0].y, poly, arc);
                            for (int i = 1; i + 1 < pg.Count; i++)
                            {
                                var q1 = InvMapUV(pg[i].x, pg[i].y, poly, arc);
                                var q2 = InvMapUV(pg[i + 1].x, pg[i + 1].y, poly, arc);
                                Emit(true, q0, q1, q2);
                                Emit(false, q0, q2, q1);   // 反向缠绕
                                r.CapTris++;
                            }
                        }
                    }
                    r.CapLoops++;
                }
            }
        }

        r.Success = true;
        r.Log = $"cut={r.CutFaces} (chord-fast={r.ChordFast})" + (r.CapLoops > 0 ? $" capped {r.CapLoops} loop(s), {r.CapTris} cap tri(s)/side" : "");
        return r;
    }

    // ── 几何小件 ──────────────────────────────────────────────────────────────
    private static bool Valid((int a, int b, int c) t, int nv)
        => t.a >= 0 && t.b >= 0 && t.c >= 0 && t.a < nv && t.b < nv && t.c < nv;

    private static double Cross((double x, double y) a, (double x, double y) b, (double x, double y) c)
        => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    private static double TriArea2D((double x, double y) a, (double x, double y) b, (double x, double y) c)
        => 0.5 * Math.Abs(Cross(a, b, c));

    /// <summary>竖直三角的壁坐标系：最长 XY 边方向为 d(单位)，o = 该边起点，s = (p-o)·d；三顶点 s 的范围 [smin,smax]。</summary>
    private static bool WallFrame((double x, double y)[] T, out double ox, out double oy, out double dx, out double dy, out double smin, out double smax)
    {
        ox = oy = dx = dy = smin = smax = 0;
        int bestE = -1; double bestL2 = 0;
        for (int e = 0; e < 3; e++)
        {
            double ex = T[(e + 1) % 3].x - T[e].x, ey = T[(e + 1) % 3].y - T[e].y, l2 = ex * ex + ey * ey;
            if (l2 > bestL2) { bestL2 = l2; bestE = e; }
        }
        if (bestE < 0 || bestL2 < 1e-24) return false;   // 三点 XY 完全重合（纯竖直棱柱退化）：不切
        double len = Math.Sqrt(bestL2);
        ox = T[bestE].x; oy = T[bestE].y; dx = (T[(bestE + 1) % 3].x - ox) / len; dy = (T[(bestE + 1) % 3].y - oy) / len;
        smin = double.MaxValue; smax = double.MinValue;
        for (int i = 0; i < 3; i++) { double si = (T[i].x - ox) * dx + (T[i].y - oy) * dy; if (si < smin) smin = si; if (si > smax) smax = si; }
        return smax - smin > 1e-12 * len;
    }

    /// <summary>点是否在三角形内（含边界，符号检查）。</summary>
    private static bool PointInTri2D(double px, double py, (double x, double y)[] T)
    {
        double d1 = (px - T[1].x) * (T[0].y - T[1].y) - (T[0].x - T[1].x) * (py - T[1].y);
        double d2 = (px - T[2].x) * (T[1].y - T[2].y) - (T[1].x - T[2].x) * (py - T[2].y);
        double d3 = (px - T[0].x) * (T[2].y - T[0].y) - (T[2].x - T[0].x) * (py - T[0].y);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>2D 线段 ab 与 cd 求交，t 在 ab 上、u 在 cd 上。</summary>
    private static bool SegSegIntersect2D(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, out double t, out double u)
    {
        t = u = 0;
        double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-20) return false;   // 平行
        t = ((cx - ax) * sy - (cy - ay) * sx) / den;
        u = ((cx - ax) * ry - (cy - ay) * rx) / den;
        return t >= -1e-12 && t <= 1 + 1e-12 && u >= -1e-12 && u <= 1 + 1e-12;
    }

    /// <summary>把 2D 线段 ab 裁到三角形 T 内（T 凸 → 至多一段）；退化为点算 false。</summary>
    private static bool ClipSegToTri2D((double x, double y) a, (double x, double y) b, (double x, double y)[] T,
        out (double x, double y) oa, out (double x, double y) ob)
    {
        oa = ob = default;
        double tMin = 2, tMax = -1; (double x, double y) pMin = default, pMax = default;
        void Hit(double t, double x, double y)
        {
            if (t < tMin) { tMin = t; pMin = (x, y); }
            if (t > tMax) { tMax = t; pMax = (x, y); }
        }
        if (PointInTri2D(a.x, a.y, T)) Hit(0, a.x, a.y);
        if (PointInTri2D(b.x, b.y, T)) Hit(1, b.x, b.y);
        for (int e = 0; e < 3; e++)
        {
            var p = T[e]; var q = T[(e + 1) % 3];
            if (SegSegIntersect2D(a.x, a.y, b.x, b.y, p.x, p.y, q.x, q.y, out double t, out _))
                Hit(t, a.x + t * (b.x - a.x), a.y + t * (b.y - a.y));
        }
        if (tMax < tMin) return false;
        oa = pMin; ob = pMax;
        double dx = ob.x - oa.x, dy = ob.y - oa.y;
        return dx * dx + dy * dy > 1e-20;
    }

    /// <summary>2D 点在源三角内的重心坐标插 Z（不钳，忠实原 liftZ）。</summary>
    private static (double x, double y, double z) LiftZ(double px, double py,
        (double x, double y, double z) v0, (double x, double y, double z) v1, (double x, double y, double z) v2)
    {
        double den = (v1.y - v2.y) * (v0.x - v2.x) + (v2.x - v1.x) * (v0.y - v2.y);
        if (Math.Abs(den) < 1e-20) return (px, py, v0.z);
        double u = ((v1.y - v2.y) * (px - v2.x) + (v2.x - v1.x) * (py - v2.y)) / den;
        double v = ((v2.y - v0.y) * (px - v2.x) + (v0.x - v2.x) * (py - v2.y)) / den;
        double w = 1 - u - v;
        return (px, py, u * v0.z + v * v1.z + w * v2.z);
    }

    /// <summary>
    /// 单弦快切：弦 A-B 两端各落在 T 的两条不同边上 → 含公共顶点的一片 + 另一侧四边形扇形拆两个，共 3 个。
    /// 输出顶点顺序沿 T 自身边界走向，原三角绕向原样保留。端点同边 / 有一端在三角内 → false，交约束剖分。
    /// </summary>
    private static bool SplitTriByChord2D((double x, double y)[] T, (double x, double y) A, (double x, double y) B, out (double x, double y)[][] sub)
    {
        sub = Array.Empty<(double x, double y)[]>();
        double scale = 0;
        for (int i = 0; i < 3; i++)
        {
            double dx = T[(i + 1) % 3].x - T[i].x, dy = T[(i + 1) % 3].y - T[i].y;
            scale = Math.Max(scale, Math.Sqrt(dx * dx + dy * dy));
        }
        if (scale <= 0) return false;
        double eps = scale * 1e-7;
        int EdgeOf((double x, double y) p)
        {
            int bestE = -1; double bestD = eps;
            for (int e = 0; e < 3; e++)
            {
                var a = T[e]; var b = T[(e + 1) % 3];
                double sx = b.x - a.x, sy = b.y - a.y, len2 = sx * sx + sy * sy;
                if (len2 < 1e-20) continue;
                double t = ((p.x - a.x) * sx + (p.y - a.y) * sy) / len2;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double dx = p.x - (a.x + t * sx), dy = p.y - (a.y + t * sy);
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < bestD) { bestD = d; bestE = e; }
            }
            return bestE;
        }
        int ea = EdgeOf(A), eb = EdgeOf(B);
        if (ea < 0 || eb < 0 || ea == eb) return false;
        var a2 = A; var b2 = B;
        if (!((ea == 0 && eb == 1) || (ea == 1 && eb == 2) || (ea == 2 && eb == 0)))
        { (a2, b2) = (b2, a2); (ea, eb) = (eb, ea); }
        int shared, far1, far2;
        if (ea == 0) { shared = 1; far1 = 2; far2 = 0; }
        else if (ea == 1) { shared = 2; far1 = 0; far2 = 1; }
        else { shared = 0; far1 = 1; far2 = 2; }
        sub = new[]
        {
            new[] { a2, T[shared], b2 },
            new[] { b2, T[far1], T[far2] },
            new[] { b2, T[far2], a2 },
        };
        return true;
    }

    /// <summary>子三角稳健定侧：3 顶点符号和多数派胜出，和为 0 才用形心兜底。</summary>
    private static int SideOfTri2D(SegGrid grid, (double x, double y) p0, (double x, double y) p1, (double x, double y) p2)
    {
        int sum = grid.SideAt(p0.x, p0.y) + grid.SideAt(p1.x, p1.y) + grid.SideAt(p2.x, p2.y);
        if (sum >= 1) return 1;
        if (sum <= -1) return -1;
        return grid.SideAt((p0.x + p1.x + p2.x) / 3.0, (p0.y + p1.y + p2.y) / 3.0);
    }

    /// <summary>拓扑闭合：每条边恰被 2 个三角引用（未焊接的 soup 判 false，正合适——soup 没有"闭合"语义）。</summary>
    private static bool IsMeshClosed(IReadOnlyList<(int a, int b, int c)> tris, int nv)
    {
        if (tris.Count < 3) return false;
        var count = new Dictionary<long, int>(tris.Count * 3, PackedKeyComparer.Instance);
        void E(int u, int w) { long k = ((long)Math.Min(u, w) << 32) | (uint)Math.Max(u, w); count[k] = count.TryGetValue(k, out int n) ? n + 1 : 1; }
        foreach (var t in tris) { if (!Valid(t, nv)) continue; E(t.a, t.b); E(t.b, t.c); E(t.c, t.a); }
        foreach (var n in count.Values) if (n != 2) return false;
        return true;
    }

    private static bool CurveClosed(List<(double x, double y, double z)> c, double tol)
    {
        if (c.Count < 3) return false;
        double dx = c[0].x - c[^1].x, dy = c[0].y - c[^1].y, dz = c[0].z - c[^1].z;
        return dx * dx + dy * dy + dz * dz <= 100.0 * tol * tol;
    }

    /// <summary>把 3D 段按端点同格(量化 tol)串成曲线：先从度=1 的开口端追，剩下的是闭环（忠实原 traceCurves）。</summary>
    private static List<List<(double x, double y, double z)>> TraceCurves(
        List<((double x, double y, double z) p0, (double x, double y, double z) p1)> segments, double tol)
    {
        var curves = new List<List<(double x, double y, double z)>>();
        if (segments.Count == 0) return curves;
        double inv = 1.0 / Math.Max(tol, 1e-15);
        var keyToVid = new Dictionary<(long, long, long), int>();
        var vidPos = new List<(double x, double y, double z)>();
        int VidOf((double x, double y, double z) p)
        {
            var k = ((long)Math.Floor(p.x * inv), (long)Math.Floor(p.y * inv), (long)Math.Floor(p.z * inv));
            if (keyToVid.TryGetValue(k, out int id)) return id;
            id = vidPos.Count; keyToVid[k] = id; vidPos.Add(p); return id;
        }
        var edges = new List<(int v0, int v1)>(segments.Count);
        var adj = new List<List<int>>();
        foreach (var (p0, p1) in segments)
        {
            int a = VidOf(p0), b = VidOf(p1);
            if (a == b) continue;
            int ei = edges.Count; edges.Add((a, b));
            while (adj.Count <= Math.Max(a, b)) adj.Add(new List<int>(2));
            adj[a].Add(ei); adj[b].Add(ei);
        }
        var used = new bool[edges.Count];
        int Other(int ei, int v) => edges[ei].v0 == v ? edges[ei].v1 : edges[ei].v0;
        int PickUnused(int v) { foreach (int ei in adj[v]) if (!used[ei]) return ei; return -1; }

        for (int startV = 0; startV < adj.Count; startV++)
        {
            int unused = 0; foreach (int ei in adj[startV]) if (!used[ei]) unused++;
            if (unused != 1) continue;
            var curve = new List<(double x, double y, double z)> { vidPos[startV] };
            int v = startV;
            while (true)
            {
                int ei = PickUnused(v); if (ei < 0) break;
                used[ei] = true; v = Other(ei, v); curve.Add(vidPos[v]);
            }
            if (curve.Count >= 2) curves.Add(curve);
        }
        for (int v0 = 0; v0 < adj.Count; v0++)
        {
            if (PickUnused(v0) < 0) continue;
            var curve = new List<(double x, double y, double z)> { vidPos[v0] };
            int v = v0;
            while (true)
            {
                int ei = PickUnused(v); if (ei < 0) break;
                used[ei] = true; v = Other(ei, v); curve.Add(vidPos[v]);
                if (v == v0) break;
            }
            if (curve.Count >= 2) curves.Add(curve);
        }
        return curves;
    }

    /// <summary>(x,y) 投到多段线上：最近段 + 段上参数 → 弧长 U。</summary>
    private static double ArcUOf(double x, double y, List<(double x, double y)> poly, double[] arc)
    {
        int best = -1; double bestT = 0, bestD2 = double.MaxValue;
        for (int i = 0; i + 1 < poly.Count; i++)
        {
            double ax = poly[i].x, ay = poly[i].y, sx = poly[i + 1].x - ax, sy = poly[i + 1].y - ay;
            double len2 = sx * sx + sy * sy; if (len2 < 1e-20) continue;
            double t = ((x - ax) * sx + (y - ay) * sy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double cx = ax + t * sx, cy = ay + t * sy, d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            if (d2 < bestD2) { bestD2 = d2; best = i; bestT = t; }
        }
        return best < 0 ? 0 : arc[best] + bestT * (arc[best + 1] - arc[best]);
    }

    /// <summary>逆映射：沿多段线走 U 弧长得 (x,y)，配 z=V。</summary>
    private static (double x, double y, double z) InvMapUV(double u, double v, List<(double x, double y)> poly, double[] arc)
    {
        if (u <= arc[0]) return (poly[0].x, poly[0].y, v);
        if (u >= arc[^1]) return (poly[^1].x, poly[^1].y, v);
        int k = 0;
        for (int i = 0; i + 1 < arc.Length; i++) if (u >= arc[i] && u <= arc[i + 1]) { k = i; break; }
        double segLen = arc[k + 1] - arc[k];
        double t = segLen < 1e-20 ? 0 : (u - arc[k]) / segLen;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return (poly[k].x + t * (poly[k + 1].x - poly[k].x), poly[k].y + t * (poly[k + 1].y - poly[k].y), v);
    }

    /// <summary>简单多边形耳切(凹凸皆可, CW/CCW 皆可)。退化输入提前退出、不死循环。</summary>
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
                if (Cross(a, b, c) <= 0) continue;
                bool anyInside = false;
                for (int k = 0; k < m; k++)
                {
                    int ik = ring[k];
                    if (ik == ia || ik == ib || ik == ic) continue;
                    var q = p[ik];
                    double d1 = Cross(q, a, b), d2 = Cross(q, b, c), d3 = Cross(q, c, a);
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

    /// <summary>
    /// 多段线段的 2D 均匀网格（CSR）：bbox 查询 + 精确最近段（从查询点所在格一圈圈外扩，
    /// 已扫方块边界到查询点的距离 ≥ 已知最近距离即可确认；段很少 / 外扩超 3 圈退回全扫）。
    /// </summary>
    private sealed class SegGrid
    {
        private readonly List<(double x, double y)> _p;
        public readonly int SegCount;
        private readonly double _minX, _minY, _maxX, _maxY, _cell, _onLine;
        private readonly int _nx, _ny;
        private readonly int[] _cellStart, _cellItems;

        public SegGrid(List<(double x, double y)> p, int targetCells, double onLineTol)
        {
            _p = p; SegCount = p.Count - 1; _onLine = onLineTol;
            _minX = _minY = double.MaxValue; _maxX = _maxY = double.MinValue;
            foreach (var q in p) { if (q.x < _minX) _minX = q.x; if (q.x > _maxX) _maxX = q.x; if (q.y < _minY) _minY = q.y; if (q.y > _maxY) _maxY = q.y; }
            double w = _maxX - _minX, h = _maxY - _minY;
            int cells = Math.Max(targetCells, SegCount);
            _cell = Math.Sqrt(Math.Max(w * h, 1.0) / Math.Max(1, cells));
            if (!(_cell > 0)) _cell = 1.0;
            _nx = Math.Max(1, (int)Math.Ceiling(w / _cell)); _ny = Math.Max(1, (int)Math.Ceiling(h / _cell));
            while ((long)_nx * _ny > 1_000_000L)
            {
                _cell *= 2; _nx = Math.Max(1, (int)Math.Ceiling(w / _cell)); _ny = Math.Max(1, (int)Math.Ceiling(h / _cell));
            }
            _cellStart = new int[_nx * _ny + 1];
            for (int si = 0; si < SegCount; si++)
            {
                Range(si, out int ix0, out int ix1, out int iy0, out int iy1);
                for (int iy = iy0; iy <= iy1; iy++) for (int ix = ix0; ix <= ix1; ix++) _cellStart[iy * _nx + ix + 1]++;
            }
            for (int i = 1; i < _cellStart.Length; i++) _cellStart[i] += _cellStart[i - 1];
            _cellItems = new int[_cellStart[^1]];
            var fill = new int[_nx * _ny]; Array.Copy(_cellStart, fill, fill.Length);
            for (int si = 0; si < SegCount; si++)
            {
                Range(si, out int ix0, out int ix1, out int iy0, out int iy1);
                for (int iy = iy0; iy <= iy1; iy++) for (int ix = ix0; ix <= ix1; ix++) _cellItems[fill[iy * _nx + ix]++] = si;
            }
        }

        private void Range(int si, out int ix0, out int ix1, out int iy0, out int iy1)
        {
            var a = _p[si]; var b = _p[si + 1];
            ix0 = Math.Max(0, (int)((Math.Min(a.x, b.x) - _minX) / _cell)); ix1 = Math.Min(_nx - 1, (int)((Math.Max(a.x, b.x) - _minX) / _cell));
            iy0 = Math.Max(0, (int)((Math.Min(a.y, b.y) - _minY) / _cell)); iy1 = Math.Min(_ny - 1, (int)((Math.Max(a.y, b.y) - _minY) / _cell));
        }

        public bool Overlaps(double x0, double y0, double x1, double y1)
            => !(_maxX < x0 || _minX > x1 || _maxY < y0 || _minY > y1);

        /// <summary>点到段 si 的平方距离；零长段返回 +∞。</summary>
        private double SegDist2(int si, double px, double py)
        {
            var a = _p[si]; var b = _p[si + 1];
            double sx = b.x - a.x, sy = b.y - a.y, len2 = sx * sx + sy * sy;
            if (len2 < 1e-20) return double.MaxValue;
            double t = ((px - a.x) * sx + (py - a.y) * sy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double dx = px - (a.x + t * sx), dy = py - (a.y + t * sy);
            return dx * dx + dy * dy;
        }

        private int BruteNearest(double px, double py)
        {
            int best = -1; double bestD2 = double.MaxValue;
            for (int si = 0; si < SegCount; si++) { double d2 = SegDist2(si, px, py); if (d2 < bestD2) { bestD2 = d2; best = si; } }
            return best;
        }

        public int NearestSeg(double px, double py)
        {
            if (SegCount <= 8) return BruteNearest(px, py);
            int cx = Math.Min(_nx - 1, Math.Max(0, (int)Math.Floor((px - _minX) / _cell)));
            int cy = Math.Min(_ny - 1, Math.Max(0, (int)Math.Floor((py - _minY) / _cell)));
            int best = -1; double bestD2 = double.MaxValue;
            void Scan(int ix, int iy)
            {
                if (ix < 0 || ix >= _nx || iy < 0 || iy >= _ny) return;
                int c = iy * _nx + ix;
                for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    int si = _cellItems[k]; double d2 = SegDist2(si, px, py);
                    if (d2 < bestD2) { bestD2 = d2; best = si; }
                }
            }
            const int maxRings = 3;
            for (int rr = 0; rr <= maxRings; rr++)
            {
                if (rr == 0) Scan(cx, cy);
                else
                {
                    for (int ix = cx - rr; ix <= cx + rr; ix++) { Scan(ix, cy - rr); Scan(ix, cy + rr); }
                    for (int iy = cy - rr + 1; iy <= cy + rr - 1; iy++) { Scan(cx - rr, iy); Scan(cx + rr, iy); }
                }
                if (best >= 0)
                {
                    double bx0 = _minX + (cx - rr) * _cell, bx1 = _minX + (cx + rr + 1) * _cell;
                    double by0 = _minY + (cy - rr) * _cell, by1 = _minY + (cy + rr + 1) * _cell;
                    double margin = Math.Min(Math.Min(px - bx0, bx1 - px), Math.Min(py - by0, by1 - py));
                    if (margin > 0 && bestD2 <= margin * margin) return best;
                }
            }
            return BruteNearest(px, py);
        }

        /// <summary>点相对多段线的左右侧：+1 左 / -1 右 / 0 在线上。</summary>
        public int SideAt(double px, double py)
        {
            int si = NearestSeg(px, py);
            if (si < 0) return 0;
            // 最近点落在与相邻段共享的顶点上（两段距离相等）时，取对该点"看得更正"的那段（单位方向叉积绝对值大者）。
            // 原版只认先到的那段：凸角外侧楔形区里会判反 —— 实测闭合环 (23,27)(71,22)(66,78)(31,64) 外的 (100,20)
            // 被判进圈内（对 (23,27)→(71,22) 的延长线它在左手 1 个单位，对 (71,22)→(66,78) 在右手 29 个单位）。
            double d2 = SegDist2(si, px, py);
            int best = si; double bestC = Math.Abs(UnitCross(si, px, py));
            for (int sj = si - 1; sj <= si + 1; sj += 2)
            {
                if (sj < 0 || sj >= SegCount || SegDist2(sj, px, py) > d2 + 1e-9 * (1 + d2)) continue;
                double c = Math.Abs(UnitCross(sj, px, py));
                if (c > bestC) { bestC = c; best = sj; }
            }
            double d = UnitCross(best, px, py);   // 带符号距离(到所选段所在直线)
            if (d > _onLine) return 1;
            if (d < -_onLine) return -1;
            return 0;
        }

        private double UnitCross(int si, double px, double py)
        {
            var a = _p[si]; var b = _p[si + 1];
            double sx = b.x - a.x, sy = b.y - a.y, len = Math.Sqrt(sx * sx + sy * sy);
            return len < 1e-300 ? 0 : (sx * (py - a.y) - sy * (px - a.x)) / len;
        }

        /// <summary>bbox 查询：stamp[si]==tick 表示本轮已收过，零分配去重。</summary>
        public void Query(double x0, double y0, double x1, double y1, List<int> outp, uint[] stamp, uint tick)
        {
            outp.Clear();
            int ix0 = Math.Max(0, (int)((x0 - _minX) / _cell)), ix1 = Math.Min(_nx - 1, (int)((x1 - _minX) / _cell));
            int iy0 = Math.Max(0, (int)((y0 - _minY) / _cell)), iy1 = Math.Min(_ny - 1, (int)((y1 - _minY) / _cell));
            if (ix1 < 0 || iy1 < 0 || ix0 >= _nx || iy0 >= _ny) return;
            for (int iy = iy0; iy <= iy1; iy++) for (int ix = ix0; ix <= ix1; ix++)
            {
                int c = iy * _nx + ix;
                for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    int si = _cellItems[k];
                    if (stamp[si] == tick) continue;
                    stamp[si] = tick; outp.Add(si);
                }
            }
        }
    }
}
