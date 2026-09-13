// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/WorkWindowCalc.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  「这台设备这个班到底能干几个小时」—— 唯一的算法。
//
//  有效时窗 = 班时窗 − 检修档期 − 爆破清场 − 非首班交接损失（− 滚动重排起点）。
//
//  抽出来是因为它现在有**两个**调用场景，口径必须完全一致：
//    ① 装箱（TaskExploder）：算作业日当天各班能装多少；
//    ② 逐日能力日历（DayCapacityCalendar）：算整月每一天的可用工时，
//       月计划按它加权摊到各天 —— 有检修的那天少排、有炮的那天少排。
//  两边各写一份的话，"计划里那天排了 8 小时、实际那天在定修"这种错就会从缝里漏出去，
//  而且不会有任何报错。同一条口径只许有一处实现（这一轮已经因为 shift-of-hour
//  各写四份吃过亏，见 ShiftScope）。
// ─────────────────────────────────────────────────────────────────────────────
public static class WorkWindowCalc
{
    /// <summary>
    /// 某设备在某个班的有效时窗 <c>[ws, we)</c>。返回 <c>we == ws</c> 表示这个班没有可用时间。
    /// </summary>
    /// <param name="fromHour">滚动重排起点：只排此刻之后。0 = 不限。</param>
    public static (double Ws, double We) Of(
        ShiftWindow sh, string? equipId,
        IReadOnlyList<MaintenanceWindow>? maintenance,
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
        // 在现场是两次进退场，装箱不假装能无缝拼起来（放弃的小时数由 CheckBlastSegmentation 报账）。
        var segs = BlastWindow.Subtract(ws, we, blasts ?? Array.Empty<BlastWindow>());
        if (segs.Count == 0) return (ws, ws);

        var best = segs[0];
        foreach (var s in segs) if (s.End - s.Start > best.End - best.Start) best = s;
        return (best.Start, best.End);
    }

    /// <summary>该设备当天三班合计的可用工时（逐日能力日历按它加权）。</summary>
    public static double DayHours(
        IReadOnlyList<ShiftWindow> shifts, string? equipId,
        IReadOnlyList<MaintenanceWindow>? maintenance,
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
}
