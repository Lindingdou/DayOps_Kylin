// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ShiftCalendarBuilder.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  按月生成班次日历。
//
//  ══ 为什么要有它 ══
//  `shift_calendar` 此前**整张表一行都没有**（不是某个月空）：迁移里没有种子，
//  唯一的写入口是「班次日历」窗口的「保存日历」，而那个窗口**一次只存一天**
//  （日期取工程当前工作日期）。要排满一个月得翻 26 次日期、点 26 次保存 ——
//  于是没人排，于是逐日逐班永远切不出来。
//
//  ══ 哪些是数据，哪些是规则 ══
//  · **作业日数是数据**：逐月配置表里已经有（2026-08 是 25 天）。这里**不另编一个数** ——
//    调用方把它传进来，生成器只负责决定"哪几天休"。
//  · **班制是数据**：每日几班取自现场参数。
//  · **只有休息日的分布是规则** —— 均匀摊开 / 周日休 / 不休。
//
//  ══ 三条不许越界 ══
//  ① **不覆盖已有的行**：已经排过、改过的日子是人排的，生成器只补缺，并报出补了几天、跳过几天。
//  ② **爆破班一律留空**：哪一班放炮是逐日定的，猜出来的爆破班会让那一班凭空少 1.5 小时清场工时。
//  ③ **生成的天数与传入的作业日数对不上就说出来**，不偷偷凑 —— 对不上往往说明配置表那个数本身有问题。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>休息日怎么摊。</summary>
public enum RestRule
{
    /// <summary>不休 —— 全月每天都是作业日（连续作业矿常见）。</summary>
    Continuous = 0,
    /// <summary>周日休。</summary>
    Sunday = 1,
    /// <summary>按作业日数<b>均匀摊开</b>休息日（月底不扎堆）。</summary>
    Even = 2,
}

/// <summary>一次生成的结果。</summary>
public sealed class ShiftCalendarBuildResult
{
    /// <summary>要写进库的行（已排除掉库里已有的日期）。</summary>
    public List<ShiftCalendar> Rows = new();
    /// <summary>本次会新排的作业日。</summary>
    public List<DateTime> NewDays = new();
    /// <summary>库里已经有、这次跳过的日期。</summary>
    public List<DateTime> KeptDays = new();
    /// <summary>休息日（不排班）。</summary>
    public List<DateTime> RestDays = new();
    public List<string> Notes = new();
    public string Headline = "";
    public bool Ok => Rows.Count > 0;
}

/// <summary>按月生成班次日历。纯计算，不碰库。</summary>
public static class ShiftCalendarBuilder
{
    /// <summary>库里班次名的值域 —— <c>shift_calendar</c> 上有 CHECK 约束 <c>shift IN ('A','B','C')</c>。</summary>
    public static readonly string[] ShiftCodes = { "A", "B", "C" };

    /// <summary>
    /// 生成一个月。
    /// </summary>
    /// <param name="year">年。</param>
    /// <param name="month">月（1–12）。</param>
    /// <param name="shiftsPerDay">每日班次数（1–3）。三班倒 = 3。</param>
    /// <param name="rule">休息日怎么摊。</param>
    /// <param name="targetWorkdays">
    /// 目标作业日数（取自逐月配置表）。<b>&gt;0 时以它为准</b>，休息日按 <paramref name="rule"/> 摊到剩下的天里；
    /// ≤0 时完全由规则决定，并在结论里说明"这个数是规则算的，不是配置表给的"。
    /// </param>
    /// <param name="existingDays">库里已有班次记录的日期 —— 这些天<b>原样保留</b>，不生成、不覆盖。</param>
    public static ShiftCalendarBuildResult Build(
        int year, int month, int shiftsPerDay, RestRule rule,
        double targetWorkdays = 0, IEnumerable<DateTime>? existingDays = null)
    {
        var res = new ShiftCalendarBuildResult();

        if (year < 1 || year > 9999 || month < 1 || month > 12)
        { res.Headline = $"◆ 年月不成立（{year}-{month}）。"; return res; }

        int n = Math.Clamp(shiftsPerDay, 1, ShiftCodes.Length);
        if (n != shiftsPerDay)
            res.Notes.Add($"◆ 每日班次填的是 {shiftsPerDay}，已夹到 {n} —— "
                        + $"库里的班次名只有 {string.Join("/", ShiftCodes)} 三档（CHECK 约束），排不下第四班。");

        int daysInMonth = DateTime.DaysInMonth(year, month);
        var first = new DateTime(year, month, 1);
        var all = Enumerable.Range(0, daysInMonth).Select(i => first.AddDays(i)).ToList();

        var have = new HashSet<DateTime>(
            (existingDays ?? Array.Empty<DateTime>()).Select(d => d.Date));

        // ── 哪几天休 ────────────────────────────────────────────────
        var rest = PickRestDays(all, rule, targetWorkdays, res.Notes);
        var work = all.Where(d => !rest.Contains(d.Date)).ToList();
        res.RestDays = rest.OrderBy(d => d).ToList();

        // ── 生成（跳过库里已有的）──────────────────────────────────
        foreach (var d in work)
        {
            if (have.Contains(d.Date)) { res.KeptDays.Add(d.Date); continue; }
            res.NewDays.Add(d.Date);
            for (int i = 0; i < n; i++)
                res.Rows.Add(new ShiftCalendar
                {
                    Date = d.Date,
                    Shift = ShiftCodes[i],
                    StartTime = StartOf(i, n),
                    // ★ 爆破班留 false —— 哪一班放炮是逐日定的。
                    //   猜一个出来会让那一班凭空少 1.5 小时清场工时，而班表看着完全正常。
                    IsBlastShift = false,
                });
        }

        res.Headline = Describe(res, year, month, n, rule, targetWorkdays, work.Count);
        return res;
    }

    /// <summary>
    /// 班次起始时刻 —— <b>整天均分</b>（三班 00/08/16，两班 00/12）。
    /// <para>收班时刻不存：<c>shift_calendar</c> 只有开班一列，下游按相邻班的间隔推。</para>
    /// </summary>
    public static string StartOf(int index, int shiftsPerDay)
    {
        double h = 24.0 / Math.Max(1, shiftsPerDay) * index;
        int hh = (int)Math.Floor(h);
        int mm = (int)Math.Round((h - hh) * 60);
        if (mm >= 60) { hh++; mm -= 60; }
        return $"{hh % 24:00}:{mm:00}";
    }

    /// <summary>按规则挑休息日。</summary>
    private static HashSet<DateTime> PickRestDays(
        List<DateTime> all, RestRule rule, double targetWorkdays, List<string> notes)
    {
        var rest = new HashSet<DateTime>();
        int total = all.Count;
        int want = targetWorkdays > 0 ? (int)Math.Round(targetWorkdays) : -1;

        if (want > total)
        {
            notes.Add($"◆ 配置表给的作业日 {want} 天**多于本月天数** {total} 天 —— 已按全月作业排，"
                    + "并请核对逐月配置表那个数。");
            return rest;
        }

        switch (rule)
        {
            case RestRule.Continuous:
                if (want > 0 && want < total)
                    notes.Add($"· 规则是【不休】⇒ 作业日 {total} 天，而配置表给的是 {want} 天。"
                            + "**以规则为准生成**，两个数不一致这件事你得知道 —— "
                            + "月计划的日均量是按配置表那个数除出来的。");
                break;

            case RestRule.Sunday:
                foreach (var d in all) if (d.DayOfWeek == DayOfWeek.Sunday) rest.Add(d.Date);
                if (want > 0 && want != total - rest.Count)
                    notes.Add($"· 规则是【周日休】⇒ 作业日 {total - rest.Count} 天，"
                            + $"而配置表给的是 {want} 天。**以规则为准生成**，请核对哪个是对的。");
                break;

            case RestRule.Even:
                if (want <= 0)
                {
                    notes.Add("◆ 规则是【按作业日数均匀摊休】，但没给作业日数 —— 已按全月作业排。"
                            + "作业日数在「短期生产计划编制 → 逐月配置表」里。");
                    break;
                }
                int nRest = total - want;
                if (nRest <= 0) break;
                // 均匀摊：把 nRest 个休息日铺在 total 天里，避免月底扎堆
                for (int k = 0; k < nRest; k++)
                {
                    int idx = (int)Math.Round((k + 0.5) * total / nRest) - 1;
                    idx = Math.Clamp(idx, 0, total - 1);
                    // 撞上已选的就往后顺延一天
                    while (idx < total && rest.Contains(all[idx].Date)) idx++;
                    if (idx < total) rest.Add(all[idx].Date);
                }
                notes.Add($"· 作业日 {want} 天取自**逐月配置表**（不是这里编的）；"
                        + $"{rest.Count} 个休息日按均匀摊开，避免月底扎堆。"
                        + "要按矿上真实的休息日排，改用【周日休】或事后逐日改。");
                break;
        }
        return rest;
    }

    private static string Describe(ShiftCalendarBuildResult r, int year, int month,
                                   int shifts, RestRule rule, double target, int workDays)
    {
        string ruleZh = rule switch
        {
            RestRule.Continuous => "不休（连续作业）",
            RestRule.Sunday => "周日休",
            _ => "按作业日数均匀摊休",
        };
        string s = $"{year}-{month:00}：作业日 {workDays} 天 × {shifts} 班"
                 + $"（{string.Join("/", ShiftCodes.Take(shifts))}，整天均分）· {ruleZh}";
        if (r.NewDays.Count > 0) s += $"　→ 本次新排 {r.NewDays.Count} 天 / {r.Rows.Count} 条";
        if (r.KeptDays.Count > 0)
            s += $"　· {r.KeptDays.Count} 天**已排过，原样保留**（生成器不覆盖人排的班）";
        if (r.NewDays.Count == 0)
            s += "　◆ 一天都不用补 —— 这个月已经排满了。";
        if (r.RestDays.Count > 0) s += $"　· 休息 {r.RestDays.Count} 天";
        s += "　◆ 爆破班一律留空：哪一班放炮是逐日定的，猜出来会让那一班凭空少 1.5 小时清场工时。";
        return s;
    }
}
