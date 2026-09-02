using System;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;   // ProcessType

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>
/// 上承短期(月度)生产计划 → 日目标：月采出/月剥离 ÷ 有效作业日 × 作业面份额 = 各面「当日目标」喂 TaskExploder。
/// 忠实移植原 TaskLib.Engine.ShortTermLink 的分配核(原经反射软读 PlanLib 月度方案; 此取 <see cref="MonthInfo"/> 入参,
/// 由调用方从 Kylin 短期计划/monthly_plan 提供)。纯逻辑、可单测。
/// </summary>
public static class ShortTermLink
{
    /// <summary>当月计划目标(供日常实绩向上对账)。</summary>
    public sealed class MonthInfo
    {
        public bool HasPlan;
        public string PlanName = "";
        public string MonthLabel = "";
        public double CoalWanT;
        public double StripWanM3;
        public double Workdays = 25;
        public double Density = 1.4;
        /// <summary>月度采剥总材料(万m³) = 采出折m³ + 剥离。</summary>
        public double MaterialWanM3 => Density > 0.1 ? CoalWanT / Density + StripWanM3 : StripWanM3;
    }

    /// <summary>日采出(m³/日) = 月采出(万t)·1e4 / 煤密度 / 作业日。</summary>
    public static double DailyCoalM3(double coalWanT, double workdays, double density)
        => coalWanT * 1e4 / Math.Max(0.1, density) / Math.Max(1, workdays);

    /// <summary>日剥离(m³/日) = 月剥离(万m³)·1e4 / 作业日。</summary>
    public static double DailyRockM3(double stripWanM3, double workdays)
        => stripWanM3 * 1e4 / Math.Max(1, workdays);

    /// <summary>
    /// 按月计划改写 cfg 各面 DayTargetM3(忠实原 ApplyToConfig): 日采出按份额分配到采装面(loadShares
    /// 缺省/长度不符则均分), 日剥离赋各排土面。返回来源文案。month.HasPlan=false → 不改, 返回兜底文案。
    /// </summary>
    public static string ApplyToConfig(ExploderConfig cfg, MonthInfo month, double[]? loadShares = null)
    {
        if (!month.HasPlan) return "（未确定短期月度方案，用样例日目标）";
        double workdays = month.Workdays > 0 ? month.Workdays : 25;
        double dayCoalM3 = DailyCoalM3(month.CoalWanT, workdays, month.Density);
        double dayRockM3 = DailyRockM3(month.StripWanM3, workdays);

        var loadFaces = cfg.Faces.Where(f => f.Process == ProcessType.Load).ToList();
        var shares = NormalizeShares(loadShares, loadFaces.Count);
        for (int i = 0; i < loadFaces.Count; i++)
            loadFaces[i].DayTargetM3 = Math.Round(dayCoalM3 * shares[i]);
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Dump))
            f.DayTargetM3 = Math.Round(dayRockM3);

        return $"短期计划「{month.PlanName}」· {month.MonthLabel}月 · 月采出 {month.CoalWanT:0}万t / 剥离 {month.StripWanM3:0}万m³ · 作业日 {workdays:0} → 日采出 {dayCoalM3 / 1e4:0.00}万m³";
    }

    /// <summary>份额归一(缺省/长度不符/和≤0 → 均分; 忠实原 FaceShares)。</summary>
    public static double[] NormalizeShares(double[]? shares, int n)
    {
        if (n <= 0) return Array.Empty<double>();
        if (shares == null || shares.Length < n) return Enumerable.Repeat(1.0 / n, n).ToArray();
        var take = shares.Take(n).ToArray();
        double sum = take.Sum();
        return sum <= 1e-6 ? Enumerable.Repeat(1.0 / n, n).ToArray() : take.Select(x => x / sum).ToArray();
    }
}
