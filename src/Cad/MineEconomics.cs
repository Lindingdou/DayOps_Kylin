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

    /// <summary>
    /// 泰勒规则【应用】服务年限: T=6.5·R^0.25 夹于 [5,60] 年(忠实原 PitEvaluator 的域约束 Min(60,Max(5,·))——
    /// 矿山服务年限工程上不 &lt;5(太短不经济)或 &gt;60(过长不确定)。R≤0→0。境界/规划报寿命用此夹后值。
    /// </summary>
    public static double TaylorServiceLifeYears(double reserveMt)
        => reserveMt > 1e-9 ? Math.Min(60.0, Math.Max(5.0, TaylorMineLifeYears(reserveMt))) : 0.0;

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

    /// <summary>经济合理剥采比 n经 的四种确定原则(忠实原 PitScheme.EconParams.EconRatioMethod)。</summary>
    public enum EconRatioMethod
    {
        CostComparison,      // 成本比较法(替代原则): n=(C_D − a)/b
        Price,               // 价格法:                n=(d − a)/b
        PriceProfit,         // 价格法 + 盈利:          n=[d − (a+e)]/b
        PriceProfitReclaim,  // 价格法 + 盈利 + 复垦:    n=[d − (a+e+c)]/b
    }

    /// <summary>
    /// 按所选原则算经济合理剥采比 n经 (m³/t, 体积口径; 忠实原 EconParams.ComputeEconRatio 四式)。
    /// a=露天纯采矿成本(元/t), b=剥离成本(元/m³), d=原煤售价(元/t), C_D=地下采矿成本(元/t),
    /// e=单位最低盈利(元/t), c=分摊复垦费(元/t)。b≤0(剥离成本非正)→ null。
    /// </summary>
    public static double? AllowableStrippingRatio(EconRatioMethod method,
        double miningCost, double stripCost, double price = 0,
        double undergroundCost = 0, double minProfit = 0, double reclaimCost = 0)
    {
        if (stripCost <= 0) return null;
        return method switch
        {
            EconRatioMethod.CostComparison     => (undergroundCost - miningCost) / stripCost,
            EconRatioMethod.Price              => (price - miningCost) / stripCost,
            EconRatioMethod.PriceProfit        => (price - (miningCost + minProfit)) / stripCost,
            EconRatioMethod.PriceProfitReclaim => (price - (miningCost + minProfit + reclaimCost)) / stripCost,
            _ => null,
        };
    }
}
