// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/WorkCalendar.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data.Entities;     // ShiftCalendar

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  工作历口径 —— 回答「这个月到底有几个有效作业日」。
//
//  在它出现之前，这个数只有一个来源：月计划自带的 Workdays，读不到就 `= 25`（写死）。
//  而月→日的整条裂解都在除以它：日采出 = 月采出 ÷ 作业日。除数是拍的，下面每一个日目标
//  就都是拍的，且拍错了不会有任何提示——月末才发现差了一大截。
//
//  真正知道「本月哪几天出勤」的是班次日历（shift_calendar，日 × 班）。本类把它算出来：
//    有效作业日 = 本月内**至少有一条班次记录**的不同日期数
//
//  为什么用「有没有排班」而不是「有没有产量」：作业日是**计划口径**（这天安排了人和设备上岗），
//  产量是执行口径。停产日在日历里就不该有班次记录——没记录即不是作业日，这条规则可自证。
//  日历里有记录但没填开班时刻的行照样算作业日（那天确实出勤），只是装箱排不出时窗，
//  故单独计数写进来源文案，让人知道该去补时刻。
//
//  ★ 与月计划口径不一致时谁说了算：见 ShortTermLink.ApplyToConfig —— 日历优先。
//    月目标是承诺量，作业日少了就得每天多干；仍按月计划的 25 日摊，月末必然欠产且直到
//    月末才暴露。按日历折算后当日能力不足会当场报「当日欠产」，问题当天就看得见。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>本月有效作业日的核算结果。</summary>
public sealed class MonthWorkdayInfo
{
    /// <summary>是不是日历口径（false = 台账没接通/本月没有任何班次记录，本对象仅为兜底占位）。</summary>
    public bool FromLedger;

    /// <summary>有效作业日（天）。<see cref="FromLedger"/>=false 时为 0，调用方须自行兜底。</summary>
    public int Workdays;

    /// <summary>本月班次记录条数（用于文案，不参与计算）。</summary>
    public int ShiftRows;

    /// <summary>其中"有记录但没填开班时刻"的天数——这些天算作业日，但装箱排不出时窗。</summary>
    public int DaysWithoutTime;

    /// <summary>月标签（如 2026-08）。</summary>
    public string MonthLabel = "";

    /// <summary>一句来源文案（UI 与校核提示直接用）。</summary>
    public string Label = "";
}

/// <summary>作业日这个除数最终取自哪一层。</summary>
public enum WorkdaySource
{
    /// <summary>班次日历（本月有几天排了班）。</summary>
    Calendar,
    /// <summary>月计划自带的作业日。</summary>
    MonthPlan,
    /// <summary>兜底常数——这个数是拍的。</summary>
    Fallback,
}

/// <summary>作业日除数的裁定结果（三层里命中哪一层 + 一句口径文案）。</summary>
public sealed class WorkdayResolution
{
    public double Workdays;
    public WorkdaySource Source;
    public string Basis = "";
    public MonthWorkdayInfo Info = new();

    /// <summary>月计划自带的作业日（0 = 月计划没给）。与日历口径的差额由调用方决定怎么报。</summary>
    public double PlanWorkdays;

    /// <summary>日历口径与月计划口径对不上（两者都有且差半天以上）。</summary>
    public bool Disagrees => Source == WorkdaySource.Calendar && PlanWorkdays > 0
                          && Math.Abs(PlanWorkdays - Workdays) > 0.5;
}

/// <summary>工作历口径：从班次日历算本月有效作业日。</summary>
public static class WorkCalendar
{
    /// <summary>日历口径不可用时的兜底作业日（原先散落在 ShortTermLink 里的那个写死的 25）。</summary>
    public const double FallbackMonthWorkdays = 25;

    /// <summary>
    /// 裁定本月的作业日除数（<b>月→日、月→周共用这一份口径</b>）。
    /// <para>
    /// 三层，日历优先：班次日历 → 月计划自带 → 兜底常数。理由见本文件顶部与
    /// <c>ShortTermLink.ResolveWorkdays</c>：月目标是承诺量，日历说本月只有 22 天出勤，
    /// 仍按 25 天摊就会到月末才暴露欠产。
    /// </para>
    /// <para>
    /// ★ 抽到这里是因为「周计划编制」也要按同一个除数摊——两处各写一遍三层兜底，
    /// 周计划与日计划迟早会显示成两个数，而那种不一致最难查（谁也不觉得自己错了）。
    /// </para>
    /// </summary>
    public static WorkdayResolution ResolveWorkdays(DateTime anyDayInMonth, double planWorkdays)
    {
        var info = MonthWorkdays(anyDayInMonth);
        var r = new WorkdayResolution { Info = info, PlanWorkdays = Math.Max(0, planWorkdays) };

        if (info.FromLedger && info.Workdays > 0)
        {
            r.Workdays = info.Workdays;
            r.Source = WorkdaySource.Calendar;
            r.Basis = $"日历口径 {info.Workdays} 天"
                    + (r.Disagrees ? $"，月计划按 {planWorkdays:0} 天编制" : "");
            return r;
        }

        if (planWorkdays > 0)
        {
            r.Workdays = planWorkdays;
            r.Source = WorkdaySource.MonthPlan;
            r.Basis = $"月计划口径 {planWorkdays:0} 天（{info.Label}）";
            return r;
        }

        r.Workdays = FallbackMonthWorkdays;
        r.Source = WorkdaySource.Fallback;
        r.Basis = $"兜底 {FallbackMonthWorkdays:0} 天（{info.Label}，月计划也没给作业日）";
        return r;
    }

    /// <summary>
    /// 本月（<paramref name="anyDayInMonth"/> 所在自然月）的有效作业日。
    /// 台账未接通或本月无任何班次记录时返回 <see cref="MonthWorkdayInfo.FromLedger"/>=false，
    /// **不猜一个数出来**——调用方自己决定兜底到月计划口径还是 <see cref="FallbackMonthWorkdays"/>。
    /// </summary>
    public static MonthWorkdayInfo MonthWorkdays(DateTime anyDayInMonth)
    {
        var first = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        string monthLabel = first.ToString("yyyy-MM");

        List<ShiftCalendar> rows;
        try
        {
            rows = EquipmentDataContext.ShiftCalendar.InRange(first, last).Where(r => r != null).ToList();
        }
        catch (Exception ex)
        {
            return new MonthWorkdayInfo
            {
                MonthLabel = monthLabel,
                Label = $"作业日：班次日历未接通（{Short(ex)}）",
            };
        }

        if (rows.Count == 0)
            return new MonthWorkdayInfo
            {
                MonthLabel = monthLabel,
                Label = $"作业日：{monthLabel} 无班次日历记录",
            };

        var byDate = rows.GroupBy(r => r.Date.Date).ToList();
        int noTime = byDate.Count(g => g.All(r => !HasTime(r.StartTime)));

        var info = new MonthWorkdayInfo
        {
            FromLedger = true,
            Workdays = byDate.Count,
            ShiftRows = rows.Count,
            DaysWithoutTime = noTime,
            MonthLabel = monthLabel,
        };
        info.Label = $"作业日：日历口径 {info.Workdays} 天（{monthLabel} · {info.ShiftRows} 条班次记录"
                   + (noTime > 0 ? $" · 其中 {noTime} 天未填开班时刻，装箱排不出时窗" : "")
                   + "）";
        return info;
    }

    /// <summary>本日的班次记录（班次日历窗口直接用；台账未接通/无记录返回空表）。</summary>
    public static IReadOnlyList<ShiftCalendar> Day(DateTime date, out string error)
    {
        error = "";
        try { return EquipmentDataContext.ShiftCalendar.ByDate(date).Where(r => r != null).ToList(); }
        catch (Exception ex) { error = Short(ex); return Array.Empty<ShiftCalendar>(); }
    }

    /// <summary>写回一条班次记录（新增或更新）。失败返回原因，成功返回空串。</summary>
    public static string Upsert(ShiftCalendar row)
    {
        try { EquipmentDataContext.ShiftCalendar.Upsert(row); return ""; }
        catch (Exception ex) { return Short(ex); }
    }

    /// <summary>A/B/C → 早/中/夜班（台账用字母，现场与单据用中文）。其它班制原样保留。</summary>
    public static string ShiftName(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "A" => "早班",
        "B" => "中班",
        "C" => "夜班",
        "" => "班次",
        _ => code!.Trim(),
    };

    /// <summary>早/中/夜班 → A/B/C（写回台账用）。认不出就原样写回，不猜。</summary>
    public static string ShiftCode(string? name) => (name ?? "").Trim() switch
    {
        "早班" => "A",
        "中班" => "B",
        "夜班" => "C",
        _ => (name ?? "").Trim(),
    };

    private static bool HasTime(string? s) => !string.IsNullOrWhiteSpace(s);

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
