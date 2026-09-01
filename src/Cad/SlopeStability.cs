using System;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 边坡稳定性 —— 忠实移植原 MineAssLib BenchTemplateResolver.CohesionlessFactorOfSafety。
/// 无黏聚力(c=0)整体边坡安全系数下界 F = tanφ / tanβ（φ=内摩擦角, β=整体帮坡角）。保守估计(有黏聚力时更高)。
/// 平坡(β→0)→ +∞(无限稳)。规范安全阈 F ≥ 1.30。纯逻辑、可单测。
/// </summary>
public static class SlopeStability
{
    /// <summary>规范整体边坡安全系数阈值(原 LocationProcessMap "F ≥ 1.30")。</summary>
    public const double SafeThreshold = 1.30;

    /// <summary>无黏聚力安全系数 F = tanφ/tanβ。β≤0(平坡)→ +∞。φ 应 &lt; β 才 &lt;1(不稳)。</summary>
    public static double CohesionlessFoS(double overallAngleDeg, double frictionAngleDeg)
    {
        double tb = Math.Tan(overallAngleDeg * Math.PI / 180.0);
        if (tb <= 1e-9) return double.PositiveInfinity;   // 平坡 → 无限稳
        return Math.Tan(frictionAngleDeg * Math.PI / 180.0) / tb;
    }

    /// <summary>是否满足规范(F ≥ 1.30)。</summary>
    public static bool IsSafe(double fos) => fos >= SafeThreshold;
}
