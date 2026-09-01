using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 设备可靠性 —— Weibull 失效分布拟合（忠实原 EquipmentAnalysisWindow §1.5 「Weibull 失效率」）：
/// 对故障间隔(天)做**中位秩回归**(Bernard F=(i−0.3)/(n+0.4))拟合形状 β/尺度 η；
/// β&lt;0.85 早期失效(磨合)、≤1.15 随机失效(偶发)、&gt;1.15 损耗失效(老化) → 浴盆定位。纯逻辑、可单测。
/// </summary>
public static class Reliability
{
    /// <summary>中位秩回归拟合 Weibull(β 形状, η 尺度)。intervals=正的故障间隔; n&lt;3 或退化返回 ok=false。</summary>
    public static (double beta, double eta, bool ok) WeibullFit(IReadOnlyList<double> intervals)
    {
        var t = new List<double>();
        if (intervals != null) foreach (var v in intervals) if (v > 0) t.Add(v);
        t.Sort();
        int n = t.Count;
        if (n < 3) return (0, 0, false);
        // 线性化: ln(−ln(1−F)) = β·ln(t) − β·ln(η); F 用中位秩 (i−0.3)/(n+0.4)
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            double F = (i + 1 - 0.3) / (n + 0.4);
            double x = Math.Log(t[i]);
            double y = Math.Log(-Math.Log(1 - F));
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }
        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-12) return (0, 0, false);   // 全相等 → 无法回归
        double beta = (n * sxy - sx * sy) / denom;
        if (beta <= 1e-9 || double.IsNaN(beta) || double.IsInfinity(beta)) return (0, 0, false);
        double c = (sy - beta * sx) / n;   // c = −β·ln(η)
        double eta = Math.Exp(-c / beta);
        if (double.IsNaN(eta) || double.IsInfinity(eta) || eta <= 0) return (0, 0, false);
        return (beta, eta, true);
    }

    /// <summary>按形状 β 定位浴盆阶段(忠实原阈值 0.85 / 1.15)。</summary>
    public static string Phase(double beta) =>
        beta < 0.85 ? "早期失效期(磨合/质量)" : beta <= 1.15 ? "随机失效期(偶发)" : "损耗失效期(老化)";

    /// <summary>按各设备的故障日期序列(每设备升序)算**汇集**故障间隔(天): 逐设备相邻日期差, 池化。</summary>
    public static List<double> PooledIntervalsDays(IEnumerable<IReadOnlyList<DateTime>> perEquipmentDates)
    {
        var res = new List<double>();
        if (perEquipmentDates == null) return res;
        foreach (var dates in perEquipmentDates)
        {
            if (dates == null) continue;
            for (int i = 1; i < dates.Count; i++)
            {
                double d = (dates[i] - dates[i - 1]).TotalDays;
                if (d > 0) res.Add(d);
            }
        }
        return res;
    }
}
