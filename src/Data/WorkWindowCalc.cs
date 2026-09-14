using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Data;

/// <summary>停产时窗（爆破清场等）。<c>Start/End</c> 是小时制。</summary>
public sealed class BlastWindow
{
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>说明（"第 3 炮 · EP-08"），仅供显示。</summary>
    public string Label { get; set; } = "";

    public BlastWindow() { }
    public BlastWindow(double start, double end, string label = "")
    { Start = start; End = end; Label = label; }

    public double Hours => Math.Max(0, End - Start);
    public bool Valid => End > Start + 1e-9;

    /// <summary>重叠的窗口并成一段，按起点排。两炮挨着放会切出一段 0.001h 的碎片，那种段没有意义。</summary>
    public static List<BlastWindow> Merge(IEnumerable<BlastWindow>? windows)
    {
        var src = (windows ?? Enumerable.Empty<BlastWindow>())
            .Where(w => w != null && w.Valid)
            .OrderBy(w => w.Start)
            .ToList();

        var res = new List<BlastWindow>();
        foreach (var w in src)
        {
            var last = res.Count > 0 ? res[^1] : null;
            if (last != null && w.Start <= last.End + 1e-9)
            {
                if (w.End > last.End) last.End = w.End;
                if (w.Label.Length > 0) last.Label = last.Label.Length > 0 ? last.Label + " / " + w.Label : w.Label;
                continue;
            }
            res.Add(new BlastWindow(w.Start, w.End, w.Label));
        }
        return res;
    }

    /// <summary>从 [from,to) 里挖掉全部停产时窗，返回剩下的可作业段（按起点排；长度 ≤1e-9 的碎片丢弃）。</summary>
    public static List<(double Start, double End)> Subtract(double from, double to, IEnumerable<BlastWindow>? windows)
    {
        var res = new List<(double Start, double End)>();
        if (to <= from + 1e-9) return res;

        double cur = from;
        foreach (var w in Merge(windows))
        {
            if (w.End <= cur + 1e-9) continue;      // 整段在左边
            if (w.Start >= to - 1e-9) break;        // 整段在右边（已按起点排，后面的更右）
            if (w.Start > cur + 1e-9) res.Add((cur, Math.Min(w.Start, to)));
            cur = Math.Max(cur, w.End);
            if (cur >= to - 1e-9) return res;
        }
        if (to > cur + 1e-9) res.Add((cur, to));
        return res;
    }
}

/// <summary>检修/计划停机窗口（小时制，绑设备）。</summary>
public sealed class MaintenanceHourWindow
{
    public string EquipId { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Label { get; set; } = "检修";

    /// <summary>由台账行（<c>HH:mm</c> 文本）转成小时制；时刻非法返回 null（**引擎丢弃它，不按 0 点算**）。</summary>
    public static MaintenanceHourWindow? From(MaintenanceWindowRow row)
    {
        if (row == null) return null;
        double? s = MaintenanceWindows.Hour(row.StartTime), e = MaintenanceWindows.Hour(row.EndTime);
        if (s is null || e is null || e.Value <= s.Value) return null;
        return new MaintenanceHourWindow
        {
            EquipId = row.EquipmentId, Start = s.Value, End = e.Value,
            Label = string.IsNullOrWhiteSpace(row.Kind) ? "检修" : row.Kind,
        };
    }
}

/// <summary>
/// 「这台设备这个班到底能干几个小时」—— **唯一的算法**（移植原 <c>TaskLib.Engine.WorkWindowCalc</c>，逐行照移）。
///
/// 有效时窗 = 班时窗 − 检修档期 − 爆破清场 − 非首班交接损失（− 滚动重排起点）。
///
/// **为什么必须只有一处实现**（原注释的理由，照搬）：它有两个调用场景 ——
/// ① 装箱：算作业日当天各班能装多少；② 逐日能力日历：算整月每天的可用工时，月计划按它加权摊到各天。
/// 两边各写一份的话，"计划里那天排了 8 小时、实际那天在定修"这种错就会从缝里漏出去，
/// **而且不会有任何报错**。
///
/// §一九四c 曾把它记为「依赖受阻的班次日历配置 → 引擎内部，记录」；§三三二/§三三三 接通班次日历与
/// 检修档期之后那条依据不再成立 —— 它本身是 73 行纯逻辑，不碰数据库、不依赖引擎。
///
/// 爆破时窗（<c>BlastWindow</c>）这一维此前在 Kylin 侧**没有数据源**（当时「钻爆计划衔接」还没移），
/// 调用方一律传空表；§三五二 接上了：<see cref="BlastPlanLink.BlastWindowsOf"/> 由 <c>blast_event</c>
/// 逐炮「爆破时刻 → +清场」合并出来，装箱侧走 <c>ExploderConfig.BlastWindows()</c>。
/// 当时"算法照原样保留这一维"的那个决定省掉了一次回头改口径。
/// </summary>
public static class WorkWindowCalc
{
    /// <summary>
    /// 某设备在某个班的有效时窗 <c>[ws, we)</c>。返回 <c>we == ws</c> 表示这个班没有可用时间。
    /// </summary>
    /// <param name="fromHour">滚动重排起点：只排此刻之后。0 = 不限。</param>
    public static (double Ws, double We) Of(
        ShiftWindow sh, string? equipId,
        IReadOnlyList<MaintenanceHourWindow>? maintenance,
        IReadOnlyList<BlastWindow>? blasts,
        double handoverH, double fromHour = 0)
    {
        double ws = sh.Start, we = sh.End;

        // 检修：把班起点推到档期结束之后（档期跨整班时 ws 会被推过 we，下面会判掉）
        if (maintenance != null)
            foreach (var mw in maintenance)
                if (string.Equals(mw.EquipId, equipId, StringComparison.OrdinalIgnoreCase)
                    && mw.Start < sh.End && mw.End > sh.Start)
                    ws = Math.Max(ws, mw.End);

        if (sh.Start > 0) ws += handoverH;                 // 非首班交接损失
        if (fromHour > 0) ws = Math.Max(ws, fromHour);     // 滚动重排：仅排此刻之后
        if (we <= ws) return (ws, ws);

        // 爆破清场把班切成几段时只取**最长的一段**：一个班里让同一台设备干两段活
        // 在现场是两次进退场，装箱不假装能无缝拼起来。
        var segs = BlastWindow.Subtract(ws, we, blasts ?? Array.Empty<BlastWindow>());
        if (segs.Count == 0) return (ws, ws);

        var best = segs[0];
        foreach (var s in segs) if (s.End - s.Start > best.End - best.Start) best = s;
        return (best.Start, best.End);
    }

    /// <summary>该设备当天各班合计的可用工时（逐日能力日历按它加权）。</summary>
    public static double DayHours(
        IReadOnlyList<ShiftWindow> shifts, string? equipId,
        IReadOnlyList<MaintenanceHourWindow>? maintenance,
        IReadOnlyList<BlastWindow>? blasts,
        double handoverH)
    {
        double sum = 0;
        if (shifts == null) return 0;
        foreach (var sh in shifts)
        {
            var (ws, we) = Of(sh, equipId, maintenance, blasts, handoverH);
            double h = we - ws;
            // 半小时以下的碎片按干不了算 —— 与装箱侧 avail >= 0.5 的门槛保持一致，
            // 否则日历说"这天还有 0.2 小时能力"，装箱那边却一条任务都排不出来。
            if (h >= 0.5) sum += h;
        }
        return sum;
    }

    /// <summary>把某日的检修台账行整批转成小时制时窗（时刻非法的**丢弃**，同引擎口径）。</summary>
    public static List<MaintenanceHourWindow> HourWindowsOf(IEnumerable<MaintenanceWindowRow>? rows)
    {
        var list = new List<MaintenanceHourWindow>();
        if (rows == null) return list;
        foreach (var r in rows)
        {
            var w = MaintenanceHourWindow.From(r);
            if (w != null) list.Add(w);
        }
        return list;
    }
}
