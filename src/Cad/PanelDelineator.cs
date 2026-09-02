using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

// ─────────────────────────────────────────────────────────────────────────────
// 拉沟·推进候选推荐(PanelDelineator)——忠实移植原 PlanLib.BoundaryOptimization.PanelDelineator。
// 从剥采比场自动推荐【首采区拉沟位置 + 推进方位】(求解链第一环)。Kylin 既有 PanelSplit 只做几何切分
// (需用户指定推进方位); 此层按 剥采比/埋深/运输/内排/工作线/地质 六约束打分, 自动推荐最优拉沟推进方案。
// 纯几何(只读剥采比场)、可单测。原 ResolveOutline(取境界线)依赖引擎, 不移(境界作输入)。
// ─────────────────────────────────────────────────────────────────────────────

// AdvanceMode { Parallel, FixedPivot, MovingPivot } 复用既有 AdvancePlanner.cs 定义(工作线推进几何)。

/// <summary>初始拉沟位置选择约束权重。忠实原 BoxcutWeights(默认值照搬)。</summary>
public sealed class BoxcutWeights
{
    public double StripRatio { get; set; } = 0.30;  // 剥采比最小
    public double Shallow { get; set; } = 0.15;     // 煤层埋藏浅 / 出露
    public double Haul { get; set; } = 0.20;        // 便于开拓运输 / 运距短
    public double InnerDump { get; set; } = 0.15;   // 利于尽早内排
    public double WorkLine { get; set; } = 0.10;    // 工作线长度足够
    public double Geology { get; set; } = 0.10;     // 避开断层/构造/地形

    public double Sum => StripRatio + Shallow + Haul + InnerDump + WorkLine + Geology;
    public static BoxcutWeights CreateDefault() => new();
}

/// <summary>一个拉沟·推进候选(带逐约束打分 + 加权综合)。忠实原 BoxcutAdvanceOption。</summary>
public sealed class BoxcutAdvanceOption
{
    public string Name { get; set; } = "拉沟方案1";
    public bool IsAuto { get; set; } = true;
    public string BoxcutDesc { get; set; } = "";
    public double AdvanceAzimuthDeg { get; set; }
    public double WorkingLineLengthM { get; set; }
    public AdvanceMode AdvanceMode { get; set; } = AdvanceMode.Parallel;
    public double SrScore { get; set; }        // 剥采比最小
    public double ShallowScore { get; set; }   // 煤层埋藏浅
    public double HaulScore { get; set; }      // 便于开拓运输
    public double InnerDumpScore { get; set; } // 利于内排
    public double WorkLineScore { get; set; }  // 工作线长足够
    public double GeoScore { get; set; }       // 避开构造/地形
    public double TotalScore { get; set; }     // 加权综合 (0..100)
    public bool Feasible { get; set; } = true;
    public bool Recommended { get; set; }
    public string Rationale { get; set; } = "";

    /// <summary>按权重重算综合得分 (0..100)。忠实原 Recompute。</summary>
    public void Recompute(BoxcutWeights w)
    {
        double s = SrScore * w.StripRatio + ShallowScore * w.Shallow + HaulScore * w.Haul
                 + InnerDumpScore * w.InnerDump + WorkLineScore * w.WorkLine + GeoScore * w.Geology;
        double sum = w.Sum <= 0 ? 1 : w.Sum;
        TotalScore = s / sum * 100.0;
    }
}

/// <summary>境界顶口轮廓(工作线形态推荐用)。忠实原 TopOutline 的几何部分。</summary>
public sealed class TopOutline
{
    public List<double> X = new();
    public List<double> Y = new();
    public int Count => X.Count;
}

/// <summary>
/// 从剥采比场生成「拉沟·推进」候选(求解链第一环)。忠实移植原 PlanLib.BoundaryOptimization.PanelDelineator。
/// 真值打分(来自块体场几何): 剥采比最小 / 煤层埋深浅 / 工作线长足够 / 运输便利(推进短) / 利于内排(推进长) /
/// 避构造(带内煤连续+剥采比平稳)。推进方位: 背向低剥采比边、朝高剥采比内部(削峰 + 为内排腾空间)。
/// MVP: 候选 = 4 条边(南/北/东/西)近边带 + 剥采比梯度候选。
/// </summary>
public static class PanelDelineator
{
    /// <summary>推荐拉沟·推进候选(按 weights 打分排序; 标记最高分为 Recommended)。</summary>
    public static List<BoxcutAdvanceOption> Recommend(StripRatioField field, double minWorkingLineM,
        BoxcutWeights? weights = null, TopOutline? outline = null)
    {
        var w = weights ?? BoxcutWeights.CreateDefault();
        var form = FormFromOutline(outline);
        double minWL = minWorkingLineM <= 0 ? 1 : minWorkingLineM;
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
                Name = r.name,
                IsAuto = true,
                BoxcutDesc = $"{r.side} 近边带 SR≈{r.sr:0.0}",
                AdvanceAzimuthDeg = r.az,
                WorkingLineLengthM = r.boxcutLen,
                AdvanceMode = form,
                SrScore = NormLowerBetter(r.sr, srMin, srMax),
                ShallowScore = NormLowerBetter(r.depth, dMin, dMax),
                HaulScore = NormLowerBetter(r.advLen, aMin, aMax),         // 推进越短 → 运距越小 → 越高
                InnerDumpScore = NormHigherBetter(r.advLen, aMin, aMax),   // 推进越长 → 采空越足 → 越高
                WorkLineScore = Math.Min(1.0, r.boxcutLen / minWL),
                GeoScore = Math.Clamp(0.3 + 0.5 * r.cover + 0.2 * (1 - Math.Min(1.0, r.cv)), 0, 1),
                Feasible = r.boxcutLen >= minWL,
                Rationale = $"{r.side}近边剥采比 {r.sr:0.0}、埋深 {r.depth:0}m、推进 {r.advLen:0}m、煤覆盖 {r.cover * 100:0}%",
            };
            o.Recompute(w);
            opts.Add(o);
        }

        var grad = GradientCandidate(field, minWL, w);
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

    /// <summary>剥采比梯度候选:全场低/高剥采比加权形心 → 推进=低→高(可斜向), 拉沟在低端。</summary>
    private static BoxcutAdvanceOption? GradientCandidate(StripRatioField f, double minWL, BoxcutWeights w)
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
        double az = (Math.Atan2(vx, vy) * 180.0 / Math.PI + 360) % 360;   // 0=N(+Y), 90=E(+X)
        double lenX = f.Nx * f.Dx, lenY = f.Ny * f.Dy, diag = Math.Sqrt(lenX * lenX + lenY * lenY);
        double boxcutLen = (lenX + lenY) / 2;

        var o = new BoxcutAdvanceOption
        {
            Name = "剥采比梯度·低→高推进",
            IsAuto = true,
            BoxcutDesc = $"沿剥采比梯度(方位 {az:0}°)",
            AdvanceAzimuthDeg = Math.Round(az, 0),
            WorkingLineLengthM = Math.Round(boxcutLen, 0),
            AdvanceMode = AdvanceMode.Parallel,
            SrScore = 0.95,
            ShallowScore = 0.70,
            HaulScore = Math.Clamp(1 - vlen / diag, 0.3, 0.9),
            InnerDumpScore = Math.Clamp(vlen / diag, 0.3, 0.9),
            WorkLineScore = Math.Min(1.0, boxcutLen / minWL),
            GeoScore = 0.75,
            Feasible = boxcutLen >= minWL,
            Rationale = $"沿剥采比梯度低→高推进(方位 {az:0}°, 可斜向), 起于全场最低剥采比区",
        };
        o.Recompute(w);
        return o;
    }

    /// <summary>近边带聚合:有限剥采比列的均值剥采比/埋深 + 煤覆盖率 + 剥采比变异系数。整带无煤 → 最差占位。</summary>
    private static (double sr, double depth, int cols, double cover, double cv) Band(StripRatioField f, int iLo, int iHi, int jLo, int jHi)
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

    /// <summary>按境界整体形状(面积占外接框比 extent)推荐工作线形态。忠实原 FormFromOutline。</summary>
    public static AdvanceMode FormFromOutline(TopOutline? o)
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
        double extent = Math.Abs(area2) / 2 / bbox;   // 1=填满外接框(矩形), 越小越收口
        return extent > 0.9 ? AdvanceMode.Parallel : extent > 0.7 ? AdvanceMode.FixedPivot : AdvanceMode.MovingPivot;
    }
}
