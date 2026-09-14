using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Data;

/// <summary>班次日历的一条记录（对应表 <c>shift_calendar</c>，字段同原 <c>ShiftCalendar</c> 实体）。</summary>
public sealed class ShiftCalendarRow
{
    public DateTime Date { get; set; }
    /// <summary>班次 A/B/C。</summary>
    public string Shift { get; set; } = "";
    /// <summary>开班时间 HH:mm；空 = 没填（装箱排不出时窗）。</summary>
    public string? StartTime { get; set; }
    public string? LeaderName { get; set; }
    public bool IsBlastShift { get; set; }
    public string? Weather { get; set; }
    public string? Notes { get; set; }
}

/// <summary>本月作业日的裁定来源。</summary>
public enum WorkdaySource { Calendar = 0, MonthPlan = 1, Fallback = 2 }

/// <summary>本月作业日信息（<c>FromLedger=false</c> 表示日历没数据，**不猜一个数出来**）。</summary>
public sealed class MonthWorkdayInfo
{
    public bool FromLedger;
    public int Workdays;
    public int ShiftRows;
    public int DaysWithoutTime;
    public string MonthLabel = "";
    public string Label = "";
}

/// <summary>作业日裁定结果。</summary>
public sealed class WorkdayResolution
{
    public MonthWorkdayInfo Info = new();
    public double PlanWorkdays;
    public double Workdays;
    public WorkdaySource Source;
    public string Basis = "";
    /// <summary>日历口径与月计划口径对不上（差 0.5 天以上就算）。</summary>
    public bool Disagrees => Info.FromLedger && PlanWorkdays > 0 && Math.Abs(Info.Workdays - PlanWorkdays) >= 0.5;
}

/// <summary>
/// 班次日历与作业日口径（移植原 <c>TaskLib.Engine.WorkCalendar</c>）。
///
/// **为什么这层要单独存在**（原注释的理由，照搬）：月目标是承诺量，日历说本月只有 22 天出勤、
/// 仍按 25 天摊，就会到月末才暴露欠产。而"月→日"与"月→周"两处若各写一遍三层兜底，
/// 周计划与日计划迟早会显示成两个数 —— **那种不一致最难查，因为谁都不觉得自己错了**。
/// 所以除数口径只此一份。
///
/// 三层，日历优先：**班次日历 → 月计划自带 → 兜底常数 25**。
///
/// Kylin 侧的实现差异：原版走 `EquipmentDataContext.ShiftCalendar` 仓储，这里直接对
/// <c>shift_calendar</c> 表发 SQL（表在 V001 就建好了，一直没人读写）。日期一律按
/// <c>yyyy-MM-dd</c> 文本存取 —— 该列本来就是 TEXT，字典序即时间序，区间查直接用字符串比较
/// （同 <c>week_plan_target</c> 的手法）。
/// </summary>
public static class WorkCalendar
{
    /// <summary>日历口径不可用时的兜底作业日（同原版：那个曾经写死在别处的 25）。</summary>
    public const double FallbackMonthWorkdays = 25;

    private static string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ShiftCalendarRow Read(DbDataReader rd) => new()
    {
        Date = DateTime.TryParse(rd.IsDBNull(0) ? "" : rd.GetValue(0)?.ToString() ?? "", CultureInfo.InvariantCulture,
                                 DateTimeStyles.None, out var dt) ? dt.Date : default,
        Shift = rd.IsDBNull(1) ? "" : rd.GetValue(1)?.ToString() ?? "",
        StartTime = rd.IsDBNull(2) ? null : rd.GetValue(2)?.ToString(),
        LeaderName = rd.IsDBNull(3) ? null : rd.GetValue(3)?.ToString(),
        IsBlastShift = !rd.IsDBNull(4) && Convert.ToInt64(rd.GetValue(4)) != 0,
        Weather = rd.IsDBNull(5) ? null : rd.GetValue(5)?.ToString(),
        Notes = rd.IsDBNull(6) ? null : rd.GetValue(6)?.ToString(),
    };

    private const string SelectCols =
        "SELECT date, shift, start_time, leader_name, is_blast_shift, weather, notes FROM shift_calendar";

    /// <summary>本日的班次记录（班次日历窗口直接用；查不到/表不通返回空表并给原因）。</summary>
    public static List<ShiftCalendarRow> Day(DbConnection conn, DateTime date, out string error)
    {
        error = "";
        var list = new List<ShiftCalendarRow>();
        if (conn == null) { error = "没有数据库连接"; return list; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectCols + " WHERE date = " + Lit(D(date)) + " ORDER BY shift";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(Read(rd));
        }
        catch (Exception ex) { error = Short(ex); }
        return list;
    }

    /// <summary>某段日期内的班次记录（含首尾）。</summary>
    public static List<ShiftCalendarRow> InRange(DbConnection conn, DateTime from, DateTime to, out string error)
    {
        error = "";
        var list = new List<ShiftCalendarRow>();
        if (conn == null) { error = "没有数据库连接"; return list; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectCols + $" WHERE date >= {Lit(D(from))} AND date <= {Lit(D(to))} ORDER BY date, shift";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(Read(rd));
        }
        catch (Exception ex) { error = Short(ex); }
        return list;
    }

    /// <summary>写回一条班次记录（新增或更新）。失败返回原因，成功返回空串。</summary>
    public static string Upsert(DbConnection conn, ShiftCalendarRow row)
    {
        if (conn == null) return "没有数据库连接";
        if (row == null || string.IsNullOrWhiteSpace(row.Shift)) return "班次不能为空";
        try
        {
            // 先删后插: 各家 upsert 语法不同(SQLite ON CONFLICT / PG ON CONFLICT / DM MERGE),
            // 主键是 (date, shift), 删一条再插一条语义等价且到处都能跑。
            using (var del = conn.CreateCommand())
            {
                del.CommandText = $"DELETE FROM shift_calendar WHERE date = {Lit(D(row.Date))} AND shift = {Lit(row.Shift)}";
                del.ExecuteNonQuery();
            }
            using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO shift_calendar (date, shift, start_time, leader_name, is_blast_shift, weather, notes) VALUES ("
                + Lit(D(row.Date)) + ", " + Lit(row.Shift) + ", " + Lit(row.StartTime) + ", " + Lit(row.LeaderName) + ", "
                + (row.IsBlastShift ? "1" : "0") + ", " + Lit(row.Weather) + ", " + Lit(row.Notes) + ")";
            ins.ExecuteNonQuery();
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    /// <summary>删一条班次记录。</summary>
    public static string Delete(DbConnection conn, DateTime date, string shift)
    {
        if (conn == null) return "没有数据库连接";
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM shift_calendar WHERE date = {Lit(D(date))} AND shift = {Lit(shift)}";
            cmd.ExecuteNonQuery();
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    /// <summary>
    /// 本月（<paramref name="anyDayInMonth"/> 所在自然月）的有效作业日。
    /// 台账未接通或本月无任何班次记录时 <see cref="MonthWorkdayInfo.FromLedger"/>=false，
    /// **不猜一个数出来** —— 调用方自己决定兜底到月计划口径还是 <see cref="FallbackMonthWorkdays"/>。
    /// </summary>
    public static MonthWorkdayInfo MonthWorkdays(DbConnection conn, DateTime anyDayInMonth)
    {
        var first = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        string monthLabel = first.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var rows = InRange(conn, first, last, out string err);
        if (err.Length > 0)
            return new MonthWorkdayInfo { MonthLabel = monthLabel, Label = $"作业日：班次日历未接通（{err}）" };
        if (rows.Count == 0)
            return new MonthWorkdayInfo { MonthLabel = monthLabel, Label = $"作业日：{monthLabel} 无班次日历记录" };

        var byDate = rows.GroupBy(r => r.Date.Date).ToList();
        int noTime = byDate.Count(g => g.All(r => string.IsNullOrWhiteSpace(r.StartTime)));

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

    /// <summary>裁定本月的作业日除数（月→日、月→周**共用这一份口径**）。三层：日历 → 月计划 → 兜底。</summary>
    public static WorkdayResolution ResolveWorkdays(DbConnection conn, DateTime anyDayInMonth, double planWorkdays)
    {
        var info = MonthWorkdays(conn, anyDayInMonth);
        var r = new WorkdayResolution { Info = info, PlanWorkdays = Math.Max(0, planWorkdays) };

        if (info.FromLedger && info.Workdays > 0)
        {
            r.Workdays = info.Workdays;
            r.Source = WorkdaySource.Calendar;
            r.Basis = $"日历口径 {info.Workdays} 天" + (r.Disagrees ? $"，月计划按 {planWorkdays:0} 天编制" : "");
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
    /// 生成整月班次（每天三班 A/B/C）。**已排过的日子原样保留**、不覆盖 ——
    /// 排班表上人工改过的东西不能被"再生成一次"抹掉。返回 (要写入的行, 保留的日子)。
    /// </summary>
    public static (List<ShiftCalendarRow> Rows, List<DateTime> KeptDays) BuildMonth(
        DateTime anyDayInMonth, IReadOnlyList<ShiftCalendarRow> existing,
        string[]? shifts = null, string[]? startTimes = null)
    {
        shifts ??= new[] { "A", "B", "C" };
        startTimes ??= new[] { "08:00", "16:00", "00:00" };
        var first = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        int days = DateTime.DaysInMonth(first.Year, first.Month);

        var had = new HashSet<DateTime>((existing ?? Array.Empty<ShiftCalendarRow>()).Select(r => r.Date.Date));
        var rows = new List<ShiftCalendarRow>();
        var kept = new List<DateTime>();
        for (int i = 0; i < days; i++)
        {
            var d = first.AddDays(i);
            if (had.Contains(d)) { kept.Add(d); continue; }
            for (int k = 0; k < shifts.Length; k++)
                rows.Add(new ShiftCalendarRow
                {
                    Date = d,
                    Shift = shifts[k],
                    StartTime = k < startTimes.Length ? startTimes[k] : null,
                });
        }
        return (rows, kept);
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

    /// <summary>早/中/夜班 → A/B/C（写回台账用）。**认不出就原样写回，不猜**。</summary>
    public static string ShiftCode(string? name) => (name ?? "").Trim() switch
    {
        "早班" => "A",
        "中班" => "B",
        "夜班" => "C",
        _ => (name ?? "").Trim(),
    };

    /// <summary>SQL 字符串字面量（单引号翻倍）；null/空 → NULL。</summary>
    private static string Lit(string? s)
        => string.IsNullOrEmpty(s) ? "NULL" : "'" + s.Replace("'", "''") + "'";

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
