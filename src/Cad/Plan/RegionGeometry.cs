using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 可采区域多段线的栅格布尔运算（检测重叠 / 求差），用于保证各区域空间互斥。
/// 任意凹多边形稳健；与笔刷一致：栅格化 + Moore 描边 + Douglas-Peucker 抽稀。纯托管、确定性。
/// 多边形以扁平 xyz（首尾不重复，隐式闭合）表示。
/// </summary>
public static class RegionGeometry
{
    /// <summary>两区域是否「成片」重叠（重叠面积占较小者 ≥2%）；仅边界相邻(abut)不算重叠。</summary>
    public static bool Overlaps(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length < 9 || b.Length < 9) return false;
        Bounds(a, out double aminx, out double aminy, out double amaxx, out double amaxy);
        Bounds(b, out double bminx, out double bminy, out double bmaxx, out double bmaxy);
        double minx = Math.Max(aminx, bminx), miny = Math.Max(aminy, bminy);
        double maxx = Math.Min(amaxx, bmaxx), maxy = Math.Min(amaxy, bmaxy);
        if (maxx <= minx || maxy <= miny) return false;   // 包围盒不相交

        double span = Math.Max(maxx - minx, maxy - miny);
        double cell = Math.Max(span / 512.0, 1e-6);       // 细一点，边界相邻的薄条占比可忽略
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

    /// <summary>subject 减去 clip（subject ∖ clip），只保留面积最大的一块；被完全覆盖返回空数组。Z 用 subject 的平面拟合。</summary>
    public static double[] SubtractKeepLargest(double[] subject, double[] clip)
    {
        if (subject == null || subject.Length < 9) return Array.Empty<double>();
        if (clip == null || clip.Length < 9) return (double[])subject.Clone();

        Bounds(subject, out double minx, out double miny, out double maxx, out double maxy);
        double span = Math.Max(maxx - minx, maxy - miny);
        if (span <= 0) return Array.Empty<double>();
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
        if (start < 0) return Array.Empty<double>();
        var ring = TraceBoundary(ms, nx, ny, start);
        if (ring.Count < 3) return Array.Empty<double>();
        var simp = Simplify(ring, 1.2);
        if (simp.Count < 3) return Array.Empty<double>();

        PlanarZ(subject, out double za, out double zb, out double zc);
        var flat = new double[simp.Count * 3];
        for (int i = 0; i < simp.Count; i++)
        {
            double wx = minx + (simp[i].x + 0.5) * cell, wy = miny + (simp[i].y + 0.5) * cell;
            flat[i * 3] = wx; flat[i * 3 + 1] = wy; flat[i * 3 + 2] = za * wx + zb * wy + zc;
        }
        return flat;
    }

    // ── 内部 ──
    private static void Bounds(double[] p, out double minx, out double miny, out double maxx, out double maxy)
    {
        minx = miny = 1e300; maxx = maxy = -1e300;
        for (int i = 0; i + 2 < p.Length; i += 3)
        {
            minx = Math.Min(minx, p[i]); maxx = Math.Max(maxx, p[i]);
            miny = Math.Min(miny, p[i + 1]); maxy = Math.Max(maxy, p[i + 1]);
        }
    }

    private static double ShoelaceArea(double[] p)
    {
        int n = p.Length / 3; double s = 0;
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; s += p[i * 3] * p[j * 3 + 1] - p[j * 3] * p[i * 3 + 1]; }
        return 0.5 * s;
    }

    private static void Rasterize(double[] ring, double minX, double minY, double cell, int nx, int ny, bool[] mask)
    {
        int n = ring.Length / 3; if (n < 3) return;
        for (int y = 0; y < ny; y++)
        {
            double wy = minY + (y + 0.5) * cell;
            var xs = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double y0 = ring[i * 3 + 1], y1 = ring[((i + 1) % n) * 3 + 1];
                double x0 = ring[i * 3], x1 = ring[((i + 1) % n) * 3];
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

    // 8-邻接连通块，只保留格子数最多的一块，其余抹掉（与笔刷一致）。
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

    // Z = a·x + b·y + c 最小二乘平面拟合（保留区域高程趋势）；退化则用均值。
    private static void PlanarZ(double[] p, out double a, out double b, out double c)
    {
        int n = p.Length / 3;
        double Sxx = 0, Sxy = 0, Syy = 0, Sx = 0, Sy = 0, Sxz = 0, Syz = 0, Sz = 0;
        for (int i = 0; i < n; i++)
        {
            double x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
            Sxx += x * x; Sxy += x * y; Syy += y * y; Sx += x; Sy += y; Sxz += x * z; Syz += y * z; Sz += z;
        }
        if (Solve3(Sxx, Sxy, Sx, Sxy, Syy, Sy, Sx, Sy, n, Sxz, Syz, Sz, out a, out b, out c)) return;
        a = 0; b = 0; c = n > 0 ? Sz / n : 0;
    }

    // 解 3×3 线性方程组（Cramer）；行列式近 0 返回 false。
    private static bool Solve3(
        double a11, double a12, double a13, double a21, double a22, double a23, double a31, double a32, double a33,
        double b1, double b2, double b3, out double x, out double y, out double z)
    {
        double det = a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
        x = y = z = 0;
        if (Math.Abs(det) < 1e-9) return false;
        double dx = b1 * (a22 * a33 - a23 * a32) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a32 - a22 * b3);
        double dy = a11 * (b2 * a33 - a23 * b3) - b1 * (a21 * a33 - a23 * a31) + a13 * (a21 * b3 - b2 * a31);
        double dz = a11 * (a22 * b3 - b2 * a32) - a12 * (a21 * b3 - b2 * a31) + b1 * (a21 * a32 - a22 * a31);
        x = dx / det; y = dy / det; z = dz / det;
        return true;
    }
}

/// <summary>
/// 区域选区笔刷会话（PS 式，单选区）：把【选中的一个区域】光栅成一张选区掩膜，
/// 笔刷沿光标涂改——Alt 涂=把笔刷圆覆盖的范围【并入】该选区(扩大)，普通涂=【移出】(缩小)；
/// 每次涂改后立即重描该选区边界(Moore + DP)→ Ring 实时更新。其它区域只作背景显示，不改。
/// 笔刷圆圈由宿主画。纯托管、无内核改动。
/// </summary>
public sealed class RegionBrushSession
{
    private readonly double _minX, _minY, _cell;
    private readonly int _nx, _ny;
    private readonly bool[] _mask;                 // 当前选区
    public double RepZ { get; }                    // 选区高程（边界/圆圈用）
    public uint TargetColor { get; }
    public IReadOnlyList<double[]> Context { get; } // 其它区域（只显示）
    public IReadOnlyList<uint> ContextColors { get; }
    public double[] Ring { get; private set; }      // 当前选区边界（实时重描）

    private bool _add;
    private bool _changed;
    private double _lastX, _lastY; private bool _hasLast;

    public RegionBrushSession(double[] targetRing, uint targetColor,
        IReadOnlyList<double[]> contextRings, IReadOnlyList<uint> contextColors, double cellSize)
    {
        TargetColor = targetColor;
        Context = contextRings ?? Array.Empty<double[]>();
        ContextColors = contextColors ?? Array.Empty<uint>();
        Ring = (double[])(targetRing ?? Array.Empty<double>()).Clone();

        double minX = 1e300, minY = 1e300, maxX = -1e300, maxY = -1e300, zsum = 0; int zc = 0;
        for (int i = 0; i + 2 < targetRing.Length; i += 3)
        {
            minX = Math.Min(minX, targetRing[i]); maxX = Math.Max(maxX, targetRing[i]);
            minY = Math.Min(minY, targetRing[i + 1]); maxY = Math.Max(maxY, targetRing[i + 1]);
            zsum += targetRing[i + 2]; zc++;
        }
        if (zc == 0) { minX = minY = 0; maxX = maxY = 1; }
        RepZ = zc > 0 ? zsum / zc : 0;

        double span = Math.Max(maxX - minX, maxY - minY);
        double margin = Math.Max(150.0, 0.6 * span);   // 留足扩大空间
        minX -= margin; minY -= margin; maxX += margin; maxY += margin;
        _cell = cellSize > 0 ? cellSize : Math.Max(2.0, span / 800.0);
        long want = ((long)((maxX - minX) / _cell) + 1) * ((long)((maxY - minY) / _cell) + 1);
        while (want > 4_000_000) { _cell *= 1.5; want = ((long)((maxX - minX) / _cell) + 1) * ((long)((maxY - minY) / _cell) + 1); }
        _minX = minX; _minY = minY;
        _nx = (int)((maxX - minX) / _cell) + 1;
        _ny = (int)((maxY - minY) / _cell) + 1;
        _mask = new bool[_nx * _ny];
        FillPolygon(targetRing);
    }

    private int CX(double x) => Math.Clamp((int)((x - _minX) / _cell), 0, _nx - 1);
    private int CY(double y) => Math.Clamp((int)((y - _minY) / _cell), 0, _ny - 1);

    public void StrokeBegin(bool add) { _add = add; _hasLast = false; _changed = false; }

    public void StrokeMove(double wx, double wy, double radiusWorld)
    {
        if (_hasLast) PaintSegment(_lastX, _lastY, wx, wy, radiusWorld);
        else PaintDisk(wx, wy, radiusWorld);
        _lastX = wx; _lastY = wy; _hasLast = true;
        if (_changed) Retrace();   // 实时更新边界
    }

    public void StrokeEnd() { _hasLast = false; if (_changed) Retrace(); }

    /// <summary>选区被擦空返回 false（调用方据此删该区域）。</summary>
    public bool HasArea()
    {
        for (int i = 0; i < _mask.Length; i++) if (_mask[i]) return true;
        return false;
    }

    // ── 内部 ──
    private void PaintSegment(double x0, double y0, double x1, double y1, double r)
    {
        double d = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int steps = Math.Max(1, (int)(d / (_cell * 0.7)));
        for (int s = 0; s <= steps; s++)
        { double t = (double)s / steps; PaintDisk(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, r); }
    }

    private void PaintDisk(double wx, double wy, double r)
    {
        int rc = Math.Max(0, (int)Math.Ceiling(r / _cell));
        int cx = CX(wx), cy = CY(wy); double r2 = r * r;
        for (int dy = -rc; dy <= rc; dy++)
            for (int dx = -rc; dx <= rc; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= _nx || y >= _ny) continue;
                double wxk = _minX + (x + 0.5) * _cell, wyk = _minY + (y + 0.5) * _cell;
                if ((wxk - wx) * (wxk - wx) + (wyk - wy) * (wyk - wy) > r2) continue;
                int c = y * _nx + x;
                if (_mask[c] != _add) { _mask[c] = _add; _changed = true; }
            }
    }

    private void Retrace()
    {
        if (!_add) KeepLargestComponent();   // 减法可能把选区切碎 → 只留面积最大的一块,小块直接丢弃
        int N = _nx * _ny, start = -1;
        for (int c = 0; c < N; c++) if (_mask[c]) { start = c; break; }
        if (start < 0) { Ring = Array.Empty<double>(); return; }
        var ring = TraceBoundary(start);
        if (ring.Count < 3) { Ring = Array.Empty<double>(); return; }
        var simp = Simplify(ring, 1.5);
        var flat = new double[simp.Count * 3];
        for (int i = 0; i < simp.Count; i++)
        { flat[i * 3] = _minX + (simp[i].x + 0.5) * _cell; flat[i * 3 + 1] = _minY + (simp[i].y + 0.5) * _cell; flat[i * 3 + 2] = RepZ; }
        Ring = flat;
    }

    // 8-邻接连通块标记,只保留格子数最多的一块,其余从掩膜抹掉。
    // 与 TraceBoundary 同用 8-连通,保证留下的块和它描出的边界一致。
    private void KeepLargestComponent()
    {
        int N = _nx * _ny;
        var comp = new int[N];                 // 0=未访问/背景, >0=连通块号
        int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 }, dy = { 0, -1, -1, -1, 0, 1, 1, 1 };
        var stack = new Stack<int>();
        int label = 0, best = 0, bestCount = 0;
        for (int s = 0; s < N; s++)
        {
            if (!_mask[s] || comp[s] != 0) continue;
            label++; int count = 0;
            stack.Push(s); comp[s] = label;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); count++;
                int x = c % _nx, y = c / _nx;
                for (int k = 0; k < 8; k++)
                {
                    int ax = x + dx[k], ay = y + dy[k];
                    if (ax < 0 || ay < 0 || ax >= _nx || ay >= _ny) continue;
                    int ac = ay * _nx + ax;
                    if (_mask[ac] && comp[ac] == 0) { comp[ac] = label; stack.Push(ac); }
                }
            }
            if (count > bestCount) { bestCount = count; best = label; }
        }
        if (label <= 1 || best == 0) return;   // 0 或 1 块,无需裁剪
        for (int c = 0; c < N; c++) if (_mask[c] && comp[c] != best) _mask[c] = false;
    }

    private void FillPolygon(double[] ring)
    {
        int n = ring.Length / 3; if (n < 3) return;
        for (int y = 0; y < _ny; y++)
        {
            double wy = _minY + (y + 0.5) * _cell;
            var xs = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double y0 = ring[i * 3 + 1], y1 = ring[((i + 1) % n) * 3 + 1];
                double x0 = ring[i * 3], x1 = ring[((i + 1) % n) * 3];
                if ((y0 <= wy && y1 > wy) || (y1 <= wy && y0 > wy))
                    xs.Add(x0 + (wy - y0) / (y1 - y0) * (x1 - x0));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int xa = CX(xs[k]), xb = CX(xs[k + 1]);
                for (int x = xa; x <= xb; x++) _mask[y * _nx + x] = true;
            }
        }
    }

    private List<(int x, int y)> TraceBoundary(int start)
    {
        var ring = new List<(int x, int y)>();
        int sx = start % _nx, sy = start / _nx;
        int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 }, dy = { 0, -1, -1, -1, 0, 1, 1, 1 };
        bool In(int x, int y) => x >= 0 && y >= 0 && x < _nx && y < _ny && _mask[y * _nx + x];
        int px = sx, py = sy, dir = 0; ring.Add((sx, sy));
        int guard = 0, maxG = _nx * _ny * 4 + 64;
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
