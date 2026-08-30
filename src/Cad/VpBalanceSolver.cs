using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 剥采比均衡求解内核（纯算，无 GUI）——VP 曲线上的分阶段均衡。移植自原 PlanLib.StrippingBalance.VpBalanceSolver。
///
/// 给一条真实累计曲线 (x=累计采出量 万t, y=累计剥离量 万m³)，用 K 段折线包在下面：
/// 折线处处不低于实际曲线（低了=欠剥），且折线与曲线之间面积（超前剥离的时间积分）最小。
/// 每段斜率 = 该阶段均衡生产剥采比。用户唯一要定的是 K（分几段），null 则按形态自动建议。
/// </summary>
public static class VpBalanceSolver
{
    /// <summary>阶段数上限。</summary>
    public const int MaxStages = 6;

    /// <summary>一段均衡阶段：顶点区间 [A,B]、该段均衡生产剥采比、段内峰值超前剥离。</summary>
    public readonly record struct Segment(int A, int B, double RatioM3PerT, double PeakLeadWanM3);

    public sealed class Result
    {
        public List<int> Breakpoints = new() { 0 };   // 断点（含 0 与 M）
        public List<Segment> Segments = new();
        public int UsedK;
        public double TotalLeadArea;                  // 总超前剥离面积（只用于比较）
        public bool Ok => Segments.Count > 0;
    }

    /// <summary>按 K 段均衡拟合。stageCount=null 时自动建议 K。xs/ys 须单调不减且等长。</summary>
    public static Result Solve(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int? stageCount)
    {
        var r = new Result();
        if (xs == null || ys == null || xs.Count != ys.Count || xs.Count < 2) return r;

        int M = xs.Count - 1;
        int maxK = Math.Min(M, MaxStages);
        int K = stageCount.HasValue ? Math.Clamp(stageCount.Value, 1, maxK) : SuggestK(xs, ys, maxK);
        var (bps, area) = FitK(xs, ys, K);

        r.Breakpoints = bps;
        r.UsedK = K;
        r.TotalLeadArea = area;
        for (int s = 0; s < bps.Count - 1; s++)
        {
            int a = bps[s], b = bps[s + 1];
            double dx = xs[b] - xs[a];
            double ratio = dx > 1e-9 ? (ys[b] - ys[a]) / dx : 0;
            r.Segments.Add(new Segment(a, b, ratio, SegPeakGap(xs, ys, a, b)));
        }
        return r;
    }

    /// <summary>均衡折线在累计采出量 x 处的累计剥离量（断点间线性插值）。</summary>
    public static double BalanceY(IReadOnlyList<double> xs, IReadOnlyList<double> ys, IReadOnlyList<int> bps, double x)
    {
        if (bps == null || bps.Count < 2) return xs.Count > 0 ? ys[0] : 0;
        for (int s = 0; s < bps.Count - 1; s++)
        {
            int a = bps[s], b = bps[s + 1];
            if (x <= xs[b] + 1e-9 || s == bps.Count - 2)
            {
                double dx = xs[b] - xs[a];
                double slope = dx > 1e-9 ? (ys[b] - ys[a]) / dx : 0;
                return ys[a] + slope * (x - xs[a]);
            }
        }
        return ys[^1];
    }

    /// <summary>DP：把顶点 0..M 分成 K 段，使总超前剥离面积最小；返回断点(含 0 与 M)与总面积。</summary>
    public static (List<int> bps, double area) FitK(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int K)
    {
        int M = xs.Count - 1;
        K = Math.Clamp(K, 1, M);
        var dp = new double[K + 1, M + 1];
        var par = new int[K + 1, M + 1];
        for (int k = 0; k <= K; k++)
            for (int j = 0; j <= M; j++) dp[k, j] = double.PositiveInfinity;
        dp[0, 0] = 0;
        for (int k = 1; k <= K; k++)
            for (int j = k; j <= M; j++)
                for (int m = k - 1; m < j; m++)
                {
                    if (double.IsInfinity(dp[k - 1, m])) continue;
                    double c = dp[k - 1, m] + SegArea(xs, ys, m, j);
                    if (c < dp[k, j]) { dp[k, j] = c; par[k, j] = m; }
                }
        var bps = new List<int>();
        int cur = M;
        for (int k = K; k >= 1; k--) { bps.Add(cur); cur = par[k, cur]; }
        bps.Add(0);
        bps.Reverse();
        return (bps, dp[K, M]);
    }

    /// <summary>按曲线形态建议期数：取使总超前剥离 ≤ 单段 20% 的最小 K（最多 MaxStages）。</summary>
    public static int SuggestK(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int maxK)
    {
        double a1 = FitK(xs, ys, 1).area;
        if (a1 <= 1e-6) return 1;
        for (int k = 1; k <= maxK; k++)
            if (FitK(xs, ys, k).area <= 0.20 * a1) return k;
        return maxK;
    }

    /// <summary>某段 [a,b] 内折线(弦)高出实际曲线的面积（≈该段超前剥离的时间积分）。</summary>
    public static double SegArea(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int a, int b)
    {
        double dx = xs[b] - xs[a];
        if (dx <= 1e-9) return 0;
        double slope = (ys[b] - ys[a]) / dx;
        double area = 0;
        for (int t = a; t < b; t++)
        {
            double gA = ys[a] + slope * (xs[t] - xs[a]) - ys[t];
            double gB = ys[a] + slope * (xs[t + 1] - xs[a]) - ys[t + 1];
            area += 0.5 * (gA + gB) * (xs[t + 1] - xs[t]);
        }
        return area;
    }

    /// <summary>某段 [a,b] 内折线高出实际曲线的最大竖直差 ≈ 该阶段峰值超前剥离量(万m³)。</summary>
    public static double SegPeakGap(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int a, int b)
    {
        double dx = xs[b] - xs[a];
        if (dx <= 1e-9) return 0;
        double slope = (ys[b] - ys[a]) / dx;
        double peak = 0;
        for (int t = a + 1; t < b; t++)
        {
            double g = ys[a] + slope * (xs[t] - xs[a]) - ys[t];
            if (g > peak) peak = g;
        }
        return peak;
    }
}
