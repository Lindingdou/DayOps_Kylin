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
    /// <summary>
    /// 无约束 Delaunay。实现改用 <see cref="DelaunatorCore"/>（O(n log n)），
    /// 与原版内核 LasLib 里内置的 delaunator 同一算法 —— 旧的 Bowyer-Watson 是 O(n²)，
    /// 等值线几万顶点就把界面卡死。
    /// </summary>
    public static List<(int a, int b, int c)> Triangulate(IReadOnlyList<(double x, double y)> input)
    {
        var result = new List<(int, int, int)>();
        if (input == null || input.Count < 3) return result;
        var d = DelaunatorCore.Build(input);
        for (int t = 0; t < d.Triangles.Length; t += 3)
            result.Add((d.Triangles[t], d.Triangles[t + 1], d.Triangles[t + 2]));
        return result;
    }

    /// <summary>
    /// 约束 Delaunay：在无约束三角网基础上，把每条约束边(constraints，索引对)嵌入三角网
    /// (忠实原「多段线作约束嵌入三角网」——断层/山脊等 breakline 必须是三角边)。
    /// 算法：对每条不在网中的约束边，删除被其穿过的三角形→形成孔洞，以约束边把孔洞分成两侧
    /// 伪多边形，各自耳切重剖(约束边即成两侧共享边)。结果仍覆盖凸包(总面积不变)。纯逻辑、可单测。
    /// </summary>
    /// <summary>
    /// 约束 Delaunay：在无约束三角网基础上，把每条约束边(constraints，索引对)嵌入三角网
    /// (忠实原「多段线作约束嵌入三角网」——断层/山脊等 breakline 必须是三角边)。
    /// 算法：对每条不在网中的约束边，删除被其穿过的三角形→形成孔洞，以约束边把孔洞分成两侧
    /// 伪多边形，各自耳切重剖(约束边即成两侧共享边)。结果仍覆盖凸包(总面积不变)。纯逻辑、可单测。
    /// </summary>
    public static List<(int a, int b, int c)> TriangulateConstrained(
        IReadOnlyList<(double x, double y)> input, IReadOnlyList<(int u, int v)> constraints)
        => TriangulateConstrained(input, constraints, out _, out _);

    /// <summary>
    /// 约束 Delaunay，并回报嵌入了几条、因预算保护跳过了几条。
    ///
    /// 预算是硬要求：不在网中的约束要扫一遍整张网找被穿过的三角形，几万条这种约束就是十亿次量级，
    /// 界面直接卡死（原版遇到同样的坏数据是"能跑完但结果不对"，我们不能比它更差 —— 宁可少嵌几条约束
    /// 也必须把面建出来）。跳过的条数如实报给用户，别让人以为结果是完整的。
    /// </summary>
    public static List<(int a, int b, int c)> TriangulateConstrained(
        IReadOnlyList<(double x, double y)> input, IReadOnlyList<(int u, int v)> constraints,
        out int inserted, out int skipped, long budget = 300_000_000)
    {
        inserted = 0; skipped = 0;
        var tris = Triangulate(input);
        if (constraints == null) return tris;

        // 边计数表(边 → 有几个三角形用到它)。等值线的相邻顶点绝大多数**本来就已经是三角边**,
        // 有了它这一步是 O(1) 直接跳过; 原来每条约束都要扫一遍整张网(m×t 次比较), 三万顶点就要一秒多。
        var edges = new Dictionary<(int, int), int>(tris.Count * 2);
        foreach (var t in tris) AddTriEdges(edges, t, +1);

        var grid = new PointGrid(input);
        long work = 0;
        foreach (var (u, v) in constraints)
        {
            if (u < 0 || v < 0 || u == v || u >= input.Count || v >= input.Count) continue;
            var key = u < v ? (u, v) : (v, u);
            if (edges.ContainsKey(key)) { inserted++; continue; }        // 已是网中的边: O(1)
            if (work >= budget) { skipped++; continue; }                  // 预算用尽: 跳过, 但面照建
            work += tris.Count;                                           // 这条要扫一遍整张网
            InsertConstraint(input, tris, edges, u, v, grid);
            inserted++;
        }
        return tris;
    }

    /// <summary>
    /// 顶点的均匀网格索引 —— 供"找落在约束边上的顶点"用。
    /// 此前那步是扫全部顶点(每条约束 O(n))，几万顶点时和扫全网一样要命。
    /// 顶点在嵌入过程中不增不减(不加 Steiner 点)，所以建一次就一直有效。
    /// </summary>
    private sealed class PointGrid
    {
        private readonly IReadOnlyList<(double x, double y)> _pts;
        private readonly List<int>[] _cells;
        private readonly int _g;
        private readonly double _minX, _minY, _cw, _ch;

        public PointGrid(IReadOnlyList<(double x, double y)> pts)
        {
            _pts = pts;
            double maxX = double.MinValue, maxY = double.MinValue;
            _minX = double.MaxValue; _minY = double.MaxValue;
            foreach (var p in pts)
            {
                if (p.x < _minX) _minX = p.x; if (p.x > maxX) maxX = p.x;
                if (p.y < _minY) _minY = p.y; if (p.y > maxY) maxY = p.y;
            }
            _g = System.Math.Clamp((int)System.Math.Sqrt(pts.Count / 2.0), 1, 512);
            _cw = maxX - _minX > 1e-12 ? (maxX - _minX) / _g : 1;
            _ch = maxY - _minY > 1e-12 ? (maxY - _minY) / _g : 1;
            _cells = new List<int>[_g * _g];
            for (int i = 0; i < pts.Count; i++) (_cells[Cell(pts[i].x, pts[i].y)] ??= new List<int>()).Add(i);
        }

        private int Ix(double x) => System.Math.Clamp((int)((x - _minX) / _cw), 0, _g - 1);
        private int Iy(double y) => System.Math.Clamp((int)((y - _minY) / _ch), 0, _g - 1);
        private int Cell(double x, double y) => Iy(y) * _g + Ix(x);

        /// <summary>枚举可能落在线段 ab 附近的顶点(按包围盒覆盖的格子)。</summary>
        public IEnumerable<int> Near((double x, double y) a, (double x, double y) b)
        {
            int x0 = Ix(System.Math.Min(a.x, b.x)), x1 = Ix(System.Math.Max(a.x, b.x));
            int y0 = Iy(System.Math.Min(a.y, b.y)), y1 = Iy(System.Math.Max(a.y, b.y));
            for (int gy = y0; gy <= y1; gy++)
                for (int gx = x0; gx <= x1; gx++)
                {
                    var c = _cells[gy * _g + gx];
                    if (c == null) continue;
                    foreach (int i in c) yield return i;
                }
        }
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
                                         Dictionary<(int, int), int> edges, int u, int v, PointGrid grid)
    {
        if (HasEdge(edges, u, v)) return;
        // 若有顶点落在约束边上(共线且严格居中)→ 在该点分段递归(breakline 穿过网点很常见)
        int mid = VertexOnSegment(pts, u, v, grid);
        if (mid >= 0) { InsertConstraint(pts, tris, edges, u, mid, grid); InsertConstraint(pts, tris, edges, mid, v, grid); return; }
        // 找被约束边穿过内部的三角形。先用包围盒粗筛: 等值线的约束段都很短,
        // 绝大多数三角形连包围盒都不重叠, 几次比较就排掉, 不必做完整的相交判定。
        var pa = pts[u]; var pb = pts[v];
        double sminX = System.Math.Min(pa.x, pb.x), smaxX = System.Math.Max(pa.x, pb.x);
        double sminY = System.Math.Min(pa.y, pb.y), smaxY = System.Math.Max(pa.y, pb.y);
        var crossed = new List<int>();
        for (int i = 0; i < tris.Count; i++)
        {
            var t = tris[i];
            var p0 = pts[t.a]; var p1 = pts[t.b]; var p2 = pts[t.c];
            if (System.Math.Min(System.Math.Min(p0.x, p1.x), p2.x) > smaxX) continue;
            if (System.Math.Max(System.Math.Max(p0.x, p1.x), p2.x) < sminX) continue;
            if (System.Math.Min(System.Math.Min(p0.y, p1.y), p2.y) > smaxY) continue;
            if (System.Math.Max(System.Math.Max(p0.y, p1.y), p2.y) < sminY) continue;
            if (TriangleCrossed(pts, t, u, v)) crossed.Add(i);
        }
        if (crossed.Count == 0) return;   // 退化/共线：跳过该约束(无破坏)
        // 孔洞边界 = 被删三角集里只出现一次的边
        var edgeCnt = new Dictionary<(int, int), int>();
        void Bump2(int a, int b) { var k = a < b ? (a, b) : (b, a); edgeCnt[k] = edgeCnt.TryGetValue(k, out int c) ? c + 1 : 1; }
        foreach (int ci in crossed) { var t = tris[ci]; Bump2(t.a, t.b); Bump2(t.b, t.c); Bump2(t.c, t.a); }
        var boundary = new List<(int, int)>();
        foreach (var kv in edgeCnt) if (kv.Value == 1) boundary.Add(kv.Key);
        // **先算清楚能不能重剖成功, 再动手删**。此前是先删被穿的三角形, 重剖失败就直接 return,
        // 网上留下一个大洞 —— 几千条硬约束下来整张网被啃光(实测 14400 点只剩 749 个三角形,
        // 表现就是"建不出来")。宁可这条约束不嵌, 也不能把已经建好的面破坏掉。
        var loop = OrderLoop(boundary);
        if (loop == null || loop.Count < 3) return;
        int iu = loop.IndexOf(u), iv = loop.IndexOf(v);
        if (iu < 0 || iv < 0) return;

        var refill = new List<(int a, int b, int c)>();
        EarClip(pts, refill, SubLoop(loop, iu, iv));   // u..v 侧
        EarClip(pts, refill, SubLoop(loop, iv, iu));   // v..u 侧

        // 面积必须守恒: 补回来的三角面积应与删掉的相当, 否则说明耳切没填满, 同样会留洞
        double removedArea = 0, refillArea = 0;
        foreach (int ci in crossed) removedArea += TriArea(pts, tris[ci]);
        foreach (var t in refill) refillArea += TriArea(pts, t);
        if (refill.Count == 0 || System.Math.Abs(refillArea - removedArea) > removedArea * 1e-6 + 1e-9) return;

        // 到这一步才真正提交: 删被穿三角(降序删), 同步维护边计数
        crossed.Sort((a, b) => b.CompareTo(a));
        foreach (int ci in crossed) { AddTriEdges(edges, tris[ci], -1); tris.RemoveAt(ci); }
        foreach (var t in refill) { tris.Add(t); AddTriEdges(edges, t, +1); }
    }

    /// <summary>三角形面积(绝对值)。用于校验重剖是否把删掉的区域填满。</summary>
    private static double TriArea(IReadOnlyList<(double x, double y)> pts, (int a, int b, int c) t)
        => System.Math.Abs(Orient(pts[t.a], pts[t.b], pts[t.c])) / 2;

    private static double Orient((double x, double y) a, (double x, double y) b, (double x, double y) c)
        => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    // 返回落在开区间(u,v)线段上、且最靠近 u 的顶点索引；无则 -1。用于约束边穿过网点时分段。
    private static int VertexOnSegment(IReadOnlyList<(double x, double y)> pts, int u, int v, PointGrid grid)
    {
        var a = pts[u]; var b = pts[v];
        double abLen2 = (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y);
        if (abLen2 < 1e-18) return -1;
        double tol = 1e-7 * System.Math.Sqrt(abLen2);   // 共线判定容差(相对边长)
        int best = -1; double bestT = double.MaxValue;
        foreach (int w in grid.Near(a, b))
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
