using System;
using System.Collections.Generic;
using System.Linq;
using CP = PitMine3D.Kylin.Cad.OrdinaryKriging.ControlPoint;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 空间估值留一交叉验证(LOO-CV) —— 忠实移植原 CoalQualityEstimator.CrossValidate。
/// 逐点把该点从控制集剔除, 用其余点按选定方法(NN/IDW/OK/MA)预测它, 汇总 ME/MAE/RMSE/R² +
/// (OK 有克里金方差时)标准化误差均值 MSE/方差 MSEVar 评估估值质量与方差合理性。
///
/// OK 核复用 <see cref="OrdinaryKriging.EstimateAt"/>(球状变差·(k+1)阶方程组·出方差); NN/IDW/MA 内联。
/// 变差函数用全体点拟合一次(LOO 上有极小泄漏但拟合稳定, 通行做法), 逐点估值时把该点从邻域剔除。
/// 纯逻辑、可单测。
/// </summary>
public static class SpatialCrossValidation
{
    /// <summary>一对(实测, 预测, 标准化误差)。StdError 仅 OK 且方差>0 时有值。</summary>
    public readonly record struct CvPair(double Actual, double Predicted, double? StdError);

    public sealed record CvResult(
        int N, int Predicted,
        double ME,        // 平均误差(系统偏差; ≈0 好)
        double RMSE,      // 均方根误差
        double MAE,       // 平均绝对误差
        double? MSE,      // 标准化误差均值(≈0 好; 仅 OK)
        double? MSEVar,   // 标准化误差方差(≈1 好, 说明方差估计合理; 仅 OK)
        double R2,        // 实测↔预测 决定系数
        IReadOnlyList<CvPair> Pairs);

    private enum Method { NN, IDW, OK, MA }

    /// <summary>留一交叉验证。method: OK/克里金 · NN/最近邻 · MA/移动平均 · 其它=IDW。parameters: p/k/radius。n&lt;4 返回空。</summary>
    public static CvResult CrossValidate(IReadOnlyList<CP> points, string method, IDictionary<string, double>? parameters = null)
    {
        int n = points?.Count ?? 0;
        var empty = new CvResult(n, 0, 0, 0, 0, null, null, 0, Array.Empty<CvPair>());
        if (points == null || n < 4) return empty;

        var kind = Kind(method);
        double P(string key, double def) => parameters?.TryGetValue(key, out var v) == true ? v : def;
        double p = P("p", 2);
        int k = Math.Max(1, (int)P("k", kind == Method.OK ? 12 : 6));
        double radius = P("radius", 0) > 0 ? P("radius", 0) : AutoRadius(points);
        OrdinaryKriging.Variogram? vg = kind == Method.OK ? OrdinaryKriging.FitVariogram(points) : null;

        var pairs = new List<CvPair>(n);
        var errs = new List<double>(n);
        var stdErrs = new List<double>();
        for (int i = 0; i < n; i++)
        {
            var loo = new List<CP>(n - 1);
            for (int j = 0; j < n; j++) if (j != i) loo.Add(points[j]);
            var e = EstimateOne(loo, points[i].X, points[i].Y, points[i].Z, kind, p, k, radius, vg);
            if (e is null) continue;                                   // 剔除后半径外, 无法预测
            double actual = points[i].V, pred = e.Value.est, err = pred - actual;
            double? se = e.Value.variance is > 1e-9 ? err / Math.Sqrt(e.Value.variance.Value) : (double?)null;
            pairs.Add(new CvPair(actual, pred, se));
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
        double r2 = R2(pairs.Select(q => q.Actual).ToList(), pairs.Select(q => q.Predicted).ToList());
        return new CvResult(n, pairs.Count, me, rmse, mae, mse, mseVar, r2, pairs);
    }

    /// <summary>结果 → CSV(摘要 + 逐点 实测/预测/标准化误差)。</summary>
    public static string ToCsv(CvResult r, string method)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("空间估值交叉验证(留一)");
        sb.AppendLine($"方法,{method}");
        sb.AppendLine($"控制点,{r.N}");
        sb.AppendLine($"预测点,{r.Predicted}");
        sb.AppendLine($"平均误差ME,{r.ME:0.####}");
        sb.AppendLine($"平均绝对误差MAE,{r.MAE:0.####}");
        sb.AppendLine($"均方根误差RMSE,{r.RMSE:0.####}");
        if (r.MSE.HasValue) sb.AppendLine($"标准化误差均值MSE,{r.MSE.Value:0.####}");
        if (r.MSEVar.HasValue) sb.AppendLine($"标准化误差方差,{r.MSEVar.Value:0.####}");
        sb.AppendLine($"决定系数R²,{r.R2:0.####}");
        sb.AppendLine();
        sb.AppendLine("序号,实测,预测,标准化误差");
        int i = 1;
        foreach (var p in r.Pairs)
            sb.AppendLine($"{i++},{p.Actual:0.###},{p.Predicted:0.###},{(p.StdError.HasValue ? p.StdError.Value.ToString("0.###") : "")}");
        return sb.ToString();
    }

    // ── 逐点核 ──────────────────────────────────────────────
    private static (double est, double? variance)? EstimateOne(
        IReadOnlyList<CP> points, double x, double y, double z, Method kind, double p, int k, double radius, OrdinaryKriging.Variogram? vg)
    {
        if (kind == Method.OK)
        {
            var r = OrdinaryKriging.EstimateAt(points, x, y, z, k, radius, vg);
            return r is null ? null : (r.Value.est, r.Value.variance);
        }
        var neigh = new List<(double d, CP pt)>(points.Count);
        foreach (var pt in points)
        {
            double dx = pt.X - x, dy = pt.Y - y, dz = pt.Z - z;
            neigh.Add((Math.Sqrt(dx * dx + dy * dy + dz * dz), pt));
        }
        neigh.Sort((a, b) => a.d.CompareTo(b.d));
        if (neigh[0].d > radius) return null;                 // 无数据支撑
        if (neigh[0].d < 1e-6) return (neigh[0].pt.V, 0);     // 正落控制点上

        var top = neigh.GetRange(0, Math.Min(k, neigh.Count));
        switch (kind)
        {
            case Method.NN: return (top[0].pt.V, null);
            case Method.MA: return (top.Average(t => t.pt.V), null);
            default:                                           // IDW
                double sw = 0, s = 0;
                foreach (var (d, pt) in top) { double w = 1.0 / Math.Pow(d, p); sw += w; s += w * pt.V; }
                return (s / sw, null);
        }
    }

    private static Method Kind(string m)
    {
        m ??= "";
        if (m.Contains("OK") || m.Contains("克里金")) return Method.OK;
        if (m.Contains("NN") || m.Contains("最近邻")) return Method.NN;
        if (m.Contains("MA") || m.Contains("移动平均")) return Method.MA;
        return Method.IDW;
    }

    /// <summary>自动搜索半径(点间距×2.5)。忠实原 AutoRadius。</summary>
    private static double AutoRadius(IReadOnlyList<CP> points)
    {
        double xn = double.MaxValue, xx = double.MinValue, yn = double.MaxValue, yx = double.MinValue;
        foreach (var pt in points)
        {
            if (pt.X < xn) xn = pt.X; if (pt.X > xx) xx = pt.X;
            if (pt.Y < yn) yn = pt.Y; if (pt.Y > yx) yx = pt.Y;
        }
        double dx = xx - xn, dy = yx - yn;
        double spacing = Math.Sqrt(Math.Max(dx * dy, 1) / Math.Max(points.Count, 1));
        return spacing * 2.5;
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
}
