using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「进度计划方案出图」六张图的**取数与标度**（从原 <c>PlanLib.LongTerm.LongTermCharts</c> 里
/// 切出来的非绘图部分）。
///
/// <b>为什么单切一层</b>：原版这些计算（累计 NPV、跨方案归一、纵轴上限）写在画布绘制方法里，
/// 图画歪了很难分清是"数错了"还是"画错了"。切成纯函数后数值这一半可以脱 GUI 验收，
/// 绘制那一半只剩坐标变换。
///
/// 六张图（与原版一一对应）：
///   ① 逐年产量 + 达产线　② 生产剥采比削峰曲线 SR(t) + n经线　③ 累计 NPV 曲线
///   ④ 生产时相甘特　⑤ 多指标综合雷达　⑥ 单方案进度图（出图用）
/// </summary>
public static class LongTermChartMath
{
    /// <summary>纵轴留白：曲线不要顶到画布边上。原版 ①② 用 1.1、⑥ 用 1.15。</summary>
    public const double HeadroomCurve = 1.1;
    public const double HeadroomSheet = 1.15;

    /// <summary>累计折现现金流（逐期把 <c>NpvWan</c> 累加）。</summary>
    public static List<double> Accumulate(LongTermPlan? p)
    {
        var list = new List<double>();
        if (p == null) return list;
        double cum = 0;
        foreach (var z in p.Periods) { cum += z.NpvWan; list.Add(cum); }
        return list;
    }

    /// <summary>各方案里最长的期数（横轴格数）；一个都没有时给 1，免得除零。</summary>
    public static int MaxPeriodCount(IReadOnlyList<LongTermPlan>? s)
        => s == null || s.Count == 0 ? 1 : Math.Max(1, s.Max(p => p.Periods.Count));

    /// <summary>
    /// ① 产量图纵轴上限 = max(设计能力, 各期采出) × 留白。
    /// <b>达产线必须在轴内</b> —— 只按实际产量定上限的话，某方案压根没达产时那条线会跑到画布外，
    /// 看图的人就看不出"差多少"。
    /// </summary>
    public static double OutputAxisMax(IReadOnlyList<LongTermPlan> s)
    {
        double ap = s.Count == 0 ? 0 : s.Max(p => p.DesignCapacityWanTa);
        double top = s.SelectMany(p => p.Periods).Select(z => z.CoalWanT).DefaultIfEmpty(1).Max();
        return Math.Max(Math.Max(ap, top), 1e-9) * HeadroomCurve;
    }

    /// <summary>② 剥采比图纵轴上限 = max(经济合理剥采比 n经, 各期生产剥采比) × 留白（理由同上）。</summary>
    public static double RatioAxisMax(IReadOnlyList<LongTermPlan> s)
    {
        double nEco = s.Count == 0 ? 0 : s.Max(p => p.EconomicStripRatioMax);
        double top = s.SelectMany(p => p.Periods).Select(z => z.Ratio).DefaultIfEmpty(1).Max();
        return Math.Max(Math.Max(nEco, top), 1e-9) * HeadroomCurve;
    }

    /// <summary>
    /// ③ 累计 NPV 的取值范围。<b>下限要把 0 算进去</b> —— 基建期累计现金流是负的，
    /// 不含 0 的话零线画不出来，"什么时候转正"这件最要紧的事就看不见了。
    /// </summary>
    public static (double Min, double Max) NpvRange(IReadOnlyList<LongTermPlan> s)
    {
        var all = s.Select(Accumulate).SelectMany(x => x).ToList();
        double mx = Math.Max(1, all.DefaultIfEmpty(1).Max());
        double mn = Math.Min(0, all.DefaultIfEmpty(0).Min());
        return (mn, mx);
    }

    /// <summary>雷达六个轴的名字（与原版同序）。</summary>
    public static readonly string[] RadarAxes = { "削峰", "达产", "内排", "均衡", "NPV", "稳产" };

    /// <summary>
    /// ⑤ 雷达：跨方案把六项归一到 0..1。
    /// <b>削峰与达产是"越小越好"</b>（峰值剥采比越低越好、达产用时越短越好），故反向归一；
    /// 其余四项正向。只有一个方案（或全相等）时一律给 0.5 —— 归一化在那种情形下没有意义，
    /// 给 0 或 1 都会让人误以为"特别差/特别好"。
    /// </summary>
    public static double[] RadarValues(IReadOnlyList<LongTermPlan> withResult, int index)
    {
        var rs = withResult.Select(p => p.Result!).ToList();
        double[] peak = rs.Select(x => x.ProductionRatioPeak).ToArray();
        double[] ttc = rs.Select(x => x.TimeToCapacityYears).ToArray();
        double[] inn = rs.Select(x => x.InnerDumpPct).ToArray();
        double[] bal = rs.Select(x => x.ReserveBalanceCoef).ToArray();
        double[] npv = rs.Select(x => x.Npv).ToArray();
        double[] plt = rs.Select(x => x.StablePlateauYears).ToArray();
        return new[]
        {
            NormLow(peak, index), NormLow(ttc, index), NormHigh(inn, index),
            NormHigh(bal, index), NormHigh(npv, index), NormHigh(plt, index),
        };
    }

    /// <summary>越大越好 → 0..1。</summary>
    public static double NormHigh(double[] a, int i)
    {
        if (a.Length == 0 || i < 0 || i >= a.Length) return 0.5;
        double mn = a.Min(), mx = a.Max();
        return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5;
    }

    /// <summary>越小越好 → 0..1。</summary>
    public static double NormLow(double[] a, int i)
    {
        if (a.Length == 0 || i < 0 || i >= a.Length) return 0.5;
        double mn = a.Min(), mx = a.Max();
        return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5;
    }

    /// <summary>年标稀疏步长：期数多时不要把横轴标签挤成一团（原版口径：至多约 12 个标签）。</summary>
    public static int LabelStep(int periodCount) => Math.Max(1, periodCount / 12);

    /// <summary>方案名截短（图例放得下）。</summary>
    public static string ShortName(string? name, int max = 8)
    {
        string s = (name ?? "").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>时相配色（基建灰 / 爬坡蓝 / 稳产 teal / 减产 amber），返回打包 RGB。</summary>
    public static uint PhaseRgb(PlanPhase ph) => ph switch
    {
        PlanPhase.Basic => 0x9A9A9A,
        PlanPhase.RampUp => 0x378ADD,
        PlanPhase.Stable => 0x1D9E75,
        PlanPhase.Decline => 0xBA7517,
        _ => 0xAAAAAA,
    };

    /// <summary>方案配色（六色循环，与原版同序）。</summary>
    public static readonly uint[] Palette =
        { 0x378ADD, 0x1D9E75, 0xD85A30, 0xBA7517, 0x993556, 0x6D4AC4 };

    public static uint SchemeRgb(int i) => Palette[((i % Palette.Length) + Palette.Length) % Palette.Length];

    /// <summary>时相中文名（图例/表头用）。</summary>
    public static string PhaseLabel(PlanPhase ph) => ph switch
    {
        PlanPhase.Basic => "基建",
        PlanPhase.RampUp => "爬坡",
        PlanPhase.Stable => "稳产",
        PlanPhase.Decline => "减产",
        _ => "其它",
    };
}
