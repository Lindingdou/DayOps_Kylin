using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云提取坡顶/坡底线 —— 忠实移植原 Kernel/LasLib/src/bench_lines.cpp (laslib::bench::extract_bench_lines) 的现行通路：
///   最低点 DEM 栅格 → JFA 欧氏最近值填空 → 高斯平滑 → 坡度场 → 坡度等值线(marching squares, 平/陡分界 slope_flat_deg)
///   → 逐顶点沿等值线法向两侧采高程判坡顶(平台在上)/坡底(平台在下) → 多数表决平滑标签 → 覆盖裁剪(无点的格子不出线)
///   → DP 化简 + 去端钩 + 去碎 → 原始点云局部断面精修(坡顶→顶缘最高点, 坡底→最大高程梯度) → 终次覆盖裁剪
///   → (可选)作业区域内/外裁剪(port 自 xllAcEd BL_ClipPolyline)。
/// 原版旧的骨架化 / 配对 / 长线合并 / 坡度支撑过滤层在现行通路里已关(源码注释写着它们是「多条线拼接/全乱了」的来源)，这里同样不做。
/// 纯逻辑：不碰 UI 与场景，可后台线程跑、可单测。坐标全程 double(原版 float 在整场大坐标下会丢精度)。
/// </summary>
public static class SlopeLineExtractor
{
    /// <summary>一条开口多段线；Xs/Ys/Zs 等长。</summary>
    public sealed class Polyline3
    {
        public List<double> Xs = new(), Ys = new(), Zs = new();
        public int Count => Xs.Count;
        public void Add(double x, double y, double z) { Xs.Add(x); Ys.Add(y); Zs.Add(z); }
    }

    /// <summary>同原 BenchLineOptions（只列现行通路真正用到的项，默认值照抄）。</summary>
    public sealed class Options
    {
        public double CellSize = 1.0;            // DEM 格网 (m)
        public double GaussSigma = 0.35;         // DEM 平滑(格)，只用于出线顶点取 z
        public double CoverageMargin = 2.0;      // m；覆盖掩膜形态学闭运算半径(补内部小采样洞)，0 = 不裁剪
        public double SlopeFlatDeg = 18.0;       // 平/陡分界 = 等值线阈值(°)
        public double ContourDemSigma = 4.0;     // 格；等值线用 DEM 平滑
        public double ContourSlopeSigma = 3.5;   // 格；等值线用坡度场再平滑
        public double ContourZsideStep = 3.0;    // m；沿法向两侧采高程判坡顶/坡底的距离
        public int ContourLabelSmooth = 2;       // 顶点数；标签多数表决半径
        public double CandidateMinLineLen = 6.0; // m；出线 run 的最短长
        public double MinLineLen = 30.0;         // m；终次去碎阈值(原版 clutter_min = max(30, candidate_min_line_len))
        public double SimplifyTol = 4.0;         // m；DP 容差
        public int SmoothIters = 0;              // Chaikin 次数(0 = 保持折线)
        public bool RefineEnabled = true;
        public double RefineMaxShift = 2.0;      // m
        public double RefineProfileLen = 3.0;    // m；沿线半长
        public double RefineProfileWidth = 4.0;  // m；横向半宽
        public double RefineBinSize = 0.5;       // m；坡底断面分箱
        public int RefineMinPoints = 6;
        public long MaxCells = 80_000_000;       // DEM 格子数上限
    }

    public sealed class Stats
    {
        public long InputPoints;
        public int DemW, DemH;
        public double CellSize, OriginX, OriginY;
        public int CrestLines, ToeLines;
        public double CrestLenM, ToeLenM;
        public long RefinedCrestVertices, RefinedToeVertices;
        public double MsRaster, MsContour, MsRefine, MsTotal;
    }

    private const float NoData = float.MaxValue;   // 原版 kNaN = FLT_MAX

    /// <summary>
    /// 主入口。失败返回 false 并给 error；成功但没找到台阶时 crest/toe 可为空。
    /// progress(0~1, 阶段名) 可从后台线程回调；ct 取消时抛 OperationCanceledException。
    /// </summary>
    public static bool Extract(IReadOnlyList<(double x, double y, double z)> pts, Options opt,
                               out List<Polyline3> crest, out List<Polyline3> toe, Stats stats, out string error,
                               Action<double, string>? progress = null, CancellationToken ct = default)
    {
        crest = new List<Polyline3>(); toe = new List<Polyline3>(); error = "";
        var swAll = Stopwatch.StartNew();
        if (pts == null || pts.Count == 0) { error = "点云为空"; return false; }
        double cs = opt.CellSize > 0 ? opt.CellSize : 1.0;

        // ── 范围 ──
        double xmn = double.MaxValue, ymn = double.MaxValue, xmx = double.MinValue, ymx = double.MinValue;
        int count = pts.Count;
        for (int i = 0; i < count; i++)
        {
            var p = pts[i];
            if (p.x < xmn) xmn = p.x; if (p.x > xmx) xmx = p.x;
            if (p.y < ymn) ymn = p.y; if (p.y > ymx) ymx = p.y;
        }
        int w = Math.Max(1, (int)((xmx - xmn) / cs) + 1);
        int h = Math.Max(1, (int)((ymx - ymn) / cs) + 1);
        long cap = opt.MaxCells > 0 ? opt.MaxCells : 80_000_000;
        if ((long)w * h > cap) { error = $"DEM 格网过大（{(long)w * h:N0} 格 > {cap:N0}），请把 DEM 网格尺寸调大"; return false; }
        stats.InputPoints = count; stats.DemW = w; stats.DemH = h; stats.CellSize = cs; stats.OriginX = xmn; stats.OriginY = ymn;
        int n = w * h;

        var sw = Stopwatch.StartNew();
        progress?.Invoke(0.02, "栅格化 DEM");
        // ── 最低点 DEM + 有点掩膜 ──
        var dem = new float[n];
        Array.Fill(dem, NoData);
        var valid = new byte[n];
        for (int i = 0; i < count; i++)
        {
            var p = pts[i];
            int gx = Math.Clamp((int)((p.x - xmn) / cs), 0, w - 1);
            int gy = Math.Clamp((int)((p.y - ymn) / cs), 0, h - 1);
            int cid = gy * w + gx;
            float z = (float)p.z;
            if (dem[cid] == NoData || z < dem[cid]) dem[cid] = z;
            valid[cid] = 1;
        }
        ct.ThrowIfCancellationRequested();
        // 空格按欧氏最近有值格填(Jump-Flooding)。Chebyshev/BFS 填会出方块状 Voronoi，往顶帽里塞假脊；欧氏最近像 scipy EDT 一样平滑。
        {
            var seed = new int[n]; bool any = false;
            for (int id = 0; id < n; id++) { if (dem[id] != NoData) { seed[id] = id; any = true; } else seed[id] = -1; }
            if (!any) { error = "DEM 没有有效格子"; return false; }
            var nseed = new int[n];
            int step0 = 1; while (step0 < Math.Max(w, h)) step0 <<= 1; step0 >>= 1;
            for (int step = step0; step >= 1; step >>= 1)
            {
                int st = step;
                var src = seed; var dst = nseed;
                ParallelRows(h, ct, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int id = y * w + x;
                            int bs = src[id];
                            double best = bs < 0 ? 1e30 : D2(x, y, bs, w);
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    if (dx == 0 && dy == 0) continue;
                                    int nx = x + dx * st, ny = y + dy * st;
                                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                    int s = src[ny * w + nx];
                                    if (s < 0) continue;
                                    double dd = D2(x, y, s, w);
                                    if (dd < best) { best = dd; bs = s; }
                                }
                            dst[id] = bs;
                        }
                });
                (seed, nseed) = (nseed, seed);
            }
            for (int id = 0; id < n; id++) if (dem[id] == NoData && seed[id] >= 0) dem[id] = dem[seed[id]];
        }
        var demS = GaussianBlur(dem, w, h, (float)opt.GaussSigma, ct);   // 出线顶点取 z 用
        stats.MsRaster = sw.Elapsed.TotalMilliseconds;
        // （原版此处还算了 dem_s 的坡度 + 2σ 平滑给旧骨架通路用，现行通路不再引用，省掉。）

        // ── 坡顶/坡底 = 坡度场的 marching-squares 等值线 ──
        // 先重平滑 DEM(生产用的 dem_s σ 很小是为了覆盖，1m 坡度会带毛刺)，再取坡度，再把坡度场平滑一点 —— 同原型管线。
        sw.Restart();
        progress?.Invoke(0.25, "坡度场");
        var demC = GaussianBlur(dem, w, h, (float)opt.ContourDemSigma, ct);
        dem = null!;   // 原始 DEM 到此用完(demS/demC 都已派生)，整场几千万格先还给 GC
        ComputeSlope(demC, w, h, (float)cs, out var slopeForC, out _, out _, ct, wantGrad: false);
        var slopeC = GaussianBlur(slopeForC, w, h, (float)opt.ContourSlopeSigma, ct);
        slopeForC = null!;
        // 坡度场的梯度 = 等值线法向，指向更陡一侧。沿它两侧采几米高程就是稳定的坡顶/坡底判据。
        ComputeSlope(slopeC, w, h, (float)cs, out _, out var scx, out var scy, ct, wantSlope: false);

        // 覆盖掩膜 = 真有点的格子(valid)按 coverage_margin 做闭运算。JFA 把 dem 铺满了整个包围盒，
        // 等值线必须裁到真有点的地方，否则会在无数据区画出坡顶/坡底线。闭运算(先膨胀后腐蚀)补内部小采样洞、
        // 不外扩边界、真空洞仍不覆盖。clip_on 只由 coverage_margin>0 决定，与格子取整无关。
        bool clipOn = opt.CoverageMargin > 0;
        int coverR = Math.Max(0, (int)Math.Round(opt.CoverageMargin / cs));
        byte[] cover;
        if (clipOn && coverR > 0)
        {
            var vf = new float[n];
            for (int i = 0; i < n; i++) vf[i] = valid[i] != 0 ? 1f : 0f;
            var vdil = BoxMorph(vf, w, h, coverR, false, ct);   // 膨胀
            var vcls = BoxMorph(vdil, w, h, coverR, true, ct);  // 腐蚀 → 闭运算
            cover = new byte[n];
            for (int i = 0; i < n; i++) cover[i] = vcls[i] > 0.5f ? (byte)1 : (byte)0;
        }
        else cover = valid;   // 严格裁到真有点的格子

        bool Covered(double cx, double cy)
        {
            if (!clipOn) return true;
            int xi = (int)Math.Round(cx), yi = (int)Math.Round(cy);
            if (xi < 0 || yi < 0 || xi >= w || yi >= h) return false;
            return cover[yi * w + xi] != 0;
        }
        float CellAt(float[] f, double cx, double cy)
        {
            int xi = (int)Math.Round(cx), yi = (int)Math.Round(cy);
            if (xi < 0 || yi < 0 || xi >= w || yi >= h) return NoData;
            return f[yi * w + xi];
        }

        double runMin = Math.Max(4.0, opt.CandidateMinLineLen);
        double tol = Math.Max(2.0, opt.SimplifyTol);
        void EmitRun(List<(float x, float y)> run, List<Polyline3> outLines)
        {
            if (run.Count < 2) return;
            var p = new Polyline3();
            foreach (var q in run)   // q = 格坐标(亚格)
                p.Add(xmn + (q.x + 0.5) * cs, ymn + (q.y + 0.5) * cs, CellAt(demS, q.x, q.y));
            if (PolyLen(p) < runMin) return;
            var s = DpSimplify(p, tol);
            Chaikin(s, opt.SmoothIters);
            outLines.Add(s);
        }

        progress?.Invoke(0.45, "坡度等值线");
        ct.ThrowIfCancellationRequested();
        var contours = MarchingSquares(slopeC, w, h, (float)opt.SlopeFlatDeg);
        double zst = opt.ContourZsideStep / cs;    // 采样步长(格)
        int lblwin = Math.Max(0, opt.ContourLabelSmooth);
        var runC = new List<(float x, float y)>(); var runT = new List<(float x, float y)>();
        foreach (var cont in contours)
        {
            int m = cont.Count;
            var lab = new byte[m];                    // 1 = 坡顶, 0 = 坡底
            for (int i = 0; i < m; i++)
            {
                double cx = cont[i].x, cy = cont[i].y;
                double nx = CellAt(scx, cx, cy), ny = CellAt(scy, cx, cy);
                double nm = Math.Sqrt(nx * nx + ny * ny);
                if (nm > 1e-6)
                {
                    nx /= nm; ny /= nm;               // 单位法向 → 更陡一侧
                    float zSteep = CellAt(demC, cx + zst * nx, cy + zst * ny);
                    float zFlat = CellAt(demC, cx - zst * nx, cy - zst * ny);
                    lab[i] = zFlat > zSteep ? (byte)1 : (byte)0;   // 平台(平的一侧)在上 → 坡顶
                }
            }
            if (lblwin > 0 && m > 2 * lblwin + 1)   // 多数表决平滑(不绕接)
            {
                var labs = new byte[m];
                for (int i = 0; i < m; i++)
                {
                    int s = 0;
                    for (int d = -lblwin; d <= lblwin; d++)
                    {
                        int j = i + d; if (j < 0) j = 0; else if (j >= m) j = m - 1;
                        s += lab[j] != 0 ? 1 : -1;
                    }
                    labs[i] = s >= 0 ? (byte)1 : (byte)0;
                }
                lab = labs;
            }
            runC.Clear(); runT.Clear();               // 切成连续 run
            for (int i = 0; i < m; i++)
            {
                // 覆盖裁剪：落在无点格子上的顶点同时打断两种 run 并被丢掉 —— 等值线不延伸进无数据填充区。
                if (!Covered(cont[i].x, cont[i].y))
                {
                    if (runC.Count > 3) EmitRun(runC, crest);
                    if (runT.Count > 3) EmitRun(runT, toe);
                    runC.Clear(); runT.Clear();
                    continue;
                }
                if (lab[i] != 0)
                {
                    runC.Add(cont[i]);
                    if (runT.Count > 3) EmitRun(runT, toe);
                    runT.Clear();
                }
                else
                {
                    runT.Add(cont[i]);
                    if (runC.Count > 3) EmitRun(runC, crest);
                    runC.Clear();
                }
            }
            if (runC.Count > 3) EmitRun(runC, crest);
            if (runT.Count > 3) EmitRun(runT, toe);
        }
        contours = null!; scx = null!; scy = null!; demC = null!; slopeC = null!;
        stats.MsContour = sw.Elapsed.TotalMilliseconds;

        // ── 收尾：拉直 + 去端钩 + 去碎 ──
        // 终次 DP 把每条线化成几段近直线(台阶线本该如此)，Chaikin 圆一下余角，没并进真台阶线的短碎段丢掉。
        {
            double clutterMin = Math.Max(opt.MinLineLen, opt.CandidateMinLineLen);
            Cleanup(crest, tol, clutterMin, opt.SmoothIters);
            Cleanup(toe, tol, clutterMin, opt.SmoothIters);
        }
        ct.ThrowIfCancellationRequested();

        // ── 原始点云精修(坡顶+坡底都做，在最终折线上) ──
        // 把每个 DP 顶点吸到原始点云量出的真断棱(亚 DEM 精度)：坡顶 → 顶缘，坡底 → 最大高程梯度处。放最后做，DP 化简才不会把它抹掉。
        if (opt.RefineEnabled && opt.RefineMaxShift > 0 && (crest.Count > 0 || toe.Count > 0))
        {
            sw.Restart();
            progress?.Invoke(0.7, "原始点云精修");
            double indexCell = Math.Max(2.0, opt.RefineMaxShift);
            var pg = new PointGrid(pts, xmn, ymn, xmx, ymx, indexCell);
            stats.RefinedCrestVertices = RefineLines(crest, true, pts, pg, opt, ct);
            stats.RefinedToeVertices = RefineLines(toe, false, pts, pg, opt, ct);
            // 逐顶点精修落在略不同的局部断棱上 → 小抖动。再 DP 一次抹掉抖动(保留它选中的精修顶点) → 仍贴断棱的干净折线。
            for (int i = 0; i < crest.Count; i++) crest[i] = DpSimplify(crest[i], tol);
            for (int i = 0; i < toe.Count; i++) toe[i] = DpSimplify(toe[i], tol);
            stats.MsRefine = sw.Elapsed.TotalMilliseconds;
        }

        // ── 终次硬覆盖裁剪(双保险) ──
        // 无论上游怎么走(等值线跨过 JFA 填充区、精修漂移、DP 再化简)，保证没有输出顶点落在无点范围外：
        // 每条线在未覆盖顶点处切断，短于 min_keep 的 run 丢掉。这是挡住 JFA Voronoi 放射缝线进图纸的最后一道闸。
        // 世界→格坐标按 EmitRun 的 (q+0.5)*cs 精确反算(减回 0.5)。原版这里没减，贴着 DEM 边界行/列的顶点会
        // 被 lround 推到 w/h 之外判"未覆盖"，一条只剩两个 DP 顶点的直台阶线就整条丢了(合成矩形点云上 100% 复现)。
        if (clipOn)
        {
            double minKeep = Math.Max(6.0, 2.0 * cs);
            ClipToCover(crest, xmn, ymn, cs, Covered, minKeep);
            ClipToCover(toe, xmn, ymn, cs, Covered, minKeep);
        }

        stats.CrestLines = crest.Count; stats.ToeLines = toe.Count;
        stats.CrestLenM = 0; foreach (var p in crest) stats.CrestLenM += PolyLen(p);
        stats.ToeLenM = 0; foreach (var p in toe) stats.ToeLenM += PolyLen(p);
        stats.MsTotal = swAll.Elapsed.TotalMilliseconds;
        progress?.Invoke(1.0, "完成");
        return true;
    }

    // ═══════════════════ 作业区域裁剪（port 自 xllAcEd.cpp BL_ClipPolyline / BL_ClipLines）═══════════════════

    /// <summary>
    /// 把整批线裁到区域并集「内」(wantInside=true)或「外」，原地替换。rings 各环为扁平 XY [x0,y0,x1,y1,…]（≥3 顶点）。
    /// 纯 XY 射线法判内 + 逐段求交(端点 + 与各环边交点) → 取子段中点判内/外 → 保留匹配一侧，Z 在边界处线性插值；短于 minKeepLen 的碎段丢弃。
    /// </summary>
    public static void ClipToRegions(List<Polyline3> lines, IReadOnlyList<double[]> rings, bool wantInside, double minKeepLen)
    {
        var use = new List<double[]>();
        foreach (var r in rings) if (r != null && r.Length >= 6) use.Add(r);
        if (use.Count == 0) return;
        var outLines = new List<Polyline3>(lines.Count);
        foreach (var pl in lines) ClipPolyline(pl, use, wantInside, minKeepLen, outLines);
        lines.Clear(); lines.AddRange(outLines);
    }

    private static bool PointInRing(double x, double y, double[] ring)
    {
        int n = ring.Length / 2; bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1], xj = ring[2 * j], yj = ring[2 * j + 1];
            bool cross = ((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi);
            if (cross) inside = !inside;
        }
        return inside;
    }
    private static bool PointInAny(double x, double y, List<double[]> rings)
    {
        foreach (var r in rings) if (PointInRing(x, y, r)) return true;
        return false;
    }
    /// <summary>段 A→B 与段 P→Q 的交点在 A→B 上的参数 t（两段都在 [0,1] 才算）。</summary>
    private static bool SegSegT(double ax, double ay, double bx, double by, double px, double py, double qx, double qy, out double t)
    {
        t = 0;
        double rx = bx - ax, ry = by - ay, sx = qx - px, sy = qy - py;
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-12) return false;   // 平行/共线
        double tt = ((px - ax) * sy - (py - ay) * sx) / denom;
        double uu = ((px - ax) * ry - (py - ay) * rx) / denom;
        if (tt < 0 || tt > 1 || uu < 0 || uu > 1) return false;
        t = tt; return true;
    }
    private static void ClipPolyline(Polyline3 inp, List<double[]> rings, bool wantInside, double minKeepLen, List<Polyline3> outLines)
    {
        int n = inp.Count;
        if (n < 2) return;
        const double eps = 1e-6;
        var cur = new Polyline3();
        void Flush()
        {
            if (cur.Count >= 2 && PolyLen(cur) >= minKeepLen) outLines.Add(cur);
            cur = new Polyline3();
        }
        void Append(double x, double y, double z)
        {
            int m = cur.Count;
            if (m >= 1 && Math.Abs(cur.Xs[m - 1] - x) < eps && Math.Abs(cur.Ys[m - 1] - y) < eps) return;   // 重合点跳过
            cur.Add(x, y, z);
        }
        var ts = new List<double>();
        for (int i = 1; i < n; i++)
        {
            double ax = inp.Xs[i - 1], ay = inp.Ys[i - 1], az = inp.Zs[i - 1];
            double bx = inp.Xs[i], by = inp.Ys[i], bz = inp.Zs[i];
            ts.Clear(); ts.Add(0.0); ts.Add(1.0);
            foreach (var r in rings)
            {
                int rn = r.Length / 2;
                for (int k = 0, j = rn - 1; k < rn; j = k++)
                    if (SegSegT(ax, ay, bx, by, r[2 * j], r[2 * j + 1], r[2 * k], r[2 * k + 1], out double t) && t > eps && t < 1 - eps)
                        ts.Add(t);
            }
            ts.Sort();
            for (int s = 0; s + 1 < ts.Count; s++)
            {
                double t0 = ts[s], t1 = ts[s + 1];
                if (t1 - t0 < eps) continue;
                double tm = 0.5 * (t0 + t1);
                bool insideMid = PointInAny(ax + (bx - ax) * tm, ay + (by - ay) * tm, rings);
                if (insideMid != wantInside) { Flush(); continue; }
                double p0x = ax + (bx - ax) * t0, p0y = ay + (by - ay) * t0, p0z = az + (bz - az) * t0;
                double p1x = ax + (bx - ax) * t1, p1y = ay + (by - ay) * t1, p1z = az + (bz - az) * t1;
                if (cur.Count == 0) Append(p0x, p0y, p0z);
                else
                {
                    int m = cur.Count;
                    if (Math.Abs(cur.Xs[m - 1] - p0x) > 1e-3 || Math.Abs(cur.Ys[m - 1] - p0y) > 1e-3)
                    { Flush(); Append(p0x, p0y, p0z); }   // run 间断 → 收束再起新段
                }
                Append(p1x, p1y, p1z);
            }
        }
        Flush();
    }

    // ═══════════════════ 栅格算子 ═══════════════════

    private static double D2(int x, int y, int seedId, int w)
    {
        double dx = x - seedId % w, dy = y - seedId / w;
        return dx * dx + dy * dy;
    }

    /// <summary>按行分块并行（同原 parallel_chunks：每块行范围互不重叠，体内只写自己的行）。</summary>
    private static void ParallelRows(int h, CancellationToken ct, Action<int, int> body)
    {
        if (h <= 0) return;
        int nt = Math.Min(Environment.ProcessorCount, Math.Max(1, h / 64));
        if (nt <= 1) { body(0, h); return; }
        int chunk = (h + nt - 1) / nt;
        Parallel.ForEach(Partitioner.Create(0, h, chunk), new ParallelOptions { CancellationToken = ct },
            range => body(range.Item1, range.Item2));
    }

    /// <summary>可分离高斯模糊（输入须无 NaN）。sigma≤0 原样返回副本。</summary>
    internal static float[] GaussianBlur(float[] src, int w, int h, float sigma, CancellationToken ct = default)
    {
        if (sigma <= 0f) return (float[])src.Clone();
        int r = Math.Max(1, (int)Math.Ceiling(sigma * 3f));
        var k = new float[2 * r + 1];
        float s2 = 2f * sigma * sigma, sum = 0f;
        for (int i = -r; i <= r; i++) { float v = (float)Math.Exp(-(i * i) / s2); k[i + r] = v; sum += v; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var tmp = new float[src.Length]; var dst = new float[src.Length];
        ParallelRows(h, ct, (y0, y1) =>   // 横向
        {
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int i = -r; i <= r; i++) { int xx = Math.Clamp(x + i, 0, w - 1); acc += src[y * w + xx] * k[i + r]; }
                    tmp[y * w + x] = acc;
                }
        });
        ParallelRows(h, ct, (y0, y1) =>   // 纵向
        {
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int i = -r; i <= r; i++) { int yy = Math.Clamp(y + i, 0, h - 1); acc += tmp[yy * w + x] * k[i + r]; }
                    dst[y * w + x] = acc;
                }
        });
        return dst;
    }

    /// <summary>
    /// 中心差分梯度 → 坡度(°)；gx/gy 为梯度分量（格边 cs 米）。
    /// wantSlope/wantGrad=false 时对应输出给空数组不分配（整场 DEM 几千万格，一份 float[] 就上百 MB，不要的别申请）。
    /// </summary>
    internal static void ComputeSlope(float[] z, int w, int h, float cs, out float[] slopeDeg, out float[] gx, out float[] gy,
                                      CancellationToken ct = default, bool wantSlope = true, bool wantGrad = true)
    {
        var sl = wantSlope ? new float[z.Length] : Array.Empty<float>();
        var ggx = wantGrad ? new float[z.Length] : Array.Empty<float>();
        var ggy = wantGrad ? new float[z.Length] : Array.Empty<float>();
        float inv2 = 1f / (2f * cs);
        ParallelRows(h, ct, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1);
                    int ym = Math.Max(0, y - 1), yp = Math.Min(h - 1, y + 1);
                    float dx = (z[y * w + xp] - z[y * w + xm]) * inv2;
                    float dy = (z[yp * w + x] - z[ym * w + x]) * inv2;
                    if (wantGrad) { ggx[y * w + x] = dx; ggy[y * w + x] = dy; }
                    if (wantSlope) sl[y * w + x] = (float)(Math.Atan(Math.Sqrt((double)dx * dx + (double)dy * dy)) * (180.0 / Math.PI));
                }
        });
        slopeDeg = sl; gx = ggx; gy = ggy;
    }

    /// <summary>单方向滑窗 min/max（单调队列，O(n)）。</summary>
    private static void Sliding1DMorph(float[] src, float[] dst, int w, int h, int r, bool useMin, bool horizontal, CancellationToken ct)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        ParallelRows(lines, ct, (l0, l1) =>
        {
            var line = new float[len]; var res = new float[len]; var dq = new int[len];
            for (int l = l0; l < l1; l++)
            {
                for (int i = 0; i < len; i++) { int x = horizontal ? i : l, y = horizontal ? l : i; line[i] = src[y * w + x]; }
                int dqh = 0, dqt = 0;
                void Push(int j)
                {
                    while (dqt > dqh && (useMin ? line[dq[dqt - 1]] >= line[j] : line[dq[dqt - 1]] <= line[j])) dqt--;
                    dq[dqt++] = j;
                }
                for (int j = 0; j <= r && j < len; j++) Push(j);   // 预热 [0, r]
                for (int i = 0; i < len; i++)
                {
                    int R = i + r;
                    if (i > 0 && R < len) Push(R);
                    int L = i - r;
                    while (dqt > dqh && dq[dqh] < L) dqh++;
                    res[i] = line[dq[dqh]];
                }
                for (int i = 0; i < len; i++) { int x = horizontal ? i : l, y = horizontal ? l : i; dst[y * w + x] = res[i]; }
            }
        });
    }

    /// <summary>可分离方盒腐蚀/膨胀：先横后纵。O(格数)。</summary>
    internal static float[] BoxMorph(float[] src, int w, int h, int r, bool useMin, CancellationToken ct = default)
    {
        var tmp = new float[src.Length]; var dst = new float[src.Length];
        Sliding1DMorph(src, tmp, w, h, r, useMin, true, ct);
        Sliding1DMorph(tmp, dst, w, h, r, useMin, false, ct);
        return dst;
    }

    /// <summary>
    /// 标量场在 level 处的 marching-squares 等值线。返回格坐标(亚格、平滑)的连通折线。
    /// 共享格边上的交点两边格子算出来完全相同，线段精确接成连续线 —— 没有骨架碎片。
    /// </summary>
    internal static List<List<(float x, float y)>> MarchingSquares(float[] f, int w, int h, float L)
    {
        var node = new Dictionary<long, int>(Math.Max(16, w * 4));
        var pos = new List<(float x, float y)>();
        var adj0 = new List<int>(); var adj1 = new List<int>();
        int MkH(int x, int y)   // (x,y)-(x+1,y) 边上的交点
        {
            long k = ((long)y * w + x) << 1;
            if (node.TryGetValue(k, out int id)) return id;
            float a = f[y * w + x], b = f[y * w + x + 1];
            float t = Math.Abs(b - a) > 1e-9f ? (L - a) / (b - a) : 0.5f;
            id = pos.Count; pos.Add((x + t, y)); adj0.Add(-1); adj1.Add(-1);
            node[k] = id; return id;
        }
        int MkV(int x, int y)   // (x,y)-(x,y+1) 边上的交点
        {
            long k = (((long)y * w + x) << 1) | 1;
            if (node.TryGetValue(k, out int id)) return id;
            float a = f[y * w + x], b = f[(y + 1) * w + x];
            float t = Math.Abs(b - a) > 1e-9f ? (L - a) / (b - a) : 0.5f;
            id = pos.Count; pos.Add((x, y + t)); adj0.Add(-1); adj1.Add(-1);
            node[k] = id; return id;
        }
        void Link(int a, int b)
        {
            if (adj0[a] < 0) adj0[a] = b; else if (adj1[a] < 0) adj1[a] = b;
            if (adj0[b] < 0) adj0[b] = a; else if (adj1[b] < 0) adj1[b] = a;
        }
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w - 1; x++)
            {
                float tl = f[y * w + x], tr = f[y * w + x + 1], br = f[(y + 1) * w + x + 1], bl = f[(y + 1) * w + x];
                int c = (tl > L ? 1 : 0) | (tr > L ? 2 : 0) | (br > L ? 4 : 0) | (bl > L ? 8 : 0);
                if (c == 0 || c == 15) continue;
                // 边：T=MkH(x,y) R=MkV(x+1,y) B=MkH(x,y+1) Lf=MkV(x,y)
                switch (c)
                {
                    case 1: case 14: Link(MkV(x, y), MkH(x, y)); break;               // L-T
                    case 2: case 13: Link(MkH(x, y), MkV(x + 1, y)); break;           // T-R
                    case 3: case 12: Link(MkV(x, y), MkV(x + 1, y)); break;           // L-R
                    case 4: case 11: Link(MkV(x + 1, y), MkH(x, y + 1)); break;       // R-B
                    case 6: case 9: Link(MkH(x, y), MkH(x, y + 1)); break;            // T-B
                    case 7: case 8: Link(MkV(x, y), MkH(x, y + 1)); break;            // L-B
                    case 5: Link(MkV(x, y), MkH(x, y)); Link(MkV(x + 1, y), MkH(x, y + 1)); break;      // 鞍点：L-T, R-B
                    case 10: Link(MkH(x, y), MkV(x + 1, y)); Link(MkH(x, y + 1), MkV(x, y)); break;     // 鞍点：T-R, B-L
                }
            }
        // 走链：先从度为 1 的端点起(开口线)，再走剩下的闭环
        var outLines = new List<List<(float x, float y)>>();
        var used = new byte[pos.Count];
        void Walk(int s)
        {
            var poly = new List<(float x, float y)>();
            int cur = s, prev = -1;
            while (cur >= 0 && used[cur] == 0)
            {
                used[cur] = 1; poly.Add(pos[cur]);
                int n0 = adj0[cur], n1 = adj1[cur];
                int nxt = (n0 != prev && n0 >= 0 && used[n0] == 0) ? n0
                        : (n1 != prev && n1 >= 0 && used[n1] == 0) ? n1 : -1;
                prev = cur; cur = nxt;
            }
            if (poly.Count >= 2) outLines.Add(poly);
        }
        for (int s = 0; s < pos.Count; s++) if (used[s] == 0 && (adj0[s] < 0 || adj1[s] < 0)) Walk(s);   // 开口链端点
        for (int s = 0; s < pos.Count; s++) if (used[s] == 0 && adj0[s] >= 0) Walk(s);                     // 闭环
        return outLines;
    }

    // ═══════════════════ 折线几何 ═══════════════════

    /// <summary>XY 平面长度。</summary>
    public static double PolyLen(Polyline3 p)
    {
        double len = 0;
        for (int i = 1; i < p.Count; i++)
        { double dx = p.Xs[i] - p.Xs[i - 1], dy = p.Ys[i] - p.Ys[i - 1]; len += Math.Sqrt(dx * dx + dy * dy); }
        return len;
    }

    /// <summary>XY 上的 Douglas-Peucker（Z 随保留顶点走）。</summary>
    internal static Polyline3 DpSimplify(Polyline3 p, double tol)
    {
        int n = p.Count;
        var o = new Polyline3();
        if (n < 3) { o.Xs.AddRange(p.Xs); o.Ys.AddRange(p.Ys); o.Zs.AddRange(p.Zs); return o; }
        var keep = new byte[n]; keep[0] = keep[n - 1] = 1;
        var st = new Stack<(int a, int b)>(); st.Push((0, n - 1));
        while (st.Count > 0)
        {
            var (a, b) = st.Pop();
            double ax = p.Xs[a], ay = p.Ys[a], bx = p.Xs[b], by = p.Ys[b];
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            double dmax = -1; int imax = -1;
            for (int i = a + 1; i < b; i++)
            {
                double t = len2 > 0 ? ((p.Xs[i] - ax) * dx + (p.Ys[i] - ay) * dy) / len2 : 0;
                t = Math.Clamp(t, 0.0, 1.0);
                double px = ax + t * dx, py = ay + t * dy;
                double d = Math.Sqrt((p.Xs[i] - px) * (p.Xs[i] - px) + (p.Ys[i] - py) * (p.Ys[i] - py));
                if (d > dmax) { dmax = d; imax = i; }
            }
            if (dmax > tol && imax > 0) { keep[imax] = 1; st.Push((a, imax)); st.Push((imax, b)); }
        }
        for (int i = 0; i < n; i++) if (keep[i] != 0) o.Add(p.Xs[i], p.Ys[i], p.Zs[i]);
        return o;
    }

    /// <summary>Chaikin 切角（开口线，端点固定）。</summary>
    internal static void Chaikin(Polyline3 p, int iters)
    {
        for (int it = 0; it < iters; it++)
        {
            if (p.Count < 3) break;
            var q = new Polyline3();
            q.Add(p.Xs[0], p.Ys[0], p.Zs[0]);
            for (int i = 0; i + 1 < p.Count; i++)
            {
                q.Add(0.75 * p.Xs[i] + 0.25 * p.Xs[i + 1], 0.75 * p.Ys[i] + 0.25 * p.Ys[i + 1], 0.75 * p.Zs[i] + 0.25 * p.Zs[i + 1]);
                q.Add(0.25 * p.Xs[i] + 0.75 * p.Xs[i + 1], 0.25 * p.Ys[i] + 0.75 * p.Ys[i + 1], 0.25 * p.Zs[i] + 0.75 * p.Zs[i + 1]);
            }
            q.Add(p.Xs[^1], p.Ys[^1], p.Zs[^1]);
            p.Xs = q.Xs; p.Ys = q.Ys; p.Zs = q.Zs;
        }
    }

    /// <summary>去端钩：端头一小段(≤20m)与下一段转折 &gt;~60° 是描迹/合并在端头卷出来的钩，去掉。</summary>
    private static void TrimHooks(Polyline3 q)
    {
        bool Sharp(int a, int b, int c)
        {
            double ax = q.Xs[b] - q.Xs[a], ay = q.Ys[b] - q.Ys[a];
            double bx = q.Xs[c] - q.Xs[b], by = q.Ys[c] - q.Ys[b];
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6) return true;
            return (ax * bx + ay * by) / (la * lb) < 0.5;   // 转折 > ~60°
        }
        const double hook = 20.0;
        while (q.Count >= 3 && Math.Sqrt(Math.Pow(q.Xs[1] - q.Xs[0], 2) + Math.Pow(q.Ys[1] - q.Ys[0], 2)) <= hook && Sharp(0, 1, 2))
        { q.Xs.RemoveAt(0); q.Ys.RemoveAt(0); q.Zs.RemoveAt(0); }
        while (q.Count >= 3)
        {
            int n = q.Count;
            if (Math.Sqrt(Math.Pow(q.Xs[n - 1] - q.Xs[n - 2], 2) + Math.Pow(q.Ys[n - 1] - q.Ys[n - 2], 2)) <= hook && Sharp(n - 3, n - 2, n - 1))
            { q.Xs.RemoveAt(n - 1); q.Ys.RemoveAt(n - 1); q.Zs.RemoveAt(n - 1); }
            else break;
        }
    }

    /// <summary>终次清理：DP → 去钩 → Chaikin → 去碎(短于 clutterMin) → 去短卷线(首尾距 &lt; 0.25L 且 L &lt; 120；长的近闭合线是绕坑的真台阶，留)。</summary>
    private static void Cleanup(List<Polyline3> lines, double tol, double clutterMin, int smoothIters)
    {
        var kept = new List<Polyline3>(lines.Count);
        foreach (var p in lines)
        {
            var s = DpSimplify(p, tol);
            TrimHooks(s);
            Chaikin(s, smoothIters);
            double L = PolyLen(s);
            if (L < clutterMin) continue;
            int m = s.Count;
            double D = m > 0 ? Math.Sqrt(Math.Pow(s.Xs[m - 1] - s.Xs[0], 2) + Math.Pow(s.Ys[m - 1] - s.Ys[0], 2)) : 0.0;
            if (D < 0.25 * L && L < 120.0) continue;
            kept.Add(s);
        }
        lines.Clear(); lines.AddRange(kept);
    }

    private static void ClipToCover(List<Polyline3> lines, double xmn, double ymn, double cs, Func<double, double, bool> covered, double minKeep)
    {
        var kept = new List<Polyline3>(lines.Count);
        foreach (var p in lines)
        {
            var run = new Polyline3();
            for (int i = 0; i < p.Count; i++)
            {
                double cx = (p.Xs[i] - xmn) / cs - 0.5, cy = (p.Ys[i] - ymn) / cs - 0.5;
                if (covered(cx, cy)) run.Add(p.Xs[i], p.Ys[i], p.Zs[i]);
                else { if (run.Count >= 2 && PolyLen(run) >= minKeep) kept.Add(run); run = new Polyline3(); }
            }
            if (run.Count >= 2 && PolyLen(run) >= minKeep) kept.Add(run);
        }
        lines.Clear(); lines.AddRange(kept);
    }

    // ═══════════════════ 原始点云精修 ═══════════════════

    /// <summary>点的均匀格网索引（链表桶），查半径内点。</summary>
    private sealed class PointGrid
    {
        public readonly double Xmin, Ymin, Cell;
        public readonly int W, H;
        public readonly int[] Head, Next;
        public PointGrid(IReadOnlyList<(double x, double y, double z)> pts, double xmn, double ymn, double xmx, double ymx, double cell)
        {
            Xmin = xmn; Ymin = ymn; Cell = Math.Max(0.5, cell);
            W = Math.Max(1, (int)((xmx - xmn) / Cell) + 1);
            H = Math.Max(1, (int)((ymx - ymn) / Cell) + 1);
            Head = new int[W * H]; Array.Fill(Head, -1);
            Next = new int[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                int gx = Math.Clamp((int)((p.x - Xmin) / Cell), 0, W - 1);
                int gy = Math.Clamp((int)((p.y - Ymin) / Cell), 0, H - 1);
                int c = gy * W + gx;
                Next[i] = Head[c]; Head[c] = i;
            }
        }
        /// <summary>遍历 (x,y) 半径 radius 的包围格内所有点（不做圆过滤，同原版：由调用方按断面窗过滤）。</summary>
        public void Query(double x, double y, double radius, Action<int> fn)
        {
            int gx0 = Math.Clamp((int)((x - radius - Xmin) / Cell), 0, W - 1);
            int gy0 = Math.Clamp((int)((y - radius - Ymin) / Cell), 0, H - 1);
            int gx1 = Math.Clamp((int)((x + radius - Xmin) / Cell), 0, W - 1);
            int gy1 = Math.Clamp((int)((y + radius - Ymin) / Cell), 0, H - 1);
            for (int gy = gy0; gy <= gy1; gy++)
                for (int gx = gx0; gx <= gx1; gx++)
                    for (int p = Head[gy * W + gx]; p >= 0; p = Next[p]) fn(p);
        }
    }

    private static bool LineTangent(Polyline3 p, int i, out double tx, out double ty)
    {
        tx = ty = 0;
        int n = p.Count;
        if (n < 2) return false;
        int a = i == 0 ? 0 : i - 1, b = i + 1 < n ? i + 1 : n - 1;
        if (a == b) return false;
        double dx = p.Xs[b] - p.Xs[a], dy = p.Ys[b] - p.Ys[a];
        double L = Math.Sqrt(dx * dx + dy * dy);
        if (L < 1e-6) return false;
        tx = dx / L; ty = dy / L;
        return true;
    }

    /// <summary>坡顶顶点精修：断面窗内最高点带(z ≥ max−0.25) 的加权质心(权 1/(1+|u|))，位移不超 max_shift。</summary>
    private static bool RefineCrestVertex(Polyline3 line, int i, IReadOnlyList<(double x, double y, double z)> pts, PointGrid grid, Options opt)
    {
        if (!LineTangent(line, i, out double tx, out double ty)) return false;
        double nx = -ty, ny = tx;
        double ox = line.Xs[i], oy = line.Ys[i];
        double halfLen = Math.Max(0.5, opt.RefineProfileLen);
        double halfWidth = Math.Max(opt.RefineMaxShift, opt.RefineProfileWidth);
        double maxShift = Math.Max(0.0, opt.RefineMaxShift);
        double radius = Math.Sqrt(halfLen * halfLen + halfWidth * halfWidth) + maxShift;

        int profilePts = 0, candPts = 0;
        double bestZ = double.MinValue;
        grid.Query(ox, oy, radius, pi =>
        {
            var q = pts[pi];
            double dx = q.x - ox, dy = q.y - oy;
            double u = dx * tx + dy * ty, v = dx * nx + dy * ny;
            if (Math.Abs(u) > halfLen || Math.Abs(v) > halfWidth) return;
            profilePts++;
            if (Math.Abs(v) <= maxShift) { candPts++; if (q.z > bestZ) bestZ = q.z; }
        });
        if (profilePts < opt.RefineMinPoints || candPts < 1 || bestZ == double.MinValue) return false;

        double sx = 0, sy = 0, sz = 0, sw = 0;
        const double zBand = 0.25;
        grid.Query(ox, oy, radius, pi =>
        {
            var q = pts[pi];
            double dx = q.x - ox, dy = q.y - oy;
            double u = dx * tx + dy * ty, v = dx * nx + dy * ny;
            if (Math.Abs(u) > halfLen || Math.Abs(v) > maxShift) return;
            if (q.z < bestZ - zBand) return;
            double wgt = 1.0 / (1.0 + Math.Abs(u));
            sx += q.x * wgt; sy += q.y * wgt; sz += q.z * wgt; sw += wgt;
        });
        if (sw <= 0) return false;
        double rx = sx / sw, ry = sy / sw;
        if (Math.Sqrt((rx - ox) * (rx - ox) + (ry - oy) * (ry - oy)) > maxShift) return false;
        line.Xs[i] = rx; line.Ys[i] = ry; line.Zs[i] = sz / sw;
        return true;
    }

    /// <summary>坡底顶点精修：横向分箱断面，取相邻箱高程梯度最大处(≥0.25)，位移不超 max_shift。</summary>
    private static bool RefineToeVertex(Polyline3 line, int i, IReadOnlyList<(double x, double y, double z)> pts, PointGrid grid, Options opt)
    {
        if (!LineTangent(line, i, out double tx, out double ty)) return false;
        double nx = -ty, ny = tx;
        double ox = line.Xs[i], oy = line.Ys[i];
        double halfLen = Math.Max(0.5, opt.RefineProfileLen);
        double halfWidth = Math.Max(opt.RefineMaxShift, opt.RefineProfileWidth);
        double maxShift = Math.Max(0.0, opt.RefineMaxShift);
        double bin = Math.Max(0.1, opt.RefineBinSize);
        int bins = Math.Max(3, (int)Math.Ceiling(2.0 * halfWidth / bin));
        double radius = Math.Sqrt(halfLen * halfLen + halfWidth * halfWidth) + maxShift;
        var zsum = new double[bins]; var zcnt = new int[bins];
        int profilePts = 0;
        grid.Query(ox, oy, radius, pi =>
        {
            var q = pts[pi];
            double dx = q.x - ox, dy = q.y - oy;
            double u = dx * tx + dy * ty, v = dx * nx + dy * ny;
            if (Math.Abs(u) > halfLen || Math.Abs(v) > halfWidth) return;
            int b = (int)((v + halfWidth) / bin);
            if (b < 0 || b >= bins) return;
            zsum[b] += q.z; zcnt[b]++; profilePts++;
        });
        if (profilePts < opt.RefineMinPoints) return false;

        int bestB = -1; double bestG = 0.0;
        for (int b = 0; b + 1 < bins; b++)
        {
            if (zcnt[b] == 0 || zcnt[b + 1] == 0) continue;
            double edgeV = -halfWidth + (b + 1) * bin;
            if (Math.Abs(edgeV) > maxShift) continue;
            double z0 = zsum[b] / zcnt[b], z1 = zsum[b + 1] / zcnt[b + 1];
            double g = Math.Abs(z1 - z0) / bin;
            if (g > bestG) { bestG = g; bestB = b; }
        }
        if (bestB < 0 || bestG < 0.25) return false;
        double vv = -halfWidth + (bestB + 1) * bin;
        double rx = ox + nx * vv, ry = oy + ny * vv;
        if (Math.Sqrt((rx - ox) * (rx - ox) + (ry - oy) * (ry - oy)) > maxShift) return false;
        double za = zsum[bestB] / zcnt[bestB], zb = zsum[bestB + 1] / zcnt[bestB + 1];
        line.Xs[i] = rx; line.Ys[i] = ry; line.Zs[i] = (za + zb) * 0.5;
        return true;
    }

    /// <summary>逐线逐顶点精修（线与线独立 → 按线并行；线内顺序同原版，结果一致）。返回移动的顶点数。</summary>
    private static long RefineLines(List<Polyline3> lines, bool crest, IReadOnlyList<(double x, double y, double z)> pts, PointGrid grid, Options opt, CancellationToken ct)
    {
        if (!opt.RefineEnabled || opt.RefineMaxShift <= 0) return 0;
        long moved = 0;
        Parallel.ForEach(lines, new ParallelOptions { CancellationToken = ct }, line =>
        {
            long m = 0;
            for (int i = 0; i < line.Count; i++)
            {
                bool ok = crest ? RefineCrestVertex(line, i, pts, grid, opt) : RefineToeVertex(line, i, pts, grid, opt);
                if (ok) m++;
            }
            Interlocked.Add(ref moved, m);
        });
        return moved;
    }
}
