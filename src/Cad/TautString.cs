using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 走廊内的「拉紧绳」：在下包络 lo（必须剥）与上包络 hi（能力/天花板）之间，
/// 求一条【单调不减】【增量最平】的累计曲线（月度剥离量均衡）。
///
/// 手册上是拿累计剥离量曲线用直尺比着画；这里写成算法——从起点拉一根绳到终点，
/// 绳被"必须剥"顶起、被"能力"压住，绷紧后自然分段直线。对【任意凸代价】同时最优
/// （拉绳解经典性质：最小方差 / 最小峰值 / 最小相邻月跳动一次全拿）。
/// 前提: 剥离只能提前不能推后(R37) —— 故绳不许下行(sMin 从 0 起)。O(T²)。
/// 忠实原 MineAssLib.Driving.TautString。纯算法、可单测。
/// </summary>
public static class TautString
{
    /// <summary>绳的一个转折点（触边处）。</summary>
    public readonly struct Pivot
    {
        public readonly int Period;
        /// <summary>true = 触下包络（露煤紧迫）；false = 触上包络（能力吃紧）。</summary>
        public readonly bool OnLower;
        public Pivot(int period, bool onLower) { Period = period; OnLower = onLower; }
        public override string ToString() => $"第{Period}期{(OnLower ? "触底(露煤紧迫)" : "触顶(能力吃紧)")}";
    }

    /// <summary>
    /// 求解。lo/hi 都是【累计】量、长度 T+1（下标 0..T，0=期初），须单调不减且 lo ≤ hi。
    /// 返回累计曲线（长度 T+1），不可行返回 null 并给出原因。
    /// yEnd = 期末目标累计（通常 lo[T]）；绳被能力压住时实际会高于它，差额=结转的超前剥离储备, 如实返回。
    /// </summary>
    public static double[]? Solve(IReadOnlyList<double> lo, IReadOnlyList<double> hi, double yEnd,
                                  out string err, List<Pivot>? pivots = null)
    {
        err = "";
        int n = lo?.Count ?? 0;
        if (n < 2) { err = "期数不足（至少要有期初 + 1 期）"; return null; }
        if (hi == null || hi.Count != n) { err = "上下包络长度不一致"; return null; }

        // 可行性：走廊不倒挂 ⟺ 逐期 lo ≤ hi（充要，C=lo 本身即可行路径）。
        for (int t = 0; t < n; t++)
            if (lo![t] > hi[t] + 1e-6)
            {
                err = $"走廊在第{t}期倒挂：累计必须剥 {lo[t] / 1e4:0.#}万m³ > 累计能力 {hi[t] / 1e4:0.#}万m³"
                    + " —— 无可行计划（降煤量目标 / 加剥离能力 / 减备采保有月数）";
                return null;
            }

        var c = new double[n];
        c[0] = lo![0];
        int cur = 0, guard = 0;
        while (cur < n - 1)
        {
            if (++guard > n + 2) { err = "拉绳未收敛（内部错误：折点没有前进）"; return null; }
            double yc = c[cur];

            // 漏斗法：从锚点向前扫，维护【可行斜率区间】[sMin, sMax]。sMin 从 0 起（绳不许下行=R37）。
            // 区间被某期挤空的那刻，绳必须在【此前绑住它的那一期】折。
            double sMin = 0, sMax = double.PositiveInfinity;
            int tMin = -1, tMax = -1;
            int pivot = -1; double s = 0; bool onLower = false;
            for (int t = cur + 1; t < n; t++)
            {
                double dt = t - cur;
                double a = (lo[t] - yc) / dt, b = (hi[t] - yc) / dt;
                if (a > sMax + 1e-12) { s = sMax; pivot = tMax; onLower = false; break; }   // 下界顶穿上界 → 触顶那期折
                if (b < sMin - 1e-12) { s = sMin; pivot = tMin; onLower = true; break; }    // 上界压穿下界 → 触底那期折
                if (a > sMin) { sMin = a; tMin = t; }
                if (b < sMax) { sMax = b; tMax = t; }
            }
            if (pivot < 0)
            {
                // 漏斗没塌：直奔终点的直线若落在 [sMin,sMax] 内就一路到底；否则仍在绑住它的那期折。
                double sEnd = (yEnd - yc) / (n - 1 - cur);
                if (sEnd < sMin) { s = sMin; pivot = tMin; onLower = true; }
                else if (sEnd > sMax) { s = sMax; pivot = tMax; onLower = false; }
                else { s = sEnd; pivot = n - 1; }
            }
            if (pivot < 0) { pivot = n - 1; s = Math.Max(0, s); }
            if (pivot <= cur) { err = "拉绳未收敛（折点没有前进）"; return null; }

            for (int t = cur + 1; t <= pivot; t++) c[t] = yc + s * (t - cur);
            if (pivot < n - 1) pivots?.Add(new Pivot(pivot, onLower));
            cur = pivot;
        }
        return c;
    }

    /// <summary>累计曲线 → 逐期增量（长度 T）。</summary>
    public static double[] ToIncrements(IReadOnlyList<double> cum)
    {
        int n = cum.Count;
        var d = new double[Math.Max(0, n - 1)];
        for (int t = 1; t < n; t++) d[t - 1] = cum[t] - cum[t - 1];
        return d;
    }

    /// <summary>增量的变异系数（越小越平）。全 0 返回 0。</summary>
    public static double Cv(IReadOnlyList<double> inc)
    {
        int n = inc.Count; if (n == 0) return 0;
        double mean = 0; foreach (var v in inc) mean += v; mean /= n;
        if (Math.Abs(mean) < 1e-9) return 0;
        double var2 = 0; foreach (var v in inc) var2 += (v - mean) * (v - mean);
        return Math.Sqrt(var2 / n) / Math.Abs(mean);
    }
}
