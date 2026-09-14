using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;

namespace PitMine3D.Kylin.Data;

/// <summary>穿孔作业计划一条（对应表 <c>drill_plan</c>，字段同原 <c>DrillPlan</c> 实体）。</summary>
public sealed class DrillPlanRow
{
    /// <summary>钻机编号（= <c>equipment.equipment_id</c>）。</summary>
    public string EquipmentId { get; set; } = "";
    /// <summary>作业日期 yyyy-MM-dd。</summary>
    public string PlanDate { get; set; } = "";
    /// <summary>起 HH:mm。</summary>
    public string StartTime { get; set; } = "";
    /// <summary>止 HH:mm（同日内；跨零点拆两条）。</summary>
    public string EndTime { get; set; } = "";
    /// <summary>待爆区/平盘（对 <c>blast_event.location_code</c> 或作业面名）。</summary>
    public string Zone { get; set; } = "";
    /// <summary>台阶标高 m。null = 未录 —— <b>0 是合法标高</b>，不拿 0 冒充。</summary>
    public double? BenchElevationM { get; set; }
    /// <summary>计划孔数。null = 未录（0 也是合法孔数）。</summary>
    public int? HoleCount { get; set; }
    /// <summary><b>单孔</b>延米 m。null = 未录。总延米 = 孔数 × 单孔（换算见 <see cref="DrillPlanStore.ToDrillInputs"/>）。</summary>
    public double? HoleLengthM { get; set; }
    /// <summary>计划 / 进行中 / 完成 / 取消。</summary>
    public string Status { get; set; } = DrillPlanStore.StatusPlanned;
    public string? Note { get; set; }
}

/// <summary>
/// 穿孔作业计划台账（<c>drill_plan</c>，V044）—— 工序链「穿孔 → 爆破 → 采装」缺的那一环。
///
/// <para>
/// 这张表 V044 建好之后<b>一直零消费者</b>：与 §三三七 的去向台账、§三五一 的作业区域是同一种情形。
/// 表建好没有入口，等于那张表永远没人填 —— 于是钻机一条任务都排不出来、甘特里没有穿孔条、
/// 工序进度的穿孔一栏恒 0%、「钻爆计划衔接」拿不到穿孔窗口。本类接通它。
/// </para>
///
/// <b>照搬原版的四条口径</b>：
/// <list type="bullet">
///   <item><b>与 <c>blast_event</c> 的分工</b>：blast_event 是<b>已发生的事实</b>（炮次/装药/方量/单耗，事后填），
///     本表是<b>计划</b>（明天谁去哪打孔）。两者按 待爆区 + 日期 相互对照 ——
///     有穿孔计划却迟迟没有对应炮次 = 采准脱节，这正是「衔接」二字要看的东西。不做外键：
///     现场先打孔后补炮记录、一次穿孔分两次爆破都是常态，硬约束会逼人造假数据。</item>
///   <item><b>跨零点拆两条</b>：装箱时窗是同一天内的 <c>[起,止)</c>，绕回 0 点会把次日的活算进今天。
///     故"止 ≤ 起"一律拒收，并把该怎么拆说清楚（同 <see cref="MaintenanceWindows"/>）。</item>
///   <item><b>起止解析不出来的行丢弃并计数</b>：一条 (0,0) 的穿孔任务在时窗里不与任何班次重叠，
///     等于静默失效 —— 界面上看得见、计划里没有，最难查。</item>
///   <item><b>台账记的是单孔延米</b>，总延米 = 孔数 × 单孔。这一条不换算的话，
///     一条 300 个孔 15 m 的穿孔任务在下游会变成 15 m 的活。</item>
/// </list>
///
/// <b>时刻口径只此一份</b>：解析/格式化一律走 <see cref="MaintenanceWindows.Hour"/> /
/// <see cref="MaintenanceWindows.HourText"/>（<c>shift_calendar.start_time</c>、
/// <c>blast_event.blast_time</c>、<c>maintenance_window.start_time</c> 同一套）。
/// 各写一份的话，同一个「24:00」会在两张表里判出两个结果。
/// </summary>
public static class DrillPlanStore
{
    public const string StatusPlanned = "计划";
    public const string StatusRunning = "进行中";
    public const string StatusDone = "完成";
    public const string StatusCancelled = "取消";

    /// <summary>四种状态（界面下拉与校验共用一处）。</summary>
    public static readonly string[] Statuses = { StatusPlanned, StatusRunning, StatusDone, StatusCancelled };

    private const string SelectCols =
        "SELECT equipment_id, plan_date, start_time, end_time, zone, bench_elevation_m, "
      + "hole_count, hole_length_m, status, note FROM drill_plan";

    // ── 校验 ────────────────────────────────────────────────────────────────

    /// <summary>校验一条的起止。返回 null 表示通过，否则是给人看的原因。</summary>
    public static string? Validate(string? start, string? end)
    {
        double? s = MaintenanceWindows.Hour(start), e = MaintenanceWindows.Hour(end);
        if (s is null) return "起始时刻写成 HH:mm（如 08:00），或直接写小时数。";
        if (e is null) return "结束时刻写成 HH:mm（如 16:00），或直接写小时数。";
        if (e.Value <= s.Value)
            return "止必须晚于起。跨零点请拆两条：当日 22:00–24:00 + 次日 00:00–02:00 —— "
                 + "装箱时窗是同一天内的 [起,止)，绕回 0 点会把次日的活算进今天。";
        return null;
    }

    /// <summary>状态归一：空/不认识一律当「计划」（不新造一个状态出来）。</summary>
    public static string NormalizeStatus(string? raw)
    {
        string t = (raw ?? "").Trim();
        return Statuses.Contains(t) ? t : StatusPlanned;
    }

    // ── 读写 ────────────────────────────────────────────────────────────────

    public static List<DrillPlanRow> ByDate(DbConnection? conn, DateTime date, out string error)
    {
        error = "";
        var list = new List<DrillPlanRow>();
        if (conn == null) { error = "没有数据库连接"; return list; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectCols + " WHERE plan_date = " + Lit(D(date))
                            + " ORDER BY start_time, equipment_id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new DrillPlanRow
                {
                    EquipmentId = Str(rd, 0), PlanDate = Str(rd, 1), StartTime = Str(rd, 2), EndTime = Str(rd, 3),
                    Zone = Str(rd, 4), BenchElevationM = Dbl(rd, 5),
                    HoleCount = Dbl(rd, 6) is { } hc ? (int)Math.Round(hc) : null,
                    HoleLengthM = Dbl(rd, 7),
                    Status = NormalizeStatus(Str(rd, 8)),
                    Note = rd.IsDBNull(9) ? null : Str(rd, 9),
                });
        }
        catch (Exception ex) { error = Short(ex); }
        return list;
    }

    /// <summary>写回一条（主键 钻机 + 日期 + 起）。校验不过直接返回原因，不写脏数据进台账。</summary>
    public static string Upsert(DbConnection? conn, DrillPlanRow? row)
    {
        if (conn == null) return "没有数据库连接";
        if (row == null) return "没有要保存的行";
        if (string.IsNullOrWhiteSpace(row.EquipmentId)) return "请先填钻机编号。";
        if (string.IsNullOrWhiteSpace(row.PlanDate)) return "请先填作业日期。";
        var bad = Validate(row.StartTime, row.EndTime);
        if (bad != null) return bad;
        try
        {
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM drill_plan WHERE equipment_id = " + Lit(row.EquipmentId.Trim())
                                + " AND plan_date = " + Lit(row.PlanDate.Trim())
                                + " AND start_time = " + Lit(row.StartTime.Trim());
                del.ExecuteNonQuery();
            }
            using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO drill_plan (equipment_id, plan_date, start_time, end_time, zone, "
              + "bench_elevation_m, hole_count, hole_length_m, status, note) VALUES ("
              + Lit(row.EquipmentId.Trim()) + ", " + Lit(row.PlanDate.Trim()) + ", " + Lit(row.StartTime.Trim()) + ", "
              + Lit(row.EndTime.Trim()) + ", " + Lit((row.Zone ?? "").Trim()) + ", "
              + Num(row.BenchElevationM) + ", " + Num(row.HoleCount) + ", " + Num(row.HoleLengthM) + ", "
              + Lit(NormalizeStatus(row.Status)) + ", " + Lit(row.Note) + ")";
            ins.ExecuteNonQuery();
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    public static string Delete(DbConnection? conn, string equipmentId, string planDate, string startTime)
    {
        if (conn == null) return "没有数据库连接";
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM drill_plan WHERE equipment_id = " + Lit(equipmentId)
                            + " AND plan_date = " + Lit(planDate) + " AND start_time = " + Lit(startTime);
            cmd.ExecuteNonQuery();
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    // ── 台账 → 装箱输入 ─────────────────────────────────────────────────────

    /// <summary>转换结果（<c>Bad</c>/<c>Cancelled</c> 要如实报出来，丢弃不能是静默的）。</summary>
    public sealed class DrillLoad
    {
        public List<DrillInput> Drills = new();
        /// <summary>起止时刻非法被丢弃的条数（<b>不按 0 点算</b>）。</summary>
        public int Bad;
        /// <summary>状态为「取消」不排的条数。</summary>
        public int Cancelled;
        /// <summary>没录孔数/延米的条数（进度只能按完成与否判）。</summary>
        public int WithoutQty;
        public string Label = "";
    }

    /// <summary>
    /// 台账行 → <see cref="DrillInput"/>。「取消」的不排；起止非法的丢弃并计数；
    /// <b>延米按 孔数 × 单孔 折成总延米</b>（台账那一列是单孔）。
    /// </summary>
    public static DrillLoad ToDrillInputs(IEnumerable<DrillPlanRow>? rows, DateTime date)
    {
        var res = new DrillLoad();
        var all = (rows ?? Enumerable.Empty<DrillPlanRow>()).Where(r => r != null).ToList();
        foreach (var r in all)
        {
            if (string.Equals((r.Status ?? "").Trim(), StatusCancelled, StringComparison.Ordinal)) { res.Cancelled++; continue; }
            double? s = MaintenanceWindows.Hour(r.StartTime), e = MaintenanceWindows.Hour(r.EndTime);
            if (s is null || e is null || e.Value <= s.Value) { res.Bad++; continue; }
            res.Drills.Add(new DrillInput
            {
                EquipId = (r.EquipmentId ?? "").Trim(),
                Zone = (r.Zone ?? "").Trim(),
                BenchElevationM = r.BenchElevationM ?? 0,
                Start = s.Value,
                End = e.Value,
                HoleCount = r.HoleCount,
                // 台账是**单孔**延米，总延米 = 孔数 × 单孔
                HoleLengthM = r.HoleCount is > 0 && r.HoleLengthM is > 0
                    ? r.HoleCount.Value * r.HoleLengthM.Value
                    : r.HoleLengthM,
            });
        }

        res.WithoutQty = res.Drills.Count(x => x.HoleCount is not > 0 && x.HoleLengthM is not > 0);
        res.Label = all.Count == 0
            ? $"穿孔：{date:MM-dd} 无穿孔计划（在「钻爆计划衔接」里排）"
            : $"穿孔：{res.Drills.Count} 条计划（{date:MM-dd}）"
              + (res.WithoutQty > 0 ? $"；其中 {res.WithoutQty} 条没录孔数/延米，进度只能按完成与否判" : "")
              + (res.Cancelled > 0 ? $"；{res.Cancelled} 条已取消不排" : "")
              + (res.Bad > 0 ? $"；{res.Bad} 条起止时刻非法已丢弃（不按 0 点算）" : "");
        return res;
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────

    internal static string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Str(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
    private static double? Dbl(DbDataReader rd, int i)
        => rd.IsDBNull(i) ? null
         : double.TryParse(rd.GetValue(i)?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    private static string Lit(string? s) => string.IsNullOrEmpty(s) ? "NULL" : "'" + s.Replace("'", "''") + "'";
    private static string Num(double? v) => v.HasValue ? v.Value.ToString("R", CultureInfo.InvariantCulture) : "NULL";
    private static string Num(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "NULL";
    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
