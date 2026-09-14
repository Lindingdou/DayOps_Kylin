using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Data;

/// <summary>检修档期一条（对应表 <c>maintenance_window</c>，字段同原 <c>MaintenanceWindowPlan</c>）。</summary>
public sealed class MaintenanceWindowRow
{
    public string EquipmentId { get; set; } = "";
    /// <summary>检修日期 yyyy-MM-dd。</summary>
    public string PlanDate { get; set; } = "";
    /// <summary>起 HH:mm。</summary>
    public string StartTime { get; set; } = "";
    /// <summary>止 HH:mm（同日内；跨零点拆两条）。</summary>
    public string EndTime { get; set; } = "";
    public string Kind { get; set; } = "定修";
    public string? Note { get; set; }
}

/// <summary>一个班的时窗（小时制 [Start, End)）。</summary>
public readonly record struct ShiftWindow(string Name, double Start, double End);

/// <summary>
/// 检修档期（移植原 <c>TaskLib.Features.MaintenancePlanWindow</c> 的数据与判定部分）。
///
/// 表 <c>maintenance_window</c> 在 V042 就建好了，但和 <c>shift_calendar</c> 一样**一直没人读写** —— 本类接通它。
///
/// **照搬原版的三条口径**（都是注释里点名过的）：
///   · <b>跨零点必须拆两条</b>（当日 22:00–24:00 + 次日 00:00–02:00）。装箱时窗是同一天内的 <c>[起,止)</c>，
///     绕回 0 点会把次日的活算进今天 —— 所以"止 ≤ 起"一律拒收，并把该怎么拆说清楚。
///   · <b>时刻非法要如实标出来</b>。引擎会丢弃这类记录；界面若不标，人只会觉得"我填了怎么没生效"。
///   · <b>计划检修 ≠ 故障</b>。已经发生的停机属"故障报修"，那边算的是实际停机与完好率；两者不要混。
///
/// <b>一处不照抄原版的地方</b>：原版的 <c>Hour()</c> 走 <c>TimeSpan.TryParse</c>，而它的 hh:mm 只收 0..23 小时，
/// 于是 <c>24:00</c> 解析不出来 —— 可原版的提示语偏偏教用户"跨零点拆成 22:00–24:00"。
/// 照它说的填就会被判「时刻非法」，这是原版自相矛盾的一处。这里**按它的意图修**：单独认 <c>24:00</c> 为当日终点。
/// </summary>
public static class MaintenanceWindows
{
    private const string SelectCols =
        "SELECT equipment_id, plan_date, start_time, end_time, kind, note FROM maintenance_window";

    /// <summary>
    /// 时刻文本 → 小时数。认 <c>HH:mm</c>、<c>HH:mm:ss</c>，也认光写小时数（如 <c>8</c>、<c>22.5</c>）。
    /// 超出 0~24 或解析不了返回 null（调用方按"时刻非法"处理，不猜）。
    /// </summary>
    public static double? Hour(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return null;

        // 「24:00」= 当日终点。**必须单独认**：`TimeSpan.TryParse` 的 hh:mm 只收 0..23 小时，
        // 而原版一边用 TimeSpan.TryParse 解析、一边在提示里教用户"跨零点拆成 22:00–24:00" ——
        // 于是照它说的填就会被判成"时刻非法"。这是原版自相矛盾的一处，按它的**意图**修，
        // 不照抄这个行为（登记在类文档）。
        if (t is "24:00" or "24:00:00" or "24") return 24.0;

        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts) && ts.TotalHours is >= 0 and <= 24)
            return ts.TotalHours;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h is >= 0 and <= 24)
            return h;
        return null;
    }

    /// <summary>小时数 → <c>HH:mm</c>（24 小时整写成 24:00，不绕回 00:00）。</summary>
    public static string HourText(double h)
    {
        if (h >= 24) return "24:00";
        int hh = (int)Math.Floor(h);
        int mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh:00}:{mm:00}";
    }

    /// <summary>这条档期压到了哪几个班（让人一眼看出"这台今天早班干不了活"）。</summary>
    public static string Overlap(IReadOnlyList<ShiftWindow> shifts, double s, double e)
    {
        if (shifts == null || shifts.Count == 0) return "（本日无班次日历）";
        var hit = shifts.Where(w => s < w.End && e > w.Start).Select(w => w.Name).ToList();
        return hit.Count > 0 ? string.Join(" / ", hit) : "不压任何班次";
    }

    /// <summary>
    /// 由班次日历推出本日的班次时窗：按开班时刻排序，**每班到下一班开班为止**，最后一班到 24:00。
    ///
    /// **登记的差异**：原版的班次时窗来自排产盘子（`ProductionPlanContext.Shifts`，有显式起止）；
    /// Kylin 的 <c>shift_calendar</c> 只存开班时刻，故这里按"下一班开班即上一班结束"推。
    /// 这与三班倒的实际排法一致；若某天班次时刻不规则（如中间空岗），推出来的窗会偏长 —— 但那种情况
    /// 在只存开班时刻的表里本来就表达不了，不是这里猜错了。没填开班时刻的班**直接跳过**（不按 0 点算）。
    /// </summary>
    public static List<ShiftWindow> ShiftWindowsOf(IReadOnlyList<ShiftCalendarRow> rows)
    {
        var list = new List<ShiftWindow>();
        if (rows == null || rows.Count == 0) return list;

        var withTime = rows
            .Select(r => (Name: WorkCalendar.ShiftName(r.Shift), H: Hour(r.StartTime)))
            .Where(x => x.H.HasValue)
            .Select(x => (x.Name, Start: x.H!.Value))
            .OrderBy(x => x.Start)
            .ToList();
        if (withTime.Count == 0) return list;

        for (int i = 0; i < withTime.Count; i++)
        {
            double end = i + 1 < withTime.Count ? withTime[i + 1].Start : 24.0;
            if (end > withTime[i].Start) list.Add(new ShiftWindow(withTime[i].Name, withTime[i].Start, end));
        }
        return list;
    }

    /// <summary>校验一条档期的起止。返回 null 表示通过，否则是给人看的原因。</summary>
    public static string? Validate(string? start, string? end)
    {
        double? s = Hour(start), e = Hour(end);
        if (s is null || e is null) return "起止时刻要写成 HH:mm（如 08:00），或直接写小时数。";
        if (e.Value <= s.Value)
            return "止必须晚于起。跨零点请拆两条：当日 22:00–24:00 + 次日 00:00–02:00 —— "
                 + "装箱时窗是同一天内的 [起,止)，绕回 0 点会把次日的活算进今天。";
        return null;
    }

    // ── 读写 ────────────────────────────────────────────────
    public static List<MaintenanceWindowRow> ByDate(DbConnection conn, DateTime date, out string error)
    {
        error = "";
        var list = new List<MaintenanceWindowRow>();
        if (conn == null) { error = "没有数据库连接"; return list; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectCols + " WHERE plan_date = " + Lit(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                            + " ORDER BY equipment_id, start_time";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new MaintenanceWindowRow
                {
                    EquipmentId = Str(rd, 0), PlanDate = Str(rd, 1), StartTime = Str(rd, 2),
                    EndTime = Str(rd, 3), Kind = Str(rd, 4), Note = rd.IsDBNull(5) ? null : Str(rd, 5),
                });
        }
        catch (Exception ex) { error = Short(ex); }
        return list;
    }

    /// <summary>写回一条（主键 设备+日期+起）。校验不过直接返回原因，不写脏数据进台账。</summary>
    public static string Upsert(DbConnection conn, MaintenanceWindowRow row)
    {
        if (conn == null) return "没有数据库连接";
        if (row == null || string.IsNullOrWhiteSpace(row.EquipmentId)) return "请先选（或输入）设备编号。";
        var bad = Validate(row.StartTime, row.EndTime);
        if (bad != null) return bad;
        try
        {
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM maintenance_window WHERE equipment_id = " + Lit(row.EquipmentId)
                                + " AND plan_date = " + Lit(row.PlanDate) + " AND start_time = " + Lit(row.StartTime);
                del.ExecuteNonQuery();
            }
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO maintenance_window (equipment_id, plan_date, start_time, end_time, kind, note) VALUES ("
                            + Lit(row.EquipmentId) + ", " + Lit(row.PlanDate) + ", " + Lit(row.StartTime) + ", "
                            + Lit(row.EndTime) + ", " + Lit(string.IsNullOrWhiteSpace(row.Kind) ? "定修" : row.Kind) + ", "
                            + Lit(row.Note) + ")";
            ins.ExecuteNonQuery();
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    public static string Delete(DbConnection conn, string equipmentId, string planDate, string startTime)
    {
        if (conn == null) return "没有数据库连接";
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM maintenance_window WHERE equipment_id = " + Lit(equipmentId)
                            + " AND plan_date = " + Lit(planDate) + " AND start_time = " + Lit(startTime);
            cmd.ExecuteNonQuery();
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    private static string Str(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
    private static string Lit(string? s) => string.IsNullOrEmpty(s) ? "NULL" : "'" + s.Replace("'", "''") + "'";
    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
