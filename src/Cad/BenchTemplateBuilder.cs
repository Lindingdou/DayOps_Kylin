using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>一条台阶线（沿工作线方向的 3D 折线）：煤台阶 / 岩台阶 / 露煤走廊 / 搭接线。toe=坡底线、crest=坡顶线。</summary>
public sealed class BenchLine
{
    public bool   IsCoal;
    /// <summary>0=煤台阶 1=岩台阶 2=露煤走廊 3=搭接线（单列成一类，绝不混进台阶线：台阶线等高、搭接段是斜的）。</summary>
    public int    Kind;
    public int    SeamIndex;     // 所属煤层(自下而上序)；岩台阶/搭接线=-1
    public int    RockLevel;     // 煤/露煤=-1；岩台阶=标高格号 k；搭接线=下端所在格号
    public int    Level;         // 同类同层内自下而上第几级
    public int    LineIndex;     // 所属工作线序
    /// <summary>岩台阶专用：这摞岩堆在哪一层煤之下（层序）；= seams.Count 表示最上层煤之上到帮顶。煤/搭接=−1。</summary>
    public int    GapSeam = -1;
    public int    FirstVi = -1;
    public int    LastVi  = -1;
    /// <summary>R28：这一端是按真尖灭断因主动收成楔尖的吗。</summary>
    public bool   PinchClosedStart, PinchClosedEnd;
    public string SeamName = "";
    public readonly List<(double X, double Y, double Z)> Crest = new();
    public readonly List<(double X, double Y, double Z)> Toe   = new();
    /// <summary>每个点的站号（与 Toe/Crest 同序同长）；后处理插入的合成点记 −1。</summary>
    public readonly List<int> Vi = new();
    /// <summary>R47 削顶标记（与 Toe/Crest 同序；未经 R47 处理的线为空表）。</summary>
    public readonly List<bool> Shaved = new();
}

public sealed class BenchTemplateResult
{
    public bool   Success;
    public string Error = "";
    public string Diag = "";
    public readonly List<string> Walk = new();
    public readonly List<BenchLine> Benches = new();
    public int CoalCount, RockCount, ExposureCount, BridgeCount;

    /// <summary>露煤面积逐层 m²（口径 A：阶段1↔阶段2 之间那块顶板，按站间梯形累加）。</summary>
    public readonly Dictionary<string, double> ExposureAreaBySeam = new();

    public sealed class ExposureBand
    {
        public string Seam = "";
        public int SeamIndex, LineIndex;
        public readonly List<(double X, double Y, double Z)> Inner = new();
        public readonly List<(double X, double Y, double Z)> Outer = new();
    }
    public readonly List<ExposureBand> ExposureBands = new();

    /// <summary>现状面上已经露着的煤，逐层真实（三维）面积 m²。</summary>
    public readonly Dictionary<string, double> OutcropAreaBySeam = new();
    public readonly Dictionary<string, double> OutcropAreaXYBySeam = new();
    public readonly List<OutcropRing> OutcropRings = new();
}

/// <summary>
/// 采区台阶模板生成（「创建工程位置」核心；忠实原 <c>BenchTemplateBuilder</c> 含 R3~R61 全部现场口径）。
/// 前界斜面直接卡位模型：一切位置由量驱动阶段几何卡死，全部从前界①斜面（advance=d，α）按站点对齐取真实交线/等高线：
///   · 煤台阶【每层】= 前界①斜面 ∩ 该层底板(toe) / ∩ 顶板(crest)，按煤台阶高分层（R26）；
///   · 露煤带【每层】= 顶板∩前界① → 顶板∩前界②（口径 A 面积，不出线）；
///   · 岩台阶【跨层】= 前界①斜面在标高格 z=gridZ0+k·rockH 的等值线（R15），被顶/底板交线 + 坑沿裁剪；
/// 尖灭 = 交线/等高线天然缺口 → 段号另起。
/// 【登记的差异】原版两个从未被调用的私有函数（SplitAtBends / TrimBacktrack）未搬。
/// </summary>
public static class BenchTemplateBuilder
{
    public static bool TraceWalk;
    public static bool DisableRimPinch;
    public static bool DisableBermWiden;
    public static bool DisableFloorPinchLevel;
    public static bool DisableHalfSliverGate;
    public static bool DisableMinBermOnRock;
    public static bool DisableHalfBenchIso;

    /// <summary>薄片阈值 R41：面高小于本值(m)的一级不成台阶（= 岩台阶面高公差 ±0.3m）。</summary>
    public const double SliverFaceM = 0.3;
    /// <summary>R59：一级台阶的坡顶线与坡底线在平面上至少分开多少(m)。</summary>
    public const double MinToeCrestSepM = 1.0;

    private static (double X, double Y, double Z)? SurfIso(IReadOnlyList<(double X, double Y, double Z)> crest, IReadOnlyList<(double X, double Y, double Z)> toe, int i, double z, double zTop, double zFloor)
    {
        if (z < zFloor - 0.01 || z > zTop + 0.01) return null;
        if (zTop - zFloor < 1e-6) return null;
        double t = (zTop - z) / (zTop - zFloor);
        var c = crest[i]; var o = toe[i];
        return (c.X + (o.X - c.X) * t, c.Y + (o.Y - c.Y) * t, z);
    }

    public static BenchTemplateResult Build(
        IReadOnlyList<WorkLineSamples> workLines, IReadOnlyList<SeamSurfaces> seamsIn, TinSampler? currentSurface,
        double dStage1, IReadOnlyList<double>? dSeamBySeam, double alphaDeg, double zTop, double zFloor,
        BenchTemplateParams p, IReadOnlyList<WorkLineCornerJoiner.CornerLink>? cornerLinks = null)
    {
        var r = new BenchTemplateResult();
        if (workLines == null || workLines.Count == 0) { r.Error = "无工作线"; return r; }
        if (seamsIn == null || seamsIn.Count == 0) { r.Error = "无煤层"; return r; }
        if (zTop <= zFloor) { r.Error = "斜面竖向范围异常 (z_top ≤ z_floor)"; return r; }
        p ??= new BenchTemplateParams();
        double rockH = Math.Max(1.0, p.RockBenchH);
        // R51 台阶一律取整数标高：锚位偏移取整
        double gridZ0 = p.BenchGridAnchorZ > 0 ? Math.Round(p.BenchGridAnchorZ - Math.Floor(p.BenchGridAnchorZ / rockH) * rockH) : 0.0;
        double GridZ(int k) => gridZ0 + k * rockH;
        int GridKBelow(double z) => (int)Math.Floor((z - gridZ0) / rockH);

        var seams = new List<SeamSurfaces>();
        var dSeam = new List<double>();
        var coalH = new List<double>();
        for (int i = 0; i < seamsIn.Count; i++)
        {
            var s = seamsIn[i];
            if (s == null || !s.Ready) continue;
            seams.Add(s);
            double ds = (dSeamBySeam != null && i < dSeamBySeam.Count) ? dSeamBySeam[i] : dStage1;
            dSeam.Add(Math.Max(ds, dStage1));
            coalH.Add((p.CoalBenchHBySeam != null && i < p.CoalBenchHBySeam.Count) ? p.CoalBenchHBySeam[i] : 0.0);
        }
        if (seams.Count == 0) { r.Error = "没有顶底板都就绪的煤层"; return r; }

        double refX0 = 0, refY0 = 0; int nref = 0;
        foreach (var wl0 in workLines) if (wl0?.Baseline != null) foreach (var b in wl0.Baseline) { refX0 += b.X; refY0 += b.Y; nref++; }
        if (nref > 0) { refX0 /= nref; refY0 /= nref; }
        var order = new int[seams.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => RepFloor(seams[a], refX0, refY0).CompareTo(RepFloor(seams[b], refX0, refY0)));
        var sortedSeams = new List<SeamSurfaces>(seams.Count); var sortedDSeam = new List<double>(seams.Count); var sortedCoalH = new List<double>(seams.Count);
        foreach (var i in order) { sortedSeams.Add(seams[i]); sortedDSeam.Add(dSeam[i]); sortedCoalH.Add(coalH[i]); }
        seams = sortedSeams; dSeam = sortedDSeam; coalH = sortedCoalH;

        // 帮顶：封在工作线标高（略放），别抬到全局地表；R55 帮顶跟着地表走（现状面最高点 + 一个台阶高）；WallTopZ>0 显式指定
        double maxZDatum = double.NegativeInfinity;
        foreach (var wl0 in workLines)
        {
            if (wl0?.Baseline == null || wl0.Baseline.Count == 0) continue;
            double zd = 0; foreach (var b in wl0.Baseline) zd += b.Z; zd /= wl0.Baseline.Count;
            if (zd > maxZDatum) maxZDatum = zd;
        }
        double zTopS = !double.IsInfinity(maxZDatum) ? maxZDatum : zTop;
        zTopS += Math.Max(2.0, 0.02 * (zTopS - zFloor));
        if (p.WallTopZ > 0) zTopS = p.WallTopZ;
        else if (currentSurface != null)
        {
            double zSurfMax = double.NegativeInfinity;
            foreach (var wl in workLines)
                foreach (var b0 in wl.Baseline)
                    for (int s = -3000; s <= 3000; s += 60)
                        if (currentSurface.TrySampleZ(b0.X + s * 0.0, b0.Y + s * 1.0, out double zz) || currentSurface.TrySampleZ(b0.X + s * 1.0, b0.Y, out zz))
                            zSurfMax = Math.Max(zSurfMax, zz);
            if (!double.IsInfinity(zSurfMax)) zTopS = Math.Max(zTopS, zSurfMax + rockH);
        }

        // R7 采样面与显示面分开：采样列沿倾向线向上延长到 zSample（够着高位煤层），斜面本身仍是 zTopS 那张
        double zSample = zTopS;
        foreach (var s0 in seams)
        {
            if (s0.Roof != null && s0.Roof.MaxZ > zSample) zSample = s0.Roof.MaxZ;
            if (s0.Floor != null && s0.Floor.MaxZ > zSample) zSample = s0.Floor.MaxZ;
        }
        zSample += Math.Max(2.0, 0.02 * (zSample - zFloor));

        static List<(double X, double Y, double Z)> ExtendCrestForSampling(IReadOnlyList<(double X, double Y, double Z)> crest, IReadOnlyList<(double X, double Y, double Z)> toe, double zTop, double zFloor, double zSample)
        {
            int n = Math.Min(crest.Count, toe.Count);
            var o = new List<(double X, double Y, double Z)>(n);
            double span = zTop - zFloor;
            double f = span > 1e-9 ? (zSample - zTop) / span : 0;
            for (int i = 0; i < n; i++) o.Add((crest[i].X + (crest[i].X - toe[i].X) * f, crest[i].Y + (crest[i].Y - toe[i].Y) * f, zSample));
            return o;
        }

        var map = new Dictionary<string, BenchLine>(StringComparer.Ordinal);
        var segIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastVi = new Dictionary<string, int>(StringComparer.Ordinal);
        var blockedLv = new HashSet<(int w, int vi, int k)>();     // R30：这一格这一站被占用（煤体 / 坑沿之上）
        var blockedCoal = new HashSet<(int w, int vi, int k)>();   // R35：因煤而断
        var rimAt = new Dictionary<(int w, int vi), double>();
        var rimPt = new Dictionary<(int w, int vi), (double X, double Y, double Z)>();
        int dgBlockBridge = 0;
        var lineLastVi = new int[workLines.Count];
        for (int i = 0; i < lineLastVi.Length; i++) lineLastVi[i] = -1;

        const int BRIDGE_GAP_COAL = 1;      // 煤台阶/露煤：不桥接
        const int BRIDGE_GAP_ROCK = 999;    // 岩台阶：站数不再是闸，把关的是错步闸
        const double DZ_MAX = 10.0;
        int dgBridgeN = 0, dgBridgeMaxGap = 0; double dgBridgeMaxLen = 0;
        // 平盘服从 α：berm = h/tanα − h/tan(坡面角)
        double rockTanB  = Math.Tan(Math.Max(10.0, Math.Min(89.0, p.RockFaceDeg)) * Math.PI / 180.0);
        double tanAlphaB = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);
        double runFaceB  = rockH / rockTanB;
        double runSlopeB = rockH / tanAlphaB;
        double berm      = runSlopeB - runFaceB;
        string bermNote;
        if (berm < 0.5)
        {
            bermNote = $"⚠平盘：帮坡角 α={alphaDeg:0.#}° 过陡 —— 每 {rockH:0.#}m 只退 {runSlopeB:0.#}m，"
                     + $"扣掉坡面进尺 {runFaceB:0.#}m 后平盘只剩 {berm:0.#}m，已钳到 0.5m。请放缓 α，或调小台阶高/放缓坡面角。";
            berm = 0.5;
        }
        else
        {
            bermNote = $"平盘 {berm:0.#}m（由 α={alphaDeg:0.#}° 反算：每{rockH:0.#}m退{runSlopeB:0.#}m − 坡面{p.RockFaceDeg:0.#}°进尺{runFaceB:0.#}m）";
            if (p.MinBerm > berm + 0.5)
                bermNote += $"；参数「最小工作平盘 {p.MinBerm:0.#}m」在**满高台阶之间**不再独立生效 —— 帮坡角/台阶高/坡面角/平盘"
                          + $"四者只能定三个，要全帮保住 {p.MinBerm:0.#}m 就得把 α 放缓到 "
                          + $"{Math.Atan(rockH / (runFaceB + p.MinBerm)) * 180.0 / Math.PI:0.#}°";
            if (p.MinBermOnRock && !DisableMinBermOnRock)
                bermNote += $"；R58 非满高岩台阶补最小平盘={Math.Max(p.MinBerm, berm):0.#}m"
                          + "（只推岩台阶：煤台阶锚在真实露头不动、R35 交接不推；被推那几级帮坡角局部缓于 α）";
        }

        double jumpStep = 0.5 * (berm + runFaceB) + 25.0;
        double jumpCap = berm + runFaceB;
        void Emit(int w, string ident, int kind, bool coal, int seamIdx, int rockLvl, int level, string name,
                  int vi, (double X, double Y, double Z) toe, (double X, double Y, double Z) crest, double dix, double diy, int gapSeam = -1)
        {
            // R31 桥接闸按"是不是等高线"分：半台阶（RockLevel<0）与煤台阶同闸
            int gapMax = (kind == 1 && rockLvl >= 0) ? BRIDGE_GAP_ROCK : BRIDGE_GAP_COAL;
            bool cont = lastVi.TryGetValue(ident, out int lv) && vi > lv && vi - lv <= gapMax;
            if (cont && segIdx.TryGetValue(ident, out int sg0) && map.TryGetValue(ident + ":S" + sg0, out var prev) && prev.Toe.Count > 0)
            {
                var pt = prev.Toe[prev.Toe.Count - 1]; var pc = prev.Crest[prev.Crest.Count - 1];
                if (vi - lv > 1 && (Math.Abs(toe.Z - pt.Z) > DZ_MAX || Math.Abs(crest.Z - pc.Z) > DZ_MAX)) cont = false;
                double jdx = toe.X - pt.X, jdy = toe.Y - pt.Y;
                if (cont && kind == 1 && Math.Abs(jdx * dix + jdy * diy) > Math.Min(jumpStep * (vi - lv), jumpCap)) cont = false;
                if (cont && kind == 1 && rockLvl >= 0 && vi - lv > 1)
                    for (int vq = lv + 1; vq < vi; vq++)
                        if (blockedLv.Contains((w, vq, rockLvl))) { cont = false; dgBlockBridge++; break; }
                if (cont && vi - lv > 1)
                {
                    dgBridgeN++;
                    dgBridgeMaxGap = Math.Max(dgBridgeMaxGap, vi - lv);
                    dgBridgeMaxLen = Math.Max(dgBridgeMaxLen, Math.Sqrt(jdx * jdx + jdy * jdy));
                }
            }
            if (!cont) segIdx[ident] = segIdx.TryGetValue(ident, out int sgi) ? sgi + 1 : 0;
            lastVi[ident] = vi;
            string key = ident + ":S" + segIdx[ident];
            if (!map.TryGetValue(key, out var bl))
            {
                bl = new BenchLine { IsCoal = coal, Kind = kind, SeamIndex = seamIdx, RockLevel = rockLvl, Level = level, SeamName = name, LineIndex = w, GapSeam = gapSeam };
                map[key] = bl;
            }
            if (bl.FirstVi < 0) bl.FirstVi = vi;
            bl.LastVi = vi;
            bl.Toe.Add(toe); bl.Crest.Add(crest); bl.Vi.Add(vi);
        }

        int dgStd = 0, dgPinch = 0, dgGapN = 0, dgOutlier = 0;
        int dgNoFloor = 0, dgSkipStat = 0, dgOverRim = 0, dgExpOverRim = 0;
        int dgCoalLvl = 0, dgCoalStat = 0, dgCoalLvlMax = 0;
        int dgExpNoGain = 0, dgHalfSliver = 0, dgFlatPinch = 0, dgFloorPinch = 0;
        const int STOP_PINCH = 1, STOP_NOFLOOR = 2, STOP_OVERRIM = 3;
        var coalStop = new Dictionary<(int w, int m, int vi), int>();
        var coalLv   = new Dictionary<(int w, int m, int vi), int>();
        var gapPinch = new HashSet<(int w, int g, int vi)>();
        var stackTop = new Dictionary<(int w, int g, int vi), double>();
        int dgPinchClose = 0, dgCutEnd = 0, dgLapCutN = 0, dgFill = 0;
        double dgGapMin = double.PositiveInfinity, dgGapMax = double.NegativeInfinity, dgGapSum = 0;

        for (int w = 0; w < workLines.Count; w++)
        {
            var wl = workLines[w];
            if (wl == null || !wl.Success || wl.Baseline.Count < 2 || wl.Samples.Count < 1) continue;

            var dwl = DensifyBaseline(wl, 20.0);
            var surf = InclineSurfaceBuilder.BuildForWorkLine(dwl, alphaDeg, zTopS, zFloor, dStage1, Array.Empty<SeamSurfaces>(), 0, 0);
            if (!surf.Success || surf.Crest.Count < 2) continue;
            int nStat = Math.Min(surf.Crest.Count, surf.Toe.Count);
            lineLastVi[w] = nStat - 1;

            double coalTan = Math.Tan(Math.Max(10.0, Math.Min(89.0, p.CoalFaceDeg)) * Math.PI / 180.0);
            double rockTan = Math.Tan(Math.Max(10.0, Math.Min(89.0, p.RockFaceDeg)) * Math.PI / 180.0);
            const double MIN_RISE = 0.3, EPS = 1e-6, coalThr = 0.5;

            int nSeam = seams.Count;
            var floorX = new (double X, double Y, double Z)?[nSeam][];
            var roofX  = new (double X, double Y, double Z)?[nSeam][];
            var roofX2 = new (double X, double Y, double Z)?[nSeam][];
            var surf2C = new List<(double X, double Y, double Z)>?[nSeam];
            var surf2T = new List<(double X, double Y, double Z)>?[nSeam];
            var ceil2 = new (double X, double Y, double Z)?[nSeam][];
            var crestS = ExtendCrestForSampling(surf.Crest, surf.Toe, zTopS, zFloor, zSample);
            for (int m = 0; m < nSeam; m++)
            {
                floorX[m] = InclineSurfaceBuilder.SampleDipPerStation(crestS, surf.Toe, zSample, zFloor, seams[m].Floor);
                roofX[m]  = InclineSurfaceBuilder.SampleDipPerStation(crestS, surf.Toe, zSample, zFloor, seams[m].Roof);
                var surf2 = InclineSurfaceBuilder.BuildForWorkLine(dwl, alphaDeg, zTopS, zFloor, dSeam[m], Array.Empty<SeamSurfaces>(), 0, 0);
                if (surf2.Success && surf2.Crest.Count >= 2)
                {
                    roofX2[m] = InclineSurfaceBuilder.SampleDipPerStation(ExtendCrestForSampling(surf2.Crest, surf2.Toe, zTopS, zFloor, zSample), surf2.Toe, zSample, zFloor, seams[m].Roof);
                    surf2C[m] = new List<(double X, double Y, double Z)>(surf2.Crest);
                    surf2T[m] = new List<(double X, double Y, double Z)>(surf2.Toe);
                    ceil2[m] = currentSurface != null ? InclineSurfaceBuilder.SampleDipPerStation(surf2.Toe, surf2.Crest, zFloor, zTopS, currentSurface) : null;
                }
                else roofX2[m] = new (double X, double Y, double Z)?[roofX[m].Length];
            }

            // R40 之后不再让开露煤带：基准面全部回到前界①（bandRef = −1），岩、煤共用同一张面
            var bandRef = new int[nSeam + 1];
            for (int g = 0; g <= nSeam; g++) bandRef[g] = -1;

            var floorXR = new (double X, double Y, double Z)?[nSeam][];
            var roofXR  = new (double X, double Y, double Z)?[nSeam][];
            for (int g = 0; g < nSeam; g++)
            {
                int b = bandRef[g];
                if (b < 0 || surf2C[b] == null) { floorXR[g] = floorX[g]; roofXR[g] = roofX[g]; continue; }
                var cR = ExtendCrestForSampling(surf2C[b]!, surf2T[b]!, zTopS, zFloor, zSample);
                floorXR[g] = InclineSurfaceBuilder.SampleDipPerStation(cR, surf2T[b]!, zSample, zFloor, seams[g].Floor);
                roofXR[g]  = InclineSurfaceBuilder.SampleDipPerStation(cR, surf2T[b]!, zSample, zFloor, seams[g].Roof);
            }

            // 坑沿：R46 从坑底往外扫取最近交点（toe/crest 对调）；R52 RimOutermost 取最外侧
            var surfX = currentSurface == null ? null
                : p.RimOutermost
                    ? InclineSurfaceBuilder.SampleDipPerStation(surf.Crest, surf.Toe, zTopS, zFloor, currentSurface)
                    : InclineSurfaceBuilder.SampleDipPerStation(surf.Toe, surf.Crest, zFloor, zTopS, currentSurface);

            // 露煤面积：按站间梯形累加；站不连续另起一条带
            var expPrevW = new double[nSeam]; var expPrevX = new double[nSeam]; var expPrevY = new double[nSeam]; var expPrevVi = new int[nSeam];
            for (int m0 = 0; m0 < nSeam; m0++) expPrevVi[m0] = int.MinValue;
            var expBand = new BenchTemplateResult.ExposureBand?[nSeam];
            void AddExposure(int m, int vi, double wExp, double ax, double ay, (double X, double Y, double Z) inner, (double X, double Y, double Z) outer)
            {
                if (wExp > EPS && expPrevVi[m] == vi - 1)
                {
                    double sx = ax - expPrevX[m], sy = ay - expPrevY[m];
                    double step = Math.Sqrt(sx * sx + sy * sy);
                    string nm = seams[m].Name;
                    r.ExposureAreaBySeam[nm] = r.ExposureAreaBySeam.GetValueOrDefault(nm) + 0.5 * (wExp + expPrevW[m]) * step;
                }
                else if (wExp > EPS) expBand[m] = null;
                if (wExp > EPS)
                {
                    if (expBand[m] == null)
                    {
                        expBand[m] = new BenchTemplateResult.ExposureBand { Seam = seams[m].Name, SeamIndex = m, LineIndex = w };
                        r.ExposureBands.Add(expBand[m]!);
                    }
                    expBand[m]!.Inner.Add(inner); expBand[m]!.Outer.Add(outer);
                }
                expPrevW[m] = wExp; expPrevX[m] = ax; expPrevY[m] = ay; expPrevVi[m] = vi;
            }

            for (int i = 0; i < nStat; i++)
            {
                var fp0 = i < floorX[0].Length ? floorX[0][i] : null;
                bool noFloor0 = !fp0.HasValue;     // R15：最下层底板无露头也不整站跳过，岩台阶照铺
                if (noFloor0) dgSkipStat++;
                double ex = surf.Crest[i].X - surf.Toe[i].X, ey = surf.Crest[i].Y - surf.Toe[i].Y;
                double el = Math.Sqrt(ex * ex + ey * ey);
                double dix = el < 1e-9 ? 1 : ex / el, diy = el < 1e-9 ? 0 : ey / el;
                double zCeil = (surfX != null && i < surfX.Length && surfX[i].HasValue) ? surfX[i]!.Value.Z : zTopS;

                double px, py, pz;
                if (!noFloor0) { px = fp0!.Value.X; py = fp0.Value.Y; pz = fp0.Value.Z; }
                else
                {
                    px = surf.Toe[i].X; py = surf.Toe[i].Y; pz = zFloor;
                    var r0 = i < roofX[0].Length ? roofX[0][i] : null;
                    if (r0.HasValue) { px = r0.Value.X; py = r0.Value.Y; pz = r0.Value.Z; }
                    else
                        for (int m0 = 0; m0 < nSeam; m0++)
                        {
                            var f = i < floorX[m0].Length ? floorX[m0][i] : null;
                            if (!f.HasValue) continue;
                            px = f.Value.X; py = f.Value.Y; pz = f.Value.Z; break;
                        }
                }
                // R30：坑沿之上的标高格这一站根本不存在（取所有基准面里最高的坑沿）
                double zRimTop = zCeil;
                for (int g = 0; g <= nSeam; g++) zRimTop = Math.Max(zRimTop, RockCeil(g));
                for (int kb = GridKBelow(zRimTop) + 1; kb <= GridKBelow(zTopS) + 2; kb++) blockedLv.Add((w, i, kb));
                rimAt[(w, i)] = zCeil;
                if (surfX != null && i < surfX.Length && surfX[i].HasValue) rimPt[(w, i)] = surfX[i]!.Value;
                double zStart = pz;
                var emittedK = new HashSet<int>();
                var coalHere = new List<(double Lo, double Hi)>();
                if (TraceWalk) r.Walk.Add($"L{w} vi{i} 起 pz={pz:0.##} zCeil={zCeil:0.##}" + (noFloor0 ? "  [最下层底板无露头]" : ""));

                (IReadOnlyList<(double X, double Y, double Z)> C, IReadOnlyList<(double X, double Y, double Z)> T) IsoRef(int gapId)
                {
                    int b = bandRef[gapId];
                    return (b >= 0 && surf2C[b] != null) ? (surf2C[b]!, surf2T[b]!) : (surf.Crest, surf.Toe);
                }
                // R54 解除坑沿对岩台阶的封顶：RockAboveRim=true 堆到帮顶，越界的交给 R47/C9
                double RockCeil(int gapId)
                {
                    if (p.RockAboveRim) return zTopS;
                    int b = bandRef[gapId];
                    if (b < 0 || ceil2[b] == null) return zCeil;
                    var a = ceil2[b]!;
                    return (i < a.Length && a[i].HasValue) ? a[i]!.Value.Z : zCeil;
                }

                // 从当前走位按标高格堆满格岩台阶到 zTarget（R14 身份只认标高格号；R15 坡顶=等值线；R41 薄片跳过；R59/R61 半台阶）
                void StackRock(int gapId, double zTarget, string halfName)
                {
                    double pzIn = pz; int kFirst = int.MinValue, kLast = int.MinValue; bool halfOut = false;
                    double sIn = (px - surf.Toe[i].X) * dix + (py - surf.Toe[i].Y) * diy, sFirst = double.NaN;
                    int kk = GridKBelow(pz) + 1, guard = 0;
                    while (GridZ(kk) <= zTarget + EPS && guard++ < 64)
                    {
                        double zt = GridZ(kk), faceK = zt - pz, run = faceK / rockTan;
                        if (faceK < Math.Max(MIN_RISE, SliverFaceM)) { kk++; continue; }
                        var (isoC, isoT) = IsoRef(gapId);
                        var iso = SurfIso(isoC, isoT, i, zt, zTopS, zFloor);
                        var crestP = iso ?? (px + run * dix, py + run * diy, zt);
                        Emit(w, $"{w}:R#{kk}@{bandRef[gapId]}", kind: 1, coal: false, seamIdx: -1, rockLvl: kk, level: kk,
                             name: $"岩·标高{zt:0}", vi: i, toe: (crestP.X - run * dix, crestP.Y - run * diy, pz), crest: crestP, dix, diy, gapId);
                        px = crestP.X; py = crestP.Y; pz = zt; dgStd++;
                        emittedK.Add(kk);
                        if (kFirst == int.MinValue)
                        {
                            kFirst = kk;
                            sFirst = (crestP.X - run * dix - surf.Toe[i].X) * dix + (crestP.Y - run * diy - surf.Toe[i].Y) * diy;
                        }
                        kLast = kk;
                        kk++;
                    }
                    double faceH = zTarget - pz;
                    double halfMin = DisableHalfSliverGate ? MIN_RISE : Math.Max(MIN_RISE, MinToeCrestSepM * rockTan);
                    bool interburden = gapId < nSeam;
                    if (interburden && !DisableFloorPinchLevel)
                    {
                        if (faceH >= halfMin) dgFloorPinch++;
                        faceH = 0;
                    }
                    if (faceH >= MIN_RISE && faceH < halfMin) { dgHalfSliver++; }
                    if (faceH >= halfMin)
                    {
                        double run = faceH / rockTan;
                        var (isoHC, isoHT) = IsoRef(gapId);
                        var isoH = DisableHalfBenchIso ? null : SurfIso(isoHC, isoHT, i, zTarget, zTopS, zFloor);
                        var crestH = isoH ?? (px + run * dix, py + run * diy, zTarget);
                        Emit(w, $"{w}:RH{gapId}", kind: 1, coal: false, seamIdx: -1, rockLvl: -1, level: 0, name: halfName, vi: i,
                             toe: (crestH.X - run * dix, crestH.Y - run * diy, pz), crest: crestH, dix, diy, gapId);
                        px = crestH.X; py = crestH.Y; pz = zTarget; dgStd++;
                        if (isoH == null) { px += berm * (faceH / rockH) * dix; py += berm * (faceH / rockH) * diy; }
                        halfOut = true;
                    }
                    if (TraceWalk)
                        r.Walk.Add($"  L{w} vi{i} 堆岩 gap={gapId} {pzIn:0.##}→{zTarget:0.##}  起堆推进坐标 sIn={sIn:0.##}"
                                 + (double.IsNaN(sFirst) ? "" : $" 首级落点 sFirst={sFirst:0.##}（差 {sFirst - sIn:+0.##;-0.##}）")
                                 + (kFirst == int.MinValue ? "  出格：无" : $"  出格 {kFirst}..{kLast}")
                                 + (halfOut ? $"  +半台阶(高{zTarget - (kLast == int.MinValue ? pzIn : GridZ(kLast)):0.##}m)" : $"  无半台阶(余{zTarget - pz:0.##}m<{MIN_RISE})"));
                }

                for (int m = 0; m < nSeam; m++)
                {
                    var Pf = i < floorXR[m].Length ? floorXR[m][i] : null;
                    var Pr = i < roofXR[m].Length  ? roofXR[m][i]  : null;
                    var Pe = i < roofX2[m].Length  ? roofX2[m][i]  : null;
                    if (!Pf.HasValue)
                    {
                        dgNoFloor++;
                        coalStop[(w, m, i)] = STOP_NOFLOOR;
                        if (TraceWalk) r.Walk.Add($"  L{w} vi{i} 层{m}「{seams[m].Name}」底板无交点 → 跳过本层（连带**不堆** gap={m} 那摞层间岩；pz 停在 {pz:0.##}）");
                        continue;
                    }
                    double zRim = RockCeil(m);
                    if (TraceWalk)
                        r.Walk.Add($"  L{w} vi{i} 层{m}「{seams[m].Name}」底板 {Pf.Value.Z:0.##} 顶板 {(Pr.HasValue ? Pr.Value.Z.ToString("0.##") : "无")} 外缘 {(Pe.HasValue ? Pe.Value.Z.ToString("0.##") : "无")}"
                                 + " 基准=前界" + (bandRef[m] < 0 ? "①" : $"②「{seams[bandRef[m]].Name}」") + (Pf.Value.Z > zRim ? $"  ★底板高过坑沿 {zRim:0.##}" : ""));

                    if (m > 0)
                    {
                        double zStackTop = Pf.Value.Z;
                        double gap = zStackTop - pz;
                        if (gap < MIN_RISE) gapPinch.Add((w, m, i));
                        if (gap < dgGapMin) dgGapMin = gap;
                        if (gap > dgGapMax) dgGapMax = gap; dgGapSum += gap; dgGapN++;
                        double zStk = Math.Min(zStackTop, RockCeil(m));
                        stackTop[(w, m, i)] = zStk;
                        StackRock(m, zStk, "岩·半台阶");
                    }
                    if (Pf.Value.Z > zRim && !p.CoalAboveRim)
                    {
                        dgOverRim++;
                        coalStop[(w, m, i)] = STOP_OVERRIM;
                        if (TraceWalk) r.Walk.Add($"    L{w} vi{i} 层{m} 底板 {Pf.Value.Z:0.##} 已高过坑沿 {zRim:0.##} → 整层不出（R22），pz 留在 {pz:0.##}");
                        continue;
                    }
                    if (Pf.Value.Z > zRim) { dgOverRim++; if (TraceWalk) r.Walk.Add($"    L{w} vi{i} 层{m} 底板 {Pf.Value.Z:0.##} 高过坑沿 {zRim:0.##} → **照出**（R53），交给 R47 按地表裁"); }
                    if (pz < Pf.Value.Z) pz = Pf.Value.Z;

                    if (!(Pr.HasValue && Pr.Value.Z - Pf.Value.Z >= coalThr))
                    {
                        dgPinch++;
                        coalStop[(w, m, i)] = STOP_PINCH;
                        if (TraceWalk) r.Walk.Add($"    L{w} vi{i} 层{m} 尖灭(煤厚<{coalThr}) → 不出煤台阶，pz={pz:0.##}");
                        continue;
                    }
                    double zTopC = Math.Min(Pr!.Value.Z, zRim);
                    if (zTopC - Pf.Value.Z < coalThr)
                    {
                        dgOverRim++;
                        coalStop[(w, m, i)] = STOP_OVERRIM;
                        if (TraceWalk) r.Walk.Add($"    L{w} vi{i} 层{m} 顶板 {Pr.Value.Z:0.##} 越坑沿 {zRim:0.##}，截后可采高 {zTopC - Pf.Value.Z:0.##}m < {coalThr} → 不出（R22）");
                        continue;
                    }
                    double runC = (zTopC - Pf.Value.Z) / coalTan;
                    if (TraceWalk)
                        r.Walk.Add($"    L{w} vi{i} 层{m} 出煤台阶：锚真露头 s0={(Pf.Value.X - surf.Toe[i].X) * dix + (Pf.Value.Y - surf.Toe[i].Y) * diy:0.##}m（离前界坡底·沿推进）　旧走位链会画在 s={(px - surf.Toe[i].X) * dix + (py - surf.Toe[i].Y) * diy:0.##}m");

                    // R21 上覆煤层也锚真实露头；R26 煤台阶按台阶高分层（<0 采全高 | 0 跟岩台阶高 | >0 用该值）
                    double hC = coalH[m] < 0 ? (zTopC - Pf.Value.Z) : coalH[m] > 0.5 ? coalH[m] : rockH;
                    int lvlC = 0; double zLo = Pf.Value.Z, gC = 0;
                    while (zLo < zTopC - MIN_RISE && gC++ < 64)
                    {
                        double zHi = Math.Min(zLo + hC, zTopC);
                        if (zTopC - zHi < MIN_RISE) zHi = zTopC;
                        var (icC, icT) = IsoRef(m);
                        var t0 = SurfIso(icC, icT, i, zLo, zTopS, zFloor) ?? (Pf.Value.X, Pf.Value.Y, zLo);
                        double runL = (zHi - zLo) / coalTan;
                        Emit(w, $"{w}:C{m}#{lvlC}", kind: 0, coal: true, seamIdx: m, rockLvl: -1, level: lvlC, name: seams[m].Name, vi: i,
                             toe: (t0.X, t0.Y, zLo), crest: (t0.X + runL * dix, t0.Y + runL * diy, zHi), dix, diy);
                        px = t0.X + runL * dix; py = t0.Y + runL * diy; pz = zHi;
                        zLo = zHi; lvlC++;
                    }
                    if (lvlC == 0) { px = Pf.Value.X + runC * dix; py = Pf.Value.Y + runC * diy; pz = zTopC; }
                    coalHere.Add((Pf.Value.Z, zTopC));
                    for (int kb = GridKBelow(Pf.Value.Z); kb <= GridKBelow(zTopC) + 1; kb++)
                        if (GridZ(kb) > Pf.Value.Z + EPS && GridZ(kb) < zTopC - EPS) { blockedLv.Add((w, i, kb)); blockedCoal.Add((w, i, kb)); }
                    coalLv[(w, m, i)] = lvlC;
                    dgCoalLvl += lvlC; dgCoalStat++;
                    if (lvlC > dgCoalLvlMax) dgCoalLvlMax = lvlC;
                    if (TraceWalk) r.Walk.Add($"      L{w} vi{i} 层{m} 煤厚 {zTopC - Pf.Value.Z:0.##}m ÷ 台阶高 " + (coalH[m] < 0 ? "采全高" : $"{hC:0.##}m") + $" ⇒ {lvlC} 级");

                    // 露煤带（口径 A）：内缘=顶板∩前界①，外缘=顶板∩前界②；宽度带符号；R40 走位不再跳到带外缘
                    if (Pe.HasValue && Pr.Value.Z > zRim) dgExpOverRim++;
                    if (Pe.HasValue && Pr.Value.Z <= zRim)
                    {
                        double ex0 = Pe.Value.X - Pr.Value.X, ey0 = Pe.Value.Y - Pr.Value.Y;
                        double wSig = ex0 * dix + ey0 * diy;
                        if (wSig > EPS) AddExposure(m, i, Math.Sqrt(ex0 * ex0 + ey0 * ey0), Pr.Value.X, Pr.Value.Y, Pr.Value, Pe.Value);
                        else dgExpNoGain++;
                    }
                }

                if (TraceWalk) r.Walk.Add($"  L{w} vi{i} 封顶前 pz={pz:0.##} 基准=前界" + (bandRef[nSeam] < 0 ? "①" : $"②「{seams[bandRef[nSeam]].Name}」") + $" 坑沿={RockCeil(nSeam):0.##}");
                StackRock(nSeam, RockCeil(nSeam), "岩·封顶半台阶");
            }
        }

        // R48 工作平盘放宽（整条线刚性平移、逐点推量、迭代收敛）；R58 只推岩台阶
        double bermTarget = berm;
        int dgBermPush = 0, dgBermIter = 0;
        double dgBermMaxPush = 0;
        bool bermAll  = p.WidenBerm && !DisableBermWiden;
        bool bermRock = p.MinBermOnRock && !DisableMinBermOnRock;
        if (bermRock && !bermAll) bermTarget = Math.Max(p.MinBerm, berm);
        if (bermAll || bermRock)
        {
            var stnIdx = new Dictionary<(int Line, int Vi), List<(BenchLine Bl, int I)>>();
            var dirOf = new Dictionary<BenchLine, (double X, double Y)>();
            foreach (var bl in map.Values)
            {
                if (bl.Kind != 0 && bl.Kind != 1) continue;
                int n = Math.Min(Math.Min(bl.Toe.Count, bl.Crest.Count), bl.Vi.Count);
                double ax = 0, ay = 0;
                for (int i = 0; i < n; i++)
                {
                    if (bl.Vi[i] < 0) continue;
                    var key = (bl.LineIndex, bl.Vi[i]);
                    if (!stnIdx.TryGetValue(key, out var l)) stnIdx[key] = l = new();
                    l.Add((bl, i));
                    double dx2 = bl.Crest[i].X - bl.Toe[i].X, dy2 = bl.Crest[i].Y - bl.Toe[i].Y;
                    double l2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
                    if (l2 > 1e-9) { ax += dx2 / l2; ay += dy2 / l2; }
                }
                double al = Math.Sqrt(ax * ax + ay * ay);
                if (al > 1e-9) dirOf[bl] = (ax / al, ay / al);
            }
            var pushAt = new Dictionary<BenchLine, double[]>();
            foreach (var bl in map.Values) if (bl.Kind == 0 || bl.Kind == 1) pushAt[bl] = new double[bl.Toe.Count];
            for (int iter = 0; iter < 64; iter++)
            {
                bool grew = false;
                foreach (var kv in stnIdx)
                {
                    var list = kv.Value.OrderBy(t => t.Bl.Crest[t.I].Z).ToList();
                    if (list.Count < 2) continue;
                    double ux = 0, uy = 0, best = -1;
                    foreach (var (b2, i2) in list)
                    {
                        double dx2 = b2.Crest[i2].X - b2.Toe[i2].X, dy2 = b2.Crest[i2].Y - b2.Toe[i2].Y;
                        double l2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
                        if (l2 > best) { best = l2; ux = dx2 / l2; uy = dy2 / l2; }
                    }
                    if (best <= 1e-9) continue;
                    double Proj(BenchLine b3, int i3, (double X, double Y, double Z) q)
                    {
                        double pv = pushAt[b3][i3];
                        if (!dirOf.TryGetValue(b3, out var d3)) d3 = (ux, uy);
                        return (q.X + d3.X * pv) * ux + (q.Y + d3.Y * pv) * uy;
                    }
                    for (int j = 1; j < list.Count; j++)
                    {
                        var (bLo, iLo) = list[j - 1]; var (bHi, iHi) = list[j];
                        if (bHi.Toe[iHi].Z < bLo.Crest[iLo].Z - SliverFaceM) continue;
                        if (!bermAll && bHi.Kind != 1) continue;
                        double req = (Proj(bLo, iLo, bLo.Crest[iLo]) + bermTarget) - Proj(bHi, iHi, bHi.Toe[iHi]);
                        if (req > 0.01) { pushAt[bHi][iHi] += req; grew = true; }
                    }
                }
                foreach (var kv2 in pushAt)
                {
                    var a2 = kv2.Value; if (a2.Length < 2) continue;
                    double mx = 0;
                    for (int i = 0; i < a2.Length; i++) mx = Math.Max(mx, a2[i]);
                    for (int i = 0; i < a2.Length; i++) if (a2[i] < mx - 1e-9) { a2[i] = mx; grew = true; }
                }
                dgBermIter = iter + 1;
                if (!grew) break;
            }
            foreach (var kv in pushAt)
            {
                var bl = kv.Key; var arr = kv.Value;
                if (!dirOf.TryGetValue(bl, out var d)) continue;
                bool moved = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] <= 0.01) continue;
                    if (i < bl.Toe.Count) { var t = bl.Toe[i]; bl.Toe[i] = (t.X + d.X * arr[i], t.Y + d.Y * arr[i], t.Z); }
                    if (i < bl.Crest.Count) { var q = bl.Crest[i]; bl.Crest[i] = (q.X + d.X * arr[i], q.Y + d.Y * arr[i], q.Z); }
                    moved = true; dgBermMaxPush = Math.Max(dgBermMaxPush, arr[i]);
                }
                if (moved) dgBermPush++;
            }
        }

        // 尖灭收口（稍缓冲软收口；R20 标准格岩台阶只在平面上收）
        void SoftClose(BenchLine bl, bool atStart, double? convOverride = null)
        {
            var toe = bl.Toe; var cr = bl.Crest;
            int n = Math.Min(toe.Count, cr.Count); if (n < 2) return;
            int e = atStart ? 0 : n - 1, e2 = atStart ? 1 : n - 2;
            var te = toe[e]; var ce = cr[e];
            double dx = te.X - toe[e2].X, dy = te.Y - toe[e2].Y;
            double L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-6) return;
            dx /= L; dy /= L;
            double buf = Math.Min(L * 0.6, 6.0);
            double mx = (te.X + ce.X) * 0.5, my = (te.Y + ce.Y) * 0.5, mz = (te.Z + ce.Z) * 0.5;
            double conv = convOverride ?? ((bl.Kind == 0 || (bl.Kind == 1 && bl.RockLevel < 0)) ? 1.0 : 0.5);
            double convZ = (bl.Kind == 1 && bl.RockLevel >= 0) ? 0.0 : conv;
            (double X, double Y, double Z) tb = (te.X + dx * buf + (mx - te.X) * conv, te.Y + dy * buf + (my - te.Y) * conv, te.Z + (mz - te.Z) * convZ);
            (double X, double Y, double Z) cb = (ce.X + dx * buf + (mx - ce.X) * conv, ce.Y + dy * buf + (my - ce.Y) * conv, ce.Z + (mz - ce.Z) * convZ);
            if (atStart) { toe.Insert(0, tb); cr.Insert(0, cb); bl.Vi.Insert(0, -1); }
            else { toe.Add(tb); cr.Add(cb); bl.Vi.Add(-1); }
        }
        // R28：按断因决定收不收口；R61：标准格被上层煤底板压掉也是尖灭
        bool IsPinchEnd(BenchLine bl, int viOutside)
        {
            if (viOutside < 0) return false;
            if (bl.Kind == 0)
            {
                var key = (bl.LineIndex, bl.SeamIndex, viOutside);
                if (coalStop.TryGetValue(key, out int why)) return why == STOP_PINCH;
                return false;
            }
            if (bl.Kind == 1 && bl.RockLevel < 0) return bl.GapSeam >= 0 && gapPinch.Contains((bl.LineIndex, bl.GapSeam, viOutside));
            if (bl.Kind == 1 && bl.RockLevel >= 0 && bl.GapSeam >= 0 && stackTop.TryGetValue((bl.LineIndex, bl.GapSeam, viOutside), out double zStkOut))
                return GridZ(bl.RockLevel) > zStkOut + 1e-6;
            return false;
        }
        foreach (var bl in map.Values)
        {
            if ((bl.Kind != 0 && bl.Kind != 1) || bl.Toe.Count < 2 || bl.Crest.Count < 2) continue;
            int llast = (bl.LineIndex >= 0 && bl.LineIndex < lineLastVi.Length) ? lineLastVi[bl.LineIndex] : int.MaxValue;
            if (bl.FirstVi > 0 && IsPinchEnd(bl, bl.FirstVi - 1))  { SoftClose(bl, atStart: true);  bl.PinchClosedStart = true; dgPinchClose++; }
            if (bl.LastVi  < llast && IsPinchEnd(bl, bl.LastVi + 1)) { SoftClose(bl, atStart: false); bl.PinchClosedEnd = true; dgPinchClose++; }
            else if (bl.LastVi < llast) dgCutEnd++;
            if (bl.FirstVi > 0 && !IsPinchEnd(bl, bl.FirstVi - 1)) dgCutEnd++;
        }

        // R35 岩台阶交接给煤台阶（因煤而断的端）；R60 默认平直收尖（HandoffToCoal=false）
        int dgHandover = 0;
        {
            double snapMax = rockH / tanAlphaB;
            var coalNow = map.Values.Where(x => x.Toe.Count >= 2 && (x.Kind == 0 || (x.Kind == 1 && x.RockLevel < 0))).ToList();
            var allLines = map.Values.Where(x => x.Toe.Count >= 2).ToList();
            static bool Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) u, (double X, double Y, double Z) v)
            {
                double ax = b.X - a.X, ay = b.Y - a.Y, bx = v.X - u.X, by = v.Y - u.Y;
                double den = ax * by - ay * bx;
                if (Math.Abs(den) < 1e-12) return false;
                double t = ((u.X - a.X) * by - (u.Y - a.Y) * bx) / den;
                double s2 = ((u.X - a.X) * ay - (u.Y - a.Y) * ax) / den;
                return t > 1e-6 && t < 1 - 1e-6 && s2 > 1e-6 && s2 < 1 - 1e-6;
            }
            (double X, double Y, double Z) SnapToCoal((double X, double Y, double Z) e, int line)
            {
                double best = snapMax * snapMax; var hit = e; bool ok = false;
                foreach (var cl in coalNow)
                {
                    if (cl.LineIndex != line) continue;
                    foreach (var poly in new[] { cl.Toe, cl.Crest })
                        for (int i = 0; i + 1 < poly.Count; i++)
                        {
                            double ax = poly[i + 1].X - poly[i].X, ay = poly[i + 1].Y - poly[i].Y;
                            double l2 = ax * ax + ay * ay; if (l2 < 1e-12) continue;
                            double t = ((e.X - poly[i].X) * ax + (e.Y - poly[i].Y) * ay) / l2;
                            t = Math.Max(0, Math.Min(1, t));
                            double qx = poly[i].X + t * ax, qy = poly[i].Y + t * ay;
                            double d2 = (qx - e.X) * (qx - e.X) + (qy - e.Y) * (qy - e.Y);
                            if (d2 < best) { best = d2; hit = (qx, qy, e.Z); ok = true; }
                        }
                }
                return ok ? hit : e;
            }
            foreach (var bl in map.Values)
            {
                if (bl.Kind != 1 || bl.RockLevel < 0 || bl.Toe.Count < 2 || bl.Crest.Count < 2) continue;
                int llast = (bl.LineIndex >= 0 && bl.LineIndex < lineLastVi.Length) ? lineLastVi[bl.LineIndex] : int.MaxValue;
                foreach (bool atStart in new[] { true, false })
                {
                    if (atStart ? bl.FirstVi <= 0 : bl.LastVi >= llast) continue;
                    int vOut = atStart ? bl.FirstVi - 1 : bl.LastVi + 1;
                    if (!blockedCoal.Contains((bl.LineIndex, vOut, bl.RockLevel))) continue;
                    if (!p.HandoffToCoal)
                    {
                        bool already = atStart ? bl.PinchClosedStart : bl.PinchClosedEnd;
                        if (already) continue;
                        SoftClose(bl, atStart, convOverride: 1.0);
                        if (atStart) bl.PinchClosedStart = true; else bl.PinchClosedEnd = true;
                        dgFlatPinch++;
                        continue;
                    }
                    int e = atStart ? 0 : bl.Toe.Count - 1;
                    int ec = atStart ? 0 : bl.Crest.Count - 1;
                    var t2 = SnapToCoal(bl.Toe[e], bl.LineIndex);
                    double mvx = t2.X - bl.Toe[e].X, mvy = t2.Y - bl.Toe[e].Y;
                    if (Math.Abs(mvx) < 1e-9 && Math.Abs(mvy) < 1e-9) continue;
                    var c2 = (X: bl.Crest[ec].X + mvx, Y: bl.Crest[ec].Y + mvy, Z: bl.Crest[ec].Z);
                    bool bad = false;
                    foreach (var ol in allLines)
                    {
                        if (ol.LineIndex != bl.LineIndex || ReferenceEquals(ol, bl)) continue;
                        foreach (var poly in new[] { ol.Toe, ol.Crest })
                            for (int q = 0; q + 1 < poly.Count && !bad; q++)
                                if (Cross(bl.Toe[e], t2, poly[q], poly[q + 1]) || Cross(bl.Crest[ec], c2, poly[q], poly[q + 1])) bad = true;
                        if (bad) break;
                    }
                    if (bad) continue;
                    if (atStart) { bl.Toe.Insert(0, t2); bl.Crest.Insert(0, c2); bl.Vi.Insert(0, -1); }
                    else { bl.Toe.Add(t2); bl.Crest.Add(c2); bl.Vi.Add(-1); }
                    dgHandover++;
                }
            }
        }

        // 角部：角平分线裁剪 → 同类同级搭接（R36 标准格单列成衔接线）
        var stitched = new HashSet<(BenchLine bl, bool atStart)>();
        var cornerJoinLines = new List<BenchLine>();
        List<BenchLine> bridgeLines = new();
        int dgCoalBefore = 0, dgCoalAfter = 0;
        foreach (var bl in map.Values) if (bl.Kind == 0 && bl.Toe.Count >= 2) dgCoalBefore++;
        if (cornerLinks != null && workLines.Count >= 2)
            foreach (var lk in cornerLinks)
            {
                ClipCornerToBisector(map.Values, lk, workLines);
                StitchCornerBenches(map.Values, lk, lineLastVi, stitched, cornerJoinLines);
            }
        foreach (var bl in map.Values) if (bl.Kind == 0 && bl.Toe.Count >= 2) dgCoalAfter++;

        // R19/R44/R49/R50 L 角补口：两臂各沿自己走向直着延伸交出折角点（越界退轴对齐），归衔接类 Kind=3
        int dgArc = 0;
        if (cornerLinks != null && workLines.Count >= 2)
            foreach (var lk in cornerLinks)
            {
                var wa = workLines[lk.A]; var wb = workLines[lk.B];
                var pa0 = lk.AStart ? wa.Baseline[0] : wa.Baseline[^1];
                var pb0 = lk.BStart ? wb.Baseline[0] : wb.Baseline[^1];
                double ox = (pa0.X + pb0.X) * 0.5, oy = (pa0.Y + pb0.Y) * 0.5;

                var lvA = new Dictionary<int, BenchLine>(); var lvB = new Dictionary<int, BenchLine>();
                (double X, double Y, double Z) NearEnd(BenchLine b)
                {
                    double d0 = (b.Toe[0].X - ox) * (b.Toe[0].X - ox) + (b.Toe[0].Y - oy) * (b.Toe[0].Y - oy);
                    double d1 = (b.Toe[^1].X - ox) * (b.Toe[^1].X - ox) + (b.Toe[^1].Y - oy) * (b.Toe[^1].Y - oy);
                    return d0 <= d1 ? b.Toe[0] : b.Toe[^1];
                }
                (double X, double Y, double Z) NearEndCrest(BenchLine b)
                {
                    double d0 = (b.Toe[0].X - ox) * (b.Toe[0].X - ox) + (b.Toe[0].Y - oy) * (b.Toe[0].Y - oy);
                    double d1 = (b.Toe[^1].X - ox) * (b.Toe[^1].X - ox) + (b.Toe[^1].Y - oy) * (b.Toe[^1].Y - oy);
                    return d0 <= d1 ? b.Crest[0] : b.Crest[^1];
                }
                double NearR(BenchLine b)
                    => Math.Min(Math.Sqrt((b.Toe[0].X - ox) * (b.Toe[0].X - ox) + (b.Toe[0].Y - oy) * (b.Toe[0].Y - oy)),
                                Math.Sqrt((b.Toe[^1].X - ox) * (b.Toe[^1].X - ox) + (b.Toe[^1].Y - oy) * (b.Toe[^1].Y - oy)));
                foreach (var bl in map.Values)
                {
                    if (bl.Kind != 1 || bl.RockLevel < 0 || bl.Toe.Count < 2) continue;
                    var tgt = bl.LineIndex == lk.A ? lvA : bl.LineIndex == lk.B ? lvB : null;
                    if (tgt == null) continue;
                    if (!tgt.TryGetValue(bl.RockLevel, out var cur) || NearR(bl) < NearR(cur)) tgt[bl.RockLevel] = bl;
                }
                (double X, double Y) Bearing(Dictionary<int, BenchLine> lv)
                {
                    double bx = 0, by = 0;
                    foreach (var b in lv.Values)
                    {
                        var e = NearEnd(b);
                        double dx0 = e.X - ox, dy0 = e.Y - oy, l0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
                        if (l0 > 1e-6) { bx += dx0 / l0; by += dy0 / l0; }
                    }
                    double bl0 = Math.Sqrt(bx * bx + by * by);
                    return bl0 > 1e-9 ? (bx / bl0, by / bl0) : (0, 0);
                }
                var bearA = Bearing(lvA); var bearB = Bearing(lvB);
                var allLv = new SortedSet<int>(lvA.Keys); allLv.UnionWith(lvB.Keys);
                foreach (int lvK in allLv)
                {
                    lvA.TryGetValue(lvK, out var bA0); lvB.TryGetValue(lvK, out var bB0);
                    var src = bA0 ?? bB0; if (src == null) continue;
                    double R(BenchLine? b)
                    {
                        if (b == null) return double.MaxValue;
                        var e0 = NearEnd(b);
                        return Math.Sqrt((e0.X - ox) * (e0.X - ox) + (e0.Y - oy) * (e0.Y - oy));
                    }
                    double rA0 = R(bA0), rB0 = R(bB0);
                    bool anchorIsA = rA0 <= rB0;
                    double rAnchor = Math.Min(rA0, rB0);
                    if (rAnchor >= double.MaxValue * 0.5) continue;
                    bool realA = bA0 != null && (anchorIsA || Math.Abs(rA0 - rAnchor) <= rockH / tanAlphaB);
                    bool realB = bB0 != null && (!anchorIsA || Math.Abs(rB0 - rAnchor) <= rockH / tanAlphaB);
                    var bA = anchorIsA ? bA0! : bB0!;
                    double zT0 = NearEnd(bA).Z, zC0 = NearEndCrest(bA).Z;
                    double runC0 = (zC0 - zT0) / Math.Max(0.1, Math.Tan(Math.Max(10.0, Math.Min(89.0, p.RockFaceDeg)) * Math.PI / 180.0));
                    (double X, double Y, double Z) Synth((double X, double Y) bear, bool crest)
                        => bear.X == 0 && bear.Y == 0
                           ? (crest ? NearEndCrest(bA) : NearEnd(bA))
                           : (ox + (crest ? Math.Max(1.0, rAnchor - runC0) : rAnchor) * bear.X, oy + (crest ? Math.Max(1.0, rAnchor - runC0) : rAnchor) * bear.Y, crest ? zC0 : zT0);
                    var eA = realA ? NearEnd(bA0!) : Synth(bearA, false);
                    var cA = realA ? NearEndCrest(bA0!) : Synth(bearA, true);
                    var eB = realB ? NearEnd(bB0!) : Synth(bearB, false);
                    var cB = realB ? NearEndCrest(bB0!) : Synth(bearB, true);
                    if (!realA && !realB) continue;
                    double rA = Math.Sqrt((eA.X - ox) * (eA.X - ox) + (eA.Y - oy) * (eA.Y - oy));
                    double rB = Math.Sqrt((eB.X - ox) * (eB.X - ox) + (eB.Y - oy) * (eB.Y - oy));
                    double gap = Math.Sqrt((eA.X - eB.X) * (eA.X - eB.X) + (eA.Y - eB.Y) * (eA.Y - eB.Y));
                    if (gap < 10.0) continue;
                    var join = new BenchLine
                    {
                        IsCoal = false, Kind = 3, SeamIndex = -1, RockLevel = bA.RockLevel, Level = bA.Level,
                        SeamName = bA.SeamName + "·角部补口", LineIndex = bA.LineIndex, GapSeam = bA.GapSeam, FirstVi = bA.FirstVi, LastVi = bA.LastVi
                    };
                    double zT0d = eA.Z, zC0d = cA.Z;
                    (double X, double Y) Strike(BenchLine? b, (double X, double Y, double Z) e, (double X, double Y) bear)
                    {
                        if (b != null && b.Toe.Count >= 2)
                        {
                            bool head = (b.Toe[0].X - e.X) * (b.Toe[0].X - e.X) + (b.Toe[0].Y - e.Y) * (b.Toe[0].Y - e.Y) < 1e-6;
                            var inner = head ? b.Toe[1] : b.Toe[^2];
                            double sx = e.X - inner.X, sy = e.Y - inner.Y;
                            double sl = Math.Sqrt(sx * sx + sy * sy);
                            if (sl > 1e-6) return (sx / sl, sy / sl);
                        }
                        return (-bear.Y, bear.X);
                    }
                    var sA = Strike(realA ? bA0 : null, eA, bearA);
                    var sB = Strike(realB ? bB0 : null, eB, bearB);
                    double kneeLimit = gap * 2.0 + rockH / tanAlphaB;
                    (double X, double Y, double Z) kneeT, kneeC;
                    double den = sA.X * (-sB.Y) - sA.Y * (-sB.X);
                    double tA = Math.Abs(den) > 1e-9 ? ((eB.X - eA.X) * (-sB.Y) - (eB.Y - eA.Y) * (-sB.X)) / den : double.NaN;
                    if (!double.IsNaN(tA) && Math.Abs(tA) <= kneeLimit)
                    {
                        kneeT = (eA.X + sA.X * tA, eA.Y + sA.Y * tA, zT0d);
                        kneeC = (cA.X + sA.X * tA, cA.Y + sA.Y * tA, zC0d);
                    }
                    else
                    {
                        double k1d = (eA.X - ox) * (eA.X - ox) + (eB.Y - oy) * (eB.Y - oy);
                        double k2d = (eB.X - ox) * (eB.X - ox) + (eA.Y - oy) * (eA.Y - oy);
                        bool useK1 = k1d <= k2d;
                        kneeT = useK1 ? (eA.X, eB.Y, zT0d) : (eB.X, eA.Y, zT0d);
                        kneeC = useK1 ? (cA.X, cB.Y, zC0d) : (cB.X, cA.Y, zC0d);
                    }
                    join.Toe.Add(eA); join.Crest.Add(cA);
                    join.Toe.Add(kneeT); join.Crest.Add(kneeC);
                    join.Toe.Add(eB); join.Crest.Add(cB);
                    map["CJ:" + lk.A + "-" + lk.B + "#" + bA.RockLevel] = join;
                    dgArc++;
                }
            }

        // 岩台阶断端 → 高一格搭接（只岩对岩、只高一格；R32/R33/R34 不搭煤接管的标高、不穿煤）
        {
            var rockByLvl = new Dictionary<(int line, int seam, int lvl), List<BenchLine>>();
            foreach (var bl in map.Values)
                if (bl.Kind == 1)
                {
                    var key = (bl.LineIndex, bl.SeamIndex, bl.RockLevel);
                    if (!rockByLvl.TryGetValue(key, out var lst)) rockByLvl[key] = lst = new List<BenchLine>();
                    lst.Add(bl);
                }
            double lap = berm + runFaceB;
            double maxLap2 = (1.6 * lap) * (1.6 * lap);
            var bridges = new List<BenchLine>();
            bool LapRockUp(BenchLine bl, bool atStart)
            {
                if (!rockByLvl.TryGetValue((bl.LineIndex, bl.SeamIndex, bl.RockLevel + 1), out var ups)) return false;
                var toe = bl.Toe; var cr = bl.Crest;
                int n = Math.Min(toe.Count, cr.Count); if (n < 2) return false;
                var te = atStart ? toe[0] : toe[n - 1];
                var ceEnd = atStart ? cr[0] : cr[n - 1];
                double adx = ceEnd.X - te.X, ady = ceEnd.Y - te.Y;
                double al2 = Math.Sqrt(adx * adx + ady * ady); if (al2 < 1e-9) return false; adx /= al2; ady /= al2;
                BenchLine? bestUp = null; bool bestEnd = false; double best = double.MaxValue;
                foreach (var up in ups)
                {
                    if (up == bl || up.Toe.Count < 2 || up.Crest.Count < 2) continue;
                    foreach (bool s in new[] { true, false })
                    {
                        var ut = s ? up.Toe[0] : up.Toe[up.Toe.Count - 1];
                        if ((ut.X - te.X) * adx + (ut.Y - te.Y) * ady < -1e-6) continue;
                        double d = (ut.X - te.X) * (ut.X - te.X) + (ut.Y - te.Y) * (ut.Y - te.Y);
                        if (d < best) { best = d; bestUp = up; bestEnd = s; }
                    }
                }
                if (bestUp == null || best > maxLap2) return false;
                if (bl.RockLevel >= 0)
                {
                    int vA = atStart ? bl.FirstVi : bl.LastVi;
                    int vB = bestEnd ? bestUp.FirstVi : bestUp.LastVi;
                    for (int vq = Math.Min(vA, vB); vq <= Math.Max(vA, vB); vq++)
                        if (blockedLv.Contains((bl.LineIndex, vq, bl.RockLevel)) || blockedLv.Contains((bl.LineIndex, vq, bl.RockLevel + 1))) return false;
                }
                var bt = bestEnd ? bestUp.Toe[0] : bestUp.Toe[bestUp.Toe.Count - 1];
                var bc = bestEnd ? bestUp.Crest[0] : bestUp.Crest[bestUp.Crest.Count - 1];
                var link = new BenchLine
                {
                    IsCoal = false, Kind = 3, SeamIndex = -1, RockLevel = bl.RockLevel, Level = bl.RockLevel, LineIndex = bl.LineIndex,
                    SeamName = $"搭接·{GridZ(bl.RockLevel):0}→{GridZ(bl.RockLevel + 1):0}",
                    FirstVi = atStart ? bl.FirstVi : bl.LastVi, LastVi = atStart ? bl.FirstVi : bl.LastVi,
                };
                link.Toe.Add(te); link.Toe.Add(bt);
                link.Crest.Add(ceEnd); link.Crest.Add(bc);
                bridges.Add(link);
                return true;
            }
            foreach (var bl in map.Values)
            {
                if (bl.Kind != 1 || bl.Toe.Count < 2 || bl.Crest.Count < 2) continue;
                int llast = (bl.LineIndex >= 0 && bl.LineIndex < lineLastVi.Length) ? lineLastVi[bl.LineIndex] : int.MaxValue;
                if (bl.FirstVi > 0 && !stitched.Contains((bl, true)) && !(bl.RockLevel >= 0 && blockedLv.Contains((bl.LineIndex, bl.FirstVi - 1, bl.RockLevel)))) LapRockUp(bl, atStart: true);
                if (bl.LastVi < llast && !stitched.Contains((bl, false)) && !(bl.RockLevel >= 0 && blockedLv.Contains((bl.LineIndex, bl.LastVi + 1, bl.RockLevel)))) LapRockUp(bl, atStart: false);
            }
            int dgLapCut = 0;
            var coalLines = map.Values.Where(x => x.Kind == 0 && x.Toe.Count >= 2).ToList();
            foreach (var key in map.Where(kv => kv.Key.StartsWith("CJ:", StringComparison.Ordinal) || kv.Key.StartsWith("ARC:", StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
            {
                var cj = map[key];
                bool bad2 = false;
                foreach (var cl in coalLines)
                {
                    if (cl.LineIndex != cj.LineIndex) continue;
                    for (int a = 0; a + 1 < cj.Toe.Count && !bad2; a++)
                        for (int b4 = 0; b4 + 1 < cl.Toe.Count && !bad2; b4++)
                            if (SegHit(cj.Toe[a], cj.Toe[a + 1], cl.Toe[b4], cl.Toe[b4 + 1]) || SegHit(cj.Toe[a], cj.Toe[a + 1], cl.Crest[b4], cl.Crest[b4 + 1])) bad2 = true;
                    if (bad2) break;
                }
                if (bad2) { map.Remove(key); dgLapCut++; }
            }
            for (int a2 = cornerJoinLines.Count - 1; a2 >= 0; a2--)
            {
                var cj = cornerJoinLines[a2]; bool bad3 = false;
                foreach (var cl in coalLines)
                {
                    if (cl.LineIndex != cj.LineIndex) continue;
                    for (int a = 0; a + 1 < cj.Toe.Count && !bad3; a++)
                        for (int b4 = 0; b4 + 1 < cl.Toe.Count && !bad3; b4++)
                            if (SegHit(cj.Toe[a], cj.Toe[a + 1], cl.Toe[b4], cl.Toe[b4 + 1]) || SegHit(cj.Toe[a], cj.Toe[a + 1], cl.Crest[b4], cl.Crest[b4 + 1])) bad3 = true;
                    if (bad3) break;
                }
                if (bad3) { cornerJoinLines.RemoveAt(a2); dgLapCut++; }
            }
            bridgeLines = new List<BenchLine>();
            foreach (var lk3 in bridges)
            {
                bool hit = false;
                foreach (var cl in coalLines)
                {
                    if (cl.LineIndex != lk3.LineIndex) continue;
                    for (int a = 0; a + 1 < lk3.Toe.Count && !hit; a++)
                        for (int b3 = 0; b3 + 1 < cl.Toe.Count && !hit; b3++)
                            if (SegHit(lk3.Toe[a], lk3.Toe[a + 1], cl.Toe[b3], cl.Toe[b3 + 1]) || SegHit(lk3.Toe[a], lk3.Toe[a + 1], cl.Crest[b3], cl.Crest[b3 + 1])) hit = true;
                    if (hit) break;
                }
                if (hit) dgLapCut++; else bridgeLines.Add(lk3);
            }
            dgLapCutN = dgLapCut;
        }

        // 煤台阶跨工作线角部拼接（R17 按端点互距挑、闸 1.6×h/tanα）；R10 煤台阶不并段
        static void JoinAtCorner(BenchLine a, BenchLine b, double cx, double cy)
        {
            double D2((double X, double Y, double Z) p) => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy);
            if (D2(a.Toe[0]) < D2(a.Toe[a.Toe.Count - 1])) { a.Toe.Reverse(); a.Crest.Reverse(); a.Vi.Reverse(); }
            if (D2(b.Toe[b.Toe.Count - 1]) < D2(b.Toe[0])) { b.Toe.Reverse(); b.Crest.Reverse(); b.Vi.Reverse(); }
            a.Toe.AddRange(b.Toe); a.Crest.AddRange(b.Crest);
            for (int q = 0; q < b.Toe.Count; q++) a.Vi.Add(-1);
            b.Toe.Clear(); b.Crest.Clear(); b.Vi.Clear();
        }
        {
            var coalByLineSeam = new Dictionary<(int Line, int Seam), List<BenchLine>>();
            foreach (var bl in map.Values)
                if (bl.Kind == 0 && bl.Toe.Count >= 2)
                {
                    var k = (bl.LineIndex, bl.SeamIndex);
                    if (!coalByLineSeam.TryGetValue(k, out var l)) coalByLineSeam[k] = l = new();
                    l.Add(bl);
                }
            if (cornerLinks != null)
                foreach (var lk in cornerLinks)
                {
                    var wa = workLines[lk.A]; var wb = workLines[lk.B];
                    var pa = lk.AStart ? wa.Baseline[0] : wa.Baseline[^1];
                    var pb = lk.BStart ? wb.Baseline[0] : wb.Baseline[^1];
                    double cx = (pa.X + pb.X) * 0.5, cy = (pa.Y + pb.Y) * 0.5;
                    foreach (int m in coalByLineSeam.Keys.Where(k => k.Line == lk.A).Select(k => k.Seam).ToList())
                    {
                        if (!coalByLineSeam.TryGetValue((lk.A, m), out var ga) || !coalByLineSeam.TryGetValue((lk.B, m), out var gb)) continue;
                        double gateJ = 1.6 * (rockH / tanAlphaB);
                        BenchLine? ja = null, jb = null; double jd2 = double.MaxValue, jx = cx, jy = cy;
                        foreach (var u in ga)
                        {
                            if (u.Toe.Count < 2) continue;
                            foreach (var v in gb)
                            {
                                if (v.Toe.Count < 2 || ReferenceEquals(u, v)) continue;
                                foreach (var pu in new[] { u.Toe[0], u.Toe[^1] })
                                    foreach (var pv in new[] { v.Toe[0], v.Toe[^1] })
                                    {
                                        double dd = (pu.X - pv.X) * (pu.X - pv.X) + (pu.Y - pv.Y) * (pu.Y - pv.Y);
                                        if (dd >= jd2) continue;
                                        jd2 = dd; ja = u; jb = v;
                                        jx = (pu.X + pv.X) * 0.5; jy = (pu.Y + pv.Y) * 0.5;
                                    }
                            }
                        }
                        if (ja == null || jb == null || jd2 > gateJ * gateJ) continue;
                        JoinAtCorner(ja, jb, jx, jy);
                    }
                }
        }

        // R12 剔单站离群
        {
            double lvlScale = rockH / Math.Max(1e-6, Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0));
            foreach (var bl in map.Values)
            {
                if (bl.Kind == 3 || bl.Toe.Count < 3) continue;
                var wl2 = workLines[Math.Max(0, Math.Min(workLines.Count - 1, bl.LineIndex))];
                double ax2 = 0, ay2 = 0;
                foreach (var sp in wl2.Samples) { ax2 += sp.Dx; ay2 += sp.Dy; }
                double al3 = Math.Sqrt(ax2 * ax2 + ay2 * ay2);
                if (al3 < 1e-9) continue; ax2 /= al3; ay2 /= al3;
                double Adv((double X, double Y, double Z) pt) => pt.X * ax2 + pt.Y * ay2;
                for (int i = bl.Toe.Count - 2; i >= 1; i--)
                {
                    if (i + 1 >= bl.Toe.Count) continue;
                    double sp0 = Adv(bl.Toe[i - 1]), sp1 = Adv(bl.Toe[i]), sp2 = Adv(bl.Toe[i + 1]);
                    if (Math.Abs(sp1 - sp0) > lvlScale && Math.Abs(sp1 - sp2) > lvlScale && Math.Abs(sp2 - sp0) <= lvlScale)
                    {
                        bl.Toe.RemoveAt(i);
                        if (i < bl.Vi.Count) bl.Vi.RemoveAt(i);
                        if (i < bl.Crest.Count) bl.Crest.RemoveAt(i);
                        dgOutlier++;
                    }
                }
            }
        }

        // R34 收尾：拿定稿的煤线再筛一遍所有衔接类（坡底坡顶都查）
        {
            var coalFinal = map.Values.Where(x => x.Kind == 0 && x.Toe.Count >= 2).ToList();
            bool CrossesCoal(BenchLine cj)
            {
                foreach (var cl in coalFinal)
                {
                    if (cl.LineIndex != cj.LineIndex) continue;
                    int nc = Math.Min(cl.Toe.Count, cl.Crest.Count);
                    foreach (var jp in new[] { cj.Toe, cj.Crest })
                        for (int a = 0; a + 1 < jp.Count; a++)
                            for (int b5 = 0; b5 + 1 < nc; b5++)
                                if (SegHit(jp[a], jp[a + 1], cl.Toe[b5], cl.Toe[b5 + 1]) || SegHit(jp[a], jp[a + 1], cl.Crest[b5], cl.Crest[b5 + 1])) return true;
                }
                return false;
            }
            foreach (var key in map.Where(kv => kv.Value.Kind == 3).Select(kv => kv.Key).ToList())
                if (CrossesCoal(map[key])) { map.Remove(key); dgLapCutN++; }
            bridgeLines.RemoveAll(x => { bool h = CrossesCoal(x); if (h) dgLapCutN++; return h; });
            cornerJoinLines.RemoveAll(x => { bool h = CrossesCoal(x); if (h) dgLapCutN++; return h; });
        }

        // R47 用现状面构建尖灭（削顶 / 裁开 / 面高削到 0 尖灭）；R57 默认关（PinchByTerrain）
        int dgShaveStn = 0, dgTerrCut = 0, dgTerrPinch = 0, dgTerrDrop = 0;
        double faceTol = SliverFaceM;
        (double X, double Y, double Z) Lerp((double X, double Y, double Z) a, (double X, double Y, double Z) b, double t) => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        double AliveMargin(BenchLine b, int i, int j, double t)
        {
            var tp = Lerp(b.Toe[i], b.Toe[j], t);
            var cp = Lerp(b.Crest[i], b.Crest[j], t);
            double zc = cp.Z;
            if (currentSurface != null && currentSurface.TrySampleZ(cp.X, cp.Y, out double zt) && zc > zt) zc = zt;
            double m1 = zc - tp.Z - faceTol;
            double m2 = currentSurface != null && currentSurface.TrySampleZ(tp.X, tp.Y, out double ztT2) ? ztT2 + faceTol - tp.Z : double.MaxValue;
            return Math.Min(m1, m2);
        }
        List<BenchLine> CutByTerrain(BenchLine bl)
        {
            var keep = new List<BenchLine> { bl };
            if (currentSurface == null || bl.Kind == 2) return keep;
            int n = Math.Min(bl.Toe.Count, bl.Crest.Count);
            if (n < 2) return keep;
            var zcE = new double[n]; var shaved = new bool[n]; var alive = new bool[n];
            for (int i = 0; i < n; i++)
            {
                double zc = bl.Crest[i].Z;
                if (currentSurface.TrySampleZ(bl.Crest[i].X, bl.Crest[i].Y, out double zt) && zc > zt) { zc = zt; shaved[i] = true; }
                zcE[i] = zc;
                bool toeIn = !currentSurface.TrySampleZ(bl.Toe[i].X, bl.Toe[i].Y, out double ztT) || bl.Toe[i].Z <= ztT + faceTol;
                alive[i] = zc - bl.Toe[i].Z >= faceTol && toeIn;
            }
            var segs = new List<(int A, int B)>();
            for (int i = 0, s = -1; i < n; i++)
            {
                if (alive[i]) { if (s < 0) s = i; if (i == n - 1) segs.Add((s, i)); }
                else if (s >= 0) { segs.Add((s, i - 1)); s = -1; }
            }
            if (segs.Count == 1 && segs[0].A == 0 && segs[0].B == n - 1)
            {
                bl.Shaved.Clear();
                for (int i = 0; i < n; i++)
                {
                    if (shaved[i]) { var c = bl.Crest[i]; bl.Crest[i] = (c.X, c.Y, zcE[i]); dgShaveStn++; }
                    bl.Shaved.Add(shaved[i]);
                }
                return keep;
            }
            (double X, double Y, double Z) PinchTip(int i, int j)
            {
                double lo = 0, hi = 1;
                for (int it = 0; it < 24; it++) { double mid = (lo + hi) * 0.5; if (AliveMargin(bl, i, j, mid) >= 0) lo = mid; else hi = mid; }
                var tp = Lerp(bl.Toe[i], bl.Toe[j], lo);
                var cp = Lerp(bl.Crest[i], bl.Crest[j], lo);
                double zc = cp.Z;
                if (currentSurface != null && currentSurface.TrySampleZ(cp.X, cp.Y, out double zt2) && zc > zt2) zc = zt2;
                double px = (tp.X + cp.X) * 0.5, py = (tp.Y + cp.Y) * 0.5, pz = (tp.Z + zc) * 0.5;
                if (currentSurface != null && currentSurface.TrySampleZ(px, py, out double zt3) && pz > zt3) pz = zt3;
                return (px, py, pz);
            }
            var res = new List<BenchLine>();
            foreach (var (a, b2) in segs)
            {
                if (b2 - a + 1 < 2) continue;
                var nb = new BenchLine { IsCoal = bl.IsCoal, Kind = bl.Kind, SeamIndex = bl.SeamIndex, RockLevel = bl.RockLevel, Level = bl.Level, LineIndex = bl.LineIndex, GapSeam = bl.GapSeam, SeamName = bl.SeamName };
                for (int i = a; i <= b2; i++)
                {
                    nb.Toe.Add(bl.Toe[i]);
                    nb.Crest.Add(shaved[i] ? (bl.Crest[i].X, bl.Crest[i].Y, zcE[i]) : bl.Crest[i]);
                    nb.Vi.Add(i < bl.Vi.Count ? bl.Vi[i] : -1);
                    nb.Shaved.Add(shaved[i]);
                    if (shaved[i]) dgShaveStn++;
                }
                if (a > 0) { var tip = PinchTip(a, a - 1); nb.Toe.Insert(0, tip); nb.Crest.Insert(0, tip); nb.Vi.Insert(0, -1); nb.Shaved.Insert(0, true); nb.PinchClosedStart = true; dgTerrPinch++; }
                else nb.PinchClosedStart = bl.PinchClosedStart;
                if (b2 < n - 1) { var tip = PinchTip(b2, b2 + 1); nb.Toe.Add(tip); nb.Crest.Add(tip); nb.Vi.Add(-1); nb.Shaved.Add(true); nb.PinchClosedEnd = true; dgTerrPinch++; }
                else nb.PinchClosedEnd = bl.PinchClosedEnd;
                foreach (int v in nb.Vi) if (v >= 0) { if (nb.FirstVi < 0) nb.FirstVi = v; nb.LastVi = v; }
                res.Add(nb);
            }
            if (res.Count == 0) { dgTerrDrop++; return res; }
            dgTerrCut += res.Count - 1;
            return res;
        }

        bool noPinch = DisableRimPinch || !p.PinchByTerrain;
        var bodyLines = new List<BenchLine>();
        foreach (var bl in map.Values)
        {
            if (bl.Toe.Count < 2 || bl.Crest.Count < 2) continue;
            if (noPinch) bodyLines.Add(bl); else bodyLines.AddRange(CutByTerrain(bl));
        }
        foreach (var bl in bodyLines)
        {
            if (bl.Toe.Count < 2 || bl.Crest.Count < 2) continue;
            r.Benches.Add(bl);
            if (bl.Kind == 1) r.RockCount++;
            else if (bl.Kind == 2) r.ExposureCount++;
            else r.CoalCount++;
        }
        foreach (var lk2 in bridgeLines)
            foreach (var cut in (noPinch ? new List<BenchLine> { lk2 } : CutByTerrain(lk2)))
                if (cut.Toe.Count >= 2) { r.Benches.Add(cut); r.BridgeCount++; }
        foreach (var cj in cornerJoinLines)
            foreach (var cut in (noPinch ? new List<BenchLine> { cj } : CutByTerrain(cj)))
                if (cut.Toe.Count >= 2) { r.Benches.Add(cut); r.BridgeCount++; }
        int rkStd = 0, rkHalf = 0;
        foreach (var bl in r.Benches) if (bl.Kind == 1) { if (bl.RockLevel >= 0) rkStd++; else rkHalf++; }

        var oc = SeamOutcropArea.Compute(currentSurface, seams);
        if (oc.Success)
        {
            for (int m = 0; m < seams.Count; m++)
            {
                if (oc.TriCount[m] == 0) continue;
                r.OutcropAreaBySeam[seams[m].Name] = oc.Area3D[m];
                r.OutcropAreaXYBySeam[seams[m].Name] = oc.AreaXY[m];
            }
            r.OutcropRings.AddRange(oc.Rings);
        }
        string expNote = r.ExposureAreaBySeam.Count == 0
            ? "露煤面积：无（各层顶板上没解出阶段1→阶段2 的带）"
            : "露煤面积(口径A·阶段1↔阶段2 之间·虚拟不出线)：" + string.Join("、", r.ExposureAreaBySeam.Select(kv => $"{kv.Key}={kv.Value / 1e4:0.##}万m²"))
              + $"，合计 {r.ExposureAreaBySeam.Values.Sum() / 1e4:0.##}万m²（{r.ExposureBands.Count} 条带）";
        expNote += !oc.Success
            ? "；现状已露煤：算不了（" + oc.Error + "）"
            : "；现状已露煤(现状面∩煤层)：" + string.Join("、", r.OutcropAreaBySeam.Select(kv => $"{kv.Key}={kv.Value / 1e4:0.##}万m²"))
              + $"，合计 {r.OutcropAreaBySeam.Values.Sum() / 1e4:0.##}万m²（投影 {r.OutcropAreaXYBySeam.Values.Sum() / 1e4:0.##}，边界环 {r.OutcropRings.Count} 条）"
              + $"；R10 桥接(仅岩台阶) {dgBridgeN} 次，最大跨 {dgBridgeMaxGap} 站/{dgBridgeMaxLen:0.#}m（煤台阶不桥接：断层/尖灭分开就分开）"
              + $"；煤台阶段数 角部裁剪前 {dgCoalBefore} → 后 {dgCoalAfter}；L角补口 {dgArc} 条(R19·折线不用圆弧)"
              + $"；段端收口(R28)：真尖灭收成楔尖 {dgPinchClose} 个、其余断因直接切断 {dgCutEnd} 个"
              + $"；R30 拦下「跨过煤体/坑沿之上」的桥接 {dgBlockBridge} 次"
              + $"；R34 丢弃穿煤的搭接线 {dgLapCutN} 条"
              + (p.HandoffToCoal ? $"；R35 岩台阶交接给煤台阶 {dgHandover} 个端头"
                 : $"；R60 因煤而断的端平直收尖 {dgFlatPinch} 个（现场令「不用指到煤台阶上」；开关 BenchTemplateParams.HandoffToCoal 回到 R35 吸附）")
              + $"；R43 接成连续水平线补点 {dgFill} 个"
              + (noPinch
                 ? "；R47 现状面构建尖灭=关（现场令「模板生成的台阶先不与现状面求尖灭」；开关 BenchTemplateParams.PinchByTerrain）—— 台阶按设计出全，**超出现状面的那截照留**，判据 C9 相应只量不判"
                 : $"；R47 现状面构建尖灭：削顶 {dgShaveStn} 站(坡顶跟地表下来)、被地表裁开 {dgTerrCut} 处、面高削到 0 尖灭 {dgTerrPinch} 个端头、整条在地表之上不出线 {dgTerrDrop} 条")
              + (!p.WidenBerm ? "；R48 工作平盘放宽=关（现场令「把平盘宽度改回来」；开关 BenchTemplateParams.WidenBerm）"
                 : $"；R48 工作平盘放宽到 {bermTarget:0.#}m：外推 {dgBermPush} 条线、最大外推 {dgBermMaxPush:0.#}m、迭代 {dgBermIter} 轮收敛（整条线刚性平移 ⇒ 线形分毫不动、不起锯齿；平盘是「不小于」约束所以实得 ≥ 目标；煤台阶同样参与，露煤面积由前界①/②定、不受影响）");
        r.Diag = expNote
               + $"；煤台阶分层(R26)：{dgCoalStat} 个逐站煤台阶共 {dgCoalLvl} 级，平均 {(dgCoalStat > 0 ? (double)dgCoalLvl / dgCoalStat : 0):0.##} 级/站、最多 {dgCoalLvlMax} 级"
               + "（煤台阶高 " + string.Join("/", seams.Select((s, k) => coalH[k] < 0 ? $"{s.Name}=采全高" : coalH[k] > 0.5 ? $"{s.Name}={coalH[k]:0.#}m" : $"{s.Name}={rockH:0.#}m(跟岩台阶)")) + "）"
               + $"；剔离群站 {dgOutlier} 处(形态贴工作线·R12)；"
               + $"岩台阶：标准格 {rkStd} 段/{dgStd} 点、半台阶 {rkHalf} 段"
               + $"（R59 薄到坡顶坡底分不开、不出线的半台阶 {dgHalfSliver} 站；闸写在平面分离距上＝{MinToeCrestSepM:0.##}m ⇒ 面高 {MinToeCrestSepM * rockTanB:0.##}m，满高台阶分离 {rockH / rockTanB:0.##}m）"
               + (DisableFloorPinchLevel ? "" : $"；R61 层间只出水平标高格、到上层煤底板处尖灭（现场令「底板这个岩台阶也是水平的」）：取消贴界面半台阶 {dgFloorPinch} 站，封顶那一摞不受影响")
               + $"、搭接线 {r.BridgeCount} 条(单列·不混进台阶线)；"
               + bermNote + "；"
               + $"尖灭归因：真尖灭(煤厚<可采阈值) {dgPinch} 处 · 该层底板无露头(面没盖到/前界不含) {dgNoFloor} 处 · "
               + $"最下层底板无露头(仅煤台阶起不来·R15 后岩台阶照铺) {dgSkipStat} 处 · 在坑沿之上(地表已剥蚀·R22 整层不出) {dgOverRim} 处"
               + $"（另有 {dgExpOverRim} 处煤台阶照出、只是顶板越坑沿⇒那条露煤带不出；{dgExpNoGain} 处本层 d_seam 不比下方那层更远⇒帮已推过去、本期无新增露煤·R27）；"
               + (dgGapN > 0
                  ? $"层间可堆岩厚 min={dgGapMin:0.#} / 平均={dgGapSum / dgGapN:0.#} / max={dgGapMax:0.#} m（<{rockH:0.#}m 则该层间只出得了一个半台阶）"
                  : "层间厚无样本（只有一层煤，或上覆各层底板全无露头）");
        r.Success = r.Benches.Count > 0;
        if (!r.Success) r.Error = "未生成任何台阶线（煤层/现状面采样全落空？前界斜面未穿煤层？）";
        return r;
    }

    /// <summary>角平分线分界裁剪：两帮各留自己一侧 → 角部不再交叉。</summary>
    private static void ClipCornerToBisector(IEnumerable<BenchLine> benches, WorkLineCornerJoiner.CornerLink lk, IReadOnlyList<WorkLineSamples> wls)
    {
        if (lk.A < 0 || lk.A >= wls.Count || lk.B < 0 || lk.B >= wls.Count) return;
        var wlA = wls[lk.A]; var wlB = wls[lk.B];
        if (wlA?.Baseline == null || wlA.Baseline.Count < 2 || wlB?.Baseline == null || wlB.Baseline.Count < 2) return;
        var pA  = lk.AStart ? wlA.Baseline[0] : wlA.Baseline[wlA.Baseline.Count - 1];
        var pA1 = lk.AStart ? wlA.Baseline[1] : wlA.Baseline[wlA.Baseline.Count - 2];
        var pB  = lk.BStart ? wlB.Baseline[0] : wlB.Baseline[wlB.Baseline.Count - 1];
        var pB1 = lk.BStart ? wlB.Baseline[1] : wlB.Baseline[wlB.Baseline.Count - 2];
        double px = (pA.X + pB.X) * 0.5, py = (pA.Y + pB.Y) * 0.5;
        double ax = pA1.X - pA.X, ay = pA1.Y - pA.Y; double al = Math.Sqrt(ax * ax + ay * ay); if (al < 1e-6) return; ax /= al; ay /= al;
        double bx = pB1.X - pB.X, by = pB1.Y - pB.Y; double bl2 = Math.Sqrt(bx * bx + by * by); if (bl2 < 1e-6) return; bx /= bl2; by /= bl2;
        foreach (var bench in benches)
        {
            bool isA = bench.LineIndex == lk.A, isB = bench.LineIndex == lk.B;
            if (!isA && !isB) continue;
            double kdx = isA ? ax : bx, kdy = isA ? ay : by;
            double odx = isA ? bx : ax, ody = isA ? by : ay;
            ClipBenchToSide(bench, px, py, kdx, kdy, odx, ody);
        }
    }

    private static void ClipBenchToSide(BenchLine bench, double px, double py, double kdx, double kdy, double odx, double ody)
    {
        int n = Math.Min(bench.Toe.Count, bench.Crest.Count);
        if (n < 1) return;
        double bx = odx - kdx, by = ody - kdy;
        double F(double qx, double qy) => (qx - px) * bx + (qy - py) * by;
        var toe = new List<(double X, double Y, double Z)>(n + 1);
        var crest = new List<(double X, double Y, double Z)>(n + 1);
        var vis = new List<int>(n + 1);
        double fPrev = 0; bool havePrev = false;
        for (int i = 0; i < n; i++)
        {
            var t = bench.Toe[i]; var c = bench.Crest[i];
            double f = F(t.X, t.Y);
            bool keep = f <= 1e-9;
            if (havePrev && keep != (fPrev <= 1e-9) && Math.Abs(fPrev - f) > 1e-12)
            {
                double tt = fPrev / (fPrev - f);
                var t0 = bench.Toe[i - 1]; var c0 = bench.Crest[i - 1];
                toe.Add((t0.X + tt * (t.X - t0.X), t0.Y + tt * (t.Y - t0.Y), t0.Z + tt * (t.Z - t0.Z)));
                vis.Add(-1);
                crest.Add((c0.X + tt * (c.X - c0.X), c0.Y + tt * (c.Y - c0.Y), c0.Z + tt * (c.Z - c0.Z)));
            }
            if (keep) { toe.Add(t); crest.Add(c); vis.Add(i < bench.Vi.Count ? bench.Vi[i] : -1); }
            fPrev = f; havePrev = true;
        }
        bench.Vi.Clear(); bench.Vi.AddRange(vis);
        bench.Toe.Clear(); bench.Toe.AddRange(toe);
        bench.Crest.Clear(); bench.Crest.AddRange(crest);
    }

    /// <summary>一处角部：A、B 两条工作线的同类·同层·同级台阶线在角部端衔接；标准格岩台阶单列成衔接线（R36）。</summary>
    private static void StitchCornerBenches(IEnumerable<BenchLine> benches, WorkLineCornerJoiner.CornerLink lk, int[] lineLastVi,
        HashSet<(BenchLine bl, bool atStart)>? stitched = null, List<BenchLine>? cornerJoins = null)
    {
        const double MAX_GAP2 = 150.0 * 150.0;
        var listA = new List<BenchLine>(); var listB = new List<BenchLine>();
        foreach (var b in benches) { if (b.LineIndex == lk.A) listA.Add(b); else if (b.LineIndex == lk.B) listB.Add(b); }
        var bestPer = new Dictionary<(int, int, int), (BenchLine A, BenchLine B, double D2)>();
        foreach (var a in listA)
        {
            if (a.Toe.Count < 2) continue;
            var ae = lk.AStart ? a.Toe[0] : a.Toe[a.Toe.Count - 1];
            foreach (var c in listB)
            {
                if (c.Toe.Count < 2) continue;
                if (c.Kind != a.Kind || c.SeamIndex != a.SeamIndex || c.Level != a.Level) continue;
                var be = lk.BStart ? c.Toe[0] : c.Toe[c.Toe.Count - 1];
                double d2 = (ae.X - be.X) * (ae.X - be.X) + (ae.Y - be.Y) * (ae.Y - be.Y);
                var key = (a.Kind, a.SeamIndex, a.Level);
                if (!bestPer.TryGetValue(key, out var cur) || d2 < cur.D2) bestPer[key] = (a, c, d2);
            }
        }
        foreach (var kv in bestPer.Values)
        {
            if (kv.D2 > MAX_GAP2) continue;
            if (kv.A.Kind == 1 && kv.A.RockLevel >= 0)
            {
                var ip = CornerPoint(kv.A.Toe, lk.AStart, kv.B.Toe, lk.BStart);
                if (ip.HasValue)
                {
                    var ea = lk.AStart ? kv.A.Toe[0] : kv.A.Toe[^1];
                    var eb = lk.BStart ? kv.B.Toe[0] : kv.B.Toe[^1];
                    var ca = lk.AStart ? kv.A.Crest[0] : kv.A.Crest[^1];
                    var cb2 = lk.BStart ? kv.B.Crest[0] : kv.B.Crest[^1];
                    var jn = new BenchLine
                    {
                        IsCoal = false, Kind = 3, SeamIndex = -1, RockLevel = kv.A.RockLevel, Level = kv.A.Level, LineIndex = kv.A.LineIndex, GapSeam = kv.A.GapSeam,
                        SeamName = $"角部衔接·格{kv.A.RockLevel}", FirstVi = kv.A.FirstVi, LastVi = kv.A.LastVi,
                    };
                    jn.Toe.Add(ea); jn.Toe.Add(ip.Value); jn.Toe.Add(eb);
                    jn.Crest.Add(ca); jn.Crest.Add((ip.Value.X, ip.Value.Y, (ca.Z + cb2.Z) * 0.5)); jn.Crest.Add(cb2);
                    cornerJoins?.Add(jn);
                    stitched?.Add((kv.A, lk.AStart)); stitched?.Add((kv.B, lk.BStart));
                }
                continue;
            }
            if (JoinPolyEnds(kv.A.Toe, lk.AStart, kv.B.Toe, lk.BStart))
            {
                JoinPolyEnds(kv.A.Crest, lk.AStart, kv.B.Crest, lk.BStart);
                kv.A.Vi.Clear(); for (int q = 0; q < kv.A.Toe.Count; q++) kv.A.Vi.Add(-1);
                kv.B.Vi.Clear(); for (int q = 0; q < kv.B.Toe.Count; q++) kv.B.Vi.Add(-1);
                stitched?.Add((kv.A, lk.AStart)); stitched?.Add((kv.B, lk.BStart));
            }
        }
    }

    /// <summary>两段平面线段是否相交（含端点）。</summary>
    private static bool SegHit((double X, double Y, double Z) p, (double X, double Y, double Z) q, (double X, double Y, double Z) r2, (double X, double Y, double Z) s2)
    {
        double ax = q.X - p.X, ay = q.Y - p.Y, bx = s2.X - r2.X, by = s2.Y - r2.Y;
        double den = ax * by - ay * bx;
        if (Math.Abs(den) < 1e-12) return false;
        double t = ((r2.X - p.X) * by - (r2.Y - p.Y) * bx) / den;
        double u = ((r2.X - p.X) * ay - (r2.Y - p.Y) * ax) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    private static (double X, double Y, double Z)? CornerPoint(List<(double X, double Y, double Z)> a, bool aStart, List<(double X, double Y, double Z)> b, bool bStart)
    {
        if (a.Count < 3 || b.Count < 3) return null;
        var pa = aStart ? a[0] : a[a.Count - 1];
        var pb = bStart ? b[0] : b[b.Count - 1];
        var pa1 = aStart ? a[2] : a[a.Count - 3];
        var pb1 = bStart ? b[2] : b[b.Count - 3];
        double ux = pa.X - pa1.X, uy = pa.Y - pa1.Y, ul = Math.Sqrt(ux * ux + uy * uy);
        double vx = pb.X - pb1.X, vy = pb.Y - pb1.Y, vl = Math.Sqrt(vx * vx + vy * vy);
        if (ul < 1e-6 || vl < 1e-6) return null;
        ux /= ul; uy /= ul; vx /= vl; vy /= vl;
        double det = uy * vx - ux * vy;
        if (Math.Abs(det) < 0.1) return null;
        double wx = pb.X - pa.X, wy = pb.Y - pa.Y;
        double sN = (vx * wy - vy * wx) / det, tN = (ux * wy - uy * wx) / det;
        const double LIM = 80.0;
        if (sN <= 0 || tN <= 0 || sN > LIM || tN > LIM) return null;
        return (pa.X + sN * ux, pa.Y + sN * uy, (pa.Z + pb.Z) * 0.5);
    }

    private static bool JoinPolyEnds(List<(double X, double Y, double Z)> a, bool aStart, List<(double X, double Y, double Z)> b, bool bStart)
    {
        if (a.Count < 3 || b.Count < 3) return false;
        var pa = aStart ? a[0] : a[a.Count - 1];
        var pb = bStart ? b[0] : b[b.Count - 1];
        var pa1 = aStart ? a[2] : a[a.Count - 3];
        var pb1 = bStart ? b[2] : b[b.Count - 3];
        double ux = pa.X - pa1.X, uy = pa.Y - pa1.Y, ul = Math.Sqrt(ux * ux + uy * uy);
        double vx = pb.X - pb1.X, vy = pb.Y - pb1.Y, vl = Math.Sqrt(vx * vx + vy * vy);
        if (ul < 1e-6 || vl < 1e-6) return false;
        ux /= ul; uy /= ul; vx /= vl; vy /= vl;
        double det = uy * vx - ux * vy;
        if (Math.Abs(det) < 0.1) return false;
        double wx = pb.X - pa.X, wy = pb.Y - pa.Y;
        double s = (vx * wy - vy * wx) / det, t = (ux * wy - uy * wx) / det;
        const double LIM = 80.0;
        if (s <= 0 || t <= 0 || s > LIM || t > LIM) return false;
        double ix = pa.X + s * ux, iy = pa.Y + s * uy, iz = (pa.Z + pb.Z) * 0.5;
        SetOrAppendEnd(a, aStart, ix, iy, iz);
        SetOrAppendEnd(b, bStart, ix, iy, iz);
        return true;
    }

    private static void SetOrAppendEnd(List<(double X, double Y, double Z)> line, bool start, double ix, double iy, double iz)
    {
        while (line.Count >= 2)
        {
            var pend = start ? line[0] : line[line.Count - 1];
            var pnext = start ? line[1] : line[line.Count - 2];
            double dx = pend.X - pnext.X, dy = pend.Y - pnext.Y;
            if ((pend.X - ix) * dx + (pend.Y - iy) * dy > 1e-6) { if (start) line.RemoveAt(0); else line.RemoveAt(line.Count - 1); }
            else break;
        }
        if (start) line.Insert(0, (ix, iy, iz)); else line.Add((ix, iy, iz));
    }

    /// <summary>沿基线按 step(m) 加密站点（每子段继承父段推进方向）。公开给验收台架：台阶是在加密后的工作线上采样的。</summary>
    public static WorkLineSamples DensifyBaseline(WorkLineSamples wl, double step)
    {
        int n = wl.Baseline.Count;
        int nSeg = Math.Min(wl.Samples.Count, n - 1 + (wl.Closed ? 1 : 0));
        if (nSeg <= 0) return wl;
        var g = new WorkLineSamples { Success = true, Closed = wl.Closed, AdvanceMode = wl.AdvanceMode, DirMode = wl.DirMode };
        for (int s = 0; s < nSeg; s++)
        {
            var A = wl.Baseline[s]; var B = wl.Baseline[(s + 1) % n];
            var smp = wl.Samples[s];
            double dx = B.X - A.X, dy = B.Y - A.Y, dz = B.Z - A.Z;
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            int div = Math.Max(1, segLen > 1e-6 ? (int)Math.Floor(segLen / step) + 1 : 1);
            for (int j = 0; j < div; j++)
            {
                double t0 = j / (double)div;
                g.Baseline.Add((A.X + dx * t0, A.Y + dy * t0, A.Z + dz * t0));
                g.Samples.Add((A.X + dx * (t0 + 0.5 / div), A.Y + dy * (t0 + 0.5 / div), A.Z, smp.Dx, smp.Dy));
            }
        }
        if (!wl.Closed) g.Baseline.Add(wl.Baseline[n - 1]);
        return g;
    }

    private static double RepFloor(SeamSurfaces s, double x, double y) => (s.Floor != null && s.Floor.TrySampleZ(x, y, out double z)) ? z : 0;
}
