using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 把激活块体单遍聚成平面剥采比场（原 <c>BlockModelLib.Domain.StripRatioFieldSampler.Sample</c> 在 <see cref="BlockModelMeta"/> 上的实现）。
/// 复用煤岩判别器（多层煤/多层岩；属岩计入剥离，忽略类别不计）；列索引 col = i + j·Nx；子块按尺寸倍数计体积。
/// 找不到煤属性或无煤返回 null。产物是 Kylin 既有 <see cref="StripRatioField"/>（同原字段）。
/// </summary>
public static class PlanStripRatioFieldSampler
{
    public static StripRatioField? Sample(BlockModelMeta? model, double density, CancellationToken ct = default)
    {
        if (model == null || model.Blocks.Count == 0) return null;
        if (!BlockModelCoal.TryGetClassifier(model, out var attr, out var cls)) return null;
        var arr = model.GetAttr(attr);
        if (arr == null) return null;

        var b = model.Bounds;
        double dx = model.Sx > 1e-9 ? model.Sx : 1, dy = model.Sy > 1e-9 ? model.Sy : 1, dz = model.Sz > 1e-9 ? model.Sz : 1;
        int nx = Math.Max(1, (int)Math.Round((b.maxX - b.minX) / dx));
        int ny = Math.Max(1, (int)Math.Round((b.maxY - b.minY) / dy));
        int nz = Math.Max(1, (int)Math.Round((b.maxZ - b.minZ) / dz));
        int ncol = nx * ny;
        var f = new StripRatioField
        {
            Nx = nx, Ny = ny, Dx = dx, Dy = dy, Ox = b.minX, Oy = b.minY, Density = density, TopZ = b.maxZ,
            CoalVol = new double[ncol], WasteVol = new double[ncol],
            CoalThickM = new double[ncol], DepthToCoalM = new double[ncol],
        };
        var topCoalK = new int[ncol];
        for (int c = 0; c < ncol; c++) topCoalK[c] = -1;
        double cellVol = dx * dy * dz;

        int n = Math.Min(model.Blocks.Count, arr.Length);
        for (int idx = 0; idx < n; idx++)
        {
            if ((idx & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (model.DeletedIds.Contains(idx)) continue;
            var blk = model.Blocks[idx];
            int i = Math.Clamp((int)Math.Floor((blk.X - b.minX) / dx), 0, nx - 1);
            int j = Math.Clamp((int)Math.Floor((blk.Y - b.minY) / dy), 0, ny - 1);
            int k = Math.Clamp((int)Math.Floor((blk.Z - b.minZ) / dz), 0, nz - 1);
            int col = i + j * nx;
            double sc = model.CellScale(blk);
            double vol = cellVol * sc * sc * sc;
            double v = arr[idx];
            if (cls.IsCoal(v))
            {
                f.CoalVol[col] += vol;
                f.CoalThickM[col] += dz * sc;
                if (k > topCoalK[col]) topCoalK[col] = k;
            }
            else if (cls.IsRock(v))
            {
                f.WasteVol[col] += vol;
            }
        }

        long coalCols = 0;
        double fullDepth = nz * dz;
        for (int c = 0; c < ncol; c++)
        {
            if (topCoalK[c] >= 0) { coalCols++; f.DepthToCoalM[c] = (nz - 1 - topCoalK[c]) * dz; }
            else f.DepthToCoalM[c] = fullDepth;
        }
        f.CoalColumns = coalCols;
        return f;
    }
}

/// <summary>
/// 从剥采比场生成「拉沟 · 推进」候选（求解链第一环，原 <c>PanelDelineator</c>）。
/// 真值打分：剥采比最小 / 煤层埋深浅 / 工作线长足够 / 运输便利(推进短) / 利于内排(推进长) / 避构造(带内煤连续+剥采比平稳)。
/// 候选 = 南/北/东/西四条近边带 + 剥采比梯度候选；工作线形态按境界弯直度自动推荐。
/// </summary>
public static class ProgramPanelDelineator
{
    public static List<BoxcutAdvanceOption> Recommend(MiningProgramPlan plan, StripRatioField field, PitTopOutline? outline = null)
    {
        var form = FormFromOutline(outline);
        double minWL = plan.MinWorkingLineM <= 0 ? 1 : plan.MinWorkingLineM;
        double lenX = field.Nx * field.Dx;
        double lenY = field.Ny * field.Dy;
        int bandX = Math.Max(1, (int)(field.Nx * 0.2));
        int bandY = Math.Max(1, (int)(field.Ny * 0.2));

        var south = Band(field, 0, field.Nx, 0, bandY);
        var north = Band(field, 0, field.Nx, field.Ny - bandY, field.Ny);
        var west = Band(field, 0, bandX, 0, field.Ny);
        var east = Band(field, field.Nx - bandX, field.Nx, 0, field.Ny);

        var raw = new List<(string name, string side, double sr, double depth, double boxcutLen, double advLen, double az, double cover, double cv)>
        {
            ("南端拉沟·向北推", "南端(min Y)", south.sr, south.depth, lenX, lenY, 0,   south.cover, south.cv),
            ("北端拉沟·向南推", "北端(max Y)", north.sr, north.depth, lenX, lenY, 180, north.cover, north.cv),
            ("西端拉沟·向东推", "西端(min X)", west.sr,  west.depth,  lenY, lenX, 90,  west.cover,  west.cv),
            ("东端拉沟·向西推", "东端(max X)", east.sr,  east.depth,  lenY, lenX, 270, east.cover,  east.cv),
        };

        double srMin = raw.Min(r => r.sr), srMax = raw.Max(r => r.sr);
        double dMin = raw.Min(r => r.depth), dMax = raw.Max(r => r.depth);
        double aMin = raw.Min(r => r.advLen), aMax = raw.Max(r => r.advLen);

        var opts = new List<BoxcutAdvanceOption>();
        foreach (var r in raw)
        {
            var o = new BoxcutAdvanceOption
            {
                Name = r.name, IsAuto = true,
                BoxcutDesc = $"{r.side} 近边带 SR≈{r.sr:0.0}",
                AdvanceAzimuthDeg = r.az, WorkingLineLengthM = r.boxcutLen, AdvanceMode = form,
                SrScore = NormLowerBetter(r.sr, srMin, srMax),
                ShallowScore = NormLowerBetter(r.depth, dMin, dMax),
                HaulScore = NormLowerBetter(r.advLen, aMin, aMax),
                InnerDumpScore = NormHigherBetter(r.advLen, aMin, aMax),
                WorkLineScore = Math.Min(1.0, r.boxcutLen / minWL),
                GeoScore = Math.Clamp(0.3 + 0.5 * r.cover + 0.2 * (1 - Math.Min(1.0, r.cv)), 0, 1),
                Feasible = r.boxcutLen >= minWL,
                Rationale = $"{r.side}近边剥采比 {r.sr:0.0}、埋深 {r.depth:0}m、推进 {r.advLen:0}m、煤覆盖 {r.cover * 100:0}%",
            };
            o.Recompute(plan.BoxcutWeights);
            opts.Add(o);
        }

        var grad = GradientCandidate(field, plan, minWL);
        if (grad != null) { grad.AdvanceMode = form; opts.Add(grad); }

        BoxcutAdvanceOption? best = null;
        foreach (var o in opts)
        {
            o.Recommended = false;
            if (o.Feasible && (best == null || o.TotalScore > best.TotalScore)) best = o;
        }
        if (best != null) best.Recommended = true;
        return opts;
    }

    private static double NormLowerBetter(double v, double lo, double hi)
        => hi > lo + 1e-9 ? Math.Clamp(1.0 - (v - lo) / (hi - lo), 0, 1) : 0.8;
    private static double NormHigherBetter(double v, double lo, double hi)
        => hi > lo + 1e-9 ? Math.Clamp((v - lo) / (hi - lo), 0, 1) : 0.8;

    /// <summary>剥采比梯度候选：全场低/高剥采比加权形心 → 推进=低→高（可斜向），拉沟在低端。</summary>
    private static BoxcutAdvanceOption? GradientCandidate(StripRatioField f, MiningProgramPlan plan, double minWL)
    {
        double srMax = 0;
        for (int j = 0; j < f.Ny; j++)
            for (int i = 0; i < f.Nx; i++)
            { double sr = f.StripRatioAt(i, j); if (!double.IsInfinity(sr) && !double.IsNaN(sr) && sr > srMax) srMax = sr; }
        if (srMax <= 0) return null;

        double sLo = 0, sHi = 0, loX = 0, loY = 0, hiX = 0, hiY = 0;
        for (int j = 0; j < f.Ny; j++)
            for (int i = 0; i < f.Nx; i++)
            {
                double sr = f.StripRatioAt(i, j);
                if (double.IsInfinity(sr) || double.IsNaN(sr)) continue;
                var (cx, cy) = f.Center(i, j);
                double wHi = sr, wLo = (srMax - sr) + 0.01;
                hiX += wHi * cx; hiY += wHi * cy; sHi += wHi;
                loX += wLo * cx; loY += wLo * cy; sLo += wLo;
            }
        if (sHi <= 0 || sLo <= 0) return null;

        double cxLo = loX / sLo, cyLo = loY / sLo, cxHi = hiX / sHi, cyHi = hiY / sHi;
        double vx = cxHi - cxLo, vy = cyHi - cyLo, vlen = Math.Sqrt(vx * vx + vy * vy);
        if (vlen < 1) return null;
        double az = (Math.Atan2(vx, vy) * 180.0 / Math.PI + 360) % 360;
        double lenX = f.Nx * f.Dx, lenY = f.Ny * f.Dy, diag = Math.Sqrt(lenX * lenX + lenY * lenY);
        double boxcutLen = (lenX + lenY) / 2;

        var o = new BoxcutAdvanceOption
        {
            Name = "剥采比梯度·低→高推进", IsAuto = true,
            BoxcutDesc = $"沿剥采比梯度（方位 {az:0}°）",
            AdvanceAzimuthDeg = Math.Round(az, 0), WorkingLineLengthM = Math.Round(boxcutLen, 0), AdvanceMode = AdvanceMode.Parallel,
            SrScore = 0.95, ShallowScore = 0.70,
            HaulScore = Math.Clamp(1 - vlen / diag, 0.3, 0.9),
            InnerDumpScore = Math.Clamp(vlen / diag, 0.3, 0.9),
            WorkLineScore = Math.Min(1.0, boxcutLen / minWL), GeoScore = 0.75,
            Feasible = boxcutLen >= minWL,
            Rationale = $"沿剥采比梯度低→高推进（方位 {az:0}°，可斜向），起于全场最低剥采比区",
        };
        o.Recompute(plan.BoxcutWeights);
        return o;
    }

    /// <summary>近边带聚合：有限剥采比列的均值剥采比/埋深 + 煤覆盖率 + 剥采比变异系数。整带无煤 → 最差占位。</summary>
    public static (double sr, double depth, int cols, double cover, double cv) Band(StripRatioField f, int iLo, int iHi, int jLo, int jHi)
    {
        double sumSr = 0, sumSr2 = 0, sumD = 0; int n = 0, total = 0;
        int j0 = Math.Max(0, jLo), j1 = Math.Min(f.Ny, jHi);
        int i0 = Math.Max(0, iLo), i1 = Math.Min(f.Nx, iHi);
        for (int j = j0; j < j1; j++)
            for (int i = i0; i < i1; i++)
            {
                total++;
                double sr = f.StripRatioAt(i, j);
                if (double.IsInfinity(sr) || double.IsNaN(sr)) continue;
                sumSr += sr; sumSr2 += sr * sr; sumD += f.DepthToCoalM[f.Idx(i, j)]; n++;
            }
        if (n == 0) return (999.0, 9999.0, 0, 0.0, 1.0);
        double mean = sumSr / n;
        double varc = Math.Max(0, sumSr2 / n - mean * mean);
        double cv = mean > 1e-9 ? Math.Sqrt(varc) / mean : 0;
        double cover = total > 0 ? (double)n / total : 0;
        return (mean, sumD / n, n, cover, cv);
    }

    /// <summary>按境界整体形状(面积占外接框比)推荐工作线形态：接近矩形→平推；中等收口→定点回转；强收口/三角→动点回转。</summary>
    public static AdvanceMode FormFromOutline(PitTopOutline? o)
    {
        if (o is not { Count: >= 4 }) return AdvanceMode.Parallel;
        int n = o.Count;
        double xmn = double.MaxValue, xmx = double.MinValue, ymn = double.MaxValue, ymx = double.MinValue, area2 = 0;
        for (int i = 0; i < n; i++)
        {
            xmn = Math.Min(xmn, o.X[i]); xmx = Math.Max(xmx, o.X[i]);
            ymn = Math.Min(ymn, o.Y[i]); ymx = Math.Max(ymx, o.Y[i]);
            int j = (i + 1) % n;
            area2 += o.X[i] * o.Y[j] - o.X[j] * o.Y[i];
        }
        double bbox = (xmx - xmn) * (ymx - ymn);
        if (bbox <= 1e-6) return AdvanceMode.Parallel;
        double extent = Math.Abs(area2) / 2 / bbox;
        return extent > 0.9 ? AdvanceMode.Parallel : extent > 0.7 ? AdvanceMode.FixedPivot : AdvanceMode.MovingPivot;
    }

    /// <summary>取境界轮廓（地表界/底周界，块体足迹兜底）——用于工作线形态推荐。</summary>
    public static PitTopOutline? ResolveOutline(MiningProgramPlan plan, IPlanEntityHost? host)
    {
        long bnd = plan.SourcePitResult is { } pr ? (pr.SurfaceBoundaryHandle != 0 ? pr.SurfaceBoundaryHandle : pr.BottomPerimeterHandle) : 0;
        return PitSchemeEnvelope.ResolveTopOutline(host, bnd, host?.ActiveBlockModel);
    }
}

/// <summary>
/// 用选定的「拉沟·推进」沿推进轴把剥采比场切成 N 个储量均衡采区（求解链第二环，原 <c>PanelSplitter</c>）。
/// 推进沿 ±X/±Y → 采区是垂直推进的条带，按等煤量切分（FixedN 按等推进距离）。
/// </summary>
public static class ProgramPanelSplitter
{
    public static List<MiningPanel> Split(MiningProgramPlan plan, StripRatioField field, BoxcutAdvanceOption boxcut)
    {
        var panels = new List<MiningPanel>();
        double az = ((boxcut.AdvanceAzimuthDeg % 360) + 360) % 360;
        double a180 = az % 180;
        bool axisIsY = a180 < 45 || a180 >= 135;
        bool forwardPos = axisIsY ? (az < 90 || az > 270) : (az > 0 && az < 180);

        int slabCount = axisIsY ? field.Ny : field.Nx;
        if (slabCount <= 0) return panels;

        var coal = new double[slabCount];
        var waste = new double[slabCount];
        for (int s = 0; s < slabCount; s++)
        {
            double cv = 0, wv = 0;
            if (axisIsY) { for (int i = 0; i < field.Nx; i++) { int c = field.Idx(i, s); cv += field.CoalVol[c]; wv += field.WasteVol[c]; } }
            else { for (int j = 0; j < field.Ny; j++) { int c = field.Idx(s, j); cv += field.CoalVol[c]; wv += field.WasteVol[c]; } }
            coal[s] = cv; waste[s] = wv;
        }

        var seq = Enumerable.Range(0, slabCount);
        int[] order = (forwardPos ? seq : seq.Reverse()).ToArray();

        double rho = field.Density;
        double totalCoalVol = coal.Sum();
        double totalCoalWanT = totalCoalVol * rho / 1e4;
        double annualCap = plan.TargetCapacityWanTa > 0 ? plan.TargetCapacityWanTa : Math.Max(1, totalCoalWanT / 30);
        double L = boxcut.WorkingLineLengthM > 0 ? boxcut.WorkingLineLengthM : (axisIsY ? field.Nx * field.Dx : field.Ny * field.Dy);
        double H = plan.BenchHeightM > 0 ? plan.BenchHeightM : 12;
        double advRate = plan.TargetAdvanceRateMpa > 0 ? plan.TargetAdvanceRateMpa
                                                       : MiningProgramPlan.AdvanceRateFrom(annualCap, L, H, rho);
        double tSwitch = (plan.InnerDumpEnabled && advRate > 0) ? plan.InnerDumpStartWidthM / advRate : double.PositiveInfinity;

        int n = DecidePanelCount(plan, totalCoalWanT, annualCap);
        bool byArea = plan.Split == SplitObjective.FixedN;
        double totalMetric = byArea ? slabCount : totalCoalVol;
        double target = totalMetric / n;
        double acc = 0, panelCoal = 0, panelWaste = 0, cumLife = 0;
        int placed = 0, panelNo = 1, sMin = int.MaxValue, sMax = int.MinValue;

        for (int t = 0; t < order.Length; t++)
        {
            int s = order[t];
            if (s < sMin) sMin = s;
            if (s > sMax) sMax = s;
            panelCoal += coal[s]; panelWaste += waste[s]; acc += byArea ? 1.0 : coal[s];
            bool last = t == order.Length - 1;
            bool cut = placed < n - 1 && acc >= target * (placed + 1) - 1e-9;
            if (!cut && !last) continue;

            double coalWanT = panelCoal * rho / 1e4;
            double wasteWanM3 = panelWaste / 1e4;
            double life = annualCap > 0 ? coalWanT / annualCap : 0;
            double startTime = cumLife;
            bool internalDump = plan.InnerDumpEnabled && panelNo > 1;

            double pMinX, pMinY, pMaxX, pMaxY;
            if (axisIsY)
            {
                pMinX = field.Ox; pMaxX = field.Ox + field.Nx * field.Dx;
                pMinY = field.Oy + sMin * field.Dy; pMaxY = field.Oy + (sMax + 1) * field.Dy;
            }
            else
            {
                pMinY = field.Oy; pMaxY = field.Oy + field.Ny * field.Dy;
                pMinX = field.Ox + sMin * field.Dx; pMaxX = field.Ox + (sMax + 1) * field.Dx;
            }

            panels.Add(new MiningPanel
            {
                Index = panelNo, Name = $"采区{panelNo}", Order = panelNo,
                CoalWanT = Math.Round(coalWanT, 0), WasteWanM3 = Math.Round(wasteWanM3, 0),
                StripRatio = coalWanT > 1e-9 ? Math.Round(wasteWanM3 / coalWanT, 2) : 0,
                AdvanceAzimuthDeg = boxcut.AdvanceAzimuthDeg,
                WorkingLineLengthM = Math.Round(L, 0), AdvanceRateMpa = Math.Round(advRate, 0),
                Dump = internalDump ? DumpMode.Internal : DumpMode.External,
                DumpSwitchYear = internalDump ? Math.Round(Math.Max(startTime, double.IsInfinity(tSwitch) ? startTime : tSwitch), 1) : 0,
                ServiceLifeYears = Math.Round(life, 1),
                MinX = pMinX, MinY = pMinY, MaxX = pMaxX, MaxY = pMaxY,
            });

            cumLife += life; panelNo++; placed++;
            panelCoal = 0; panelWaste = 0; sMin = int.MaxValue; sMax = int.MinValue;
        }
        return panels;
    }

    /// <summary>按取向定采区数：FixedN=用户给定；ByLife/ByCapacity=随实际储量自适应。</summary>
    public static int DecidePanelCount(MiningProgramPlan plan, double totalCoalWanT, double annualCap)
    {
        int suggested = Math.Max(1, plan.PanelCount);
        if (plan.Split == SplitObjective.FixedN) return suggested;
        double perPanelLife = (plan.TargetServiceLifeYears > 0 && suggested > 0) ? plan.TargetServiceLifeYears / suggested : 5;
        if (perPanelLife < 1) perPanelLife = 1;
        int n;
        if (plan.Split == SplitObjective.ByCapacity)
            n = (int)Math.Round(totalCoalWanT / Math.Max(1, annualCap * perPanelLife));
        else
            n = (int)Math.Round((annualCap > 0 ? totalCoalWanT / annualCap : 0) / perPanelLife);
        return Math.Clamp(n, 1, 16);
    }
}

/// <summary>从采区序算开采程序系统指标 ProgramResult（求解链第二环·评价，原 <c>ProgramEvaluator</c>）。</summary>
public static class ProgramPlanEvaluator
{
    public static ProgramResult Evaluate(MiningProgramPlan plan)
    {
        var ps = plan.Panels;
        var r = new ProgramResult { PanelCount = ps.Count };
        if (ps.Count == 0) { r.Ok = false; return r; }

        double totalCoal = ps.Sum(p => p.CoalWanT);
        double totalWaste = ps.Sum(p => p.WasteWanM3);
        double annualCap = plan.TargetCapacityWanTa > 0 ? plan.TargetCapacityWanTa : Math.Max(1, totalCoal / 30);

        r.ServiceLifeYears = Math.Round(ps.Sum(p => p.ServiceLifeYears), 1);
        r.ProductionRatioPeak = Math.Round(ps.Max(p => p.StripRatio), 2);

        double mean = totalCoal / ps.Count;
        double variance = ps.Sum(p => (p.CoalWanT - mean) * (p.CoalWanT - mean)) / ps.Count;
        double cv = mean > 1e-9 ? Math.Sqrt(variance) / mean : 0;
        r.ReserveBalanceCoef = Math.Round(Math.Clamp(1 - cv, 0, 1), 2);

        var first = ps.OrderBy(p => p.Order).First();
        r.BasicStrippingYiM3 = Math.Round(first.WasteWanM3 / 1e4, 2);

        const double swell = 1.2;
        double rho = MiningProgramPlan.DefaultCoalDensity;
        double availVoid = 0, internalPlaced = 0;
        foreach (var p in ps.OrderBy(z => z.Order))
        {
            if (p.Dump == DumpMode.Internal)
            {
                double need = p.WasteWanM3 * swell;
                double placed = Math.Min(need, availVoid);
                internalPlaced += placed / swell;
                availVoid -= placed;
            }
            availVoid += p.CoalWanT / rho + p.WasteWanM3;
        }
        r.InnerDumpPct = totalWaste > 1e-9 ? Math.Round(internalPlaced / totalWaste * 100, 0) : 0;

        double avgStrip = totalCoal > 1e-9 ? totalWaste / totalCoal : 0;
        double annualWaste = annualCap * avgStrip;
        r.TimeToCapacityYears = Math.Round(Math.Clamp(annualWaste > 1e-9 ? first.WasteWanM3 / annualWaste : 1, 1, Math.Max(1, r.ServiceLifeYears)), 0);

        r.AvgHaulKm = AvgHaulKm(plan);
        r.Npv = Npv(plan, totalCoal, totalWaste, annualCap);

        double advRate = first.AdvanceRateMpa;
        bool advOk = plan.MaxAdvanceRateMpa <= 0 || advRate <= plan.MaxAdvanceRateMpa + 1e-6;
        bool wlOk = ps.All(p => p.WorkingLineLengthM >= plan.MinWorkingLineM - 1e-6);
        r.Ok = advOk && wlOk;
        r.CompositeScore = 0;
        return r;
    }

    /// <summary>平均运距 ≈ 各采区中心到首采区拉沟边的沿推进距离（煤量加权），km。</summary>
    public static double AvgHaulKm(MiningProgramPlan plan)
    {
        var ps = plan.Panels;
        if (ps.Count == 0 || plan.SelectedBoxcut == null) return 0;
        double az = ((plan.SelectedBoxcut.AdvanceAzimuthDeg % 360) + 360) % 360;
        double a180 = az % 180;
        bool axisIsY = a180 < 45 || a180 >= 135;
        bool fwd = axisIsY ? (az < 90 || az > 270) : (az > 0 && az < 180);
        var first = ps.OrderBy(p => p.Order).First();
        double box = axisIsY ? (fwd ? first.MinY : first.MaxY) : (fwd ? first.MinX : first.MaxX);
        double total = ps.Sum(p => p.CoalWanT);
        if (total <= 0) return 0;
        double acc = 0;
        foreach (var p in ps)
        {
            double cen = axisIsY ? (p.MinY + p.MaxY) / 2 : (p.MinX + p.MaxX) / 2;
            acc += p.CoalWanT * Math.Abs(cen - box);
        }
        return Math.Round(acc / total / 1000.0, 2);
    }

    /// <summary>NPV：本功能内自算分期现金流。基建剥离=年0资本，生产剥离按年折现，内排省 30% 运费。</summary>
    public static double Npv(MiningProgramPlan plan, double totalCoalWanT, double totalWasteWanM3, double annualCap)
    {
        if (annualCap <= 0 || totalCoalWanT <= 0) return 0;
        double d = plan.CoalPriceYuanT, a = plan.MiningCostYuanT, b = plan.StripCostYuanM3;
        double rate = Math.Max(0, plan.DiscountRatePct) / 100.0;
        double internalWaste = plan.Panels.Where(p => p.Dump == DumpMode.Internal).Sum(p => p.WasteWanM3);
        double innerPct = totalWasteWanM3 > 1e-9 ? internalWaste / totalWasteWanM3 : 0;
        double stripFactor = 1 - innerPct * 0.3;
        double basicWaste = plan.Panels.OrderBy(p => p.Order).FirstOrDefault()?.WasteWanM3 ?? 0;
        double opWaste = Math.Max(0, totalWasteWanM3 - basicWaste);
        int life = Math.Max(1, (int)Math.Ceiling(totalCoalWanT / annualCap));
        double annualRevenue = annualCap * (d - a);
        double annualStrip = (opWaste / life) * b * stripFactor;
        double annualNet = annualRevenue - annualStrip;
        double npv = -basicWaste * b * stripFactor;
        for (int t = 1; t <= life; t++) npv += annualNet / Math.Pow(1 + rate, t);
        return Math.Round(npv, 0);
    }
}

/// <summary>级2：整方案比选评分（求解链第三环，原 <c>ProgramComparer</c>）。硬约束门槛 + 方向感知归一 + 多准则加权 → 综合评分/推荐。</summary>
public static class ProgramPlanComparer
{
    public const double WPeak = 0.22, WBasic = 0.15, WInner = 0.15, WTtc = 0.10, WLife = 0.10, WBalance = 0.18, WNpv = 0.10;

    /// <summary>对已求解方案打分回填 Result.CompositeScore；返回推荐（最高分且可行）方案名。</summary>
    public static string Score(IReadOnlyList<MiningProgramPlan> plans)
    {
        var withR = plans.Where(p => p.Result != null).ToList();
        if (withR.Count == 0) return "—";

        double[] peak = withR.Select(p => p.Result!.ProductionRatioPeak).ToArray();
        double[] basic = withR.Select(p => p.Result!.BasicStrippingYiM3).ToArray();
        double[] inner = withR.Select(p => p.Result!.InnerDumpPct).ToArray();
        double[] ttc = withR.Select(p => p.Result!.TimeToCapacityYears).ToArray();
        double[] life = withR.Select(p => p.Result!.ServiceLifeYears).ToArray();
        double[] bal = withR.Select(p => p.Result!.ReserveBalanceCoef).ToArray();
        double[] npv = withR.Select(p => p.Result!.Npv).ToArray();

        double wsum = WPeak + WBasic + WInner + WTtc + WLife + WBalance + WNpv;
        MiningProgramPlan? best = null; double bestScore = -1;

        for (int i = 0; i < withR.Count; i++)
        {
            double s = NormLow(peak, i) * WPeak + NormLow(basic, i) * WBasic + NormHigh(inner, i) * WInner
                     + NormLow(ttc, i) * WTtc + NormHigh(life, i) * WLife + NormHigh(bal, i) * WBalance
                     + NormHigh(npv, i) * WNpv;
            double score = s / wsum * 100;
            withR[i].Result!.CompositeScore = Math.Round(score, 0);
            if (withR[i].Result!.Ok && score > bestScore) { bestScore = score; best = withR[i]; }
        }
        if (best == null)
            foreach (var p in withR)
                if (p.Result!.CompositeScore > bestScore) { bestScore = p.Result!.CompositeScore; best = p; }

        return best?.Name ?? "—";
    }

    private static double NormHigh(double[] a, int i)
    { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NormLow(double[] a, int i)
    { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }
}

/// <summary>由一个基准开采程序方案，按「工作线推进方式」派生多套方案（平行 / 定点回转 / 动点回转）（原 <c>MiningProgramGenerator</c>）。</summary>
public static class MiningProgramGenerator
{
    public static ObservableCollection<MiningProgramPlan> GenerateAdvanceVariants(MiningProgramPlan basePlan)
    {
        var list = new ObservableCollection<MiningProgramPlan>();
        foreach (var mode in new[] { AdvanceMode.Parallel, AdvanceMode.FixedPivot, AdvanceMode.MovingPivot })
        {
            var p = basePlan.Clone();
            p.Name = $"{basePlan.Name} · {ModeName(mode)}";
            if (basePlan.SelectedBoxcut is { } sb)
            {
                var o = sb.Copy();
                o.AdvanceMode = mode;
                o.Name = $"{sb.Name} · {ModeName(mode)}";
                p.BoxcutOptions.Add(o);
                p.SelectedBoxcut = o;
            }
            list.Add(p);
        }
        return list;
    }

    public static string ModeName(AdvanceMode m) => m switch
    {
        AdvanceMode.Parallel => "平行推进", AdvanceMode.FixedPivot => "定点回转", AdvanceMode.MovingPivot => "动点回转", _ => "推进"
    };
}

/// <summary>
/// 段④ 落地（原 <c>ProgramMaterializer</c>）：采区边界 / 拉沟线 / 推进箭头 → 图纸实体。
/// 有境界来源时用 Sutherland-Hodgman 把境界多段线裁到每个采区的矩形窗口里 → 贴合境界真形状；无境界则退化矩形足迹。
/// 按方案图层「采区_&lt;方案名&gt;」幂等（先删后建）；落地后回填 Result.EntityHandles。
/// </summary>
public static class ProgramMaterializer
{
    public static MaterializeOutcome Materialize(MiningProgramPlan plan, IPlanEntityHost? host)
    {
        if (host == null) return MaterializeOutcome.Fail("实体能力不可用");
        var ps = plan.Panels;
        if (ps.Count == 0) return MaterializeOutcome.Fail("请先「求解 / 一键划分」出采区再确定");
        bool hasGeom = ps.Any(p => p.MaxX > p.MinX + 1e-6 && p.MaxY > p.MinY + 1e-6);
        if (!hasGeom) return MaterializeOutcome.Fail("采区无平面几何：请先点「求解 / 一键划分」从激活块体真切采区，再确定落地");

        double z = plan.Result?.DrawZ ?? 0;
        string layer = "采区_" + PitMaterializer.Sanitize(plan.Name);

        long bnd = plan.SourcePitResult is { } pr ? (pr.SurfaceBoundaryHandle != 0 ? pr.SurfaceBoundaryHandle : pr.BottomPerimeterHandle) : 0;
        var outline = PitSchemeEnvelope.ResolveTopOutline(host, bnd, host.ActiveBlockModel);
        bool clip = outline is { Count: >= 3 };

        try { var old = host.GetHandlesByLayer(layer); if (old.Length > 0) host.DeleteEntities(old); } catch { }

        var batch = new PlanEntityBatch { Layer = layer, LayerColor = (20, 184, 166) };
        (byte, byte, byte) teal = (20, 150, 136), green = (34, 197, 94), orange = (234, 88, 12), gray = (90, 90, 90), label = (15, 118, 110);

        int drawn = 0;
        foreach (var p in ps)
        {
            List<(double x, double y)> poly = clip
                ? ClipOutlineToRect(outline!, p.MinX, p.MinY, p.MaxX, p.MaxY)
                : new() { (p.MinX, p.MinY), (p.MaxX, p.MinY), (p.MaxX, p.MaxY), (p.MinX, p.MaxY) };
            if (poly.Count < 3) continue;
            var col = p.IsFirst ? green : teal;
            batch.Rings.Add((poly.Select(q => q.x).ToArray(), poly.Select(q => q.y).ToArray(), z, col.Item1, col.Item2, col.Item3));
            double cx = poly.Average(q => q.x), cy = poly.Average(q => q.y);
            double th = Math.Max(10, Math.Min(p.MaxX - p.MinX, p.MaxY - p.MinY) * 0.08);
            // 原 WriteText(hAlign=1 中, vAlign=2 中)：Kylin VAlign 编号差 1（原 2(Middle) → Kylin 1）
            batch.Texts.Add((cx, cy, z, th, p.IsFirst ? $"①{p.Name}·首采" : p.Name, 1, 1, label.Item1, label.Item2, label.Item3));
            drawn++;
        }
        if (drawn == 0) return MaterializeOutcome.Fail("采区均落在境界外，无可落地几何（检查境界来源）");

        var first = ps.OrderBy(p => p.Order).First();
        double az = (((plan.SelectedBoxcut?.AdvanceAzimuthDeg ?? first.AdvanceAzimuthDeg) % 360) + 360) % 360;
        double a180 = az % 180;
        bool axisIsY = a180 < 45 || a180 >= 135;
        bool forwardPos = axisIsY ? (az < 90 || az > 270) : (az > 0 && az < 180);
        DrawBoxcutAndArrow(batch, first, axisIsY, forwardPos, z, orange, gray);

        long[] handles;
        try { handles = host.Import(batch); }
        catch (Exception ex) { return MaterializeOutcome.Fail($"落地失败：{ex.Message}"); }
        if (handles.Length == 0) return MaterializeOutcome.Fail("引擎导入失败");
        if (plan.Result != null) plan.Result.EntityHandles = handles.ToList();

        string shape = clip ? (outline!.FromSurfaceLimit ? "贴境界(地表界)" : "贴块体足迹") : "矩形足迹";
        return new MaterializeOutcome
        {
            Ok = true, Levels = drawn,
            Message = $"已在图层「{layer}」落地 {drawn} 个采区（{shape}）+ 拉沟 + 推进箭头（首采区高亮）",
        };
    }

    /// <summary>Sutherland-Hodgman：把境界多边形裁到矩形窗口（4 个半平面）。矩形是凸的 → 对非凸境界也正确。</summary>
    public static List<(double x, double y)> ClipOutlineToRect(PitTopOutline o, double xmin, double ymin, double xmax, double ymax)
    {
        var poly = new List<(double x, double y)>(o.Count);
        for (int i = 0; i < o.Count; i++) poly.Add((o.X[i], o.Y[i]));
        poly = ClipHalf(poly, p => p.x >= xmin, (a, b) => Ix(a, b, xmin));
        poly = ClipHalf(poly, p => p.x <= xmax, (a, b) => Ix(a, b, xmax));
        poly = ClipHalf(poly, p => p.y >= ymin, (a, b) => Iy(a, b, ymin));
        poly = ClipHalf(poly, p => p.y <= ymax, (a, b) => Iy(a, b, ymax));
        return poly;
    }
    private static List<(double x, double y)> ClipHalf(List<(double x, double y)> poly,
        Func<(double x, double y), bool> inside, Func<(double x, double y), (double x, double y), (double x, double y)> isect)
    {
        var outp = new List<(double x, double y)>();
        int n = poly.Count;
        if (n == 0) return outp;
        for (int i = 0; i < n; i++)
        {
            var cur = poly[i]; var prev = poly[(i + n - 1) % n];
            bool ci = inside(cur), pi = inside(prev);
            if (ci) { if (!pi) outp.Add(isect(prev, cur)); outp.Add(cur); }
            else if (pi) outp.Add(isect(prev, cur));
        }
        return outp;
    }
    private static (double x, double y) Ix((double x, double y) a, (double x, double y) b, double xc)
    { double dx = b.x - a.x; if (Math.Abs(dx) < 1e-12) return (xc, a.y); double t = (xc - a.x) / dx; return (xc, a.y + t * (b.y - a.y)); }
    private static (double x, double y) Iy((double x, double y) a, (double x, double y) b, double yc)
    { double dy = b.y - a.y; if (Math.Abs(dy) < 1e-12) return (a.x, yc); double t = (yc - a.y) / dy; return (a.x + t * (b.x - a.x), yc); }

    /// <summary>首采区起始边画拉沟（垂直推进），中点沿推进方向画箭头。</summary>
    private static void DrawBoxcutAndArrow(PlanEntityBatch w, MiningPanel first, bool axisIsY, bool forwardPos, double z,
        (byte r, byte g, byte b) boxcut, (byte r, byte g, byte b) arrow)
    {
        double bx0, by0, bx1, by1, ax, ay, dx, dy;
        if (axisIsY)
        {
            double y = forwardPos ? first.MinY : first.MaxY;
            bx0 = first.MinX; by0 = y; bx1 = first.MaxX; by1 = y;
            ax = (first.MinX + first.MaxX) / 2; ay = y; dx = 0; dy = forwardPos ? 1 : -1;
        }
        else
        {
            double x = forwardPos ? first.MinX : first.MaxX;
            bx0 = x; by0 = first.MinY; bx1 = x; by1 = first.MaxY;
            ax = x; ay = (first.MinY + first.MaxY) / 2; dx = forwardPos ? 1 : -1; dy = 0;
        }
        w.Lines.Add((bx0, by0, z, bx1, by1, z, boxcut.r, boxcut.g, boxcut.b));

        double depth = axisIsY ? (first.MaxY - first.MinY) : (first.MaxX - first.MinX);
        double len = Math.Max(20, depth * 0.5);
        double tx = ax + dx * len, ty = ay + dy * len;
        w.Lines.Add((ax, ay, z, tx, ty, z, arrow.r, arrow.g, arrow.b));
        double head = len * 0.18, px = -dy, py = dx;
        w.Lines.Add((tx, ty, z, tx - dx * head + px * head * 0.6, ty - dy * head + py * head * 0.6, z, arrow.r, arrow.g, arrow.b));
        w.Lines.Add((tx, ty, z, tx - dx * head - px * head * 0.6, ty - dy * head - py * head * 0.6, z, arrow.r, arrow.g, arrow.b));
    }
}
