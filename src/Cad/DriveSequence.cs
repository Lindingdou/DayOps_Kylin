using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 开采程序逐期切分 —— 忠实移植原 MineAssLib TemplateDrivingEngine 的「距离驱动」核(可验证切片)：
/// 沿推进方向把块体逐格投影到推进轴 → 按推进步距 advancePerPeriod 分期 → 逐期煤/岩体积 + 累计剥采比。
/// 生成的分期量表正是 <see cref="TautString"/>(剥采比均衡)的输入(闭合"块体→分期→均衡"环)。
/// 平面近似(陡帮 α→90° 台阶退距≈0, 与原测试 α=89° 一致); 缓帮台阶退距(s=a0+(cz−floorZ)/tanα)属工程细化, 记录。
/// 纯逻辑、可单测。
/// </summary>
public static class DriveSequence
{
    public readonly record struct Cell(double X, double Y, double VolM3, bool IsCoal);

    public sealed record PeriodTally(int Index, double CoalVolM3, double RockVolM3,
        double CumCoalVolM3, double CumRockVolM3, double CumStripRatio);

    public sealed record DriveResult(IReadOnlyList<PeriodTally> Periods,
        double TotalCoalVolM3, double TotalRockVolM3, double OverallStripRatio);

    /// <summary>
    /// 按推进步距分期。dir=推进方位(未归一化); advancePerPeriod=每期推进距 m; coalDensity=煤密度 t/m³(算剥采比 m³/t)。
    /// maxPeriods&gt;0 则只取前若干期。累计剥采比 = 累计岩量 / (累计煤量·密度)。
    /// </summary>
    public static DriveResult SweepByDistance(IReadOnlyList<Cell> cells, double dirX, double dirY,
        double advancePerPeriod, double coalDensity, int maxPeriods = 0)
    {
        double dl = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (cells == null || cells.Count == 0 || dl < 1e-9 || advancePerPeriod <= 0 || coalDensity <= 0)
            return new DriveResult(Array.Empty<PeriodTally>(), 0, 0, 0);
        double ux = dirX / dl, uy = dirY / dl;

        double sMin = double.MaxValue;
        foreach (var c in cells) { double s = c.X * ux + c.Y * uy; if (s < sMin) sMin = s; }

        var coalByP = new Dictionary<int, double>();
        var rockByP = new Dictionary<int, double>();
        int maxP = -1;
        foreach (var c in cells)
        {
            double s = c.X * ux + c.Y * uy - sMin;
            int p = (int)(s / advancePerPeriod);
            if (p < 0) p = 0;
            if (maxPeriods > 0 && p >= maxPeriods) continue;
            if (c.IsCoal) coalByP[p] = coalByP.GetValueOrDefault(p) + c.VolM3;
            else rockByP[p] = rockByP.GetValueOrDefault(p) + c.VolM3;
            if (p > maxP) maxP = p;
        }

        var periods = new List<PeriodTally>(Math.Max(0, maxP + 1));
        double cumC = 0, cumR = 0;
        for (int p = 0; p <= maxP; p++)
        {
            double cv = coalByP.GetValueOrDefault(p), rv = rockByP.GetValueOrDefault(p);
            cumC += cv; cumR += rv;
            double cumSR = cumC > 1e-9 ? cumR / (cumC * coalDensity) : 0;
            periods.Add(new PeriodTally(p, cv, rv, cumC, cumR, cumSR));
        }
        double overallSR = cumC > 1e-9 ? cumR / (cumC * coalDensity) : 0;
        return new DriveResult(periods, cumC, cumR, overallSR);
    }

    /// <summary>分期量表 → CSV(采出量万t, 剥离量万m³)——直接可喂 剥采比均衡。</summary>
    public static string ToBalanceCsv(DriveResult r, double coalDensity)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("period,coal_wan_t,waste_wan_m3\n");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var p in r.Periods)
            sb.Append($"{p.Index + 1},{(p.CoalVolM3 * coalDensity / 1e4).ToString("0.####", inv)},{(p.RockVolM3 / 1e4).ToString("0.####", inv)}\n");
        return sb.ToString();
    }
}
