using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 煤层露头线抽取：在【现状面三角网】上求 现状Z − 顶板Z = 0 与 现状Z − 底板Z = 0 两条等值线 ——
/// 前者是坡顶线（顶板露头），后者是坡底线（底板露头）。采煤台阶的坡顶/坡底就是这两条露头线。
///
/// 露头线是【算出来的】：两条线天然同属一层煤、天然一一对应，错配从根上不存在（区别 BenchFaceExtractor
/// 的坡度式、StandardLevelModel 的线配对式）。水平间距 = 煤厚 / tan(现状面坡度), 沿走向变化正常。
/// 算法 = marching triangles: 逐三角看标量 f 三顶点符号, 变号边线性插值取点, 一三角最多一段, 焊接成折线。
/// 顶/底板取 Z 由调用方传采样器(Kylin: 层位展点建 seam TIN → TinSampler)。纯几何、不依赖引擎、可单测。
/// 忠实原 BlockModelLib.SeamOutcropLineExtractor。
/// </summary>
public static class SeamOutcropLineExtractor
{
    /// <summary>抽出的一条露头折线（世界坐标扁平 [x,y,z,...]）。</summary>
    public sealed class OutcropLine
    {
        public double[] Xyz = Array.Empty<double>();
        public bool Closed;
        /// <summary>平面(XY)长度，m。</summary>
        public double PlanLengthM;
        public int PointCount => Xyz.Length / 3;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public List<OutcropLine> CrestLines = new();   // 坡顶线（顶板露头）
        public List<OutcropLine> ToeLines = new();      // 坡底线（底板露头）
        public List<string> Warnings = new();
    }

    public sealed class Options
    {
        /// <summary>平面长度小于此值的碎线丢弃（m）。0 = 不筛。</summary>
        public double MinLengthM = 20.0;
        /// <summary>折线焊接容差（m）。</summary>
        public double WeldTolM = 0.01;
        /// <summary>抽稀容差（m，Douglas-Peucker）。0 = 不抽稀。</summary>
        public double SimplifyTolM = 0.5;
        /// <summary>单条折线的点数上限；超了加大抽稀容差重来。0 = 不限。</summary>
        public int MaxPointsPerLine = 500;
    }

    /// <summary>在 (x,y) 采一个面的 Z；返回 false = 该处无数据。</summary>
    public delegate bool SampleZ(double x, double y, out double z);

    /// <summary>
    /// 抽取。surfVerts/surfTris = 现状面三角网; roofZ/floorZ = 顶/底板取 Z 采样器。异常降级 Ok=false, 不抛。
    /// </summary>
    public static Result Extract(double[]? surfVerts, int[]? surfTris,
                                 SampleZ roofZ, SampleZ floorZ, Options? opt = null)
    {
        opt ??= new Options();
        var res = new Result();
        if (surfVerts == null || surfVerts.Length < 9 || surfTris == null || surfTris.Length < 3)
        { res.Message = "现状面三角网为空或不合法。"; return res; }
        if (roofZ == null || floorZ == null) { res.Message = "顶/底板采样器为空。"; return res; }

        int nv = surfVerts.Length / 3;

        // 逐顶点算两个标量场：f_roof = 现状Z − 顶板Z，f_floor = 现状Z − 底板Z。采不到标 NaN → 含 NaN 三角整体跳过。
        var fRoof = new double[nv];
        var fFloor = new double[nv];
        int noData = 0;
        for (int i = 0; i < nv; i++)
        {
            double x = surfVerts[i * 3], y = surfVerts[i * 3 + 1], z = surfVerts[i * 3 + 2];
            fRoof[i] = roofZ(x, y, out double zr) ? z - zr : double.NaN;
            fFloor[i] = floorZ(x, y, out double zf) ? z - zf : double.NaN;
            if (double.IsNaN(fRoof[i]) || double.IsNaN(fFloor[i])) noData++;
        }

        res.CrestLines = BuildIsoLines(surfVerts, surfTris, fRoof, opt);
        res.ToeLines = BuildIsoLines(surfVerts, surfTris, fFloor, opt);

        if (noData > 0)
            res.Warnings.Add($"{noData:N0}/{nv:N0} 个现状顶点采不到顶板或底板（超出面范围），露头线会在那里断开。");

        if (res.CrestLines.Count == 0 && res.ToeLines.Count == 0)
        {
            res.Message = "没抽到任何露头线：现状面与该层顶/底板可能不相交（整层已采完/尚未揭露），或 XY 范围不重叠。";
            return res;
        }

        res.Ok = true;
        res.Message = $"露头线：坡顶 {res.CrestLines.Count} 条、坡底 {res.ToeLines.Count} 条"
                    + $"（坡顶合计 {res.CrestLines.Sum(l => l.PlanLengthM):N0}m，坡底合计 {res.ToeLines.Sum(l => l.PlanLengthM):N0}m）。";
        if (res.CrestLines.Count == 0) res.Warnings.Add("只有坡底线没有坡顶线：顶板整体在现状面之上（该层尚未揭露顶板）。");
        if (res.ToeLines.Count == 0) res.Warnings.Add("只有坡顶线没有坡底线：底板整体在现状面之下（该层尚未挖到底板）。");
        return res;
    }

    /// <summary>配好对的一条露头带：上边界 = 坡顶线，下边界 = 坡底线。</summary>
    public sealed class OutcropBand
    {
        public OutcropLine Crest = null!;
        public OutcropLine Toe = null!;
        /// <summary>两线的中位水平间距（m）= 煤厚 / tan(现状面坡度)。</summary>
        public double MedianSpacingM;
        public double LengthM => Crest.PlanLengthM;
    }

    /// <summary>
    /// 把坡顶线与坡底线按【并行性】配成露头带（不按最近距离——那正是配错的根源）。
    /// 沿坡顶线取点求到候选坡底线距离, 要求 ①中位数落在 [煤厚/tan60°, 煤厚/tan5°] ②离散度(四分位距/中位)小。
    /// 一对一消费, 配不上的单独报。
    /// </summary>
    public static List<OutcropBand> PairIntoBands(
        IReadOnlyList<OutcropLine> crests, IReadOnlyList<OutcropLine> toes,
        double seamThickM, out List<string> notes, double maxSpreadRatio = 0.6)
    {
        notes = new List<string>();
        var bands = new List<OutcropBand>();
        if (crests == null || toes == null || crests.Count == 0 || toes.Count == 0) return bands;

        double thick = Math.Max(0.1, seamThickM);
        double loSpacing = thick / Math.Tan(60.0 * Math.PI / 180.0);
        double hiSpacing = thick / Math.Tan(5.0 * Math.PI / 180.0);

        var cands = new List<(double Score, double Med, int Ci, int Ti)>();
        for (int ci = 0; ci < crests.Count; ci++)
            for (int ti = 0; ti < toes.Count; ti++)
            {
                var ds = SampleDistances(crests[ci], toes[ti], 24);
                if (ds.Count == 0) continue;
                ds.Sort();
                double med = Median(ds);
                if (med < loSpacing || med > hiSpacing) continue;
                double spread = Percentile(ds, 75) - Percentile(ds, 25);
                double ratio = med > 1e-9 ? spread / med : double.MaxValue;
                if (ratio > maxSpreadRatio) continue;
                cands.Add((ratio, med, ci, ti));
            }

        cands.Sort((a, b) => a.Score.CompareTo(b.Score));
        var usedC = new HashSet<int>();
        var usedT = new HashSet<int>();
        foreach (var (score, med, ci, ti) in cands)
        {
            if (!usedC.Add(ci)) continue;
            if (!usedT.Add(ti)) { usedC.Remove(ci); continue; }
            bands.Add(new OutcropBand { Crest = crests[ci], Toe = toes[ti], MedianSpacingM = med });
        }

        int lostC = crests.Count - usedC.Count, lostT = toes.Count - usedT.Count;
        if (lostC > 0) notes.Add($"{lostC} 条坡顶线没配上坡底线（该段底板可能还没挖到，或被道路/未采区隔断）。");
        if (lostT > 0) notes.Add($"{lostT} 条坡底线没配上坡顶线（该段顶板可能已剥完）。");
        if (bands.Count == 0 && crests.Count > 0 && toes.Count > 0)
            notes.Add($"一条带都没配成：间距应在 {loSpacing:0.#}~{hiSpacing:0.#}m 内且沿走向平稳。");

        bands.Sort((a, b) => b.LengthM.CompareTo(a.LengthM));
        return bands;
    }

    private static void Simplify(OutcropLine line, Options opt)
    {
        if (opt.SimplifyTolM <= 0 && opt.MaxPointsPerLine <= 0) return;
        int n = line.PointCount;
        if (n <= 2) return;

        double tol = Math.Max(1e-6, opt.SimplifyTolM);
        for (int round = 0; round < 12; round++)
        {
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            DouglasPeucker(line.Xyz, 0, n - 1, tol, keep);

            int kept = 0;
            for (int i = 0; i < n; i++) if (keep[i]) kept++;

            if (opt.MaxPointsPerLine <= 0 || kept <= opt.MaxPointsPerLine || round == 11)
            {
                var xyz = new double[kept * 3];
                int w = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!keep[i]) continue;
                    xyz[w * 3] = line.Xyz[i * 3];
                    xyz[w * 3 + 1] = line.Xyz[i * 3 + 1];
                    xyz[w * 3 + 2] = line.Xyz[i * 3 + 2];
                    w++;
                }
                line.Xyz = xyz;
                return;
            }
            tol *= 1.6;
        }
    }

    private static void DouglasPeucker(double[] xyz, int lo, int hi, double tol, bool[] keep)
    {
        if (hi <= lo + 1) return;
        double ax = xyz[lo * 3], ay = xyz[lo * 3 + 1];
        double bx = xyz[hi * 3], by = xyz[hi * 3 + 1];
        double best = -1; int bestI = -1;
        for (int i = lo + 1; i < hi; i++)
        {
            double d = PointSegDistXY(xyz[i * 3], xyz[i * 3 + 1], ax, ay, bx, by);
            if (d > best) { best = d; bestI = i; }
        }
        if (bestI < 0 || best <= tol) return;
        keep[bestI] = true;
        DouglasPeucker(xyz, lo, bestI, tol, keep);
        DouglasPeucker(xyz, bestI, hi, tol, keep);
    }

    /// <summary>沿 a 等弧长取 n 点，各求到 b 的最近距离。</summary>
    private static List<double> SampleDistances(OutcropLine a, OutcropLine b, int n)
    {
        var outD = new List<double>();
        int na = a.PointCount, nb = b.PointCount;
        if (na < 2 || nb < 2) return outD;
        int step = Math.Max(1, na / Math.Max(2, n));
        for (int i = 0; i < na; i += step)
        {
            double px = a.Xyz[i * 3], py = a.Xyz[i * 3 + 1];
            double best = double.MaxValue;
            for (int j = 1; j < nb; j++)
            {
                double d = PointSegDistXY(px, py,
                    b.Xyz[(j - 1) * 3], b.Xyz[(j - 1) * 3 + 1], b.Xyz[j * 3], b.Xyz[j * 3 + 1]);
                if (d < best) best = d;
            }
            if (best < double.MaxValue) outD.Add(best);
        }
        return outD;
    }

    private static double PointSegDistXY(double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double len2 = vx * vx + vy * vy;
        double t = len2 > 1e-18 ? ((px - ax) * vx + (py - ay) * vy) / len2 : 0.0;
        t = t < 0 ? 0 : (t > 1 ? 1 : t);
        double qx = ax + t * vx, qy = ay + t * vy;
        return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }

    private static double Median(List<double> sorted) => Percentile(sorted, 50);

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double idx = p / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        double f = idx - lo;
        return sorted[lo] * (1 - f) + sorted[hi] * f;
    }

    // ── marching triangles ────────────────────────────────────────
    private static List<OutcropLine> BuildIsoLines(double[] v, int[] tris, double[] f, Options opt)
    {
        var segs = new List<(double ax, double ay, double az, double bx, double by, double bz)>();

        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0) continue;
            if (i0 >= f.Length || i1 >= f.Length || i2 >= f.Length) continue;
            double f0 = f[i0], f1 = f[i1], f2 = f[i2];
            if (double.IsNaN(f0) || double.IsNaN(f1) || double.IsNaN(f2)) continue;

            var pts = new List<(double X, double Y, double Z)>(3);
            void Edge(int a, int b, double fa, double fb)
            {
                if ((fa < 0 && fb < 0) || (fa > 0 && fb > 0)) return;   // 同号，不过零
                if (fa == fb) return;
                double s = fa / (fa - fb);                               // fa + s·(fb−fa) = 0
                if (s < 0 || s > 1) return;
                pts.Add((v[a * 3]     + s * (v[b * 3]     - v[a * 3]),
                         v[a * 3 + 1] + s * (v[b * 3 + 1] - v[a * 3 + 1]),
                         v[a * 3 + 2] + s * (v[b * 3 + 2] - v[a * 3 + 2])));
            }
            Edge(i0, i1, f0, f1);
            Edge(i1, i2, f1, f2);
            Edge(i2, i0, f2, f0);

            if (pts.Count >= 2)
            {
                double sx = pts[0].X - pts[1].X, sy = pts[0].Y - pts[1].Y, sz = pts[0].Z - pts[1].Z;
                if (sx * sx + sy * sy + sz * sz > 1e-12)
                    segs.Add((pts[0].X, pts[0].Y, pts[0].Z, pts[1].X, pts[1].Y, pts[1].Z));
            }
        }

        // 去重：等值线穿网格顶点时相邻三角会各出一条完全相同的段, 贪心焊接会沿原路折回。按无序端点对去重(1mm 量化)。
        if (segs.Count > 1)
        {
            var seen = new HashSet<(long, long, long, long)>();
            var uniq = new List<(double ax, double ay, double az, double bx, double by, double bz)>(segs.Count);
            const double q = 1e3;
            foreach (var s in segs)
            {
                long ax = (long)Math.Round(s.ax * q), ay = (long)Math.Round(s.ay * q);
                long bx = (long)Math.Round(s.bx * q), by = (long)Math.Round(s.by * q);
                var key = (ax < bx || (ax == bx && ay <= by)) ? (ax, ay, bx, by) : (bx, by, ax, ay);
                if (seen.Add(key)) uniq.Add(s);
            }
            segs = uniq;
        }

        return WeldSegments(segs, opt);
    }

    private static List<OutcropLine> WeldSegments(
        List<(double ax, double ay, double az, double bx, double by, double bz)> segs, Options opt)
    {
        var outLines = new List<OutcropLine>();
        if (segs.Count == 0) return outLines;

        double tol = Math.Max(1e-9, opt.WeldTolM);
        double tol2 = tol * tol;
        var used = new bool[segs.Count];

        bool Near(double x1, double y1, double z1, double x2, double y2, double z2)
        {
            double dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
            return dx * dx + dy * dy + dz * dz <= tol2;
        }

        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var chain = new List<(double X, double Y, double Z)>
            {
                (segs[i].ax, segs[i].ay, segs[i].az),
                (segs[i].bx, segs[i].by, segs[i].bz),
            };

            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int j = 0; j < segs.Count; j++)
                {
                    if (used[j]) continue;
                    var s = segs[j];
                    var tail = chain[^1];
                    var head = chain[0];

                    if (Near(tail.X, tail.Y, tail.Z, s.ax, s.ay, s.az))
                    { chain.Add((s.bx, s.by, s.bz)); used[j] = true; grew = true; }
                    else if (Near(tail.X, tail.Y, tail.Z, s.bx, s.by, s.bz))
                    { chain.Add((s.ax, s.ay, s.az)); used[j] = true; grew = true; }
                    else if (Near(head.X, head.Y, head.Z, s.ax, s.ay, s.az))
                    { chain.Insert(0, (s.bx, s.by, s.bz)); used[j] = true; grew = true; }
                    else if (Near(head.X, head.Y, head.Z, s.bx, s.by, s.bz))
                    { chain.Insert(0, (s.ax, s.ay, s.az)); used[j] = true; grew = true; }
                }
            }

            var line = new OutcropLine { Closed = chain.Count > 2 && Near(chain[0].X, chain[0].Y, chain[0].Z, chain[^1].X, chain[^1].Y, chain[^1].Z) };
            var xyz = new double[chain.Count * 3];
            double len = 0;
            for (int k = 0; k < chain.Count; k++)
            {
                xyz[k * 3] = chain[k].X; xyz[k * 3 + 1] = chain[k].Y; xyz[k * 3 + 2] = chain[k].Z;
                if (k > 0)
                {
                    double dx = chain[k].X - chain[k - 1].X, dy = chain[k].Y - chain[k - 1].Y;
                    len += Math.Sqrt(dx * dx + dy * dy);
                }
            }
            line.Xyz = xyz;
            line.PlanLengthM = len;

            if (opt.MinLengthM > 0 && len < opt.MinLengthM) continue;
            Simplify(line, opt);
            outLines.Add(line);
        }

        outLines.Sort((a, b) => b.PlanLengthM.CompareTo(a.PlanLengthM));
        return outLines;
    }
}
