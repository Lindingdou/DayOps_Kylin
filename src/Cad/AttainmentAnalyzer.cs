using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>达成缺口的一项归因。</summary>
public sealed class GapItem
{
    public string Cause = "";
    public double VolumeM3;
}

/// <summary>产量达成分解。</summary>
public sealed class AttainmentBreakdown
{
    public double PlanM3, ActualM3, HourGapM3, RateGapM3;
    public List<GapItem> Items = new();
    public double GapM3 => Math.Max(0, PlanM3 - ActualM3);
    public double AttainPct => PlanM3 <= 1e-6 ? 100 : Math.Round(ActualM3 / PlanM3 * 100, 0);
    public double ExplainedPct => GapM3 <= 1 ? 100 : Math.Round(Items.Sum(x => x.VolumeM3) / GapM3 * 100, 0);
}

/// <summary>
/// 产量达成分析核 —— 移植自原 `TaskLib.AttainmentAnalyzer` 的 gap 分解公式（工时缺口=计划班产×少干工时、
/// 效率缺口=实际工时×班产差；故障/检修按工时归因）。原吃 `ProductionTask`(含设备/原因码域对象)，
/// 此抽标量输入(计划量/实际量/计划工时/实际工时/故障h/检修h)；**设备布置·运力不足与等待原因码归因需域模型, 未移植(记录)**。
/// </summary>
public static class AttainmentAnalyzer
{
    public static AttainmentBreakdown Of(double planM3, double actualM3, double plannedHours, double actualHours,
                                         double faultHours = 0, double maintHours = 0)
    {
        double plan = Math.Max(0, planM3), act = Math.Max(0, actualM3);
        double hPlan = Math.Max(0, plannedHours), hAct = Math.Max(0, actualHours);
        double capPlan = hPlan > 1e-6 ? plan / hPlan : 0;
        double capAct = hAct > 1e-6 ? act / hAct : 0;
        double hourGap = capPlan * (hPlan - hAct);
        double rateGap = hAct * (capPlan - capAct);

        var res = new AttainmentBreakdown { PlanM3 = plan, ActualM3 = act, HourGapM3 = hourGap, RateGapM3 = rateGap };
        double gap = plan - act;
        if (gap <= 1 || capPlan <= 1e-6) return res;   // 超额/无缺口：不硬凑归因

        double hourBudget = Math.Max(0, hourGap), used = 0;
        void AddHour(string cause, double hours)
        {
            if (hours <= 1e-6 || hourBudget - used <= 1e-6) return;
            double v = Math.Min(hours * capPlan, hourBudget - used);
            if (v <= 1e-6) return;
            used += v;
            res.Items.Add(new GapItem { Cause = cause, VolumeM3 = Math.Round(v) });
        }
        AddHour("突发停机（非计划故障）", faultHours);
        AddHour("档期外检修", maintHours);

        if (rateGap > 1) res.Items.Add(new GapItem { Cause = "执行效率", VolumeM3 = Math.Round(rateGap) });
        return res;
    }

    /// <summary>多条分解合并：量相加、归因按原因合并（先分解后合并，见原注释）。</summary>
    public static AttainmentBreakdown Combine(IEnumerable<AttainmentBreakdown> parts)
    {
        var res = new AttainmentBreakdown();
        var byCause = new Dictionary<string, double>();
        foreach (var p in parts)
        {
            res.PlanM3 += p.PlanM3; res.ActualM3 += p.ActualM3;
            res.HourGapM3 += p.HourGapM3; res.RateGapM3 += p.RateGapM3;
            foreach (var it in p.Items)
                byCause[it.Cause] = byCause.TryGetValue(it.Cause, out var v) ? v + it.VolumeM3 : it.VolumeM3;
        }
        foreach (var kv in byCause.OrderByDescending(k => k.Value))
            res.Items.Add(new GapItem { Cause = kv.Key, VolumeM3 = kv.Value });
        return res;
    }
}
