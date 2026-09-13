// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/CoalQualityEstimator.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Services;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 煤质指标空间估值引擎（纯 C#，无外部依赖）。
/// 煤矿场景估的是**煤质指标**（灰分 Ad / 硫分 St / 发热量 Qnet / 挥发分 Vdaf / 粘结 G），
/// 不是金属矿的"品位"。四种方法：
///   · 最近邻 NN      —— 取最近控制点值；无平滑，看原始控制。
///   · 反距离加权 IDW —— k 近邻按 1/dᵖ 加权；p、k 可调。
///   · 普通克里金 OK  —— 球状变差函数(自动拟合)，解 (k+1) 阶克里金方程组，出估计值 + **克里金方差**。
///   · 移动平均 MA    —— k 近邻等权平均。
/// 两个入口：<see cref="Interpolate"/> 填规则网格(空间分布窗)；<see cref="EstimateAt"/> 在任意目标点
/// (如块体 cell 中心)估值(块体联动)。二者共用同一套 <see cref="EstimateOne"/> 逐点核。
/// 搜索半径外（无数据支撑）返回"不赋值"，把体素/属性压到贴着化验区的薄带。
/// </summary>
internal sealed class CoalQualityEstimator : IInterpolationServiceHook
{
    public IReadOnlyList<string> AvailableMethods { get; } = new[]
    {
        "普通克里金 OK", "反距离加权 IDW", "最近邻 NN", "移动平均 MA",
    };

    // ─────────────────────────── 规则网格（空间分布窗）───────────────────────────
    public IReadOnlyList<InterpolatedVoxel> Interpolate(
        IReadOnlyList<ControlPoint> points,
        string method,
        SpatialBounds bounds,
        double resolution,
        IDictionary<string, double>? parameters = null)
    {
        if (points.Count == 0) return Array.Empty<InterpolatedVoxel>();
        var cfg = Prepare(points, method, bounds, parameters);

        var result = new List<InterpolatedVoxel>();
        for (double x = bounds.XMin; x <= bounds.XMax + 1e-6; x += resolution)
        for (double y = bounds.YMin; y <= bounds.YMax + 1e-6; y += resolution)
        for (double z = bounds.ZMin; z <= bounds.ZMax + 1e-6; z += resolution)
        {
            var e = EstimateOne(points, x, y, z, cfg);
            if (e is null) continue;                       // 无数据支撑 → 不外插
            result.Add(new InterpolatedVoxel(x, y, z, e.Value.est, e.Value.variance));
        }
        return result;
    }

    // ─────────────────────────── 任意目标点（块体联动）───────────────────────────
    public IReadOnlyList<double?> EstimateAt(
        IReadOnlyList<ControlPoint> points,
        IReadOnlyList<TargetPoint> targets,
        string method,
        IDictionary<string, double>? parameters = null)
    {
        var outv = new double?[targets.Count];
        if (points.Count == 0 || targets.Count == 0) return outv;
        var cfg = Prepare(points, method, BoundsOfPoints(points), parameters);

        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var e = EstimateOne(points, t.X, t.Y, t.Z, cfg);
            outv[i] = e?.est;                              // 半径外 → null（不赋值）
        }
        return outv;
    }

    // ─────────────────────────── 带方差估值（块体可信度）───────────────────────────
    public IReadOnlyList<EstimatedValue> EstimateWithVariance(
        IReadOnlyList<ControlPoint> points,
        IReadOnlyList<TargetPoint> targets,
        string method,
        IDictionary<string, double>? parameters = null)
    {
        var outv = new EstimatedValue[targets.Count];
        var none = new EstimatedValue(null, null);
        if (points.Count == 0 || targets.Count == 0)
        {
            for (int i = 0; i < outv.Length; i++) outv[i] = none;
            return outv;
        }
        var cfg = Prepare(points, method, BoundsOfPoints(points), parameters);
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var e = EstimateOne(points, t.X, t.Y, t.Z, cfg);
            outv[i] = e is null ? none : new EstimatedValue(e.Value.est, e.Value.variance);
        }
        return outv;
    }

    // ─────────────────────────── 留一交叉验证 ───────────────────────────
    public CrossValidationResult CrossValidate(
        IReadOnlyList<ControlPoint> points,
        string method,
        IDictionary<string, double>? parameters = null)
    {
        int n = points.Count;
        var empty = new CrossValidationResult(n, 0, 0, 0, 0, null, null, 0, Array.Empty<CrossValidationPair>());
        if (n < 4) return empty;

        // 变差函数用全体点拟合一次（LOO 纯度上有极小泄漏，但拟合稳定，是通行做法）；
        // 逐点估值时把该点从邻域中剔除。
        var cfg = Prepare(points, method, BoundsOfPoints(points), parameters);

        var pairs = new List<CrossValidationPair>(n);
        var errs = new List<double>(n);
        var stdErrs = new List<double>();
        var loo = new List<ControlPoint>(n);
        for (int i = 0; i < n; i++)
        {
            loo.Clear();
            for (int j = 0; j < n; j++) if (j != i) loo.Add(points[j]);
            var e = EstimateOne(loo, points[i].X, points[i].Y, points[i].Z, cfg);
            if (e is null) continue;                                   // 剔除后半径外，无法预测
            double actual = points[i].V, pred = e.Value.est, err = pred - actual;
            double? se = e.Value.variance is > 1e-9 ? err / Math.Sqrt(e.Value.variance.Value) : (double?)null;
            pairs.Add(new CrossValidationPair(actual, pred, se));
            errs.Add(err);
            if (se.HasValue) stdErrs.Add(se.Value);
        }
        if (errs.Count == 0) return empty;

        double me = errs.Average();
        double mae = errs.Average(Math.Abs);
        double rmse = Math.Sqrt(errs.Average(v => v * v));
        double? mse = stdErrs.Count > 0 ? stdErrs.Average() : null;
        double? mseVar = null;
        if (stdErrs.Count > 1)
        {
            double m = stdErrs.Average();
            mseVar = stdErrs.Sum(s => (s - m) * (s - m)) / (stdErrs.Count - 1);
        }
        double r2 = R2(pairs.Select(p => p.Actual).ToList(), pairs.Select(p => p.Predicted).ToList());
        return new CrossValidationResult(n, pairs.Count, me, rmse, mae, mse, mseVar, r2, pairs);
    }

    private static double R2(List<double> actual, List<double> pred)
    {
        int n = actual.Count;
        if (n < 2) return 0;
        double ma = actual.Average();
        double ssTot = actual.Sum(v => (v - ma) * (v - ma));
        if (ssTot < 1e-12) return 0;
        double ssRes = 0;
        for (int i = 0; i < n; i++) { double d = actual[i] - pred[i]; ssRes += d * d; }
        return Math.Max(0, 1 - ssRes / ssTot);
    }

    // ─────────────────────────── 逐点核 ───────────────────────────
    private enum Method { NN, IDW, OK, MA }
    private readonly record struct Config(Method Kind, double P, int K, double Radius, Variogram? Vg);

    private Config Prepare(IReadOnlyList<ControlPoint> points, string method, SpatialBounds bounds, IDictionary<string, double>? parameters)
    {
        double P(string key, double def) => parameters?.TryGetValue(key, out var v) == true ? v : def;
        var kind = Kind(method);
        double p = P("p", 2);
        int k = Math.Max(1, (int)P("k", kind == Method.OK ? 12 : 6));
        double radius = P("radius", 0) > 0 ? P("radius", 0) : AutoRadius(points, bounds);
        Variogram? vg = kind == Method.OK ? FitVariogram(points, bounds, parameters) : null;
        return new Config(kind, p, k, radius, vg);
    }

    private static (double est, double? variance)? EstimateOne(IReadOnlyList<ControlPoint> points, double x, double y, double z, Config cfg)
    {
        var neigh = new List<(double d, ControlPoint pt)>(points.Count);
        foreach (var pt in points)
        {
            double dx = pt.X - x, dy = pt.Y - y, dz = pt.Z - z;
            neigh.Add((Math.Sqrt(dx * dx + dy * dy + dz * dz), pt));
        }
        neigh.Sort((a, b) => a.d.CompareTo(b.d));
        if (neigh[0].d > cfg.Radius) return null;              // 无数据支撑
        if (neigh[0].d < 1e-6) return (neigh[0].pt.V, 0);      // 正落控制点上

        var top = neigh.GetRange(0, Math.Min(cfg.K, neigh.Count));
        switch (cfg.Kind)
        {
            case Method.NN: return (top[0].pt.V, null);
            case Method.MA: return (top.Average(t => t.pt.V), null);
            case Method.OK: var (e, v) = Krige(top, cfg.Vg!); return (e, v);
            default:                                           // IDW
                double sw = 0, s = 0;
                foreach (var (d, pt) in top) { double w = 1.0 / Math.Pow(d, cfg.P); sw += w; s += w * pt.V; }
                return (s / sw, null);
        }
    }

    private static Method Kind(string m)
    {
        if (m.Contains("OK") || m.Contains("克里金")) return Method.OK;
        if (m.Contains("NN") || m.Contains("最近邻")) return Method.NN;
        if (m.Contains("MA") || m.Contains("移动平均")) return Method.MA;
        return Method.IDW;
    }

    private static double AutoRadius(IReadOnlyList<ControlPoint> points, SpatialBounds b)
    {
        double dx = b.XMax - b.XMin, dy = b.YMax - b.YMin;
        double spacing = Math.Sqrt(Math.Max(dx * dy, 1) / Math.Max(points.Count, 1));
        return spacing * 2.5;
    }

    private static SpatialBounds BoundsOfPoints(IReadOnlyList<ControlPoint> pts)
    {
        double xn = double.MaxValue, xx = double.MinValue, yn = double.MaxValue,
               yx = double.MinValue, zn = double.MaxValue, zx = double.MinValue;
        foreach (var p in pts)
        {
            if (p.X < xn) xn = p.X; if (p.X > xx) xx = p.X;
            if (p.Y < yn) yn = p.Y; if (p.Y > yx) yx = p.Y;
            if (p.Z < zn) zn = p.Z; if (p.Z > zx) zx = p.Z;
        }
        return new SpatialBounds(xn, xx, yn, yx, zn, zx);
    }

    // ─────────────────────────── 普通克里金 ───────────────────────────
    private sealed record Variogram(double Nugget, double Sill, double Range)
    {
        /// <summary>球状模型 γ(h)。</summary>
        public double Gamma(double h)
        {
            if (h <= 1e-9) return 0;
            if (h >= Range) return Sill;
            double t = h / Range;
            return Nugget + (Sill - Nugget) * (1.5 * t - 0.5 * t * t * t);
        }
    }

    /// <summary>
    /// 自动拟合球状变差函数：sill = 样本方差；range = 实验变差首次达到 ~95% sill 的滞后；
    /// nugget 由首个滞后箱估计。三者均可由 parameters(range/nugget/sill) 覆盖。
    /// </summary>
    private static Variogram FitVariogram(IReadOnlyList<ControlPoint> pts, SpatialBounds b, IDictionary<string, double>? pr)
    {
        double mean = pts.Average(q => q.V);
        double sill = pts.Count > 1 ? pts.Sum(q => (q.V - mean) * (q.V - mean)) / (pts.Count - 1) : 1;
        if (sill < 1e-9) sill = 1;

        double diag = Math.Sqrt(Math.Pow(b.XMax - b.XMin, 2) + Math.Pow(b.YMax - b.YMin, 2));
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

        double O(string key, double def) => pr?.TryGetValue(key, out var v) == true && v > 0 ? v : def;
        return new Variogram(O("nugget", nugget), O("sill", sill), O("range", range));
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
            rhs[i] = vg.Gamma(nb[i].d);       // γ(节点, 第 i 邻)
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

    private static double Dist(ControlPoint a, ControlPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
