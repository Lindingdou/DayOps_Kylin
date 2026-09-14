using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 周计划的**取数壳**（对应原 <c>WeekPlanLink.Build</c>，按 Kylin 自己的库重接）。
/// 算法在 <see cref="WeekPlanLink.Compose"/>，这里只负责把四样输入凑齐：
/// 逐日班次数（<c>shift_calendar</c>）· 月计划基准（<c>monthly_plan</c>）· 逐日实绩（<c>daily_mine_summary</c>）·
/// 当日盘子（Kylin 无装箱引擎，恒 null → 自动退回月计划日均）。
///
/// **任何一处取不到都只降级为"判不了"，不抛、不造数**（原版这条纪律照搬）。
/// </summary>
public static class WeekPlanSource
{
    /// <summary>煤密度 t/m³：与 <see cref="Cad.LongTermPlan.DefaultCoalDensity"/> 同一个常量，不另立一份。</summary>
    public const double CoalDensity = Cad.LongTermPlan.DefaultCoalDensity;

    /// <summary>
    /// 「当日出煤」的口径 —— <c>daily_mine_summary</c> 把煤按**外运通道**分了六列，
    /// 库里没有任何一处定义过"合计"该怎么取，Kylin 侧此前也从未读过这张表。
    ///
    /// <b>这里默认按六列相加，并把所用列名如实写进结果的口径文案里</b>，
    /// 让看数的人一眼能核对："是不是把某两路重复算了"。若现场口径不同（例如某几路是内部倒运、
    /// 不该计入外运总量），改这一个数组即可，不必动别处。
    /// <b>此口径尚待现场确认</b>（见 §三三六 登记）。
    /// </summary>
    public static readonly string[] CoalColumns =
    {
        "big_belt_coal_t", "small_belt_coal_t", "longhua_coal_t",
        "truck_coal_export_t", "winnowed_coal_t", "big_truck_pile_coal_t",
    };

    /// <summary>筒仓三列是**库存**不是产出，故不计入当日出煤。</summary>
    public static string CoalBasisText => "当日出煤 = " + string.Join(" + ", CoalColumns) + "（筒仓存量不计）";

    /// <summary>装齐一周的输入。<paramref name="anchor"/> 传要看的那一周里的任意一天。</summary>
    public static WeekPlanInputs Load(DbConnection? conn, DateTime anchor, DateTime today)
    {
        var monday = WeekPlanLink.MondayOf(anchor);
        var inp = new WeekPlanInputs { Anchor = anchor.Date, Today = today.Date };

        if (conn == null) { inp.CalendarUsable = false; return inp; }

        // ① 逐日班次数（一次范围查询，不逐日查七遍）
        var shiftRows = WorkCalendar.InRange(conn, monday, monday.AddDays(6), out string calErr);
        if (calErr.Length > 0) { inp.CalendarUsable = false; }
        else
            foreach (var g in shiftRows.GroupBy(r => r.Date.Date))
                inp.ShiftsByDay[g.Key] = g.Count();

        // ② 本周涉及的每个月各算一份基准（跨月的周有两份）
        foreach (string key in Enumerable.Range(0, 7)
                     .Select(i => WeekPlanLink.MonthKeyOf(monday.AddDays(i)))
                     .Distinct(StringComparer.Ordinal))
            inp.Months[key] = MonthBasisOf(conn, key);

        // ③ 逐日实绩
        foreach (var kv in ActualsInRange(conn, monday, monday.AddDays(6)))
            inp.ActualsByDay[kv.Key] = kv.Value;

        // ④ 当日盘子：Kylin 无装箱引擎，留 null —— Compose 会自动退回月计划日均
        return inp;
    }

    /// <summary>某月的裂解基准：日采出 / 日剥离 = 月量 ÷ 作业日（除数走 <see cref="WorkCalendar"/> 那份口径）。</summary>
    public static MonthBasis MonthBasisOf(DbConnection? conn, string monthKey)
    {
        var basis = new MonthBasis { MonthKey = monthKey };
        if (conn == null || !TryMonth(monthKey, out int y, out int m)) return basis;

        var anyDay = new DateTime(y, m, 1);
        var wd = WorkCalendar.ResolveWorkdays(conn, anyDay, planWorkdays: 0);
        basis.Workdays = wd.Workdays;
        basis.WorkdayBasis = wd.Basis;
        basis.CalendarDays = wd.Info.FromLedger ? wd.Info.Workdays : 0;

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT plan_coal_wan_t, plan_strip_wan_m3 FROM monthly_plan WHERE year = {y} AND month = {m}";
            using var rd = cmd.ExecuteReader();
            if (rd.Read())
            {
                double coalWanT = rd.IsDBNull(0) ? 0 : Convert.ToDouble(rd.GetValue(0));
                double stripWanM3 = rd.IsDBNull(1) ? 0 : Convert.ToDouble(rd.GetValue(1));
                if (coalWanT > 0 || stripWanM3 > 0)
                {
                    basis.HasPlan = true;
                    basis.PlanLabel = $"月计划 {monthKey}";
                    if (basis.Workdays > 0)
                    {
                        // 煤按密度折方，与剥离统一成 m³ 实方（原版 MonthBasis 的口径）
                        basis.DayLoadM3 = coalWanT * 1e4 / CoalDensity / basis.Workdays;
                        basis.DayDumpM3 = stripWanM3 * 1e4 / basis.Workdays;
                    }
                }
            }
        }
        catch { /* 取不到就是没有月计划, 由 Usable 判掉 */ }
        return basis;
    }

    /// <summary>某段日期的逐日实绩（采出 m³ 实方, 剥离 m³）。没有记录的日子**不出现在字典里**（= 那天没录）。</summary>
    public static Dictionary<DateTime, (double Load, double Dump)> ActualsInRange(DbConnection? conn, DateTime from, DateTime to)
    {
        var map = new Dictionary<DateTime, (double, double)>();
        if (conn == null) return map;
        string coalSum = string.Join(" + ", CoalColumns.Select(c => $"COALESCE({c},0)"));
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT date, {coalSum}, COALESCE(stripping_total_m3,0) FROM daily_mine_summary "
                            + $"WHERE date >= '{from:yyyy-MM-dd}' AND date <= '{to:yyyy-MM-dd}'";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                if (!DateTime.TryParse(rd.GetValue(0)?.ToString() ?? "", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var d)) continue;
                double coalT = rd.IsDBNull(1) ? 0 : Convert.ToDouble(rd.GetValue(1));
                double stripM3 = rd.IsDBNull(2) ? 0 : Convert.ToDouble(rd.GetValue(2));
                map[d.Date] = (coalT / CoalDensity, stripM3);   // 煤 t → m³ 实方，与计划同口径
            }
        }
        catch { /* 表不通就是没实绩, 逐日显示"—" */ }
        return map;
    }

    private static bool TryMonth(string key, out int y, out int m)
    {
        y = m = 0;
        var p = (key ?? "").Split('-');
        return p.Length == 2 && int.TryParse(p[0], out y) && int.TryParse(p[1], out m) && m is >= 1 and <= 12;
    }
}
