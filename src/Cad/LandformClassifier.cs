using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 采场/排土场自动识别 —— 忠实移植原 PlanLib.ShortTerm.LandformClassifier(纯 C# 栅格管线, 无内核依赖):
///   ① 台阶足迹 = 台阶线连通范围(加密栅格化 + 闭运算桥接);
///   ② 足迹内极性分采/排: 台阶线高程建 DEM, 残差 = 中尺度平滑现状 − 大尺度趋势面, 凹(&lt;0)=采场·凸(&gt;0)=排土;
///   ③ 连通成块 + 面积过滤 + 外轮廓 + DP 简化 → 区域多边形;
///   ④ 排土块外环带残差为负(被坑壁围) → 内排, 否则外排。
/// 只用台阶线自身高程。输入为各台阶线 flat xyz(不需 crest/toe 标签; 极性由高程残差定)。可单测。
/// </summary>
public static class LandformClassifier
{
    public sealed class Region
    {
        public double[] PolygonXyz = Array.Empty<double>();   // 闭合环 [x,y,z,...]
        public string Category = "pit";                       // pit / external_dump / internal_dump
        public double AreaHa;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public List<Region> Regions = new();
    }

    private readonly struct Ln
    {
        public readonly double[] Xyz; public readonly double RepZ, Cx, Cy;
        public Ln(double[] xyz)
        {
            Xyz = xyz; int n = xyz.Length / 3; double sx = 0, sy = 0, sz = 0;
            for (int i = 0; i < n; i++) { sx += xyz[i * 3]; sy += xyz[i * 3 + 1]; sz += xyz[i * 3 + 2]; }
            Cx = n > 0 ? sx / n : 0; Cy = n > 0 ? sy / n : 0; RepZ = n > 0 ? sz / n : 0;
        }
    }

    public static Result Classify(IReadOnlyList<double[]> linesXyz,
        double cellSize = 5.0, double trendWindowM = 600.0, double bridgeM = 50.0,
        double minAreaHa = 30.0, double threshFrac = 0.5,
        double relAreaFrac = 0.20, int maxPit = 1, bool requirePairs = true, double pairGapM = 80.0)
    {
        var res = new Result();
        try
        {
            var all = (linesXyz ?? Enumerable.Empty<double[]>()).Where(x => x != null && x.Length >= 6).Select(x => new Ln(x)).ToList();
            if (all.Count == 0) { res.Message = "未提供台阶线。"; return res; }

            int pairedDropped = 0;
            if (requirePairs)
            {
                var paired = KeepPairedLines(all, pairGapM, 2.5, 30.0);
                if (paired.Count >= 2) { pairedDropped = all.Count - paired.Count; all = paired; }
            }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var l in all)
                for (int i = 0; i < l.Xyz.Length; i += 3)
                { minX = Math.Min(minX, l.Xyz[i]); maxX = Math.Max(maxX, l.Xyz[i]); minY = Math.Min(minY, l.Xyz[i + 1]); maxY = Math.Max(maxY, l.Xyz[i + 1]); }
            double pad = bridgeM; minX -= pad; minY -= pad; maxX += pad; maxY += pad;

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
                for (int i = 0; i + 5 < l.Xyz.Length; i += 3)
                {
                    double x0 = l.Xyz[i], y0 = l.Xyz[i + 1], z0 = l.Xyz[i + 2], x1 = l.Xyz[i + 3], y1 = l.Xyz[i + 4], z1 = l.Xyz[i + 5];
                    int steps = Math.Max(1, (int)(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / cellSize));
                    for (int s = 0; s <= steps; s++) { double t = (double)s / steps; Stamp(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, z0 + (z1 - z0) * t); }
                }
            var foot0 = new bool[N];
            for (int c = 0; c < N; c++) foot0[c] = seedCnt[c] > 0;
            int br = Math.Max(1, (int)Math.Round(bridgeM / cellSize));
            var foot = Close(foot0, nx, ny, br);
            FillHoles(foot, nx, ny);

            // ② DEM (多源 BFS) + 中尺度平滑 + 趋势面
            var z = new double[N]; var hasZ = new bool[N]; var q = new Queue<int>();
            for (int c = 0; c < N; c++) if (seedCnt[c] > 0) { z[c] = seedSum[c] / seedCnt[c]; hasZ[c] = true; q.Enqueue(c); }
            int[] dxn = { 1, -1, 0, 0, 1, 1, -1, -1 }, dyn = { 0, 0, 1, -1, 1, -1, 1, -1 };
            while (q.Count > 0)
            {
                int c = q.Dequeue(); int cx = c % nx, cy = c / nx;
                for (int k = 0; k < 8; k++)
                {
                    int ax = cx + dxn[k], ay = cy + dyn[k];
                    if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                    int a = ay * nx + ax; if (!hasZ[a]) { z[a] = z[c]; hasZ[a] = true; q.Enqueue(a); }
                }
            }
            var allMask = new bool[N]; Array.Fill(allMask, true);
            var S = BoxBlur(z, allMask, nx, ny, Math.Max(1, (int)Math.Round(40.0 / cellSize)));
            var trend = BoxBlur(z, allMask, nx, ny, Math.Max(1, (int)Math.Round(trendWindowM / cellSize)));
            double[] groundRef = trend;

            double mean = 0; int cnt = 0; var resid = new double[N];
            for (int c = 0; c < N; c++) if (foot[c]) { resid[c] = S[c] - groundRef[c]; mean += resid[c]; cnt++; }
            if (cnt == 0) { res.Message = "台阶足迹为空。"; return res; }
            mean /= cnt; double var0 = 0;
            for (int c = 0; c < N; c++) if (foot[c]) var0 += (resid[c] - mean) * (resid[c] - mean);
            double std = Math.Sqrt(var0 / cnt);
            double T = Math.Max(6.0, threshFrac * std);

            double eps = Math.Min(2.0, 0.25 * T);
            var pit = new bool[N]; var dump = new bool[N];
            for (int c = 0; c < N; c++) if (foot[c]) { if (resid[c] < -eps) pit[c] = true; else if (resid[c] > eps) dump[c] = true; }

            int mergeR = Math.Max(1, (int)Math.Round(140.0 / cellSize));
            pit = Close(pit, nx, ny, mergeR); dump = Close(dump, nx, ny, mergeR);
            for (int c = 0; c < N; c++) { if (!foot[c]) { pit[c] = false; dump[c] = false; } else if (pit[c] && dump[c]) dump[c] = false; }

            // ③ 连通域 + 面积过滤
            double cellHa = cellSize * cellSize / 1e4;
            int minCells = Math.Max(1, (int)(minAreaHa / cellHa));
            var pitComps = FilterByRelArea(Components(pit, nx, ny, minCells), relAreaFrac);
            var dumpComps = FilterByRelArea(Components(dump, nx, ny, minCells), relAreaFrac);
            if (maxPit > 0 && pitComps.Count > maxPit) pitComps = pitComps.OrderByDescending(c => c.Count).Take(maxPit).ToList();

            // ④ 内/外排: 排土块外环带残差
            var residAll = new double[N];
            for (int c = 0; c < N; c++) residAll[c] = S[c] - groundRef[c];
            int ringSteps = Math.Max(3, (int)Math.Round(120.0 / cellSize));
            var ringBuf = new int[N]; for (int c = 0; c < N; c++) ringBuf[c] = -1;
            string DumpCategory(List<int> comp) => RingMeanResidual(comp, ringSteps, residAll, ringBuf, nx, ny) < -0.5 * T ? "internal_dump" : "external_dump";

            var allRegionCells = new bool[N];
            foreach (var comp in pitComps) foreach (int c in comp) allRegionCells[c] = true;
            foreach (var comp in dumpComps) foreach (int c in comp) allRegionCells[c] = true;

            void Emit(List<int> comp, string category)
            {
                var mask = CarveForeignHoles(comp, allRegionCells, nx, ny);
                var ring = TraceBoundary(mask, nx, ny);
                if (ring.Count < 3) return;
                double zr = comp.Average(c => z[c]);
                var simp = Simplify(ring, 1.5);
                var flat = new double[simp.Count * 3];
                for (int i = 0; i < simp.Count; i++) { flat[i * 3] = minX + (simp[i].x + 0.5) * cellSize; flat[i * 3 + 1] = minY + (simp[i].y + 0.5) * cellSize; flat[i * 3 + 2] = zr; }
                res.Regions.Add(new Region { PolygonXyz = flat, Category = category, AreaHa = comp.Count * cellHa });
            }
            foreach (var comp in pitComps) Emit(comp, "pit");
            foreach (var comp in dumpComps) Emit(comp, DumpCategory(comp));

            res.Regions = res.Regions.OrderByDescending(r => r.AreaHa).ToList();
            res.Ok = res.Regions.Count > 0;
            int np = res.Regions.Count(r => r.Category == "pit"), no = res.Regions.Count(r => r.Category == "external_dump"), ni = res.Regions.Count(r => r.Category == "internal_dump");
            string filt = pairedDropped > 0 ? $"，滤除 {pairedDropped} 条孤立线" : "";
            res.Message = res.Ok
                ? $"识别到 采场 {np} 块 / 外排 {no} 块 / 内排 {ni} 块（共 {res.Regions.Count} 块，栅格 {cellSize:0.#}m，趋势面近似{filt}）。"
                : $"未识别到达标的台阶区域（可调最小面积/阈值，或检查台阶线是否成对{filt}）。";
            return res;
        }
        catch (Exception ex) { res.Message = "识别失败：" + ex.Message; return res; }
    }

    // ── 栅格算子(纯 C#) ──
    private static bool[] Dilate(bool[] m, int nx, int ny, int r)
    {
        var cur = (bool[])m.Clone();
        for (int it = 0; it < r; it++)
        {
            var next = (bool[])cur.Clone();
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    int c = y * nx + x; if (cur[c]) continue;
                    if ((x > 0 && cur[c - 1]) || (x < nx - 1 && cur[c + 1]) || (y > 0 && cur[c - nx]) || (y < ny - 1 && cur[c + nx]) ||
                        (x > 0 && y > 0 && cur[c - nx - 1]) || (x < nx - 1 && y > 0 && cur[c - nx + 1]) ||
                        (x > 0 && y < ny - 1 && cur[c + nx - 1]) || (x < nx - 1 && y < ny - 1 && cur[c + nx + 1])) next[c] = true;
                }
            cur = next;
        }
        return cur;
    }

    private static bool[] Erode(bool[] m, int nx, int ny, int r)
    {
        var inv = new bool[m.Length]; for (int i = 0; i < m.Length; i++) inv[i] = !m[i];
        var d = Dilate(inv, nx, ny, r); var o = new bool[m.Length]; for (int i = 0; i < m.Length; i++) o[i] = !d[i]; return o;
    }

    internal static bool[] Close(bool[] m, int nx, int ny, int r) => Erode(Dilate(m, nx, ny, r), nx, ny, r);

    private static double RingMeanResidual(List<int> comp, int steps, double[] resid, int[] depth, int nx, int ny)
    {
        var q = new Queue<int>(); var touched = new List<int>();
        foreach (int c in comp) if (depth[c] != 0) { depth[c] = 0; touched.Add(c); q.Enqueue(c); }
        int[] dxn = { 1, -1, 0, 0, 1, 1, -1, -1 }, dyn = { 0, 0, 1, -1, 1, -1, 1, -1 };
        double sum = 0; int cnt = 0;
        while (q.Count > 0)
        {
            int c = q.Dequeue(); int d = depth[c]; if (d >= steps) continue; int cx = c % nx, cy = c / nx;
            for (int k = 0; k < 8; k++)
            {
                int ax = cx + dxn[k], ay = cy + dyn[k]; if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                int a = ay * nx + ax; if (depth[a] != -1) continue; depth[a] = d + 1; touched.Add(a); q.Enqueue(a); sum += resid[a]; cnt++;
            }
        }
        foreach (int c in touched) depth[c] = -1;
        return cnt > 0 ? sum / cnt : 0;
    }

    internal static void FillHoles(bool[] m, int nx, int ny)
    {
        int N = nx * ny; var outside = new bool[N]; var st = new Stack<int>();
        void Push(int c) { if (c >= 0 && c < N && !m[c] && !outside[c]) { outside[c] = true; st.Push(c); } }
        for (int x = 0; x < nx; x++) { Push(x); Push((ny - 1) * nx + x); }
        for (int y = 0; y < ny; y++) { Push(y * nx); Push(y * nx + nx - 1); }
        while (st.Count > 0) { int c = st.Pop(); int cx = c % nx, cy = c / nx; if (cx > 0) Push(c - 1); if (cx < nx - 1) Push(c + 1); if (cy > 0) Push(c - nx); if (cy < ny - 1) Push(c + nx); }
        for (int c = 0; c < N; c++) if (!m[c] && !outside[c]) m[c] = true;
    }

    private static bool[] CarveForeignHoles(List<int> comp, bool[] otherCells, int nx, int ny)
    {
        int N = nx * ny; var m = new bool[N]; foreach (int c in comp) m[c] = true;
        var ext = new bool[N]; var st = new Stack<int>();
        void PushE(int c) { if (c >= 0 && c < N && !m[c] && !ext[c]) { ext[c] = true; st.Push(c); } }
        for (int x = 0; x < nx; x++) { PushE(x); PushE((ny - 1) * nx + x); }
        for (int y = 0; y < ny; y++) { PushE(y * nx); PushE(y * nx + nx - 1); }
        while (st.Count > 0) { int c = st.Pop(); int cx = c % nx, cy = c / nx; if (cx > 0) PushE(c - 1); if (cx < nx - 1) PushE(c + 1); if (cy > 0) PushE(c - nx); if (cy < ny - 1) PushE(c + nx); }
        var seen = new bool[N]; int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
        for (int s = 0; s < N; s++)
        {
            if (m[s] || ext[s] || seen[s]) continue;
            var hole = new List<int>(); var q = new Queue<int>(); q.Enqueue(s); seen[s] = true; bool foreign = false;
            while (q.Count > 0)
            {
                int c = q.Dequeue(); hole.Add(c); if (otherCells[c]) foreign = true; int cx = c % nx, cy = c / nx;
                for (int k = 0; k < 4; k++) { int ax = cx + dx[k], ay = cy + dy[k]; if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue; int a = ay * nx + ax; if (!m[a] && !ext[a] && !seen[a]) { seen[a] = true; q.Enqueue(a); } }
            }
            if (!foreign) { foreach (int c in hole) m[c] = true; continue; }
            CarveChannelToExterior(m, ext, hole, nx, ny);
        }
        return m;
    }

    private static void CarveChannelToExterior(bool[] m, bool[] ext, List<int> hole, int nx, int ny)
    {
        int N = nx * ny; var pred = new int[N]; Array.Fill(pred, -2); var q = new Queue<int>();
        foreach (int c in hole) { pred[c] = -1; q.Enqueue(c); }
        int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 }; int reach = -1;
        while (q.Count > 0 && reach < 0)
        {
            int c = q.Dequeue(); int cx = c % nx, cy = c / nx;
            for (int k = 0; k < 4; k++)
            {
                int ax = cx + dx[k], ay = cy + dy[k]; if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                int a = ay * nx + ax; if (pred[a] != -2) continue;
                if (ext[a]) { pred[a] = c; reach = a; break; }
                if (m[a]) { pred[a] = c; q.Enqueue(a); }
            }
        }
        if (reach < 0) return;
        var path = new List<int>();
        for (int c = pred[reach]; c >= 0 && pred[c] != -1; c = pred[c]) path.Add(c);
        foreach (int c in path) m[c] = false;
        foreach (int c in new List<int>(path)) { int cx = c % nx, cy = c / nx; for (int k = 0; k < 4; k++) { int ax = cx + dx[k], ay = cy + dy[k]; if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue; int a = ay * nx + ax; if (m[a]) m[a] = false; } }
    }

    private static double[] BoxBlur(double[] z, bool[] foot, int nx, int ny, int w)
    {
        int W = nx + 1, H = ny + 1; var satZ = new double[W * H]; var satC = new double[W * H];
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                int c = y * nx + x; double zv = foot[c] ? z[c] : 0; double cv = foot[c] ? 1 : 0; int s = (y + 1) * W + (x + 1);
                satZ[s] = zv + satZ[s - 1] + satZ[s - W] - satZ[s - W - 1]; satC[s] = cv + satC[s - 1] + satC[s - W] - satC[s - W - 1];
            }
        double Sum(double[] sat, int x0, int y0, int x1, int y1) => sat[(y1 + 1) * W + (x1 + 1)] - sat[(y1 + 1) * W + x0] - sat[y0 * W + (x1 + 1)] + sat[y0 * W + x0];
        var outz = new double[nx * ny];
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                int c = y * nx + x; if (!foot[c]) continue;
                int x0 = Math.Max(0, x - w), y0 = Math.Max(0, y - w), x1 = Math.Min(nx - 1, x + w), y1 = Math.Min(ny - 1, y + w);
                double sc = Sum(satC, x0, y0, x1, y1); outz[c] = sc > 0 ? Sum(satZ, x0, y0, x1, y1) / sc : z[c];
            }
        return outz;
    }

    internal static List<List<int>> Components(bool[] m, int nx, int ny, int minCells)
    {
        int N = nx * ny; var seen = new bool[N]; var outc = new List<List<int>>();
        int[] dxn = { 1, -1, 0, 0, 1, 1, -1, -1 }, dyn = { 0, 0, 1, -1, 1, -1, 1, -1 }; var st = new Stack<int>();
        for (int s = 0; s < N; s++)
        {
            if (!m[s] || seen[s]) continue;
            var comp = new List<int>(); st.Push(s); seen[s] = true;
            while (st.Count > 0)
            {
                int c = st.Pop(); comp.Add(c); int cx = c % nx, cy = c / nx;
                for (int k = 0; k < 8; k++) { int ax = cx + dxn[k], ay = cy + dyn[k]; if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue; int a = ay * nx + ax; if (m[a] && !seen[a]) { seen[a] = true; st.Push(a); } }
            }
            if (comp.Count >= minCells) outc.Add(comp);
        }
        return outc;
    }

    private static List<Ln> KeepPairedLines(List<Ln> lines, double gap, double dzMin, double dzMax)
    {
        var keep = new List<Ln>(lines.Count); double gap2 = gap * gap, pre2 = (gap + 400) * (gap + 400);
        for (int i = 0; i < lines.Count; i++)
        {
            var a = lines[i]; bool paired = false;
            for (int j = 0; j < lines.Count && !paired; j++)
            {
                if (j == i) continue; double dz = Math.Abs(a.RepZ - lines[j].RepZ); if (dz < dzMin || dz > dzMax) continue;
                double cdx = a.Cx - lines[j].Cx, cdy = a.Cy - lines[j].Cy; if (cdx * cdx + cdy * cdy > pre2) continue;
                if (AnyVertexWithin(a.Xyz, lines[j].Xyz, gap2)) paired = true;
            }
            if (paired) keep.Add(a);
        }
        return keep;
    }

    private static bool AnyVertexWithin(double[] a, double[] b, double thr2)
    {
        int na = a.Length / 3, nb = b.Length / 3;
        for (int i = 0; i < na; i++) { double ax = a[i * 3], ay = a[i * 3 + 1]; for (int j = 0; j < nb; j++) { double dx = ax - b[j * 3], dy = ay - b[j * 3 + 1]; if (dx * dx + dy * dy <= thr2) return true; } }
        return false;
    }

    private static List<List<int>> FilterByRelArea(List<List<int>> comps, double relFrac)
    {
        if (comps.Count == 0 || relFrac <= 0) return comps;
        int max = 0; foreach (var c in comps) if (c.Count > max) max = c.Count;
        double thr = relFrac * max; var outc = new List<List<int>>(); foreach (var c in comps) if (c.Count >= thr) outc.Add(c); return outc;
    }

    internal static List<(int x, int y)> TraceBoundary(bool[] m, int nx, int ny)
    {
        int N = nx * ny; int start = -1; for (int c = 0; c < N; c++) if (m[c]) { start = c; break; }
        var ring = new List<(int x, int y)>(); if (start < 0) return ring;
        int sx = start % nx, sy = start / nx; int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 }, dy = { 0, -1, -1, -1, 0, 1, 1, 1 };
        bool In(int x, int y) => x >= 0 && y >= 0 && x < nx && y < ny && m[y * nx + x];
        int px = sx, py = sy, dir = 0; ring.Add((sx, sy)); int guard = 0, maxGuard = N * 8 + 16;
        while (guard++ < maxGuard)
        {
            bool found = false;
            for (int k = 0; k < 8; k++)
            {
                int nd = (dir + k) % 8; int ax = px + dx[nd], ay = py + dy[nd];
                if (In(ax, ay)) { px = ax; py = ay; ring.Add((px, py)); dir = (nd + 6) % 8; found = true; break; }
            }
            if (!found) break; if (px == sx && py == sy) break;
        }
        return ring;
    }

    internal static List<(int x, int y)> Simplify(List<(int x, int y)> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count]; keep[0] = keep[pts.Count - 1] = true; var st = new Stack<(int a, int b)>(); st.Push((0, pts.Count - 1));
        while (st.Count > 0)
        {
            var (a, b) = st.Pop(); double maxd = -1; int idx = -1;
            double ax = pts[a].x, ay = pts[a].y, bx = pts[b].x, by = pts[b].y; double dxl = bx - ax, dyl = by - ay; double len = Math.Sqrt(dxl * dxl + dyl * dyl);
            for (int i = a + 1; i < b; i++)
            {
                double d = len < 1e-9 ? Math.Sqrt((pts[i].x - ax) * (pts[i].x - ax) + (pts[i].y - ay) * (pts[i].y - ay)) : Math.Abs(dxl * (ay - pts[i].y) - (ax - pts[i].x) * dyl) / len;
                if (d > maxd) { maxd = d; idx = i; }
            }
            if (maxd > tol && idx > 0) { keep[idx] = true; st.Push((a, idx)); st.Push((idx, b)); }
        }
        var outp = new List<(int x, int y)>(); for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]); return outp;
    }
}
