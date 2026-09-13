// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/RollingReplan.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>
/// 已完成月份的<b>实绩</b>。滚动重排的期初状态就是它。
/// <para><b>实绩不是"上期计划"</b> —— 现场从来不会严格照计划走。拿计划当期初重排，
/// 偏差会一期一期累积下去，而且每次重排都显示"一切正常"。</para>
/// </summary>
public sealed class ActualToDate
{
    /// <summary>已完成的月数 k（重排从第 k+1 月起）。</summary>
    public int MonthsElapsed;

    /// <summary>累计<b>实际</b>采出（万t）。</summary>
    public double CoalWt;
    /// <summary>累计<b>实际</b>剥离（m³ 实方）。</summary>
    public double RockM3;

    /// <summary>
    /// 各标高格的<b>实际</b>位置（u，与 <c>Rock.Levels()</c> 同序）。
    /// <para>最好从<b>现状面反算</b>；退而求其次用上期计划的对应月份，但那样就带上了"计划=实际"的假设，
    /// 必须标出来（<see cref="RollingReplan.Prepare"/> 会记一条）。</para>
    /// </summary>
    public double[] BenchX = Array.Empty<double>();

    /// <summary>期初已有的备采煤量（万t）。留空 = 让引擎从几何重算。</summary>
    public double PreparedWt;
}

/// <summary>滚动重排的一次结果：新计划 + 与上期计划的偏差账。</summary>
public sealed class ReplanResult
{
    public bool   Success;
    public string Error = "";
    public MonthlyScheduleResult? Plan;

    /// <summary>已完成月数 k。新计划的第 1 个月 = 原计划的第 k+1 个月。</summary>
    public int MonthsElapsed;

    // ── 达成度（实际 vs 上期计划的同期累计）──────────────────────────────
    public double PlannedCoalWt, ActualCoalWt;
    public double PlannedRockM3, ActualRockM3;
    public double CoalAttainPct => PlannedCoalWt > 1e-9 ? ActualCoalWt / PlannedCoalWt * 100 : 0;
    public double RockAttainPct => PlannedRockM3 > 1e-9 ? ActualRockM3 / PlannedRockM3 * 100 : 0;
    /// <summary>剥离欠账（m³ 实方，正=欠剥）。<b>欠剥比欠采危险</b>：欠采是这个月少卖点煤，欠剥是下个月没煤可采。</summary>
    public double StripDebtM3 => Math.Max(0, PlannedRockM3 - ActualRockM3);

    public readonly List<string> Notes = new();

    public string Summary()
        => !Success ? "滚动重排失败：" + Error
         : $"已完成 {MonthsElapsed} 月 · 采出达成 {CoalAttainPct:0.0}%（{ActualCoalWt:0.0}/{PlannedCoalWt:0.0}万t）"
         + $" · 剥离达成 {RockAttainPct:0.0}%（{ActualRockM3 / 1e4:0.0}/{PlannedRockM3 / 1e4:0.0}万m³）"
         + (StripDebtM3 > 1e-6 ? $" · ◆剥离欠账 {StripDebtM3 / 1e4:0.0}万m³" : "")
         + $" → 重排 {Plan?.Months.Count ?? 0} 个月";
}

/// <summary>
/// 「滚动重排」—— 拿<b>实绩</b>当期初，把剩下的月份重新排一遍。
///
/// <para><b>为什么必须有</b>：短期计划在现场是滚动的。你在第 5 个月，1–4 月已经是既成事实，
/// 要重排的是 5–16 月。一次性排满 12 个月然后照着走，只在教科书里成立。
/// 对标 `采运排一体化` 的「实绩录入 → 达成度评价 → 回灌，必要时一键再平衡」。</para>
///
/// <para><b>它不新增算法</b>：期初姿态用 <see cref="MonthlyScheduleInput.InitialBenchX"/>、
/// 累计煤用 <see cref="MonthlyScheduleInput.CoalStartWt"/> —— 零件早就有了，
/// 这里只是把「实绩 → 期初状态」这一跳做对，并把<b>偏差账</b>算出来。</para>
///
/// <para><b>偏差账为什么重要</b>：欠采是这个月少卖点煤；<b>欠剥是下个月没煤可采</b>。
/// 两者绝不能合成一个"完成率"看。</para>
/// </summary>
public static class RollingReplan
{
    /// <summary>
    /// 由「上期计划 + 实绩」造出下一次排产的输入，并给出偏差账。
    /// </summary>
    /// <param name="previous">上一期的完整计划（用来算同期应完成量）。可为 null（首次排产没有上期）。</param>
    /// <param name="actual">实绩。<see cref="ActualToDate.MonthsElapsed"/> 决定从第几个月起重排。</param>
    /// <param name="remainingTargets">剩余月份的煤量目标（万t）。空 = 沿用上期计划里剩下那几个月的目标。</param>
    public static (MonthlyScheduleInput Input, ReplanResult Report) Prepare(
        MonthlyScheduleInput baseInput, MonthlyScheduleResult? previous, ActualToDate actual,
        double[]? remainingTargets = null)
    {
        var rep = new ReplanResult { MonthsElapsed = Math.Max(0, actual?.MonthsElapsed ?? 0) };
        var inp = Clone(baseInput);
        if (actual == null) { rep.Notes.Add("没有实绩，按首次排产处理"); return (inp, rep); }
        int k = rep.MonthsElapsed;

        // ── 剩余月份的目标 ────────────────────────────────────────────────
        if (remainingTargets is { Length: > 0 }) inp.CoalTargetWt = (double[])remainingTargets.Clone();
        else if (baseInput.CoalTargetWt.Length > k)
            inp.CoalTargetWt = baseInput.CoalTargetWt.Skip(k).ToArray();
        else
        {
            rep.Error = $"已完成 {k} 个月，但计划只有 {baseInput.CoalTargetWt.Length} 个月 —— 没有可重排的月份";
            return (inp, rep);
        }
        if (baseInput.StripCapM3 is { Length: > 0 })
            inp.StripCapM3 = baseInput.StripCapM3.Length > k
                ? baseInput.StripCapM3.Skip(k).ToArray() : Array.Empty<double>();
        inp.ExtraCumCapM3 = Array.Empty<double>();      // 累计上限是按原月轴算的，重排后失效，得由外循环重给
        if (baseInput.ExtraCumCapM3 is { Length: > 0 })
            rep.Notes.Add("⚠ 上期的「外部累计上限」（排土容量）按原月轴算，重排后已清空 —— 需重跑外循环重给");

        // ── 期初状态 = 实绩 ──────────────────────────────────────────────
        inp.CoalStartWt = actual.CoalWt;
        if (actual.BenchX is { Length: > 0 })
        {
            inp.InitialBenchX = (double[])actual.BenchX.Clone();
            inp.StartInSteadyState = false;             // 有真位置就不用稳态推了
        }
        else
        {
            rep.Notes.Add("⚠ 实绩没给台阶位置 —— 退回稳态推算。**这等于假设前 " + k + " 个月完全照计划走**，"
                        + "偏差会被吞掉。最好从现状面反算真位置。");
            inp.StartInSteadyState = true;
        }

        // ── 偏差账 ───────────────────────────────────────────────────────
        rep.ActualCoalWt = actual.CoalWt;
        rep.ActualRockM3 = actual.RockM3;
        if (previous is { Success: true } && previous.Months.Count >= k && k > 0)
        {
            rep.PlannedCoalWt = previous.Months[k - 1].CoalCumWt;
            rep.PlannedRockM3 = previous.Months[k - 1].RockCumM3;
            if (rep.StripDebtM3 > 1e-6)
                rep.Notes.Add($"◆ 剥离欠账 {rep.StripDebtM3 / 1e4:0.0}万m³ —— "
                            + "**欠剥比欠采危险**：欠采是这个月少卖点煤，欠剥是下个月没煤可采。"
                            + "重排会把它压进后续月份，注意看能力还够不够。");
            if (rep.CoalAttainPct < 95 || rep.CoalAttainPct > 105)
                rep.Notes.Add($"⚠ 采出达成 {rep.CoalAttainPct:0.0}% —— 偏离超过 5%，"
                            + "先确认是产量问题还是实绩口径问题，再重排");
        }
        else if (k > 0)
            rep.Notes.Add("没有上期计划可比 —— 只能重排，给不出达成度");

        return (inp, rep);
    }

    /// <summary>准备 + 求解一步到位。</summary>
    public static ReplanResult Run(MonthlyScheduleInput baseInput, MonthlyScheduleResult? previous,
                                   ActualToDate actual, double[]? remainingTargets = null)
    {
        var (inp, rep) = Prepare(baseInput, previous, actual, remainingTargets);
        if (!string.IsNullOrEmpty(rep.Error)) return rep;
        var plan = MonthlyMineScheduler.Solve(inp);
        rep.Plan = plan;
        rep.Success = plan.Success;
        if (!plan.Success) rep.Error = plan.Error;
        foreach (var n in plan.Notes) rep.Notes.Add(n);
        return rep;
    }

    /// <summary>
    /// 从一份已有计划<b>模拟</b>实绩（照计划走、按给定比例打折）。
    /// 台架与"如果这样会怎样"的推演用；<b>不是</b>生产路径 —— 生产要用现状面反算的真位置。
    ///
    /// <para><b>造出来的实绩必须自洽</b>：<paramref name="rockRate"/> &lt; 1 时台阶位置会
    /// 按同一比例退回去（推进距离 × rockRate）。少剥了岩，台阶就不可能推到位 ——
    /// 位置不退的话欠账在几何上根本不存在，重排也就不会补，而账面数字照样显示欠了多少。</para>
    ///
    /// <para>退回幅度是<b>线性近似</b>（体积对推进距离不是线性的），方向和量级对，
    /// 不要拿它当精确反算。生产路径用现状面反算真位置。</para>
    /// </summary>
    public static ActualToDate SimulateActual(MonthlyScheduleResult plan, int monthsElapsed,
                                              double coalRate = 1.0, double rockRate = 1.0)
    {
        int k = Math.Clamp(monthsElapsed, 0, plan.Months.Count);
        if (k == 0) return new ActualToDate();
        var m = plan.Months[k - 1];

        // ⚠ 台阶位置必须跟着 rockRate 退回去。
        // 原来这里直接照抄计划位置 ⇒ 造出一个**物理上不可能**的实绩：
        // 「只剥了 70% 的岩，可台阶偏偏停在剥满 100% 才到得了的地方」。
        // 后果不是报错，是**欠账凭空消失** —— 几何上活已经干完了，重排自然不必补。
        // 账面 `StripDebtM3` 还是 17.8万m³，两个数各说各的，而每一项校核都过。
        // （G37b 当初是在【判据里】手工把位置改回去绕开的 —— 那等于只有那一条判据是对的，
        //   别的调用方拿到的全是这个坏状态。修在这里。）
        var bx = (double[])m.BenchX.Clone();
        if (rockRate < 1.0 - 1e-9 && bx.Length > 0)
        {
            // 基准：期初那一排；拿不到就退回上一个月的位置（比不退好，但幅度偏小，标出来）
            var from = plan.InitialBenchX is { Length: > 0 } && plan.InitialBenchX.Length == bx.Length
                     ? plan.InitialBenchX
                     : (k >= 2 && plan.Months[k - 2].BenchX.Length == bx.Length ? plan.Months[k - 2].BenchX : null);
            if (from != null)
                for (int i = 0; i < bx.Length; i++)
                    bx[i] = from[i] + (bx[i] - from[i]) * Math.Max(0, rockRate);
        }

        return new ActualToDate
        {
            MonthsElapsed = k,
            CoalWt = m.CoalCumWt * coalRate,
            RockM3 = m.RockCumM3 * rockRate,
            BenchX = bx,
            PreparedWt = m.PreparedWt,
        };
    }

    /// <summary>判据入口（G18j 用反射逐字段比）。</summary>
    public static MonthlyScheduleInput CloneForTest(MonthlyScheduleInput s) => Clone(s);

    private static MonthlyScheduleInput Clone(MonthlyScheduleInput s) => new()
    {
        Rock = s.Rock, AlphaDeg = s.AlphaDeg, ZDatum = s.ZDatum,
        CoalTargetWt = (double[])s.CoalTargetWt.Clone(),
        // 深拷：浅引用的话克隆件与来源**共用同一批行**，改一边另一边跟着变
        CoalTargetBySeam = s.CoalTargetBySeam?.Select(a => (double[])(a ?? Array.Empty<double>()).Clone())
                                              .ToArray() ?? Array.Empty<double[]>(),
        StripCapM3 = (double[])(s.StripCapM3 ?? Array.Empty<double>()).Clone(),
        ExtraCumCapM3 = (double[])(s.ExtraCumCapM3 ?? Array.Empty<double>()).Clone(),
        LookaheadMonths = s.LookaheadMonths, RecoveryTotalWt = s.RecoveryTotalWt,
        RatioCeiling = s.RatioCeiling, CoalStartWt = s.CoalStartWt,
        StartInSteadyState = s.StartInSteadyState,
        InitialBenchX = (double[])(s.InitialBenchX ?? Array.Empty<double>()).Clone(),
        Pace = s.Pace,
    };

    /// <summary>达成度 + 重排表（命令行/报表直接打）。</summary>
    public static string Report(ReplanResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var n in r.Notes) sb.AppendLine("  " + n);
        if (r.Success && r.Plan != null) sb.Append(MonthlyMineScheduler.Report(r.Plan));
        return sb.ToString();
    }
}
