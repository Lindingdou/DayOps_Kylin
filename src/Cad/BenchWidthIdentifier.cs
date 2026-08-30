using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 采场参数识别 · 按平盘宽度提取区域（忠实移植原 <c>PlanLib.ShortTerm.BenchWidthIdentifier</c>）——
/// 直接利用现状台阶线, 圈出「平盘宽度 ≥ 目标值」的区域。纯栅格管线, 复用 <see cref="RasterMorphology"/>:
///   ① 台阶线加密栅格化 → 足迹(闭运算桥接) + 种子高程;
///   ② 最近种子高程多源 BFS 填满 → DEM;
///   ③ 局部高差场 = 小窗口 (Zmax−Zmin); 平盘 = 足迹内局部高差 &lt; dzFlat(近水平);
///   ④ 形态学开运算(半径 = W_target/2) 只保留放得下直径 W_target 圆的平盘 = 宽度 ≥ W_target 的条带;
///   ⑤ 连通成条带 + 面积过滤 + 描外轮廓 + DP 简化 → 达标平盘多边形。每块附代表(内切圆直径)宽度。
/// 输入为各台阶线的世界 flat-xyz(不需坡顶/坡底标签)。任何异常降级返回 Ok=false, 绝不抛。
/// </summary>
public static class BenchWidthIdentifier
{
    public sealed class Region
    {
        public List<(double x, double y)> Polygon = new();   // 闭合环(投影 XY)
        public double AreaHa;
        public double RepWidthM;                              // 代表(最大内切)宽度
        public double Z;                                      // 代表高程
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public List<Region> Regions = new();
    }

    public static Result Identify(
        IReadOnlyList<double[]> lines,
        double wTargetM,
        double cellSize = 4.0, double bridgeM = 40.0,
        double dzFlatM = 2.5, double flatWindowM = 10.0, double minStripAreaM2 = 600.0)
    {
        var res = new Result();
        try
        {
            if (wTargetM <= 0) { res.Message = "目标平盘宽度需为正值。"; return res; }
            var all = (lines ?? Enumerable.Empty<double[]>()).Where(l => l != null && l.Length >= 6).ToList();
            if (all.Count == 0) { res.Message = "未提供台阶线。"; return res; }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var l in all)
                for (int i = 0; i + 2 < l.Length; i += 3)
                {
                    minX = Math.Min(minX, l[i]); maxX = Math.Max(maxX, l[i]);
                    minY = Math.Min(minY, l[i + 1]); maxY = Math.Max(maxY, l[i + 1]);
                }
            double pad = bridgeM;
            minX -= pad; minY -= pad; maxX += pad; maxY += pad;

            const long MaxCells = 6_000_000;
            int nx = (int)Math.Ceiling((maxX - minX) / cellSize) + 1;
            int ny = (int)Math.Ceiling((maxY - minY) / cellSize) + 1;
            while ((long)nx * ny > MaxCells) { cellSize *= 1.5; nx = (int)Math.Ceiling((maxX - minX) / cellSize) + 1; ny = (int)Math.Ceiling((maxY - minY) / cellSize) + 1; }
            int N = nx * ny;
            int CX(double x) => Math.Clamp((int)((x - minX) / cellSize), 0, nx - 1);
            int CY(double y) => Math.Clamp((int)((y - minY) / cellSize), 0, ny - 1);

            // ① 足迹 + 种子高程
            var seedSum = new double[N]; var seedCnt = new int[N];
            void Stamp(double x, double y, double zz) { int c = CY(y) * nx + CX(x); seedSum[c] += zz; seedCnt[c]++; }
            foreach (var l in all)
                for (int i = 0; i + 5 < l.Length; i += 3)
                {
                    double x0 = l[i], y0 = l[i + 1], z0 = l[i + 2];
                    double x1 = l[i + 3], y1 = l[i + 4], z1 = l[i + 5];
                    int steps = Math.Max(1, (int)(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / cellSize));
                    for (int s = 0; s <= steps; s++)
                    { double t = (double)s / steps; Stamp(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, z0 + (z1 - z0) * t); }
                }
            var foot0 = new bool[N];
            for (int c = 0; c < N; c++) foot0[c] = seedCnt[c] > 0;
            int br = Math.Max(1, (int)Math.Round(bridgeM / cellSize));
            var foot = RasterMorphology.Close(foot0, nx, ny, br);
            RasterMorphology.FillHoles(foot, nx, ny);

            // ② DEM：最近种子高程多源 BFS
            var z = new double[N]; var hasZ = new bool[N];
            var q = new Queue<int>();
            for (int c = 0; c < N; c++) if (seedCnt[c] > 0) { z[c] = seedSum[c] / seedCnt[c]; hasZ[c] = true; q.Enqueue(c); }
            int[] dxn = { 1, -1, 0, 0, 1, 1, -1, -1 }, dyn = { 0, 0, 1, -1, 1, -1, 1, -1 };
            while (q.Count > 0)
            {
                int c = q.Dequeue(); int cx = c % nx, cy = c / nx;
                for (int k = 0; k < 8; k++)
                {
                    int ax = cx + dxn[k], ay = cy + dyn[k];
                    if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                    int a = ay * nx + ax;
                    if (!hasZ[a]) { z[a] = z[c]; hasZ[a] = true; q.Enqueue(a); }
                }
            }

            // ③ 局部高差场 → 平盘掩膜
            int wR = Math.Max(1, (int)Math.Round(flatWindowM / cellSize));
            var range = WindowRange(z, nx, ny, wR);
            var flat = new bool[N];
            for (int c = 0; c < N; c++) if (foot[c] && range[c] < dzFlatM) flat[c] = true;

            // ④ 开运算保留宽度 ≥ W_target
            int r = Math.Max(1, (int)Math.Round(wTargetM / 2.0 / cellSize));
            var qualify = RasterMorphology.Open(flat, nx, ny, r);
            for (int c = 0; c < N; c++) if (!foot[c]) qualify[c] = false;

            var dist = ChamferDistance(qualify, nx, ny);

            // ⑤ 连通 + 面积过滤 + 描边
            double cellHa = cellSize * cellSize / 1e4;
            int minCells = Math.Max(4, (int)(minStripAreaM2 / (cellSize * cellSize)));
            var comps = RasterMorphology.Components(qualify, nx, ny, minCells);

            foreach (var comp in comps)
            {
                var mask = new bool[N];
                foreach (int c in comp) mask[c] = true;
                var ring = RasterMorphology.TraceBoundary(mask, nx, ny);
                if (ring.Count < 3) continue;
                var simp = RasterMorphology.Simplify(ring, 1.5);
                if (simp.Count < 3) continue;
                double zr = comp.Average(c => z[c]);
                double maxD = comp.Max(c => dist[c]);
                var poly = new List<(double x, double y)>(simp.Count);
                foreach (var (gx, gy) in simp)
                    poly.Add((minX + (gx + 0.5) * cellSize, minY + (gy + 0.5) * cellSize));
                res.Regions.Add(new Region { Polygon = poly, AreaHa = comp.Count * cellHa, RepWidthM = 2.0 * maxD * cellSize, Z = zr });
            }

            res.Regions = res.Regions.OrderByDescending(rg => rg.AreaHa).ToList();
            res.Ok = res.Regions.Count > 0;
            res.Message = res.Ok
                ? $"提取到 {res.Regions.Count} 块达标平盘(宽度 ≥ {wTargetM:0.#}m, 总面积 {res.Regions.Sum(rg => rg.AreaHa):0.#}ha, 栅格 {cellSize:0.#}m)。"
                : $"未找到宽度 ≥ {wTargetM:0.#}m 的平盘(可调小目标宽度/最小面积, 或检查台阶线覆盖)。";
            return res;
        }
        catch (Exception ex) { res.Message = "平盘宽度识别失败：" + ex.Message; return res; }
    }

    /// <summary>可分离窗口高差：window-max − window-min(半径 wR, 先行后列两遍)。</summary>
    private static double[] WindowRange(double[] z, int nx, int ny, int wR)
    {
        var rowMin = new double[z.Length]; var rowMax = new double[z.Length];
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                double mn = double.MaxValue, mx = double.MinValue;
                int x0 = Math.Max(0, x - wR), x1 = Math.Min(nx - 1, x + wR);
                for (int xx = x0; xx <= x1; xx++) { double v = z[y * nx + xx]; if (v < mn) mn = v; if (v > mx) mx = v; }
                rowMin[y * nx + x] = mn; rowMax[y * nx + x] = mx;
            }
        var outRange = new double[z.Length];
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                double mn = double.MaxValue, mx = double.MinValue;
                int y0 = Math.Max(0, y - wR), y1 = Math.Min(ny - 1, y + wR);
                for (int yy = y0; yy <= y1; yy++)
                { if (rowMin[yy * nx + x] < mn) mn = rowMin[yy * nx + x]; if (rowMax[yy * nx + x] > mx) mx = rowMax[yy * nx + x]; }
                outRange[y * nx + x] = mx - mn;
            }
        return outRange;
    }

    /// <summary>掩膜内每格到掩膜外的 8-连通(Chebyshev)距离, 单位 = cell(两遍倒角法)。</summary>
    private static int[] ChamferDistance(bool[] m, int nx, int ny)
    {
        int N = nx * ny; const int INF = 1 << 28;
        var d = new int[N];
        for (int c = 0; c < N; c++) d[c] = m[c] ? INF : 0;
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                int c = y * nx + x; if (d[c] == 0) continue;
                int best = d[c];
                if (x > 0) best = Math.Min(best, d[c - 1] + 1);
                if (y > 0) best = Math.Min(best, d[c - nx] + 1);
                if (x > 0 && y > 0) best = Math.Min(best, d[c - nx - 1] + 1);
                if (x < nx - 1 && y > 0) best = Math.Min(best, d[c - nx + 1] + 1);
                d[c] = best;
            }
        for (int y = ny - 1; y >= 0; y--)
            for (int x = nx - 1; x >= 0; x--)
            {
                int c = y * nx + x; if (d[c] == 0) continue;
                int best = d[c];
                if (x < nx - 1) best = Math.Min(best, d[c + 1] + 1);
                if (y < ny - 1) best = Math.Min(best, d[c + nx] + 1);
                if (x < nx - 1 && y < ny - 1) best = Math.Min(best, d[c + nx + 1] + 1);
                if (x > 0 && y < ny - 1) best = Math.Min(best, d[c + nx - 1] + 1);
                d[c] = best;
            }
        return d;
    }
}
