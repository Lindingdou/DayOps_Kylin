using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云算子的纯逻辑集合（原 PointCloudLib 各 native 算子的托管等价）：色带 / 逐点属性区间 /
/// C2C 最近邻位移 / 点云直接剖面 / 点云栅格化。
///
/// 都不碰 UI 与场景，便于单测；对话框只负责取参数，命令入口只负责把结果放回场景。
/// </summary>
public static class PointCloudOps
{
    // ═══════════════ 色带（忠实原版三档：地形色 / 灰阶 / 分歧色）═══════════════

    /// <summary>色带取色。colormap: 0=地形色(蓝→青→绿→黄→红) 1=灰阶 2=分歧色(蓝-白-红)。t 自动钳到 [0,1]。</summary>
    public static (float r, float g, float b) Ramp(int colormap, double t)
    {
        t = Math.Clamp(t, 0, 1);
        switch (colormap)
        {
            case 1:   // 灰阶：暗→亮
            {
                float v = (float)(0.15 + 0.8 * t);
                return (v, v, v);
            }
            case 2:   // 分歧色：蓝(负) — 白(零) — 红(正)。有正负的量(位移/曲率)用它，零值居中显白
            {
                if (t < 0.5)
                {
                    float k = (float)(t / 0.5);
                    return (0.15f + 0.85f * k, 0.35f + 0.65f * k, 0.75f + 0.25f * k);
                }
                else
                {
                    float k = (float)((t - 0.5) / 0.5);
                    return (1f - 0.05f * k, 1f - 0.75f * k, 1f - 0.85f * k);
                }
            }
            default:  // 地形色：蓝→青→绿→黄→红（同原版「地形色（蓝→红）」）
            {
                (float r, float g, float b)[] stops =
                {
                    (0.15f, 0.30f, 0.85f), (0.10f, 0.75f, 0.85f), (0.20f, 0.75f, 0.25f),
                    (0.95f, 0.90f, 0.20f), (0.90f, 0.15f, 0.10f),
                };
                double s = t * (stops.Length - 1);
                int i = Math.Min(stops.Length - 2, (int)Math.Floor(s));
                float f = (float)(s - i);
                var a = stops[i]; var b = stops[i + 1];
                return (a.r + (b.r - a.r) * f, a.g + (b.g - a.g) * f, a.b + (b.b - a.b) * f);
            }
        }
    }

    /// <summary>按显示区间 [lo,hi] 把逐点值映射成逐点色（区间外钳到端点，同原版 clamp 语义）。</summary>
    public static List<(float r, float g, float b)> Colorize(IReadOnlyList<double> values, int colormap, double lo, double hi)
    {
        var res = new List<(float, float, float)>(values.Count);
        double span = hi - lo;
        for (int i = 0; i < values.Count; i++)
        {
            double t = Math.Abs(span) < 1e-12 ? 0.5 : (values[i] - lo) / span;
            res.Add(Ramp(colormap, t));
        }
        return res;
    }

    // ═══════════════ 逐点属性（坡度 / 坡向 / 曲率）═══════════════

    /// <summary>分析项名：0=坡度 1=坡向 2=曲率。</summary>
    public static string AttrName(int attr) => attr switch { 1 => "坡向", 2 => "曲率", _ => "坡度" };

    /// <summary>分析项单位。</summary>
    public static string AttrUnit(int attr) => attr switch { 1 => "°", 2 => "", _ => "°" };

    /// <summary>
    /// 逐点属性取值：0=坡度(°) 1=坡向(罗盘°) 2=曲率(表面变异度 λmin/Σλ)。
    /// attribs 为 <see cref="PointNormals.ComputeFull"/> 的结果。
    /// </summary>
    public static double[] AttributeValues(IReadOnlyList<PointNormals.PointAttrib> attribs, int attr)
    {
        var v = new double[attribs.Count];
        for (int i = 0; i < attribs.Count; i++)
            v[i] = attr switch { 1 => attribs[i].AspectDeg, 2 => attribs[i].Curvature, _ => attribs[i].SlopeDeg };
        return v;
    }

    /// <summary>
    /// 分析项的默认显示区间（用户给的上限 ≤ 下限时取这里）：
    /// 坡度 0–90 / 坡向 0–360（跨数据集可比）/ 曲率按 P99 截断（少数尖点会把色带整段压平）。
    /// 忠实原 PointAttribDialog「上限 ≤ 下限时按分析项取默认区间」的口径。
    /// </summary>
    public static (double lo, double hi) DefaultRange(int attr, double[] values)
    {
        if (attr == 0) return (0, 90);
        if (attr == 1) return (0, 360);
        if (values.Length == 0) return (0, 1);
        var s = (double[])values.Clone();
        Array.Sort(s);
        double p99 = s[Math.Min(s.Length - 1, (int)Math.Floor(0.99 * (s.Length - 1)))];
        return (0, p99 > 1e-12 ? p99 : 1);
    }

    // ═══════════════ C2C 位移监测 ═══════════════

    /// <summary>C2C 结果：逐点位移 + 匹配情况 + 统计量（未匹配点的位移为 NaN，不参与统计也不着色）。</summary>
    public sealed class C2cResult
    {
        public double[] Dist = Array.Empty<double>();
        public int Matched;
        public int Unmatched;
        public double Min, Max, Mean, Std, AbsP95;
        public double[] Histogram = Array.Empty<double>();   // 20 桶计数
        public double HistLo, HistHi;
    }

    /// <summary>
    /// 两期点云逐点最近邻位移（C2C）：对 <paramref name="b"/>（对比期/后期）的每个点，
    /// 求它到 <paramref name="a"/>（基准期/前期）的最近距离。
    ///
    /// · maxDist &gt; 0 时超限即判「未匹配」(NaN)，不伪造巨大位移（原版同款保护）；
    /// · signedByZ = true 时按高差定正负：对比期点高于最近邻 → 正(隆起)，低于 → 负(沉降)。
    ///
    /// 用 XY 网格 + 逐环扩张做最近邻，避免 O(n·m) 全对全（露天矿两期点云各几十万点，
    /// 朴素实现是几小时量级）。
    /// </summary>
    public static C2cResult CloudToCloud(
        IReadOnlyList<(double x, double y, double z)> a,
        IReadOnlyList<(double x, double y, double z)> b,
        double maxDist, bool signedByZ)
    {
        var res = new C2cResult { Dist = new double[b?.Count ?? 0] };
        if (a == null || b == null || a.Count == 0 || b.Count == 0) return res;

        var idx = new XyGrid(a);
        var vals = new List<double>(b.Count);
        for (int i = 0; i < b.Count; i++)
        {
            var p = b[i];
            int j = idx.Nearest(p, maxDist, out double d);
            if (j < 0) { res.Dist[i] = double.NaN; res.Unmatched++; continue; }
            double v = d;
            if (signedByZ && p.z < a[j].z) v = -d;
            res.Dist[i] = v; res.Matched++; vals.Add(v);
        }
        if (vals.Count == 0) return res;

        double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (double v in vals) { if (v < min) min = v; if (v > max) max = v; sum += v; }
        double mean = sum / vals.Count, var2 = 0;
        foreach (double v in vals) { double e = v - mean; var2 += e * e; }
        res.Min = min; res.Max = max; res.Mean = mean; res.Std = Math.Sqrt(var2 / vals.Count);

        var abs = new double[vals.Count];
        for (int i = 0; i < vals.Count; i++) abs[i] = Math.Abs(vals[i]);
        Array.Sort(abs);
        res.AbsP95 = abs[Math.Min(abs.Length - 1, (int)Math.Floor(0.95 * (abs.Length - 1)))];

        // 20 桶直方图（原版「统计与分布直方图」）
        const int nb = 20;
        res.Histogram = new double[nb];
        res.HistLo = min; res.HistHi = max;
        double span = max - min;
        foreach (double v in vals)
        {
            int k = span < 1e-12 ? 0 : (int)((v - min) / span * nb);
            res.Histogram[Math.Clamp(k, 0, nb - 1)]++;
        }
        return res;
    }

    // ═══════════════ 点云直接剖面 ═══════════════

    /// <summary>剖面站点：沿线里程 / 取值高程 / 该站缓冲带内点数（0 = 没测到，留缺口不插值）。</summary>
    public readonly record struct ProfileStation(double Dist, double Z, int Count);

    /// <summary>
    /// 点云直接剖面：沿多段线按 step 布站，取每站 halfWidth 缓冲带内点的 agg 值
    /// （agg 0=最低点(地面) 1=均值 2=最高点(顶面)）。
    ///
    /// 不建面不插值 —— 没测到的站点留成缺口(Count=0)，而不是被 TIN 长边桥接成一段假地面。
    /// </summary>
    public static List<ProfileStation> CloudProfile(
        IReadOnlyList<(double x, double y, double z)> pts,
        IReadOnlyList<(double x, double y)> line,
        double halfWidth, double step, int agg)
    {
        var res = new List<ProfileStation>();
        if (pts == null || line == null || line.Count < 2) return res;
        halfWidth = Math.Max(halfWidth, 1e-6);
        step = Math.Max(step, 1e-6);

        // 站点：沿线按里程等距布点（含首尾两端），多段线按累积里程定位到具体段上
        var cum = new double[line.Count];
        for (int i = 1; i < line.Count; i++)
        {
            double dx = line[i].x - line[i - 1].x, dy = line[i].y - line[i - 1].y;
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = cum[^1];
        if (total < 1e-12) return res;
        var stations = new List<(double x, double y, double dist)>();
        int seg = 0;
        for (double d = 0; d <= total + 1e-9; d += step)
        {
            double dd = Math.Min(d, total);
            while (seg + 2 < line.Count && cum[seg + 1] < dd) seg++;
            double segLen = cum[seg + 1] - cum[seg];
            double t = segLen < 1e-12 ? 0 : (dd - cum[seg]) / segLen;
            stations.Add((line[seg].x + (line[seg + 1].x - line[seg].x) * t,
                          line[seg].y + (line[seg + 1].y - line[seg].y) * t, dd));
        }
        if (stations.Count == 0) return res;

        // 站点索引：点按 XY 格分桶，每点只投给最近的站(避免逐点扫全部站)
        double cell = Math.Max(halfWidth, step);
        var buckets = new Dictionary<(long, long), List<int>>();
        for (int s = 0; s < stations.Count; s++)
        {
            var key = ((long)Math.Floor(stations[s].x / cell), (long)Math.Floor(stations[s].y / cell));
            if (!buckets.TryGetValue(key, out var l)) { l = new List<int>(); buckets[key] = l; }
            l.Add(s);
        }

        var accZ = new double[stations.Count];
        var cnt = new int[stations.Count];
        var minZ = new double[stations.Count];
        var maxZ = new double[stations.Count];
        for (int s = 0; s < stations.Count; s++) { minZ[s] = double.MaxValue; maxZ[s] = double.MinValue; }

        double hw2 = halfWidth * halfWidth;
        foreach (var p in pts)
        {
            long gx = (long)Math.Floor(p.x / cell), gy = (long)Math.Floor(p.y / cell);
            int best = -1; double bestD = hw2;
            for (long ix = gx - 1; ix <= gx + 1; ix++)
                for (long iy = gy - 1; iy <= gy + 1; iy++)
                {
                    if (!buckets.TryGetValue((ix, iy), out var l)) continue;
                    foreach (int s in l)
                    {
                        double dx = stations[s].x - p.x, dy = stations[s].y - p.y;
                        double d2 = dx * dx + dy * dy;
                        if (d2 <= bestD) { bestD = d2; best = s; }
                    }
                }
            if (best < 0) continue;
            accZ[best] += p.z; cnt[best]++;
            if (p.z < minZ[best]) minZ[best] = p.z;
            if (p.z > maxZ[best]) maxZ[best] = p.z;
        }

        for (int s = 0; s < stations.Count; s++)
        {
            if (cnt[s] == 0) { res.Add(new ProfileStation(stations[s].dist, double.NaN, 0)); continue; }
            double z = agg switch { 1 => accZ[s] / cnt[s], 2 => maxZ[s], _ => minZ[s] };
            res.Add(new ProfileStation(stations[s].dist, z, cnt[s]));
        }
        return res;
    }

    // ═══════════════ 点云栅格化（算量/坡顶底线/统计共用）═══════════════

    /// <summary>栅格 DEM：每格一个高程 + 是否有数据（无数据格不虚构地形，同原版「只统计落到点的格」）。</summary>
    public sealed class Raster
    {
        public int Nx, Ny;
        public double Cell, MinX, MinY;
        public double[] Z = Array.Empty<double>();
        public bool[] Has = Array.Empty<bool>();
        public int Filled;
        public double At(int ix, int iy) => Z[iy * Nx + ix];
        public bool HasAt(int ix, int iy) => Has[iy * Nx + ix];
    }

    /// <summary>点云 → 栅格 DEM。agg 0=最低(取地面, 抗噪) 1=平均 2=最高。</summary>
    public static Raster Rasterize(IReadOnlyList<(double x, double y, double z)> pts, double cell, int agg)
    {
        var r = new Raster { Cell = Math.Max(cell, 1e-6) };
        if (pts == null || pts.Count == 0) { r.Nx = r.Ny = 0; return r; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts)
        {
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
        }
        r.MinX = minX; r.MinY = minY;
        r.Nx = Math.Max(1, (int)Math.Floor((maxX - minX) / r.Cell) + 1);
        r.Ny = Math.Max(1, (int)Math.Floor((maxY - minY) / r.Cell) + 1);
        int n = r.Nx * r.Ny;
        r.Z = new double[n]; r.Has = new bool[n];
        var cnt = agg == 1 ? new int[n] : null;
        foreach (var p in pts)
        {
            int ix = Math.Min(r.Nx - 1, Math.Max(0, (int)Math.Floor((p.x - minX) / r.Cell)));
            int iy = Math.Min(r.Ny - 1, Math.Max(0, (int)Math.Floor((p.y - minY) / r.Cell)));
            int k = iy * r.Nx + ix;
            if (!r.Has[k]) { r.Has[k] = true; r.Z[k] = p.z; if (cnt != null) cnt[k] = 1; r.Filled++; continue; }
            switch (agg)
            {
                case 1: r.Z[k] += p.z; cnt![k]++; break;
                case 2: if (p.z > r.Z[k]) r.Z[k] = p.z; break;
                default: if (p.z < r.Z[k]) r.Z[k] = p.z; break;
            }
        }
        if (cnt != null)
            for (int k = 0; k < n; k++) if (r.Has[k] && cnt[k] > 0) r.Z[k] /= cnt[k];
        return r;
    }

    /// <summary>
    /// 填补「有界小洞」：无数据格若在 <paramref name="radius"/> 格邻域内被有数据格包住，就用邻域均值补上。
    /// 只补包得住的小洞 —— 大片无数据区(点云根本没测到的地方)始终留空，不虚构地形。
    /// 迭代 radius 轮，每轮只补当前四邻域内至少 3 面有数据的格，避免从边界往里"长"出一片假地形。
    /// </summary>
    public static int FillSmallHoles(Raster r, int radius)
    {
        if (r.Nx == 0 || r.Ny == 0 || radius <= 0) return 0;
        int filled = 0;
        for (int pass = 0; pass < radius; pass++)
        {
            var addZ = new List<(int idx, double z)>();
            for (int iy = 0; iy < r.Ny; iy++)
                for (int ix = 0; ix < r.Nx; ix++)
                {
                    int k = iy * r.Nx + ix;
                    if (r.Has[k]) continue;
                    double sum = 0; int n = 0;
                    if (ix > 0 && r.Has[k - 1]) { sum += r.Z[k - 1]; n++; }
                    if (ix + 1 < r.Nx && r.Has[k + 1]) { sum += r.Z[k + 1]; n++; }
                    if (iy > 0 && r.Has[k - r.Nx]) { sum += r.Z[k - r.Nx]; n++; }
                    if (iy + 1 < r.Ny && r.Has[k + r.Nx]) { sum += r.Z[k + r.Nx]; n++; }
                    if (n >= 3) addZ.Add((k, sum / n));   // 四面里至少三面有数据 = 被包住的小洞
                }
            if (addZ.Count == 0) break;
            foreach (var (k, z) in addZ) { r.Z[k] = z; r.Has[k] = true; r.Filled++; filled++; }
        }
        return filled;
    }

    /// <summary>
    /// 栅格 DEM → 三角网（格中心为顶点，相邻四格都有数据才成一个四边形、切两个三角）。
    /// 两期算量要的是"面"，而点云只有点：直接栅格这条路就是在这里把格网变成可求交的面，
    /// 无数据格自然形成孔洞，不会被桥接成假地面。
    /// </summary>
    public static (double[] verts, int[] tris) RasterToMesh(Raster r)
    {
        if (r.Nx == 0 || r.Ny == 0) return (Array.Empty<double>(), Array.Empty<int>());
        var idx = new int[r.Nx * r.Ny];
        var vs = new List<double>(r.Filled * 3);
        int n = 0;
        for (int iy = 0; iy < r.Ny; iy++)
            for (int ix = 0; ix < r.Nx; ix++)
            {
                int k = iy * r.Nx + ix;
                if (!r.Has[k]) { idx[k] = -1; continue; }
                idx[k] = n++;
                vs.Add(r.MinX + (ix + 0.5) * r.Cell); vs.Add(r.MinY + (iy + 0.5) * r.Cell); vs.Add(r.Z[k]);
            }
        var ts = new List<int>();
        for (int iy = 0; iy + 1 < r.Ny; iy++)
            for (int ix = 0; ix + 1 < r.Nx; ix++)
            {
                int a = idx[iy * r.Nx + ix], b = idx[iy * r.Nx + ix + 1];
                int c = idx[(iy + 1) * r.Nx + ix + 1], d = idx[(iy + 1) * r.Nx + ix];
                if (a < 0 || b < 0 || c < 0 || d < 0) continue;
                ts.Add(a); ts.Add(b); ts.Add(c);
                ts.Add(a); ts.Add(c); ts.Add(d);
            }
        return (vs.ToArray(), ts.ToArray());
    }

    /// <summary>栅格格中心点集（供建 TIN / 等高线 / 断棱线检测复用现成的散点算法）。</summary>
    public static List<(double x, double y, double z)> RasterPoints(Raster r)
    {
        var pts = new List<(double, double, double)>(r.Filled);
        for (int iy = 0; iy < r.Ny; iy++)
            for (int ix = 0; ix < r.Nx; ix++)
                if (r.HasAt(ix, iy))
                    pts.Add((r.MinX + (ix + 0.5) * r.Cell, r.MinY + (iy + 0.5) * r.Cell, r.At(ix, iy)));
        return pts;
    }

    /// <summary>高程直方图（质量统计用）：返回各桶计数与桶宽。</summary>
    public static (int[] hist, double lo, double hi) ZHistogram(IReadOnlyList<(double x, double y, double z)> pts, int buckets = 20)
    {
        var h = new int[Math.Max(1, buckets)];
        if (pts == null || pts.Count == 0) return (h, 0, 0);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var p in pts) { if (p.z < lo) lo = p.z; if (p.z > hi) hi = p.z; }
        double span = hi - lo;
        foreach (var p in pts)
        {
            int k = span < 1e-12 ? 0 : (int)((p.z - lo) / span * h.Length);
            h[Math.Clamp(k, 0, h.Length - 1)]++;
        }
        return (h, lo, hi);
    }

    /// <summary>
    /// 平均点间距（质量统计用）：按包围盒面积与点数估算 √(A/n)。
    /// 与逐点最近邻均距相比是个粗估，但对"这份点云够不够密"的判断足够，且 O(n)。
    /// </summary>
    public static double MeanSpacing(int count, double areaXY)
        => count <= 0 || areaXY <= 1e-9 ? 0 : Math.Sqrt(areaXY / count);

    // ═══════════════ XY 网格最近邻索引 ═══════════════

    /// <summary>XY 分桶 + 逐环扩张的最近邻索引（C2C / 邻域查询用）。</summary>
    public sealed class XyGrid
    {
        private readonly IReadOnlyList<(double x, double y, double z)> _pts;
        private readonly Dictionary<(long, long), List<int>> _grid = new();
        private readonly double _cell;
        private readonly long _gx0, _gy0, _gx1, _gy1;

        public XyGrid(IReadOnlyList<(double x, double y, double z)> pts)
        {
            _pts = pts;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
            }
            double area = Math.Max((maxX - minX) * (maxY - minY), 1e-9);
            _cell = Math.Max(Math.Sqrt(area / Math.Max(pts.Count, 1)) * 2, 1e-6);
            for (int i = 0; i < pts.Count; i++)
            {
                var key = Key(pts[i].x, pts[i].y);
                if (!_grid.TryGetValue(key, out var l)) { l = new List<int>(); _grid[key] = l; }
                l.Add(i);
            }
            _gx0 = (long)Math.Floor(minX / _cell); _gx1 = (long)Math.Floor(maxX / _cell);
            _gy0 = (long)Math.Floor(minY / _cell); _gy1 = (long)Math.Floor(maxY / _cell);
        }

        private (long, long) Key(double x, double y) => ((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell));

        /// <summary>最近点下标与三维距离；maxDist &gt; 0 时超限返回 -1（未匹配）。空集返回 -1。</summary>
        public int Nearest((double x, double y, double z) p, double maxDist, out double dist)
        {
            dist = double.MaxValue;
            if (_pts.Count == 0) return -1;
            var (cx, cy) = Key(p.x, p.y);
            int best = -1; double best2 = double.MaxValue;
            long maxRing = Math.Max(_gx1 - _gx0, _gy1 - _gy0) + 1;
            for (long ring = 0; ring <= maxRing; ring++)
            {
                for (long ix = cx - ring; ix <= cx + ring; ix++)
                    for (long iy = cy - ring; iy <= cy + ring; iy++)
                    {
                        // 只扫本环新增的一圈(内圈上一轮已扫过)
                        if (ring > 0 && Math.Abs(ix - cx) != ring && Math.Abs(iy - cy) != ring) continue;
                        if (!_grid.TryGetValue((ix, iy), out var l)) continue;
                        foreach (int j in l)
                        {
                            var q = _pts[j];
                            double dx = q.x - p.x, dy = q.y - p.y, dz = q.z - p.z;
                            double d2 = dx * dx + dy * dy + dz * dz;
                            if (d2 < best2) { best2 = d2; best = j; }
                        }
                    }
                // 找到了且已确保更外环不可能更近(环内切距离 ≥ ring×cell)，即可收手
                if (best >= 0 && best2 <= ring * _cell * (ring * _cell)) break;
                if (maxDist > 0 && ring * _cell > maxDist && best < 0) break;   // 超出匹配半径仍没找到 → 未匹配
            }
            if (best < 0) return -1;
            double d = Math.Sqrt(best2);
            if (maxDist > 0 && d > maxDist) return -1;
            dist = d;
            return best;
        }
    }
}
