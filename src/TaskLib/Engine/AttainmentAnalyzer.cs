// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/AttainmentAnalyzer.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  达成缺口的归因分解 —— 回答"这个班干成这样，到底是谁的问题"。
//
//  在它出现之前，达成度评价只给一个百分比 + 一串人填的原因码：
//  「主采面·东 68%，原因：设备故障、运力不足」—— 两个原因各占多少？没人答得上来。
//  于是"该补车还是该修设备"这个决定只能凭感觉，而这正是评价这一步存在的理由。
//
//  ── 恒等式（先分解，再归因）──
//    计划 P = cap计划 × H计划          实绩 A = cap实际 × H实际
//    缺口 G = P − A
//           = cap计划 × (H计划 − H实际)     ← ① 工时缺口：少干了几个小时
//           + H实际 × (cap计划 − cap实际)   ← ② 效率缺口：单位时间干得慢了
//  这是**恒等变形**，两项之和严格等于缺口，不会因为归因不全而丢量。
//
//  ── 再把两项各自归到原因上 ──
//    ① 工时缺口 ← 非计划故障停机 / 档期外检修 / 等待类原因码（爆破、卸点拥堵、缺料、缺勤）
//    ② 效率缺口 ← 配车不足（设备布置：铲等车）/ 其余算执行效率
//
//  ★ 归不上的一律进「未解释」并**如实显示**。凑不满就是凑不满 ——
//    把残差摊进最近的一个原因里，会让"运力不足占 40%"这种结论看着精确、实则编的。
//    同 [[green-audit-blind-spots]]：数字自洽不等于数字是对的。
//
//  ★ 计划里已经扣过的不再算缺口：检修档期与爆破清场在装箱时就从时窗里减掉了
//    （见 WorkWindowCalc），它们压根没进 H计划。这里只解释**计划之外**发生的事。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>缺口的一项归因。</summary>
public sealed class GapItem
{
    public string Cause { get; init; } = "";
    /// <summary>这一项解释掉多少 m³ 实方。</summary>
    public double VolumeM3 { get; init; }
    /// <summary>怎么算出来的（界面上要能追到算式，不能只给个数）。</summary>
    public string Basis { get; init; } = "";
}

/// <summary>一条任务（或一组任务聚合）的达成缺口分解。</summary>
public sealed class AttainmentBreakdown
{
    public double PlanM3 { get; init; }
    public double ActualM3 { get; init; }

    /// <summary>缺口 = 计划 − 实绩。<b>负数表示超额</b>，此时不做归因。</summary>
    public double GapM3 => PlanM3 - ActualM3;
    public bool IsOver => GapM3 < -1;

    /// <summary>① 工时缺口：少干的小时数 × 计划班产。</summary>
    public double HourGapM3 { get; init; }
    /// <summary>② 效率缺口：实际工时 × 班产差。</summary>
    public double RateGapM3 { get; init; }

    public IReadOnlyList<GapItem> Items { get; init; } = Array.Empty<GapItem>();

    /// <summary>归不上任何原因的那部分。<b>必须显示</b>，不许摊进别的项里。</summary>
    public double UnexplainedM3 => Math.Max(0, GapM3 - Items.Sum(x => x.VolumeM3));

    /// <summary>解释率 %：归上因的占缺口多少。缺口为 0 时返回 100。</summary>
    public double ExplainedPct => GapM3 <= 1 ? 100 : Math.Round(Items.Sum(x => x.VolumeM3) / GapM3 * 100, 0);

    /// <summary>最主要的那一项（界面上"一句话结论"用）。</summary>
    public GapItem? Top => Items.OrderByDescending(x => x.VolumeM3).FirstOrDefault();
}

public static class AttainmentAnalyzer
{
    /// <summary>等待类原因码——它们造成的是"停着不干"，归到工时缺口那一侧。</summary>
    private static readonly IncompleteReason[] WaitReasons =
    {
        IncompleteReason.BlastWait, IncompleteReason.ProcessWait,
        IncompleteReason.RoadCongestion, IncompleteReason.OreShortage,
        IncompleteReason.Absence, IncompleteReason.Weather,
    };

    /// <summary>
    /// 分解一条任务的达成缺口。
    /// </summary>
    /// <param name="faultHours">本任务时段内的**非计划**故障停机 h（来自 FaultEvent 汇总）。</param>
    /// <param name="maintHours">本任务时段内的**计划检修**停机 h（档期外的那部分才会形成缺口）。</param>
    public static AttainmentBreakdown Of(ProductionTask t, double faultHours = 0, double maintHours = 0)
    {
        double plan = Math.Max(0, t.TargetVolumeM3);
        double act = Math.Max(0, t.ActualVolumeM3);

        double hPlan = t.PlannedHours > 1e-6 ? t.PlannedHours : Math.Max(0, t.EndHour - t.StartHour);
        double hAct = Math.Max(0, t.ActualHours);
        double capPlan = hPlan > 1e-6 ? plan / hPlan : 0;      // 计划班产（按计划量与计划工时反推，口径自洽）
        double capAct = hAct > 1e-6 ? act / hAct : 0;

        double hourGap = capPlan * (hPlan - hAct);
        double rateGap = hAct * (capPlan - capAct);

        var items = new List<GapItem>();
        double gap = plan - act;

        // 超额 / 没缺口：不硬凑归因
        if (gap <= 1 || capPlan <= 1e-6)
            return new AttainmentBreakdown { PlanM3 = plan, ActualM3 = act, HourGapM3 = hourGap, RateGapM3 = rateGap };

        // ── ① 工时缺口 ──────────────────────────────────────────────────────
        double hourBudget = Math.Max(0, hourGap);      // 只在真少干了小时数时才分
        double used = 0;

        void AddHour(string cause, double hours, string basis)
        {
            if (hours <= 1e-6 || hourBudget - used <= 1e-6) return;
            double v = Math.Min(hours * capPlan, hourBudget - used);
            if (v <= 1e-6) return;
            used += v;
            items.Add(new GapItem { Cause = cause, VolumeM3 = Math.Round(v), Basis = basis });
        }

        AddHour("突发停机（非计划故障）", faultHours, $"{faultHours:0.#}h × 计划班产 {capPlan:0.#} m³/h");
        AddHour("档期外检修", maintHours, $"{maintHours:0.#}h × 计划班产 {capPlan:0.#} m³/h（档期内的已在计划时窗里扣过）");

        // 剩下的少干工时，若录了等待类原因码就归给它们（平摊：原因码只说"有这回事"，没说各占多久）
        double restHours = Math.Max(0, (hourBudget - used) / capPlan);
        var waits = t.Reasons.Where(r => WaitReasons.Contains(r)).Distinct().ToList();
        if (restHours > 1e-6 && waits.Count > 0)
        {
            double each = restHours / waits.Count;
            foreach (var w in waits)
                AddHour($"等待·{w.Label()}", each,
                        $"少干 {restHours:0.#}h 由 {waits.Count} 项等待原因均摊 × 班产 {capPlan:0.#}");
        }

        // ── ② 效率缺口 ──────────────────────────────────────────────────────
        if (rateGap > 1)
        {
            // 配车不足是**设备布置**问题，且可量化：实配/荐车 就是运力对铲装能力的折扣上限
            int need = t.Group.RecommendedTrucks, got = t.Group.Trucks.Count;
            if (t.Process == ProcessType.Load && need > 0 && got < need)
            {
                double shortRatio = 1 - (double)got / need;
                double v = Math.Min(rateGap, rateGap * shortRatio);
                if (v > 1e-6)
                    items.Add(new GapItem
                    {
                        Cause = "设备布置·运力不足",
                        VolumeM3 = Math.Round(v),
                        Basis = $"配 {got} 车 < 荐 {need} 车（缺 {shortRatio * 100:0}%）· 铲等车，效率缺口按缺车比例折算",
                    });
                double rest = rateGap - v;
                if (rest > 1)
                    items.Add(new GapItem { Cause = "执行效率", VolumeM3 = Math.Round(rest), Basis = "配车已足的那部分里仍未达到计划班产" });
            }
            else
            {
                items.Add(new GapItem
                {
                    Cause = "执行效率",
                    VolumeM3 = Math.Round(rateGap),
                    Basis = $"实际班产 {capAct:0.#} < 计划 {capPlan:0.#} m³/h × 实际工时 {hAct:0.#}h",
                });
            }
        }

        return new AttainmentBreakdown
        {
            PlanM3 = plan, ActualM3 = act,
            HourGapM3 = hourGap, RateGapM3 = rateGap,
            Items = items,
        };
    }

    /// <summary>
    /// 一组任务的合并分解（按班/按面/按设备汇总时用）。
    /// 逐条分解后按原因合并 —— 不是拿汇总量再算一次：各条的计划班产不同，
    /// 先汇总再分解会把一台大铲和一台小铲的效率差算成同一个数。
    /// </summary>
    public static AttainmentBreakdown Combine(IEnumerable<AttainmentBreakdown> parts)
    {
        var list = parts.Where(p => p != null).ToList();
        var merged = list.SelectMany(p => p.Items)
            .GroupBy(x => x.Cause)
            .Select(g => new GapItem
            {
                Cause = g.Key,
                VolumeM3 = Math.Round(g.Sum(x => x.VolumeM3)),
                Basis = g.Count() == 1 ? g.First().Basis : $"{g.Count()} 条任务合计",
            })
            .OrderByDescending(x => x.VolumeM3)
            .ToList();

        return new AttainmentBreakdown
        {
            PlanM3 = list.Sum(p => p.PlanM3),
            ActualM3 = list.Sum(p => p.ActualM3),
            HourGapM3 = list.Sum(p => p.HourGapM3),
            RateGapM3 = list.Sum(p => p.RateGapM3),
            Items = merged,
        };
    }

    /// <summary>"缺 1,240 m³：突发停机 620 · 运力不足 380 · 未解释 240（解释率 81%）"。</summary>
    public static string Caption(AttainmentBreakdown b)
    {
        if (b.IsOver) return $"超额 {-b.GapM3:N0} m³实方";
        if (b.GapM3 <= 1) return "达成";

        string items = string.Join(" · ", b.Items.OrderByDescending(x => x.VolumeM3)
                                                 .Select(x => $"{x.Cause} {x.VolumeM3:N0}"));
        return $"缺 {b.GapM3:N0} m³：" + (items.Length > 0 ? items : "无可归因项")
             + (b.UnexplainedM3 > 1 ? $" · 未解释 {b.UnexplainedM3:N0}" : "")
             + $"（解释率 {b.ExplainedPct:0}%）";
    }
}
