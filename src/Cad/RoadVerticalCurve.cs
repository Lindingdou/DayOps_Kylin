using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>纵断面竖曲线平滑结果。</summary>
public sealed class VerticalCurveResult
{
    /// <summary>平滑后的纵断面 (弧长 s, 标高 z)，竖曲线处插入了抛物线采样点。</summary>
    public List<(double S, double Z)> Profile { get; } = new();
    /// <summary>插入的竖曲线条数。</summary>
    public int Count { get; set; }
    /// <summary>实际达到的最小竖曲线半径 m（段长放不下被 clamp 后）。</summary>
    public double MinRadiusM { get; set; }
    /// <summary>半径不达标（clamp 到 &lt; R_v）的竖曲线数。</summary>
    public int Violations { get; set; }
}

/// <summary>
/// 纵断面竖曲线平滑（GBJ22-87 阶段③）：相邻纵坡代数差 &gt; trigger 的变坡点插抛物线竖曲线，
/// 长度 Lv = R_v·|Δi|（段长放不下则 clamp，半径降低计 violation）。
/// 抛物线在 BVC/EVC 与两侧直坡同点 → 总降不变、端点 Z 自动保持。
/// 纯几何、(弧长 s, 标高 z) 域、可单测。
/// </summary>
public static class RoadVerticalCurve
{
    public static VerticalCurveResult Smooth(
        IReadOnlyList<double> s, IReadOnlyList<double> z, double triggerPct, double rV, int samplesPerVc = 8)
    {
        var res = new VerticalCurveResult();
        int n = s.Count;
        if (n < 3 || rV <= 1e-9)
        {
            for (int k = 0; k < n; k++) res.Profile.Add((s[k], z[k]));
            return res;
        }

        double trig = triggerPct / 100.0;
        var g = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            double ds = s[k + 1] - s[k];
            g[k] = ds > 1e-9 ? (z[k + 1] - z[k]) / ds : 0.0;
        }

        double minR = double.MaxValue;
        int count = 0, viol = 0;
        var outP = res.Profile;
        outP.Add((s[0], z[0]));
        for (int k = 1; k <= n - 2; k++)
        {
            double g1 = g[k - 1], g2 = g[k];
            double dg = Math.Abs(g2 - g1);
            if (dg <= trig) { outP.Add((s[k], z[k])); continue; }     // 变坡小 → 不设竖曲线

            double Lv = rV * dg;
            double half = Math.Min(Lv / 2.0, 0.49 * Math.Min(s[k] - s[k - 1], s[k + 1] - s[k]));
            if (half <= 1e-9) { outP.Add((s[k], z[k])); continue; }

            double rAch = (2.0 * half) / dg;                          // clamp 后实际半径
            if (rAch < rV - 1e-6) viol++;
            if (rAch < minR) minR = rAch;
            count++;

            double sb = s[k] - half;
            double zb = z[k] - g1 * half;                            // BVC（沿进坡 g1 回退 half）
            double span = 2.0 * half;
            outP.Add((sb, zb));
            for (int j = 1; j <= samplesPerVc; j++)                   // 抛物线 z = zb + g1·x + (g2−g1)/(2Lv)·x²
            {
                double x = span * j / samplesPerVc;
                double zz = zb + g1 * x + (g2 - g1) / (2.0 * span) * x * x;
                outP.Add((sb + x, zz));
            }
        }
        outP.Add((s[n - 1], z[n - 1]));

        res.Count = count;
        res.MinRadiusM = (minR == double.MaxValue) ? 0.0 : minR;
        res.Violations = viol;
        return res;
    }
}
