using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>骨架法道路中心线提取参数。忠实移植原 PointCloudLib.RoadCenterline.RoadSkeletonOptions。</summary>
public sealed class RoadSkeletonOptions
{
    /// <summary>DEM 栅格尺寸 (m)。越小越细越慢。</summary>
    public double CellSize = 3.0;
    /// <summary>DEM 高斯平滑 (cells)：让台阶面成为有坡度的过渡带而非阶跃。</summary>
    public double DemSmoothSigma = 1.2;
    /// <summary>坡度场额外平滑 (cells)：去栅格噪声，掩膜边界更干净。</summary>
    public double SlopeSmoothSigma = 1.5;
    /// <summary>可行驶坡度上限 (°)：≤此为平盘/坡道(可行驶)，&gt;此为台阶面(排除)。</summary>
    public double MaxDriveSlopeDeg = 14.0;
    /// <summary>用台阶线当"挡墙"：把台阶线栅格化后从可行驶面挖掉 → 路被关在平盘内、只能从坡道口
    /// (台阶线缺口)换层，杜绝"斜穿小台阶"。几何上最精确。</summary>
    public bool UseBenchBarrier = true;
    /// <summary>填充距离上限 (m)：离最近台阶线超过此距离的格子不算"面内"(界定坑域、剔坑外大平地)。</summary>
    public double MaxFillDist = 70.0;
    /// <summary>剔除小于此格数的可行驶连通块（噪声/孤立小平地）。</summary>
    public int MinComponentCells = 60;
    /// <summary>骨架毛刺修剪遍数（去交叉口的短发）。</summary>
    public int SpurPrunePasses = 8;
    /// <summary>Douglas-Peucker 简化容差 (m)。</summary>
    public double SimplifyTol = 2.5;
    /// <summary>最短中心线长 (m)：短于此丢弃。</summary>
    public double MinLineLen = 30.0;
    /// <summary>安全上限：DEM 超过此格数则拒绝（提示调大 CellSize）。</summary>
    public long MaxCells = 60_000_000;

    // ── 干线路由（只要主运输路/出入沟，不要每个平盘）──
    /// <summary>true=只输出"坑口→各深区"的干线网（路由）；false=输出全部可行驶骨架（每平盘一条）。</summary>
    public bool TrunkOnly = true;
    /// <summary>true=扩展路由：从入口(每坑最高点)长生成树，标记<b>全部可达通路</b>（采场往下/排土场往上，
    /// 任意路段抽枝杈，全网覆盖）；false=只标记到选定深点的几条固定线路。</summary>
    public bool FullNetwork = true;
    /// <summary>每个坑（连通分量）路由的目的地个数。FullNetwork 时=FPS 撒点数，越多覆盖越全越密。</summary>
    public int MaxDestinations = 400;
    /// <summary>相邻深区目的地的最小间距 (m)：太近视为同一处，只取一个。越小支线越密、覆盖越全。</summary>
    public double MinDestSepMeters = 40.0;
    /// <summary>参与路由的连通分量最小格数：大于此的"坑"才各自路由（覆盖多坑/多作业区）。</summary>
    public int MinRouteComponentCells = 300;
    /// <summary>最小路面宽度 W_min (m)：路由只走"走廊宽度≥此"的骨架；窄于此=不满足道路宽度，自动排除。</summary>
    public double MinRoadWidth = 12.0;
    /// <summary>缺口桥接距离 (m)：把骨架上相距≤此、且【同高程】(Δz小)的悬空端点连起来——
    /// 接回被挡墙/路宽闸门切断的坡道/相邻片，使本该连通的路网真正连通。只桥同高程，绝不跨台阶面。0=不桥。</summary>
    public double BridgeGapMeters = 30.0;
    /// <summary>桥接同高程容差 (m)：两端点高差小于此才桥（路在缺口处连续）。远小于台阶高，杜绝跨台阶面误桥。</summary>
    public double BridgeZTol = 4.0;
}

public sealed class RoadSkeletonResult
{
    public List<double[]> Centerlines = new();
    public int DemW, DemH;
    public double CellSize, OriginX, OriginY;
    public long MaskCells, SkeletonCells;
    public string Summary = string.Empty;
    public bool Ok;
    public string Error = string.Empty;
}

/// <summary>
/// 骨架法（可行驶走廊中轴线）道路中心线提取 —— 忠实移植原 PointCloudLib.RoadCenterline.RoadSkeletonExtractor。
///
/// 管线（纯 C#，逻辑照搬 Kernel/LasLib/bench_lines.cpp 已验证那套，不动坡线提取算法）：
///   台阶线段栅格化成稀疏 DEM → JFA 欧氏最近填充成连续面 → 高斯平滑 → 坡度场
///   → 可行驶掩膜(坡度≤阈值 且 离线≤MaxFillDist) → 剔小连通块 → Zhang-Suen 细化成 1px 骨架
///   → 修毛刺 → 追踪成折线(交叉口天然分叉=连通网) → 赋 Z + DP 简化 + 剔短。
///
/// 相比"坡线配对取中线"(<see cref="RoadCenterlineExtractor"/>)：一条走廊出一条中轴(无重叠"花圈")，
/// 坡道口天然 T 形连通(无需焊接)。纯逻辑、可单测(确定性子算法 ZhangSuen/DpSimplify/ComputeSlopeDeg 等标 internal 供已知值回归)。
/// </summary>
public static class RoadSkeletonExtractor
{
    private const float NaNz = float.MinValue;

    /// <summary>无 TIN：用台阶线段栅格化 + JFA 块状填充建面（粗）。</summary>
    public static RoadSkeletonResult Extract(IReadOnlyList<double[]> benchLines, RoadSkeletonOptions opt)
        => Extract(benchLines, null, null, opt);

    /// <summary>有 TIN：栅格化三角网 mesh 得插值面（坡度干净、低路宽不塌），强烈推荐。
    /// meshVerts=[x,y,z,...]（world），meshTris=[i0,i1,i2,...]；任一为空则退回块状填充。</summary>
    public static RoadSkeletonResult Extract(IReadOnlyList<double[]> benchLines,
        double[]? meshVerts, int[]? meshTris, RoadSkeletonOptions opt)
    {
        var res = new RoadSkeletonResult();
        if (benchLines == null || benchLines.Count == 0) { res.Error = "无台阶线输入。"; return res; }

        float cs = (float)(opt.CellSize > 0.1 ? opt.CellSize : 3.0);

        // ── 包围盒 ──
        double xmn = double.MaxValue, ymn = double.MaxValue, xmx = double.MinValue, ymx = double.MinValue;
        foreach (var ln in benchLines)
            for (int i = 0; i + 2 < ln.Length; i += 3)
            {
                if (ln[i] < xmn) xmn = ln[i]; if (ln[i] > xmx) xmx = ln[i];
                if (ln[i+1] < ymn) ymn = ln[i+1]; if (ln[i+1] > ymx) ymx = ln[i+1];
            }
        if (xmx <= xmn || ymx <= ymn) { res.Error = "台阶线退化（包围盒为空）。"; return res; }

        int w = Math.Max(1, (int)((xmx - xmn) / cs) + 1);
        int h = Math.Max(1, (int)((ymx - ymn) / cs) + 1);
        if ((long)w * h > opt.MaxCells) { res.Error = $"DEM 过大（{(long)w*h} 格），请调大栅格尺寸。"; return res; }
        res.DemW = w; res.DemH = h; res.CellSize = cs; res.OriginX = xmn; res.OriginY = ymn;

        // ── 建面 DEM + valid（坑域）：有 TIN 走三角网插值，无则台阶线段+JFA 块状填充 ──
        long N = (long)w * h;
        var dem = new float[N];
        var srcX = new int[N];
        var srcY = new int[N];
        var valid = new bool[N];
        for (long id = 0; id < N; id++) { dem[id] = NaNz; srcX[id] = -1; srcY[id] = -1; }
        bool useTin = meshVerts != null && meshTris != null && meshVerts.Length >= 9 && meshTris.Length >= 3;

        if (useTin)
        {
            // 三角网栅格化：逐三角扫描，格中心在三角内 → 填重心插值 Z（= TIN 连续面）
            RasterizeMesh(meshVerts!, meshTris!, xmn, ymn, cs, w, h, dem, valid, srcX, srcY);
        }
        else
        {
            var sum = new double[N]; var cnt = new int[N];
            foreach (var ln in benchLines)
            {
                int n = ln.Length / 3;
                for (int s = 0; s + 1 < n; s++)
                {
                    double ax = ln[3*s], ay = ln[3*s+1], az = ln[3*s+2];
                    double bx = ln[3*s+3], by = ln[3*s+4], bz = ln[3*s+5];
                    double segLen = Math.Sqrt((bx-ax)*(bx-ax) + (by-ay)*(by-ay));
                    int steps = Math.Max(1, (int)(segLen / (cs * 0.5)));
                    for (int t = 0; t <= steps; t++)
                    {
                        double f = (double)t / steps;
                        double px = ax + (bx-ax)*f, py = ay + (by-ay)*f, pz = az + (bz-az)*f;
                        int gx = Clamp((int)((px - xmn) / cs), 0, w - 1), gy = Clamp((int)((py - ymn) / cs), 0, h - 1);
                        long id = (long)gy * w + gx; sum[id] += pz; cnt[id]++;
                    }
                }
            }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    long id = (long)y * w + x;
                    if (cnt[id] > 0) { dem[id] = (float)(sum[id] / cnt[id]); srcX[id] = x; srcY[id] = y; }
                }
        }

        bool anySrc = false; for (long id = 0; id < N; id++) if (srcX[id] >= 0) { anySrc = true; break; }
        if (!anySrc) { res.Error = useTin ? "三角网未覆盖任何格。" : "无有效 DEM 格。"; return res; }

        // 公共：JFA 最近填充 dem（让坡度处处有限）；无 TIN 时顺带按距离界定坑域
        JumpFloodFill(srcX, srcY, w, h);
        float maxFill = (float)opt.MaxFillDist;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                long id = (long)y * w + x;
                if (srcX[id] < 0) continue;
                long sid = (long)srcY[id] * w + srcX[id];
                if (dem[id] == NaNz) dem[id] = dem[sid];
                if (!useTin)
                {
                    double dx = x - srcX[id], dy = y - srcY[id];
                    if (Math.Sqrt(dx*dx + dy*dy) * cs <= maxFill) valid[id] = true;
                }
            }
        // useTin 时 valid 已由 RasterizeMesh 设好（= TIN 覆盖域）。

        // ── 平滑 → 坡度场 → 再平滑 ──
        var demS = GaussianBlur(dem, w, h, (float)opt.DemSmoothSigma);
        var slope = ComputeSlopeDeg(demS, w, h, cs);
        var slopeS = GaussianBlur(slope, w, h, (float)opt.SlopeSmoothSigma);

        // ── 可行驶掩膜：坡度≤阈值 且 在坑域内 ──
        float maxSlope = (float)opt.MaxDriveSlopeDeg;
        var mask = new bool[N];
        long maskCells = 0;
        for (long id = 0; id < N; id++)
            if (slopeS[id] <= maxSlope && valid[id]) { mask[id] = true; maskCells++; }
        res.MaskCells = maskCells;
        if (maskCells == 0) { res.Error = "可行驶掩膜为空（坡度阈值过小？）。"; return res; }

        // ── 台阶线挡墙：从可行驶面挖掉台阶线(+膨胀1格)，封死"斜穿小台阶"，只留坡道口换层 ──
        if (opt.UseBenchBarrier)
        {
            var barrier = RasterizeBenchBarrier(benchLines, xmn, ymn, cs, w, h);
            for (long id = 0; id < N; id++) if (barrier[id]) mask[id] = false;
        }

        // ── 剔小连通块 ──
        RemoveSmallComponents(mask, w, h, opt.MinComponentCells);

        // ── 距离变换：每格到可行驶区边界的距离(格)=走廊半宽，用于"路宽闸门" ──
        var dtCells = DistanceTransformCells(mask, w, h);

        // ── Zhang-Suen 细化 → 修毛刺 ──
        var skel = (bool[])mask.Clone();
        ZhangSuen(skel, w, h);
        for (int p = 0; p < opt.SpurPrunePasses; p++) PruneSpurs(skel, w, h);

        // ── 缺口桥接：把相距≤BridgeGap、同高程(Δz小)的悬空端点连起来，接回被挡墙/路宽闸门
        //    切断的坡道/相邻片，使本该连通的路网真正连通（只桥同高程，绝不跨台阶面）──
        if (opt.BridgeGapMeters > 0)
            BridgeSkeletonGaps(skel, demS, dtCells, w, h, cs, opt);

        long skelCells = 0; for (long id = 0; id < (long)w*h; id++) if (skel[id]) skelCells++;
        res.SkeletonCells = skelCells;

        // ── 干线路由：只取"坑口(最高)→各深区(最低)"的可行驶最短路并集（甩掉每平盘同心线）──
        var traceMask = skel;
        if (opt.TrunkOnly)
        {
            traceMask = RouteHaulNetwork(skel, demS, dtCells, w, h, cs, opt);
            long haulCells = 0; for (long id = 0; id < (long)w*h; id++) if (traceMask[id]) haulCells++;
            if (haulCells < 2) { res.Error = "干线路由为空（可行驶网未连通？可改 TrunkOnly=false 看全网）。"; return res; }
        }

        // ── 追踪成像素折线（交叉口/端点处分段 → 连通网） ──
        var paths = TraceSkeleton(traceMask, w, h);

        // ── 像素折线 → 世界折线（Z 取 demS）+ DP 简化 + 剔短 ──
        double tol = Math.Max(0.5, opt.SimplifyTol);
        foreach (var path in paths)
        {
            if (path.Count < 2) continue;
            var xs = new List<double>(path.Count);
            var ys = new List<double>(path.Count);
            var zs = new List<double>(path.Count);
            foreach (int cell in path)
            {
                int cx = cell % w, cy = cell / w;
                xs.Add(xmn + (cx + 0.5) * cs);
                ys.Add(ymn + (cy + 0.5) * cs);
                zs.Add(demS[(long)cy * w + cx]);
            }
            double len = 0;
            for (int i = 1; i < xs.Count; i++) len += Math.Sqrt((xs[i]-xs[i-1])*(xs[i]-xs[i-1]) + (ys[i]-ys[i-1])*(ys[i]-ys[i-1]));
            // 只剔"短死端杈"（端点度≤1=悬空），保留所有通过边——否则剔短边会把连通网剪出缺口、看着间断。
            int e0 = path[0], e1 = path[path.Count - 1];
            bool deadEnd = PixDegree(traceMask, w, h, e0 % w, e0 / w) <= 1
                        || PixDegree(traceMask, w, h, e1 % w, e1 / w) <= 1;
            if (deadEnd && len < opt.MinLineLen) continue;
            var simp = DpSimplify(xs, ys, zs, tol);
            if (simp.Length >= 6) res.Centerlines.Add(simp);
        }

        double total = 0; foreach (var cl in res.Centerlines) total += PolyLen(cl);
        res.Ok = true;
        res.Summary = $"道路中心线(骨架法)：{res.Centerlines.Count} 条，合计 {total:F0} m" +
                      $"（DEM {w}×{h}@{cs:F1}m，可行驶 {maskCells} 格，骨架 {skelCells} 格）。";
        return res;
    }

    // ── JFA 欧氏最近源填充（srcX/srcY 原地：每格→最近源格坐标） ──
    internal static void JumpFloodFill(int[] sx, int[] sy, int w, int h)
    {
        long N = (long)w * h;
        var nx = new int[N]; var ny = new int[N];
        int step = 1; while (step < Math.Max(w, h)) step <<= 1; step >>= 1;
        for (; step >= 1; step >>= 1)
        {
            Array.Copy(sx, nx, N); Array.Copy(sy, ny, N);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    long id = (long)y * w + x;
                    int bsx = sx[id], bsy = sy[id];
                    double best = (bsx < 0) ? 1e30 : (double)(x-bsx)*(x-bsx) + (double)(y-bsy)*(y-bsy);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int qx = x + dx*step, qy = y + dy*step;
                            if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                            long qid = (long)qy * w + qx;
                            if (sx[qid] < 0) continue;
                            double d = (double)(x-sx[qid])*(x-sx[qid]) + (double)(y-sy[qid])*(y-sy[qid]);
                            if (d < best) { best = d; bsx = sx[qid]; bsy = sy[qid]; }
                        }
                    nx[id] = bsx; ny[id] = bsy;
                }
            Array.Copy(nx, sx, N); Array.Copy(ny, sy, N);
        }
    }

    // 台阶线栅格化成"挡墙"：沿每段按 ~cs/2 步距打点 + 8 邻膨胀 1 格（封 1px 缝，防骨架 8 连通绕过）。
    private static bool[] RasterizeBenchBarrier(IReadOnlyList<double[]> lines, double xmn, double ymn, float cs, int w, int h)
    {
        int N = w * h;
        var b0 = new bool[N];
        foreach (var ln in lines)
        {
            int n = ln.Length / 3;
            for (int s = 0; s + 1 < n; s++)
            {
                double ax = ln[3*s], ay = ln[3*s+1], bx = ln[3*s+3], by = ln[3*s+4];
                double segLen = Math.Sqrt((bx-ax)*(bx-ax) + (by-ay)*(by-ay));
                int steps = Math.Max(1, (int)(segLen / (cs * 0.5)));
                for (int t = 0; t <= steps; t++)
                {
                    double f = (double)t / steps;
                    int gx = Clamp((int)((ax + (bx-ax)*f - xmn) / cs), 0, w - 1);
                    int gy = Clamp((int)((ay + (by-ay)*f - ymn) / cs), 0, h - 1);
                    b0[gy * w + gx] = true;
                }
            }
        }
        // 膨胀 1 格
        var b = new bool[N];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!b0[y * w + x]) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h) b[ny * w + nx] = true;
                    }
            }
        return b;
    }

    // 三角网栅格化：逐三角扫描转换，格中心落在三角内则填重心插值 Z；valid 标 TIN 覆盖域。
    private static void RasterizeMesh(double[] verts, int[] tris, double xmn, double ymn, float cs,
        int w, int h, float[] dem, bool[] valid, int[] srcX, int[] srcY)
    {
        int nt = tris.Length / 3, nv = verts.Length / 3;
        for (int t = 0; t < nt; t++)
        {
            int i0 = tris[3*t], i1 = tris[3*t+1], i2 = tris[3*t+2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= nv || i1 >= nv || i2 >= nv) continue;
            double ax = verts[3*i0], ay = verts[3*i0+1], az = verts[3*i0+2];
            double bx = verts[3*i1], by = verts[3*i1+1], bz = verts[3*i1+2];
            double cx = verts[3*i2], cy = verts[3*i2+1], cz = verts[3*i2+2];
            double minx = Math.Min(ax, Math.Min(bx, cx)), maxx = Math.Max(ax, Math.Max(bx, cx));
            double miny = Math.Min(ay, Math.Min(by, cy)), maxy = Math.Max(ay, Math.Max(by, cy));
            int gx0 = Clamp((int)((minx - xmn) / cs), 0, w - 1), gx1 = Clamp((int)((maxx - xmn) / cs), 0, w - 1);
            int gy0 = Clamp((int)((miny - ymn) / cs), 0, h - 1), gy1 = Clamp((int)((maxy - ymn) / cs), 0, h - 1);
            double d00 = bx - ax, d01 = cx - ax, d10 = by - ay, d11 = cy - ay;
            double denom = d00 * d11 - d01 * d10;
            if (Math.Abs(denom) < 1e-12) continue;
            for (int gy = gy0; gy <= gy1; gy++)
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    double px = xmn + (gx + 0.5) * cs - ax, py = ymn + (gy + 0.5) * cs - ay;
                    double bU = (d11 * px - d01 * py) / denom;     // 权重(B-A)
                    double bV = (d00 * py - d10 * px) / denom;     // 权重(C-A)
                    if (bU < -1e-6 || bV < -1e-6 || bU + bV > 1 + 1e-6) continue;
                    double z = az + bU * (bz - az) + bV * (cz - az);
                    long id = (long)gy * w + gx;
                    dem[id] = (float)z; valid[id] = true; srcX[id] = gx; srcY[id] = gy;
                }
        }
    }

    private static float[] GaussianBlur(float[] src, int w, int h, float sigma)
    {
        if (sigma <= 0.05f) return (float[])src.Clone();
        int r = Math.Max(1, (int)(sigma * 3));
        var k = new float[2*r + 1]; float ksum = 0;
        for (int i = -r; i <= r; i++) { float v = (float)Math.Exp(-(i*i) / (2.0*sigma*sigma)); k[i+r] = v; ksum += v; }
        for (int i = 0; i < k.Length; i++) k[i] /= ksum;
        var tmp = new float[(long)w * h];
        var dst = new float[(long)w * h];
        // 横向
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int i = -r; i <= r; i++) acc += k[i+r] * src[(long)y * w + Clamp(x+i, 0, w-1)];
                tmp[(long)y * w + x] = acc;
            }
        // 纵向
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int i = -r; i <= r; i++) acc += k[i+r] * tmp[(long)Clamp(y+i, 0, h-1) * w + x];
                dst[(long)y * w + x] = acc;
            }
        return dst;
    }

    internal static float[] ComputeSlopeDeg(float[] z, int w, int h, float cs)
    {
        var slope = new float[(long)w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int xm = Clamp(x-1, 0, w-1), xp = Clamp(x+1, 0, w-1);
                int ym = Clamp(y-1, 0, h-1), yp = Clamp(y+1, 0, h-1);
                float gx = (z[(long)y*w + xp] - z[(long)y*w + xm]) / ((xp - xm) * cs);
                float gy = (z[(long)yp*w + x] - z[(long)ym*w + x]) / ((yp - ym) * cs);
                slope[(long)y*w + x] = (float)(Math.Atan(Math.Sqrt(gx*gx + gy*gy)) * 180.0 / Math.PI);
            }
        return slope;
    }

    // 距离变换：每个可行驶格 → 到最近"非可行驶格"(走廊边界)的距离(格)。复用 JFA。
    internal static float[] DistanceTransformCells(bool[] mask, int w, int h)
    {
        int wh = w * h;
        var sx = new int[wh]; var sy = new int[wh];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int id = y * w + x;
                if (!mask[id]) { sx[id] = x; sy[id] = y; } else { sx[id] = -1; sy[id] = -1; }
            }
        JumpFloodFill(sx, sy, w, h);   // mask 格 → 最近非 mask 格
        var dt = new float[wh];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int id = y * w + x;
                if (!mask[id]) { dt[id] = 0; continue; }
                if (sx[id] >= 0) { double dx = x - sx[id], dy = y - sy[id]; dt[id] = (float)Math.Sqrt(dx * dx + dy * dy); }
                else dt[id] = float.MaxValue;   // 整图皆可行驶（无边界）
            }
        return dt;
    }

    internal static void RemoveSmallComponents(bool[] mask, int w, int h, int minCells)
    {
        if (minCells <= 1) return;
        var label = new int[(long)w * h];
        var stack = new Stack<int>();
        int cur = 0;
        for (int s = 0; s < w * h; s++)
        {
            if (!mask[s] || label[s] != 0) continue;
            cur++;
            var cells = new List<int>();
            stack.Push(s); label[s] = cur;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); cells.Add(c);
                int cx = c % w, cy = c / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int nid = ny * w + nx;
                        if (mask[nid] && label[nid] == 0) { label[nid] = cur; stack.Push(nid); }
                    }
            }
            if (cells.Count < minCells) foreach (int c in cells) mask[c] = false;
        }
    }

    // ── Zhang-Suen 细化（8-连通 1px 骨架） ──
    internal static void ZhangSuen(bool[] m, int w, int h)
    {
        var toDel = new List<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int sub = 0; sub < 2; sub++)
            {
                toDel.Clear();
                for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int id = y * w + x;
                        if (!m[id]) continue;
                        bool p2 = m[id - w], p3 = m[id - w + 1], p4 = m[id + 1], p5 = m[id + w + 1];
                        bool p6 = m[id + w], p7 = m[id + w - 1], p8 = m[id - 1], p9 = m[id - w - 1];
                        int b = (p2?1:0)+(p3?1:0)+(p4?1:0)+(p5?1:0)+(p6?1:0)+(p7?1:0)+(p8?1:0)+(p9?1:0);
                        if (b < 2 || b > 6) continue;
                        bool[] seq = { p2, p3, p4, p5, p6, p7, p8, p9, p2 };
                        int a = 0; for (int i = 0; i < 8; i++) if (!seq[i] && seq[i+1]) a++;
                        if (a != 1) continue;
                        if (sub == 0)
                        {
                            if (p2 && p4 && p6) continue;
                            if (p4 && p6 && p8) continue;
                        }
                        else
                        {
                            if (p2 && p4 && p8) continue;
                            if (p2 && p6 && p8) continue;
                        }
                        toDel.Add(id);
                    }
                if (toDel.Count > 0) { changed = true; foreach (int id in toDel) m[id] = false; }
            }
        }
    }

    private static int PixDegree(bool[] m, int w, int h, int x, int y)
    {
        int d = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                if (m[ny * w + nx]) d++;
            }
        return d;
    }

    private static void PruneSpurs(bool[] m, int w, int h)
    {
        // 删掉长度 1 的端点毛刺（端点且其唯一邻居是交叉口）——保守每遍剥一层短端点。
        var rm = new List<int>();
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int id = y * w + x;
                if (!m[id]) continue;
                if (PixDegree(m, w, h, x, y) != 1) continue;
                // 端点：若其唯一邻居是交叉口(度≥3)，此端点是毛刺
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (m[ny * w + nx] && PixDegree(m, w, h, nx, ny) >= 3) rm.Add(id);
                    }
            }
        foreach (int id in rm) m[id] = false;
    }

    // ── 缺口桥接：连接相距≤BridgeGap、同高程的悬空端点（接回被切的坡道/相邻片，使本该连通的真连通）──
    private static void BridgeSkeletonGaps(bool[] skel, float[] demS, float[] dtCells, int w, int h, float cs, RoadSkeletonOptions opt)
    {
        double minHalf = (opt.MinRoadWidth * 0.5) / cs;
        double bridgeCells = opt.BridgeGapMeters / cs;
        double b2 = bridgeCells * bridgeCells;
        float zTol = (float)opt.BridgeZTol;

        bool IsNode(int x, int y)
        { if (x < 0 || y < 0 || x >= w || y >= h) return false; int id = y * w + x; return skel[id] && dtCells[id] >= minHalf; }
        int Deg(int x, int y)
        { int d = 0; for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { if (dx == 0 && dy == 0) continue; if (IsNode(x + dx, y + dy)) d++; } return d; }

        var eps = new List<int>();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (IsNode(x, y) && Deg(x, y) == 1) eps.Add(y * w + x);
        if (eps.Count < 2) return;

        int gc = Math.Max(1, (int)Math.Ceiling(bridgeCells));
        long CellKey(int cgx, int cgy) => ((long)cgx << 32) | (uint)cgy;
        var grid = new Dictionary<long, List<int>>(PackedKeyComparer.Instance);
        foreach (int id in eps) { long k = CellKey((id % w) / gc, (id / w) / gc); if (!grid.TryGetValue(k, out var l)) { l = new List<int>(); grid[k] = l; } l.Add(id); }

        var used = new HashSet<int>();
        foreach (int id in eps)
        {
            if (used.Contains(id)) continue;
            int x = id % w, y = id / w; float z = demS[id];
            int gx = x / gc, gy = y / gc, best = -1; double bestD = b2;
            for (int dgy = -1; dgy <= 1; dgy++)
                for (int dgx = -1; dgx <= 1; dgx++)
                {
                    if (!grid.TryGetValue(CellKey(gx + dgx, gy + dgy), out var bucket)) continue;
                    foreach (int oid in bucket)
                    {
                        if (oid == id || used.Contains(oid)) continue;
                        int ox = oid % w, oy = oid / w;
                        double dd = (double)(ox - x) * (ox - x) + (double)(oy - y) * (oy - y);
                        if (dd > bestD || dd < 4) continue;              // 超距 / 太近(同线)跳过
                        if (Math.Abs(demS[oid] - z) > zTol) continue;    // 同高程才桥
                        bestD = dd; best = oid;
                    }
                }
            if (best < 0) continue;
            DrawLineMark(skel, dtCells, w, h, x, y, best % w, best / w, (float)minHalf);
            used.Add(id); used.Add(best);
        }
    }

    // Bresenham 画线，沿线置 skel=true 且 dt≥minHalf（让桥接段通过路宽闸门、进图被路由）。
    private static void DrawLineMark(bool[] skel, float[] dt, int w, int h, int x0, int y0, int x1, int y1, float minHalf)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx - dy;
        while (true)
        {
            int id = y0 * w + x0; skel[id] = true; if (dt[id] < minHalf) dt[id] = minHalf;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err; if (e2 > -dy) { err -= dy; x0 += sx; } if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    // ── 干线路由：可行驶骨架建图（只取走廊宽度≥W_min 的格）→ 坑口(最高)到各深区(最低)
    //    Dijkstra → 路径并集掩膜。窄于路宽的走廊不入图 → 路由自动绕开/排除。 ──
    private static bool[] RouteHaulNetwork(bool[] skel, float[] demS, float[] dtCells, int w, int h, float cs, RoadSkeletonOptions opt)
    {
        int wh = w * h;
        // 路宽闸门：骨架格的走廊宽 = 2·dt·cs；保留 ≥ W_min 的格作为图节点。
        double minHalfCells = (opt.MinRoadWidth * 0.5) / cs;
        var nodeId = new int[wh];
        var cell = new List<int>();
        for (int id = 0; id < wh; id++)
        {
            if (skel[id] && dtCells[id] >= minHalfCells) { nodeId[id] = cell.Count; cell.Add(id); }
            else nodeId[id] = -1;
        }
        int M = cell.Count;
        if (M < 2) return new bool[wh];

        // 8-邻接（节点索引 + 权重）写入复用缓冲
        int Nbrs(int nodeIndex, Span<int> oi, Span<double> ow)
        {
            int c = cell[nodeIndex], cx = c % w, cy = c / w, k = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int nc = ny * w + nx;
                    if (nodeId[nc] >= 0) { oi[k] = nodeId[nc]; ow[k] = (dx == 0 || dy == 0) ? 1.0 : 1.4142135623730951; k++; }
                }
            return k;
        }

        Span<int> nbrIdx = stackalloc int[8];
        Span<double> nbrW = stackalloc double[8];

        // 连通分量（BFS）→ 取最大
        var comp = new int[M]; for (int i = 0; i < M; i++) comp[i] = -1;
        int nComp = 0; var compSize = new List<int>(); var q = new Queue<int>();
        for (int s = 0; s < M; s++)
        {
            if (comp[s] != -1) continue;
            comp[s] = nComp; int sz = 0; q.Clear(); q.Enqueue(s);
            while (q.Count > 0)
            {
                int u = q.Dequeue(); sz++;
                int kn = Nbrs(u, nbrIdx, nbrW);
                for (int t = 0; t < kn; t++) { int v = nbrIdx[t]; if (comp[v] == -1) { comp[v] = nComp; q.Enqueue(v); } }
            }
            compSize.Add(sz); nComp++;
        }
        // 按分量分组（每个"坑"各自路由，覆盖多坑/多作业区 = 搜出所有线路）
        var byComp = new List<int>[nComp];
        for (int c = 0; c < nComp; c++) byComp[c] = new List<int>();
        for (int i = 0; i < M; i++) byComp[comp[i]].Add(i);

        var dist = new double[M]; var pred = new int[M];
        var haul = new bool[wh];
        int minRoute = Math.Max(50, opt.MinRouteComponentCells);
        double sepCells = opt.MinDestSepMeters / cs;

        for (int c = 0; c < nComp; c++)
        {
            var members = byComp[c];
            if (members.Count < minRoute) continue;

            // 坑口 = 该坑最高点
            int rim = -1; float rimZ = float.MinValue;
            foreach (int i in members) { float z = demS[cell[i]]; if (z > rimZ) { rimZ = z; rim = i; } }
            if (rim < 0) continue;

            // Dijkstra from rim（只在本坑内扩散）
            foreach (int i in members) { dist[i] = double.MaxValue; pred[i] = -1; }
            dist[rim] = 0;
            var pq = new PriorityQueue<int, double>(); pq.Enqueue(rim, 0);
            while (pq.Count > 0)
            {
                pq.TryDequeue(out int u, out double du);
                if (du > dist[u]) continue;
                int kn = Nbrs(u, nbrIdx, nbrW);
                for (int t = 0; t < kn; t++)
                {
                    int v = nbrIdx[t]; double nd = du + nbrW[t];
                    if (nd < dist[v]) { dist[v] = nd; pred[v] = u; pq.Enqueue(v, nd); }
                }
            }

            if (opt.FullNetwork)
            {
                // 扩展路由：最远点采样(FPS)撒 N 个遍布全坑的目的地(首点=入口)，并集它们到入口的
                // 【路径】→ 覆盖所有区(含外圈环/各高程)，且每条是干净单线(不会出宽平地"刷子")。
                var dests = new List<int> { rim };
                var minD2 = new double[members.Count];
                for (int i = 0; i < members.Count; i++) minD2[i] = double.MaxValue;
                int ndTarget = Math.Max(2, opt.MaxDestinations);
                while (dests.Count < ndTarget)
                {
                    int last = dests[dests.Count - 1];
                    int lcx = cell[last] % w, lcy = cell[last] / w;
                    double best = -1; int bestNode = -1;
                    for (int mi = 0; mi < members.Count; mi++)
                    {
                        int v = members[mi];
                        if (dist[v] >= double.MaxValue) continue;
                        int vx = cell[v] % w, vy = cell[v] / w;
                        double d2 = (double)(vx - lcx) * (vx - lcx) + (double)(vy - lcy) * (vy - lcy);
                        if (d2 < minD2[mi]) minD2[mi] = d2;
                        if (minD2[mi] > best) { best = minD2[mi]; bestNode = v; }
                    }
                    if (bestNode < 0 || best < 1) break;
                    dests.Add(bestNode);
                }
                foreach (int d in dests)
                {
                    int cur = d; int guard = 0;
                    while (cur >= 0 && guard++ < M + 1) { haul[cell[cur]] = true; if (cur == rim) break; cur = pred[cur]; }
                }
            }
            else
            {
                // 固定线路：本坑可达点按 Z 升序，贪心取分散最低点，只标到这几个深点的最短路。
                var reach = new List<int>();
                foreach (int i in members) if (dist[i] < double.MaxValue) reach.Add(i);
                reach.Sort((a, b) => demS[cell[a]].CompareTo(demS[cell[b]]));
                var dests = new List<int>();
                foreach (int cand in reach)
                {
                    int cx = cell[cand] % w, cy = cell[cand] / w; bool ok = true;
                    foreach (int d in dests)
                    {
                        int dx = cell[d] % w, dy = cell[d] / w;
                        if (Math.Sqrt((double)(cx-dx)*(cx-dx) + (double)(cy-dy)*(cy-dy)) <= sepCells) { ok = false; break; }
                    }
                    if (ok) dests.Add(cand);
                    if (dests.Count >= Math.Max(1, opt.MaxDestinations)) break;
                }
                foreach (int d in dests)
                {
                    int cur = d; int guard = 0;
                    while (cur >= 0 && guard++ < M + 1) { haul[cell[cur]] = true; if (cur == rim) break; cur = pred[cur]; }
                }
            }
        }
        return haul;
    }

    // ── 骨架追踪：从端点/交叉口出发沿度=2 链走，产出每条边的像素序列 ──
    internal static List<List<int>> TraceSkeleton(bool[] m, int w, int h)
    {
        var result = new List<List<int>>();
        var used = new bool[(long)w * h];   // 标记已走过的"边像素"(度=2 链内部)，避免重复

        (int x, int y)[] Neigh(int x, int y, List<(int,int)> buf)
        {
            buf.Clear();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    if (m[ny * w + nx]) buf.Add((nx, ny));
                }
            return buf.ToArray();
        }

        var nb = new List<(int,int)>();
        // 从所有"节点"(端点 deg==1 或交叉口 deg>=3)出发
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!m[y * w + x]) continue;
                int deg = PixDegree(m, w, h, x, y);
                if (deg == 2) continue;   // 链内部，等节点来走
                var neigh = Neigh(x, y, nb);
                foreach (var (sx, sy) in neigh)
                {
                    long eid = (long)sy * w + sx;
                    if (PixDegree(m, w, h, sx, sy) == 2 && used[eid]) continue;
                    // 沿该方向走一条边
                    var path = new List<int> { y * w + x };
                    int px = x, py = y, cx2 = sx, cy2 = sy;
                    while (true)
                    {
                        path.Add(cy2 * w + cx2);
                        int cdeg = PixDegree(m, w, h, cx2, cy2);
                        if (cdeg != 2) break;             // 到下一个节点，停
                        used[(long)cy2 * w + cx2] = true;
                        var cn = Neigh(cx2, cy2, nb);
                        int nxs = -1, nys = -1;
                        foreach (var (ax, ay) in cn)
                        {
                            if (ax == px && ay == py) continue;
                            nxs = ax; nys = ay; break;
                        }
                        if (nxs < 0) break;
                        px = cx2; py = cy2; cx2 = nxs; cy2 = nys;
                    }
                    if (path.Count >= 2) result.Add(path);
                }
            }

        // 孤立环（全度=2，无节点）：扫未用的度=2 像素，走一圈
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int id = y * w + x;
                if (!m[id] || used[id] || PixDegree(m, w, h, x, y) != 2) continue;
                var path = new List<int>();
                int px = -1, py = -1, cx2 = x, cy2 = y;
                int guard = 0;
                while (guard++ < w * h)
                {
                    path.Add(cy2 * w + cx2);
                    used[(long)cy2 * w + cx2] = true;
                    var cn = Neigh(cx2, cy2, nb);
                    int nxs = -1, nys = -1;
                    foreach (var (ax, ay) in cn)
                    {
                        if (ax == px && ay == py) continue;
                        if (used[(long)ay * w + ax]) continue;
                        nxs = ax; nys = ay; break;
                    }
                    if (nxs < 0) break;
                    px = cx2; py = cy2; cx2 = nxs; cy2 = nys;
                }
                if (path.Count >= 2) { path.Add(path[0]); result.Add(path); }
            }

        return result;
    }

    // ── Douglas-Peucker（XY 上判距，保 Z）→ 扁平 [x,y,z,...] ──
    internal static double[] DpSimplify(List<double> xs, List<double> ys, List<double> zs, double tol)
    {
        int n = xs.Count;
        if (n < 3)
        {
            var outp = new double[n * 3];
            for (int i = 0; i < n; i++) { outp[3*i]=xs[i]; outp[3*i+1]=ys[i]; outp[3*i+2]=zs[i]; }
            return outp;
        }
        var keep = new bool[n]; keep[0] = keep[n-1] = true;
        DpRec(xs, ys, 0, n - 1, tol, keep);
        var list = new List<double>();
        for (int i = 0; i < n; i++) if (keep[i]) { list.Add(xs[i]); list.Add(ys[i]); list.Add(zs[i]); }
        return list.ToArray();
    }

    private static void DpRec(List<double> xs, List<double> ys, int i0, int i1, double tol, bool[] keep)
    {
        if (i1 <= i0 + 1) return;
        double ax = xs[i0], ay = ys[i0], bx = xs[i1], by = ys[i1];
        double abx = bx - ax, aby = by - ay;
        double abl = Math.Sqrt(abx*abx + aby*aby);
        double maxD = -1; int maxI = -1;
        for (int i = i0 + 1; i < i1; i++)
        {
            double d = abl < 1e-9
                ? Math.Sqrt((xs[i]-ax)*(xs[i]-ax) + (ys[i]-ay)*(ys[i]-ay))
                : Math.Abs((xs[i]-ax)*aby - (ys[i]-ay)*abx) / abl;
            if (d > maxD) { maxD = d; maxI = i; }
        }
        if (maxD > tol && maxI > 0)
        {
            keep[maxI] = true;
            DpRec(xs, ys, i0, maxI, tol, keep);
            DpRec(xs, ys, maxI, i1, tol, keep);
        }
    }

    private static double PolyLen(double[] flat)
    {
        double len = 0; int n = flat.Length / 3;
        for (int i = 1; i < n; i++)
            len += Math.Sqrt((flat[3*i]-flat[3*(i-1)])*(flat[3*i]-flat[3*(i-1)]) + (flat[3*i+1]-flat[3*(i-1)+1])*(flat[3*i+1]-flat[3*(i-1)+1]));
        return len;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
