using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 开采程序方案比选评分——移植自 PlanLib.BoundaryOptimization.ProgramComparer 的纯算法核。
/// 硬方向感知 min-max 归一 + 多准则加权 → 综合评分/推荐。
/// 峰值生产剥采比/基建剥离/达产时间 = 低优；内排率/服务年限/储量均衡/NPV = 高优。
/// 原类吃 `MiningProgramPlan.Result`(需求解链回填)；此抽纯函数吃 7 项指标向量, 权重/方向/归一逐字照搬。
/// </summary>
public static class ProgramComparer
{
    // 原权重（和为 1）
    public const double WPeak = 0.22, WBasic = 0.15, WInner = 0.15, WTtc = 0.10, WLife = 0.10, WBalance = 0.18, WNpv = 0.10;

    /// <summary>一套方案的 7 项指标（+ 名称、是否可行）。</summary>
    public readonly record struct Plan(
        string Name,
        double ProductionRatioPeak,   // 峰值生产剥采比（低优）
        double BasicStrippingYiM3,    // 基建剥离量（低优）
        double InnerDumpPct,          // 内排率（高优）
        double TimeToCapacityYears,   // 达产时间（低优）
        double ServiceLifeYears,      // 服务年限（高优）
        double ReserveBalanceCoef,    // 储量均衡系数（高优）
        double Npv,                   // 净现值（高优）
        bool Ok = true);

    public sealed class Scored
    {
        public string Name = "";
        public double CompositeScore;
        public bool Ok;
    }

    /// <summary>对方案打分（综合分 0..100），返回 (各方案分, 推荐方案名)。推荐 = 最高分且可行；全不可行则退最高分。</summary>
    public static (List<Scored> scored, string best) Score(IReadOnlyList<Plan> plans)
    {
        var scored = new List<Scored>();
        if (plans == null || plans.Count == 0) return (scored, "—");

        double[] peak = plans.Select(p => p.ProductionRatioPeak).ToArray();
        double[] basic = plans.Select(p => p.BasicStrippingYiM3).ToArray();
        double[] inner = plans.Select(p => p.InnerDumpPct).ToArray();
        double[] ttc = plans.Select(p => p.TimeToCapacityYears).ToArray();
        double[] life = plans.Select(p => p.ServiceLifeYears).ToArray();
        double[] bal = plans.Select(p => p.ReserveBalanceCoef).ToArray();
        double[] npv = plans.Select(p => p.Npv).ToArray();

        double wsum = WPeak + WBasic + WInner + WTtc + WLife + WBalance + WNpv;
        Scored? best = null; double bestScore = -1;

        for (int i = 0; i < plans.Count; i++)
        {
            double s = NormLow(peak, i) * WPeak + NormLow(basic, i) * WBasic + NormHigh(inner, i) * WInner
                     + NormLow(ttc, i) * WTtc + NormHigh(life, i) * WLife + NormHigh(bal, i) * WBalance
                     + NormHigh(npv, i) * WNpv;
            double score = Math.Round(s / wsum * 100, 0);
            var sc = new Scored { Name = plans[i].Name, CompositeScore = score, Ok = plans[i].Ok };
            scored.Add(sc);
            if (plans[i].Ok && score > bestScore) { bestScore = score; best = sc; }
        }
        if (best == null)
            foreach (var sc in scored)
                if (sc.CompositeScore > bestScore) { bestScore = sc.CompositeScore; best = sc; }

        return (scored, best?.Name ?? "—");
    }

    private static double NormHigh(double[] a, int i)
    { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NormLow(double[] a, int i)
    { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }
}
