// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/DrillPlanWriter.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;                   // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data.Entities;          // DrillPlan / ProcessZone / ShiftCalendar
namespace PitMine3D.Kylin.Cad.Units;

// ─────────────────────────────────────────────────────────────────────────────
//  排产结果 + 工序作业区 → 穿孔作业计划（drill_plan / V044）。工序链的第一环。
//
//  ══ 这一环之前是断的 ══
//  `drill_plan` 表建了（V044），可**没有生产者**：只有人手工往里录。于是台账模式下
//  钻机一条任务都排不出来 —— 甘特里没有穿孔条、工序进度跟踪的穿孔一栏恒 0%、
//  「钻爆计划衔接」拿不到穿孔窗口。而 `EquipmentAssigner` 其实早就把
//  「哪台钻机、第几个工日、打哪个单元、多少控制方量」算出来了，只是没人把它落到表上。
//
//  ══ 两边各出一半 ══
//  · **谁 / 何时 / 多少** ← `EquipmentAssigner` 的穿孔笔（`MachineAssignment` Role=Drill）；
//  · **在哪儿 / 什么标高** ← 本期**穿孔工序区**（`process_zone`，process='drill'）。
//  这正是工序区存在的理由：穿孔区是采装区沿推进方向**前推超前期**的那一段地 ——
//  把钻机的位置写成采装面的位置，钻机就被派到了电铲脚下，而报表上每个数都正常。
//
//  ══ 三条口径 ══
//  **D1 延米与孔数一律留 NULL，不写 0。**
//      孔网参数（孔距/排距/超深）在 `FaceProcessChain`（PlanLib）里，本模块<b>引不到</b>，
//      而 GeoDataBase 的实体里一条都没有。**「免爆」与「算不出」的延米都是 0，报表上一模一样** ——
//      写 0 等于把缺口藏起来。V044 的这两列本就是可空的，NULL 才是「未录」的正确表达。
//      算不出多少条要**报出来**，别让人以为表里就是没有孔。
//  **D2 工日序号 → 日历日期走班次日历。**
//      排产器输出的是「第几个工日」，不是日期。作业日 = 本月 `shift_calendar` 里
//      **有班的那些日子**（与 `WorkCalendar.MonthWorkdays` 同一口径）。
//      日历给不出足够的作业日时**拒绝写**，不拿自然日顺延顶替 ——
//      顺延出来的日期看着完全正常，而钻机会被排到本来不上班的那天。
//  **D3 收班时刻是推出来的，不是表里的。**
//      `shift_calendar` **只有开班时刻，没有收班时刻**。本类按当日各班开班时刻的间隔
//      推一个班长（只有一班时按 24h），末班收班 = 末班开班 + 班长。
//      跨零点按 V044 的要求截到 23:59 并**说明** —— 绕回 0 点会把次日的活算进今天。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次写盘的结果。永不抛，失败以 <see cref="Notes"/> 表达。</summary>
public sealed class DrillPlanWriteResult
{
    public int Written;
    public int Skipped;
    /// <summary>延米/孔数算不出的条数（一律 NULL，不写 0）。</summary>
    public int NoHoleParams;
    /// <summary>没配上穿孔工序区的条数（zone 为空，钻爆衔接对不上）。</summary>
    public int NoZone;
    public List<string> Notes = new();
    public string Headline = "";
    public bool Ok => Written > 0;
}

/// <summary>排产结果 → <c>drill_plan</c>。纯写盘，不做排产。</summary>
public static class DrillPlanWriter
{
    /// <summary>
    /// 把本次排产的穿孔笔写进 <c>drill_plan</c>。
    /// </summary>
    /// <param name="result">排产结果（<see cref="EquipmentAssignResult.DrillingScheduled"/> 为假时不写）。</param>
    /// <param name="period">期次 yyyy-MM —— 决定去哪一期取工序区、以及工日落在哪个月。</param>
    /// <param name="dryRun">true = 只算不写盘（界面先给人看一眼）。</param>
    public static DrillPlanWriteResult Write(EquipmentAssignResult? result, string period, bool dryRun = false)
    {
        var res = new DrillPlanWriteResult();
        if (result == null) { res.Headline = "没有排产结果，本次不写穿孔计划。"; return res; }
        if (!result.DrillingScheduled)
        {
            res.Headline = "本次排产**没有排穿孔**（ScheduleDrilling 关着）—— 不写 drill_plan。"
                         + "这不代表不需要穿爆，只代表这份班表里没有它。";
            return res;
        }

        var drills = result.Drilling.Where(a => a != null && !string.IsNullOrWhiteSpace(a.MachineId)).ToList();
        if (drills.Count == 0) { res.Headline = "本次排产的穿孔笔为 0，无可写。"; return res; }

        // ── D2 工日序号 → 日历日期 ──
        var days = Workdays(period, res);
        if (days.Count == 0) { res.Headline = $"{period} 的班次日历里一个作业日都没有 —— **拒绝写**穿孔计划。"; return res; }

        int need = drills.Max(a => a.EndDay);
        if (need > days.Count)
        {
            res.Headline = $"排产用了 {need} 个工日，而 {period} 的班次日历只有 {days.Count} 个作业日 —— "
                         + "**拒绝写**穿孔计划。";
            res.Notes.Add("不拿自然日顺延顶替：顺延出来的日期看着完全正常，"
                        + "而钻机会被排到本来不上班的那天，谁也看不出来。"
                        + "补法：在「班次日历」里把本月的班排齐，或把排产的作业日数调成与日历一致。");
            return res;
        }

        // ── 在哪儿：本期穿孔工序区（单元号 → 区名 + 标高）──
        var zoneOf = DrillZones(period, res);

        var rows = BuildRows(drills, days, d => DayWindow(d, res), zoneOf, period, res);
        if (rows.Count == 0) { res.Headline = "算下来一条穿孔计划都没有。"; return res; }

        if (!dryRun)
        {
            try
            {
                var svc = EquipmentDataContext.DrillPlans;
                foreach (var r in rows) { svc.Upsert(r); res.Written++; }
            }
            catch (Exception ex)
            {
                res.Headline = $"写 drill_plan 失败（{ex.GetType().Name}：{Short(ex)}）—— 已写入 {res.Written} 条。";
                return res;
            }
        }
        else res.Written = rows.Count;

        res.Headline = (dryRun ? "试算：" : "已写入：")
                     + $"{res.Written} 条穿孔计划（{drills.Select(a => a.MachineId).Distinct().Count()} 台钻机 · "
                     + $"{rows.Select(r => r.PlanDate).Distinct().Count()} 个作业日）";

        if (res.NoZone > 0)
            res.Notes.Add($"◆ {res.NoZone} 条**没配上穿孔工序区**（zone 为空）—— "
                        + "「钻爆计划衔接」按 待爆区 + 日期 对照穿孔与炮次，zone 空着就对不上，"
                        + "那一段接续从此看不见。补法：到「作业区划分 · 工序作业区」为本期生成穿孔区并入库。");
        res.Notes.Add($"· {res.NoHoleParams} 条的**孔数与延米是 NULL（算不出），不是 0**。"
                    + "孔网参数（孔距/排距/超深）不在本模块可达范围。"
                    + "「免爆」与「算不出」的延米都是 0，报表上一模一样 —— 所以这里留空而不是填 0。"
                    + "要延米就在「确定开采程序」的工艺链上填孔网参数，由那一侧回填。");
        if (res.Skipped > 0)
            res.Notes.Add($"· {res.Skipped} 条因为那一天排不出时窗被跳过（班次日历里那天没有开班时刻）。");
        return res;
    }

    /// <summary>
    /// 排产笔 + 作业日 + 时窗 + 区位 → 计划行。<b>纯计算，不碰数据库</b>。
    /// <para>离线判据喂合成算例走它 —— <see cref="Write"/> 的每一步输入都来自台账，
    /// 裸台架里那些路径一律静默走兜底，拿它判等于什么都没判。</para>
    /// </summary>
    /// <param name="windowOf">某一天的 (起, 止, 说明)；起为空串 = 那天排不出时窗。</param>
    /// <param name="zoneOf">单元号 → (穿孔区名, 台阶标高)；取不到即 (null, null)。</param>
    public static List<DrillPlan> BuildRows(
        IReadOnlyList<MachineAssignment> drills,
        IReadOnlyList<DateTime> days,
        Func<DateTime, (string Start, string End, string Note)> windowOf,
        IReadOnlyDictionary<string, (string? Zone, double? BenchZ)> zoneOf,
        string period,
        DrillPlanWriteResult res)
    {
        var rows = new List<DrillPlan>();
        foreach (var a in drills)
        {
            for (int d = a.StartDay; d <= a.EndDay; d++)
            {
                if (d < 1 || d > days.Count) { res.Skipped++; continue; }
                var date = days[d - 1];
                var (start, end, note) = windowOf(date);
                if (string.IsNullOrEmpty(start)) { res.Skipped++; continue; }

                zoneOf.TryGetValue(a.UnitId ?? "", out var z);
                if (z.Zone == null) res.NoZone++;

                rows.Add(new DrillPlan
                {
                    EquipmentId = (a.MachineId ?? "").Trim(),
                    PlanDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    StartTime = start,
                    EndTime = end,
                    Zone = z.Zone ?? "",
                    BenchElevationM = z.BenchZ,
                    // D1：算不出就是 NULL，不是 0
                    HoleCount = null,
                    HoleLengthM = null,
                    Status = "计划",
                    Note = $"自动生成｜期次 {period}｜第 {d}/{days.Count} 工日｜单元 {a.UnitId}"
                         + $"｜控制方量 {a.AssignedM3:N0} m³"
                         + (z.Zone == null ? "｜⚠ 没配上穿孔工序区（钻爆衔接对不上）" : "")
                         + (note.Length > 0 ? "｜" + note : "")
                         + "｜孔数与延米未录（孔网参数不在本模块可达范围，见 D1）",
                });
                res.NoHoleParams++;
            }
        }
        return rows;
    }

    // ── D2：作业日 ────────────────────────────────────────────────────

    /// <summary>
    /// 本期的作业日（<c>shift_calendar</c> 里**有班的那些日子**，按日期升序）。
    /// <para>与 <c>WorkCalendar.MonthWorkdays</c> 同一口径 —— 两处口径不同的话，
    /// 排产用 25 天、写盘用 30 天，同一份班表会给出两套日期。</para>
    /// </summary>
    private static List<DateTime> Workdays(string period, DrillPlanWriteResult res)
    {
        var list = new List<DateTime>();
        if (!DateTime.TryParseExact((period ?? "").Trim() + "-01", "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        {
            res.Notes.Add($"◆ 期次「{period}」不是 yyyy-MM。");
            return list;
        }
        try
        {
            var last = first.AddMonths(1).AddDays(-1);
            foreach (var g in EquipmentDataContext.ShiftCalendar.InRange(first, last)
                                                  .Where(s => s != null)
                                                  .GroupBy(s => s.Date.Date)
                                                  .OrderBy(g => g.Key))
                list.Add(g.Key);
        }
        catch (Exception ex) { res.Notes.Add($"◆ 班次日历读不到（{Short(ex)}）。"); }
        return list;
    }

    // ── D3：一天的时窗 ────────────────────────────────────────────────

    /// <summary>
    /// 当日时窗 = 首班开班 ～ 末班开班 + 班长。
    /// <para><b>班长是推出来的</b>：<c>shift_calendar</c> 只有开班时刻，没有收班时刻。
    /// 按各班开班时刻的间隔推（只有一班时按 24h）。跨零点按 V044 截到 23:59 并说明 ——
    /// 绕回 0 点会把次日的活算进今天。</para>
    /// </summary>
    public static (string Start, string End, string Note) DayWindow(DateTime date, DrillPlanWriteResult res)
    {
        List<double> starts;
        try
        {
            starts = EquipmentDataContext.ShiftCalendar.ByDate(date)
                        .Where(s => s != null)
                        .Select(s => ParseHour(s.StartTime))
                        .Where(h => h >= 0)
                        .Distinct().OrderBy(h => h).ToList();
        }
        catch { return ("", "", ""); }

        if (starts.Count == 0) return ("", "", "");

        double span = starts.Count >= 2
            ? Median(Enumerable.Range(1, starts.Count - 1).Select(i => starts[i] - starts[i - 1]).ToList())
            : 24.0;
        double end = starts[^1] + span;

        string note = "";
        if (end >= 24.0)
        {
            end = 23.0 + 59.0 / 60.0;                     // V044：同日内 [start,end)
            note = "末班跨零点，收班时刻截到 23:59（V044 要求同日内；绕回 0 点会把次日的活算进今天）";
        }
        if (starts.Count == 1)
            note = (note.Length > 0 ? note + "；" : "") + "当日只有一个班，班长按 24h 推";

        return (Hhmm(starts[0]), Hhmm(end), note);
    }

    // ── 在哪儿：本期穿孔工序区 ────────────────────────────────────────

    /// <summary>单元号 → (穿孔区名, 台阶标高)。取不到就是取不到，<b>不拿采装区顶替</b>。</summary>
    private static Dictionary<string, (string? Zone, double? BenchZ)> DrillZones(
        string period, DrillPlanWriteResult res)
    {
        var map = new Dictionary<string, (string?, double?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var zones = EquipmentDataContext.ProcessZones
                            .ByProcess(period, ProcessZone.ProcDrill)
                            .Where(z => z != null && z.Active != 0).ToList();
            if (zones.Count == 0)
            {
                res.Notes.Add($"◆ {period} 没有**穿孔工序区** —— 写出来的计划全都不带待爆区，"
                            + "「钻爆计划衔接」对不上。"
                            + "★ 不拿采装区顶替：穿孔区是采装区沿推进方向**前推超前期**的那一段地，"
                            + "拿采装区的位置写钻机，钻机就被派到了电铲脚下，而报表上每个数都正常。");
                return map;
            }
            foreach (var z in zones)
            {
                double? bench = ParseAvgZ(z.PointsJson);
                foreach (var id in (z.UnitIds ?? "").Split(new[] { '、', ',', ';', '；' },
                                                           StringSplitOptions.RemoveEmptyEntries))
                {
                    string k = id.Trim();
                    if (k.Length > 0 && !map.ContainsKey(k)) map[k] = (z.Name, bench);
                }
            }
        }
        catch (Exception ex) { res.Notes.Add($"◆ 工序作业区读不到（{Short(ex)}）—— 计划不带待爆区。"); }
        return map;
    }

    /// <summary>环上顶点 Z 均值。<b>解不出返回 null，不返回 0</b>（0 是合法标高）。</summary>
    public static double? ParseAvgZ(string? pointsJson)
    {
        try
        {
            var flat = System.Text.Json.JsonSerializer.Deserialize<double[]>(pointsJson ?? "[]");
            if (flat == null || flat.Length < 9) return null;
            double s = 0; int n = 0;
            for (int i = 2; i < flat.Length; i += 3)
                if (!double.IsNaN(flat[i]) && !double.IsInfinity(flat[i])) { s += flat[i]; n++; }
            return n > 0 ? s / n : null;
        }
        catch { return null; }
    }

    // ── 小件 ──────────────────────────────────────────────────────────

    /// <summary>"HH:mm" → 小时数；解不出返回 -1（<b>不返回 0</b>，0 点是合法开班时刻）。</summary>
    public static double ParseHour(string? hhmm)
    {
        string t = (hhmm ?? "").Trim();
        if (t.Length == 0) return -1;
        var parts = t.Split(':');
        if (parts.Length < 1) return -1;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) return -1;
        int m = 0;
        if (parts.Length >= 2) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m);
        if (h < 0 || h > 23 || m < 0 || m > 59) return -1;
        return h + m / 60.0;
    }

    /// <summary>
    /// 小时数 → "HH:mm"。<b>≥24:00 一律截到 23:59</b>（V044 要求同日内）。
    /// <para><b>只夹小时是错的</b>：24.5 会被夹成 23:30 —— 一个凭空冒出来的时刻，
    /// 既不是 24:30 也不是本意的 23:59，而它看上去完全正常。判据 D3c 就是钉这一条的。</para>
    /// </summary>
    public static string Hhmm(double hour)
    {
        if (double.IsNaN(hour) || hour < 0) return "00:00";
        if (hour >= 24.0) return "23:59";
        int h = (int)Math.Floor(hour);
        int m = (int)Math.Round((hour - h) * 60);
        if (m >= 60) { h++; m -= 60; }
        if (h >= 24) return "23:59";
        return $"{h:00}:{m:00}";
    }

    public static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var v = xs.OrderBy(x => x).ToList();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) * 0.5;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
