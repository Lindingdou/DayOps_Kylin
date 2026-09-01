using System;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 矿山经济/时序评价基元 —— 忠实移植原 PlanLib PitEvaluator 的标准式(非硬编码占位)。
/// 泰勒规则服务年限、等额现金流 NPV。纯逻辑、可单测。
/// </summary>
public static class MineEconomics
{
    /// <summary>泰勒规则矿山服务年限 T ≈ 6.5·R^0.25(R=可采储量, 百万吨 Mt)。R≤0 → 0。经验式, 未给年产时估寿命。</summary>
    public static double TaylorMineLifeYears(double reserveMt)
        => reserveMt > 1e-9 ? 6.5 * Math.Pow(reserveMt, 0.25) : 0.0;

    /// <summary>年金现值系数 (1−(1+r)⁻ᵀ)/r(r=折现率, T=年限)。r≈0 时退化为 T。用于总净值等额分摊折现。</summary>
    public static double AnnuityPvFactor(double rate, double years)
    {
        if (years <= 0) return 0.0;
        if (Math.Abs(rate) < 1e-9) return years;
        return (1.0 - Math.Pow(1.0 + rate, -years)) / rate;
    }

    /// <summary>总净值按服务年限等额分摊后折现的 NPV = (净值/年限)·年金现值系数(忠实原 PitEvaluator DCF)。</summary>
    public static double NpvLevelized(double totalNet, double life, double rate)
        => life > 1e-9 ? (totalNet / life) * AnnuityPvFactor(rate, life) : 0.0;
}
