using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// Delaunay 三角剖分（Delaunator 算法的 C# 移植）——半边结构，O(n log n)。
///
/// 为什么换掉原来的实现：原来那版是教科书式 Bowyer-Watson，每插一个点都要扫全部三角形、
/// 再用 List.Remove 逐个线性删除，量级是 O(n²) 起步。等值线动辄几万顶点，界面直接卡死。
/// 原版 PitMine3D 之所以没这个问题，是因为它内核里就带着这套算法
/// (Kernel/LasLib/external/delaunator-cpp) —— 这里按同一算法移植过来。
///
/// 半边约定（与 delaunator 一致）：
///   · 三角形 t 占用半边 3t、3t+1、3t+2；
///   · <see cref="Triangles"/>[e] 是半边 e 的起点顶点号，e 由它指向 Triangles[Next(e)]；
///   · <see cref="Halfedges"/>[e] 是对偶半边，-1 表示这条边在凸包上（无邻接三角形）。
/// 纯逻辑、无 UI 依赖、可单测。
/// </summary>
public sealed class DelaunatorCore
{
    public const int Invalid = -1;

    /// <summary>三角形顶点索引，每 3 个一组。</summary>
    public int[] Triangles = Array.Empty<int>();
    /// <summary>对偶半边表，与 <see cref="Triangles"/> 等长。</summary>
    public int[] Halfedges = Array.Empty<int>();
    /// <summary>凸包上任一顶点。</summary>
    public int HullStart;

    private readonly double[] _coords;   // x0,y0,x1,y1,...
    private int[] _hullPrev = Array.Empty<int>(), _hullNext = Array.Empty<int>(), _hullTri = Array.Empty<int>();
    private int[] _hash = Array.Empty<int>();
    private int _hashSize;
    private double _cx, _cy;
    private int _triLen, _heLen;
    private readonly List<int> _edgeStack = new();

    public static int Next(int e) => e % 3 == 2 ? e - 2 : e + 1;
    public static int Prev(int e) => e % 3 == 0 ? e + 2 : e - 1;

    /// <summary>点数（坐标对数）。</summary>
    public int PointCount => _coords.Length / 2;

    private DelaunatorCore(double[] coords) { _coords = coords; }

    /// <summary>
    /// 剖分。点少于 3 个、或全部重合/共线时返回空结果（<see cref="Triangles"/> 长度为 0），
    /// 不抛异常 —— 上层拿到空结果给用户一句话就行，不该让一份坏数据把程序带崩。
    /// </summary>
    public static DelaunatorCore Build(IReadOnlyList<(double x, double y)> pts)
    {
        var coords = new double[pts.Count * 2];
        for (int i = 0; i < pts.Count; i++) { coords[2 * i] = pts[i].x; coords[2 * i + 1] = pts[i].y; }
        var d = new DelaunatorCore(coords);
        d.Run();
        return d;
    }

    private double X(int i) => _coords[2 * i];
    private double Y(int i) => _coords[2 * i + 1];

    private void Run()
    {
        int n = PointCount;
        if (n < 3) return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double x = X(i), y = Y(i);
            if (x < minX) minX = x; if (y < minY) minY = y;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y;
        }
        double width = maxX - minX, height = maxY - minY;
        double span = width * width + height * height;
        double ccx = (minX + maxX) / 2, ccy = (minY + maxY) / 2;

        // 种子三角形：离质心最近的点 → 离它最近的点 → 与前两点外接圆最小的点
        int i0 = Invalid, i1 = Invalid, i2 = Invalid;
        double best = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double d = Dist(ccx, ccy, X(i), Y(i));
            if (d < best) { i0 = i; best = d; }
        }
        best = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (i == i0) continue;
            double d = Dist(X(i0), Y(i0), X(i), Y(i));
            if (d < best && d > 0) { i1 = i; best = d; }
        }
        if (i1 == Invalid) return;   // 所有点重合

        double minRadius = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (i == i0 || i == i1) continue;
            double r = Circumradius(X(i0), Y(i0), X(i1), Y(i1), X(i), Y(i));
            if (r < minRadius) { i2 = i; minRadius = r; }
        }
        if (i2 == Invalid || minRadius == double.MaxValue) return;   // 全共线

        if (CounterClockwise(X(i0), Y(i0), X(i1), Y(i1), X(i2), Y(i2))) (i1, i2) = (i2, i1);
        Circumcenter(X(i0), Y(i0), X(i1), Y(i1), X(i2), Y(i2), out _cx, out _cy);

        // 按到种子外接圆心的距离排序后逐点插入（这是 O(n log n) 的关键：新点总在凸包附近）
        var ids = new int[n];
        var dists = new double[n];
        for (int i = 0; i < n; i++) { ids[i] = i; dists[i] = Dist(X(i), Y(i), _cx, _cy); }
        Array.Sort(dists, ids);

        _hashSize = (int)Math.Ceiling(1.618 * Math.Sqrt(n));
        _hash = new int[_hashSize];
        Array.Fill(_hash, Invalid);
        _hullPrev = new int[n]; _hullNext = new int[n]; _hullTri = new int[n];

        HullStart = i0;
        _hullNext[i0] = _hullPrev[i2] = i1;
        _hullNext[i1] = _hullPrev[i0] = i2;
        _hullNext[i2] = _hullPrev[i1] = i0;
        _hullTri[i0] = 0; _hullTri[i1] = 1; _hullTri[i2] = 2;
        _hash[HashKey(X(i0), Y(i0))] = i0;
        _hash[HashKey(X(i1), Y(i1))] = i1;
        _hash[HashKey(X(i2), Y(i2))] = i2;

        int maxTriangles = 2 * n - 5;
        Triangles = new int[maxTriangles * 3];
        Halfedges = new int[maxTriangles * 3];
        _triLen = _heLen = 0;
        AddTriangle(i0, i1, i2, Invalid, Invalid, Invalid);

        double xp = double.NaN, yp = double.NaN;
        for (int k = 0; k < n; k++)
        {
            int i = ids[k];
            double x = X(i), y = Y(i);
            if (k > 0 && Equal(x, y, xp, yp)) continue;         // 近重复点
            xp = x; yp = y;
            if (Equal(x, y, X(i0), Y(i0)) || Equal(x, y, X(i1), Y(i1)) || Equal(x, y, X(i2), Y(i2))) continue;

            // 用凸包哈希找一条可见边
            int start = 0, key = HashKey(x, y);
            for (int j = 0; j < _hashSize; j++)
            {
                start = _hash[(key + j) % _hashSize];
                if (start != Invalid && start != _hullNext[start]) break;
            }
            if (start == Invalid) continue;
            start = _hullPrev[start];
            int e = start, q;
            while (true)
            {
                q = _hullNext[e];
                if (NearlySame(i, e, span) || NearlySame(i, q, span)) { e = Invalid; break; }
                if (CounterClockwise(x, y, X(e), Y(e), X(q), Y(q))) break;
                e = q;
                if (e == start) { e = Invalid; break; }
            }
            if (e == Invalid) continue;

            int t = AddTriangle(e, i, _hullNext[e], Invalid, Invalid, _hullTri[e]);
            _hullTri[i] = Legalize(t + 2);
            _hullTri[e] = t;

            int next = _hullNext[e];
            while (true)
            {
                q = _hullNext[next];
                if (!CounterClockwise(x, y, X(next), Y(next), X(q), Y(q))) break;
                t = AddTriangle(next, i, q, _hullTri[i], Invalid, _hullTri[next]);
                _hullTri[i] = Legalize(t + 2);
                _hullNext[next] = next;   // 标记移出凸包
                next = q;
            }
            if (e == start)
                while (true)
                {
                    q = _hullPrev[e];
                    if (!CounterClockwise(x, y, X(q), Y(q), X(e), Y(e))) break;
                    t = AddTriangle(q, i, e, Invalid, _hullTri[e], _hullTri[q]);
                    Legalize(t + 2);
                    _hullTri[q] = t;
                    _hullNext[e] = e;
                    e = q;
                }

            _hullPrev[i] = e; HullStart = e;
            _hullPrev[next] = i; _hullNext[e] = i; _hullNext[i] = next;
            _hash[HashKey(x, y)] = i;
            _hash[HashKey(X(e), Y(e))] = e;
        }

        Array.Resize(ref Triangles, _triLen);
        Array.Resize(ref Halfedges, _heLen);
    }

    private bool NearlySame(int a, int b, double span)
        => span > 0 && Dist(X(a), Y(a), X(b), Y(b)) / span < 1e-20;

    private int Legalize(int a)
    {
        int i = 0, ar = 0;
        _edgeStack.Clear();
        while (true)
        {
            int b = Halfedges[a];
            int a0 = 3 * (a / 3);
            ar = a0 + (a + 2) % 3;

            if (b == Invalid)
            {
                if (i > 0) { i--; a = _edgeStack[i]; continue; }
                break;
            }
            int b0 = 3 * (b / 3);
            int al = a0 + (a + 1) % 3;
            int bl = b0 + (b + 2) % 3;
            int p0 = Triangles[ar], pr = Triangles[a], pl = Triangles[al], p1 = Triangles[bl];

            if (InCircle(X(p0), Y(p0), X(pr), Y(pr), X(pl), Y(pl), X(p1), Y(p1)))
            {
                Triangles[a] = p1; Triangles[b] = p0;
                int hbl = Halfedges[bl];
                if (hbl == Invalid)   // 翻到凸包外侧（少见）：修正 hull_tri 指向
                {
                    int e = HullStart;
                    do { if (_hullTri[e] == bl) { _hullTri[e] = a; break; } e = _hullPrev[e]; } while (e != HullStart);
                }
                Link(a, hbl);
                Link(b, Halfedges[ar]);
                Link(ar, bl);
                int br = b0 + (b + 1) % 3;
                if (i < _edgeStack.Count) _edgeStack[i] = br; else _edgeStack.Add(br);
                i++;
            }
            else
            {
                if (i > 0) { i--; a = _edgeStack[i]; continue; }
                break;
            }
        }
        return ar;
    }

    private int HashKey(double x, double y)
    {
        double dx = x - _cx, dy = y - _cy;
        int k = (int)Math.Floor(PseudoAngle(dx, dy) * _hashSize);
        return ((k % _hashSize) + _hashSize) % _hashSize;
    }

    private int AddTriangle(int i0, int i1, int i2, int a, int b, int c)
    {
        int t = _triLen;
        Triangles[t] = i0; Triangles[t + 1] = i1; Triangles[t + 2] = i2;
        _triLen += 3;
        Link(t, a); Link(t + 1, b); Link(t + 2, c);
        return t;
    }

    private void Link(int a, int b)
    {
        if (a >= _heLen) _heLen = a + 1;
        Halfedges[a] = b;
        if (b != Invalid)
        {
            if (b >= _heLen) _heLen = b + 1;
            Halfedges[b] = a;
        }
    }

    // ── 几何谓词（与 delaunator 一致：带相对行列式保护，避免近共线时误判） ──

    private static double Dist(double ax, double ay, double bx, double by)
    { double dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }

    private static bool Equal(double x1, double y1, double x2, double y2)
        => Math.Abs(x1 - x2) <= double.Epsilon && Math.Abs(y1 - y2) <= double.Epsilon;

    private static double Circumradius(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double dx = bx - ax, dy = by - ay, ex = cx - ax, ey = cy - ay;
        double bl = dx * dx + dy * dy, cl = ex * ex + ey * ey, det = dx * ey - dy * ex;
        if (bl == 0 || cl == 0 || det == 0) return double.MaxValue;
        double rx = (ey * bl - dy * cl) * 0.5 / det, ry = (dx * cl - ex * bl) * 0.5 / det;
        return rx * rx + ry * ry;
    }

    private static void Circumcenter(double ax, double ay, double bx, double by, double cx, double cy,
                                     out double ox, out double oy)
    {
        double dx = bx - ax, dy = by - ay, ex = cx - ax, ey = cy - ay;
        double bl = dx * dx + dy * dy, cl = ex * ex + ey * ey, d = dx * ey - dy * ex;
        ox = ax + (ey * bl - dy * cl) * 0.5 / d;
        oy = ay + (dx * cl - ex * bl) * 0.5 / d;
    }

    /// <summary>逆时针判定；近共线（相对行列式过大）时判否，避免数值噪声制造假三角形。</summary>
    public static bool CounterClockwise(double px, double py, double qx, double qy, double rx, double ry)
    {
        double v0x = qx - px, v0y = qy - py, v1x = rx - px, v1y = ry - py;
        double det = v0x * v1y - v0y * v1x;
        if (det == 0) return false;
        double dist = v0x * v0x + v0y * v0y + v1x * v1x + v1y * v1y;
        if (Math.Abs(dist / det) > 1e14) return false;
        return det > 0;
    }

    private static bool InCircle(double ax, double ay, double bx, double by,
                                 double cx, double cy, double px, double py)
    {
        double dx = ax - px, dy = ay - py, ex = bx - px, ey = by - py, fx = cx - px, fy = cy - py;
        double ap = dx * dx + dy * dy, bp = ex * ex + ey * ey, cp = fx * fx + fy * fy;
        return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) < 0;
    }

    private static double PseudoAngle(double dx, double dy)
    {
        double p = dx / (Math.Abs(dx) + Math.Abs(dy));
        return (dy > 0 ? 3 - p : 1 + p) / 4;
    }
}
