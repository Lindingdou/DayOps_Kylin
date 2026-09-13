// 忠实移植自原 PitMine3D Modules/TaskLib/Adjust/DayRollover.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Adjust;

// ─────────────────────────────────────────────────────────────────────────────
//  跨天滚动顺延 —— 「今天欠的，后面哪几天补得回来」。
//
//  ── 与 TaskRescheduler 的分工（两层，不重叠）──
//    · TaskRescheduler：**日内**。fromHour 之后的班次里重排剩余量，改编组、补车、换面。
//      它的时间轴是 0–24h，跨不过零点。
//    · 本类：**日间**。日内消化不掉的那部分，按后续作业日的**余量能力**逐日装下去。
//  先日内后日间，顺序不能反：日内能补车解决的，不该先摊到明天去。
//
//  ── 「后面那天还塞得下多少」这个数不许拍 ──
//  唯一诚实的来源是编组自己的能力：
//      余量能力 = (班窗长度 − 已排工时) × 编组班产
//  编组班产由 FleetMatcher 按循环时间 T_c 解出（min(铲装能力, 车队运力)），是真的；
//  班窗长度来自班次配置。两者任一解不出，这条线当天就是**判不了**，
//  既不能当"有无限余量"（会给出执行不了的计划），也不能当"零余量"
//  （会把本来补得回来的量误报成消化不掉）。判不了的量单列，让人自己决定。
//
//  ── 顺延量单列，不并进原计划 ──
//  追加量写在 DayStage.RolledInM3 上，不改 TargetVolumeM3。这样「原计划多少 / 顺延进来多少」
//  在图上和台账里始终分得开 —— 并进去之后就再也说不清某天的量里哪部分是补的了。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>顺延的装填策略。<b>两种都不改「装不装得下」，只改「摊在哪几天」。</b></summary>
public enum RolloverStrategy
{
    /// <summary>
    /// 最早可用日优先填满。欠量尽早补回，后面不滚雪球；
    /// 代价是紧接着的那几天被顶到产能上限，一旦再出岔子就没有缓冲。
    /// </summary>
    EarliestFirst,

    /// <summary>
    /// 按各作业日的<b>余量能力占比</b>均衡摊平。谁余量多谁多担，没有哪天被顶满；
    /// 代价是欠量一直拖到期末才补完，中途再欠就会叠加。
    /// </summary>
    Even,
}

/// <summary>一笔顺延：把某天某环节的欠量追加到后面某个作业日。</summary>
public sealed class RolloverAssignment
{
    /// <summary>欠量发生在哪天。</summary>
    public DateTime FromDate { get; set; }
    /// <summary>补到哪天。</summary>
    public DateTime ToDate { get; set; }
    public string Region { get; set; } = "";
    public ProcessType Process { get; set; }
    /// <summary>本笔追加量 m³（采装实方 / 排土占容，随极性走）。</summary>
    public double AddedM3 { get; set; }
    /// <summary>追加前该日该线的余量能力 m³。</summary>
    public double SpareBeforeM3 { get; set; }

    public string Caption =>
        $"{FromDate:MM-dd} 欠 → {ToDate:MM-dd} 补 {AddedM3:N0} m³"
        + $"（{Region} · {Process.Label()}，该日余量 {SpareBeforeM3:N0} m³）";
}

/// <summary>某条环节线消化不掉的余额。</summary>
public sealed class RolloverRemainder
{
    public string Region { get; set; } = "";
    public ProcessType Process { get; set; }
    public double ShortfallM3 { get; set; }
    public double AbsorbedM3 { get; set; }
    public double LeftM3 => Math.Max(0, ShortfallM3 - AbsorbedM3);
    /// <summary>本线的产能全程解不出 ⇒ 上面的 Left 是「判不了」，不是「塞不下」。</summary>
    public bool CapacityUnknown { get; set; }
    /// <summary>可用于消化的作业日数。</summary>
    public int WorkdaysAvailable { get; set; }
}

public sealed class RolloverResult
{
    public bool Ok { get; set; }
    /// <summary>本次用的装填策略。</summary>
    public RolloverStrategy Strategy { get; set; } = RolloverStrategy.EarliestFirst;
    public DateTime FromDate { get; set; }
    /// <summary>总欠量 m³。</summary>
    public double ShortfallM3 { get; set; }
    /// <summary>已排进后续作业日的量 m³。</summary>
    public double AbsorbedM3 { get; set; }
    /// <summary>确实塞不下的量 m³（产能已解出，但后续作业日装不完）。</summary>
    public double OverflowM3 { get; set; }
    /// <summary>产能解不出、判不了的量 m³。<b>与 <see cref="OverflowM3"/> 不可合并统计。</b></summary>
    public double UnknownM3 { get; set; }

    public List<RolloverAssignment> Assignments { get; set; } = new();
    public List<RolloverRemainder> Remainders { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public int DaysUsed => Assignments.Select(a => a.ToDate.Date).Distinct().Count();

    public string Summary
    {
        get
        {
            if (ShortfallM3 <= 1e-6) return "本日无欠量，无需跨天顺延。";
            var s = $"欠量 {ShortfallM3:N0} m³：已顺延 {AbsorbedM3:N0} m³ 到之后 {DaysUsed} 个作业日";
            if (OverflowM3 > 1e-6) s += $"；**{OverflowM3:N0} m³ 本期内装不下**";
            if (UnknownM3 > 1e-6) s += $"；{UnknownM3:N0} m³ 因产能未解出**判不了**";
            return s + "。";
        }
    }
}

public static class DayRollover
{
    /// <summary>
    /// 规划跨天顺延。<paramref name="fromDayIndex"/> 是欠量发生的那天（通常是作业日）；
    /// 只从它**之后**的作业日里找空间。不改动时间轴 —— 要落到图上再调 <see cref="Apply"/>。
    /// </summary>
    public static RolloverResult Plan(DayStageTimeline tl, int fromDayIndex,
                                      RolloverStrategy strategy = RolloverStrategy.EarliestFirst)
    {
        var res = new RolloverResult { Strategy = strategy };
        try { return PlanCore(tl, fromDayIndex, res); }
        catch (Exception ex)
        {
            res.Notes.Add($"顺延规划失败（{ex.GetType().Name}），未做任何改动。");
            return res;
        }
    }

    private static RolloverResult PlanCore(DayStageTimeline tl, int fromDayIndex, RolloverResult res)
    {
        if (tl.Days.Count == 0 || fromDayIndex < 0 || fromDayIndex >= tl.Days.Count)
        {
            res.Notes.Add("时间轴为空或起始日越界，无可顺延的量。");
            return res;
        }

        var fromDay = tl.Days[fromDayIndex];
        res.FromDate = fromDay.Date;

        // ── ① 收欠量。只认**量型工序**且**实绩确实录过**的环节 ──
        //  没录实绩的环节谈不上"欠"：计划 18500、实绩空着，那是还没干，不是没干成。
        var shortfalls = fromDay.Stages
            .Where(s => s.Process is ProcessType.Load or ProcessType.Dump)
            .Where(s => s.ActualVolumeM3 > 1e-6 && s.ShortfallM3 > 1e-6)
            .GroupBy(s => (s.RegionName, s.Process))
            .Select(g => (g.Key.RegionName, g.Key.Process, M3: g.Sum(s => s.ShortfallM3)))
            .ToList();

        if (shortfalls.Count == 0)
        {
            res.Ok = true;
            res.Notes.Add($"{fromDay.Date:MM-dd} 没有「已录实绩且未达标」的环节 ⇒ 无欠量可顺延。"
                        + "（计划有量但实绩空着的环节不算欠产 —— 那是还没干，不是没干成。）");
            return res;
        }

        // ── ② 后续作业日 ──
        var laterDays = new List<DayPlan>();
        for (int i = fromDayIndex + 1; i < tl.Days.Count; i++)
            if (tl.Days[i].IsWorkday) laterDays.Add(tl.Days[i]);

        if (laterDays.Count == 0)
        {
            res.ShortfallM3 = shortfalls.Sum(x => x.M3);
            res.OverflowM3 = res.ShortfallM3;
            foreach (var (region, proc, m3) in shortfalls)
                res.Remainders.Add(new RolloverRemainder
                { Region = region, Process = proc, ShortfallM3 = m3, WorkdaysAvailable = 0 });
            res.Notes.Add($"{fromDay.Date:MM-dd} 之后**没有作业日**（本区间到头了 / 剩下全是检修停产日）"
                        + " ⇒ 欠量在本区间内无处可放。把区间拉长，或走外委/调整月计划。");
            return res;
        }

        // ── ③ 逐线装 ──
        foreach (var (region, proc, shortfall) in shortfalls)
        {
            res.ShortfallM3 += shortfall;

            // 逐日余量（先全量算出来：均衡摊平要先知道总盘子）
            var slots = new List<(DayPlan Day, double Spare)>();
            bool anyCapacityResolved = false;
            foreach (var day in laterDays)
            {
                var lanes = day.Stages
                    .Where(s => s.Process == proc
                             && string.Equals(s.RegionName, region, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (lanes.Count == 0) continue;          // 那天这条线不作业
                if (lanes.Any(s => s.CapacityResolved)) anyCapacityResolved = true;

                // 一天内多条（多班）：能力相加，已顺延进去的要扣掉
                double spare = lanes.Sum(s => s.CapacityResolved
                    ? Math.Max(0, s.SpareCapacityM3 - s.RolledInM3)
                    : 0);
                if (spare > 1e-6) slots.Add((day, spare));
            }

            double left = shortfall;
            double absorbed = 0;
            double totalSpare = slots.Sum(s => s.Spare);

            // 均衡摊平只在**装得下**时才有意义：装不下的话每天都得顶满，两种策略本来就一样。
            bool even = res.Strategy == RolloverStrategy.Even && totalSpare > shortfall + 1e-6;

            foreach (var (day, spare) in slots)
            {
                if (left <= 1e-6) break;

                double take = even
                    ? Math.Min(left, shortfall * (spare / totalSpare))   // 按余量占比分摊
                    : Math.Min(left, spare);                            // 最早可用日填满
                if (take <= 1e-6) continue;

                res.Assignments.Add(new RolloverAssignment
                {
                    FromDate = fromDay.Date,
                    ToDate = day.Date,
                    Region = region,
                    Process = proc,
                    AddedM3 = take,
                    SpareBeforeM3 = spare,
                });
                left -= take;
                absorbed += take;
            }

            // 分摊的浮点残差（各天按比例取完仍差一点点）补给最后一个有余量的日子，
            // 否则守恒判据会因为几个 1e-9 报红，而那不是真的"装不下"。
            if (even && left > 1e-9 && left < 1.0 && res.Assignments.Count > 0)
            {
                var last = res.Assignments[^1];
                last.AddedM3 += left;
                absorbed += left;
                left = 0;
            }

            res.AbsorbedM3 += absorbed;
            int avail = laterDays.Count(d => d.Stages.Any(s => s.Process == proc
                && string.Equals(s.RegionName, region, StringComparison.OrdinalIgnoreCase)));

            var rem = new RolloverRemainder
            {
                Region = region, Process = proc,
                ShortfallM3 = shortfall, AbsorbedM3 = absorbed,
                WorkdaysAvailable = avail,
                CapacityUnknown = !anyCapacityResolved,
            };
            res.Remainders.Add(rem);

            // 塞不下 vs 判不了：两码事，绝不合并
            if (rem.LeftM3 > 1e-6)
            {
                if (rem.CapacityUnknown) res.UnknownM3 += rem.LeftM3;
                else res.OverflowM3 += rem.LeftM3;
            }
        }

        res.Ok = true;
        BuildNotes(res, laterDays.Count);
        return res;
    }

    private static void BuildNotes(RolloverResult res, int laterWorkdays)
    {
        res.Notes.Add($"消化空间取自各作业日的**余量能力** =（班窗长度 − 已排工时）× 编组班产。"
                    + $"编组班产由 FleetMatcher 按循环时间 T_c 解出，不是拍的系数；"
                    + $"本次可用后续作业日 {laterWorkdays} 天。");

        res.Notes.Add(res.Strategy == RolloverStrategy.Even
            ? "装填策略：**均衡摊平** —— 按各作业日的余量能力占比分摊，没有哪天被顶到上限，"
              + "留着缓冲；代价是欠量拖到期末才补完，中途再欠会叠加。"
              + "（后续作业日总余量不够时自动等同于「最早优先」—— 装不下时每天本来就得顶满。）"
            : "装填策略：**最早可用日优先填满** —— 欠量尽早补回、后面不滚雪球；"
              + "代价是紧接着那几天被顶到产能上限，再出岔子就没有缓冲。");

        var unknown = res.Remainders.Where(r => r.CapacityUnknown && r.LeftM3 > 1e-6).ToList();
        if (unknown.Count > 0)
            res.Notes.Add("⚠ **判不了**（编组班产或班窗未解出，不是「塞不下」）："
                        + string.Join("、", unknown.Select(r => $"{r.Region}·{r.Process.Label()} {r.LeftM3:N0} m³"))
                        + " —— 这些线没有可信的产能上限，既不能当有余量也不能当没余量。"
                        + "先去「生产任务编制」把编组解出来，再重跑顺延。");

        var over = res.Remainders.Where(r => !r.CapacityUnknown && r.LeftM3 > 1e-6).ToList();
        if (over.Count > 0)
            res.Notes.Add("⚠ **本期内装不下**："
                        + string.Join("、", over.Select(r => $"{r.Region}·{r.Process.Label()} 还差 {r.LeftM3:N0} m³（该线有 {r.WorkdaysAvailable} 个作业日可用）"))
                        + " —— 后续作业日按编组能力已经排满。可选：延长区间 / 加编组 / 外委 / 报调整月计划。");

        var spread = res.Assignments.GroupBy(a => (a.Region, a.Process)).ToList();
        foreach (var g in spread)
        {
            var days = g.Select(a => a.ToDate).OrderBy(d => d).ToList();
            if (days.Count == 0) continue;
            res.Notes.Add($"{g.Key.Region}·{g.Key.Process.Label()}：顺延 {g.Sum(a => a.AddedM3):N0} m³ "
                        + $"分摊到 {days.Count} 天（{days.First():MM-dd} ~ {days.Last():MM-dd}），"
                        + "按「最早可用日优先填满」装 —— 欠量越早补回，后面越不会滚雪球。");
        }
    }

    /// <summary>
    /// 把顺延结果落到时间轴上（写 <see cref="DayStage.RolledInM3"/>，<b>不改</b> TargetVolumeM3）。
    /// 幂等前提是调用方先 <see cref="Clear"/> 或重建时间轴 —— 本方法只做累加。
    /// </summary>
    public static void Apply(DayStageTimeline tl, RolloverResult res)
    {
        foreach (var a in res.Assignments)
        {
            var day = tl.Days.FirstOrDefault(d => d.Date.Date == a.ToDate.Date);
            if (day == null) continue;

            var lanes = day.Stages
                .Where(s => s.Process == a.Process
                         && string.Equals(s.RegionName, a.Region, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (lanes.Count == 0) continue;

            // 多班时按各自余量能力占比分摊，摊不动就全给第一条
            double totalSpare = lanes.Sum(s => Math.Max(0, s.SpareCapacityM3 - s.RolledInM3));
            if (totalSpare <= 1e-6) { lanes[0].RolledInM3 += a.AddedM3; continue; }
            foreach (var s in lanes)
            {
                double share = Math.Max(0, s.SpareCapacityM3 - s.RolledInM3) / totalSpare;
                s.RolledInM3 += a.AddedM3 * share;
            }
        }
    }

    /// <summary>抹掉时间轴上所有顺延量（重算前调）。</summary>
    public static void Clear(DayStageTimeline tl)
    {
        foreach (var s in tl.AllStages) s.RolledInM3 = 0;
    }
}
