using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 捕捉空间索引的公共底座 —— 二维均匀网格(CSR 桶 + 大件旁路)。
/// 图元一多, 每次鼠标移动都线性扫全场景顶点/原语, 光标就拖不动了；
/// 网格把每次查询压到光标邻域的几个桶。跨格过多的大件(长线/大圆)不铺格, 走旁路逐次带上。
/// 查询结果按原下标升序回放 → 调用方的遍历顺序与线性版一致(同优先级同距离的取舍不变)。
/// 非线程安全(复用查询缓冲), 只在 UI 线程用。纯逻辑, 可单测。
/// </summary>
internal sealed class UniformGrid
{
    private const int MaxCellsPerItem = 24;      // 一件最多铺 24 格, 超了走旁路
    private const int MaxDim = 1024;             // 每轴最多 1024 格(封顶内存)

    private readonly double _minX, _minY, _maxX, _maxY, _cell;
    private readonly int _nx, _ny;
    private readonly int[] _start;               // CSR 桶起点, 长度 nx*ny+1
    private readonly int[] _items;
    private readonly List<int> _big = new();     // 旁路: 跨格过多 / 坐标非有限
    private readonly int[] _stamp;               // 去重标记(一件跨多格会重复命中)
    private int _query;

    /// <summary>按 count 件的 AABB 建格。aabb 会被调用两遍(计数 + 填充), 须是纯函数。</summary>
    public UniformGrid(int count, Func<int, (double minX, double minY, double maxX, double maxY)> aabb)
    {
        _stamp = new int[Math.Max(count, 1)];
        double gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
        int finite = 0;
        for (int i = 0; i < count; i++)
        {
            var b = aabb(i);
            if (!IsFinite(b)) continue;
            if (b.minX < gx0) gx0 = b.minX;
            if (b.minY < gy0) gy0 = b.minY;
            if (b.maxX > gx1) gx1 = b.maxX;
            if (b.maxY > gy1) gy1 = b.maxY;
            finite++;
        }
        if (finite == 0) { gx0 = gy0 = 0; gx1 = gy1 = 1; }
        _minX = gx0; _minY = gy0; _maxX = gx1; _maxY = gy1;

        double w = Math.Max(gx1 - gx0, 1e-12), h = Math.Max(gy1 - gy0, 1e-12);
        double cell = Math.Sqrt(w * h * 2.0 / Math.Max(finite, 1));      // 目标每格约 2 件
        if (!(cell > 0) || double.IsInfinity(cell)) cell = Math.Max(w, h);
        cell = Math.Max(cell, Math.Max(w, h) / MaxDim);                  // 每轴格数封顶
        if (!(cell > 0)) cell = 1;
        _cell = cell;
        _nx = Math.Clamp((int)(w / cell) + 1, 1, MaxDim + 1);
        _ny = Math.Clamp((int)(h / cell) + 1, 1, MaxDim + 1);

        var counts = new int[_nx * _ny + 1];
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            if (!TrySpan(aabb(i), out int x0, out int y0, out int x1, out int y1)) { _big.Add(i); continue; }
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) { counts[y * _nx + x + 1]++; total++; }
        }
        _start = counts;
        for (int c = 0; c < _nx * _ny; c++) _start[c + 1] += _start[c];
        _items = new int[total];
        var cursor = new int[_nx * _ny];
        Array.Copy(_start, cursor, _nx * _ny);
        for (int i = 0; i < count; i++)
        {
            if (!TrySpan(aabb(i), out int x0, out int y0, out int x1, out int y1)) continue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) _items[cursor[y * _nx + x]++] = i;
        }
    }

    /// <summary>把与查询窗相交的候选下标(升序、去重)写入 outIdx。</summary>
    public void Query(double minX, double minY, double maxX, double maxY, List<int> outIdx)
    {
        outIdx.Clear();
        _query++;
        if (maxX >= _minX && minX <= _maxX && maxY >= _minY && minY <= _maxY)
        {
            int x0 = CellX(minX), x1 = CellX(maxX), y0 = CellY(minY), y1 = CellY(maxY);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int c = y * _nx + x;
                    for (int k = _start[c]; k < _start[c + 1]; k++)
                    {
                        int it = _items[k];
                        if (_stamp[it] == _query) continue;
                        _stamp[it] = _query; outIdx.Add(it);
                    }
                }
        }
        foreach (int i in _big)
            if (_stamp[i] != _query) { _stamp[i] = _query; outIdx.Add(i); }
        outIdx.Sort();     // 回到原下标顺序: 与线性遍历同序, 平局取舍不变
    }

    private bool TrySpan((double minX, double minY, double maxX, double maxY) b, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (!IsFinite(b)) return false;
        x0 = CellX(b.minX); x1 = CellX(b.maxX); y0 = CellY(b.minY); y1 = CellY(b.maxY);
        long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
        return cells <= MaxCellsPerItem;
    }

    private int CellX(double x) => Math.Clamp((int)((x - _minX) / _cell), 0, _nx - 1);
    private int CellY(double y) => Math.Clamp((int)((y - _minY) / _cell), 0, _ny - 1);

    private static bool IsFinite((double minX, double minY, double maxX, double maxY) b)
        => double.IsFinite(b.minX) && double.IsFinite(b.minY) && double.IsFinite(b.maxX) && double.IsFinite(b.maxY);
}

public static partial class SnapPoints
{
    /// <summary>
    /// 顶点捕捉索引 —— 把 <see cref="SnapPoints.FindNearest"/> 的全量扫描换成网格邻域查询。
    /// 结果与线性版逐点一致(含"等距取后出现者"的取舍)。建一次多次查, 场景变了重建。
    /// </summary>
    public sealed class Index
    {
        private readonly float[] _v;
        private readonly UniformGrid _grid;
        private readonly List<int> _buf = new();

        public Index(float[]? verts)
        {
            _v = verts ?? Array.Empty<float>();
            int n = _v.Length / 6;
            _grid = new UniformGrid(n, i => (_v[i * 6], _v[i * 6 + 1], _v[i * 6], _v[i * 6 + 1]));
        }

        /// <summary>建索引时的源数组(缓存命中判定用)。</summary>
        public float[] Source => _v;

        public int Count => _v.Length / 6;

        /// <summary>容差 tol 内离 (cx,cy) 最近的顶点；无则 null。语义同 <see cref="SnapPoints.FindNearest"/>。</summary>
        public (double x, double y)? FindNearest(double cx, double cy, double tol)
        {
            if (_v.Length < 6 || tol <= 0) return null;
            _grid.Query(cx - tol, cy - tol, cx + tol, cy + tol, _buf);
            double best = tol * tol;
            (double x, double y)? found = null;
            foreach (int i in _buf)
            {
                double dx = _v[i * 6] - cx, dy = _v[i * 6 + 1] - cy;
                double d2 = dx * dx + dy * dy;
                if (d2 <= best) { best = d2; found = (_v[i * 6], _v[i * 6 + 1]); }
            }
            return found;
        }
    }
}

public static partial class ObjectSnap
{
    /// <summary>Find 的候选子集(网格查得), 各表按原下标升序。null = 全量线性。</summary>
    internal sealed class Cand
    {
        public readonly List<int> Segs = new();          // 端点/中点/最近/垂足用: tol 窗
        public readonly List<int> SegsWide = new();      // 交点用: 4·tol 快拒窗(同 SegNearCursor)
        public readonly List<int> Circles = new();
        public readonly List<int> CirclesWide = new();
        public readonly List<int> Arcs = new();
        public readonly List<int> Pts = new();
        public readonly List<Seg> NearBuf = new();       // 交点快拒后的近邻段(复用, 免每帧分配)
    }

    /// <summary>
    /// 对象捕捉索引 —— 原语建网格, 每次只对光标邻域求解。
    /// 命中与全量 <see cref="ObjectSnap.Find"/> 完全一致: 候选是超集(几何上不可能漏),
    /// 且按原下标升序回放, 平局(同模式同距离)的取舍不变。
    /// 场景/图层变了重建即可; 非线程安全, 只在 UI 线程用。
    /// </summary>
    public sealed class Index
    {
        private readonly IReadOnlyList<Seg> _segs;
        private readonly IReadOnlyList<Circ> _circles;
        private readonly IReadOnlyList<ArcP> _arcs;
        private readonly IReadOnlyList<(double x, double y)> _pts;
        private readonly UniformGrid _gSeg, _gCirc, _gArc, _gPt;
        private readonly Cand _cand = new();

        public Index(IReadOnlyList<Seg>? segs, IReadOnlyList<Circ>? circles,
                     IReadOnlyList<ArcP>? arcs, IReadOnlyList<(double x, double y)>? pts)
        {
            _segs = segs ?? Array.Empty<Seg>();
            _circles = circles ?? Array.Empty<Circ>();
            _arcs = arcs ?? Array.Empty<ArcP>();
            _pts = pts ?? Array.Empty<(double, double)>();
            _gSeg = new UniformGrid(_segs.Count, i =>
            {
                var s = _segs[i];
                return (Math.Min(s.X1, s.X2), Math.Min(s.Y1, s.Y2), Math.Max(s.X1, s.X2), Math.Max(s.Y1, s.Y2));
            });
            // 圆/弧一律用整圆 AABB: 既含圆周(最近), 也含圆心(圆心捕捉)与端点/中点。
            _gCirc = new UniformGrid(_circles.Count, i =>
            {
                var c = _circles[i];
                double r = Math.Abs(c.R);
                return (c.Cx - r, c.Cy - r, c.Cx + r, c.Cy + r);
            });
            _gArc = new UniformGrid(_arcs.Count, i =>
            {
                var a = _arcs[i];
                double r = Math.Abs(a.R);
                return (a.Cx - r, a.Cy - r, a.Cx + r, a.Cy + r);
            });
            _gPt = new UniformGrid(_pts.Count, i => (_pts[i].x, _pts[i].y, _pts[i].x, _pts[i].y));
        }

        public int SegCount => _segs.Count;
        public int CircleCount => _circles.Count;
        public int ArcCount => _arcs.Count;
        public int PointCount => _pts.Count;

        /// <summary>语义同 <see cref="ObjectSnap.Find"/>, 只是候选取自网格邻域。</summary>
        public Hit? Find(double cx, double cy, double tol, int modeMask, (double x, double y)? anchor)
        {
            if (tol <= 0) return null;
            bool OnM(Mode m) => (modeMask & (1 << (int)m)) != 0;
            bool wantSeg = OnM(Mode.Endpoint) || OnM(Mode.Midpoint) || OnM(Mode.Nearest) || OnM(Mode.Perpendicular);
            bool wantRound = OnM(Mode.Center) || OnM(Mode.Endpoint) || OnM(Mode.Midpoint) || OnM(Mode.Nearest);
            bool wantX = OnM(Mode.Intersection);
            double wide = tol * 4;   // 交点快拒窗, 与 SegNearCursor/CircNearCursor 同尺度

            Fill(_gSeg, wantSeg, tol, _cand.Segs);
            Fill(_gSeg, wantX, wide, _cand.SegsWide);
            Fill(_gCirc, wantRound, tol, _cand.Circles);
            Fill(_gCirc, wantX, wide, _cand.CirclesWide);
            Fill(_gArc, wantRound, tol, _cand.Arcs);
            Fill(_gPt, OnM(Mode.Endpoint), tol, _cand.Pts);

            return ObjectSnap.Find(_segs, _circles, _arcs, _pts, cx, cy, tol, modeMask, anchor, _cand);

            void Fill(UniformGrid g, bool want, double r, List<int> outIdx)
            {
                if (want) g.Query(cx - r, cy - r, cx + r, cy + r, outIdx);
                else outIdx.Clear();
            }
        }
    }
}
