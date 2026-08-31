using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  务实统计预测（忠实移植原 GeoDataBase.Equipment.ForecastModels；不引入重型 ML）。
//    · 最小二乘线性趋势（斜率 + R²）
//    · EWMA 指数平滑（贴近近期、抗噪）
//    · 按 R² 融合趋势与 EWMA → 点预测 + 未来路径
//    · 残差标准差 → 预测区间（§2.4 越远越宽 σ_k²=σ_res²·(1+1/n+(x_k−x̄)²/Sxx)）；|res|>2σ → 异常
//    · 可选 Holt 双指数（动态水平+趋势）
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>预测内核选择。</summary>
public enum ForecastMethod
{
    /// <summary>趋势(LSQ)+EWMA 按 R² 融合（默认，原实现）。</summary>
    Fusion,
    /// <summary>Holt 双指数：动态水平+趋势递推。</summary>
    Holt,
}

public sealed class ForecastResult
{
    public double Next { get; set; }            // 下一期点预测
    public double[] Path { get; set; } = Array.Empty<double>(); // 未来 horizon 期路径
    public double ResidualStd { get; set; }     // 残差标准差（常宽 95% 区间，向后兼容）
    /// <summary>每步 95% 置信半宽（越远越宽）。长度=horizon；未取时为空。</summary>
    public double[] IntervalHalfWidth { get; set; } = Array.Empty<double>();
    public double Slope { get; set; }           // 每期变化量
    public double R2 { get; set; }              // 趋势拟合优度
    public string TrendLabel { get; set; } = "平稳";
    public int AnomalyCount { get; set; }       // 历史异常期数（|残差|>2σ）
    public string Method { get; set; } = "";    // 所用方法（供 UI 标注来源）

    /// <summary>取第 k 步（0-based）的 95% 置信半宽；无增长区间时回落常宽。</summary>
    public double HalfWidthAt(int k)
    {
        if (IntervalHalfWidth.Length > 0)
            return IntervalHalfWidth[Math.Clamp(k, 0, IntervalHalfWidth.Length - 1)];
        return 1.96 * ResidualStd;
    }
}

public static class ForecastModels
{
    /// <summary>预测结果 → CSV（元信息 + 历史序列 + 未来 horizon 期点预测 + 95% 区间）。</summary>
    public static string PathToCsv(IReadOnlyList<double> history, ForecastResult r)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append($"# method={r.Method.Replace(",", ";")} slope={r.Slope.ToString("0.###", inv)} r2={r.R2.ToString("0.###", inv)} trend={r.TrendLabel} anomalies={r.AnomalyCount}\n");
        sb.Append("index,kind,value,lower95,upper95\n");
        int n = history?.Count ?? 0;
        for (int i = 0; i < n; i++) sb.Append($"{i},history,{history![i].ToString("0.###", inv)},,\n");
        for (int k = 0; k < r.Path.Length; k++)
        {
            double hw = r.HalfWidthAt(k);
            sb.Append($"{n + k},forecast,{r.Path[k].ToString("0.###", inv)},{System.Math.Max(0, r.Path[k] - hw).ToString("0.###", inv)},{(r.Path[k] + hw).ToString("0.###", inv)}\n");
        }
        return sb.ToString();
    }

    /// <summary>趋势+EWMA 融合 / Holt 双指数预测。series 按时间升序。</summary>
    public static ForecastResult Forecast(IReadOnlyList<double> series, int horizon,
        double alpha = 0.4, ForecastMethod method = ForecastMethod.Fusion, double beta = 0.2)
    {
        horizon = Math.Max(1, horizon);
        if (series == null || series.Count == 0)
            return new ForecastResult { Path = new double[horizon], Method = "无数据" };
        int n = series.Count;

        double mean = series.Average();
        if (n < 3)
        {
            var flat = Enumerable.Repeat(mean, horizon).ToArray();
            double sd0 = n >= 2 ? Math.Sqrt(series.Sum(v => (v - mean) * (v - mean)) / (n - 1)) : 0;
            return new ForecastResult
            {
                Next = mean, Path = flat, ResidualStd = sd0,
                IntervalHalfWidth = Enumerable.Repeat(1.96 * sd0, horizon).ToArray(),
                TrendLabel = "样本不足", Method = $"均值（n={n}）"
            };
        }

        // ── 最小二乘趋势 y = a + b·x, x=0..n-1 ──
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++) { sx += i; sy += series[i]; sxx += (double)i * i; sxy += (double)i * series[i]; }
        double denom = n * sxx - sx * sx;
        double b = Math.Abs(denom) < 1e-9 ? 0 : (n * sxy - sx * sy) / denom;
        double a = (sy - b * sx) / n;

        double ssRes = 0, ssTot = 0;
        for (int i = 0; i < n; i++)
        {
            double pred = a + b * i, res = series[i] - pred;
            ssRes += res * res; ssTot += (series[i] - mean) * (series[i] - mean);
        }
        double residualStd = Math.Sqrt(ssRes / Math.Max(1, n - 2));
        double r2 = ssTot > 1e-9 ? Math.Clamp(1 - ssRes / ssTot, 0, 1) : 0;

        // EWMA
        double ewma = series[0];
        for (int i = 1; i < n; i++) ewma = alpha * series[i] + (1 - alpha) * ewma;

        var path = new double[horizon];
        double slope = b;
        string methodTag;

        if (method == ForecastMethod.Holt)
        {
            double level = series[0];
            double trend = (series[n - 1] - series[0]) / (n - 1);
            double holtSsRes = 0; int holtCnt = 0;
            for (int t = 1; t < n; t++)
            {
                double oneStep = level + trend;
                double err = series[t] - oneStep;
                holtSsRes += err * err; holtCnt++;
                double prevLevel = level;
                level = alpha * series[t] + (1 - alpha) * (level + trend);
                trend = beta * (level - prevLevel) + (1 - beta) * trend;
            }
            for (int k = 0; k < horizon; k++)
                path[k] = Math.Max(0, level + (k + 1) * trend);
            residualStd = holtCnt > 0 ? Math.Sqrt(holtSsRes / Math.Max(1, holtCnt - 1)) : residualStd;
            slope = trend;
            methodTag = $"Holt 双指数（α={alpha:0.0}, β={beta:0.0}, 末端趋势 {trend:+0.0;-0.0}/期）";
        }
        else
        {
            double w = Math.Clamp(r2, 0.2, 0.8);
            for (int k = 0; k < horizon; k++)
            {
                double trendV = a + b * (n + k);
                path[k] = Math.Max(0, w * trendV + (1 - w) * ewma);
            }
            methodTag = $"趋势+EWMA 融合（R²={r2:0.00}, 斜率 {b:+0.0;-0.0}/期）";
        }

        // ── 越远越宽区间：σ_k² = σ_res²·(1 + 1/n + (x_k−x̄)²/Sxx) ──
        double xbar = (n - 1) / 2.0;
        double sxxCentered = sxx - sx * sx / n;
        var halfWidth = new double[horizon];
        for (int k = 0; k < horizon; k++)
        {
            double xk = n + k;
            double lev = 1 + 1.0 / n + (sxxCentered > 1e-9 ? (xk - xbar) * (xk - xbar) / sxxCentered : 0);
            halfWidth[k] = 1.96 * residualStd * Math.Sqrt(Math.Max(1, lev));
        }

        // 异常识别（对 LSQ 趋势的残差越 2σ）
        int anomalies = 0;
        if (residualStd > 1e-9)
            for (int i = 0; i < n; i++)
                if (Math.Abs(series[i] - (a + b * i)) > 2 * residualStd) anomalies++;

        double rel = mean > 1e-9 ? slope / mean : 0;
        string trendLabel = rel > 0.01 ? "上升" : rel < -0.01 ? "下降" : "平稳";

        return new ForecastResult
        {
            Next = path[0], Path = path, ResidualStd = residualStd,
            IntervalHalfWidth = halfWidth,
            Slope = slope, R2 = r2, TrendLabel = trendLabel, AnomalyCount = anomalies,
            Method = methodTag,
        };
    }
}
