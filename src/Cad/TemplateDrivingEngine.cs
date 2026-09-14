using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>一刀（刀量切割的一个固定步距 Δ 切片）。累积量表的一行，给均衡剥采比用。</summary>
public sealed class CutSlice
{
    public int    Index;
    public double AdvanceFrom, AdvanceTo;
    public double CoalVolM3, RockVolM3;
    public double CumCoalVolM3, CumRockVolM3;
    /// <summary>累积剥采比 = 累积岩体积 / 累积煤量(t)。</summary>
    public double StripRatioCum;
    public readonly List<(double X, double Y, double Z)> PositionLine = new();
}

/// <summary>一期（工程位置）。</summary>
public sealed class DrivePeriod
{
    public int    Index;
    public double AdvanceFrom, AdvanceTo;
    public double CoalVolM3, RockVolM3, CoalTonnage, StripRatio;
    public readonly List<(double X, double Y, double Z)> PositionLine = new();
}

public sealed class DriveOutput
{
    public bool   Success;
    public string Error = "";
    public string Provenance = "";
    public double CoalDensity, CellVolumeM3, SliceWidth;
    public long   CoalCellCount, RockCellCount;
    public readonly List<CutSlice>   Cuts    = new();
    public readonly List<DrivePeriod> Periods = new();
    public double TotalCoalVolM3, TotalRockVolM3, TotalCoalTonnage, TotalAdvance, OverallStripRatio;
    public bool   RanOutOfCoal;
    /// <summary>块体标高范围（台阶面 / 卡阶段块用）。</summary>
    public double MinZ, MaxZ;
}

/// <summary>一个块体单元（中心 + 尺寸 + 煤/岩）。IsCoal / IsRock 都 false = 忽略类别（不计煤也不计岩）。</summary>
public sealed class CellBox
{
    public double Cx, Cy, Cz, Sx, Sy, Sz;
    public bool   IsCoal;
    public bool   IsRock = true;
    public int    SourceIndex = -1;
    public double VolM3 => Sx * Sy * Sz;
}

/// <summary>
/// 「驱动开采模板」= 刀量切割引擎（忠实移植原 <c>TemplateDrivingEngine</c>；Kylin 消费块体单元表而非网格规格）。
/// 每隔 Δ 米驱动一刀，逐刀留块分煤/岩，累加成累积量表（给均衡剥采比）；工程位置（期）按 卡量(基于量,只数煤)/v(基于距离) 从累积量表切出。
/// 推进坐标 s = 沿推进方向垂距 + 台阶退距 BenchOffset(块高−煤底板)；方向逐段来自工作线。
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item>原版按网格 (nx,ny,nz) 线性索引逐列；Kylin 块体是单元表，列 = 按 (cx/sx, cy/sy) 取整分组，煤底板 = 该列最下煤块的底面。</item>
///   <item>煤/岩判据由调用方给（Kylin：「块体煤岩分类」指定的类别码；未指定则品位 ≥ 平均品位算煤 —— 与「开采程序切分」同口径）。</item>
/// </list>
/// </summary>
public static class TemplateDrivingEngine
{
    private const int kMaxBins = 500_000;

    /// <summary>台阶剖面在高出底基准 h 处、沿推进方向的水平退距。benchH≤0 退化为单斜面 h/tanFace。</summary>
    public static double BenchOffset(double h, double benchH, double bermW, double tanFace)
    {
        if (h <= 0) return 0;
        if (benchH <= 0) return h / tanFace;
        int nb = (int)(h / benchH);
        double hr = h - nb * benchH;
        return nb * (benchH / tanFace + bermW) + hr / tanFace;
    }

    private sealed class Geometry
    {
        public int nSeg;
        public double[] segMidX = Array.Empty<double>(), segMidY = Array.Empty<double>(), dirX = Array.Empty<double>(), dirY = Array.Empty<double>(), tanX = Array.Empty<double>(), tanY = Array.Empty<double>(), halfLen = Array.Empty<double>();
        public double[] vDirX = Array.Empty<double>(), vDirY = Array.Empty<double>();
    }

    private static Geometry? BuildGeometry(WorkLineSamples wl)
    {
        int nSeg = Math.Min(wl.Samples.Count, wl.Baseline.Count - 1 + (wl.Closed ? 1 : 0));
        if (nSeg < 1) return null;
        var g = new Geometry { nSeg = nSeg, segMidX = new double[nSeg], segMidY = new double[nSeg], dirX = new double[nSeg], dirY = new double[nSeg], tanX = new double[nSeg], tanY = new double[nSeg], halfLen = new double[nSeg] };
        for (int i = 0; i < nSeg; i++)
        {
            var a = wl.Baseline[i]; var b = wl.Baseline[(i + 1) % wl.Baseline.Count];
            double tx = b.X - a.X, ty = b.Y - a.Y;
            double tl = Math.Sqrt(tx * tx + ty * ty); if (tl < 1e-9) { tx = 1; ty = 0; tl = 1; }
            g.tanX[i] = tx / tl; g.tanY[i] = ty / tl; g.halfLen[i] = tl * 0.5;
            g.segMidX[i] = (a.X + b.X) * 0.5; g.segMidY[i] = (a.Y + b.Y) * 0.5;
            double dx = wl.Samples[i].Dx, dy = wl.Samples[i].Dy;
            double dl = Math.Sqrt(dx * dx + dy * dy); if (dl < 1e-9) { dx = -g.tanY[i]; dy = g.tanX[i]; dl = 1; }
            g.dirX[i] = dx / dl; g.dirY[i] = dy / dl;
        }
        int n = wl.Baseline.Count;
        g.vDirX = new double[n]; g.vDirY = new double[n];
        for (int vi = 0; vi < n; vi++)
        {
            double axx = 0, ayy = 0; int cnt = 0;
            if (vi - 1 >= 0 && vi - 1 < nSeg) { axx += g.dirX[vi - 1]; ayy += g.dirY[vi - 1]; cnt++; }
            if (vi < nSeg) { axx += g.dirX[vi]; ayy += g.dirY[vi]; cnt++; }
            if (cnt == 0) { axx = g.dirX[0]; ayy = g.dirY[0]; }
            double al = Math.Sqrt(axx * axx + ayy * ayy); if (al < 1e-9) al = 1;
            g.vDirX[vi] = axx / al; g.vDirY[vi] = ayy / al;
        }
        return g;
    }

    /// <summary>期末 / 刀末工作线位置：基线各顶点沿逐顶点推进方向推 a 米。</summary>
    public static List<(double X, double Y, double Z)> PositionAt(WorkLineSamples wl, double a)
    {
        var g = BuildGeometry(wl);
        var line = new List<(double X, double Y, double Z)>(wl.Baseline.Count);
        if (g == null) return line;
        for (int vi = 0; vi < wl.Baseline.Count; vi++) { var p = wl.Baseline[vi]; line.Add((p.X + a * g.vDirX[vi], p.Y + a * g.vDirY[vi], p.Z)); }
        return line;
    }

    /// <summary>列 = 同 (x,y) 的一摞块。</summary>
    private sealed class Column { public double Cx, Cy, FloorZ = double.MaxValue, A0; public bool Swept, HasCoal; public List<int> Cells = new(); }

    private static Dictionary<(long, long), Column> GroupColumns(IReadOnlyList<CellBox> cells, out double minZ, out double maxZ, out double sxMax, out double syMax)
    {
        var cols = new Dictionary<(long, long), Column>();
        minZ = double.MaxValue; maxZ = double.MinValue; sxMax = 0; syMax = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            double sx = c.Sx > 0 ? c.Sx : 1, sy = c.Sy > 0 ? c.Sy : 1;
            var key = ((long)Math.Floor(c.Cx / sx + 1e-9), (long)Math.Floor(c.Cy / sy + 1e-9));   // 中心落在格中 ⇒ floor 即列号（Round 会把 .5 按银行家舍入并错列）
            if (!cols.TryGetValue(key, out var col)) { col = new Column { Cx = c.Cx, Cy = c.Cy }; cols[key] = col; }
            col.Cells.Add(i);
            if (c.IsCoal) { col.HasCoal = true; col.FloorZ = Math.Min(col.FloorZ, c.Cz - c.Sz * 0.5); }
            minZ = Math.Min(minZ, c.Cz - c.Sz * 0.5); maxZ = Math.Max(maxZ, c.Cz + c.Sz * 0.5);
            sxMax = Math.Max(sxMax, sx); syMax = Math.Max(syMax, sy);
        }
        return cols;
    }

    /// <summary>列指派工作线段 + 推进垂距 a0（只依赖列中心）。</summary>
    private static double AssignColumns(Geometry g, Dictionary<(long, long), Column> cols, double latTol, bool cutToEnd, double oz)
    {
        double maxA0 = 0;
        foreach (var col in cols.Values)
        {
            if (!cutToEnd && !col.HasCoal) continue;
            if (cutToEnd) col.FloorZ = oz;
            int best = -1; double bestPerp = double.MaxValue, bestA0 = 0;
            for (int sgi = 0; sgi < g.nSeg; sgi++)
            {
                double rx = col.Cx - g.segMidX[sgi], ry = col.Cy - g.segMidY[sgi];
                double t = rx * g.tanX[sgi] + ry * g.tanY[sgi];
                if (Math.Abs(t) > g.halfLen[sgi] + latTol) continue;
                double a0 = rx * g.dirX[sgi] + ry * g.dirY[sgi];
                if (Math.Abs(a0) < bestPerp) { bestPerp = Math.Abs(a0); best = sgi; bestA0 = a0; }
            }
            if (best < 0) continue;
            col.Swept = true; col.A0 = bestA0;
            if (bestA0 > maxA0) maxA0 = bestA0;
        }
        return maxA0;
    }

    public static DriveOutput Run(
        IReadOnlyList<CellBox>? cells, WorkLineSamples wl, bool volumeDriven,
        double advancePerPeriod, double targetCoalM3PerPeriod, int periods,
        double alphaDeg, double coalDensity, double sliceWidth,
        bool cutToEnd = false, double benchStepHeight = 0, double minBermWidth = 0, string provenance = "块体")
    {
        var o = new DriveOutput { CoalDensity = coalDensity, SliceWidth = sliceWidth };
        if (wl == null || !wl.Success || wl.Baseline.Count < 2 || wl.Samples.Count < 1) { o.Error = "工作线几何无效"; return o; }
        if (periods < 1) { o.Error = "期数 < 1"; return o; }
        if (sliceWidth <= 0) { o.Error = "刀距 Δ 必须 > 0"; return o; }
        if (cells == null || cells.Count == 0) { o.Error = "未激活块体模型（请先导入/生成块体）"; return o; }
        var g = BuildGeometry(wl);
        if (g == null) { o.Error = "工作线段数 < 1"; return o; }
        o.Provenance = provenance;

        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);
        var cols = GroupColumns(cells, out double oz, out double topZ, out double sx, out double sy);
        o.MinZ = oz; o.MaxZ = topZ;
        long coalCells = 0; foreach (var c in cells) if (c.IsCoal) coalCells++;
        if (coalCells == 0) { o.Error = "块体里没有煤块"; return o; }
        double cellVol = cells[0].VolM3; o.CellVolumeM3 = cellVol;
        double latTol = Math.Max(sx, sy);
        double maxA0 = AssignColumns(g, cols, latTol, cutToEnd, oz);

        long estBins = (long)((maxA0 + BenchOffset(topZ - oz, benchStepHeight, minBermWidth, tanA)) / sliceWidth) + 2;
        int nBins = (int)Math.Min(Math.Max(estBins, 1), kMaxBins);
        var coalBin = new double[nBins]; var rockBin = new double[nBins];
        long rockCells = 0;
        foreach (var col in cols.Values)
        {
            if (!col.Swept) continue;
            foreach (int ci in col.Cells)
            {
                var c = cells[ci];
                double h = c.Cz - col.FloorZ; if (h < 0) continue;
                double s = col.A0 + BenchOffset(h, benchStepHeight, minBermWidth, tanA);
                if (s < 0) continue;
                int bin = (int)(s / sliceWidth); if (bin < 0) bin = 0; if (bin >= nBins) bin = nBins - 1;
                if (c.IsCoal) coalBin[bin] += c.VolM3;
                else if (c.IsRock) { rockBin[bin] += c.VolM3; rockCells++; }
            }
        }
        o.CoalCellCount = coalCells; o.RockCellCount = rockCells;

        var cumCoal = new double[nBins]; var cumRock = new double[nBins];
        double ac = 0, ar = 0;
        for (int bi = 0; bi < nBins; bi++) { ac += coalBin[bi]; ar += rockBin[bi]; cumCoal[bi] = ac; cumRock[bi] = ar; }
        if (ac <= 0) { o.Error = "工作线前方扫掠不到煤块（方向/位置不对？）"; return o; }

        double CumCoalVol(int bin) => bin < 0 ? 0 : cumCoal[Math.Min(bin, nBins - 1)];
        double CumRockVol(int bin) => bin < 0 ? 0 : cumRock[Math.Min(bin, nBins - 1)];
        int BinAt(double a) { int b = (int)(a / sliceWidth) - 1; if (b < 0) b = -1; if (b >= nBins) b = nBins - 1; return b; }
        List<(double, double, double)> PosAt(double a)
        {
            var line = new List<(double, double, double)>(wl.Baseline.Count);
            for (int vi = 0; vi < wl.Baseline.Count; vi++) { var p = wl.Baseline[vi]; line.Add((p.X + a * g.vDirX[vi], p.Y + a * g.vDirY[vi], p.Z)); }
            return line;
        }

        double advanceA;
        if (cutToEnd)
        {
            int lastBin = -1;
            for (int bi = nBins - 1; bi >= 0; bi--) if (coalBin[bi] > 0 || rockBin[bi] > 0) { lastBin = bi; break; }
            advanceA = (lastBin + 1) * sliceWidth;
            if (advanceA <= 0) advanceA = sliceWidth;
        }
        else if (!volumeDriven) advanceA = periods * advancePerPeriod;
        else
        {
            double targetTotalVol = periods * Math.Max(0.0, targetCoalM3PerPeriod);
            int hitBin = -1;
            for (int bi = 0; bi < nBins; bi++) if (cumCoal[bi] >= targetTotalVol) { hitBin = bi; break; }
            if (hitBin < 0) { advanceA = nBins * sliceWidth; o.RanOutOfCoal = true; }
            else advanceA = (hitBin + 1) * sliceWidth;
        }
        double maxBinAdvance = nBins * sliceWidth;
        if (advanceA > maxBinAdvance) advanceA = maxBinAdvance;
        o.TotalAdvance = advanceA;

        int cutCount = Math.Min((int)Math.Ceiling(advanceA / sliceWidth), nBins);
        for (int bi = 0; bi < cutCount; bi++)
        {
            double cumCoalT = cumCoal[bi] * coalDensity;
            var cut = new CutSlice
            {
                Index = bi + 1, AdvanceFrom = bi * sliceWidth, AdvanceTo = (bi + 1) * sliceWidth,
                CoalVolM3 = coalBin[bi], RockVolM3 = rockBin[bi], CumCoalVolM3 = cumCoal[bi], CumRockVolM3 = cumRock[bi],
                StripRatioCum = cumCoalT > 1e-6 ? cumRock[bi] / cumCoalT : 0,
            };
            cut.PositionLine.AddRange(PosAt((bi + 1) * sliceWidth));
            o.Cuts.Add(cut);
        }
        o.TotalCoalVolM3 = CumCoalVol(cutCount - 1);
        o.TotalRockVolM3 = CumRockVol(cutCount - 1);
        o.TotalCoalTonnage = o.TotalCoalVolM3 * coalDensity;
        o.OverallStripRatio = o.TotalCoalTonnage > 1e-6 ? o.TotalRockVolM3 / o.TotalCoalTonnage : 0;

        double prevA = 0, prevCoalVol = 0, prevRockVol = 0;
        for (int kk = 1; kk <= periods && !cutToEnd; kk++)
        {
            double aTo;
            if (!volumeDriven) aTo = kk * advancePerPeriod;
            else
            {
                double targetVol = kk * Math.Max(0.0, targetCoalM3PerPeriod);
                int hb = -1;
                for (int bi = 0; bi < nBins; bi++) if (cumCoal[bi] >= targetVol) { hb = bi; break; }
                aTo = hb < 0 ? maxBinAdvance : (hb + 1) * sliceWidth;
            }
            if (aTo > advanceA) aTo = advanceA;
            double cCoal = CumCoalVol(BinAt(aTo)), cRock = CumRockVol(BinAt(aTo));
            double coalVol = cCoal - prevCoalVol, rockVol = cRock - prevRockVol;
            double coalT = coalVol * coalDensity;
            var per = new DrivePeriod { Index = kk, AdvanceFrom = prevA, AdvanceTo = aTo, CoalVolM3 = coalVol, RockVolM3 = rockVol, CoalTonnage = coalT, StripRatio = coalT > 1e-6 ? rockVol / coalT : 0 };
            per.PositionLine.AddRange(PosAt(aTo));
            o.Periods.Add(per);
            prevA = aTo; prevCoalVol = cCoal; prevRockVol = cRock;
            if (volumeDriven && o.RanOutOfCoal && aTo >= advanceA) break;
        }
        o.Success = o.Cuts.Count > 0 && (cutToEnd || o.Periods.Count > 0);
        if (!o.Success) o.Error = "未产出累积量表/期";
        return o;
    }

    /// <summary>沿推进轴的累积量剖面（口径同 Run；常规档：底 = 该列煤底板）。</summary>
    public sealed class AdvanceProfile
    {
        public bool Success; public string Error = ""; public string Provenance = "";
        public double SliceWidth; public int NBins;
        public double[] CumCoal = Array.Empty<double>(); public double[] CumRock = Array.Empty<double>();
        public double TotalCoalVol, TotalRockVol, MaxAdvance;
        public Func<double, List<(double X, double Y, double Z)>> PosAt = _ => new();
    }

    public static AdvanceProfile BuildAdvanceProfile(IReadOnlyList<CellBox>? cells, WorkLineSamples wl, double alphaDeg, double benchStepHeight, double minBermWidth, double sliceWidth, string provenance = "块体")
    {
        var p = new AdvanceProfile { SliceWidth = sliceWidth, Provenance = provenance };
        if (wl == null || !wl.Success || wl.Baseline.Count < 2 || wl.Samples.Count < 1) { p.Error = "工作线几何无效"; return p; }
        if (sliceWidth <= 0) { p.Error = "刀距 Δ 必须 > 0"; return p; }
        if (cells == null || cells.Count == 0) { p.Error = "未激活块体模型"; return p; }
        var g = BuildGeometry(wl); if (g == null) { p.Error = "工作线段数 < 1"; return p; }
        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);
        var cols = GroupColumns(cells, out double oz, out double topZ, out double sx, out double sy);
        bool anyCoal = false; foreach (var c in cells) if (c.IsCoal) { anyCoal = true; break; }
        if (!anyCoal) { p.Error = "块体里没有煤块"; return p; }
        double maxA0 = AssignColumns(g, cols, Math.Max(sx, sy), false, oz);
        long estBins = (long)((maxA0 + BenchOffset(topZ - oz, benchStepHeight, minBermWidth, tanA)) / sliceWidth) + 2;
        int nBins = (int)Math.Min(Math.Max(estBins, 1), kMaxBins);
        var coalBin = new double[nBins]; var rockBin = new double[nBins];
        foreach (var col in cols.Values)
        {
            if (!col.Swept) continue;
            foreach (int ci in col.Cells)
            {
                var c = cells[ci];
                double h = c.Cz - col.FloorZ; if (h < 0) continue;
                double s = col.A0 + BenchOffset(h, benchStepHeight, minBermWidth, tanA);
                if (s < 0) continue;
                int bin = (int)(s / sliceWidth); if (bin < 0) bin = 0; if (bin >= nBins) bin = nBins - 1;
                if (c.IsCoal) coalBin[bin] += c.VolM3; else if (c.IsRock) rockBin[bin] += c.VolM3;
            }
        }
        var cumCoal = new double[nBins]; var cumRock = new double[nBins];
        double ac = 0, ar = 0;
        for (int bi = 0; bi < nBins; bi++) { ac += coalBin[bi]; ar += rockBin[bi]; cumCoal[bi] = ac; cumRock[bi] = ar; }
        if (ac <= 0) { p.Error = "工作线前方扫掠不到煤块（方向/位置不对？）"; return p; }
        var baseline = wl.Baseline;
        p.PosAt = a =>
        {
            var line = new List<(double X, double Y, double Z)>(baseline.Count);
            for (int vi = 0; vi < baseline.Count; vi++) { var pt = baseline[vi]; line.Add((pt.X + a * g.vDirX[vi], pt.Y + a * g.vDirY[vi], pt.Z)); }
            return line;
        };
        p.CumCoal = cumCoal; p.CumRock = cumRock; p.TotalCoalVol = ac; p.TotalRockVol = ar;
        p.NBins = nBins; p.MaxAdvance = nBins * sliceWidth;
        p.Success = true;
        return p;
    }

    /// <summary>量约束双前界反解结果。</summary>
    public sealed class QuantityRatioSolution
    {
        public bool Success; public string Error = ""; public string Provenance = "";
        public double CoalFrontAdvance, RockFrontAdvance, LeadDistance;
        public double TargetCoalVolM3, TargetRockVolM3;
        public double ActualCoalVolM3, ActualRockVolM3, ActualCoalTonnage, ActualStripRatio;
        public double MaxCoalVolM3, MaxRockVolM3;
        public bool CoalReachable, RockReachable, RockLeadsCoal;
        public readonly List<(double X, double Y, double Z)> CoalFrontLine = new();
        public readonly List<(double X, double Y, double Z)> RockFrontLine = new();
    }

    /// <summary>量约束双前界反解：给 Q(本期煤量 m³)、n(剥采比)、ρ → 采煤前界 + 剥离前界(超前位置) + 三闸（剥离超前 / 量可达 / 单调）。</summary>
    public static QuantityRatioSolution SolveQuantityRatio(IReadOnlyList<CellBox>? cells, WorkLineSamples wl, double targetCoalM3, double targetRatio, double coalDensity,
                                                           double alphaDeg, double sliceWidth, double benchStepHeight = 0, double minBermWidth = 0, string provenance = "块体")
    {
        var s = new QuantityRatioSolution();
        if (targetCoalM3 <= 0) { s.Error = "本期煤量目标 Q 必须 > 0"; return s; }
        if (targetRatio < 0) { s.Error = "剥采比 n 不能为负"; return s; }
        if (coalDensity <= 0) coalDensity = 1.0;
        var prof = BuildAdvanceProfile(cells, wl, alphaDeg, benchStepHeight, minBermWidth, sliceWidth, provenance);
        if (!prof.Success) { s.Error = prof.Error; return s; }
        s.Provenance = prof.Provenance;
        double rockTarget = targetRatio * targetCoalM3 * coalDensity;
        s.TargetCoalVolM3 = targetCoalM3; s.TargetRockVolM3 = rockTarget;
        s.MaxCoalVolM3 = prof.TotalCoalVol; s.MaxRockVolM3 = prof.TotalRockVol;
        int coalBin = FirstBinAtLeast(prof.CumCoal, targetCoalM3);
        int rockBin = FirstBinAtLeast(prof.CumRock, rockTarget);
        s.CoalReachable = coalBin >= 0;
        s.RockReachable = rockBin >= 0 || rockTarget <= 0;
        if (coalBin < 0) coalBin = prof.NBins - 1;
        if (rockBin < 0) rockBin = prof.NBins - 1;
        s.CoalFrontAdvance = (coalBin + 1) * prof.SliceWidth;
        s.RockFrontAdvance = (rockBin + 1) * prof.SliceWidth;
        s.LeadDistance = s.RockFrontAdvance - s.CoalFrontAdvance;
        s.RockLeadsCoal = s.RockFrontAdvance >= s.CoalFrontAdvance;
        s.ActualCoalVolM3 = prof.CumCoal[coalBin]; s.ActualRockVolM3 = prof.CumRock[rockBin];
        s.ActualCoalTonnage = s.ActualCoalVolM3 * coalDensity;
        s.ActualStripRatio = s.ActualCoalTonnage > 1e-6 ? s.ActualRockVolM3 / s.ActualCoalTonnage : 0;
        s.CoalFrontLine.AddRange(prof.PosAt(s.CoalFrontAdvance));
        s.RockFrontLine.AddRange(prof.PosAt(s.RockFrontAdvance));
        s.Success = true;
        return s;
    }

    private static int FirstBinAtLeast(double[] cum, double targetVol)
    {
        if (targetVol <= 0) return 0;
        for (int bi = 0; bi < cum.Length; bi++) if (cum[bi] >= targetVol - 1e-9) return bi;
        return -1;
    }

    /// <summary>纯几何预览（未激活块体）：只按工作线逐段方向 + 刀距 Δ + 推进度 v 算逐刀 / 逐期位置线，不数煤/岩量。</summary>
    public static DriveOutput PreviewGeometry(WorkLineSamples wl, double advancePerPeriod, int periods, double sliceWidth)
    {
        var o = new DriveOutput { SliceWidth = sliceWidth, Provenance = "几何预览（未激活块体，不数量）" };
        if (wl == null || !wl.Success || wl.Baseline.Count < 2 || wl.Samples.Count < 1) { o.Error = "工作线几何无效"; return o; }
        if (periods < 1) { o.Error = "期数 < 1"; return o; }
        if (advancePerPeriod <= 0) advancePerPeriod = sliceWidth > 0 ? sliceWidth : 10;
        if (sliceWidth <= 0) sliceWidth = advancePerPeriod;
        var g = BuildGeometry(wl); if (g == null) { o.Error = "工作线段数 < 1"; return o; }
        List<(double, double, double)> PosAt(double a)
        {
            var line = new List<(double, double, double)>(wl.Baseline.Count);
            for (int vi = 0; vi < wl.Baseline.Count; vi++) { var p = wl.Baseline[vi]; line.Add((p.X + a * g.vDirX[vi], p.Y + a * g.vDirY[vi], p.Z)); }
            return line;
        }
        double A = periods * advancePerPeriod;
        int cutCount = (int)Math.Ceiling(A / sliceWidth);
        for (int bi = 0; bi < cutCount; bi++)
        {
            double aTo = Math.Min((bi + 1) * sliceWidth, A);
            var cut = new CutSlice { Index = bi + 1, AdvanceFrom = bi * sliceWidth, AdvanceTo = aTo };
            cut.PositionLine.AddRange(PosAt(aTo));
            o.Cuts.Add(cut);
        }
        for (int kk = 1; kk <= periods; kk++)
        {
            var per = new DrivePeriod { Index = kk, AdvanceFrom = (kk - 1) * advancePerPeriod, AdvanceTo = kk * advancePerPeriod };
            per.PositionLine.AddRange(PosAt(kk * advancePerPeriod));
            o.Periods.Add(per);
        }
        o.TotalAdvance = A;
        o.Success = o.Cuts.Count > 0;
        return o;
    }

    /// <summary>收集推进坐标 s ∈ [a0,a1) 的块体单元（切到最后一刀口径：底 = 块体最下标高，横向工作线带内全扫）。给「卡阶段块」用。</summary>
    public static List<CellBox> CollectCellsBetween(IReadOnlyList<CellBox> cells, WorkLineSamples wl, double alphaDeg, double benchStepHeight, double minBermWidth,
                                                    double a0, double a1, bool includeCoal, bool includeRock, out double coalVol, out double rockVol)
    {
        coalVol = 0; rockVol = 0;
        var outp = new List<CellBox>();
        var g = BuildGeometry(wl); if (g == null || cells.Count == 0) return outp;
        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);
        var cols = GroupColumns(cells, out double oz, out _, out double sx, out double sy);
        AssignColumns(g, cols, Math.Max(sx, sy), true, oz);
        foreach (var col in cols.Values)
        {
            if (!col.Swept) continue;
            foreach (int ci in col.Cells)
            {
                var c = cells[ci];
                double h = c.Cz - oz; if (h < 0) continue;
                double s = col.A0 + BenchOffset(h, benchStepHeight, minBermWidth, tanA);
                if (s < a0 || s >= a1) continue;
                if (c.IsCoal) { if (!includeCoal) continue; coalVol += c.VolM3; }
                else if (c.IsRock) { if (!includeRock) continue; rockVol += c.VolM3; }
                else continue;
                outp.Add(c);
            }
        }
        return outp;
    }

    /// <summary>台阶剖面：从底 minZ 到顶 maxZ 的 (沿推进退距 off, 高程 z) 折线（分台阶坡面 + 平盘）。</summary>
    public static List<(double off, double z)> BenchProfile(double minZ, double maxZ, double alphaDeg, double benchH, double bermW)
    {
        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);
        var pts = new List<(double, double)> { (0, minZ) };
        if (benchH <= 0) { pts.Add(((maxZ - minZ) / tanA, maxZ)); return pts; }
        double off = 0, z = minZ; int guard = 0;
        while (z < maxZ - 1e-6 && guard++ < 2000)
        {
            double dz = Math.Min(benchH, maxZ - z);
            off += dz / tanA; z += dz;
            pts.Add((off, z));
            if (z >= maxZ - 1e-6) break;
            off += bermW;
            pts.Add((off, z));
        }
        return pts;
    }

    /// <summary>某刀推进 a 的分台阶切割台阶面：三角网顶点/索引（工作线各顶点 × 剖面点）。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) FaceMesh(WorkLineSamples wl, double a, List<(double off, double z)> prof)
    {
        var verts = new List<(double x, double y, double z)>(); var tris = new List<(int a, int b, int c)>();
        var g = BuildGeometry(wl); if (g == null) return (verts, tris);
        int n = wl.Baseline.Count, P = prof.Count;
        if (n < 2 || P < 2) return (verts, tris);
        for (int i = 0; i < n; i++)
            for (int p = 0; p < P; p++)
            {
                var b = wl.Baseline[i]; double adv = a - prof[p].off;
                verts.Add((b.X + adv * g.vDirX[i], b.Y + adv * g.vDirY[i], prof[p].z));
            }
        for (int i = 0; i + 1 < n; i++)
            for (int p = 0; p + 1 < P; p++)
            {
                int a00 = i * P + p, a10 = (i + 1) * P + p, a11 = (i + 1) * P + p + 1, a01 = i * P + p + 1;
                tris.Add((a00, a10, a11)); tris.Add((a00, a11, a01));
            }
        return (verts, tris);
    }
}
