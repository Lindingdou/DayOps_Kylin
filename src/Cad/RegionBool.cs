using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 区域多边形栅格布尔运算（忠实移植原 <c>PlanLib.ShortTerm.RegionGeometry</c> 的 2D 版）——
/// 检测重叠 / 求差(subject ∖ clip 保最大块)，用于保证各可采区域空间互斥。任意凹多边形稳健：
/// 扫描线栅格化 + 8-邻接连通保最大块 + Moore 描边 + Douglas-Peucker 抽稀。纯托管、确定性、可单测。
/// 多边形以有序点表(首尾不重复, 隐式闭合)表示；场景为 2D 故去掉原 Z 平面拟合。
/// </summary>
public static class RegionBool
{
    /// <summary>两区域是否「成片」重叠(重叠面积占较小者 ≥2%)；仅边界相邻不算。</summary>
    public static bool Overlaps(IReadOnlyList<(double x, double y)> a, IReadOnlyList<(double x, double y)> b)
    {
        if (a == null || b == null || a.Count < 3 || b.Count < 3) return false;
        Bounds(a, out double aminx, out double aminy, out double amaxx, out double amaxy);
        Bounds(b, out double bminx, out double bminy, out double bmaxx, out double bmaxy);
        double minx = Math.Max(aminx, bminx), miny = Math.Max(aminy, bminy);
        double maxx = Math.Min(amaxx, bmaxx), maxy = Math.Min(amaxy, bmaxy);
        if (maxx <= minx || maxy <= miny) return false;

        double span = Math.Max(maxx - minx, maxy - miny);
        double cell = Math.Max(span / 512.0, 1e-6);
        int nx = (int)((maxx - minx) / cell) + 1, ny = (int)((maxy - miny) / cell) + 1;
        var ma = new bool[nx * ny]; var mb = new bool[nx * ny];
        Rasterize(a, minx, miny, cell, nx, ny, ma);
        Rasterize(b, minx, miny, cell, nx, ny, mb);
        int both = 0;
        for (int i = 0; i < ma.Length; i++) if (ma[i] && mb[i]) both++;

        double overlapArea = both * cell * cell;
        double areaA = Math.Abs(ShoelaceArea(a)), areaB = Math.Abs(ShoelaceArea(b));
        double minArea = Math.Min(areaA, areaB);
        return minArea > 0 && overlapArea / minArea > 0.02;
    }

    /// <summary>subject 减去 clip(subject ∖ clip), 只保留面积最大的一块；被完全覆盖返回空表。</summary>
    public static List<(double x, double y)> SubtractKeepLargest(
        IReadOnlyList<(double x, double y)> subject, IReadOnlyList<(double x, double y)> clip)
    {
        var empty = new List<(double x, double y)>();
        if (subject == null || subject.Count < 3) return empty;
        if (clip == null || clip.Count < 3) return new List<(double x, double y)>(subject);

        Bounds(subject, out double minx, out double miny, out double maxx, out double maxy);
        double span = Math.Max(maxx - minx, maxy - miny);
        if (span <= 0) return empty;
        double cell = Math.Max(span / 600.0, 1e-6);
        int nx, ny; long want;
        while (true)
        {
            nx = (int)((maxx - minx) / cell) + 1; ny = (int)((maxy - miny) / cell) + 1;
            want = (long)nx * ny; if (want <= 2_000_000) break; cell *= 1.3;
        }
        var ms = new bool[nx * ny]; var mc = new bool[nx * ny];
        Rasterize(subject, minx, miny, cell, nx, ny, ms);
        Rasterize(clip, minx, miny, cell, nx, ny, mc);
        for (int i = 0; i < ms.Length; i++) if (mc[i]) ms[i] = false;   // 差集
        KeepLargest(ms, nx, ny);

        int start = -1;
        for (int c = 0; c < ms.Length; c++) if (ms[c]) { start = c; break; }
        if (start < 0) return empty;
        var ring = TraceBoundary(ms, nx, ny, start);
        if (ring.Count < 3) return empty;
        var simp = Simplify(ring, 1.2);
        if (simp.Count < 3) return empty;

        var outp = new List<(double x, double y)>(simp.Count);
        foreach (var (gx, gy) in simp)
            outp.Add((minx + (gx + 0.5) * cell, miny + (gy + 0.5) * cell));
        return outp;
    }

    // ── 内部(逐字自 RegionGeometry, 去 Z) ──
    private static void Bounds(IReadOnlyList<(double x, double y)> p, out double minx, out double miny, out double maxx, out double maxy)
    {
        minx = miny = 1e300; maxx = maxy = -1e300;
        foreach (var (x, y) in p)
        {
            minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
            miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
        }
    }

    private static double ShoelaceArea(IReadOnlyList<(double x, double y)> p)
    {
        int n = p.Count; double s = 0;
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; s += p[i].x * p[j].y - p[j].x * p[i].y; }
        return 0.5 * s;
    }

    private static void Rasterize(IReadOnlyList<(double x, double y)> ring, double minX, double minY, double cell, int nx, int ny, bool[] mask)
    {
        int n = ring.Count; if (n < 3) return;
        for (int y = 0; y < ny; y++)
        {
            double wy = minY + (y + 0.5) * cell;
            var xs = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double y0 = ring[i].y, y1 = ring[(i + 1) % n].y;
                double x0 = ring[i].x, x1 = ring[(i + 1) % n].x;
                if ((y0 <= wy && y1 > wy) || (y1 <= wy && y0 > wy))
                    xs.Add(x0 + (wy - y0) / (y1 - y0) * (x1 - x0));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int xa = Math.Clamp((int)((xs[k] - minX) / cell), 0, nx - 1);
                int xb = Math.Clamp((int)((xs[k + 1] - minX) / cell), 0, nx - 1);
                for (int x = xa; x <= xb; x++) mask[y * nx + x] = true;
            }
        }
    }

    private static void KeepLargest(bool[] mask, int nx, int ny)
    {
        int N = nx * ny;
        var comp = new int[N];
        int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 }, dy = { 0, -1, -1, -1, 0, 1, 1, 1 };
        var stack = new Stack<int>();
        int label = 0, best = 0, bestCount = 0;
        for (int s = 0; s < N; s++)
        {
            if (!mask[s] || comp[s] != 0) continue;
            label++; int count = 0;
            stack.Push(s); comp[s] = label;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); count++;
                int x = c % nx, y = c / nx;
                for (int k = 0; k < 8; k++)
                {
                    int ax = x + dx[k], ay = y + dy[k];
                    if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                    int ac = ay * nx + ax;
                    if (mask[ac] && comp[ac] == 0) { comp[ac] = label; stack.Push(ac); }
                }
            }
            if (count > bestCount) { bestCount = count; best = label; }
        }
        if (label <= 1 || best == 0) return;
        for (int c = 0; c < N; c++) if (mask[c] && comp[c] != best) mask[c] = false;
    }

    private static List<(int x, int y)> TraceBoundary(bool[] mask, int nx, int ny, int start)
    {
        var ring = new List<(int x, int y)>();
        int sx = start % nx, sy = start / nx;
        int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 }, dy = { 0, -1, -1, -1, 0, 1, 1, 1 };
        bool In(int x, int y) => x >= 0 && y >= 0 && x < nx && y < ny && mask[y * nx + x];
        int px = sx, py = sy, dir = 0; ring.Add((sx, sy));
        int guard = 0, maxG = nx * ny * 4 + 64;
        while (guard++ < maxG)
        {
            bool found = false;
            for (int k = 0; k < 8; k++)
            {
                int nd = (dir + k) % 8, ax = px + dx[nd], ay = py + dy[nd];
                if (In(ax, ay)) { px = ax; py = ay; ring.Add((px, py)); dir = (nd + 6) % 8; found = true; break; }
            }
            if (!found) break;
            if (px == sx && py == sy) break;
        }
        return ring;
    }

    private static List<(int x, int y)> Simplify(List<(int x, int y)> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count]; keep[0] = keep[pts.Count - 1] = true;
        var st = new Stack<(int a, int b)>(); st.Push((0, pts.Count - 1));
        while (st.Count > 0)
        {
            var (a, b) = st.Pop(); double maxd = -1; int idx = -1;
            double ax = pts[a].x, ay = pts[a].y, bx = pts[b].x, by = pts[b].y;
            double dxl = bx - ax, dyl = by - ay, len = Math.Sqrt(dxl * dxl + dyl * dyl);
            for (int i = a + 1; i < b; i++)
            {
                double dd = len < 1e-9
                    ? Math.Sqrt((pts[i].x - ax) * (pts[i].x - ax) + (pts[i].y - ay) * (pts[i].y - ay))
                    : Math.Abs(dxl * (ay - pts[i].y) - (ax - pts[i].x) * dyl) / len;
                if (dd > maxd) { maxd = dd; idx = i; }
            }
            if (maxd > tol && idx > 0) { keep[idx] = true; st.Push((a, idx)); st.Push((idx, b)); }
        }
        var outp = new List<(int x, int y)>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]);
        return outp;
    }
}
