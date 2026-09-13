// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ShiftScope.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  班次口径的唯一出口 —— 日常生产组织的每一件事都是按班组织的。
//
//  任务书按班出、下达按班签、派车按班展、派工按班配、实绩按班录、看板按班盯。
//  可原先「有哪几个班」在 8 个窗口的 XAML 里各硬编码了一遍
//  （<ComboBoxItem>早班</ComboBoxItem>…），「此刻是第几班」又在 EquipStatusWindow 里
//  私藏了一个把 8 / 16 写死的 ShiftOf(hour)。于是三件事同时成立：
//
//    · 班次日历改成四班制、或只是把交接时刻从 8:00 挪到 7:30，界面全然不知；
//    · 任务的 Shift 来自盘子（cfg.Shifts），下拉框却来自 XAML —— 两边对不上时
//      筛选结果恒空，而界面看着完全正常（同 [[always-firing-warning-is-a-dead-path]]）；
//    · 打开窗口默认落「全部」或写死的「中班」，夜班的人每次都得先自己切一下。
//
//  本类把它收成一处：班次清单、当前班、某时刻属于哪个班、某个班的时窗——全部取自当日盘子。
//  盘子不可用时回落 早/中/夜（8h 一班）并如实标注，不假装读到了台账。
//
//  跨零点的班（如 22:00→06:00）按回绕判，不要求 Start < End。
// ─────────────────────────────────────────────────────────────────────────────
public static class ShiftScope
{
    /// <summary>下拉框里「不分班」那一项的文案。各窗口一律用它判"要不要筛"，别各写各的字符串。</summary>
    public const string All = "全部";

    /// <summary>盘子不可用时的兜底班制（与 SampleTaskBoard 的种子班次同口径）。</summary>
    private static readonly ShiftWindow[] Fallback =
    {
        new ShiftWindow("早班", 0, 8),
        new ShiftWindow("中班", 8, 16),
        new ShiftWindow("夜班", 16, 24),
    };

    // ── 测试挂钩 ──────────────────────────────────────────────────────────────
    //  样例盘子只给得出「早/中/夜 各 8h」这一种班制，而本类存在的理由恰恰是
    //  **班制不是固定的**：四班三运转、跨零点的夜班、没排满全天的班制，判据都得覆盖到。
    //  同 CompileOverrides.SetForTest 的路数。
    private static IReadOnlyList<ShiftWindow>? _testWindows;
    private static double? _testNow;

    /// <summary>仅供单测：注入班制与「此刻」；两个都传 null 即还原为真实盘子。</summary>
    internal static void SetForTest(IReadOnlyList<ShiftWindow>? windows, double? nowHour = null)
    {
        _testWindows = windows;
        _testNow = nowHour;
    }

    /// <summary>当日班次时窗（来自装配好的盘子；读不到回落三班）。</summary>
    public static IReadOnlyList<ShiftWindow> Windows
    {
        get
        {
            if (_testWindows is { Count: > 0 }) return _testWindows;
            try
            {
                var s = ProductionPlanContext.Shifts;
                if (s is { Count: > 0 }) return s;
            }
            catch { /* 盘子装配不出来（无库/无期次）→ 回落，不抛：下拉框不能因此变空 */ }
            return Fallback;
        }
    }

    /// <summary>盘子里真有班次时窗吗（false = 正在用兜底三班，供来源文案如实标注）。</summary>
    public static bool FromPlate
    {
        get
        {
            try { return ProductionPlanContext.Shifts is { Count: > 0 }; }
            catch { return false; }
        }
    }

    /// <summary>当日班次名（下拉框的取值域）。</summary>
    public static IReadOnlyList<string> Names => Windows.Select(w => w.Name).ToList();

    /// <summary>某个班的时窗；名字对不上返回 null（调用方据此决定是"不筛"还是"报错"，不猜）。</summary>
    public static ShiftWindow? Window(string? shift)
    {
        string s = (shift ?? "").Trim();
        if (s.Length == 0 || s == All) return null;
        return Windows.FirstOrDefault(w => string.Equals(w.Name, s, StringComparison.Ordinal));
    }

    /// <summary>
    /// <paramref name="hour"/>（0..24）落在哪个班。
    /// 一个都不落时（班制没排满全天，比如只排了两个班）取<b>起点最近的前一个班</b>，
    /// 返回空串会让调用方拿空字符串去筛，静默筛出 0 条。
    /// </summary>
    public static string ShiftOf(double hour) => ShiftOf(Windows, hour);

    /// <summary>
    /// 同上，但对**指定的一份班制**判（装箱时手里拿着的是 <c>cfg.Shifts</c>，不是环境里的那份）。
    /// <para>
    /// 引擎侧原先自带一句 <c>cfg.Shifts.FirstOrDefault(s =&gt; hour &gt;= s.Start &amp;&amp; hour &lt; s.End)?.Name ?? ""</c>：
    /// 跨零点的夜班（23→6）永远匹配不上，于是穿孔/检修任务的 <c>Shift</c> 落成<b>空串</b>，
    /// 之后每一个按班筛选的窗口里它们都消失了 —— 而计划里明明排着。
    /// </para>
    /// </summary>
    public static string ShiftOf(IReadOnlyList<ShiftWindow> windows, double hour)
    {
        var all = windows is { Count: > 0 } ? windows : Fallback;
        double h = Wrap(hour);
        foreach (var w in all)
            if (Contains(w, h)) return w.Name;

        // 没排满全天：归到"起点在此刻之前且最近"的那个班，全都在之后就归最后一个班（昨夜跨过来的）
        // —— 绝不返回空串：空串会被当成"不筛"或"没有班"，两种都会让这条任务在界面上消失。
        ShiftWindow? best = null;
        double bestGap = double.MaxValue;
        foreach (var w in all)
        {
            double gap = h - Wrap(w.Start);
            if (gap < 0) gap += 24;
            if (gap < bestGap) { bestGap = gap; best = w; }
        }
        return best?.Name ?? "";
    }

    /// <summary>此刻所在的班。</summary>
    public static string Current => ShiftOf(SafeNow());

    /// <summary>
    /// 某个班的「锚点时刻」—— 界面上一切"进度 / 此刻"类计算的基准。
    /// <list type="bullet">
    /// <item>当前班（或「全部」）→ 真实此刻；</item>
    /// <item>已经过去的班 → 该班<b>结束</b>时刻；</item>
    /// <item>还没到的班 → 该班<b>开始</b>时刻。</item>
    /// </list>
    /// 没有这条，看板切到夜班时会拿上午十点去算夜班进度，整班显示成 0%，看着像谁都没干活。
    /// </summary>
    public static double AnchorHour(string? shift)
    {
        double now = SafeNow();
        var w = Window(shift);
        if (w == null) return now;                       // 「全部」/ 认不出 → 真实此刻
        if (string.Equals(w.Name, ShiftOf(now), StringComparison.Ordinal)) return now;

        // 与此刻的先后：比"班起点距此刻还有多久"，跨零点也成立
        double toStart = Wrap(w.Start) - Wrap(now);
        if (toStart < 0) toStart += 24;
        return toStart > 0 && toStart < 12 ? w.Start : w.End;
    }

    /// <summary>时刻是否落在该班时窗内（跨零点按回绕判）。</summary>
    public static bool Contains(ShiftWindow w, double hour)
    {
        double h = Wrap(hour), s = Wrap(w.Start), e = w.End;
        if (e >= 24 && s < 24 && Math.Abs(e - 24) < 1e-9) return h >= s;   // [s,24) 写成 [s,24]
        e = Wrap(e);
        return s <= e ? h >= s && h < e : h >= s || h < e;                 // 后者 = 跨零点
    }

    /// <summary>
    /// [from,to) 与该班时窗的重叠小时数（按班汇总故障工时用）。
    /// 班次为空/「全部」时不裁，原样返回区间长度——"不筛"不等于"筛不到"。
    /// </summary>
    public static double OverlapHours(string? shift, double from, double to)
    {
        double span = Math.Max(0, to - from);
        var w = Window(shift);
        if (w == null) return span;
        double s = w.Start, e = w.End <= w.Start ? w.End + 24 : w.End;     // 跨零点摊平到 [s, s+24)
        return Math.Max(0, Math.Min(e, to) - Math.Max(s, from));
    }

    /// <summary>"中班 08:00–16:00" —— 抬头写清当前看的是哪个班的哪段时间。</summary>
    public static string Caption(string? shift)
    {
        var w = Window(shift);
        if (w == null) return "全天";
        return $"{w.Name} {Domain.DispatchClock.Hm(w.Start)}–{Domain.DispatchClock.Hm(w.End)}";
    }

    private static double SafeNow()
    {
        if (_testNow.HasValue) return _testNow.Value;
        try { return ProductionPlanContext.NowHour; }
        catch { return 0; }
    }

    private static double Wrap(double h)
    {
        if (double.IsNaN(h) || double.IsInfinity(h)) return 0;
        h %= 24;
        return h < 0 ? h + 24 : h;
    }
}
