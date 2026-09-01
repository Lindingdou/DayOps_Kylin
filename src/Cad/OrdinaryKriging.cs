using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 普通克里金 OK 空间估值（忠实移植原 CoalQualityEstimator 的 OK 核）——球状变差函数(自动拟合)，
/// 解 (k+1) 阶克里金方程组，出估计值 + 克里金方差。控制点上精确内插；搜索半径外返回 null(不赋值)。
/// 纯逻辑、可单测。IDW/NN/MA 见 Contour.GridInto / Estimation。
/// </summary>
public static class OrdinaryKriging
{
    public readonly record struct ControlPoint(double X, double Y, double Z, double V);

    /// <summary>变差函数模型(忠实原 EstimationAlgorithms.Gamma 三型)。</summary>
    public enum VariogramModel { Spherical, Exponential, Gaussian }

    public sealed record Variogram(double Nugget, double Sill, double Range, VariogramModel Model = VariogramModel.Spherical)
    {
        /// <summary>模型 γ(h)。Spherical: h≥range 即 sill; Exponential/Gaussian: 3·range 实用变程渐近。忠实原三型公式。</summary>
        public double Gamma(double h)
        {
            if (h <= 1e-9) return 0;
            double c = Sill - Nugget;
            switch (Model)
            {
                case VariogramModel.Exponential:
                    return Nugget + c * (1.0 - Math.Exp(-3.0 * h / Range));
                case VariogramModel.Gaussian:
                    double tg = h / Range;
                    return Nugget + c * (1.0 - Math.Exp(-3.0 * tg * tg));
                default:   // Spherical
                    if (h >= Range) return Sill;
                    double t = h / Range;
                    return Nugget + c * (1.5 * t - 0.5 * t * t * t);
            }
        }
    }

    /// <summary>
    /// 挑最佳变差函数模型: 用 <see cref="FitVariogram"/> 估 nugget/sill/range, 再让三型各评对实验 γ(h) 的残差平方和,
    /// 取最小者。返回(最佳模型 vg, 三型 SSE[球/指数/高斯])。不同矿床空间相关形状各异, 自动选型比固定球状更贴。
    /// </summary>
    public static (Variogram best, double sseSph, double sseExp, double sseGauss) SelectVariogramModel(
        IReadOnlyList<ControlPoint> pts, double maxLag = 0, int lagCount = 12)
    {
        var baseVg = FitSpherical(pts, maxLag, lagCount);   // LSQ 拟合(忠实原 FitSpherical), 优于矩法启发式作基准
        var exp = ExperimentalVariogram(pts, maxLag, lagCount);
        double Sse(VariogramModel m)
        {
            var v = baseVg with { Model = m };
            double s = 0; int n = 0;
            foreach (var lag in exp) if (lag.Count > 0) { double d = v.Gamma(lag.H) - lag.Gamma; s += d * d; n++; }
            return n > 0 ? s : double.MaxValue;
        }
        double sph = Sse(VariogramModel.Spherical), ex = Sse(VariogramModel.Exponential), ga = Sse(VariogramModel.Gaussian);
        var bestM = (sph <= ex && sph <= ga) ? VariogramModel.Spherical
                  : (ex <= ga) ? VariogramModel.Exponential : VariogramModel.Gaussian;
        return (baseVg with { Model = bestM }, sph, ex, ga);
    }

    /// <summary>在目标点 (x,y,z) 估值。k=最大邻数, radius=搜索半径(≤0 自动)。半径外返回 null。</summary>
    public static (double est, double variance)? EstimateAt(
        IReadOnlyList<ControlPoint> points, double x, double y, double z, int k = 12, double radius = 0, Variogram? vg = null)
    {
        if (points.Count == 0) return null;
        if (radius <= 0) radius = AutoRadius(points);
        vg ??= FitVariogram(points);
        var neigh = new List<(double d, ControlPoint pt)>(points.Count);
        foreach (var pt in points)
        {
            double dx = pt.X - x, dy = pt.Y - y, dz = pt.Z - z;
            neigh.Add((Math.Sqrt(dx * dx + dy * dy + dz * dz), pt));
        }
        neigh.Sort((a, b) => a.d.CompareTo(b.d));
        if (neigh[0].d > radius) return null;                 // 无数据支撑
        if (neigh[0].d < 1e-6) return (neigh[0].pt.V, 0);     // 正落控制点上
        var top = neigh.GetRange(0, Math.Min(Math.Max(1, k), neigh.Count));
        return Krige(top, vg);
    }

    /// <summary>
    /// 反距离权重 IDW 估值（忠实移植原 EstimationAlgorithms.IdwEstimate）——克里金家族里快速稳健的同伴, 无需变差函数。
    /// 3D 距离; 可配幂次 power; 搜索半径 radius(≤0 自动); 邻域 [minSamples,maxSamples]; 平滑项 smoothing → w=1/(d+smoothing)^power。
    /// 零距离样本精确返回其值; 邻域不足(<minSamples)或权和≤0 返回 null(不赋值)。返回(估计值, 实用样本数)。
    /// </summary>
    public static (double est, int used)? IdwEstimate(
        IReadOnlyList<ControlPoint> points, double x, double y, double z,
        double power = 2.0, double radius = 0, int minSamples = 1, int maxSamples = 12, double smoothing = 0.0)
    {
        if (points.Count == 0) return null;
        if (radius <= 0) radius = AutoRadius(points);
        int need = Math.Max(1, minSamples);
        double r2 = radius * radius;
        var hits = new List<(double d, double v)>();
        foreach (var pt in points)
        {
            double dx = pt.X - x, dy = pt.Y - y, dz = pt.Z - z;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 <= r2) hits.Add((Math.Sqrt(d2), pt.V));
        }
        hits.Sort((a, b) => a.d.CompareTo(b.d));
        int cap = Math.Min(Math.Max(1, maxSamples), hits.Count);   // 裁到 maxSamples 后再比 minSamples(忠实原序)
        if (cap < need) return null;

        double wSum = 0, vSum = 0;
        for (int i = 0; i < cap; i++)
        {
            var (dist, v) = hits[i];
            if (dist < 1e-9) return (v, 1);                        // 零距离精确插值(避免除零)
            double w = 1.0 / Math.Pow(dist + smoothing, power);
            wSum += w;
            vSum += w * v;
        }
        if (wSum <= 0) return null;
        return (vSum / wSum, cap);
    }

    /// <summary>
    /// 简单克里金 SK（已知均值 mean, 无 Σw=1 约束）在 (x,y,z) 估值。忠实原 CoalQualityEstimator 的 SK 核。
    /// 区别于 OK：**数据稀疏区回归全局均值 mean**(OK 保持局部)。mean 默认样本均值。半径外 null。
    /// </summary>
    public static (double est, double variance)? EstimateSimpleAt(
        IReadOnlyList<ControlPoint> points, double x, double y, double z, double? mean = null, int k = 12, double radius = 0, Variogram? vg = null)
    {
        if (points.Count == 0) return null;
        if (radius <= 0) radius = AutoRadius(points);
        vg ??= FitVariogram(points);
        double m = mean ?? points.Average(q => q.V);
        var neigh = new List<(double d, ControlPoint pt)>(points.Count);
        foreach (var pt in points)
        {
            double dx = pt.X - x, dy = pt.Y - y, dz = pt.Z - z;
            neigh.Add((Math.Sqrt(dx * dx + dy * dy + dz * dz), pt));
        }
        neigh.Sort((a, b) => a.d.CompareTo(b.d));
        if (neigh[0].d > radius) return null;
        if (neigh[0].d < 1e-6) return (neigh[0].pt.V, 0);
        var top = neigh.GetRange(0, Math.Min(Math.Max(1, k), neigh.Count));
        return KrigeSimple(top, vg, m);
    }

    // 简单克里金：Γw=γ0(n×n, 无约束); est=m+Σw(v−m); var=sill−Σw·γ0。
    private static (double est, double variance) KrigeSimple(List<(double d, ControlPoint pt)> nb, Variogram vg, double m)
    {
        int n = nb.Count;
        var A = new double[n, n];
        var rhs = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) A[i, j] = vg.Gamma(Dist(nb[i].pt, nb[j].pt));
            rhs[i] = vg.Gamma(nb[i].d);
        }
        var w = Solve(A, rhs, n);
        if (w == null) { double sw = 0, s = 0; foreach (var (d, pt) in nb) { double ww = 1.0 / (d * d); sw += ww; s += ww * pt.V; } return (s / sw, vg.Sill); }
        double est = m, varr = vg.Sill;
        for (int i = 0; i < n; i++) { est += w[i] * (nb[i].pt.V - m); varr -= w[i] * rhs[i]; }
        return (est, Math.Max(0, varr));
    }

    /// <summary>
    /// 泛克里金 UK（带线性趋势 f=[1,x,y]）在 (x,y,z) 估值。忠实原 CoalQualityEstimator 的 UK 核。
    /// 区别于 OK：显式建模一次趋势面, 对**有区域趋势**的数据(如煤层品位沿走向渐变)更准——
    /// 对线性趋势数据**处处精确**(OK 只在控制点精确)。邻点 &lt;3 回落 OK。半径外返回 null。
    /// </summary>
    public static (double est, double variance)? EstimateUniversalAt(
        IReadOnlyList<ControlPoint> points, double x, double y, double z, int k = 12, double radius = 0, Variogram? vg = null)
    {
        if (points.Count == 0) return null;
        if (radius <= 0) radius = AutoRadius(points);
        vg ??= FitVariogram(points);
        var neigh = new List<(double d, ControlPoint pt)>(points.Count);
        foreach (var pt in points)
        {
            double dx = pt.X - x, dy = pt.Y - y, dz = pt.Z - z;
            neigh.Add((Math.Sqrt(dx * dx + dy * dy + dz * dz), pt));
        }
        neigh.Sort((a, b) => a.d.CompareTo(b.d));
        if (neigh[0].d > radius) return null;
        if (neigh[0].d < 1e-6) return (neigh[0].pt.V, 0);
        var top = neigh.GetRange(0, Math.Min(Math.Max(1, k), neigh.Count));
        if (top.Count < 3) return Krige(top, vg);                 // 趋势基需 ≥3 点, 否则回落 OK
        return KrigeUniversal(top, vg, x, y);
    }

    // 泛克里金：约束 Σw=1 + Σw·x=x0 + Σw·y=y0（一次趋势无偏），系统 (n+3) 阶。
    private static (double est, double variance) KrigeUniversal(List<(double d, ControlPoint pt)> nb, Variogram vg, double tx, double ty)
    {
        int n = nb.Count, p = 3, m = n + p;
        var A = new double[m, m];
        var rhs = new double[m];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) A[i, j] = vg.Gamma(Dist(nb[i].pt, nb[j].pt));
            // 趋势基 F 及其转置：f=[1, x, y]
            A[i, n] = 1; A[i, n + 1] = nb[i].pt.X; A[i, n + 2] = nb[i].pt.Y;
            A[n, i] = 1; A[n + 1, i] = nb[i].pt.X; A[n + 2, i] = nb[i].pt.Y;
            rhs[i] = vg.Gamma(nb[i].d);
        }
        rhs[n] = 1; rhs[n + 1] = tx; rhs[n + 2] = ty;              // f0=[1,x0,y0]
        var w = Solve(A, rhs, m);
        if (w == null) return Krige(nb, vg);                       // 病态 → 回落 OK
        double est = 0, varr = w[n] + w[n + 1] * tx + w[n + 2] * ty;
        for (int i = 0; i < n; i++) { est += w[i] * nb[i].pt.V; varr += w[i] * rhs[i]; }
        return (est, Math.Max(0, varr));
    }

    /// <summary>自动拟合球状变差函数：sill=样本方差；range=实验变差首达 95% sill 的滞后；nugget 由首箱估计。</summary>
    public static Variogram FitVariogram(IReadOnlyList<ControlPoint> pts)
    {
        double mean = pts.Average(q => q.V);
        double sill = pts.Count > 1 ? pts.Sum(q => (q.V - mean) * (q.V - mean)) / (pts.Count - 1) : 1;
        if (sill < 1e-9) sill = 1;

        double xn = double.MaxValue, xx = double.MinValue, yn = double.MaxValue, yx = double.MinValue;
        foreach (var p in pts) { if (p.X < xn) xn = p.X; if (p.X > xx) xx = p.X; if (p.Y < yn) yn = p.Y; if (p.Y > yx) yx = p.Y; }
        double diag = Math.Sqrt((xx - xn) * (xx - xn) + (yx - yn) * (yx - yn));
        double maxLag = Math.Max(diag * 0.6, 1);
        const int nb = 12;
        double bw = maxLag / nb;
        var sg = new double[nb];
        var cn = new int[nb];
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                double h = Dist(pts[i], pts[j]);
                if (h > maxLag) continue;
                int bi = Math.Min(nb - 1, (int)(h / bw));
                double dv = pts[i].V - pts[j].V;
                sg[bi] += 0.5 * dv * dv;
                cn[bi]++;
            }

        double range = maxLag * 0.5;
        for (int bi = 0; bi < nb; bi++)
            if (cn[bi] > 0 && sg[bi] / cn[bi] >= 0.95 * sill) { range = (bi + 0.5) * bw; break; }
        range = Math.Max(bw, Math.Min(maxLag, range));
        double nugget = cn[0] > 0 ? Math.Min(0.5 * sill, 0.5 * (sg[0] / cn[0])) : 0.1 * sill;
        return new Variogram(nugget, sill, range);
    }

    /// <summary>
    /// 球状变差函数最小二乘拟合(忠实移植原 EstimationAlgorithms.FitSpherical)——在 nugget/sill/range 粗网格上
    /// 扫最小「点对数加权」残差平方和 Σ cnt·(γ_model(h)−γ_exp(h))²; 选最小者。简单稳健, 不需外部数值优化库。
    /// 区别于 <see cref="FitVariogram"/>(矩法启发式: sill=样本方差·range=首达 0.95sill 的滞后·nugget 由首箱估):
    /// 此法真按 LSQ 拟合实验 γ(h), 更贴合空间相关结构。网格: nugget∈[0,0.3·maxG](7)·sill∈(nug,1.5·max(maxG,方差)](10)·range∈(0,maxH](12)。
    /// </summary>
    public static Variogram FitSpherical(IReadOnlyList<VariogramLag> exp, double sampleVariance)
    {
        var data = new List<VariogramLag>();
        foreach (var b in exp) if (b.Count > 0) data.Add(b);
        double sv = sampleVariance > 1e-9 ? sampleVariance : 1;
        if (data.Count < 3) return new Variogram(0, sv, 100);   // 忠实原: 数据不足退 nugget0/sill=方差/range100

        double maxH = 0, maxG = 0;
        foreach (var b in data) { if (b.H > maxH) maxH = b.H; if (b.Gamma > maxG) maxG = b.Gamma; }
        double sillUpper = Math.Max(maxG, sv) * 1.5;

        double bestNug = 0, bestSill = maxG > 0 ? maxG : sv, bestRange = maxH * 0.5, bestErr = double.MaxValue;
        for (int n = 0; n <= 6; n++)                            // nugget ∈ [0, 0.3·maxG]
        {
            double nug = (maxG * 0.3) * n / 6.0;
            for (int s = 1; s <= 10; s++)                       // sill ∈ (nug, sillUpper]
            {
                double sill = nug + (sillUpper - nug) * s / 10.0;
                if (sill <= nug) continue;
                for (int r = 1; r <= 12; r++)                   // range ∈ (0, maxH]
                {
                    double range = maxH * r / 12.0;
                    if (range <= 0) continue;
                    var vg = new Variogram(nug, sill, range);
                    double err = 0; int cnt = 0;
                    foreach (var b in data) { double d = vg.Gamma(b.H) - b.Gamma; err += d * d * b.Count; cnt += b.Count; }  // 点对数加权
                    if (cnt > 0) err /= cnt;
                    if (err < bestErr) { bestErr = err; bestNug = nug; bestSill = sill; bestRange = range; }
                }
            }
        }
        return new Variogram(bestNug, bestSill, bestRange);
    }

    /// <summary>便捷重载: 从控制点自动算实验变差 + 样本方差, 再 LSQ 拟合球状模型。</summary>
    public static Variogram FitSpherical(IReadOnlyList<ControlPoint> pts, double maxLag = 0, int lagCount = 12)
    {
        if (pts.Count == 0) return new Variogram(0, 1, 100);
        double mean = pts.Average(q => q.V);
        double variance = pts.Count > 1 ? pts.Sum(q => (q.V - mean) * (q.V - mean)) / (pts.Count - 1) : 1;
        return FitSpherical(ExperimentalVariogram(pts, maxLag, lagCount), variance);
    }

    /// <summary>实验(经验)变差函数的一个滞后 bin: 滞后中心 H · 半变异 γ(h) · 点对数。</summary>
    public readonly record struct VariogramLag(double H, double Gamma, int Count);

    /// <summary>
    /// 实验变差函数(经验半变异 γ(h) 云) —— 忠实移植原 EstimationAlgorithms.ComputeExperimentalVariogram。
    /// 逐点对按 3D 滞后距分箱, γ(h) = 0.5·mean((v_i−v_j)²)。maxLag≤0 自动取包围盒对角×0.6; lagCount 分箱数。
    /// 用于建模前看空间相关结构 / 验证 <see cref="FitVariogram"/> 的球状拟合。纯逻辑、可单测。
    /// </summary>
    public static List<VariogramLag> ExperimentalVariogram(IReadOnlyList<ControlPoint> pts, double maxLag = 0, int lagCount = 12)
    {
        int nb = Math.Max(1, lagCount);
        if (pts == null || pts.Count < 2) { var e = new List<VariogramLag>(nb); for (int i = 0; i < nb; i++) e.Add(new VariogramLag((i + 0.5), 0, 0)); return e; }
        if (maxLag <= 0)
        {
            double xn = double.MaxValue, xx = double.MinValue, yn = double.MaxValue, yx = double.MinValue;
            foreach (var p in pts) { if (p.X < xn) xn = p.X; if (p.X > xx) xx = p.X; if (p.Y < yn) yn = p.Y; if (p.Y > yx) yx = p.Y; }
            double diag = Math.Sqrt((xx - xn) * (xx - xn) + (yx - yn) * (yx - yn));
            maxLag = Math.Max(diag * 0.6, 1);
        }
        double bw = maxLag / nb;
        var g = new double[nb]; var cn = new int[nb];
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y, dz = pts[i].Z - pts[j].Z;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d >= maxLag || d < 1e-9) continue;
                int b = (int)(d / bw);
                if (b >= nb) continue;
                double diff = pts[i].V - pts[j].V;
                g[b] += 0.5 * diff * diff; cn[b]++;
            }
        var bins = new List<VariogramLag>(nb);
        for (int i = 0; i < nb; i++) bins.Add(new VariogramLag((i + 0.5) * bw, cn[i] > 0 ? g[i] / cn[i] : 0, cn[i]));
        return bins;
    }

    private static (double est, double variance) Krige(List<(double d, ControlPoint pt)> nb, Variogram vg)
    {
        int n = nb.Count;
        if (n == 1) return (nb[0].pt.V, vg.Sill);

        int m = n + 1;                       // 拉格朗日约束多一行/列
        var A = new double[m, m];
        var rhs = new double[m];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                A[i, j] = vg.Gamma(Dist(nb[i].pt, nb[j].pt));
            A[i, n] = 1; A[n, i] = 1;
            rhs[i] = vg.Gamma(nb[i].d);
        }
        A[n, n] = 0; rhs[n] = 1;

        var w = Solve(A, rhs, m);
        if (w == null)                        // 病态 → 退回 IDW，方差记 sill
        {
            double sw = 0, s = 0;
            foreach (var (d, pt) in nb) { double ww = 1.0 / (d * d); sw += ww; s += ww * pt.V; }
            return (s / sw, vg.Sill);
        }

        double est = 0, varr = w[n];          // μ（拉格朗日乘子）
        for (int i = 0; i < n; i++) { est += w[i] * nb[i].pt.V; varr += w[i] * rhs[i]; }
        return (est, Math.Max(0, varr));
    }

    /// <summary>高斯消元（部分主元）解 A·x=b；奇异返回 null。</summary>
    private static double[]? Solve(double[,] A, double[] b, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int piv = c; double best = Math.Abs(A[c, c]);
            for (int r = c + 1; r < n; r++) { double v = Math.Abs(A[r, c]); if (v > best) { best = v; piv = r; } }
            if (best < 1e-12) return null;
            if (piv != c)
            {
                for (int j = 0; j < n; j++) (A[c, j], A[piv, j]) = (A[piv, j], A[c, j]);
                (b[c], b[piv]) = (b[piv], b[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = A[r, c] / A[c, c];
                if (f == 0) continue;
                for (int j = c; j < n; j++) A[r, j] -= f * A[c, j];
                b[r] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int j = r + 1; j < n; j++) s -= A[r, j] * x[j];
            x[r] = s / A[r, r];
        }
        return x;
    }

    private static double AutoRadius(IReadOnlyList<ControlPoint> pts)
    {
        double xn = double.MaxValue, xx = double.MinValue, yn = double.MaxValue, yx = double.MinValue;
        foreach (var p in pts) { if (p.X < xn) xn = p.X; if (p.X > xx) xx = p.X; if (p.Y < yn) yn = p.Y; if (p.Y > yx) yx = p.Y; }
        double dx = xx - xn, dy = yx - yn;
        double spacing = Math.Sqrt(Math.Max(dx * dy, 1) / Math.Max(pts.Count, 1));
        return spacing * 2.5;
    }

    private static double Dist(ControlPoint a, ControlPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
