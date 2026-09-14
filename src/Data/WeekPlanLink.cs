using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  月 → 周裂解的口径（W1–W8），移植原 TaskLib.Engine.WeekPlanLink 的**纯函数部分**。
//
//  §一九〇 起「周计划编制」一直算在未移植的排产域里。复审(§三三五)发现：这条链的口径
//  是一支干净的纯函数 —— 原文件自己就把"取数(Build)"与"算(Compose)"分开了，Compose
//  不碰库、判据可直接构造输入。而它依赖的两样东西现在都有了：
//    · W2 有效班 = 该日 shift_calendar 的班次记录数  → §三三二 接通；
//    · W3 日计划 = 月计划量 ÷ 本月作业日，**除数与装箱同一份口径** → §三三二 的
//      WorkCalendar.ResolveWorkdays（这正是它当初被判受阻的那个依赖）。
//  故本轮移 Compose 这一支；取数壳(Build)按 Kylin 自己的库另接（下一轮）。
//
//  **未移植的一处（登记）**：W5「当日那一行取当日盘子的实数」中的"盘子"是装箱引擎的产物，
//  Kylin 尚无该数据源。输入里那两个字段留着（`AnchorPlanLoadM3`/`AnchorPlanDumpM3`），
//  传 null 即自动退回"月计划日均" —— 算法一行不改，等装箱移过来直接能用。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>某个月的裂解基准：日采出 / 日剥离 = 月量 ÷ 作业日。</summary>
public sealed class MonthBasis
{
    /// <summary>yyyy-MM。</summary>
    public string MonthKey = "";

    /// <summary>有没有月计划（既没确定方案、台账也没有这个月 ⇒ false，日目标判不了）。</summary>
    public bool HasPlan;

    /// <summary>月计划名 + 月标签（"短期计划A · 8月"）。</summary>
    public string PlanLabel = "";

    /// <summary>作业日除数（与装箱同一份口径）。</summary>
    public double Workdays;

    /// <summary>除数取自哪一层的文案。</summary>
    public string WorkdayBasis = "";

    /// <summary>本月日历里排了班的日子数；0 = 本月日历一条记录都没有。</summary>
    public int CalendarDays;

    /// <summary>日采出 m³ 实方（煤按密度折方）。</summary>
    public double DayLoadM3;

    /// <summary>日剥离 m³ 实方。</summary>
    public double DayDumpM3;

    public bool Usable => HasPlan && Workdays > 0;
}

/// <summary>周计划一行 = 一天。</summary>
public sealed class WeekDayRow
{
    public DateTime Date;

    /// <summary>"周一 08-10"。</summary>
    public string DayLabel = "";

    public bool IsAnchor;      // 当前作业日那一行
    public bool IsToday;

    /// <summary>该日班次数；null = 班次日历读不通（不是 0 班）。</summary>
    public int? Shifts;

    /// <summary>排了班（有班次记录）。</summary>
    public bool Scheduled => Shifts is > 0;

    /// <summary>计划采装 m³ 实方；null = 判不了（无月计划或无日历）。</summary>
    public double? PlanLoadM3;

    /// <summary>计划排土 m³ 实方；null = 判不了。</summary>
    public double? PlanDumpM3;

    /// <summary>实绩采装 m³；null = 本日没有实绩录入（不是 0）。</summary>
    public double? ActualLoadM3;

    /// <summary>实绩排土 m³；null = 没录。</summary>
    public double? ActualDumpM3;

    /// <summary>计划量的出处（当日盘子 / 月计划日均 / 未排班 / 判不了）。</summary>
    public string Basis = "";

    public string Status = "";

    /// <summary>达成度 %；计划与实绩缺一即 null（W7）。</summary>
    public double? AttainmentPct => PlanLoadM3 is > 1e-6 && ActualLoadM3.HasValue
        ? ActualLoadM3.Value / PlanLoadM3.Value * 100
        : null;
}

/// <summary>Compose 的输入（判据直接构造这一份，不碰库）。</summary>
public sealed class WeekPlanInputs
{
    /// <summary>基准日（当前作业日）。本周由它定。</summary>
    public DateTime Anchor;

    /// <summary>"今天"（状态判定用；判据注入固定值，避免跨零点变红）。</summary>
    public DateTime Today;

    /// <summary>班次日历接不接得通。false ⇒ 全周班次显示 "—"，计划量判不了。</summary>
    public bool CalendarUsable = true;

    /// <summary>本周逐日班次数（只放有记录的日；没有的键 = 那天没排班）。</summary>
    public Dictionary<DateTime, int> ShiftsByDay = new();

    /// <summary>按 yyyy-MM 的裂解基准（跨月的周会有两份）。</summary>
    public Dictionary<string, MonthBasis> Months = new(StringComparer.Ordinal);

    /// <summary>当日盘子的采装/排土总目标（null = 没取到，那一行退回月计划日均）。</summary>
    public double? AnchorPlanLoadM3;
    public double? AnchorPlanDumpM3;

    /// <summary>当日盘子来源的一句话（写进那一行的"出处"）。</summary>
    public string AnchorBasis = "当日盘子";

    /// <summary>逐日实绩（采装, 排土）；没有键 = 那天没录。</summary>
    public Dictionary<DateTime, (double Load, double Dump)> ActualsByDay = new();
}

/// <summary>一周的裂解结果。</summary>
public sealed class WeekPlanResult
{
    public DateTime Monday;
    public DateTime Sunday => Monday.AddDays(6);
    public List<WeekDayRow> Days = new();

    /// <summary>抬头：本周区间 + 月计划 + 作业日口径。</summary>
    public string Header = "";

    /// <summary>逐条口径提示（跨月、无月计划、日历缺失…）。</summary>
    public List<string> Notes = new();

    /// <summary>本周计划合计（只累判得出的那几天）。</summary>
    public double PlanLoadSumM3 => Days.Sum(d => d.PlanLoadM3 ?? 0);
    public double PlanDumpSumM3 => Days.Sum(d => d.PlanDumpM3 ?? 0);
    public double ActualLoadSumM3 => Days.Sum(d => d.ActualLoadM3 ?? 0);

    /// <summary>本周有几天判不出计划量（合计因此偏小，必须说出来）。</summary>
    public int UnknownDays => Days.Count(d => d.PlanLoadM3 == null);

    /// <summary>本周有几天排了班。</summary>
    public int ScheduledDays => Days.Count(d => d.Scheduled);

    /// <summary>本周计划采装占本月的比例 %；跨月或无基准时为 null。</summary>
    public double? MonthSharePct;
}

/// <summary>月→周裂解。<see cref="Compose"/> 是纯函数；取数壳(原 Build)按 Kylin 自己的库另接。</summary>
public static class WeekPlanLink
{
    /// <summary>本周的周一（W1：自然周，周一起）。</summary>
    public static DateTime MondayOf(DateTime day)
        => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static string MonthKeyOf(DateTime day) => day.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // ═════════════════════════════════════════════════════════════════════════
    //  纯函数
    // ═════════════════════════════════════════════════════════════════════════

    public static WeekPlanResult Compose(WeekPlanInputs inp)
    {
        var res = new WeekPlanResult { Monday = MondayOf(inp.Anchor) };

        for (int i = 0; i < 7; i++)
        {
            var d = res.Monday.AddDays(i);
            var row = new WeekDayRow
            {
                Date = d,
                DayLabel = $"{WeekCn(d)} {d:MM-dd}",
                IsAnchor = d == inp.Anchor.Date,
                IsToday = d == inp.Today.Date,
                Shifts = inp.CalendarUsable
                    ? (inp.ShiftsByDay.TryGetValue(d, out int n) ? n : 0)
                    : null,
            };

            inp.Months.TryGetValue(MonthKeyOf(d), out var basis);
            FillPlan(row, basis, inp);

            if (inp.ActualsByDay.TryGetValue(d, out var act))
            {
                row.ActualLoadM3 = act.Load;
                row.ActualDumpM3 = act.Dump;
            }

            row.Status = StatusOf(row, inp.Today.Date);
            res.Days.Add(row);
        }

        FillHeader(res, inp);
        return res;
    }

    /// <summary>W3–W5：这一天的计划量是多少、这个数哪来的。</summary>
    private static void FillPlan(WeekDayRow row, MonthBasis? basis, WeekPlanInputs inp)
    {
        // W5 当日行优先取盘子实数 —— 盘子里的日目标已经过单元路/份额路、
        // 作业组织重分配、配煤重分配，跟甘特上画的是同一份；这里再按月均算一遍就会对不上。
        if (row.IsAnchor && inp.AnchorPlanLoadM3.HasValue)
        {
            row.PlanLoadM3 = inp.AnchorPlanLoadM3;
            row.PlanDumpM3 = inp.AnchorPlanDumpM3;
            row.Basis = inp.AnchorBasis;
            return;
        }

        if (row.Shifts == null) { row.Basis = "判不了（班次日历读不通）"; return; }

        // W4 未排班 = 0，但只有在"本月日历确实有记录"时才敢这么说
        if (row.Shifts == 0)
        {
            if (basis is { CalendarDays: > 0 })
            {
                row.PlanLoadM3 = 0;
                row.PlanDumpM3 = 0;
                row.Basis = "未排班（不计作业日）";
            }
            else
            {
                row.Basis = "判不了（本月日历无记录）";
            }
            return;
        }

        if (basis is not { Usable: true }) { row.Basis = "判不了（无月计划）"; return; }

        row.PlanLoadM3 = basis.DayLoadM3;
        row.PlanDumpM3 = basis.DayDumpM3;
        row.Basis = $"月计划日均（÷{basis.Workdays:0} 作业日）";
    }

    /// <summary>W8：状态只从"有没有排班 / 有没有实绩 / 日期在哪一边"推出来。</summary>
    private static string StatusOf(WeekDayRow row, DateTime today)
    {
        if (row.Shifts == 0) return "未排班";

        if (row.ActualLoadM3 is > 1e-6)
        {
            if (row.Date > today) return "已录实绩(日期未到)";
            double? att = row.AttainmentPct;
            if (row.Date == today) return "执行中";
            return att == null ? "已录实绩" : att >= 98 ? "已完成" : "部分完成";
        }

        if (row.Date < today) return "无实绩录入";
        if (row.Date == today) return "执行中";
        return "计划";
    }

    private static void FillHeader(WeekPlanResult res, WeekPlanInputs inp)
    {
        var months = res.Days
            .Select(d => MonthKeyOf(d.Date))
            .Distinct(StringComparer.Ordinal)
            .Select(k => inp.Months.TryGetValue(k, out var b) ? b : new MonthBasis { MonthKey = k })
            .ToList();

        res.Header = $"{res.Monday:yyyy-MM-dd} — {res.Sunday:MM-dd}　"
                   + string.Join("　|　", months.Select(m => m.Usable
                        ? $"{m.MonthKey} {m.PlanLabel} · 日采出 {m.DayLoadM3 / 1e4:0.00}万m³ / 日剥离 {m.DayDumpM3 / 1e4:0.00}万m³（{m.WorkdayBasis}）"
                        : $"{m.MonthKey} 无月计划（日目标判不了）"));

        if (months.Count > 1)
            res.Notes.Add("本周跨月：逐日按各自所属月份的月计划与作业日摊，不混成一个数。");

        if (!inp.CalendarUsable)
            res.Notes.Add("班次日历读不通 —— 有效班与逐日计划全部判不了（不拿「每天三班」顶）。");
        else if (months.Any(m => m.CalendarDays == 0))
            res.Notes.Add("本月班次日历没有记录 —— 没排班的日子按「判不了」显示；在「班次日历」补齐本月排班后即可按日摊。");

        if (res.UnknownDays > 0)
            res.Notes.Add($"本周合计只累了判得出的那几天，{res.UnknownDays} 天未计入。");

        // 本周占月：只有全周同一个月、且该月基准可用时才给这个数
        if (months.Count == 1 && months[0].Usable && months[0].Workdays > 0)
            res.MonthSharePct = res.PlanLoadSumM3 / (months[0].DayLoadM3 * months[0].Workdays) * 100;
    }

    private static string WeekCn(DateTime d) => "周" + d.DayOfWeek switch
    {
        DayOfWeek.Monday => "一", DayOfWeek.Tuesday => "二", DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四", DayOfWeek.Friday => "五", DayOfWeek.Saturday => "六", _ => "日"
    };
}
