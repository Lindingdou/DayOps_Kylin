using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 多段线嵌入三角网（EMBED）—— 2.5D 落面 + 原位保形细分。
///
/// 此前的做法是把网顶点 + 线顶点合在一起整张重做约束 Delaunay：地形网原有的三角结构(等值线约束边)被推倒重来，
/// 线顶点之间的直段又只在两端贴面，中间穿山越谷 —— 用户看到的就是线一半埋在面下、一半飘在面上。
/// 原版内核 PolylineEmbedder 也只投影线顶点、再对面做扇形细分，同样不保证中间段落在面上（审计报"部分嵌入"）。
///
/// 现按"节点重算 + 保形细分"做：
///   ① 落面：每条线段在 XY 面上与网的所有三角边求交，交点(高程沿边插值) + 线顶点(高程按重心插值)
///      按线段参数排序成新节点串 —— 新线逐段都落在某一个三角形内(或沿边), 因此**整条线严格贴在面上**；
///      线与线互相交叉处也补节点, 免得局部约束互相打架。
///   ② 细分：只动被线经过的三角形。每个这样的三角形取"3 角 + 落在其边上的节点 + 内部节点"做一次小型
///      约束 Delaunay(约束 = 落在它里面的线段 + 各边上的节点链), 新顶点全在原三角形所在平面上,
///      **面形一点不变**；边上的节点被相邻两个三角形共用, 不产生 T 形接头。未被经过的三角形原样保留。
/// 输出：新网 + 落面后的各条线(供替换原多段线实体) + 统计(细分面数/新增点/未嵌入段/网外节点)。
/// 纯几何、可单测。2.5D 假设：网在 XY 上是单值面(地形/层位面)。
/// </summary>
public static class MeshEmbed
{
    public sealed class Result
    {
        public List<(double x, double y, double z)> Verts = new();
        public List<(int a, int b, int c)> Tris = new();
        /// <summary>落面后的各条线(与输入同序)：节点含全部与三角边的交点, 高程取自面。网外节点保留输入高程。</summary>
        public List<List<(double x, double y, double z)>> Polylines = new();
        public int OriginalFaces, NewFaces, SplitFaces, InsertedPoints;
        /// <summary>未能成为网边的线段数(落在网外 / 局部剖分失败)。</summary>
        public int Unembedded;
        /// <summary>落在网投影范围外的线节点数。</summary>
        public int OutsideNodes;
        public bool Strict => Unembedded == 0;
    }

    /// <summary>一条输入线：XY 点串 + 每点原高程(网外节点保留用) + 闭合。</summary>
    public readonly record struct Line(IReadOnlyList<(double x, double y)> Points, IReadOnlyList<double>? Z, bool Closed);

    private sealed class Node
    {
        public double X, Y, Z;
        public int Vert = -1;        // ≥0: 网顶点下标(原有或新分配); -1 待分配; -2 网外
        public bool Outside => Vert == -2;
    }

    private const long EdgeShift = 32;

    public static Result? Embed(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris,
        IReadOnlyList<Line> lines, double tolerance = 1e-6)
    {
        if (verts == null || tris == null || verts.Count < 3 || tris.Count == 0 || lines == null) return null;
        var ctx = new Ctx(verts, tris, Math.Max(tolerance, 1e-9));
        var res = new Result { OriginalFaces = tris.Count };

        // ── ① 落面：每条线 → 节点串 ──
        var seqs = new List<List<Node>>(lines.Count);
        var segs = new List<(int line, int seg, Node a, Node b, List<(double s, Node n)> cross)>();
        foreach (var ln in lines)
        {
            var seq = new List<Node>();
            if (ln.Points == null || ln.Points.Count == 0) { seqs.Add(seq); continue; }
            // 去掉相邻重复点(零长段会让节点/归属退化)；退化成单点的线只探高程, 不往网里插点
            var pts = new List<(double x, double y)>(); var zs = new List<double>();
            for (int i = 0; i < ln.Points.Count; i++)
            {
                var p = ln.Points[i];
                if (pts.Count > 0 && Math.Abs(pts[^1].x - p.x) <= tolerance && Math.Abs(pts[^1].y - p.y) <= tolerance) continue;
                pts.Add(p); zs.Add(ln.Z != null && i < ln.Z.Count ? ln.Z[i] : 0);
            }
            if (ln.Closed && pts.Count > 1 && Math.Abs(pts[^1].x - pts[0].x) <= tolerance && Math.Abs(pts[^1].y - pts[0].y) <= tolerance) { pts.RemoveAt(pts.Count - 1); zs.RemoveAt(zs.Count - 1); }
            int n = pts.Count;
            int segCount = ln.Closed && n > 2 ? n : n - 1;
            if (segCount <= 0) { seq.Add(ctx.Probe(pts[0].x, pts[0].y, zs[0])); seqs.Add(seq); continue; }
            var ends = new Node[n];
            for (int i = 0; i < n; i++) ends[i] = ctx.NodeAtXY(pts[i].x, pts[i].y, zs[i]);
            int lineIdx = seqs.Count;
            for (int i = 0; i < segCount; i++)
            {
                var a = ends[i]; var b = ends[(i + 1) % n];
                var cross = ctx.CrossEdges(pts[i].x, pts[i].y, pts[(i + 1) % n].x, pts[(i + 1) % n].y);
                segs.Add((lineIdx, i, a, b, cross));
            }
            seqs.Add(seq);
        }
        // 线与线(含自身不相邻段)交叉处补节点
        ctx.CrossSegments(segs, lines);
        // 组装节点串
        var segsByLine = new Dictionary<int, List<(int seg, Node a, Node b, List<(double s, Node n)> cross)>>();
        foreach (var s in segs) (segsByLine.TryGetValue(s.line, out var l) ? l : segsByLine[s.line] = new()).Add((s.seg, s.a, s.b, s.cross));
        for (int li = 0; li < lines.Count; li++)
        {
            if (!segsByLine.TryGetValue(li, out var ls)) continue;
            ls.Sort((p, q) => p.seg.CompareTo(q.seg));
            var seq = seqs[li];
            foreach (var (_, a, b, cross) in ls)
            {
                if (seq.Count == 0 || !ReferenceEquals(seq[^1], a)) Push(seq, a);
                cross.Sort((p, q) => p.s.CompareTo(q.s));
                foreach (var (_, nd) in cross) Push(seq, nd);
                Push(seq, b);
            }
            if (lines[li].Closed && seq.Count > 1 && ReferenceEquals(seq[0], seq[^1])) seq.RemoveAt(seq.Count - 1);
        }

        // ── ② 线段归属三角形 ──
        var pieces = new Dictionary<int, List<(Node a, Node b)>>();
        for (int li = 0; li < lines.Count; li++)
        {
            var seq = seqs[li];
            int cnt = seq.Count;
            int m = lines[li].Closed && cnt > 2 ? cnt : cnt - 1;
            for (int i = 0; i < m; i++)
            {
                var a = seq[i]; var b = seq[(i + 1) % cnt];
                if (ReferenceEquals(a, b)) continue;
                if (a.Outside || b.Outside) { res.Unembedded++; continue; }
                int t = ctx.Locate(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y), out _, out _, out _);
                if (t < 0) { res.Unembedded++; continue; }
                (pieces.TryGetValue(t, out var pl) ? pl : pieces[t] = new()).Add((a, b));
                ctx.EnsureOnTri(t, a); ctx.EnsureOnTri(t, b);
            }
        }

        // ── ③ 分配新顶点、逐三角形保形细分 ──
        res.Verts.AddRange(verts);
        foreach (var nd in ctx.Nodes)
            if (nd.Vert == -1) { nd.Vert = res.Verts.Count; res.Verts.Add((nd.X, nd.Y, nd.Z)); res.InsertedPoints++; }
        for (int t = 0; t < tris.Count; t++)
        {
            bool affected = pieces.ContainsKey(t) || ctx.InteriorNodes.ContainsKey(t) || ctx.HasEdgeNodes(t);
            if (!affected) { res.Tris.Add(tris[t]); continue; }
            var (ok, skipped) = ctx.Subdivide(t, pieces.TryGetValue(t, out var pl2) ? pl2 : null, res.Tris);
            if (!ok) { res.Tris.Add(tris[t]); res.Unembedded += pl2?.Count ?? 0; continue; }
            res.SplitFaces++; res.Unembedded += skipped;
        }
        res.NewFaces = res.Tris.Count;

        // ── ④ 落面后的线 ──
        foreach (var seq in seqs)
        {
            var outl = new List<(double x, double y, double z)>(seq.Count);
            foreach (var nd in seq) { outl.Add((nd.X, nd.Y, nd.Z)); if (nd.Outside) res.OutsideNodes++; }
            res.Polylines.Add(outl);
        }
        return res;
    }

    private static void Push(List<Node> seq, Node n)
    {
        if (seq.Count > 0 && ReferenceEquals(seq[^1], n)) return;
        seq.Add(n);
    }

    /// <summary>网的格网索引 + 节点表 + 落面/定位/细分工具。</summary>
    private sealed class Ctx
    {
        private readonly IReadOnlyList<(double x, double y, double z)> _v;
        private readonly IReadOnlyList<(int a, int b, int c)> _t;
        private readonly double _tol;
        private readonly double _minX, _minY, _cell;
        private readonly int _nx, _ny;
        private readonly List<int>?[] _grid;
        private int[] _stamp; private int _stampGen;

        public readonly List<Node> Nodes = new();
        private readonly Dictionary<int, Node> _vertNode = new();
        private readonly Dictionary<(long, long), List<Node>> _posNode = new();
        private readonly Dictionary<long, List<(double t, Node n)>> _edgeNodes = new();   // 边键 → 边上节点(参数 t 自小端点起)
        public readonly Dictionary<int, List<Node>> InteriorNodes = new();

        public Ctx(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t, double tol)
        {
            _v = v; _t = t; _tol = tol;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in v) { if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x; if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
            double extX = Math.Max(1e-6, maxX - minX), extY = Math.Max(1e-6, maxY - minY);
            _cell = Math.Max(Math.Sqrt(extX * extY / Math.Max(1, t.Count)), 1e-9);
            _minX = minX; _minY = minY;
            _nx = Math.Max(1, Math.Min(4096, (int)(extX / _cell) + 1));
            _ny = Math.Max(1, Math.Min(4096, (int)(extY / _cell) + 1));
            _grid = new List<int>?[_nx * _ny];
            for (int i = 0; i < t.Count; i++)
            {
                var (a, b, c) = t[i];
                if (a < 0 || b < 0 || c < 0 || a >= v.Count || b >= v.Count || c >= v.Count) continue;
                double x0 = Math.Min(v[a].x, Math.Min(v[b].x, v[c].x)), x1 = Math.Max(v[a].x, Math.Max(v[b].x, v[c].x));
                double y0 = Math.Min(v[a].y, Math.Min(v[b].y, v[c].y)), y1 = Math.Max(v[a].y, Math.Max(v[b].y, v[c].y));
                for (int gy = Cy(y0); gy <= Cy(y1); gy++)
                    for (int gx = Cx(x0); gx <= Cx(x1); gx++)
                        (_grid[gy * _nx + gx] ??= new List<int>()).Add(i);
            }
            _stamp = new int[t.Count];
        }

        private int Cx(double x) => Math.Clamp((int)((x - _minX) / _cell), 0, _nx - 1);
        private int Cy(double y) => Math.Clamp((int)((y - _minY) / _cell), 0, _ny - 1);
        private static long EdgeKey(int a, int b) => a < b ? ((long)a << (int)EdgeShift) | (uint)b : ((long)b << (int)EdgeShift) | (uint)a;

        /// <summary>定位 (x,y) 所在三角形；返回下标与重心坐标 (u→a, v→b, w→c)，网外 -1。</summary>
        public int Locate(double x, double y, out double u, out double v, out double w)
        {
            u = v = w = 0;
            if (x < _minX - _tol || y < _minY - _tol || x > _minX + _nx * _cell + _tol || y > _minY + _ny * _cell + _tol) return -1;
            var bucket = _grid[Cy(y) * _nx + Cx(x)];
            if (bucket == null) return -1;
            int best = -1; double bestMin = double.NegativeInfinity;
            foreach (int ti in bucket)
            {
                if (!Bary(ti, x, y, out double bu, out double bv, out double bw)) continue;
                double mn = Math.Min(bu, Math.Min(bv, bw));
                if (mn > bestMin) { bestMin = mn; best = ti; u = bu; v = bv; w = bw; }
                if (mn >= 0) break;
            }
            if (best < 0) return -1;
            // 容许落在边上/角上的一点点负值：按最短边尺度换算的相对容差
            var (a, b, c) = _t[best];
            double scale = Math.Max(Math.Max(Dist(_v[a], _v[b]), Dist(_v[b], _v[c])), Dist(_v[c], _v[a]));
            return bestMin >= -Math.Max(1e-9, _tol / Math.Max(scale, 1e-12)) ? best : -1;
        }

        private bool Bary(int ti, double x, double y, out double u, out double v, out double w)
        {
            var (a, b, c) = _t[ti];
            var p0 = _v[a]; var p1 = _v[b]; var p2 = _v[c];
            double d = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
            u = v = w = 0;
            if (Math.Abs(d) < 1e-300) return false;
            u = ((p1.y - p2.y) * (x - p2.x) + (p2.x - p1.x) * (y - p2.y)) / d;
            v = ((p2.y - p0.y) * (x - p2.x) + (p0.x - p2.x) * (y - p2.y)) / d;
            w = 1 - u - v;
            return true;
        }

        // ── 节点 ──
        public Node NodeAtVertex(int vi)
        {
            if (_vertNode.TryGetValue(vi, out var n)) return n;
            n = new Node { X = _v[vi].x, Y = _v[vi].y, Z = _v[vi].z, Vert = vi };
            _vertNode[vi] = n; Nodes.Add(n);
            return n;
        }

        private Node? FindNear(double x, double y)
        {
            long kx = (long)Math.Floor(x / _tol), ky = (long)Math.Floor(y / _tol);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (_posNode.TryGetValue((kx + dx, ky + dy), out var lst))
                        foreach (var n in lst)
                            if (Math.Abs(n.X - x) <= _tol && Math.Abs(n.Y - y) <= _tol) return n;
            return null;
        }

        private Node NewNode(double x, double y, double z, int vert)
        {
            var n = new Node { X = x, Y = y, Z = z, Vert = vert };
            Nodes.Add(n);
            var key = ((long)Math.Floor(x / _tol), (long)Math.Floor(y / _tol));
            (_posNode.TryGetValue(key, out var lst) ? lst : _posNode[key] = new()).Add(n);
            return n;
        }

        /// <summary>边 (a,b) 上参数 t 处的节点(t 沿 a→b)；贴近端点则归并到顶点。</summary>
        public Node NodeOnEdge(int a, int b, double t)
        {
            var pa = _v[a]; var pb = _v[b];
            double len = Dist(pa, pb);
            if (len <= _tol) return NodeAtVertex(a);
            if (t * len <= _tol) return NodeAtVertex(a);
            if ((1 - t) * len <= _tol) return NodeAtVertex(b);
            double x = pa.x + (pb.x - pa.x) * t, y = pa.y + (pb.y - pa.y) * t, z = pa.z + (pb.z - pa.z) * t;
            var near = FindNear(x, y);
            if (near != null && near.Vert != -2) return near;
            var n = NewNode(x, y, z, -1);
            long key = EdgeKey(a, b);
            double ts = a < b ? t : 1 - t;   // 统一按小端点起算
            (_edgeNodes.TryGetValue(key, out var lst) ? lst : _edgeNodes[key] = new()).Add((ts, n));
            return n;
        }

        /// <summary>只探高程、不登记的游离节点(单点线用)。</summary>
        public Node Probe(double x, double y, double zOutside)
        {
            int t = Locate(x, y, out double u, out double v, out double w);
            if (t < 0) return new Node { X = x, Y = y, Z = zOutside, Vert = -2 };
            var (a, b, c) = _t[t];
            return new Node { X = x, Y = y, Z = u * _v[a].z + v * _v[b].z + w * _v[c].z, Vert = -3 };
        }

        /// <summary>(x,y) 处的节点：网外→保留 zOutside 的网外节点；落在角/边/内部分别归并/登记。</summary>
        public Node NodeAtXY(double x, double y, double zOutside)
        {
            int t = Locate(x, y, out double u, out double v, out double w);
            if (t < 0)
            {
                var near0 = FindNear(x, y);
                if (near0 != null) return near0;
                return NewNode(x, y, zOutside, -2);
            }
            var (a, b, c) = _t[t];
            // 到各边的垂距(用重心坐标 × 对应高)
            double area2 = Math.Abs((_v[b].x - _v[a].x) * (_v[c].y - _v[a].y) - (_v[c].x - _v[a].x) * (_v[b].y - _v[a].y));
            double dA = u * area2 / Math.Max(Dist(_v[b], _v[c]), 1e-300);   // 到边 bc(对角 a)
            double dB = v * area2 / Math.Max(Dist(_v[c], _v[a]), 1e-300);
            double dC = w * area2 / Math.Max(Dist(_v[a], _v[b]), 1e-300);
            if (Dist2(_v[a], x, y) <= _tol * _tol) return NodeAtVertex(a);
            if (Dist2(_v[b], x, y) <= _tol * _tol) return NodeAtVertex(b);
            if (Dist2(_v[c], x, y) <= _tol * _tol) return NodeAtVertex(c);
            if (dA <= _tol) return NodeOnEdge(b, c, ParamOn(_v[b], _v[c], x, y));
            if (dB <= _tol) return NodeOnEdge(c, a, ParamOn(_v[c], _v[a], x, y));
            if (dC <= _tol) return NodeOnEdge(a, b, ParamOn(_v[a], _v[b], x, y));
            var near = FindNear(x, y);
            if (near != null && near.Vert != -2) return near;
            double z = u * _v[a].z + v * _v[b].z + w * _v[c].z;
            var n = NewNode(x, y, z, -1);
            (InteriorNodes.TryGetValue(t, out var lst) ? lst : InteriorNodes[t] = new()).Add(n);
            return n;
        }

        private static double ParamOn((double x, double y, double z) a, (double x, double y, double z) b, double x, double y)
        {
            double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
            if (l2 <= 0) return 0;
            return Math.Clamp(((x - a.x) * dx + (y - a.y) * dy) / l2, 0, 1);
        }

        /// <summary>线段 (x0,y0)→(x1,y1) 与网中所有三角边的交点(线段参数 s 与节点)。</summary>
        public List<(double s, Node n)> CrossEdges(double x0, double y0, double x1, double y1)
        {
            var res = new List<(double s, Node n)>();
            double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= _tol) return res;
            var seenEdge = new HashSet<long>();
            _stampGen++;
            foreach (int ti in CellsAlong(x0, y0, x1, y1))
            {
                var bucket = _grid[ti];
                if (bucket == null) continue;
                foreach (int t in bucket)
                {
                    if (_stamp[t] == _stampGen) continue;
                    _stamp[t] = _stampGen;
                    var (a, b, c) = _t[t];
                    TryEdge(a, b); TryEdge(b, c); TryEdge(c, a);
                }
            }
            return res;

            void TryEdge(int a, int b)
            {
                long key = EdgeKey(a, b);
                if (!seenEdge.Add(key)) return;
                var pa = _v[a]; var pb = _v[b];
                double ex = pb.x - pa.x, ey = pb.y - pa.y;
                double den = dx * ey - dy * ex;
                double elen = Math.Sqrt(ex * ex + ey * ey);
                if (Math.Abs(den) <= 1e-12 * len * elen) return;           // 平行/共线：由端点与顶点命中处理
                double s = ((pa.x - x0) * ey - (pa.y - y0) * ex) / den;    // 线段参数
                double u = ((pa.x - x0) * dy - (pa.y - y0) * dx) / den;    // 边参数(a→b)
                double es = _tol / len, eu = _tol / Math.Max(elen, 1e-300);
                if (s < -es || s > 1 + es || u < -eu || u > 1 + eu) return;
                if (s <= es || s >= 1 - es) return;                          // 落在线段端点：端点节点已覆盖
                res.Add((Math.Clamp(s, 0, 1), NodeOnEdge(a, b, Math.Clamp(u, 0, 1))));
            }
        }

        // 线段经过的格子(逐列取线段在该列 x 跨度内的 y 范围，不漏角格)
        private IEnumerable<int> CellsAlong(double x0, double y0, double x1, double y1)
        {
            if (x0 > x1) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }
            int gx0 = Cx(x0), gx1 = Cx(x1);
            double dx = x1 - x0;
            for (int gx = gx0; gx <= gx1; gx++)
            {
                double cxa = Math.Max(x0, _minX + gx * _cell), cxb = Math.Min(x1, _minX + (gx + 1) * _cell);
                double ya = dx > 0 ? y0 + (y1 - y0) * (cxa - x0) / dx : y0;
                double yb = dx > 0 ? y0 + (y1 - y0) * (cxb - x0) / dx : y1;
                if (dx <= 0) { ya = Math.Min(y0, y1); yb = Math.Max(y0, y1); }
                int gya = Cy(Math.Min(ya, yb)), gyb = Cy(Math.Max(ya, yb));
                for (int gy = gya; gy <= gyb; gy++) yield return gy * _nx + gx;
            }
        }

        /// <summary>线段两两(不相邻)交叉处补节点，交点计入两段的交点表。</summary>
        public void CrossSegments(List<(int line, int seg, Node a, Node b, List<(double s, Node n)> cross)> segs, IReadOnlyList<Line> lines)
        {
            int n = segs.Count;
            if (n < 2) return;
            // 线段格网(与网同格)
            var sg = new Dictionary<int, List<int>>();
            var boxes = new (double x0, double y0, double x1, double y1)[n];
            for (int i = 0; i < n; i++)
            {
                var (la, lb) = (segs[i].a, segs[i].b);
                boxes[i] = (Math.Min(la.X, lb.X), Math.Min(la.Y, lb.Y), Math.Max(la.X, lb.X), Math.Max(la.Y, lb.Y));
                foreach (int cell in CellsAlong(la.X, la.Y, lb.X, lb.Y))
                    (sg.TryGetValue(cell, out var l) ? l : sg[cell] = new()).Add(i);
            }
            var done = new HashSet<(int, int)>();
            for (int i = 0; i < n; i++)
            {
                var si = segs[i];
                foreach (int cell in CellsAlong(si.a.X, si.a.Y, si.b.X, si.b.Y))
                {
                    if (!sg.TryGetValue(cell, out var cand)) continue;
                    foreach (int j in cand)
                    {
                        if (j <= i || !done.Add((i, j))) continue;
                        var sj = segs[j];
                        if (ReferenceEquals(si.a, sj.a) || ReferenceEquals(si.a, sj.b) || ReferenceEquals(si.b, sj.a) || ReferenceEquals(si.b, sj.b)) continue;
                        if (boxes[i].x1 < boxes[j].x0 - _tol || boxes[j].x1 < boxes[i].x0 - _tol || boxes[i].y1 < boxes[j].y0 - _tol || boxes[j].y1 < boxes[i].y0 - _tol) continue;
                        double dx = si.b.X - si.a.X, dy = si.b.Y - si.a.Y, ex = sj.b.X - sj.a.X, ey = sj.b.Y - sj.a.Y;
                        double den = dx * ey - dy * ex;
                        double li = Math.Sqrt(dx * dx + dy * dy), lj = Math.Sqrt(ex * ex + ey * ey);
                        if (li <= _tol || lj <= _tol || Math.Abs(den) <= 1e-12 * li * lj) continue;
                        double s = ((sj.a.X - si.a.X) * ey - (sj.a.Y - si.a.Y) * ex) / den;
                        double u = ((sj.a.X - si.a.X) * dy - (sj.a.Y - si.a.Y) * dx) / den;
                        double es = _tol / li, eu = _tol / lj;
                        if (s <= es || s >= 1 - es || u <= eu || u >= 1 - eu) continue;
                        double x = si.a.X + dx * s, y = si.a.Y + dy * s;
                        var nd = NodeAtXY(x, y, 0.5 * (si.a.Z + si.b.Z));
                        si.cross.Add((s, nd)); sj.cross.Add((u, nd));
                    }
                }
            }
        }

        public bool HasEdgeNodes(int t)
        {
            var (a, b, c) = _t[t];
            return _edgeNodes.ContainsKey(EdgeKey(a, b)) || _edgeNodes.ContainsKey(EdgeKey(b, c)) || _edgeNodes.ContainsKey(EdgeKey(c, a));
        }

        /// <summary>保证节点出现在三角形 t 的局部点集里(角/边上节点本已在；数值擦边的内部节点补登记)。</summary>
        public void EnsureOnTri(int t, Node n)
        {
            if (n.Vert >= 0 && n.Vert < _v.Count) { var (a, b, c) = _t[t]; if (n.Vert == a || n.Vert == b || n.Vert == c) return; }
            if (InteriorNodes.TryGetValue(t, out var lst) && lst.Contains(n)) return;
            foreach (var key in TriEdgeKeys(t))
                if (_edgeNodes.TryGetValue(key, out var el)) foreach (var (_, en) in el) if (ReferenceEquals(en, n)) return;
            if (n.Vert >= 0 && n.Vert < _v.Count) return;   // 其他网顶点: 不属于此三角形, 交给剖分时按内部点处理
            (InteriorNodes.TryGetValue(t, out var l2) ? l2 : InteriorNodes[t] = new()).Add(n);
        }

        private IEnumerable<long> TriEdgeKeys(int t)
        {
            var (a, b, c) = _t[t];
            yield return EdgeKey(a, b); yield return EdgeKey(b, c); yield return EdgeKey(c, a);
        }

        /// <summary>对三角形 t 做局部约束剖分并追加到 outTris；返回 (成功, 未嵌入约束数)。</summary>
        public (bool ok, int skipped) Subdivide(int t, List<(Node a, Node b)>? pieces, List<(int a, int b, int c)> outTris)
        {
            var (ia, ib, ic) = _t[t];
            var pa = _v[ia]; var pb = _v[ib]; var pc = _v[ic];
            var local = new List<(double x, double y)>();
            var gidx = new List<int>();
            var map = new Dictionary<Node, int>();
            int AddPt(double x, double y, int g) { local.Add((x - pa.x, y - pa.y)); gidx.Add(g); return local.Count - 1; }
            int la = AddPt(pa.x, pa.y, ia), lb = AddPt(pb.x, pb.y, ib), lc = AddPt(pc.x, pc.y, ic);
            var cons = new List<(int u, int v)>();
            int Idx(Node n)
            {
                if (map.TryGetValue(n, out int li)) return li;
                if (n.Vert == ia) return map[n] = la;
                if (n.Vert == ib) return map[n] = lb;
                if (n.Vert == ic) return map[n] = lc;
                return map[n] = AddPt(n.X, n.Y, n.Vert);
            }
            // 三条边上的节点链(按小端点起的参数排序, 再按边方向串成链)
            void Chain(int u, int v, int lu, int lv)
            {
                if (!_edgeNodes.TryGetValue(EdgeKey(u, v), out var lst) || lst.Count == 0) { cons.Add((lu, lv)); return; }
                var ordered = new List<(double t, Node n)>(lst);
                ordered.Sort((p, q) => p.t.CompareTo(q.t));
                if (u > v) ordered.Reverse();   // 参数按小端点起算；u 是大端点则反向
                int prev = lu;
                foreach (var (_, nd) in ordered) { int li = Idx(nd); if (li != prev) cons.Add((prev, li)); prev = li; }
                if (prev != lv) cons.Add((prev, lv));
            }
            Chain(ia, ib, la, lb); Chain(ib, ic, lb, lc); Chain(ic, ia, lc, la);
            if (InteriorNodes.TryGetValue(t, out var inner)) foreach (var nd in inner) Idx(nd);
            if (pieces != null) foreach (var (a, b) in pieces) { int u = Idx(a), v = Idx(b); if (u != v) cons.Add((u, v)); }

            var tris = Delaunay.TriangulateConstrained(local, cons);
            if (tris.Count == 0) return (false, 0);
            double s0 = Math.Sign((pb.x - pa.x) * (pc.y - pa.y) - (pc.x - pa.x) * (pb.y - pa.y));
            var edgeSet = new HashSet<(int, int)>();
            foreach (var (a, b, c) in tris)
            {
                var p0 = local[a]; var p1 = local[b]; var p2 = local[c];
                double s = (p1.x - p0.x) * (p2.y - p0.y) - (p2.x - p0.x) * (p1.y - p0.y);
                if (Math.Abs(s) <= 1e-18) continue;   // 退化(共线)三角
                if (Math.Sign(s) == s0) outTris.Add((gidx[a], gidx[b], gidx[c]));
                else outTris.Add((gidx[a], gidx[c], gidx[b]));
                edgeSet.Add(a < b ? (a, b) : (b, a)); edgeSet.Add(b < c ? (b, c) : (c, b)); edgeSet.Add(a < c ? (a, c) : (c, a));
            }
            // 审计：落在此三角形里的线段是否都成了边(TriangulateConstrained 对嵌不进的约束静默放弃)
            int skipped = 0;
            if (pieces != null)
                foreach (var (a, b) in pieces)
                {
                    int u = map[a], v = map[b];
                    if (u != v && !edgeSet.Contains(u < v ? (u, v) : (v, u))) skipped++;
                }
            return (true, skipped);
        }

        private static double Dist((double x, double y, double z) a, (double x, double y, double z) b)
            => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
        private static double Dist2((double x, double y, double z) a, double x, double y)
            => (a.x - x) * (a.x - x) + (a.y - y) * (a.y - y);
    }
}
