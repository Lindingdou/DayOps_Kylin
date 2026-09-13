using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 月度计划方案「联合对比」评分（多维度方向感知归一 + 加权 → 综合评分/推荐）。
/// 仿 LongTermComparer：完成率偏差/峰值月产/月产变异 = 低优，均衡系数/设备利用贴合/推进合计 = 高优。
/// 权重取各方案 StDecisionWeights 的平均（UI 可调）。
/// </summary>
public static class ShortTermComparer
{
    private const double UtilTarget = 90; // 设备利用率目标（贴合 90% 最优；过低=闲置，过高=超负荷）

    /// <summary>对已求解方案打分回填 Result.CompositeScore；返回推荐（最高分且可行）方案名。</summary>
    public static string Score(IReadOnlyList<ShortTermPlan> plans)
    {
        var withR = plans.Where(p => p.Result != null).ToList();
        if (withR.Count == 0) return "—";

        double[] compDev = withR.Select(p => Math.Abs(p.Result!.CompletionRatePct - 100)).ToArray();
        double[] bal = withR.Select(p => p.Result!.BalanceCoef).ToArray();
        double[] utilFit = withR.Select(p => -Math.Abs(p.Result!.AvgEquipUtilPct - UtilTarget)).ToArray();
        double[] peak = withR.Select(p => p.Result!.PeakMonthCoalWanT).ToArray();
        double[] adv = withR.Select(p => p.Result!.AdvanceTotalM).ToArray();

        var w = AverageWeights(withR);
        double wsum = w.Sum <= 0 ? 1 : w.Sum;

        ShortTermPlan? best = null; double bestScore = -1;
        for (int i = 0; i < withR.Count; i++)
        {
            double s = NormLow(compDev, i) * w.Completion
                     + NormHigh(bal, i) * w.OutputBalance
                     + NormHigh(utilFit, i) * w.EquipUtil
                     + NormLow(peak, i) * w.PeakShaving
                     + NormHigh(adv, i) * w.AdvanceAttain;
            double score = s / wsum * 100;
            withR[i].Result!.CompositeScore = Math.Round(score, 0);
            if (withR[i].Result!.Ok && score > bestScore) { bestScore = score; best = withR[i]; }
        }
        if (best == null)
            foreach (var p in withR)
                if (p.Result!.CompositeScore > bestScore) { bestScore = p.Result!.CompositeScore; best = p; }

        return best?.Name ?? "—";
    }

    private static StDecisionWeights AverageWeights(IReadOnlyList<ShortTermPlan> plans) => new()
    {
        Completion = plans.Average(p => p.Decision.Completion),
        OutputBalance = plans.Average(p => p.Decision.OutputBalance),
        EquipUtil = plans.Average(p => p.Decision.EquipUtil),
        PeakShaving = plans.Average(p => p.Decision.PeakShaving),
        AdvanceAttain = plans.Average(p => p.Decision.AdvanceAttain),
    };

    private static double NormHigh(double[] a, int i)
    { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NormLow(double[] a, int i)
    { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }
}
