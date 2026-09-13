// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/DayCapacityCalendar.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  逐日能力日历 —— 月计划摊到每一天的**依据**。
//
//  在它出现之前，月→日是一句 `月量 ÷ 作业日`：整月每天目标一模一样。
//  于是「今天这台电铲上午定修」「今天下午两点放炮清场 40 分钟」「这个面备采只够两天」
//  这三件事对当天的目标量**毫无影响** —— 计划把它排满，现场干不出来，
//  月末回头看是一堆"当日欠产"，而每一天当时看着都很正常。
//
//  本类给出每台设备每一天真正能干几个小时，月计划按它加权摊：
//
//      当天可用工时 = Σ班( 班时窗 − 检修档期 − 爆破清场 − 交接损失 )      ← WorkWindowCalc（与装箱同一套）
//      当天权重     = 班产 × 当天可用工时 × 设备可用率
//      某面某天目标 = 该面月量 × 当天权重 / 该面本月各作业日权重之和
//
//  **设备可用率**是"突发情况"进入前瞻计划的口径：按该设备近期**非计划故障**的实际停机占比折算
//  （计划检修不算——它已经在可用工时里扣过了，再扣一次是双重惩罚）。
//  没有历史就是 1.0 并如实标注，不凭空打折。
//
//  三条纪律：
//   ① 读不到台账**不猜**：作业日读不到就退回整月自然日并标注，别假装是日历口径。
//   ② 与装箱共用 WorkWindowCalc：日历说"这天有 6.5 小时"，装箱那边就必须真能排 6.5 小时。
//   ③ 半小时以下的碎片按干不了算（与装箱的 avail>=0.5 门槛一致）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>某设备某一天的可用产能。</summary>
public sealed class DayCapacity
{
    public DateTime Date { get; init; }
    /// <summary>三班合计可用工时（已扣检修/爆破清场/交接）。</summary>
    public double AvailHours { get; init; }
    /// <summary>当天权重 = 班产 × 可用工时 × 设备可用率。月计划按它摊。</summary>
    public double Weight { get; init; }
    /// <summary>这一天扣掉了多少小时（相对班制满时窗），用于解释"为什么这天排得少"。</summary>
    public double LostHours { get; init; }
    public string LostReason { get; init; } = "";
}

/// <summary>一台设备一个月的能力日历。</summary>
public sealed class CapacityCalendar
{
    public string EquipId { get; init; } = "";
    public IReadOnlyList<DayCapacity> Days { get; init; } = Array.Empty<DayCapacity>();
    /// <summary>设备可用率（近期非计划故障折算）。1.0 = 无历史或无故障。</summary>
    public double AvailabilityFactor { get; init; } = 1.0;
    public string SourceLabel { get; init; } = "";

    public double TotalWeight => Days.Sum(d => d.Weight);

    /// <summary>某一天的权重；不是作业日返回 0。</summary>
    public double WeightOn(DateTime d)
        => Days.FirstOrDefault(x => x.Date.Date == d.Date)?.Weight ?? 0;

    public DayCapacity? On(DateTime d) => Days.FirstOrDefault(x => x.Date.Date == d.Date);

    /// <summary>
    /// 某一天该分到多少量：<c>月量 × 当天权重 ÷ 全月权重和</c>。
    /// 全月权重为 0（整月都没能力）时返回 0 —— 不摊给一个干不了的日子。
    /// </summary>
    public double ShareOf(DateTime d, double monthTotal)
    {
        double tot = TotalWeight;
        return tot <= 1e-9 ? 0 : monthTotal * WeightOn(d) / tot;
    }
}

public static class DayCapacityCalendar
{
    /// <summary>可用率的回看天数（太短抓不到规律，太长把早就修好的老毛病也算进来）。★工程缺省。</summary>
    private const int LookBackDays = 14;

    /// <summary>可用率下限：再差的设备也不按低于此值排，否则一次大修会把它整月踢出计划。★工程缺省。</summary>
    private const double MinAvailability = 0.5;

    /// <summary>
    /// 建某台设备在 <paramref name="anyDayInMonth"/> 所在整月的能力日历。
    /// </summary>
    /// <param name="capacityM3PerH">编组班产（已含天气降效由调用方决定要不要先乘）。</param>
    public static CapacityCalendar Build(string equipId, DateTime anyDayInMonth,
                                         IReadOnlyList<ShiftWindow> defaultShifts,
                                         double capacityM3PerH, double handoverH)
    {
        var first = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var notes = new List<string>();

        // ── ① 作业日：至少有一条班次记录的日期 ──
        var workdays = LoadWorkdays(first, last, out string wdNote);
        notes.Add(wdNote);

        // ── ② 检修档期 / ③ 爆破窗口（整月一次读完，逐日分组）──
        var maintByDay = LoadMaintenance(first, last, out string mNote);
        notes.Add(mNote);
        var blastByDay = LoadBlasts(first, last, out string bNote);
        notes.Add(bNote);

        // ── ④ 设备可用率：近期非计划故障 ──
        double avail = Availability(equipId, anyDayInMonth, out string aNote);
        notes.Add(aNote);

        double fullDayHours = defaultShifts?.Sum(s => Math.Max(0, s.End - s.Start)) ?? 24;
        var days = new List<DayCapacity>();

        foreach (var d in workdays)
        {
            maintByDay.TryGetValue(d.Date, out var maint);
            blastByDay.TryGetValue(d.Date, out var blasts);

            double hours = WorkWindowCalc.DayHours(defaultShifts ?? Array.Empty<ShiftWindow>(),
                                                   equipId, maint, blasts, handoverH);
            double lost = Math.Max(0, fullDayHours - hours);

            var why = new List<string>();
            if (maint is { Count: > 0 }) why.Add($"检修 {maint.Sum(x => Math.Max(0, x.End - x.Start)):0.#}h");
            if (blasts is { Count: > 0 }) why.Add($"{blasts.Count} 炮清场");
            if (lost > 0.05 && why.Count == 0) why.Add("交接损失");

            days.Add(new DayCapacity
            {
                Date = d.Date,
                AvailHours = hours,
                Weight = capacityM3PerH * hours * avail,
                LostHours = lost,
                LostReason = string.Join(" + ", why),
            });
        }

        return new CapacityCalendar
        {
            EquipId = equipId,
            Days = days,
            AvailabilityFactor = avail,
            SourceLabel = string.Join("；", notes.Where(s => s.Length > 0)),
        };
    }

    // ── 台账读取（每一处读不到都如实说，不猜）──────────────────────────────────

    private static List<DateTime> LoadWorkdays(DateTime first, DateTime last, out string note)
    {
        try
        {
            var rows = EquipmentDataContext.ShiftCalendar.InRange(first, last).Where(r => r != null).ToList();
            if (rows.Count > 0)
            {
                var days = rows.Select(r => r.Date.Date).Distinct().OrderBy(x => x).ToList();
                note = $"作业日 {days.Count} 天（班次日历）";
                return days;
            }
            note = $"作业日：{first:yyyy-MM} 无班次日历记录，按整月自然日兜底";
        }
        catch (Exception ex)
        {
            note = $"作业日：班次日历未接通（{Short(ex)}），按整月自然日兜底";
        }

        var all = new List<DateTime>();
        for (var d = first; d <= last; d = d.AddDays(1)) all.Add(d);
        return all;
    }

    private static Dictionary<DateTime, List<MaintenanceWindow>> LoadMaintenance(
        DateTime first, DateTime last, out string note)
    {
        var map = new Dictionary<DateTime, List<MaintenanceWindow>>();
        try
        {
            var rows = EquipmentDataContext.MaintenanceWindows.InRange(first, last).Where(r => r != null).ToList();
            int bad = 0;
            foreach (var r in rows)
            {
                // PlanDate 在台账里是字符串，解析不出来的整条丢弃并计数 ——
                // 落到某个默认日期上会把一台设备的检修算到别的天头上，比丢掉更糟
                if (!DateTime.TryParse(r.PlanDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && !DateTime.TryParse(r.PlanDate, out day)) { bad++; continue; }
                double? s = ParseHour(r.StartTime), e = ParseHour(r.EndTime);
                if (s is null || e is null || e.Value <= s.Value) { bad++; continue; }
                if (!map.TryGetValue(day.Date, out var list)) map[day.Date] = list = new List<MaintenanceWindow>();
                list.Add(new MaintenanceWindow
                {
                    EquipId = (r.EquipmentId ?? "").Trim(),
                    Start = s.Value, End = e.Value,
                    Label = string.IsNullOrWhiteSpace(r.Kind) ? "检修" : r.Kind!.Trim(),
                });
            }
            note = $"检修 {rows.Count - bad} 条" + (bad > 0 ? $"（{bad} 条日期/时刻非法已丢弃）" : "");
        }
        catch (Exception ex) { note = $"检修：档期表未接通（{Short(ex)}），不扣检修"; }
        return map;
    }

    private static Dictionary<DateTime, List<BlastWindow>> LoadBlasts(
        DateTime first, DateTime last, out string note)
    {
        var map = new Dictionary<DateTime, List<BlastWindow>>();
        try
        {
            var rows = EquipmentDataContext.Blast.InRange(first, last).Where(b => b != null).ToList();
            int noTime = 0;
            foreach (var b in rows)
            {
                double? t = ParseHour(b.BlastTime);
                if (t is null) { noTime++; continue; }
                var d = b.BlastDate.Date;
                if (!map.TryGetValue(d, out var list)) map[d] = list = new List<BlastWindow>();
                list.Add(new BlastWindow(t.Value, t.Value + ClearanceH));
            }
            foreach (var k in map.Keys.ToList()) map[k] = BlastWindow.Merge(map[k]).ToList();
            note = $"爆破 {rows.Count - noTime} 炮" + (noTime > 0 ? $"（{noTime} 炮没记时刻，排不进时窗）" : "");
        }
        catch (Exception ex) { note = $"爆破：台账未接通（{Short(ex)}），不扣清场"; }
        return map;
    }

    /// <summary>爆破后的清场时长 h（台账只记爆破时刻）。与 ProductionPlanContext 同一个缺省。</summary>
    private const double ClearanceH = 0.67;

    /// <summary>
    /// 设备可用率 = 1 − 近 <see cref="LookBackDays"/> 天**非计划故障**停机 ÷ 同期日历工时。
    /// <para>
    /// 计划检修不计：它已经在"可用工时"里按档期扣过了，再折一次是双重惩罚。
    /// 没有故障记录就返回 1.0 并说明"无历史"——不凭空给一个悲观系数。
    /// </para>
    /// </summary>
    private static double Availability(string equipId, DateTime anchor, out string note)
    {
        double stop = 0;
        int daysWithData = 0;
        for (int i = 1; i <= LookBackDays; i++)
        {
            var d = anchor.Date.AddDays(-i);
            List<Domain.FaultEvent> faults;
            try { faults = TaskPersistence.LoadFaults(PitMine3D.Kylin.Platform.ProjectPeriodFormat.DateLabel(d)); }
            catch { continue; }
            if (faults.Count == 0) continue;
            daysWithData++;
            stop += faults
                .Where(f => !f.IsPlanned && string.Equals(f.EquipId, equipId, StringComparison.OrdinalIgnoreCase))
                .Sum(f => f.DurationHours);
        }

        if (daysWithData == 0) { note = "可用率 1.00（近期无故障记录）"; return 1.0; }

        double calendarH = daysWithData * 24.0;
        double a = Math.Clamp(1 - stop / Math.Max(1, calendarH), MinAvailability, 1.0);
        note = stop > 0.05
            ? $"可用率 {a:0.00}（近 {daysWithData} 天有记录，非计划停机 {stop:0.#}h）"
            : $"可用率 1.00（近 {daysWithData} 天无非计划停机）";
        return a;
    }

    private static double? ParseHour(string? hhmm)
    {
        string s = (hhmm ?? "").Trim();
        if (s.Length == 0) return null;
        int i = s.IndexOf(':');
        if (i <= 0) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        if (!int.TryParse(s[..i], out int h)) return null;
        int.TryParse(s[(i + 1)..].Split(':')[0], out int m);
        return h + m / 60.0;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 48 ? m : m[..48] + "…";
    }
}
