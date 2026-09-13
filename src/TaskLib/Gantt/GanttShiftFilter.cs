// 忠实移植自原 PitMine3D Modules/TaskLib/Gantt/GanttShiftFilter.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Gantt;

// ─────────────────────────────────────────────────────────────────────────────
//  日甘特的班次口径。
//
//  ══ 这个窗为什么和别的八个不一样 ══
//  `ShiftSelector` 的规矩是**默认落当前班、不落「全部」**——因为任务书、派车单
//  这类单据本身就是一班一份，默认全天会印出跨班的单子。
//  但**日甘特是全天视图**：它的整张图就是 0–24 点那条时间轴，
//  默认只显示一个班等于开窗就藏掉三分之二的天，而画面上完全看不出被藏了。
//  ⇒ 这个窗**默认全天**，这是一条写明的例外，不是忘了跟规矩。
//
//  ══ 真正的坑：Shift 为空的任务 ══
//  盘子里总有一批任务的 `Shift` 是空的（排产没落班、跨班连续作业、临时补的活）。
//  按 `t.Shift == 选中班` 直筛，它们**一条都不会出现在任何一个班里** ——
//  切到早班没有、切到中班也没有、切到夜班还是没有，而「全天」时它们又在。
//  没有任何东西会报错，图上只是少了几根条。
//  ⇒ 筛掉多少、其中多少是「没有班次归属」，必须**分开报出来**。
//
//  ══ 一条纪律 ══
//  **条形与表头统计必须同源**：筛了条形就得筛统计。
//  两边各取各的话，图上是一个班、上面的「计划 xx 万m³ / 达成度 xx%」是全天，
//  而这两个数并排摆着，读的人只会以为这一班干了全天的量。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>日甘特按班次筛任务。纯函数，永不抛。</summary>
public static class GanttShiftFilter
{
    /// <summary>一次筛选的结果。</summary>
    public sealed class Result
    {
        /// <summary>筛完的任务（<b>甘特与表头统计都用这一份</b>）。</summary>
        public List<ProductionTask> Tasks = new();
        /// <summary>真的筛了（false = 全天，原样）。</summary>
        public bool Filtered;
        /// <summary>被筛掉的、有班次归属的条数。</summary>
        public int DroppedOtherShift;
        /// <summary>被筛掉的、<b>没有班次归属</b>的条数 —— 这些任务哪个班都进不去。</summary>
        public int DroppedNoShift;
        /// <summary>界面直接显示的一句话。</summary>
        public string Caption = "";
    }

    /// <summary>
    /// 按班次筛。
    /// </summary>
    /// <param name="tasks">当日全部任务。</param>
    /// <param name="shift">
    /// 班次名；空串或 <see cref="ShiftScope.All"/> = 全天不筛
    /// （<see cref="TaskLib.Features.ShiftSelector.Filter"/> 已把「全部」转成空串）。
    /// </param>
    public static Result Apply(IReadOnlyList<ProductionTask>? tasks, string? shift)
    {
        var all = (tasks ?? Array.Empty<ProductionTask>()).Where(t => t != null).ToList();
        var r = new Result();

        string want = (shift ?? "").Trim();
        if (want.Length == 0 || want == ShiftScope.All)
        {
            r.Tasks = all;
            r.Caption = $"全天 · {all.Count} 项";
            return r;
        }

        r.Filtered = true;
        foreach (var t in all)
        {
            string s = (t.Shift ?? "").Trim();
            if (s == want) r.Tasks.Add(t);
            else if (s.Length == 0) r.DroppedNoShift++;
            else r.DroppedOtherShift++;
        }

        r.Caption = Describe(r, want, all.Count);
        return r;
    }

    private static string Describe(Result r, string want, int total)
    {
        string s = $"只看 {want}：{r.Tasks.Count} 项";

        if (r.Tasks.Count == 0)
            // 空甘特看着就像"今天没活"。必须说是筛出来的空，不是本来就空。
            s += $"　◆ **这一班一条任务都没有**（全天共 {total} 项，都不在这一班）"
               + "—— 图是空的，但这不是「今天没排活」。";

        if (r.DroppedOtherShift > 0) s += $"　· 其余班 {r.DroppedOtherShift} 项已隐藏";

        // ★ 这一条是这个类存在的理由：Shift 为空的任务**哪个班都进不去**。
        //   切到早班没有、中班没有、夜班还是没有，只有「全天」才看得见 ——
        //   而按班次直筛的写法把它们悄悄丢了，一句话都没有。
        if (r.DroppedNoShift > 0)
            s += $"　⚠ 另有 **{r.DroppedNoShift} 项没有班次归属**，"
               + "它们**哪个班都进不去**（切到别的班同样看不到），只有切回「全天」才看得见。"
               + "要么是排产没落班，要么是跨班连续作业。";

        s += "　（表头的计划/实绩/达成度已按同一把尺子重算）";
        return s;
    }
}
