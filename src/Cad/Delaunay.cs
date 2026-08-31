using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// Delaunay 三角剖分（Bowyer-Watson）—— 散点 → 三角网（TIN 基础）。
/// 复用 <see cref="ArcMath.Circumcircle"/> 做外接圆判定。纯逻辑、可单测。
/// 返回三角形顶点索引三元组（索引指向输入点表）。
/// </summary>
public static class Delaunay
{
    public static List<(int a, int b, int c)> Triangulate(IReadOnlyList<(double x, double y)> input)
    {
        var result = new List<(int, int, int)>();
        int n = input.Count;
        if (n < 3) return result;

        // 点表 + 超级三角形三个远点(索引 n,n+1,n+2)
        var pts = new List<(double x, double y)>(input);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in input) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; }
        double dmax = System.Math.Max(maxX - minX, maxY - minY); if (dmax < 1e-9) dmax = 1;
        double midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;
        pts.Add((midX - 20 * dmax, midY - dmax));
        pts.Add((midX + 20 * dmax, midY - dmax));
        pts.Add((midX, midY + 20 * dmax));
        int s0 = n, s1 = n + 1, s2 = n + 2;

        var tris = new List<(int a, int b, int c)> { (s0, s1, s2) };

        for (int ip = 0; ip < n; ip++)
        {
            var (px, py) = pts[ip];
            var bad = new List<(int a, int b, int c)>();
            foreach (var t in tris)
                if (InCircumcircle(pts, t, px, py)) bad.Add(t);

            // 空腔边界 = 只属于一个坏三角形的边
            var edgeCount = new Dictionary<(int, int), int>();
            foreach (var t in bad)
            {
                Bump(edgeCount, t.a, t.b); Bump(edgeCount, t.b, t.c); Bump(edgeCount, t.c, t.a);
            }
            foreach (var t in bad) tris.Remove(t);
            foreach (var kv in edgeCount)
                if (kv.Value == 1) tris.Add((kv.Key.Item1, kv.Key.Item2, ip));
        }

        // 去掉含超级三角形顶点的三角形
        foreach (var t in tris)
            if (t.a < n && t.b < n && t.c < n) result.Add(t);
        return result;
    }

    /// <summary>
    /// 约束 Delaunay：在无约束三角网基础上，把每条约束边(constraints，索引对)嵌入三角网
    /// (忠实原「多段线作约束嵌入三角网」——断层/山脊等 breakline 必须是三角边)。
    /// 算法：对每条不在网中的约束边，删除被其穿过的三角形→形成孔洞，以约束边把孔洞分成两侧
    /// 伪多边形，各自耳切重剖(约束边即成两侧共享边)。结果仍覆盖凸包(总面积不变)。纯逻辑、可单测。
    /// </summary>
    public static List<(int a, int b, int c)> TriangulateConstrained(
        IReadOnlyList<(double x, double y)> input, IReadOnlyList<(int u, int v)> constraints)
    {
        var tris = Triangulate(input);
        if (constraints == null) return tris;
        foreach (var (u, v) in constraints)
        {
            if (u < 0 || v < 0 || u == v || u >= input.Count || v >= input.Count) continue;
            InsertConstraint(input, tris, u, v);
        }
        return tris;
    }

    private static bool HasEdge(List<(int a, int b, int c)> tris, int u, int v)
    {
        foreach (var t in tris)
            if ((t.a == u && t.b == v) || (t.b == u && t.c == v) || (t.c == u && t.a == v) ||
                (t.a == v && t.b == u) || (t.b == v && t.c == u) || (t.c == v && t.a == u)) return true;
        return false;
    }

    private static void InsertConstraint(IReadOnlyList<(double x, double y)> pts, List<(int a, int b, int c)> tris, int u, int v)
    {
        if (HasEdge(tris, u, v)) return;
        // 若有顶点落在约束边上(共线且严格居中)→ 在该点分段递归(breakline 穿过网点很常见)
        int mid = VertexOnSegment(pts, u, v);
        if (mid >= 0) { InsertConstraint(pts, tris, u, mid); InsertConstraint(pts, tris, mid, v); return; }
        // 找被约束边穿过内部的三角形
        var crossed = new List<int>();
        for (int i = 0; i < tris.Count; i++)
            if (TriangleCrossed(pts, tris[i], u, v)) crossed.Add(i);
        if (crossed.Count == 0) return;   // 退化/共线：跳过该约束(无破坏)
        // 孔洞边界 = 被删三角集里只出现一次的边
        var edgeCnt = new Dictionary<(int, int), int>();
        void Bump2(int a, int b) { var k = a < b ? (a, b) : (b, a); edgeCnt[k] = edgeCnt.TryGetValue(k, out int c) ? c + 1 : 1; }
        foreach (int ci in crossed) { var t = tris[ci]; Bump2(t.a, t.b); Bump2(t.b, t.c); Bump2(t.c, t.a); }
        var boundary = new List<(int, int)>();
        foreach (var kv in edgeCnt) if (kv.Value == 1) boundary.Add(kv.Key);
        // 删被穿三角(降序删)
        crossed.Sort((a, b) => b.CompareTo(a));
        foreach (int ci in crossed) tris.RemoveAt(ci);
        // 边界排成环
        var loop = OrderLoop(boundary);
        if (loop == null || loop.Count < 3) return;
        int iu = loop.IndexOf(u), iv = loop.IndexOf(v);
        if (iu < 0 || iv < 0) return;
        EarClip(pts, tris, SubLoop(loop, iu, iv));   // u..v 侧
        EarClip(pts, tris, SubLoop(loop, iv, iu));   // v..u 侧
    }

    private static double Orient((double x, double y) a, (double x, double y) b, (double x, double y) c)
        => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    // 返回落在开区间(u,v)线段上、且最靠近 u 的顶点索引；无则 -1。用于约束边穿过网点时分段。
    private static int VertexOnSegment(IReadOnlyList<(double x, double y)> pts, int u, int v)
    {
        var a = pts[u]; var b = pts[v];
        double abLen2 = (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y);
        if (abLen2 < 1e-18) return -1;
        double tol = 1e-7 * System.Math.Sqrt(abLen2);   // 共线判定容差(相对边长)
        int best = -1; double bestT = double.MaxValue;
        for (int w = 0; w < pts.Count; w++)
        {
            if (w == u || w == v) continue;
            var p = pts[w];
            double cross = Orient(a, b, p);
            if (System.Math.Abs(cross) > tol * System.Math.Sqrt(abLen2)) continue;   // 不共线(面积≈2×距离×长)
            double t = ((p.x - a.x) * (b.x - a.x) + (p.y - a.y) * (b.y - a.y)) / abLen2;
            if (t > 1e-9 && t < 1 - 1e-9 && t < bestT) { bestT = t; best = w; }        // 严格居中
        }
        return best;
    }

    private static bool ProperIntersect((double x, double y) a, (double x, double y) b, (double x, double y) c, (double x, double y) d)
    {
        double d1 = Orient(a, b, c), d2 = Orient(a, b, d), d3 = Orient(c, d, a), d4 = Orient(c, d, b);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static bool TriangleCrossed(IReadOnlyList<(double x, double y)> pts, (int a, int b, int c) t, int u, int v)
    {
        return EdgeCrossed(pts, t.a, t.b, u, v) || EdgeCrossed(pts, t.b, t.c, u, v) || EdgeCrossed(pts, t.c, t.a, u, v);
    }
    private static bool EdgeCrossed(IReadOnlyList<(double x, double y)> pts, int p, int q, int u, int v)
    {
        if (p == u || p == v || q == u || q == v) return false;   // 与约束边共端点的边不算穿过
        return ProperIntersect(pts[u], pts[v], pts[p], pts[q]);
    }

    private static List<int>? OrderLoop(List<(int, int)> edges)
    {
        if (edges.Count < 3) return null;
        var adj = new Dictionary<int, List<int>>();
        void Add(int a, int b) { if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<int>(); l.Add(b); }
        foreach (var (a, b) in edges) { Add(a, b); Add(b, a); }
        foreach (var kv in adj) if (kv.Value.Count != 2) return null;   // 非简单环 → 放弃
        var loop = new List<int>();
        int start = edges[0].Item1, prev = -1, cur = start;
        do
        {
            loop.Add(cur);
            var nb = adj[cur];
            int next = nb[0] != prev ? nb[0] : nb[1];
            prev = cur; cur = next;
            if (loop.Count > edges.Count + 1) return null;
        } while (cur != start);
        return loop;
    }

    private static List<int> SubLoop(List<int> loop, int i, int j)
    {
        var r = new List<int>();
        int n = loop.Count, k = i;
        while (true) { r.Add(loop[k]); if (k == j) break; k = (k + 1) % n; }
        return r;
    }

    private static double SignedArea(IReadOnlyList<(double x, double y)> pts, List<int> poly)
    {
        double s = 0;
        for (int i = 0; i < poly.Count; i++) { var a = pts[poly[i]]; var b = pts[poly[(i + 1) % poly.Count]]; s += a.x * b.y - b.x * a.y; }
        return s / 2;
    }

    private static void EarClip(IReadOnlyList<(double x, double y)> pts, List<(int a, int b, int c)> tris, List<int> poly)
    {
        if (poly.Count < 3) return;
        var idx = new List<int>(poly);
        if (SignedArea(pts, idx) < 0) idx.Reverse();   // 统一 CCW
        int guard = idx.Count * idx.Count + 16;
        while (idx.Count >= 3 && guard-- > 0)
        {
            bool clipped = false;
            int m = idx.Count;
            for (int i = 0; i < m; i++)
            {
                int ia = idx[(i - 1 + m) % m], ib = idx[i], ic = idx[(i + 1) % m];
                if (Orient(pts[ia], pts[ib], pts[ic]) <= 0) continue;   // 非凸顶点(CCW 下)
                bool ear = true;
                for (int k = 0; k < m; k++)
                {
                    int p = idx[k];
                    if (p == ia || p == ib || p == ic) continue;
                    if (PointInTri(pts[p], pts[ia], pts[ib], pts[ic])) { ear = false; break; }
                }
                if (ear) { tris.Add((ia, ib, ic)); idx.RemoveAt(i); clipped = true; break; }
            }
            if (!clipped) break;   // 退化保护
        }
    }

    private static bool PointInTri((double x, double y) p, (double x, double y) a, (double x, double y) b, (double x, double y) c)
    {
        double d1 = Orient(a, b, p), d2 = Orient(b, c, p), d3 = Orient(c, a, p);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);   // 同侧(含边)→ 内部
    }

    /// <summary>三角网 → 去重的三角边线实体（TIN 线框渲染）。</summary>
    public static List<SceneEntity> BuildEdges(
        IReadOnlyList<(double x, double y)> pts, List<(int a, int b, int c)> tris, float r, float g, float b)
    {
        var seen = new HashSet<(int, int)>();
        var list = new List<SceneEntity>();
        void Edge(int u, int v)
        {
            var k = u < v ? (u, v) : (v, u);
            if (seen.Add(k))
                list.Add(new LineEntity { X0 = pts[u].x, Y0 = pts[u].y, X1 = pts[v].x, Y1 = pts[v].y, Cr = r, Cg = g, Cb = b });
        }
        foreach (var t in tris) { Edge(t.a, t.b); Edge(t.b, t.c); Edge(t.c, t.a); }
        return list;
    }

    private static void Bump(Dictionary<(int, int), int> m, int u, int v)
    {
        var key = u < v ? (u, v) : (v, u);
        m[key] = m.TryGetValue(key, out int c) ? c + 1 : 1;
    }

    private static bool InCircumcircle(List<(double x, double y)> pts, (int a, int b, int c) t, double px, double py)
    {
        var A = pts[t.a]; var B = pts[t.b]; var C = pts[t.c];
        var cc = ArcMath.Circumcircle(A.x, A.y, B.x, B.y, C.x, C.y);
        if (cc == null) return false;   // 退化三角形
        double dx = px - cc.Value.cx, dy = py - cc.Value.cy;
        return dx * dx + dy * dy <= cc.Value.r * cc.Value.r + 1e-9;
    }
}
