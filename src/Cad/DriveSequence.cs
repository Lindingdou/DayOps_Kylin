using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 开采程序逐期切分 —— 忠实移植原 MineAssLib TemplateDrivingEngine 的距离/量驱动核 + 台阶退距。
/// 沿推进方向把块体逐格投影到推进轴(可选加台阶退距: 高处的格因帮坡后退) → 按步距(等距)或累计煤量(等煤量)分期
/// → 逐期煤/岩体积 + 累计剥采比。生成的分期量表正是 <see cref="TautString"/>(剥采比均衡)的输入(闭合"块体→分期→均衡"环)。
/// 纯逻辑、可单测。faceAngleDeg≤0 时退化为平面(陡帮/垂直, 无退距)。
/// </summary>
public static class DriveSequence
{
    public readonly record struct Cell(double X, double Y, double Z, double VolM3, bool IsCoal);

    public sealed record PeriodTally(int Index, double CoalVolM3, double RockVolM3,
        double CumCoalVolM3, double CumRockVolM3, double CumStripRatio);

    public sealed record DriveResult(IReadOnlyList<PeriodTally> Periods,
        double TotalCoalVolM3, double TotalRockVolM3, double OverallStripRatio);

    /// <summary>
    /// 台阶剖面在高出底基准 h 处沿推进方向的水平退距(忠实原 BenchOffset)。
    /// benchHeightM≤0 = 单斜面 h/tanα; 否则逐台阶(坡面 benchH/tanα + 平盘 bermW)+ 余高 hr/tanα。α 钳 [1,89]°。
    /// </summary>
    public static double BenchOffset(double h, double benchHeightM, double bermWidthM, double faceAngleDeg)
    {
        if (h <= 0) return 0;
        double a = Math.Max(1.0, Math.Min(89.0, faceAngleDeg));
        double tanFace = Math.Tan(a * Math.PI / 180.0);
        if (tanFace <= 1e-9) return 0;
        if (benchHeightM <= 0) return h / tanFace;
        int nb = (int)(h / benchHeightM);
        double hr = h - nb * benchHeightM;
        return nb * (benchHeightM / tanFace + Math.Max(0.0, bermWidthM)) + hr / tanFace;
    }

    // 各格投影到推进轴 + 可选台阶退距 → 调整后位置 adj[i]; 出 sMin。setback 时用最低格作底基准。
    private static (double[] adj, double sMin) Project(IReadOnlyList<Cell> cells, double ux, double uy,
        bool setback, double benchHeightM, double bermWidthM, double faceAngleDeg)
    {
        double floorZ = double.MaxValue;
        if (setback) foreach (var c in cells) if (c.Z < floorZ) floorZ = c.Z;
        var adj = new double[cells.Count];
        double sMin = double.MaxValue;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            double s = c.X * ux + c.Y * uy + (setback ? BenchOffset(c.Z - floorZ, benchHeightM, bermWidthM, faceAngleDeg) : 0);
            adj[i] = s; if (s < sMin) sMin = s;
        }
        return (adj, sMin);
    }

    /// <summary>
    /// 按推进步距分期(等距)。dir=推进方位; advancePerPeriod=每期推进距 m; coalDensity=煤密度 t/m³。
    /// faceAngleDeg&gt;0 时加台阶退距(benchHeightM/bermWidthM 定台阶剖面); ≤0 = 平面。maxPeriods&gt;0 只取前若干期。
    /// </summary>
    public static DriveResult SweepByDistance(IReadOnlyList<Cell> cells, double dirX, double dirY,
        double advancePerPeriod, double coalDensity, int maxPeriods = 0,
        double faceAngleDeg = 0, double benchHeightM = 0, double bermWidthM = 0)
    {
        double dl = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (cells == null || cells.Count == 0 || dl < 1e-9 || advancePerPeriod <= 0 || coalDensity <= 0)
            return new DriveResult(Array.Empty<PeriodTally>(), 0, 0, 0);
        double ux = dirX / dl, uy = dirY / dl;
        var (adj, sMin) = Project(cells, ux, uy, faceAngleDeg > 0, benchHeightM, bermWidthM, faceAngleDeg);

        var coalByP = new Dictionary<int, double>();
        var rockByP = new Dictionary<int, double>();
        int maxP = -1;
        for (int i = 0; i < cells.Count; i++)
        {
            int p = (int)((adj[i] - sMin) / advancePerPeriod);
            if (p < 0) p = 0;
            if (maxPeriods > 0 && p >= maxPeriods) continue;
            if (cells[i].IsCoal) coalByP[p] = coalByP.GetValueOrDefault(p) + cells[i].VolM3;
            else rockByP[p] = rockByP.GetValueOrDefault(p) + cells[i].VolM3;
            if (p > maxP) maxP = p;
        }

        var periods = new List<PeriodTally>(Math.Max(0, maxP + 1));
        double cumC = 0, cumR = 0;
        for (int p = 0; p <= maxP; p++)
        {
            double cv = coalByP.GetValueOrDefault(p), rv = rockByP.GetValueOrDefault(p);
            cumC += cv; cumR += rv;
            periods.Add(new PeriodTally(p, cv, rv, cumC, cumR, cumC > 1e-9 ? cumR / (cumC * coalDensity) : 0));
        }
        return new DriveResult(periods, cumC, cumR, cumC > 1e-9 ? cumR / (cumC * coalDensity) : 0);
    }

    /// <summary>
    /// 按煤量分期(等煤量, 恒定产量规划): 细分刀(sliceWidth) → 顺推累计煤量, 每达 targetCoalVolM3 切一期(刀粒度)。
    /// faceAngleDeg&gt;0 时同样加台阶退距。
    /// </summary>
    public static DriveResult SweepByVolume(IReadOnlyList<Cell> cells, double dirX, double dirY,
        double sliceWidth, double targetCoalVolM3, double coalDensity, int maxPeriods = 0,
        double faceAngleDeg = 0, double benchHeightM = 0, double bermWidthM = 0)
    {
        double dl = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (cells == null || cells.Count == 0 || dl < 1e-9 || sliceWidth <= 0 || targetCoalVolM3 <= 0 || coalDensity <= 0)
            return new DriveResult(Array.Empty<PeriodTally>(), 0, 0, 0);
        double ux = dirX / dl, uy = dirY / dl;
        var (adj, sMin) = Project(cells, ux, uy, faceAngleDeg > 0, benchHeightM, bermWidthM, faceAngleDeg);
        double sMax = double.MinValue; foreach (var s in adj) if (s > sMax) sMax = s;
        int nBins = (int)((sMax - sMin) / sliceWidth) + 1;
        var coalBin = new double[nBins]; var rockBin = new double[nBins];
        for (int i = 0; i < cells.Count; i++)
        {
            int bi = (int)((adj[i] - sMin) / sliceWidth);
            if (bi < 0) bi = 0; if (bi >= nBins) bi = nBins - 1;
            if (cells[i].IsCoal) coalBin[bi] += cells[i].VolM3; else rockBin[bi] += cells[i].VolM3;
        }
        var periods = new List<PeriodTally>();
        double pc = 0, pr = 0, cumC = 0, cumR = 0;
        for (int bi = 0; bi < nBins; bi++)
        {
            pc += coalBin[bi]; pr += rockBin[bi];
            bool last = bi == nBins - 1;
            if ((pc >= targetCoalVolM3 - 1e-9 || last) && (pc > 1e-9 || pr > 1e-9))
            {
                if (maxPeriods > 0 && periods.Count >= maxPeriods) break;
                cumC += pc; cumR += pr;
                periods.Add(new PeriodTally(periods.Count, pc, pr, cumC, cumR, cumC > 1e-9 ? cumR / (cumC * coalDensity) : 0));
                pc = 0; pr = 0;
            }
        }
        double totC = 0, totR = 0; foreach (var p in periods) { totC += p.CoalVolM3; totR += p.RockVolM3; }
        return new DriveResult(periods, totC, totR, totC > 1e-9 ? totR / (totC * coalDensity) : 0);
    }

    /// <summary>
    /// 沿(可弯)工作线多段推进分期(忠实原多段投影): 每格投影到最近工作线段的法向(=推进方向)得推进位置 a0(取 |a0| 最小段,
    /// 横向容差 latTol=cellSizeM 收角点), 按步距分期。工作线直线时等价 <see cref="SweepByDistance"/>(法向=段切向+90°, 工作线走向定推进侧)。
    /// </summary>
    public static DriveResult SweepAlongWorkLine(IReadOnlyList<(double x, double y)> workLine, IReadOnlyList<Cell> cells,
        double advancePerPeriod, double coalDensity, double cellSizeM, int maxPeriods = 0,
        double faceAngleDeg = 0, double benchHeightM = 0, double bermWidthM = 0)
    {
        if (cells == null || cells.Count == 0 || workLine == null || workLine.Count < 2 || advancePerPeriod <= 0 || coalDensity <= 0)
            return new DriveResult(Array.Empty<PeriodTally>(), 0, 0, 0);
        int nSeg = workLine.Count - 1;
        var mx = new double[nSeg]; var my = new double[nSeg];
        var tx = new double[nSeg]; var ty = new double[nSeg];   // 切向(单位)
        var nnx = new double[nSeg]; var nny = new double[nSeg]; // 法向 = 切向 +90°: (x,y)→(−y,x)
        var half = new double[nSeg];
        double latTol = Math.Max(1e-9, cellSizeM);
        for (int s = 0; s < nSeg; s++)
        {
            var a = workLine[s]; var b = workLine[s + 1];
            double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
            mx[s] = (a.x + b.x) / 2; my[s] = (a.y + b.y) / 2; half[s] = len / 2;
            if (len < 1e-9) { tx[s] = 1; ty[s] = 0; nnx[s] = 0; nny[s] = 1; }
            else { tx[s] = dx / len; ty[s] = dy / len; nnx[s] = -ty[s]; nny[s] = tx[s]; }
        }
        bool setback = faceAngleDeg > 0;
        double floorZ = double.MaxValue;
        if (setback) foreach (var c in cells) if (c.Z < floorZ) floorZ = c.Z;

        var adj = new double[cells.Count]; var okc = new bool[cells.Count];
        double sMin = double.MaxValue;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            int best = -1; double bestPerp = double.MaxValue, bestA0 = 0;
            for (int s = 0; s < nSeg; s++)
            {
                double rx = c.X - mx[s], ry = c.Y - my[s];
                if (Math.Abs(rx * tx[s] + ry * ty[s]) > half[s] + latTol) continue;   // 越出段沿程(容 latTol)
                double a0 = rx * nnx[s] + ry * nny[s];
                if (Math.Abs(a0) < bestPerp) { bestPerp = Math.Abs(a0); best = s; bestA0 = a0; }
            }
            if (best < 0) { okc[i] = false; continue; }
            adj[i] = bestA0 + (setback ? BenchOffset(c.Z - floorZ, benchHeightM, bermWidthM, faceAngleDeg) : 0);
            okc[i] = true; if (adj[i] < sMin) sMin = adj[i];
        }

        var coalByP = new Dictionary<int, double>();
        var rockByP = new Dictionary<int, double>();
        int maxP = -1;
        for (int i = 0; i < cells.Count; i++)
        {
            if (!okc[i]) continue;
            int p = (int)((adj[i] - sMin) / advancePerPeriod); if (p < 0) p = 0;
            if (maxPeriods > 0 && p >= maxPeriods) continue;
            if (cells[i].IsCoal) coalByP[p] = coalByP.GetValueOrDefault(p) + cells[i].VolM3;
            else rockByP[p] = rockByP.GetValueOrDefault(p) + cells[i].VolM3;
            if (p > maxP) maxP = p;
        }
        var periods = new List<PeriodTally>(); double cumC = 0, cumR = 0;
        for (int p = 0; p <= maxP; p++)
        {
            double cv = coalByP.GetValueOrDefault(p), rv = rockByP.GetValueOrDefault(p);
            cumC += cv; cumR += rv;
            periods.Add(new PeriodTally(p, cv, rv, cumC, cumR, cumC > 1e-9 ? cumR / (cumC * coalDensity) : 0));
        }
        return new DriveResult(periods, cumC, cumR, cumC > 1e-9 ? cumR / (cumC * coalDensity) : 0);
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
