using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

public enum ContourSmoothMode { None = 0, Chaikin = 1, Spline = 2 }

/// <summary>「构建等值线」参数（窗体 → 引擎）。标量场 v1 = 高程 Z。忠实原 MeshEditLib.Contour.ContourOptions。</summary>
public sealed class ContourOptions
{
    public double Interval = 5.0;              // 等值距（ExplicitLevels 为空时生效）
    public double BaseLevel = 0.0;             // 等值距起算基准
    public double[]? ExplicitLevels;           // 指定值列表（优先于等值距）
    public double RangeMin = double.NegativeInfinity;  // 值域裁剪
    public double RangeMax = double.PositiveInfinity;
    public int IndexEvery = 5;                 // 每 N 条为计曲线；0 = 不区分

    public ContourSmoothMode Smooth = ContourSmoothMode.None;
    public int ChaikinIterations = 2;          // 1~4
    public double SplineStepMeters = 2.0;      // Catmull-Rom 重采样步距
    public double SimplifyTolerance = 0.2;     // Douglas-Peucker；0 = 不简化（圆滑前执行，防噪声放大）
    public double MinLengthMeters = 10.0;      // 碎线过滤（开线长度 / 闭环周长）
}

/// <summary>一条等值线结果（世界坐标，Z ≡ Level）。</summary>
public sealed class ContourPolyline
{
    public double Level;
    public bool Closed;
    public bool IsIndex;                       // 计曲线
    public List<double> Xyz = new();           // 扁平 [x,y,z,...]
    public double Length2D;
}

/// <summary>
/// 等值线提取引擎（忠实移植原 <c>MeshEditLib.Contour.ContourEngine</c>，纯 C#）：输入若干张三角网
/// (点/线源先经 <see cref="TriangulateScatter"/> 统一成网)，对每个等值面高程做 marching-triangles 逐三角形切割
/// → 边键拼链 → Douglas-Peucker 简化 → Chaikin/Catmull-Rom 圆滑(样条自交回退) → 碎线过滤；等值距模式按
/// (level−base)/interval 的整数倍每 IndexEvery 条标计曲线。另含原窗口里的线源加密采样、沿线标注布点与高程色带。
/// </summary>
public static class ContourEngine
{
    public static List<ContourPolyline> Build(
        IReadOnlyList<(double[] verts, int[] tris)> meshes, ContourOptions opt, out string warning)
    {
        warning = "";
        var result = new List<ContourPolyline>();
        if (meshes.Count == 0) return result;

        double dataMin = double.MaxValue, dataMax = double.MinValue;
        foreach (var (verts, _) in meshes)
            for (int i = 2; i < verts.Length; i += 3)
            {
                if (verts[i] < dataMin) dataMin = verts[i];
                if (verts[i] > dataMax) dataMax = verts[i];
            }
        if (dataMin > dataMax) return result;

        double lo = Math.Max(opt.RangeMin, dataMin), hi = Math.Min(opt.RangeMax, dataMax);
        var levels = BuildLevels(opt, lo, hi, ref warning);
        if (levels.Count == 0) return result;

        foreach (var (verts, tris) in meshes)
        {
            int nv = verts.Length / 3;
            var d = new double[nv];
            foreach (double level in levels)
            {
                for (int i = 0; i < nv; i++)
                {
                    double dz = verts[i * 3 + 2] - level;
                    d[i] = Math.Abs(dz) < 1e-9 ? 1e-9 : dz;
                }
                foreach (var raw in MarchLevel(verts, tris, d, level))
                {
                    var line = PostProcess(raw.pts, raw.closed, level, opt);
                    if (line != null) result.Add(line);
                }
            }
        }

        if (opt.IndexEvery > 0 && (opt.ExplicitLevels == null || opt.ExplicitLevels.Length == 0) && opt.Interval > 0)
            foreach (var l in result)
            {
                long k = (long)Math.Round((l.Level - opt.BaseLevel) / opt.Interval);
                l.IsIndex = k % opt.IndexEvery == 0;
            }
        return result;
    }

    public static List<double> BuildLevels(ContourOptions opt, double lo, double hi, ref string warning)
    {
        const int MaxLevels = 5000;
        var levels = new List<double>();
        if (opt.ExplicitLevels is { Length: > 0 })
        {
            foreach (double v in opt.ExplicitLevels.Distinct().OrderBy(v => v))
                if (v >= lo - 1e-9 && v <= hi + 1e-9) levels.Add(v);
            int dropped = opt.ExplicitLevels.Distinct().Count() - levels.Count;
            if (dropped > 0) warning += $"指定值有 {dropped} 个落在数据值域外被跳过；";
            return levels;
        }
        if (opt.Interval <= 0) { warning += "等值距必须 > 0；"; return levels; }
        double first = opt.BaseLevel + Math.Ceiling((lo - opt.BaseLevel) / opt.Interval - 1e-9) * opt.Interval;
        for (double v = first; v <= hi + 1e-9; v += opt.Interval)
        {
            levels.Add(v);
            if (levels.Count > MaxLevels)
            { warning += $"等值线层数超过 {MaxLevels}，已截断（请增大等值距）；"; break; }
        }
        return levels;
    }

    // ── marching-triangles：一张网一个高程 → 若干条链 ──
    private static List<(List<double> pts, bool closed)> MarchLevel(double[] verts, int[] tris, double[] d, double level)
    {
        var edgePt = new Dictionary<long, (double x, double y)>(PackedKeyComparer.Instance);
        var segs = new List<(long e0, long e1)>();
        var atEdge = new Dictionary<long, (int s0, int s1)>(PackedKeyComparer.Instance);

        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            long k0 = CrossEdge(a, b, d), k1 = CrossEdge(b, c, d), k2 = CrossEdge(c, a, d);
            long e0 = -1, e1 = -1;
            if (k0 >= 0) e0 = k0;
            if (k1 >= 0) { if (e0 < 0) e0 = k1; else e1 = k1; }
            if (k2 >= 0) { if (e0 < 0) e0 = k2; else if (e1 < 0) e1 = k2; }
            if (e0 < 0 || e1 < 0) continue;

            AddEdgePoint(edgePt, e0, verts, d);
            AddEdgePoint(edgePt, e1, verts, d);
            int sid = segs.Count;
            segs.Add((e0, e1));
            Hang(atEdge, e0, sid);
            Hang(atEdge, e1, sid);
        }

        var chains = new List<(List<double> pts, bool closed)>();
        var used = new bool[segs.Count];
        foreach (var kv in atEdge)
        {
            if (kv.Value.s1 >= 0) continue;
            int seg = kv.Value.s0;
            if (used[seg]) continue;
            chains.Add(WalkChain(kv.Key, seg, segs, atEdge, used, edgePt, level, closedLoop: false));
        }
        for (int s = 0; s < segs.Count; s++)
        {
            if (used[s]) continue;
            chains.Add(WalkChain(segs[s].e0, s, segs, atEdge, used, edgePt, level, closedLoop: true));
        }
        return chains;
    }

    private static long CrossEdge(int a, int b, double[] d) => d[a] * d[b] < 0 ? EdgeKey(a, b) : -1;

    private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static void AddEdgePoint(Dictionary<long, (double x, double y)> edgePt, long key, double[] verts, double[] d)
    {
        if (edgePt.ContainsKey(key)) return;
        int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
        double t = d[a] / (d[a] - d[b]);
        edgePt[key] = (verts[a * 3] + t * (verts[b * 3] - verts[a * 3]),
                       verts[a * 3 + 1] + t * (verts[b * 3 + 1] - verts[a * 3 + 1]));
    }

    private static void Hang(Dictionary<long, (int s0, int s1)> atEdge, long key, int sid)
    {
        if (atEdge.TryGetValue(key, out var v)) atEdge[key] = (v.s0, sid);
        else atEdge[key] = (sid, -1);
    }

    private static (List<double> pts, bool closed) WalkChain(
        long startKey, int startSeg, List<(long e0, long e1)> segs,
        Dictionary<long, (int s0, int s1)> atEdge, bool[] used,
        Dictionary<long, (double x, double y)> edgePt, double level, bool closedLoop)
    {
        var pts = new List<double>();
        long key = startKey;
        int seg = startSeg;
        Append(pts, edgePt[key], level);
        while (true)
        {
            used[seg] = true;
            long next = segs[seg].e0 == key ? segs[seg].e1 : segs[seg].e0;
            Append(pts, edgePt[next], level);
            var (s0, s1) = atEdge[next];
            int follow = s0 != seg && s0 >= 0 && !used[s0] ? s0
                       : s1 != seg && s1 >= 0 && !used[s1] ? s1 : -1;
            if (follow < 0) break;
            key = next; seg = follow;
        }
        bool closed = closedLoop;
        if (closed && pts.Count >= 6) pts.RemoveRange(pts.Count - 3, 3);
        return (pts, closed);
    }

    private static void Append(List<double> pts, (double x, double y) p, double z) { pts.Add(p.x); pts.Add(p.y); pts.Add(z); }

    // ── 后处理：简化 → 圆滑（样条自交则回退）→ 碎线过滤 ──
    private static ContourPolyline? PostProcess(List<double> pts, bool closed, double level, ContourOptions opt)
    {
        int minPts = closed ? 3 : 2;
        if (pts.Count / 3 < minPts) return null;

        var work = opt.SimplifyTolerance > 0 ? Simplify(pts, closed, opt.SimplifyTolerance) : pts;
        if (work.Count / 3 < minPts) work = pts;

        List<double> smoothed = work;
        if (opt.Smooth == ContourSmoothMode.Chaikin)
            smoothed = Chaikin(work, closed, Math.Clamp(opt.ChaikinIterations, 1, 4));
        else if (opt.Smooth == ContourSmoothMode.Spline)
        {
            smoothed = CatmullRom(work, closed, Math.Max(0.1, opt.SplineStepMeters));
            if (SelfIntersects(smoothed, closed)) smoothed = work;
        }

        double len = Length2D(smoothed, closed);
        if (len < opt.MinLengthMeters) return null;

        return new ContourPolyline { Level = level, Closed = closed, Xyz = smoothed, Length2D = len };
    }

    // ── Douglas-Peucker（XY；闭环拆两半各自简化）──
    public static List<double> Simplify(List<double> pts, bool closed, double tol)
    {
        int n = pts.Count / 3;
        if (n <= (closed ? 4 : 2)) return pts;
        var keep = new bool[n];
        if (!closed)
        {
            keep[0] = keep[n - 1] = true;
            DpRange(pts, 0, n - 1, tol, keep);
        }
        else
        {
            int far = 1;
            double best = -1;
            for (int i = 1; i < n; i++)
            {
                double dx = pts[i * 3] - pts[0], dy = pts[i * 3 + 1] - pts[1];
                double d2 = dx * dx + dy * dy;
                if (d2 > best) { best = d2; far = i; }
            }
            keep[0] = keep[far] = true;
            DpRange(pts, 0, far, tol, keep);
            DpRangeWrap(pts, far, n, tol, keep);
        }
        var outPts = new List<double>(pts.Count);
        for (int i = 0; i < n; i++)
            if (keep[i]) { outPts.Add(pts[i * 3]); outPts.Add(pts[i * 3 + 1]); outPts.Add(pts[i * 3 + 2]); }
        return outPts.Count / 3 >= (closed ? 3 : 2) ? outPts : pts;
    }

    private static void DpRange(List<double> pts, int i0, int i1, double tol, bool[] keep)
    {
        var stack = new Stack<(int a, int b)>();
        stack.Push((i0, i1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            double ax = pts[a * 3], ay = pts[a * 3 + 1], bx = pts[b * 3], by = pts[b * 3 + 1];
            double ux = bx - ax, uy = by - ay, uu = ux * ux + uy * uy;
            int far = -1; double best = tol * tol;
            for (int i = a + 1; i < b; i++)
            {
                double wx = pts[i * 3] - ax, wy = pts[i * 3 + 1] - ay;
                double d2;
                if (uu < 1e-20) d2 = wx * wx + wy * wy;
                else { double cr = wx * uy - wy * ux; d2 = cr * cr / uu; }
                if (d2 > best) { best = d2; far = i; }
            }
            if (far >= 0) { keep[far] = true; stack.Push((a, far)); stack.Push((far, b)); }
        }
    }

    private static void DpRangeWrap(List<double> pts, int far, int n, double tol, bool[] keep)
    {
        int m = n - far + 1;
        if (m < 3) return;
        var tmp = new List<double>(m * 3);
        for (int i = far; i < n; i++) { tmp.Add(pts[i * 3]); tmp.Add(pts[i * 3 + 1]); tmp.Add(pts[i * 3 + 2]); }
        tmp.Add(pts[0]); tmp.Add(pts[1]); tmp.Add(pts[2]);
        var k2 = new bool[m];
        k2[0] = k2[m - 1] = true;
        DpRange(tmp, 0, m - 1, tol, k2);
        for (int i = 1; i < m - 1; i++) if (k2[i]) keep[far + i] = true;
    }

    // ── Chaikin 切角圆滑 ──
    public static List<double> Chaikin(List<double> pts, bool closed, int iterations)
    {
        var cur = pts;
        for (int it = 0; it < iterations; it++)
        {
            int n = cur.Count / 3;
            if (n < 3) break;
            var next = new List<double>(cur.Count * 2);
            int segCount = closed ? n : n - 1;
            if (!closed) AddPt(next, cur, 0);
            for (int i = 0; i < segCount; i++)
            {
                int j = (i + 1) % n;
                LerpAdd(next, cur, i, j, 0.25);
                LerpAdd(next, cur, i, j, 0.75);
            }
            if (!closed) AddPt(next, cur, n - 1);
            cur = next;
        }
        return cur;
    }

    // ── Catmull-Rom 过点样条按步距重采样 ──
    public static List<double> CatmullRom(List<double> pts, bool closed, double step)
    {
        int n = pts.Count / 3;
        if (n < 3) return pts;
        var outPts = new List<double>(pts.Count * 2);
        int segCount = closed ? n : n - 1;
        for (int i = 0; i < segCount; i++)
        {
            int i0 = closed ? (i - 1 + n) % n : Math.Max(i - 1, 0);
            int i1 = i, i2 = (i + 1) % n;
            int i3 = closed ? (i + 2) % n : Math.Min(i + 2, n - 1);
            double segLen = Dist2D(pts, i1, i2);
            int sub = Math.Max(1, (int)Math.Ceiling(segLen / step));
            for (int s = 0; s < sub; s++) AddCatmull(outPts, pts, i0, i1, i2, i3, (double)s / sub);
        }
        if (!closed) AddPt(outPts, pts, n - 1);
        return outPts;
    }

    private static void AddCatmull(List<double> outPts, List<double> p, int i0, int i1, int i2, int i3, double t)
    {
        double t2 = t * t, t3 = t2 * t;
        for (int c = 0; c < 3; c++)
        {
            double p0 = p[i0 * 3 + c], p1 = p[i1 * 3 + c], p2 = p[i2 * 3 + c], p3 = p[i3 * 3 + c];
            outPts.Add(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3));
        }
    }

    /// <summary>朴素 O(n²) 自交检测（bbox 早退；段数超限直接放行）。</summary>
    public static bool SelfIntersects(List<double> pts, bool closed)
    {
        int n = pts.Count / 3;
        int segCount = closed ? n : n - 1;
        if (segCount < 3 || segCount > 3000) return false;
        for (int i = 0; i < segCount; i++)
        {
            int i1 = (i + 1) % n;
            double ax = pts[i * 3], ay = pts[i * 3 + 1], bx = pts[i1 * 3], by = pts[i1 * 3 + 1];
            for (int j = i + 2; j < segCount; j++)
            {
                if (i == 0 && j == segCount - 1 && closed) continue;
                int j1 = (j + 1) % n;
                double cx = pts[j * 3], cy = pts[j * 3 + 1], dx = pts[j1 * 3], dy = pts[j1 * 3 + 1];
                if (Math.Max(ax, bx) < Math.Min(cx, dx) || Math.Max(cx, dx) < Math.Min(ax, bx) ||
                    Math.Max(ay, by) < Math.Min(cy, dy) || Math.Max(cy, dy) < Math.Min(ay, by)) continue;
                double d1 = Cross(cx, cy, dx, dy, ax, ay), d2 = Cross(cx, cy, dx, dy, bx, by);
                double d3 = Cross(ax, ay, bx, by, cx, cy), d4 = Cross(ax, ay, bx, by, dx, dy);
                if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            }
        }
        return false;
    }

    private static double Cross(double ax, double ay, double bx, double by, double px2, double py2) => (bx - ax) * (py2 - ay) - (by - ay) * (px2 - ax);

    public static double Length2D(List<double> pts, bool closed)
    {
        int n = pts.Count / 3;
        double len = 0;
        for (int i = 0; i + 1 < n; i++) len += Dist2D(pts, i, i + 1);
        if (closed && n > 2) len += Dist2D(pts, n - 1, 0);
        return len;
    }

    private static double Dist2D(List<double> pts, int i, int j)
    {
        double dx = pts[j * 3] - pts[i * 3], dy = pts[j * 3 + 1] - pts[i * 3 + 1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void AddPt(List<double> dst, List<double> src, int i) { dst.Add(src[i * 3]); dst.Add(src[i * 3 + 1]); dst.Add(src[i * 3 + 2]); }

    private static void LerpAdd(List<double> dst, List<double> src, int i, int j, double t)
    {
        for (int c = 0; c < 3; c++) dst.Add(src[i * 3 + c] + t * (src[j * 3 + c] - src[i * 3 + c]));
    }

    // ═══════ 原窗口(ContourBuilderWindow)中的纯逻辑 ═══════

    /// <summary>多段线顶点入散点池；step&gt;0 时每段按最大间距等分插点。</summary>
    public static void DensifyInto(List<double> scatter, double[] xyz, bool closed, double step)
    {
        int n = xyz.Length / 3;
        if (n == 0) return;
        int segCount = closed ? n : n - 1;
        scatter.Add(xyz[0]); scatter.Add(xyz[1]); scatter.Add(xyz[2]);
        for (int i = 0; i < segCount; i++)
        {
            int j = (i + 1) % n;
            double dx = xyz[j * 3] - xyz[i * 3], dy = xyz[j * 3 + 1] - xyz[i * 3 + 1], dz = xyz[j * 3 + 2] - xyz[i * 3 + 2];
            double len = Math.Sqrt(dx * dx + dy * dy);
            int sub = step > 0 ? Math.Max(1, (int)Math.Ceiling(len / step)) : 1;
            for (int s = 1; s <= sub; s++)
            {
                if (closed && i == segCount - 1 && s == sub) break;
                double t = (double)s / sub;
                scatter.Add(xyz[i * 3] + t * dx);
                scatter.Add(xyz[i * 3 + 1] + t * dy);
                scatter.Add(xyz[i * 3 + 2] + t * dz);
            }
        }
    }

    /// <summary>
    /// 散点 [x,y,z,...] → XY Delaunay 三角网(原 DelaunayTriangulator 的等价: 0.1mm 网格去重 + <see cref="Delaunay.Triangulate"/>
    /// + 长边剔除(任一边 XY 长度超 longEdgeMeters 的三角丢弃, ≤0 不剔) + 退化剔除)。返回索引指向输入点序号。
    /// </summary>
    public static int[] TriangulateScatter(IReadOnlyList<double> xyzFlat, double longEdgeMeters, out int usedPoints)
    {
        usedPoints = 0;
        int nIn = xyzFlat.Count / 3;
        if (nIn < 3) return Array.Empty<int>();
        var pts = new List<(double x, double y)>(nIn);
        var orig = new List<int>(nIn);
        var seen = new HashSet<(long, long)>();
        const double DedupeTol = 1e-4;
        for (int i = 0; i < nIn; i++)
        {
            double x = xyzFlat[i * 3], y = xyzFlat[i * 3 + 1];
            var key = ((long)Math.Round(x / DedupeTol), (long)Math.Round(y / DedupeTol));
            if (!seen.Add(key)) continue;
            pts.Add((x, y)); orig.Add(i);
        }
        usedPoints = pts.Count;
        if (pts.Count < 3) return Array.Empty<int>();
        var tris = Delaunay.Triangulate(pts);
        double edge2Max = longEdgeMeters > 0 ? longEdgeMeters * longEdgeMeters : double.MaxValue;
        var result = new List<int>(tris.Count * 3);
        foreach (var (a, b, c) in tris)
        {
            if (E2(pts, a, b) > edge2Max || E2(pts, b, c) > edge2Max || E2(pts, c, a) > edge2Max) continue;
            double o = (pts[b].x - pts[a].x) * (pts[c].y - pts[a].y) - (pts[b].y - pts[a].y) * (pts[c].x - pts[a].x);
            if (Math.Abs(o) < 1e-10) continue;
            result.Add(orig[a]); result.Add(orig[b]); result.Add(orig[c]);
        }
        return result.ToArray();
    }

    private static double E2(List<(double x, double y)> p, int a, int b) { double dx = p[a].x - p[b].x, dy = p[a].y - p[b].y; return dx * dx + dy * dy; }

    /// <summary>高程色带 蓝→青→绿→黄→红 (0..1)。</summary>
    public static (float r, float g, float b) RampColor(double level, double lo, double hi)
    {
        double t = hi > lo ? (level - lo) / (hi - lo) : 0.5;
        double r, g, b;
        if (t < 0.25) { r = 0; g = t / 0.25; b = 1; }
        else if (t < 0.5) { r = 0; g = 1; b = 1 - (t - 0.25) / 0.25; }
        else if (t < 0.75) { r = (t - 0.5) / 0.25; g = 1; b = 0; }
        else { r = 1; g = 1 - (t - 0.75) / 0.25; b = 0; }
        return ((float)r, (float)g, (float)b);
    }

    /// <summary>沿线按间隔放置高程文字的位置与朝向(弧度; X 分量为负时翻转防倒字)。</summary>
    public static List<(double x, double y, double angle)> LabelStops(ContourPolyline line, double textH, double gap)
    {
        var res = new List<(double x, double y, double angle)>();
        var p = line.Xyz;
        int n = p.Count / 3;
        int segCount = line.Closed ? n : n - 1;
        if (n < 2) return res;
        double total = line.Length2D;
        var stops = new List<double>();
        if (total >= gap) for (double s = gap * 0.5; s < total; s += gap) stops.Add(s);
        else if (total >= textH * 6) stops.Add(total * 0.5);
        if (stops.Count == 0) return res;
        int stop = 0;
        double walked = 0;
        for (int i = 0; i < segCount && stop < stops.Count; i++)
        {
            int j = (i + 1) % n;
            double dx = p[j * 3] - p[i * 3], dy = p[j * 3 + 1] - p[i * 3 + 1];
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            if (segLen < 1e-9) continue;
            while (stop < stops.Count && stops[stop] <= walked + segLen)
            {
                double t = (stops[stop] - walked) / segLen;
                double x = p[i * 3] + t * dx, y = p[i * 3 + 1] + t * dy;
                double ux = dx / segLen, uy = dy / segLen;
                if (ux < 0) { ux = -ux; uy = -uy; }
                res.Add((x, y, Math.Atan2(uy, ux)));
                stop++;
            }
            walked += segLen;
        }
        return res;
    }

    /// <summary>
    /// 等值线 → 场景实体(原 BuildPmbi 的等价): 三维多段线(Zs=层值)落 layer, 标注文字落 labelLayer。
    /// colorMode 0=首/计曲线双色 1=单色(随层, 用默认棕) 2=高程色带。
    /// </summary>
    public static List<SceneEntity> BuildEntities(List<ContourPolyline> lines, string layer, string labelLayer,
        int colorMode, ContourOptions opt, bool wantLabel, bool indexOnly, double textH, double labelGap)
    {
        var list = new List<SceneEntity>();
        if (lines.Count == 0) return list;
        double lvMin = lines.Min(l => l.Level), lvMax = lines.Max(l => l.Level);
        foreach (var line in lines)
        {
            (float r, float g, float b) col = colorMode switch
            {
                1 => (166 / 255f, 110 / 255f, 60 / 255f),
                2 => RampColor(line.Level, lvMin, lvMax),
                _ => line.IsIndex ? (230 / 255f, 92 / 255f, 0f) : (166 / 255f, 110 / 255f, 60 / 255f),
            };
            var pl = new PolylineEntity { Closed = line.Closed, LayerName = layer, Cr = col.r, Cg = col.g, Cb = col.b, Zs = new List<double>() };
            for (int i = 0; i + 2 < line.Xyz.Count; i += 3) { pl.Points.Add((line.Xyz[i], line.Xyz[i + 1])); pl.Zs.Add(line.Xyz[i + 2]); }
            list.Add(pl);
        }
        if (wantLabel)
        {
            foreach (var line in lines)
            {
                if (indexOnly && opt.IndexEvery > 0 && !line.IsIndex) continue;
                string label = line.Level.ToString("0.##", CultureInfo.InvariantCulture);
                foreach (var (x, y, ang) in LabelStops(line, textH, labelGap))
                    list.Add(new TextEntity { X = x, Y = y, Elevation = line.Level, Height = textH, Rotation = ang, HAlign = 1, VAlign = 0, Text = label, LayerName = labelLayer, Cr = 235 / 255f, Cg = 235 / 255f, Cb = 235 / 255f });
            }
        }
        return list;
    }
}
