using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 工作面线【按趋势修正】—— 把格网扫出的露煤格拟合成一条可用作驱动线的折线。移植自原
/// BlockModelLib.Domain.WorkingFaceLineFitter（纯几何、无副作用、可单测；仅依赖 System/Linq）。
///
/// 露煤格来自 10m 格网 + 现状面采样，天生带三类错误：① 锯齿(量化台阶) ② 毛刺(局部噪声鼓尖)
/// ③ 斑点(孤立几格把线拽远)。修正：点投到 PCA 走向轴排序 → 垂直走向方向做局部趋势回归 →
/// 偏离趋势带的点按局部趋势拉回，拉不回的丢弃 → 沿走向等距重采样抹锯齿。
/// </summary>
public static class WorkingFaceLineFitter
{
    public sealed class Options
    {
        /// <summary>孤立斑点阈值：连通块点数少于此值直接丢（格数）。</summary>
        public int MinClusterPoints = 4;
        /// <summary>连通判定距离（m）：点间距超此即不连通。建议 1.5~2 倍格网。</summary>
        public double LinkDistM = 18.0;
        /// <summary>趋势窗口（沿走向，m）：窗口内做局部线性回归定趋势。</summary>
        public double TrendWindowM = 80.0;
        /// <summary>离群阈值（倍）：点到局部趋势垂距 &gt; 此倍数 × 窗口内中位残差 即离群。</summary>
        public double OutlierFactor = 3.0;
        /// <summary>离群点处理：true=拉回趋势线；false=丢弃。</summary>
        public bool PullBackOutliers = true;
        /// <summary>重采样间距（m）；&lt;=0=不重采样。</summary>
        public double ResampleStepM = 20.0;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        /// <summary>修正后折线（可能多段：工作面被道路/未采区隔断时）。各段 [x0,y0,x1,y1,...]。</summary>
        public List<double[]> Segments = new();
        /// <summary>走向方位角（°，0=正北顺时针），由 PCA 求。</summary>
        public double StrikeAzimuthDeg;
        public int InputPoints, DroppedSpeckle, PulledBack, DroppedOutlier;
        public List<string> Warnings = new();
    }

    /// <summary>修正。pts=露煤格中心 XY。任何异常降级为 Ok=false，不抛。</summary>
    public static Result Fit(IReadOnlyList<(double X, double Y)>? pts, Options? opt = null)
    {
        opt ??= new Options();
        var res = new Result();
        if (pts == null || pts.Count < 2) { res.Message = "露煤点不足 2 个，拟合不出工作面线。"; return res; }
        res.InputPoints = pts.Count;

        // ── ① 主方向（走向）：PCA 取最大方差方向 ──
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in pts)
        {
            double dx = p.X - mx, dy = p.Y - my;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        double tr = sxx + syy, det = sxx * syy - sxy * sxy;
        double lam = tr * 0.5 + Math.Sqrt(Math.Max(0, tr * tr * 0.25 - det));
        double ux, uy;
        if (Math.Abs(sxy) > 1e-9) { ux = lam - syy; uy = sxy; }
        else { ux = sxx >= syy ? 1 : 0; uy = sxx >= syy ? 0 : 1; }
        double ul = Math.Sqrt(ux * ux + uy * uy);
        if (ul < 1e-12) { res.Message = "露煤点退化成一点，定不出走向。"; return res; }
        ux /= ul; uy /= ul;
        res.StrikeAzimuthDeg = (Math.Atan2(ux, uy) * 180.0 / Math.PI + 360.0) % 360.0;

        (double S, double T, int I)[] proj = pts.Select((p, i) =>
            ((p.X - mx) * ux + (p.Y - my) * uy, -(p.X - mx) * uy + (p.Y - my) * ux, i)).ToArray();
        Array.Sort(proj, (a, b) => a.S.CompareTo(b.S));

        // ── ② 沿走向切连通块，丢孤立斑点 ──
        var clusters = new List<List<(double S, double T, int I)>>();
        var cur = new List<(double S, double T, int I)> { proj[0] };
        for (int i = 1; i < proj.Length; i++)
        {
            if (proj[i].S - proj[i - 1].S > opt.LinkDistM)
            { clusters.Add(cur); cur = new List<(double, double, int)>(); }
            cur.Add(proj[i]);
        }
        clusters.Add(cur);

        var kept = new List<List<(double S, double T, int I)>>();
        foreach (var c in clusters)
        {
            if (c.Count < opt.MinClusterPoints) { res.DroppedSpeckle += c.Count; continue; }
            kept.Add(c);
        }
        if (kept.Count == 0)
        {
            res.Message = $"露煤点全是孤立斑点（各段都不足 {opt.MinClusterPoints} 点），拼不出工作面线。";
            return res;
        }

        // ── ③ 逐段按趋势修正 ──
        foreach (var c in kept)
        {
            var fixedPts = new List<(double S, double T)>(c.Count);
            for (int i = 0; i < c.Count; i++)
            {
                double s0 = c[i].S;
                var win = c.Where(q => Math.Abs(q.S - s0) <= opt.TrendWindowM * 0.5).ToList();
                if (win.Count < 3) { fixedPts.Add((c[i].S, c[i].T)); continue; }

                double ws = win.Average(q => q.S), wt = win.Average(q => q.T);
                double num = 0, den = 0;
                foreach (var q in win) { num += (q.S - ws) * (q.T - wt); den += (q.S - ws) * (q.S - ws); }
                double a = den > 1e-9 ? num / den : 0;
                double b = wt - a * ws;

                var resid = win.Select(q => Math.Abs(q.T - (a * q.S + b))).OrderBy(v => v).ToList();
                double med = resid[resid.Count / 2];
                double tol = Math.Max(1e-6, med * opt.OutlierFactor);

                double trend = a * s0 + b;
                double dev = Math.Abs(c[i].T - trend);
                if (dev <= tol) { fixedPts.Add((c[i].S, c[i].T)); continue; }

                if (opt.PullBackOutliers) { fixedPts.Add((c[i].S, trend)); res.PulledBack++; }
                else res.DroppedOutlier++;
            }
            if (fixedPts.Count < 2) continue;

            // ── ④ 沿走向等距重采样，抹掉格网锯齿 ──
            var outPts = opt.ResampleStepM > 1e-6 ? Resample(fixedPts, opt.ResampleStepM) : fixedPts;
            if (outPts.Count < 2) continue;

            var flat = new double[outPts.Count * 2];
            for (int i = 0; i < outPts.Count; i++)
            {
                flat[i * 2]     = mx + outPts[i].S * ux - outPts[i].T * uy;
                flat[i * 2 + 1] = my + outPts[i].S * uy + outPts[i].T * ux;
            }
            res.Segments.Add(flat);
        }

        if (res.Segments.Count == 0)
        {
            res.Message = "修正后各段都不足 2 点，拼不出工作面线。";
            return res;
        }

        res.Ok = true;
        double totalLen = res.Segments.Sum(PlanLength);
        res.Message = $"工作面线：{res.Segments.Count} 段，合计 {totalLen:N0} m，走向 {res.StrikeAzimuthDeg:0.#}°（输入 {res.InputPoints:N0} 点）。";
        if (res.DroppedSpeckle > 0)
            res.Warnings.Add($"丢弃孤立斑点 {res.DroppedSpeckle} 点（各自不足 {opt.MinClusterPoints} 点成段）。");
        if (res.PulledBack > 0)
            res.Warnings.Add($"{res.PulledBack} 个点偏离局部趋势超 {opt.OutlierFactor:0.#} 倍中位残差，已按趋势拉回。");
        if (res.DroppedOutlier > 0)
            res.Warnings.Add($"{res.DroppedOutlier} 个离群点已丢弃。");
        if (res.Segments.Count > 1)
            res.Warnings.Add($"工作面被隔成 {res.Segments.Count} 段（道路/未采区/尖灭），每段各出一条采掘带。");
        return res;
    }

    /// <summary>沿 S 等距重采样，T 线性插值。</summary>
    private static List<(double S, double T)> Resample(List<(double S, double T)> src, double step)
    {
        var outPts = new List<(double, double)>();
        if (src.Count < 2) return outPts;
        double s0 = src[0].S, s1 = src[^1].S;
        if (s1 - s0 < step) return src;

        int seg = 1;
        for (double s = s0; s <= s1 + 1e-9; s += step)
        {
            while (seg < src.Count - 1 && src[seg].S < s) seg++;
            var p0 = src[seg - 1];
            var p1 = src[seg];
            double d = p1.S - p0.S;
            double f = d > 1e-9 ? (s - p0.S) / d : 0;
            outPts.Add((s, p0.T + f * (p1.T - p0.T)));
        }
        return outPts;
    }

    private static double PlanLength(double[] flat)
    {
        double len = 0;
        for (int i = 1; i < flat.Length / 2; i++)
        {
            double dx = flat[i * 2] - flat[(i - 1) * 2];
            double dy = flat[i * 2 + 1] - flat[(i - 1) * 2 + 1];
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }
}
