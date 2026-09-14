using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「驱动量」把斜面变成块体约束 + 分阶段着色的判据工厂（忠实原 <c>InclineConstraint</c>；块体侧改 Kylin <see cref="BlockModelMeta"/>）。
/// 约束目标：块体【只保留显示采出区的煤】，岩石、前方未采储量、工作线后方（已采侧）全部挖除，且按阶段分两色：
///   · 阶段1(现状↔前界① d)   —— s∈[0, d]
///   · 阶段2(前界①↔前界② d_seam) —— s∈(d, d_seam]（逐层 d_seam；无 Stage2 时退化为只有阶段1）
///
/// 趟A：<see cref="BuildCoalPredicate"/> 出判煤谓词 → ApplyKeep 整块删岩（只留煤）。
/// 趟B：<see cref="InclineStageField"/>.StageAt≠0 当几何 keepFn → ApplyKeep 挖除采出区外的煤。
/// 着色：逐块用 StageAt 写「驱动阶段」码(1/2)，配两色分类离散着色。
/// 【登记的差异】Kylin 块体是单元表、无八叉树：原版「前界处递归细分保留侧」在此不存在，跨前界的块按块心归属整块保留/删除。
/// </summary>
public static class InclineConstraint
{
    public const string StageAttr = "驱动阶段";
    public static readonly (byte r, byte g, byte b) Stage1Color = (0xD9, 0x82, 0x2B);   // 阶段1 琥珀
    public static readonly (byte r, byte g, byte b) Stage2Color = (0x3F, 0xA8, 0x7F);   // 阶段2 青绿

    /// <summary>趟A「只留煤」用：逐块判煤谓词（keep=是煤）。判煤规则与 InclineVolumeEngine 数煤【同一份】。</summary>
    public static Func<int, bool> BuildCoalPredicate(InclineBlockSource src, IReadOnlyList<SeamSurfaces> seams, int coalMode, double coalThreshold)
    {
        var rules = InclineVolumeEngine.BuildSeamRules(src, seams, coalMode, coalThreshold);
        int M = rules.Length;
        var arr = new double[M][];
        for (int m = 0; m < M; m++) arr[m] = src.AttrArray(seams[m].Attribute) ?? Array.Empty<double>();
        return idx =>
        {
            for (int m = 0; m < M; m++)
                if (idx < arr[m].Length && InclineVolumeEngine.Hit(rules[m], arr[m][idx])) return true;
            return false;
        };
    }

    public sealed class ApplyResult
    {
        public int RockDeleted, CoalDeleted;
        public long Live;
        public string ModeNote = "";
        public int RestoredPrevious;
    }

    /// <summary>
    /// 斜面约束块体：把 Stage1/2 前界做成 keepFn 切块体。重跑前先精确回退上次模板约束（幂等不叠加），
    /// 本次记录存 <see cref="BlockModelMeta.LastInclineUndo"/>（勾掉约束重跑=撤销）。不刷显示（调用方刷）。
    /// </summary>
    public static ApplyResult Apply(BlockModelMeta blk, InclineBlockSource src, IReadOnlyList<WorkLineSamples> wls, TinSampler? currentSurface,
                                    IReadOnlyList<SeamSurfaces> seams, double alphaDeg, int coalMode, double coalThreshold,
                                    double advance, IReadOnlyList<double>? dSeam)
    {
        var r = new ApplyResult();
        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);
        var proj = WorkLineProjector.Build(wls, Math.Max(blk.Sx, blk.Sy));
        if (!proj.HasAny) throw new InvalidOperationException("无有效工作线");

        if (blk.LastInclineUndo != null) { r.RestoredPrevious = blk.Restore(blk.LastInclineUndo); blk.LastInclineUndo = null; }

        InclineStageField field;
        if (dSeam != null && dSeam.Count > 0) { field = new InclineStageField(proj, currentSurface, tanA, advance, seams, dSeam); r.ModeNote = "阶段1+2 分色"; }
        else { field = new InclineStageField(proj, currentSurface, tanA, advance, null, null); r.ModeNote = "仅阶段1"; }

        var rec = new BlockDeleteRecord();
        var isCoal = BuildCoalPredicate(src, seams, coalMode, coalThreshold);
        r.RockDeleted = blk.ApplyKeep((i, _) => isCoal(i), rec);
        r.CoalDeleted = blk.ApplyKeep((_, b) => field.StageAt(b.X, b.Y, b.Z) != 0, rec);
        blk.LastInclineUndo = rec;
        AssignStageColoring(blk, field);
        r.Live = blk.LiveBlockCount();
        return r;
    }

    /// <summary>撤销上次约束（勾掉「约束块体」重跑时）。返回恢复块数。</summary>
    public static int Undo(BlockModelMeta blk)
    {
        if (blk.LastInclineUndo == null) return 0;
        int n = blk.Restore(blk.LastInclineUndo);
        blk.LastInclineUndo = null;
        return n;
    }

    /// <summary>分阶段着色：逐块写「驱动阶段」码(1/2/0)，登记分类列 + 两色 + 设为当前着色属性。</summary>
    public static void AssignStageColoring(BlockModelMeta blk, InclineStageField field)
    {
        var stage = blk.EnsureAttr(StageAttr, 0);
        for (int i = 0; i < blk.Blocks.Count; i++)
        {
            var b = blk.Blocks[i];
            stage[i] = field.ColorStageAt(b.X, b.Y, b.Z);
        }
        var col = blk.FindColumn(StageAttr)!;
        col.DataType = BlockPropertyType.Int32;
        col.IsCategorical = true;
        col.CategoryLabels = new List<string> { "(未采)", "阶段1·年产量", "阶段2·增量" };
        col.Description = "量驱动开采阶段（派生态）";
        var cc = blk.DisplayStyle.EnsureCategoryColors(StageAttr);
        cc[1] = Stage1Color;
        cc[2] = Stage2Color;
        blk.ActiveColormapAttribute = StageAttr;
        blk.ColormapRange = null;
    }
}

/// <summary>
/// 采出区阶段场：给世界点算它落在哪个开采阶段。1=阶段1(现状↔前界①)，2=阶段2(前界①↔前界②)，
/// 0=不在采出区（现状面之上/工作线后方/前界之外/corridor 外 → 挖除隐藏）。逐层前界 d_seam：点落某层顶底板夹持带内用该层，带外回退全局 d。
/// </summary>
public sealed class InclineStageField
{
    private readonly WorkLineProjector _proj;
    private readonly TinSampler? _surf;
    private readonly double _tanA;
    private readonly double _d;
    private readonly IReadOnlyList<SeamSurfaces>? _seams;
    private readonly IReadOnlyList<double>? _dSeam;

    public InclineStageField(WorkLineProjector proj, TinSampler? surf, double tanA, double d,
                             IReadOnlyList<SeamSurfaces>? seams, IReadOnlyList<double>? dSeam)
    {
        _proj = proj; _surf = surf;
        _tanA = Math.Abs(tanA) < 1e-6 ? 1e-6 : tanA;
        _d = d; _seams = seams; _dSeam = dSeam;
    }

    public int StageAt(double x, double y, double z)
    {
        if (_surf != null && _surf.TrySampleZ(x, y, out double sz) && z >= sz) return 0;
        if (!_proj.TryProject(x, y, out double a0, out double zDatum)) return 0;
        double s = a0 - (z - zDatum) / _tanA;
        if (s < 0) return 0;
        double front = _d;
        if (_seams != null && _dSeam != null)
        {
            int M = Math.Min(_seams.Count, _dSeam.Count);
            for (int m = 0; m < M; m++)
            {
                var sm = _seams[m];
                if (sm.Roof == null || sm.Floor == null) continue;
                if (!sm.Roof.TrySampleZ(x, y, out double rz) || !sm.Floor.TrySampleZ(x, y, out double fz)) continue;
                double lo = Math.Min(rz, fz), hi = Math.Max(rz, fz);
                if (z >= lo && z <= hi) { front = _dSeam[m]; break; }
            }
        }
        if (s > front) return 0;
        return s <= _d ? 1 : 2;
    }

    /// <summary>着色用阶段码：只按 s 相对分界 d 分 1/2（不带截断）；corridor 外返回 0。</summary>
    public int ColorStageAt(double x, double y, double z)
    {
        if (!_proj.TryProject(x, y, out double a0, out double zDatum)) return 0;
        double s = a0 - (z - zDatum) / _tanA;
        return s <= _d ? 1 : 2;
    }
}

/// <summary>
/// 用【现状面】估算大致的最大工作帮坡角 α（忠实原 <c>WorkingSlopeEstimator</c>）：沿各工作线【推进方向】在现状面上采一条带（±窗口），
/// 最小二乘拟合平面，取平面沿【推进方向】的倾角；多条工作线取【最大】。
/// </summary>
public static class WorkingSlopeEstimator
{
    public struct Result { public bool Ok; public double AngleDeg; public int Points; public int Lines; public double AvgDeg; public string Note; }

    public static Result Estimate(TinSampler? surface, IReadOnlyList<WorkLineSamples> wls)
    {
        var res = new Result { Note = "" };
        if (surface == null) { res.Note = "现状面采样器为空"; return res; }
        if (wls == null || wls.Count == 0) { res.Note = "无工作线"; return res; }

        const double WINDOW = 250.0;
        const double STEP   = 8.0;

        double maxAngle = 0, sumAngle = 0; int okLines = 0, totalPts = 0;
        foreach (var wl in wls)
        {
            if (wl == null || !wl.Success || wl.Baseline.Count < 1 || wl.Samples.Count < 1) continue;
            double dx = 0, dy = 0;
            foreach (var s in wl.Samples) { dx += s.Dx; dy += s.Dy; }
            double dl = Math.Sqrt(dx * dx + dy * dy); if (dl < 1e-9) continue; dx /= dl; dy /= dl;

            var pts = new List<(double X, double Y, double Z)>();
            foreach (var b in wl.Baseline)
                for (double s = -WINDOW; s <= WINDOW + 1e-6; s += STEP)
                {
                    double x = b.X + s * dx, y = b.Y + s * dy;
                    if (surface.TrySampleZ(x, y, out double z)) pts.Add((x, y, z));
                }
            if (pts.Count < 8) continue;

            double cx = 0, cy = 0, cz = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
            cx /= pts.Count; cy /= pts.Count; cz /= pts.Count;
            double sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0;
            foreach (var p in pts)
            {
                double x = p.X - cx, y = p.Y - cy, z = p.Z - cz;
                sxx += x * x; sxy += x * y; syy += y * y; sxz += x * z; syz += y * z;
            }
            double det = sxx * syy - sxy * sxy;
            if (Math.Abs(det) < 1e-9) continue;
            double a  = (sxz * syy - syz * sxy) / det;
            double bb = (syz * sxx - sxz * sxy) / det;
            double slopeDi = Math.Abs(a * dx + bb * dy);
            double angle = Math.Atan(slopeDi) * 180.0 / Math.PI;
            sumAngle += angle;
            if (angle > maxAngle) maxAngle = angle;
            okLines++; totalPts += pts.Count;
        }

        if (okLines == 0) { res.Note = "现状面在工作线推进方向上覆盖不足，无法估算（现状面范围是否覆盖工作帮？）"; return res; }
        res.Ok = true;
        res.Lines = okLines;
        res.Points = totalPts;
        res.AvgDeg = sumAngle / okLines;
        res.AngleDeg = Math.Max(1.0, Math.Min(60.0, maxAngle));
        res.Note = $"现状面估算：{okLines} 条工作线、{totalPts} 采样点 → 最大工作帮坡角 ≈ {res.AngleDeg:0.#}°（各线均值 {res.AvgDeg:0.#}°）";
        return res;
    }
}
