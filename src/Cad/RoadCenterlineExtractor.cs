using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>道路中心线提取参数。主参数为最小路面宽度 W_min；其余给合理默认、可在对话框调。忠实移植原 PointCloudLib.RoadCenterline。</summary>
public sealed class RoadCenterlineOptions
{
    /// <summary>最小路面宽度 W_min (m)：平盘两侧台阶线间距（平盘宽）大于此值才提取中心线。</summary>
    public double MinRoadWidth = 15.0;
    /// <summary>最大配对间距 W_max (m)：两线间距超过此值视为非道路（宽阔料场/坑底），不配对。</summary>
    public double MaxPairDist = 60.0;
    /// <summary>同平盘标高容差 Δz (m)：两条台阶线高差小于此值才算同一平盘的两条边。
    /// 应远小于一个台阶高，以排除"同一立面的坡顶/坡底"（相差一个台阶高）的误配。</summary>
    public double ZTolerance = 3.0;
    /// <summary>沿台阶线采样步距 (m)：站点越密中心线越平滑、越慢。</summary>
    public double SampleStep = 3.0;
    /// <summary>最短中心线长 (m)：短于此的碎段丢弃。</summary>
    public double MinLineLen = 30.0;
    /// <summary>碎段拼接端点间距 (m)：测绘台阶线常裂成许多短段，先按"端点相接 + 同标高 +
    /// 方向延续"拼成连续的平盘边界，再配对取中线，否则中线又碎又重复。0=不拼接。</summary>
    public double StitchGap = 12.0;
    /// <summary>坡道连通焊接半径 (m)：斜坡道把上下平盘的中线接通——把开口中线（平盘弧 / 坡道段）
    /// 的端点焊到此半径内最近的另一条中线上，形成连通路网（闭合环不动）。0=不焊接。</summary>
    public double WeldGap = 25.0;
    /// <summary>配对方向与台阶线法向的最大夹角 (°)：保证"横量"间距，排除沿走向的错配。</summary>
    public double MaxNormalAngleDeg = 40.0;
    /// <summary>Douglas-Peucker 简化容差 (m)。</summary>
    public double SimplifyTol = 1.0;
}

/// <summary>道路中心线提取结果。每条中心线为扁平 [x0,y0,z0,x1,y1,z1,...]。</summary>
public sealed class RoadCenterlineResult
{
    public List<double[]> Centerlines = new();
    public int LinesUsed;
    public int StationsPaired;
    public int StationsGatedOut;
    public double TotalLengthM;
    public string Summary = string.Empty;
}

/// <summary>
/// 由台阶线按"同高程配对 + 取中线 + 最小路宽闸门"提取台阶平盘 / 斜坡道的道路中心线。
/// 忠实移植原 PointCloudLib.RoadCenterline.RoadCenterlineExtractor（纯 C# 后处理，只吃台阶线多段线）。
///
/// 几何规则：一级平盘的两条边是【同一高程、平行、相距 = 平盘宽】的两条台阶线；台阶立面两条边相差一个台阶高。
/// 每条台阶线找它"同高程、近法向、间距∈[W_min,W_max]"的对向邻线，取两线中点 = 道路中心线；窄于 W_min 处自动断开。
///
/// 支持两种输入：①已分顶/底 → <see cref="Extract(IReadOnlyList{double[]},IReadOnlyList{double[]},RoadCenterlineOptions)"/>；
/// ②单一"台阶线"图层未分顶底 → <see cref="Extract(IReadOnlyList{double[]},RoadCenterlineOptions)"/>（自配对 + 去重）。
/// </summary>
public static class RoadCenterlineExtractor
{
    private readonly struct P3
    {
        public readonly double X, Y, Z;
        public readonly int Line;
        public P3(double x, double y, double z, int line) { X = x; Y = y; Z = z; Line = line; }
    }

    /// <summary>单一台阶线池（未分顶底）：每条线找同高程对向邻线取中线，跨边重复自配对会去重。</summary>
    public static RoadCenterlineResult Extract(
        IReadOnlyList<double[]> benchLines, RoadCenterlineOptions opt)
        => ExtractCore(benchLines, benchLines, opt, samePool: true);

    /// <summary>已分顶/底：坡顶线 × 坡底线 单向配对（无重复，无需去重）。</summary>
    public static RoadCenterlineResult Extract(
        IReadOnlyList<double[]> crestLines,
        IReadOnlyList<double[]> toeLines,
        RoadCenterlineOptions opt)
        => ExtractCore(crestLines, toeLines, opt, samePool: false);

    private static RoadCenterlineResult ExtractCore(
        IReadOnlyList<double[]> srcLines,
        IReadOnlyList<double[]> tgtLines,
        RoadCenterlineOptions opt,
        bool samePool)
    {
        var res = new RoadCenterlineResult();
        if (srcLines == null || tgtLines == null || srcLines.Count == 0 || tgtLines.Count == 0)
        {
            res.Summary = "缺少台阶线（请确认图纸中已有台阶线）。";
            return res;
        }

        double step = opt.SampleStep > 0.1 ? opt.SampleStep : 3.0;
        double cell = opt.MaxPairDist > 1.0 ? opt.MaxPairDist : 60.0;
        double cosMax = Math.Cos(Math.Max(0.0, Math.Min(89.0, opt.MaxNormalAngleDeg)) * Math.PI / 180.0);

        // ── 先把碎段拼成连续台阶线（重建连续平盘边界），再配对——否则碎段让中线又碎又重复 ──
        var srcL = srcLines;
        var tgtL = tgtLines;
        if (opt.StitchGap > 0)
        {
            double stitchCos = Math.Cos(60.0 * Math.PI / 180.0);   // 方向延续：转角 < 60° 才连
            srcL = StitchFragments(srcLines, opt.StitchGap, opt.ZTolerance, stitchCos);
            tgtL = samePool ? srcL : StitchFragments(tgtLines, opt.StitchGap, opt.ZTolerance, stitchCos);
        }

        // ── 把所有"目标"台阶线加密并装入均匀网格（按 W_max 为格距，查询时扫 3×3）──
        var tgtPts = new List<P3>();
        for (int li = 0; li < tgtL.Count; li++)
            ResampleByArc(tgtL[li], step, tgtPts, li);
        var grid = new Dictionary<long, List<int>>(tgtPts.Count, PackedKeyComparer.Instance);
        for (int i = 0; i < tgtPts.Count; i++)
        {
            long key = CellKey(tgtPts[i].X, tgtPts[i].Y, cell);
            if (!grid.TryGetValue(key, out var bucket)) { bucket = new List<int>(); grid[key] = bucket; }
            bucket.Add(i);
        }

        // ── 逐条"源"台阶线：采样 → 找对向邻线点 → 闸门取中点 → 串段 ──
        var srcSta = new List<P3>();
        for (int si = 0; si < srcL.Count; si++)
        {
            srcSta.Clear();
            ResampleByArc(srcL[si], step, srcSta, si);
            if (srcSta.Count < 2) continue;
            res.LinesUsed++;

            var seg = new List<P3>();
            for (int i = 0; i < srcSta.Count; i++)
            {
                P3 p = srcSta[i];
                // 法向（XY 平面，垂直于局部切向）
                int a = Math.Max(0, i - 1), b = Math.Min(srcSta.Count - 1, i + 1);
                double tx = srcSta[b].X - srcSta[a].X, ty = srcSta[b].Y - srcSta[a].Y;
                double tl = Math.Sqrt(tx * tx + ty * ty);
                if (tl < 1e-9) { FlushSeg(seg, opt, res); continue; }
                double nx = -ty / tl, ny = tx / tl;   // 单位法向

                if (TryFindOpposite(p, nx, ny, tgtPts, grid, cell, opt, cosMax, samePool, si,
                                    out double qx, out double qy, out double qz))
                {
                    seg.Add(new P3(0.5 * (p.X + qx), 0.5 * (p.Y + qy), 0.5 * (p.Z + qz), si));
                    res.StationsPaired++;
                }
                else
                {
                    res.StationsGatedOut++;
                    FlushSeg(seg, opt, res);   // 断口：收束当前段
                }
            }
            FlushSeg(seg, opt, res);
        }

        // 输出也拼接：闸门断口 / 采样切换造成的中线碎段，按连续性接成长线；再剔仍过短的。
        if (opt.StitchGap > 0 && res.Centerlines.Count > 1)
        {
            double stitchCos = Math.Cos(60.0 * Math.PI / 180.0);
            res.Centerlines = StitchFragments(res.Centerlines, opt.StitchGap, opt.ZTolerance, stitchCos);
        }
        res.Centerlines.RemoveAll(cl => PolyLenXY(cl) < opt.MinLineLen);

        // 去重兜底（理论上 id-ordering 已无重复；拼接残留的近重合再清一次）。
        DedupCoincident(res.Centerlines, Math.Max(2.0, 0.4 * opt.MinRoadWidth));

        // 坡道连通：把开口中线端点焊到附近其他中线上，斜坡道口连成路网（闭合环不动）。
        if (opt.WeldGap > 0)
            WeldEndpoints(res.Centerlines, opt.WeldGap, opt.ZTolerance);

        foreach (var cl in res.Centerlines) res.TotalLengthM += PolyLenXY(cl);
        res.Summary = $"道路中心线：{res.Centerlines.Count} 条，合计 {res.TotalLengthM:F0} m" +
                      $"（用台阶线 {res.LinesUsed} 条，配对站点 {res.StationsPaired}，闸门剔除 {res.StationsGatedOut}）。";
        return res;
    }

    /// <summary>在目标点集中找 p 的对向（沿 ±法向）最近点：同标高 + 间距∈[W_min,W_max] + 近法向。
    /// samePool 时跳过"与自己同一条线"的点（防闭合环跨内部自配对）。</summary>
    private static bool TryFindOpposite(P3 p, double nx, double ny,
        List<P3> tgtPts, Dictionary<long, List<int>> grid, double cell,
        RoadCenterlineOptions opt, double cosMax, bool samePool, int srcLine,
        out double qx, out double qy, out double qz)
    {
        qx = qy = qz = 0;
        int gx = (int)Math.Floor(p.X / cell), gy = (int)Math.Floor(p.Y / cell);
        double best = double.MaxValue; int bestIdx = -1;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!grid.TryGetValue(PackCell(gx + dx, gy + dy), out var bucket)) continue;
                foreach (int idx in bucket)
                {
                    P3 q = tgtPts[idx];
                    // 池内：只往"线号更大"的一侧配对 → 每个平盘对仅在处理较小线号那次被采一次，
                    // 从源头杜绝"同一平盘内外两边各采一次"的重叠中线（"花圈"）。同时天然跳过自配对。
                    if (samePool && q.Line <= srcLine) continue;
                    if (Math.Abs(q.Z - p.Z) > opt.ZTolerance) continue;       // 同平盘
                    double vx = q.X - p.X, vy = q.Y - p.Y;
                    double d = Math.Sqrt(vx * vx + vy * vy);
                    if (d < 1e-9 || d > opt.MaxPairDist) continue;            // 搜索半径
                    double along = Math.Abs(vx * nx + vy * ny);               // 法向（横向）分量 = 真实路宽
                    if (along / d < cosMax) continue;                         // 必须近横向（排除沿走向错配）
                    if (along < opt.MinRoadWidth || along > opt.MaxPairDist) continue; // 路宽闸门（按法向宽度）
                    if (d < best) { best = d; bestIdx = idx; }
                }
            }
        if (bestIdx < 0) return false;
        P3 bp = tgtPts[bestIdx];
        qx = bp.X; qy = bp.Y; qz = bp.Z;
        return true;
    }

    private static void FlushSeg(List<P3> seg, RoadCenterlineOptions opt, RoadCenterlineResult res)
    {
        if (seg.Count >= 2)
        {
            double len = 0;
            for (int i = 1; i < seg.Count; i++)
            {
                double dx = seg[i].X - seg[i - 1].X, dy = seg[i].Y - seg[i - 1].Y;
                len += Math.Sqrt(dx * dx + dy * dy);
            }
            if (len >= opt.MinLineLen)
            {
                var simplified = Simplify(seg, Math.Max(0.1, opt.SimplifyTol));
                if (simplified.Count >= 2)
                {
                    var flat = new double[simplified.Count * 3];
                    for (int i = 0; i < simplified.Count; i++)
                    {
                        flat[3 * i] = simplified[i].X;
                        flat[3 * i + 1] = simplified[i].Y;
                        flat[3 * i + 2] = simplified[i].Z;
                    }
                    res.Centerlines.Add(flat);
                }
            }
        }
        seg.Clear();
    }

    /// <summary>把端点相接、标高相近、方向延续的碎段拼成连续线（重建连续平盘边界 / 中线）。
    /// 贪心：每条未用线为一条链，两端各反复吸附"端点在 gap 内、Δz&lt;zTol、转角&lt;阈值"的最近碎段。
    /// 方向延续判据防止在平盘收窄 / 坡道口处把内、外两侧边界错接成一条（那是 ~180° 折返）。</summary>
    private static List<double[]> StitchFragments(
        IReadOnlyList<double[]> lines, double gap, double zTol, double cosMin)
    {
        int n = lines.Count;
        var used = new bool[n];
        double g2 = gap * gap;
        var result = new List<double[]>();

        // 把 j 按"近连接点→远端"的朝向接到 chain 的指定端。
        static void AddOriented(List<double> chain, double[] lj, int nearSide, bool atHead)
        {
            int mj = lj.Length / 3;
            if (atHead)
            {
                // 头部：远端在前，近端贴 chain 旧头。nearSide=0→近端是 index0，远→近为 0..mj-1 倒着放。
                var pre = new List<double>(lj.Length);
                if (nearSide == 0) for (int k = mj - 1; k >= 0; k--) { pre.Add(lj[3*k]); pre.Add(lj[3*k+1]); pre.Add(lj[3*k+2]); }
                else for (int k = 0; k < mj; k++) { pre.Add(lj[3*k]); pre.Add(lj[3*k+1]); pre.Add(lj[3*k+2]); }
                pre.AddRange(chain);
                chain.Clear(); chain.AddRange(pre);
            }
            else
            {
                if (nearSide == 0) for (int k = 0; k < mj; k++) { chain.Add(lj[3*k]); chain.Add(lj[3*k+1]); chain.Add(lj[3*k+2]); }
                else for (int k = mj - 1; k >= 0; k--) { chain.Add(lj[3*k]); chain.Add(lj[3*k+1]); chain.Add(lj[3*k+2]); }
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (used[i] || lines[i].Length < 6) continue;
            used[i] = true;
            var chain = new List<double>(lines[i]);

            for (int e = 0; e < 2; e++)
            {
                bool head = (e == 0);
                while (true)
                {
                    int m = chain.Count / 3;
                    int ei = head ? 0 : m - 1;
                    int ni = head ? Math.Min(1, m - 1) : Math.Max(0, m - 2);
                    double ex = chain[3*ei], ey = chain[3*ei+1], ez = chain[3*ei+2];
                    double tdx = ex - chain[3*ni], tdy = ey - chain[3*ni+1];     // chain 端外向切向
                    double tl = Math.Sqrt(tdx*tdx + tdy*tdy);
                    if (tl > 1e-9) { tdx /= tl; tdy /= tl; }

                    int best = -1, bestSide = 0; double bestD = g2;
                    for (int j = 0; j < n; j++)
                    {
                        if (used[j] || lines[j].Length < 6) continue;
                        var lj = lines[j]; int mj = lj.Length / 3;
                        for (int side = 0; side < 2; side++)
                        {
                            int p = side == 0 ? 0 : mj - 1;
                            double dx = lj[3*p] - ex, dy = lj[3*p+1] - ey, dz = lj[3*p+2] - ez;
                            double d2 = dx*dx + dy*dy;
                            if (d2 > bestD || Math.Abs(dz) > zTol) continue;
                            int interior = side == 0 ? Math.Min(1, mj - 1) : Math.Max(0, mj - 2);
                            double jdx = lj[3*interior] - lj[3*p], jdy = lj[3*interior+1] - lj[3*p+1];
                            double jl = Math.Sqrt(jdx*jdx + jdy*jdy);
                            if (jl > 1e-9) { jdx /= jl; jdy /= jl; }
                            // 方向延续：chain 外向切向 与 j 的"向内"方向同向才接。
                            if (tl > 1e-9 && jl > 1e-9 && (tdx*jdx + tdy*jdy) < cosMin) continue;
                            best = j; bestSide = side; bestD = d2;
                        }
                    }
                    if (best < 0) break;
                    used[best] = true;
                    AddOriented(chain, lines[best], bestSide, head);
                }
            }
            result.Add(chain.ToArray());
        }
        return result;
    }

    /// <summary>去重：按长度降序贪心保留；与已保留线大半重合（点到线距 ≤ tol 占比 &gt; 60%）者丢弃。</summary>
    private static void DedupCoincident(List<double[]> lines, double tol)
    {
        if (lines.Count < 2) return;
        lines.Sort((a, b) => PolyLenXY(b).CompareTo(PolyLenXY(a)));
        var kept = new List<double[]>(lines.Count);
        foreach (var c in lines)
        {
            bool dup = false;
            foreach (var k in kept)
                if (CoincidentFraction(c, k, tol) > 0.6) { dup = true; break; }
            if (!dup) kept.Add(c);
        }
        lines.Clear();
        lines.AddRange(kept);
    }

    private static double CoincidentFraction(double[] a, double[] b, double tol)
    {
        int n = a.Length / 3;
        if (n == 0) return 0;
        int within = 0;
        for (int i = 0; i < n; i++)
            if (NearestDistToPolyline3D(a[3 * i], a[3 * i + 1], a[3 * i + 2], b) <= tol) within++;
        return (double)within / n;
    }

    // 3D 最近距离：去重必须含 Z —— 露天矿上下两级台阶在平面上常正好重叠（同 XY、
    // 差一个台阶高），只比 XY 会把两条不同标高的中心线误并为一条。
    private static double NearestDistToPolyline3D(double px, double py, double pz, double[] poly)
    {
        int n = poly.Length / 3;
        if (n == 0) return double.MaxValue;
        if (n == 1)
        {
            double dx0 = px - poly[0], dy0 = py - poly[1], dz0 = pz - poly[2];
            return Math.Sqrt(dx0 * dx0 + dy0 * dy0 + dz0 * dz0);
        }
        double best = double.MaxValue;
        for (int i = 1; i < n; i++)
        {
            double ax = poly[3 * (i - 1)], ay = poly[3 * (i - 1) + 1], az = poly[3 * (i - 1) + 2];
            double bx = poly[3 * i], by = poly[3 * i + 1], bz = poly[3 * i + 2];
            double dx = bx - ax, dy = by - ay, dz = bz - az;
            double L2 = dx * dx + dy * dy + dz * dz;
            double t = L2 < 1e-12 ? 0 : ((px - ax) * dx + (py - ay) * dy + (pz - az) * dz) / L2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double cx = ax + t * dx, cy = ay + t * dy, cz = az + t * dz;
            double ddx = px - cx, ddy = py - cy, ddz = pz - cz;
            double d = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>坡道连通：把开口中线（非闭合环）的两个端点焊到 weldTol 内最近的另一条中线上，
    /// 使斜坡道口的平盘弧与坡道段连成路网。闭合环（首尾≈重合）不动。
    /// <b>Δz 约束</b>：路在连接点是连续的（同高程），故只焊 |Δz|≤zWeldTol 的目标点。</summary>
    private static void WeldEndpoints(List<double[]> lines, double weldTol, double zWeldTol)
    {
        double t2 = weldTol * weldTol;
        int cnt = lines.Count;
        for (int i = 0; i < cnt; i++)
        {
            var li = lines[i];
            int mi = li.Length / 3;
            if (mi < 2) continue;
            // 闭合环跳过（首尾≈重合）
            double cdx = li[0] - li[3*(mi-1)], cdy = li[1] - li[3*(mi-1)+1], cdz = li[2] - li[3*(mi-1)+2];
            if (cdx*cdx + cdy*cdy + cdz*cdz < 1.0) continue;

            for (int end = 0; end < 2; end++)
            {
                int ei = end == 0 ? 0 : mi - 1;
                double px = li[3*ei], py = li[3*ei+1], pz = li[3*ei+2];
                double bestD = t2, bx = 0, by = 0, bz = 0; bool found = false;
                for (int j = 0; j < cnt; j++)
                {
                    if (j == i) continue;
                    NearestPointOnPolyline3D(px, py, pz, lines[j],
                        out double nx, out double ny, out double nz, out double d2);
                    if (d2 < bestD && Math.Abs(nz - pz) <= zWeldTol)
                    { bestD = d2; bx = nx; by = ny; bz = nz; found = true; }
                }
                if (found && bestD > 1e-6) { li[3*ei] = bx; li[3*ei+1] = by; li[3*ei+2] = bz; }
            }
        }
    }

    private static void NearestPointOnPolyline3D(double px, double py, double pz, double[] poly,
        out double cx, out double cy, out double cz, out double bestD2)
    {
        cx = cy = cz = 0; bestD2 = double.MaxValue;
        int n = poly.Length / 3;
        if (n == 0) return;
        if (n == 1)
        {
            cx = poly[0]; cy = poly[1]; cz = poly[2];
            double ex = px - cx, ey = py - cy, ez = pz - cz;
            bestD2 = ex*ex + ey*ey + ez*ez; return;
        }
        for (int i = 1; i < n; i++)
        {
            double ax = poly[3*(i-1)], ay = poly[3*(i-1)+1], az = poly[3*(i-1)+2];
            double bx = poly[3*i], by = poly[3*i+1], bz = poly[3*i+2];
            double dx = bx - ax, dy = by - ay, dz = bz - az;
            double L2 = dx*dx + dy*dy + dz*dz;
            double t = L2 < 1e-12 ? 0 : ((px-ax)*dx + (py-ay)*dy + (pz-az)*dz) / L2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = ax + t*dx, qy = ay + t*dy, qz = az + t*dz;
            double ex = px - qx, ey = py - qy, ez = pz - qz;
            double d2 = ex*ex + ey*ey + ez*ez;
            if (d2 < bestD2) { bestD2 = d2; cx = qx; cy = qy; cz = qz; }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────
    private static void ResampleByArc(double[] flat, double step, List<P3> outPts, int lineId)
    {
        int n = flat.Length / 3;
        if (n < 2) { if (n == 1) outPts.Add(new P3(flat[0], flat[1], flat[2], lineId)); return; }
        P3 prev = new P3(flat[0], flat[1], flat[2], lineId);
        outPts.Add(prev);
        double carry = 0;
        for (int i = 1; i < n; i++)
        {
            P3 cur = new P3(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2], lineId);
            double dx = cur.X - prev.X, dy = cur.Y - prev.Y, dz = cur.Z - prev.Z;
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            if (segLen < 1e-9) { prev = cur; continue; }
            double t = step - carry;
            while (t <= segLen)
            {
                double f = t / segLen;
                outPts.Add(new P3(prev.X + dx * f, prev.Y + dy * f, prev.Z + dz * f, lineId));
                t += step;
            }
            carry = segLen - (t - step);
            prev = cur;
        }
        // 保留终点
        P3 last = new P3(flat[3 * (n - 1)], flat[3 * (n - 1) + 1], flat[3 * (n - 1) + 2], lineId);
        if (outPts.Count == 0 ||
            (Math.Abs(outPts[outPts.Count - 1].X - last.X) + Math.Abs(outPts[outPts.Count - 1].Y - last.Y)) > 1e-6)
            outPts.Add(last);
    }

    private static List<P3> Simplify(List<P3> pts, double tol)
    {
        int n = pts.Count;
        if (n < 3) return new List<P3>(pts);
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        DpRec(pts, 0, n - 1, tol, keep);
        var outp = new List<P3>(n);
        for (int i = 0; i < n; i++) if (keep[i]) outp.Add(pts[i]);
        return outp;
    }

    private static void DpRec(List<P3> pts, int i0, int i1, double tol, bool[] keep)
    {
        if (i1 <= i0 + 1) return;
        double ax = pts[i0].X, ay = pts[i0].Y, bx = pts[i1].X, by = pts[i1].Y;
        double abx = bx - ax, aby = by - ay;
        double abl = Math.Sqrt(abx * abx + aby * aby);
        double maxD = -1; int maxI = -1;
        for (int i = i0 + 1; i < i1; i++)
        {
            double d;
            if (abl < 1e-9)
            {
                double dx = pts[i].X - ax, dy = pts[i].Y - ay;
                d = Math.Sqrt(dx * dx + dy * dy);
            }
            else
            {
                d = Math.Abs((pts[i].X - ax) * aby - (pts[i].Y - ay) * abx) / abl;
            }
            if (d > maxD) { maxD = d; maxI = i; }
        }
        if (maxD > tol && maxI > 0)
        {
            keep[maxI] = true;
            DpRec(pts, i0, maxI, tol, keep);
            DpRec(pts, maxI, i1, tol, keep);
        }
    }

    private static double PolyLenXY(double[] flat)
    {
        double len = 0; int n = flat.Length / 3;
        for (int i = 1; i < n; i++)
        {
            double dx = flat[3 * i] - flat[3 * (i - 1)], dy = flat[3 * i + 1] - flat[3 * (i - 1) + 1];
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }

    private static long CellKey(double x, double y, double cell) =>
        PackCell((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));

    private static long PackCell(int gx, int gy) =>
        ((long)(uint)gx << 32) | (uint)gy;
}
