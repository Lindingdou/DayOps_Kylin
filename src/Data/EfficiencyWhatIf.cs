using System;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  设备效能 What-if 提升路径模拟 —— 忠实原 EquipmentForecastWindow「What-if 提升路径模拟器」:
//  基线产能 × (1 + Σ 杠杆贡献 × 协同衰减)。四杠杆(占比按基线):
//    l1 故障降低比例 → c1 = l1 × faultShare(故障工时占计划工时比, 解锁工时)
//    l2 出动率提升   → c2 = l2 (直接)
//    l3 装载效率     → c3 = l3 (直接)
//    l4 运距优化     → c4 = l4 × 0.6 (仅 60% 转化为产能, 其余在卡车侧)
//  actualGain = (c1+c2+c3+c4) × SynergyDecay(0.85, 多杠杆协同衰减防理论膨胀)。
//  纯逻辑、可单测。杠杆为 0..1 比例(15% → 0.15)。
// ─────────────────────────────────────────────────────────────────────────────

public static class EfficiencyWhatIf
{
    public const double SynergyDecay = 0.85;
    public const double HaulConversion = 0.6;

    public sealed record Result(double C1, double C2, double C3, double C4,
        double RawGain, double ActualGain, double BaseOutput, double Simulated, double Delta);

    /// <summary>
    /// baseOutput=基线产能(如 万m³); faultShare=故障工时/计划工时(∈[0,1], 无则传 0.1);
    /// l1..l4=四杠杆提升比例(0..1)。返回各杠杆贡献 + 总增益 + 模拟产能 + 增量。
    /// </summary>
    public static Result Simulate(double baseOutput, double faultShare, double l1, double l2, double l3, double l4)
    {
        double fs = Math.Max(0, Math.Min(1, faultShare));
        double c1 = l1 * fs, c2 = l2, c3 = l3, c4 = l4 * HaulConversion;
        double raw = c1 + c2 + c3 + c4;
        double gain = raw * SynergyDecay;
        double sim = baseOutput * (1 + gain);
        return new Result(c1, c2, c3, c4, raw, gain, baseOutput, sim, sim - baseOutput);
    }
}
