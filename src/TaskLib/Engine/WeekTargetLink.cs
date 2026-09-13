// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/WeekTargetLink.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Platform;                // ProjectPeriodFormat
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  周 → 日 的裂解口径（WK1–WK8）。月 → 周 是人给的，本类只管"这一周剩下的量怎么落到今天"。
//
//  ── 在它之前 ──
//  日目标直接由**月量 ÷ 当月作业日**摊出来（ShortTermLink）。一个月里的每一天因此长得一模一样：
//  这周抢采区、下周大修、月底冲量 —— 月计划表达不了，平均摊更表达不了。
//  「周计划编制」窗口那七行也只是同一个除法的投影，没有任何输入口。
//
//  ── 现在的链 ──
//      月计划（已确定）
//        └─ 周目标（人下达，week_plan_target / V049：采出万t + 剥离万m³）
//             └─ 日目标（本类：剩余周目标 × 今天的能力权重 ÷ 剩余作业日的能力权重和）
//                  └─ 日任务（分解器按 面 × 工序 × 班 × 设备 切）
//
//  ── 八条口径 ──
//   WK1 周 = 自然周、周一起，键是周一的 yyyy-MM-dd（与 WeekPlanLink.MondayOf 同一口径）。
//   WK2 摊日权重**复用** DayCapacityCalendar，不另写一份：
//       w(d) = 班产 × 当天可用工时（班时窗 − 检修档期 − 爆破清场 − 交接） × 设备可用率（近期非计划故障）。
//       这就是"结合设备与现场实际状况"落到算法上的样子；再乘天气系数由调用方决定。
//   WK3 非作业日 w = 0（班次日历里没排班就是不出勤，不拿自然日顺延）。
//   WK4 **周内滚动**：已过去的作业日按**实绩**核销，剩余周目标 = 周目标 − 已完成，
//       只摊到「今天及以后」的作业日。今天不核销（今天的实绩还在长，扣了等于自己减自己的目标）。
//       ⚠ 过去的作业日**没录实绩就按 0 核销**，并把这些天点名报出来 ——
//       不许假设"没录=干完了"：那样剩余量会凭空变小，后面几天的目标一起塌下去，
//       而每一天看着都正常。
//   WK5 面级封顶仍在调用方（备采储量），本类只出总量与比例。
//   WK6 能力日历建不起来（缺班产/主设备/台账没接通）⇒ 整条退回「剩余量 ÷ 剩余作业日」等分，
//       并如实标注。**不许半套加权**：一部分面按能力摊、一部分按等分，加起来不等于周目标，
//       而没有任何地方会报错。
//   WK7 与月计划冲突以**周**为准（2026-08-20 定），差额不拦不改，由对账侧逐周累计后点名。
//   WK8 这一周没下过目标 ⇒ 返回 <see cref="WeekTargetResolution.HasTarget"/>=false，
//       调用方退回月路，并在来源文案里写明是哪一条路 —— 两条路的数不一样，看的人必须知道自己在看哪一条。
//
//  跨月的周（周一 7/29、周日 8/4）不拆：周目标就是这七天的总量，
//  拆到日之后各天自然落到各自的月份里。能力日历按天所在的月分别建，本类内部合并。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>周目标裂解到"今天"的结果。</summary>
public sealed class WeekTargetResolution
{
    /// <summary>这一周下过目标（两条腿至少一条非 0）。</summary>
    public bool HasTarget;

    public DateTime Monday;
    public DateTime Sunday => Monday.AddDays(6);

    /// <summary>周目标原值（与月计划同单位：万t / 万m³）。</summary>
    public double TargetCoalWanT;
    public double TargetStripWanM3;

    /// <summary>周目标折成实方 m³（煤按密度折方，与 ShortTermLink 同一口径）。</summary>
    public double TargetCoalM3;
    public double TargetStripM3;

    /// <summary>本周已过去的作业日已完成（实绩，实方 m³）。</summary>
    public double DoneCoalM3;
    public double DoneStripM3;

    /// <summary>剩余量 = 目标 − 已完成（下限 0）。</summary>
    public double RemainCoalM3 => Math.Max(0, TargetCoalM3 - DoneCoalM3);
    public double RemainStripM3 => Math.Max(0, TargetStripM3 - DoneStripM3);

    /// <summary>今天及以后、且排了班的日子（升序）。</summary>
    public List<DateTime> RemainWorkdays = new();

    /// <summary>本周已过去的作业日里**一条实绩都没录**的那些天（按 0 核销，必须报出来）。</summary>
    public List<DateTime> PastDaysWithoutActuals = new();

    /// <summary>班次日历读不到（≠ 本周没排班）。</summary>
    public bool CalendarUnavailable;

    /// <summary>今天在不在剩余作业日里。</summary>
    public bool TodayIsWorkday;

    /// <summary>来源/依据文案（界面直接显示）。</summary>
    public string Label = "";

    /// <summary>能用来定今天的目标：下过目标、今天排了班、剩余作业日非空。</summary>
    public bool Usable => HasTarget && TodayIsWorkday && RemainWorkdays.Count > 0;
}

/// <summary>周目标 → 当日目标。</summary>
public static class WeekTargetLink
{
    /// <summary>上一次 <see cref="Resolve"/> 走的是不是周路（false = 没有周目标，调用方该退回月路）。</summary>
    public static bool LastFromWeek { get; private set; }

    /// <summary>本周的周一（自然周，周一起）。与 <see cref="WeekPlanLink.MondayOf"/> 同一口径。</summary>
    public static DateTime MondayOf(DateTime day) => WeekPlanLink.MondayOf(day);

    /// <summary>yyyy-MM-dd（库里周键的唯一格式）。</summary>
    public static string KeyOf(DateTime monday)
        => monday.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 取 <paramref name="day"/> 所在周的目标，并扣掉已过去作业日的实绩。
    /// <para>任何一处取不到都只降级（<see cref="WeekTargetResolution.HasTarget"/>=false 或
    /// <see cref="WeekTargetResolution.CalendarUnavailable"/>=true），不抛、不造数。</para>
    /// </summary>
    /// <param name="day">作业日。</param>
    /// <param name="coalDensity">煤密度 t/m³（把万t 折成实方 m³；与 ShortTermLink 同一个数）。</param>
    public static WeekTargetResolution Resolve(DateTime day, double coalDensity)
    {
        var res = new WeekTargetResolution { Monday = MondayOf(day) };
        double density = Math.Max(0.1, coalDensity);

        // ── ① 周目标 ──
        try
        {
            var t = EquipmentDataContext.Plan.GetWeek(KeyOf(res.Monday));
            if (t != null && t.HasTarget)
            {
                res.HasTarget = true;
                res.TargetCoalWanT = t.TargetCoalWanT;
                res.TargetStripWanM3 = t.TargetStripWanM3;
                res.TargetCoalM3 = t.TargetCoalWanT * 1e4 / density;
                res.TargetStripM3 = t.TargetStripWanM3 * 1e4;
            }
        }
        catch (Exception ex)
        {
            LastFromWeek = false;
            res.Label = $"周目标：台账未接通（{Short(ex)}）";
            return res;
        }

        if (!res.HasTarget)
        {
            LastFromWeek = false;
            res.Label = $"周目标：{KeyOf(res.Monday)} 那一周没有下达过（在「周计划编制」里填）";
            return res;
        }

        // ── ② 本周作业日（WK3）──
        var workdays = new List<DateTime>();
        try
        {
            var rows = EquipmentDataContext.ShiftCalendar.InRange(res.Monday, res.Sunday)
                                           .Where(r => r != null).ToList();
            workdays = rows.Select(r => r.Date.Date).Distinct().OrderBy(d => d).ToList();
        }
        catch { res.CalendarUnavailable = true; }

        if (res.CalendarUnavailable || workdays.Count == 0)
        {
            LastFromWeek = false;
            res.Label = res.CalendarUnavailable
                ? "周目标：班次日历读不到 —— 拆不出作业日，本次不走周路"
                : $"周目标：{KeyOf(res.Monday)} 那一周班次日历里一个作业日都没有 —— 拆不出日目标"
                  + "（不拿自然日顶替：顶出来的日子看着正常，而设备被排到了本来不上班的那天）";
            return res;
        }

        // ── ③ 周内滚动：已过去的作业日按实绩核销（WK4）──
        var today = day.Date;
        foreach (var d in workdays.Where(d => d < today))
        {
            double load = 0, dump = 0;
            bool any = false;
            try
            {
                var recs = TaskPersistence.LoadActualsOfDay(ProjectPeriodFormat.DateLabel(d));
                if (recs.Count > 0)
                {
                    any = true;
                    load = recs.Where(r => r.Process == ProcessType.Load).Sum(r => r.ActualVolumeM3);
                    dump = recs.Where(r => r.Process == ProcessType.Dump).Sum(r => r.ActualVolumeM3);
                }
            }
            catch { /* 读盘失败 = 没录，按 0 核销并点名 */ }

            if (any) { res.DoneCoalM3 += load; res.DoneStripM3 += dump; }
            else res.PastDaysWithoutActuals.Add(d);
        }

        res.RemainWorkdays = workdays.Where(d => d >= today).ToList();
        res.TodayIsWorkday = res.RemainWorkdays.Count > 0 && res.RemainWorkdays[0] == today;

        LastFromWeek = res.Usable;
        res.Label = Describe(res);
        return res;
    }

    private static string Describe(WeekTargetResolution r)
    {
        string head = $"周目标「{KeyOf(r.Monday)} 起」采出 {r.TargetCoalWanT:0.##}万t / 剥离 {r.TargetStripWanM3:0.##}万m³";

        if (!r.TodayIsWorkday)
            return head + " —— **今天不在本周作业日里**，当日目标为 0（班次日历里这天没排班）";

        string done = r.DoneCoalM3 > 1e-6 || r.DoneStripM3 > 1e-6
            ? $"　已完成 {r.DoneCoalM3 / 1e4:0.##}/{r.DoneStripM3 / 1e4:0.##} 万m³"
            : "　本周尚无已核销实绩";

        string miss = r.PastDaysWithoutActuals.Count > 0
            ? $"　⚠ {r.PastDaysWithoutActuals.Count} 个已过作业日没录实绩（"
              + string.Join("、", r.PastDaysWithoutActuals.Select(d => d.ToString("MM-dd", CultureInfo.InvariantCulture)))
              + "），**按 0 核销** —— 没录不等于没干，先去「实绩录入」补上，否则剩余量被高估、后面几天的目标一起被抬高"
            : "";

        return head + done + $"　剩余作业日 {r.RemainWorkdays.Count} 天（含今天）" + miss;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? "";
        int i = m.IndexOf('\n');
        if (i > 0) m = m[..i];
        return m.Length > 60 ? m[..60] + "…" : m;
    }
}
