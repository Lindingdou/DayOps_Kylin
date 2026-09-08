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
    /// <summary>
    /// 散点 Delaunay（Bowyer-Watson）。
    ///
    /// 三处性能要点（等值线建面动辄几万顶点，原实现在这个量级要几十秒）：
    ///   ① 外接圆**建三角形时算一次**存起来，不再每次判定都重算 —— 原来是
    ///      「每个点 × 每个三角形」都调一次 Circumcircle，这是最大的开销；
    ///   ② 删除坏三角形改用存活标记 + 尾部压缩，不再 List.Remove（每次 O(n) 线性查找 + 搬移）；
    ///   ③ 插入顺序按网格蛇形排过 —— 相邻点落在相邻位置，坏三角形集中，命中更快；
    ///   ④ 三角形按**外接圆包围盒**登记进均匀网格：新点只需检查自己所在格子里的三角形，
    ///      不再逐个扫全表。点若落在某三角形的外接圆内，必然落在其包围盒内，因而必在
    ///      登记过的格子里 —— 不会漏。外接圆特别大的（超级三角形、凸包附近那些）
    ///      跨格太多，单独放 oversized 表每次都查，数量很少。
    /// 算法与结果口径不变，仍是同一套 Bowyer-Watson。
    /// </summary>
    public static List<(int a, int b, int c)> Triangulate(IReadOnlyList<(double x, double y)> input)
    {
        var result = new List<(int, int, int)>();
        int n = input.Count;
        if (n < 3) return result;

        var pts = new List<(double x, double y)>(input);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in input) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; }
        double dmax = System.Math.Max(maxX - minX, maxY - minY); if (dmax < 1e-9) dmax = 1;
        double midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;
        pts.Add((midX - 20 * dmax, midY - dmax));
        pts.Add((midX + 20 * dmax, midY - dmax));
        pts.Add((midX, midY + 20 * dmax));
        int s0 = n, s1 = n + 1, s2 = n + 2;

        // 三角形表：顶点索引 + 外接圆(圆心/半径²) + 存活标记
        int cap = System.Math.Max(64, n * 4);
        var ta = new int[cap]; var tb = new int[cap]; var tc = new int[cap];
        var ccx = new double[cap]; var ccy = new double[cap]; var ccr = new double[cap];
        var alive = new bool[cap];
        int count = 0, deadCount = 0;

        void Grow()
        {
            if (count < ta.Length) return;
            int m = ta.Length * 2;
            System.Array.Resize(ref ta, m); System.Array.Resize(ref tb, m); System.Array.Resize(ref tc, m);
            System.Array.Resize(ref ccx, m); System.Array.Resize(ref ccy, m); System.Array.Resize(ref ccr, m);
            System.Array.Resize(ref alive, m);
        }

        // 均匀网格：三角形按外接圆包围盒登记，新点只查自己所在格子
        int gN = System.Math.Clamp((int)System.Math.Sqrt(n / 2.0), 1, 512);
        double gw = (maxX - minX) > 1e-12 ? (maxX - minX) / gN : 1;
        double gh = (maxY - minY) > 1e-12 ? (maxY - minY) / gN : 1;
        const int MaxCellsPerTri = 16;              // 跨格超过这个数就算"过大", 进 oversized
        var cells = new List<int>[gN * gN];
        var oversized = new List<int>();

        int Gx(double x) => System.Math.Clamp((int)((x - minX) / gw), 0, gN - 1);
        int Gy(double y) => System.Math.Clamp((int)((y - minY) / gh), 0, gN - 1);

        void Register(int t)
        {
            double r = System.Math.Sqrt(ccr[t]);
            int x0 = Gx(ccx[t] - r), x1 = Gx(ccx[t] + r), y0 = Gy(ccy[t] - r), y1 = Gy(ccy[t] + r);
            long span = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
            if (span > MaxCellsPerTri) { oversized.Add(t); return; }
            for (int gy = y0; gy <= y1; gy++)
                for (int gx = x0; gx <= x1; gx++)
                {
                    int ci = gy * gN + gx;
                    (cells[ci] ??= new List<int>()).Add(t);
                }
        }

        void AddTri(int a, int b, int c)
        {
            var A = pts[a]; var B = pts[b]; var C = pts[c];
            var cc = ArcMath.Circumcircle(A.x, A.y, B.x, B.y, C.x, C.y);
            if (cc == null) return;   // 退化(共线)三角形不入表
            Grow();
            ta[count] = a; tb[count] = b; tc[count] = c;
            ccx[count] = cc.Value.cx; ccy[count] = cc.Value.cy; ccr[count] = cc.Value.r * cc.Value.r;
            alive[count] = true;
            Register(count);
            count++;
        }

        // 压缩：挤掉死三角形并按新下标重建网格(下标变了, 格子里的旧索引全部失效)
        void Compact()
        {
            int w = 0;
            for (int r = 0; r < count; r++)
            {
                if (!alive[r]) continue;
                if (w != r)
                {
                    ta[w] = ta[r]; tb[w] = tb[r]; tc[w] = tc[r];
                    ccx[w] = ccx[r]; ccy[w] = ccy[r]; ccr[w] = ccr[r]; alive[w] = true;
                }
                w++;
            }
            count = w; deadCount = 0;
            System.Array.Clear(cells, 0, cells.Length);
            oversized.Clear();
            for (int t = 0; t < count; t++) Register(t);
        }

        AddTri(s0, s1, s2);

        var edgeCount = new Dictionary<(int, int), int>();
        var badEdges = new List<(int u, int v)>();
        foreach (int ip in SpatialOrder(input, minX, minY, maxX, maxY))
        {
            var (px, py) = pts[ip];
            edgeCount.Clear();

            void Kill(List<int>? bucket)
            {
                if (bucket == null) return;
                for (int i = 0; i < bucket.Count; i++)
                {
                    int t = bucket[i];
                    if (!alive[t]) continue;
                    double dx = px - ccx[t], dy = py - ccy[t];
                    if (dx * dx + dy * dy > ccr[t]) continue;   // 圆外
                    alive[t] = false; deadCount++;
                    Bump(edgeCount, ta[t], tb[t]); Bump(edgeCount, tb[t], tc[t]); Bump(edgeCount, tc[t], ta[t]);
                }
            }
            Kill(cells[Gy(py) * gN + Gx(px)]);
            Kill(oversized);

            badEdges.Clear();
            foreach (var kv in edgeCount) if (kv.Value == 1) badEdges.Add(kv.Key);
            foreach (var (u, v) in badEdges) AddTri(u, v, ip);
            if (deadCount > 4096 && deadCount * 2 > count) Compact();
        }

        for (int t = 0; t < count; t++)
            if (alive[t] && ta[t] < n && tb[t] < n && tc[t] < n) result.Add((ta[t], tb[t], tc[t]));
        return result;
    }

    /// <summary>
    /// 插入顺序：按网格蛇形走（行内左右交替），让相邻插入的点在平面上也相邻。
    /// 坏三角形因此集中在上一次的邻域，判定命中更快、空腔更小。
    /// </summary>
    private static int[] SpatialOrder(IReadOnlyList<(double x, double y)> pts, double minX, double minY, double maxX, double maxY)
    {
        int n = pts.Count;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        double w = maxX - minX, h = maxY - minY;
        if (n < 64 || (w < 1e-12 && h < 1e-12)) return order;

        int cells = System.Math.Max(1, (int)System.Math.Sqrt(n / 2.0));
        double cw = w > 1e-12 ? w / cells : 1, ch = h > 1e-12 ? h / cells : 1;
        var key = new long[n];
        for (int i = 0; i < n; i++)
        {
            int gx = System.Math.Clamp((int)((pts[i].x - minX) / cw), 0, cells - 1);
            int gy = System.Math.Clamp((int)((pts[i].y - minY) / ch), 0, cells - 1);
            if ((gy & 1) == 1) gx = cells - 1 - gx;      // 蛇形：奇数行反向
            key[i] = (long)gy * cells + gx;
        }
        System.Array.Sort(key, order);
        return order;
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

        // 边计数表(边 → 有几个三角形用到它)。等值线的相邻顶点绝大多数**本来就已经是三角边**,
        // 有了它这一步是 O(1) 直接跳过; 原来每条约束都要扫一遍整张网(m×t 次比较), 三万顶点就要一秒多。
        var edges = new Dictionary<(int, int), int>(tris.Count * 2);
        foreach (var t in tris) AddTriEdges(edges, t, +1);

        foreach (var (u, v) in constraints)
        {
            if (u < 0 || v < 0 || u == v || u >= input.Count || v >= input.Count) continue;
            InsertConstraint(input, tris, edges, u, v);
        }
        return tris;
    }

    /// <summary>三角形三条边的计数 ±1（delta=+1 加入 / -1 移除）；计数归零即从表中删掉。</summary>
    private static void AddTriEdges(Dictionary<(int, int), int> edges, (int a, int b, int c) t, int delta)
    {
        Edge(t.a, t.b); Edge(t.b, t.c); Edge(t.c, t.a);
        void Edge(int u, int v)
        {
            var k = u < v ? (u, v) : (v, u);
            int c = edges.TryGetValue(k, out int cur) ? cur + delta : delta;
            if (c <= 0) edges.Remove(k); else edges[k] = c;
        }
    }

    private static bool HasEdge(Dictionary<(int, int), int> edges, int u, int v)
        => edges.ContainsKey(u < v ? (u, v) : (v, u));

    private static void InsertConstraint(IReadOnlyList<(double x, double y)> pts, List<(int a, int b, int c)> tris,
                                         Dictionary<(int, int), int> edges, int u, int v)
    {
        if (HasEdge(edges, u, v)) return;
        // 若有顶点落在约束边上(共线且严格居中)→ 在该点分段递归(breakline 穿过网点很常见)
        int mid = VertexOnSegment(pts, u, v);
        if (mid >= 0) { InsertConstraint(pts, tris, edges, u, mid); InsertConstraint(pts, tris, edges, mid, v); return; }
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
        // 删被穿三角(降序删), 同步扣减边计数
        crossed.Sort((a, b) => b.CompareTo(a));
        foreach (int ci in crossed) { AddTriEdges(edges, tris[ci], -1); tris.RemoveAt(ci); }
        // 边界排成环
        var loop = OrderLoop(boundary);
        if (loop == null || loop.Count < 3) return;
        int iu = loop.IndexOf(u), iv = loop.IndexOf(v);
        if (iu < 0 || iv < 0) return;
        int before = tris.Count;
        EarClip(pts, tris, SubLoop(loop, iu, iv));   // u..v 侧
        EarClip(pts, tris, SubLoop(loop, iv, iu));   // v..u 侧
        for (int i = before; i < tris.Count; i++) AddTriEdges(edges, tris[i], +1);
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

    /// <summary>
    /// 裁剪三角网：三角剖分后只保留质心落在闭合边界多边形内的三角形
    /// (忠实原「用闭合多段线裁剪三角网」——不规则域如矿坑轮廓建面)。boundary &lt;3 点则不裁。
    /// </summary>
    public static List<(int a, int b, int c)> TriangulateClipped(
        IReadOnlyList<(double x, double y)> input, IReadOnlyList<(double x, double y)> boundary)
    {
        var tris = Triangulate(input);
        if (boundary == null || boundary.Count < 3) return tris;
        var kept = new List<(int a, int b, int c)>();
        foreach (var t in tris)
        {
            double cx = (input[t.a].x + input[t.b].x + input[t.c].x) / 3;
            double cy = (input[t.a].y + input[t.b].y + input[t.c].y) / 3;
            if (LineMath.PointInPolygon(cx, cy, boundary)) kept.Add(t);
        }
        return kept;
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
