using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 真实估值算法集合（逐字移植原 MeshEditLib/Estimation/Services/EstimationAlgorithms.cs，
/// 样本以 <see cref="SampleRecord"/>(坐标 + 属性字典) 表达，供「快速估值 / 克里金估值」选点插值窗口的
/// <see cref="EstimationEngine"/> 调用；<see cref="OrdinaryKriging"/> 为按控制点的另一套核，两者互不替代）。
///
/// 提供：
///   - IdwEstimate / ComputeIdw   反距离权重（IDW）
///   - SimpleKriging              简单克里金（已知均值，无 Lagrange）
///   - OrdinaryKriging / KrigingWithDrift  普通克里金（Σw=1）/ 泛克里金（一、二阶漂移）
///   - Gamma                      变异函数模型 γ(h)（Spherical / Exponential / Gaussian）
///   - ComputeExperimentalVariogram  从样本对计算实验变异函数
///   - FitSpherical               最小二乘拟合 Spherical 模型参数
///   - SampleGrid2D               2D 均匀网格近邻索引（大样本加速）
///
/// 注意：
///   - 所有距离都是 3D（包含 Z）
///   - 单位与样本一致（不做单位换算）
///   - 失败时返回 DefaultValue 而非抛异常（与 EstimationEngine 错误处理铁律一致）
/// </summary>
public static class EstimationAlgorithms
{
    // ───────────────────────────── 距离 / KD scan ─────────────────────────────

    public static double Distance3D(double ax, double ay, double az,
                                      double bx, double by, double bz)
    {
        double dx = ax - bx, dy = ay - by, dz = az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>线性扫描 + 半径裁剪：返回 (distance, sampleIndex) 升序排序的前 maxK 项</summary>
    public static List<(double dist, int index)> FindNearestSamples(
        List<SampleRecord> samples,
        double bx, double by, double bz,
        double radius, int maxK)
    {
        var hits = new List<(double dist, int index)>();
        double r2 = radius * radius;
        for (int i = 0; i < samples.Count; ++i)
        {
            var s = samples[i];
            double dx = s.X - bx, dy = s.Y - by, dz = s.Z - bz;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 <= r2)
                hits.Add((Math.Sqrt(d2), i));
        }
        hits.Sort((a, b) => a.dist.CompareTo(b.dist));
        if (hits.Count > maxK) hits.RemoveRange(maxK, hits.Count - maxK);
        return hits;
    }

    // ───────────────────────────── IDW ─────────────────────────────

    /// <summary>
    /// 反距离权重估值。零距离样本直接返回其值（精确插值）。
    /// 失败/邻域过少返回 (defaultValue, 0)。
    /// </summary>
    public static (double estimate, int samplesUsed) IdwEstimate(
        double bx, double by, double bz,
        List<SampleRecord> samples, string targetProp,
        double power, double radius, int minSamples, int maxSamples,
        double smoothing, double defaultValue)
    {
        var hits = FindNearestSamples(samples, bx, by, bz, radius, maxSamples);
        if (hits.Count < minSamples) return (defaultValue, 0);

        double wSum = 0, vSum = 0;
        foreach (var (dist, idx) in hits)
        {
            if (!samples[idx].Properties.TryGetValue(targetProp, out double v)) continue;
            // 零距离精确插值（避免除零）
            if (dist < 1e-9) return (v, 1);
            double w = 1.0 / Math.Pow(dist + smoothing, power);
            wSum += w;
            vSum += w * v;
        }
        if (wSum <= 0) return (defaultValue, 0);
        return (vSum / wSum, hits.Count);
    }

    // ───────────────────────────── Variogram γ(h) ─────────────────────────────

    public static double Gamma(string modelType, double h,
                                double nugget, double sill, double range)
    {
        if (h <= 0) return 0;
        double c = sill - nugget;
        switch (modelType)
        {
            case "Spherical":
                if (h >= range) return sill;
                return nugget + c * (1.5 * (h / range) - 0.5 * Math.Pow(h / range, 3));
            case "Exponential":
                return nugget + c * (1.0 - Math.Exp(-3.0 * h / range));
            case "Gaussian":
                return nugget + c * (1.0 - Math.Exp(-3.0 * Math.Pow(h / range, 2)));
            default:
                return sill;
        }
    }

    // ──────────────── Experimental Variogram ────────────────

    public class ExperimentalLag
    {
        public double H;     // bin 中心距离
        public double Gamma; // 0.5 * E[(z_i - z_j)^2]
        public int Count;    // 该 bin 内样本对数
    }

    /// <summary>
    /// 计算实验变异函数：按距离分 bin，对每个 bin 内的样本对计算 0.5·(Δz)²，求平均。
    /// </summary>
    public static List<ExperimentalLag> ComputeExperimentalVariogram(
        List<SampleRecord> samples, string targetProp,
        double maxLag, int lagCount)
    {
        var bins = new List<ExperimentalLag>(lagCount);
        for (int i = 0; i < lagCount; ++i)
            bins.Add(new ExperimentalLag { H = (i + 0.5) * (maxLag / lagCount), Gamma = 0, Count = 0 });

        for (int i = 0; i < samples.Count; ++i)
        {
            if (!samples[i].Properties.TryGetValue(targetProp, out double vi)) continue;
            for (int j = i + 1; j < samples.Count; ++j)
            {
                if (!samples[j].Properties.TryGetValue(targetProp, out double vj)) continue;
                double d = Distance3D(samples[i].X, samples[i].Y, samples[i].Z,
                                       samples[j].X, samples[j].Y, samples[j].Z);
                if (d >= maxLag || d < 1e-9) continue;
                int bin = (int)(d / (maxLag / lagCount));
                if (bin >= lagCount) continue;
                double diff = vi - vj;
                bins[bin].Gamma += 0.5 * diff * diff;
                bins[bin].Count++;
            }
        }
        foreach (var b in bins)
            if (b.Count > 0) b.Gamma /= b.Count;
        return bins;
    }

    // ──────────────── Variogram Fitter (Spherical, brute force grid + LSQ refine) ────────────────

    /// <summary>
    /// 用 Spherical 模型对实验变异函数做粗糙拟合：在合理网格上扫 nugget/sill/range，
    /// 选最小残差作为初值。简单稳健，不需要外部数值优化库。
    /// </summary>
    public static VariogramConfig FitSpherical(List<ExperimentalLag> exp, double sampleVariance)
    {
        // 排除空 bin
        var data = exp.Where(b => b.Count > 0).ToList();
        if (data.Count < 3)
            return new VariogramConfig { Type = "Spherical", Nugget = 0, Sill = sampleVariance, Range = 100 };

        double maxH = data.Max(b => b.H);
        double maxG = data.Max(b => b.Gamma);
        double sillUpper = Math.Max(maxG, sampleVariance) * 1.5;

        double bestNug = 0, bestSill = maxG, bestRange = maxH * 0.5;
        double bestErr = double.MaxValue;

        // 粗格扫（Spherical 通常足够稳健）
        for (int n = 0; n <= 6; ++n)
        {
            double nug = (maxG * 0.3) * n / 6.0;  // nugget ∈ [0, 0.3·maxG]
            for (int s = 1; s <= 10; ++s)
            {
                double sill = nug + (sillUpper - nug) * s / 10.0;
                if (sill <= nug) continue;
                for (int r = 1; r <= 12; ++r)
                {
                    double range = maxH * r / 12.0;
                    double err = 0;
                    int cnt = 0;
                    foreach (var b in data)
                    {
                        double pred = Gamma("Spherical", b.H, nug, sill, range);
                        double diff = pred - b.Gamma;
                        err += diff * diff * b.Count;  // 加权（点对多的 bin 权重大）
                        cnt += b.Count;
                    }
                    if (cnt > 0) err /= cnt;
                    if (err < bestErr)
                    {
                        bestErr = err;
                        bestNug = nug; bestSill = sill; bestRange = range;
                    }
                }
            }
        }
        return new VariogramConfig
        {
            Type = "Spherical",
            Nugget = bestNug,
            Sill = bestSill,
            Range = bestRange,
        };
    }

    // ───────────────────────────── Ordinary Kriging ─────────────────────────────

    /// <summary>
    /// 普通克里金（OK）：求解 (N+1)x(N+1) 线性系统，含 Lagrange 项保证 Σw=1。
    /// 数值不稳/邻域过少时回退 IDW（power=2）。
    /// </summary>
    public static (double estimate, double variance, int samplesUsed) OrdinaryKriging(
        double bx, double by, double bz,
        List<SampleRecord> samples, string targetProp,
        VariogramConfig variogram,
        double radius, int minSamples, int maxSamples, double defaultValue)
    {
        var hits = FindNearestSamples(samples, bx, by, bz, radius, maxSamples);
        if (hits.Count < minSamples) return (defaultValue, double.NaN, 0);

        // 提取 N 个有效样本 + 对应值
        var idx = new List<int>();
        var vals = new List<double>();
        foreach (var (_, i) in hits)
        {
            if (samples[i].Properties.TryGetValue(targetProp, out double v))
            {
                idx.Add(i);
                vals.Add(v);
            }
        }
        int N = idx.Count;
        if (N < minSamples) return (defaultValue, double.NaN, 0);

        // 构造 (N+1)×(N+1) 系统
        //   A·w = b
        //   A[i,j] = γ(d_ij)      i,j ∈ [0,N)
        //   A[i,N] = A[N,i] = 1
        //   A[N,N] = 0
        //   b[i]   = γ(d_i_block)
        //   b[N]   = 1
        int M = N + 1;
        var A = new double[M, M];
        var b = new double[M];

        for (int i = 0; i < N; ++i)
        {
            for (int j = 0; j < N; ++j)
            {
                if (i == j) { A[i, j] = 0; continue; }
                double d = Distance3D(samples[idx[i]].X, samples[idx[i]].Y, samples[idx[i]].Z,
                                       samples[idx[j]].X, samples[idx[j]].Y, samples[idx[j]].Z);
                A[i, j] = Gamma(variogram.Type, d, variogram.Nugget, variogram.Sill, variogram.Range);
            }
            A[i, N] = 1; A[N, i] = 1;

            double dBlk = Distance3D(samples[idx[i]].X, samples[idx[i]].Y, samples[idx[i]].Z, bx, by, bz);
            b[i] = Gamma(variogram.Type, dBlk, variogram.Nugget, variogram.Sill, variogram.Range);
        }
        A[N, N] = 0;
        b[N] = 1;

        // 保存 RHS（γ(x₀,xᵢ) 与约束行）：解后 b 被覆写为 [权重 λ; 拉格朗日乘子 μ]，求方差要用原始 RHS
        var rhs = (double[])b.Clone();

        // Gauss 部分主元消去（小尺寸 N+1 ≤ ~13 通常足够稳）
        if (!GaussSolveInPlace(A, b, M))
        {
            // 数值不稳 → 回退 IDW（无克里金方差）
            var (e, n) = IdwEstimate(bx, by, bz, samples, targetProp, 2.0, radius, minSamples, maxSamples, 0, defaultValue);
            return (e, double.NaN, n);
        }

        // 估值 = Σ λᵢ·zᵢ；OK 方差 σ² = Σ λᵢ·γ(x₀,xᵢ) + μ（μ = b[N]），数值上夹到 ≥0
        double est = 0, variance = 0;
        for (int i = 0; i < N; ++i) { est += b[i] * vals[i]; variance += b[i] * rhs[i]; }
        variance = Math.Max(0, variance + b[N]);
        return (est, variance, N);
    }

    // ═══════════════════ 加速：2D 空间网格索引 + 预取邻域的估值 ═══════════════════
    // 原 FindNearestSamples 对每个格网结点线性扫全部样本（O(结点×样本)），大数据极慢。
    // 下面用均匀网格桶做近邻查询（O(结点×局部密度)），并按算法码分派 IDW/OK/SK/UK。

    /// <summary>样本 2D（X/Y）均匀网格索引：环形扩张近邻查询，配合 maxK 提前收敛。</summary>
    public sealed class SampleGrid2D
    {
        private readonly List<SampleRecord> _samples;
        private readonly double _minX, _minY, _cell;
        private readonly int _nx, _ny;
        private readonly List<int>?[] _buckets;

        public SampleGrid2D(List<SampleRecord> samples)
        {
            _samples = samples;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in samples)
            {
                if (s.X < minX) minX = s.X; if (s.X > maxX) maxX = s.X;
                if (s.Y < minY) minY = s.Y; if (s.Y > maxY) maxY = s.Y;
            }
            if (samples.Count == 0) { minX = minY = 0; maxX = maxY = 1; }
            _minX = minX; _minY = minY;
            double area = Math.Max(1e-6, (maxX - minX) * (maxY - minY));
            // 目标 ~1 样本/格：cell ≈ 平均点距；限制网格规模上限，防超密样本爆内存
            _cell = Math.Max(1e-6, Math.Sqrt(area / Math.Max(1, samples.Count)));
            _nx = Clamp((int)((maxX - minX) / _cell) + 1, 1, 4096);
            _ny = Clamp((int)((maxY - minY) / _cell) + 1, 1, 4096);
            _buckets = new List<int>?[_nx * _ny];
            for (int i = 0; i < samples.Count; ++i)
            {
                int cx = Clamp((int)((samples[i].X - _minX) / _cell), 0, _nx - 1);
                int cy = Clamp((int)((samples[i].Y - _minY) / _cell), 0, _ny - 1);
                int b = cx + cy * _nx;
                (_buckets[b] ??= new List<int>()).Add(i);
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>返回半径内、按距离升序、至多 maxK 个近邻 (dist, index)。环形扩张 + 提前收敛。</summary>
        public List<(double dist, int index)> FindNearest(double bx, double by, double bz, double radius, int maxK)
        {
            var hits = new List<(double dist, int index)>();
            double r2 = radius * radius;
            int cx = Clamp((int)((bx - _minX) / _cell), 0, _nx - 1);
            int cy = Clamp((int)((by - _minY) / _cell), 0, _ny - 1);
            // 环形扩张封顶：栅格外的环永远扫不到样本，纯空转。从任一(含被 Clamp 到边缘的)查询格出发，
            // Max(_nx,_ny) 已覆盖整张网。根治观测点稀疏/重合(如单点)时 _cell→0 致 maxSpan 爆炸(80/0.001≈8万)的卡死。
            int maxSpan = Math.Min((int)(radius / _cell) + 1, Math.Max(_nx, _ny));

            for (int R = 0; R <= maxSpan; ++R)
            {
                int x0 = cx - R, x1 = cx + R, y0 = cy - R, y1 = cy + R;
                for (int gy = y0; gy <= y1; ++gy)
                {
                    if (gy < 0 || gy >= _ny) continue;
                    for (int gx = x0; gx <= x1; ++gx)
                    {
                        if (gx < 0 || gx >= _nx) continue;
                        // 只扫“新环”（Chebyshev 距离 == R）的格，避免重复
                        if (R > 0 && gx != x0 && gx != x1 && gy != y0 && gy != y1) continue;
                        var bucket = _buckets[gx + gy * _nx];
                        if (bucket == null) continue;
                        foreach (int i in bucket)
                        {
                            var s = _samples[i];
                            double dx = s.X - bx, dy = s.Y - by, dz = s.Z - bz;
                            double d2 = dx * dx + dy * dy + dz * dz;
                            if (d2 <= r2) hits.Add((Math.Sqrt(d2), i));
                        }
                    }
                }
                // 已够 maxK 且下一环最近可能距离(R·cell)不可能更近 → 收敛
                if (hits.Count >= maxK)
                {
                    hits.Sort((a, b) => a.dist.CompareTo(b.dist));
                    double kth = hits[maxK - 1].dist;
                    if ((double)R * _cell >= kth) { hits.RemoveRange(maxK, hits.Count - maxK); return hits; }
                }
            }
            hits.Sort((a, b) => a.dist.CompareTo(b.dist));
            if (hits.Count > maxK) hits.RemoveRange(maxK, hits.Count - maxK);
            return hits;
        }
    }

    /// <summary>IDW（预取邻域版）。</summary>
    public static (double estimate, int samplesUsed) ComputeIdw(
        double bx, double by, double bz,
        List<SampleRecord> samples, string targetProp, double power,
        IReadOnlyList<(double dist, int index)> hits, int minSamples, double smoothing, double defaultValue)
    {
        if (hits.Count < minSamples) return (defaultValue, 0);
        double wSum = 0, vSum = 0; int used = 0;
        foreach (var (dist, i) in hits)
        {
            if (!samples[i].Properties.TryGetValue(targetProp, out double v)) continue;
            if (dist < 1e-9) return (v, 1);
            double w = 1.0 / Math.Pow(dist + smoothing, power);
            wSum += w; vSum += w * v; ++used;
        }
        if (wSum <= 0) return (defaultValue, 0);
        return (vSum / wSum, used);
    }

    /// <summary>
    /// 克里金统一实现（预取邻域版），按漂移阶数区分：
    ///   driftOrder=0 → 普通克里金 OK（仅常数漂移，Σλ=1）
    ///   driftOrder=1 → 泛克里金 UK 一阶（漂移基 1,x,y）
    ///   driftOrder=2 → 泛克里金 UK 二阶（1,x,y,x²,xy,y²）
    /// 漂移基用相对目标点的局部坐标，改善大坐标（CGCS2000）条件数。奇异回退 IDW。
    /// </summary>
    public static (double estimate, double variance, int samplesUsed) KrigingWithDrift(
        double bx, double by, double bz,
        List<SampleRecord> samples, string targetProp,
        VariogramConfig variogram, int driftOrder,
        IReadOnlyList<(double dist, int index)> hits, int minSamples, double defaultValue)
    {
        var idx = new List<int>(); var vals = new List<double>();
        foreach (var (_, i) in hits)
            if (samples[i].Properties.TryGetValue(targetProp, out double v)) { idx.Add(i); vals.Add(v); }
        int N = idx.Count;
        if (N < minSamples) return (defaultValue, double.NaN, 0);

        int p = driftOrder <= 0 ? 1 : (driftOrder == 1 ? 3 : 6);
        int M = N + p;
        var A = new double[M, M];
        var b = new double[M];
        Span<double> f = stackalloc double[6];

        for (int i = 0; i < N; ++i)
        {
            var si = samples[idx[i]];
            for (int j = 0; j < N; ++j)
            {
                if (i == j) { A[i, j] = 0; continue; }
                var sj = samples[idx[j]];
                double d = Distance3D(si.X, si.Y, si.Z, sj.X, sj.Y, sj.Z);
                A[i, j] = Gamma(variogram.Type, d, variogram.Nugget, variogram.Sill, variogram.Range);
            }
            DriftBasis(si.X - bx, si.Y - by, driftOrder, f);
            for (int k = 0; k < p; ++k) { A[i, N + k] = f[k]; A[N + k, i] = f[k]; }
            double dBlk = Distance3D(si.X, si.Y, si.Z, bx, by, bz);
            b[i] = Gamma(variogram.Type, dBlk, variogram.Nugget, variogram.Sill, variogram.Range);
        }
        DriftBasis(0, 0, driftOrder, f);   // 目标点相对坐标 = 0
        for (int k = 0; k < p; ++k) b[N + k] = f[k];

        var rhs = (double[])b.Clone();
        if (!GaussSolveInPlace(A, b, M))
        {
            var (e, n) = ComputeIdw(bx, by, bz, samples, targetProp, 2.0, hits, minSamples, 0, defaultValue);
            return (e, double.NaN, n);
        }
        double est = 0, variance = 0;
        for (int i = 0; i < N; ++i) { est += b[i] * vals[i]; variance += b[i] * rhs[i]; }
        for (int k = 0; k < p; ++k) variance += b[N + k] * rhs[N + k];
        return (est, Math.Max(0, variance), N);
    }

    private static void DriftBasis(double rx, double ry, int order, Span<double> f)
    {
        f[0] = 1;
        if (order >= 1) { f[1] = rx; f[2] = ry; }
        if (order >= 2) { f[3] = rx * rx; f[4] = rx * ry; f[5] = ry * ry; }
    }

    /// <summary>简单克里金 SK（已知全局均值 mean，无无偏约束；预取邻域版）。奇异回退 IDW。</summary>
    public static (double estimate, double variance, int samplesUsed) SimpleKriging(
        double bx, double by, double bz,
        List<SampleRecord> samples, string targetProp,
        VariogramConfig variogram, double mean,
        IReadOnlyList<(double dist, int index)> hits, int minSamples, double defaultValue)
    {
        var idx = new List<int>(); var vals = new List<double>();
        foreach (var (_, i) in hits)
            if (samples[i].Properties.TryGetValue(targetProp, out double v)) { idx.Add(i); vals.Add(v); }
        int N = idx.Count;
        if (N < minSamples) return (defaultValue, double.NaN, 0);

        double sill = variogram.Sill;
        var A = new double[N, N];
        var b = new double[N];
        for (int i = 0; i < N; ++i)
        {
            var si = samples[idx[i]];
            for (int j = 0; j < N; ++j)
            {
                if (i == j) { A[i, j] = sill; continue; }   // C(0) = sill
                var sj = samples[idx[j]];
                double d = Distance3D(si.X, si.Y, si.Z, sj.X, sj.Y, sj.Z);
                A[i, j] = sill - Gamma(variogram.Type, d, variogram.Nugget, variogram.Sill, variogram.Range);
            }
            double dBlk = Distance3D(si.X, si.Y, si.Z, bx, by, bz);
            b[i] = sill - Gamma(variogram.Type, dBlk, variogram.Nugget, variogram.Sill, variogram.Range);
        }
        var rhs = (double[])b.Clone();
        if (!GaussSolveInPlace(A, b, N))
        {
            var (e, n) = ComputeIdw(bx, by, bz, samples, targetProp, 2.0, hits, minSamples, 0, defaultValue);
            return (e, double.NaN, n);
        }
        double est = mean, variance = sill;
        for (int i = 0; i < N; ++i) { est += b[i] * (vals[i] - mean); variance -= b[i] * rhs[i]; }
        return (est, Math.Max(0, variance), N);
    }

    // 高斯消元（带部分主元）。失败返回 false。结果原地写入 rhs。
    private static bool GaussSolveInPlace(double[,] A, double[] rhs, int n)
    {
        for (int k = 0; k < n; ++k)
        {
            // 选主元
            int piv = k;
            double pivMax = Math.Abs(A[k, k]);
            for (int i = k + 1; i < n; ++i)
                if (Math.Abs(A[i, k]) > pivMax) { piv = i; pivMax = Math.Abs(A[i, k]); }
            if (pivMax < 1e-12) return false;
            if (piv != k)
            {
                for (int j = k; j < n; ++j) (A[k, j], A[piv, j]) = (A[piv, j], A[k, j]);
                (rhs[k], rhs[piv]) = (rhs[piv], rhs[k]);
            }
            // 消元
            double diag = A[k, k];
            for (int i = k + 1; i < n; ++i)
            {
                double f = A[i, k] / diag;
                for (int j = k; j < n; ++j) A[i, j] -= f * A[k, j];
                rhs[i] -= f * rhs[k];
            }
        }
        // 回代
        for (int i = n - 1; i >= 0; --i)
        {
            double s = rhs[i];
            for (int j = i + 1; j < n; ++j) s -= A[i, j] * rhs[j];
            rhs[i] = s / A[i, i];
        }
        return true;
    }
}
