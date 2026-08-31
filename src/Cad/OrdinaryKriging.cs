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

    public sealed record Variogram(double Nugget, double Sill, double Range)
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
