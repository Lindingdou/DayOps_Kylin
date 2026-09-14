using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad;

/// <summary>一条老台阶线的判决。</summary>
public enum BenchReplaceVerdict
{
    /// <summary>整条在覆盖范围外 —— 不动（端帮 / 坑内其它水平）。</summary>
    Untouched = 0,
    /// <summary>整条在覆盖范围内 —— 这一阶段被新台阶线取代（删）。</summary>
    Superseded = 1,
    /// <summary>跨界 —— 删原线，把范围外那几段原样写回（端帮保留段）。</summary>
    Clipped = 2,
    /// <summary>在覆盖范围内，但**标高归不进任何一级新台阶** —— 一条都不删，单列出来给人看。</summary>
    Unmatched = 3,
}

/// <summary>
/// **新台阶的标高格**：把老台阶线按标高归到"哪一级新台阶"上。匹配一律对**实际建出来的那些标高**，格距只用来定容差与回显。
/// 交接单优先（<see cref="FromHandoff"/>，带煤层/级号身份）；没有交接单时从图上新台阶线反推（<see cref="FromTemplateLines"/>）。
/// </summary>
public sealed class BenchLevelGrid
{
    public double AnchorZ, Step;
    /// <summary>归格容差(m)：默认半个标高格。</summary>
    public double Tol = 7.5;
    public readonly List<double> LevelZ = new();
    public readonly List<string> LevelTag = new();
    public readonly List<string> LevelSeam = new();
    public int Count => LevelZ.Count;

    public bool TryMatch(double z, out int idx, out double dz)
    {
        idx = -1; dz = double.MaxValue;
        for (int i = 0; i < LevelZ.Count; i++)
        {
            double d = Math.Abs(LevelZ[i] - z);
            if (d < Math.Abs(dz)) { dz = LevelZ[i] - z; idx = i; }
        }
        if (idx < 0 || Math.Abs(dz) > Tol) { idx = -1; return false; }
        return true;
    }

    /// <summary>从「驱动量」交接单装（有身份，台账能说出被哪一级取代）。</summary>
    public static BenchLevelGrid FromHandoff(EpHandoff h, double? tol = null)
    {
        var g = new BenchLevelGrid { AnchorZ = h.BenchGridAnchorZ, Step = h.RockBenchH, Tol = tol is > 0 ? tol.Value : h.LevelTol };
        var src = new List<(double Z, string Tag, string Seam)>(h.Levels.Count);
        foreach (var lv in h.Levels) src.Add((lv.Z, lv.Tag, lv.Kind == 0 || lv.Kind == 2 ? (lv.SeamName ?? "") : ""));
        Cluster(g, src);
        return g;
    }

    /// <summary>没有交接单时的兜底：直接拿图上新台阶线的标高聚。身份拿不到，标签退化成图层名。</summary>
    public static BenchLevelGrid FromTemplateLines(IReadOnlyList<EpPolyline> tpl, double tol)
    {
        var g = new BenchLevelGrid { AnchorZ = 0, Step = 0, Tol = tol > 0 ? tol : 7.5 };
        var src = new List<(double Z, string Tag, string Seam)>();
        foreach (var pl in tpl)
        {
            if (pl.Xyz == null || pl.Xyz.Length < 6) continue;
            var zs = new List<double>(pl.Xyz.Length / 3);
            for (int i = 2; i < pl.Xyz.Length; i += 3) zs.Add(pl.Xyz[i]);
            zs.Sort();
            string lay = pl.Layer ?? "";
            string seam = lay.StartsWith("台阶_", StringComparison.Ordinal) ? lay.Substring(3) : "";
            if (seam is "岩台阶" or "搭接线") seam = "";
            src.Add((zs[zs.Count / 2], lay, seam));
        }
        Cluster(g, src);
        if (g.LevelZ.Count >= 2)
        {
            var gaps = new List<double>();
            for (int i = 1; i < g.LevelZ.Count; i++) gaps.Add(g.LevelZ[i] - g.LevelZ[i - 1]);
            gaps.Sort();
            g.Step = gaps[gaps.Count / 2];
            if (tol <= 0) g.Tol = Math.Max(0.25, g.Step * 0.5);
        }
        return g;
    }

    /// <summary>链式聚（不是固定格）：相邻差 ≤ 容差/4 就并到同一级，标签合并计数、煤层取众数。</summary>
    private static void Cluster(BenchLevelGrid g, List<(double Z, string Tag, string Seam)> src)
    {
        if (src.Count == 0) return;
        src.Sort((a, b) => a.Z.CompareTo(b.Z));
        double clusterTol = Math.Max(1e-6, g.Tol * 0.25);
        int i = 0;
        while (i < src.Count)
        {
            int j = i; double sum = 0;
            var tags = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            var seams = new Dictionary<string, int>(StringComparer.Ordinal);
            while (j < src.Count && src[j].Z - src[i].Z <= clusterTol)
            {
                sum += src[j].Z;
                string t = src[j].Tag ?? "";
                if (!tags.ContainsKey(t)) { tags[t] = 0; order.Add(t); }
                tags[t]++;
                string s = src[j].Seam ?? "";
                if (s.Length > 0) { seams.TryGetValue(s, out int c); seams[s] = c + 1; }
                j++;
            }
            g.LevelZ.Add(sum / (j - i));
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < order.Count && k < 3; k++)
            {
                if (sb.Length > 0) sb.Append('·');
                sb.Append(order[k]);
                if (tags[order[k]] > 1) sb.Append('×').Append(tags[order[k]]);
            }
            if (order.Count > 3) sb.Append('…');
            g.LevelTag.Add(sb.ToString());
            string best = ""; int bestN = 0;
            foreach (var kv in seams) if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
            g.LevelSeam.Add(best);
            i = j;
        }
    }
}

/// <summary>一处裁剪断口 + 它的**退路**（trail[0] = 断口本身，逐点向外；总长 = 这一侧的可用余地）。</summary>
public sealed class BenchCut
{
    public readonly double X, Y, Z;
    public readonly List<(double X, double Y, double Z)> Trail;

    public BenchCut(double x, double y, double z, List<(double X, double Y, double Z)>? trail = null)
    { X = x; Y = y; Z = z; Trail = trail ?? new List<(double, double, double)> { (x, y, z) }; }

    public double TrailLength
    {
        get
        {
            double s = 0;
            for (int i = 0; i + 1 < Trail.Count; i++)
            {
                double dx = Trail[i + 1].X - Trail[i].X, dy = Trail[i + 1].Y - Trail[i].Y, dz = Trail[i + 1].Z - Trail[i].Z;
                s += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return s;
        }
    }
}

/// <summary>一条老台阶线的账目：判决 + 保留段 + 丢弃段 + 裁剪断口。保留长 + 丢弃长 = 原长。</summary>
public sealed class BenchReplaceItem
{
    public ulong  Handle;
    public string Layer = "";
    public bool   Closed;
    public BenchReplaceVerdict Verdict;
    public double Length;
    public double KeptLength;
    public double DropLength => Length - KeptLength;
    public readonly List<List<(double X, double Y, double Z)>> Keep = new();
    public readonly List<List<(double X, double Y, double Z)>> Drop = new();
    public readonly List<BenchCut> Cuts = new();

    /// <summary>代表标高 = 各点 Z 的中位数。</summary>
    public double Z;
    public double ZSpread;
    public int    MatchedLevel = -1;
    public double MatchedZ, MatchedDz;
    public string MatchedTag = "";
    public bool   Overridden;
    public bool Removes => Verdict == BenchReplaceVerdict.Superseded || Verdict == BenchReplaceVerdict.Clipped;
}

public sealed class BenchReplaceLayerTally
{
    public string Layer = "";
    public int    Superseded, Clipped, Unmatched;
    public double DropLength;
}

/// <summary>**一级新台阶在这一阶段的去处**：被它取代了几条老线 / 一条都没有（"碰不到"）。</summary>
public sealed class BenchLevelUsage
{
    public int    Index;
    public double Z;
    public string Tag = "";
    public string Seam = "";
    public int    OldCount;
    public double OldDropLength;
    public bool   Touched => OldCount > 0;
}

/// <summary>一层煤在这一阶段的处境（台账按煤层分组报）。</summary>
public sealed class BenchSeamTally
{
    public string Seam = "";
    public bool   InRoster;
    public bool   Ready = true;
    public int    BenchCount;
    public int    TouchedCount;
    public int    UntouchedCount => Math.Max(0, BenchCount - TouchedCount);
    public double ReplacedLength;
}

/// <summary>一条工作线上的覆盖区间（回显 / 判据）。推进 × 走向两个一维区间的交 = 模板影响区域。</summary>
public readonly struct BenchCoverBand
{
    public readonly int    Line;
    public readonly double Lo, Hi;
    public readonly double TemplateLo, TemplateHi;
    public readonly int    TemplatePts;
    public readonly double LatLo, LatHi;
    public readonly double TemplateLatLo, TemplateLatHi;
    public bool LatClipped => !double.IsInfinity(LatLo) || !double.IsInfinity(LatHi);

    public BenchCoverBand(int line, double lo, double hi, double tLo, double tHi, int pts, double latLo, double latHi, double tLatLo, double tLatHi)
    {
        Line = line; Lo = lo; Hi = hi; TemplateLo = tLo; TemplateHi = tHi; TemplatePts = pts;
        LatLo = latLo; LatHi = latHi; TemplateLatLo = tLatLo; TemplateLatHi = tLatHi;
    }
}

/// <summary>替换方案（只算不落地）。</summary>
public sealed class BenchReplacePlan
{
    public bool   Success;
    public string Error = "";
    public string Diag  = "";

    public readonly List<BenchReplaceItem>  Items = new();
    public readonly List<BenchCoverBand>    Bands = new();
    /// <summary>逐工作线覆盖范围环（扁平 XY，隐式闭合）—— 示意用，判定以逐点投影为准。</summary>
    public readonly List<double[]> BandRings = new();

    public int    WorkLineCount;
    public bool   LateralToTemplate;
    public double LateralMargin;
    public int    NewLineCount;      public double NewLength;
    public int    SupersededCount,   ClippedCount, UntouchedCount, UnmatchedCount;
    public double ScannedLength,     KeptLength,   DroppedLength;

    public BenchLevelGrid? Levels;
    public bool LevelsFromHandoff;
    public readonly List<BenchLevelUsage> LevelUsage = new();
    public readonly List<BenchSeamTally> SeamTally = new();
    public readonly Dictionary<ulong, bool> Overrides = new();

    public int UntouchedLevelCount
    { get { int n = 0; foreach (var u in LevelUsage) if (!u.Touched) n++; return n; } }

    /// <summary>要删的实体：整条被取代 + 跨界被裁的。归不进级的一条都不删。</summary>
    public ulong[] HandlesToDelete()
    {
        var l = new List<ulong>();
        foreach (var it in Items) if (it.Removes && it.Handle != 0) l.Add(it.Handle);
        return l.ToArray();
    }

    public IEnumerable<(string Layer, List<(double X, double Y, double Z)> Pts)> KeepSegments()
    {
        foreach (var it in Items)
        {
            if (it.Verdict != BenchReplaceVerdict.Clipped) continue;
            foreach (var seg in it.Keep) if (seg.Count >= 2) yield return (it.Layer, seg);
        }
    }

    public List<BenchReplaceLayerTally> ByLayer()
    {
        var order = new List<string>();
        var map = new Dictionary<string, BenchReplaceLayerTally>(StringComparer.Ordinal);
        foreach (var it in Items)
        {
            if (it.Verdict == BenchReplaceVerdict.Untouched) continue;
            if (!map.TryGetValue(it.Layer, out var v)) { v = new BenchReplaceLayerTally { Layer = it.Layer }; map[it.Layer] = v; order.Add(it.Layer); }
            switch (it.Verdict)
            {
                case BenchReplaceVerdict.Superseded: v.Superseded++; break;
                case BenchReplaceVerdict.Clipped:    v.Clipped++;    break;
                default:                             v.Unmatched++;  break;
            }
            v.DropLength += it.DropLength;
        }
        var outp = new List<BenchReplaceLayerTally>(order.Count);
        foreach (var lay in order) outp.Add(map[lay]);
        outp.Sort((a, b) => b.DropLength.CompareTo(a.DropLength));
        return outp;
    }

    public IEnumerable<BenchReplaceItem> UnmatchedItems()
    {
        foreach (var it in Items) if (it.Verdict == BenchReplaceVerdict.Unmatched) yield return it;
    }
}

/// <summary>
/// 「创建工程位置」· **用新台阶线替换覆盖范围内的老台阶线**（忠实移植原 <c>BenchLineReplacer</c>）。
///
/// 口径（2026-08-06 现场定）：
///  ① 覆盖范围 = 模板影响区域 = 推进 × 走向两个一维区间的交（工作线坐标系，见 <see cref="WorkLineProjector"/>）；
///  ①ʹ 横向限在模板影响区域内（默认 true）—— 工作线通常一画就是整个采场宽，模板只占其中一段；
///  ② 下界回溯到工作线（默认 true）：工作线到前界之间那一段正是这一阶段要挖掉的；
///  ③ 逐条判决：整条在内 = 取代（删）；跨界 = 删原线、范围外那几段写回；整条在外 = 不动。跨界处二分收敛切在覆盖边界上；
///  ④ 记账：每条扫过的线都落一条 <see cref="BenchReplaceItem"/>，保留长 + 丢弃长 = 原长；
///  ⑤ 平面范围内再按标高格逐级替换：归不进 ⇒ <see cref="BenchReplaceVerdict.Unmatched"/>，一条都不删，单列。
/// 纯几何、只读、不碰图。
/// </summary>
public static class BenchLineReplacer
{
    private const int BisectIters = 22;

    public static BenchReplacePlan Plan(
        IReadOnlyList<WorkLineSamples>? workLines,
        IReadOnlyList<EpPolyline>? templateLines,
        IReadOnlyList<EpPolyline>? oldLines,
        double margin = 5.0,
        double latTol = 5.0,
        bool backToWorkLine = true,
        BenchLevelGrid? levels = null,
        bool levelsFromHandoff = false,
        IReadOnlyDictionary<ulong, bool>? overrides = null,
        IReadOnlyList<string>? seamRoster = null,
        IReadOnlyList<bool>? seamReady = null,
        bool lateralToTemplate = true,
        double lateralMargin = -1)
    {
        var p = new BenchReplacePlan();
        try
        {
            workLines ??= Array.Empty<WorkLineSamples>();
            templateLines ??= Array.Empty<EpPolyline>();
            oldLines ??= Array.Empty<EpPolyline>();
            if (margin < 0) margin = 0;
            if (latTol <= 0) latTol = 5.0;
            double latMargin = lateralMargin >= 0 ? lateralMargin : margin;

            int L = workLines.Count;
            if (L == 0) { p.Error = "没有工作线 —— 覆盖范围由工作线的推进方向定，缺了它无从判断哪段老台阶线该被取代。"; return p; }

            var proj = WorkLineProjector.Build(workLines, latTol);
            if (!proj.HasAny) { p.Error = "工作线都退化了（基线点 < 2 或无方向样本），投影器为空。"; return p; }

            // ── ① 模板影响区域（逐工作线）
            var tLo = new double[L]; var tHi = new double[L]; var tCnt = new int[L];
            var tsLo = new double[L]; var tsHi = new double[L];
            for (int l = 0; l < L; l++) { tLo[l] = double.MaxValue; tHi[l] = double.MinValue; tsLo[l] = double.MaxValue; tsHi[l] = double.MinValue; }
            foreach (var pl in templateLines)
            {
                if (pl.Xyz == null || pl.Xyz.Length < 6) continue;
                p.NewLineCount++;
                p.NewLength += Length3D(pl.Xyz, pl.Closed);
                for (int i = 0; i + 2 < pl.Xyz.Length; i += 3)
                {
                    if (!proj.TryProject(pl.Xyz[i], pl.Xyz[i + 1], out double a0, out _, out int ln, out double s)) continue;
                    if (ln < 0 || ln >= L) continue;
                    if (a0 < tLo[ln]) tLo[ln] = a0;
                    if (a0 > tHi[ln]) tHi[ln] = a0;
                    if (s  < tsLo[ln]) tsLo[ln] = s;
                    if (s  > tsHi[ln]) tsHi[ln] = s;
                    tCnt[ln]++;
                }
            }

            var lo = new double[L]; var hi = new double[L]; var live = new bool[L];
            var sLo = new double[L]; var sHi = new double[L];
            int liveCount = 0;
            p.LateralToTemplate = lateralToTemplate;
            p.LateralMargin = latMargin;
            for (int l = 0; l < L; l++)
            {
                if (tCnt[l] == 0) continue;
                live[l] = true; liveCount++;
                double lo0 = backToWorkLine ? Math.Min(0.0, tLo[l]) : tLo[l];
                lo[l] = lo0 - margin;
                hi[l] = tHi[l] + margin;
                sLo[l] = lateralToTemplate ? tsLo[l] - latMargin : double.NegativeInfinity;
                sHi[l] = lateralToTemplate ? tsHi[l] + latMargin : double.PositiveInfinity;
                p.Bands.Add(new BenchCoverBand(l, lo[l], hi[l], tLo[l], tHi[l], tCnt[l], sLo[l], sHi[l], tsLo[l], tsHi[l]));
                var ring = BandRing(workLines[l], lo[l], hi[l], latTol, sLo[l], sHi[l]);
                if (ring != null) p.BandRings.Add(ring);
            }
            p.WorkLineCount = L;

            if (liveCount == 0)
            {
                p.Error = p.NewLineCount == 0
                    ? "没有新台阶线（模板）—— 先生成模板台阶线，再来替换。"
                    : $"{p.NewLineCount} 条新台阶线一个点都投不到 {L} 条工作线上（超出工作线纵向跨度 ±{latTol:0.#}m）。多半是工作线选错了，或模板不是这几条工作线生成的。";
                return p;
            }

            bool Inside(double x, double y)
            {
                if (!proj.TryProject(x, y, out double a0, out _, out int ln, out double s)) return false;
                if (ln < 0 || ln >= L || !live[ln]) return false;
                if (a0 < lo[ln] || a0 > hi[ln]) return false;
                return s >= sLo[ln] && s <= sHi[ln];
            }

            double minExt = double.MaxValue;
            for (int l = 0; l < L; l++)
            {
                if (!live[l]) continue;
                minExt = Math.Min(minExt, hi[l] - lo[l]);
                if (lateralToTemplate) minExt = Math.Min(minExt, sHi[l] - sLo[l]);
            }
            double probe = Math.Max(2.0, (minExt == double.MaxValue ? 100.0 : minExt) * 0.25);

            // ── ② 逐条老台阶线判决
            p.Levels = levels;
            p.LevelsFromHandoff = levelsFromHandoff && levels != null;
            foreach (var pl in oldLines)
            {
                if (pl.Xyz == null || pl.Xyz.Length < 6) continue;
                var it = new BenchReplaceItem { Handle = pl.Handle, Layer = pl.Layer ?? "", Closed = pl.Closed, Length = Length3D(pl.Xyz, pl.Closed) };
                MedianZ(pl.Xyz, out it.Z, out it.ZSpread);
                ClipOne(pl, Inside, it, probe);

                if (it.Removes && levels is { Count: > 0 })
                {
                    if (levels.TryMatch(it.Z, out int li, out double dz))
                    { it.MatchedLevel = li; it.MatchedZ = levels.LevelZ[li]; it.MatchedDz = dz; it.MatchedTag = levels.LevelTag[li]; }
                    else Demote(it);
                }

                if (overrides != null && it.Handle != 0 && overrides.TryGetValue(it.Handle, out bool force))
                {
                    if (force && !it.Removes && it.Verdict != BenchReplaceVerdict.Untouched)
                    { it.Verdict = BenchReplaceVerdict.Superseded; it.Keep.Clear(); it.Cuts.Clear(); it.Drop.Clear(); it.Drop.Add(AllPts(pl)); it.KeptLength = 0; it.Overridden = true; }
                    else if (!force && it.Removes)
                    { Demote(it); it.Verdict = BenchReplaceVerdict.Unmatched; it.Overridden = true; }
                }

                p.Items.Add(it);
                p.ScannedLength += it.Length;
                switch (it.Verdict)
                {
                    case BenchReplaceVerdict.Untouched:  p.UntouchedCount++;  break;
                    case BenchReplaceVerdict.Superseded: p.SupersededCount++; break;
                    case BenchReplaceVerdict.Unmatched:  p.UnmatchedCount++;  break;
                    default:                             p.ClippedCount++;    break;
                }
                p.KeptLength    += it.KeptLength;
                p.DroppedLength += it.DropLength;
            }
            if (overrides != null) foreach (var kv in overrides) p.Overrides[kv.Key] = kv.Value;

            RollUp(p, levels, seamRoster, seamReady);
            p.Diag = Describe(p, margin, latTol, backToWorkLine, latMargin);
            p.Success = true;
            return p;
        }
        catch (Exception ex)
        {
            p.Success = false;
            p.Error = $"{ex.GetType().Name}: {ex.Message}";
            return p;
        }
    }

    private static void RollUp(BenchReplacePlan p, BenchLevelGrid? levels, IReadOnlyList<string>? roster, IReadOnlyList<bool>? ready)
    {
        if (levels is { Count: > 0 })
        {
            for (int i = 0; i < levels.Count; i++)
                p.LevelUsage.Add(new BenchLevelUsage
                {
                    Index = i, Z = levels.LevelZ[i],
                    Tag = i < levels.LevelTag.Count ? levels.LevelTag[i] : "",
                    Seam = i < levels.LevelSeam.Count ? levels.LevelSeam[i] : "",
                });
            foreach (var it in p.Items)
            {
                if (it.MatchedLevel < 0 || it.MatchedLevel >= p.LevelUsage.Count) continue;
                if (!it.Removes) continue;
                var u = p.LevelUsage[it.MatchedLevel];
                u.OldCount++;
                u.OldDropLength += it.DropLength;
            }
        }
        var order = new List<string>();
        var map = new Dictionary<string, BenchSeamTally>(StringComparer.Ordinal);
        BenchSeamTally Get(string seam)
        {
            if (!map.TryGetValue(seam, out var t)) { t = new BenchSeamTally { Seam = seam }; map[seam] = t; order.Add(seam); }
            return t;
        }
        foreach (var u in p.LevelUsage)
        {
            if (string.IsNullOrEmpty(u.Seam)) continue;
            var t = Get(u.Seam);
            t.BenchCount++;
            if (u.Touched) t.TouchedCount++;
            t.ReplacedLength += u.OldDropLength;
        }
        if (roster != null)
            for (int i = 0; i < roster.Count; i++)
            {
                string s = roster[i];
                if (string.IsNullOrEmpty(s)) continue;
                var t = Get(s);
                t.InRoster = true;
                t.Ready = ready == null || i >= ready.Count || ready[i];
            }
        foreach (var s in order) p.SeamTally.Add(map[s]);
    }

    private static void Demote(BenchReplaceItem it)
    {
        it.Verdict = BenchReplaceVerdict.Unmatched;
        it.Keep.Clear(); it.Drop.Clear(); it.Cuts.Clear();
        it.KeptLength = it.Length;
    }

    private static string Describe(BenchReplacePlan p, double margin, double latTol, bool back, double latMargin)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append($"工作线 {p.WorkLineCount} 条（{p.Bands.Count} 条圈出覆盖范围）· 新台阶线 {p.NewLineCount} 条/{p.NewLength.ToString("0.#", ci)}m · ");
        sb.Append($"老台阶线扫过 {p.Items.Count} 条/{p.ScannedLength.ToString("0.#", ci)}m → ");
        sb.Append($"取代 {p.SupersededCount} 条 + 裁剪 {p.ClippedCount} 条 ⇒ 丢弃 {p.DroppedLength.ToString("0.#", ci)}m、");
        sb.Append($"留下 {p.KeptLength.ToString("0.#", ci)}m（含不动的 {p.UntouchedCount} 条");
        if (p.UnmatchedCount > 0) sb.Append($"、归不进级的 {p.UnmatchedCount} 条");
        sb.Append("）· ");
        sb.Append($"余量 ±{margin.ToString("0.#", ci)}m · 纵向容差 {latTol.ToString("0.#", ci)}m · 下界{(back ? "回溯到工作线" : "取模板前界")}");
        sb.Append(p.LateralToTemplate ? $" · 横向限在模板影响区域内（走向余量 ±{latMargin.ToString("0.#", ci)}m）" : " · 横向吃满整条工作线宽（未限模板影响区域）");
        if (p.Levels is { Count: > 0 })
        {
            sb.Append($" · 逐级替换：{p.Levels.Count} 级新台阶（{(p.LevelsFromHandoff ? "驱动量交接单" : "图上反推")}），归格容差 ±{p.Levels.Tol.ToString("0.##", ci)}m，其中触及到老台阶 {p.LevelUsage.Count - p.UntouchedLevelCount} 级、碰不到 {p.UntouchedLevelCount} 级");
            int noBench = 0;
            foreach (var t in p.SeamTally) if (t.InRoster && t.BenchCount == 0) noBench++;
            if (noBench > 0) sb.Append($" · 名册里 {noBench} 层煤一条台阶线都没建出来");
        }
        else sb.Append(" · 未启用逐级（只按平面范围）");
        if (p.Overrides.Count > 0) sb.Append($" · 人工改判 {p.Overrides.Count} 条");
        foreach (var b in p.Bands)
        {
            sb.Append($" ｜ 线{b.Line + 1} 推进 [{b.Lo.ToString("0.#", ci)}, {b.Hi.ToString("0.#", ci)}]m（模板 {b.TemplateLo.ToString("0.#", ci)}~{b.TemplateHi.ToString("0.#", ci)}）");
            sb.Append(b.LatClipped
                ? $" × 走向 [{b.LatLo.ToString("0.#", ci)}, {b.LatHi.ToString("0.#", ci)}]m（模板 {b.TemplateLatLo.ToString("0.#", ci)}~{b.TemplateLatHi.ToString("0.#", ci)}，宽 {(b.LatHi - b.LatLo).ToString("0.#", ci)}m）"
                : " × 走向 整条工作线");
        }
        return sb.ToString();
    }

    private static void MedianZ(double[] xyz, out double med, out double spread)
    {
        int n = xyz.Length / 3;
        var zs = new double[n];
        for (int i = 0; i < n; i++) zs[i] = xyz[i * 3 + 2];
        Array.Sort(zs);
        med = zs[n / 2];
        spread = zs[n - 1] - zs[0];
    }

    // ── 逐条裁剪 ───────────────────────────────────────────────────────────

    private const int MaxProbePerSeg = 2048;

    private static void ClipOne(EpPolyline pl, Func<double, double, bool> inside, BenchReplaceItem it, double probe)
    {
        int n = pl.Xyz.Length / 3;
        if (n < 2) { it.Verdict = BenchReplaceVerdict.Untouched; return; }

        int mv = pl.Closed ? n + 1 : n;
        (double X, double Y, double Z) V(int i) { int k = i % n; return (pl.Xyz[k * 3], pl.Xyz[k * 3 + 1], pl.Xyz[k * 3 + 2]); }

        // 工作点列 = 原顶点 + 段内探针（只在内外真的变了的地方插点，插进去的点就在原线上）
        var wp = new List<(double X, double Y, double Z)>(mv + 8);
        var insL = new List<bool>(mv + 8);
        void Push((double X, double Y, double Z) q, bool? known = null) { wp.Add(q); insL.Add(known ?? inside(q.X, q.Y)); }

        Push(V(0));
        for (int i = 1; i < mv; i++)
        {
            var a = V(i - 1); var b = V(i);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            int k = probe > 0 ? (int)Math.Ceiling(len / probe) : 1;
            if (k > MaxProbePerSeg) k = MaxProbePerSeg;
            for (int j = 1; j < k; j++)
            {
                double t = j / (double)k;
                var q = (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
                bool qi = inside(q.Item1, q.Item2);
                if (qi != insL[insL.Count - 1]) Push(q, qi);
            }
            Push(b);
        }

        int m = wp.Count;
        (double X, double Y, double Z) P(int i) => wp[i];
        var ins = insL.ToArray();

        bool anyIn = false, anyOut = false;
        for (int i = 0; i < m; i++) { anyIn |= ins[i]; anyOut |= !ins[i]; }

        if (!anyIn)  { it.Verdict = BenchReplaceVerdict.Untouched;  it.KeptLength = it.Length; return; }
        if (!anyOut) { it.Verdict = BenchReplaceVerdict.Superseded; it.KeptLength = 0; it.Drop.Add(AllPts(pl)); return; }

        it.Verdict = BenchReplaceVerdict.Clipped;

        // 切点取【范围外那一侧】的括号端 —— 保留段写回再跑一次不会又被判成跨界（替换可重复执行）
        (double X, double Y, double Z) Cross(int i0, int i1)
        {
            var a = P(i0); var b = P(i1); bool ia = ins[i0];
            for (int k = 0; k < BisectIters; k++)
            {
                (double X, double Y, double Z) mid = ((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
                if (inside(mid.X, mid.Y) == ia) a = mid; else b = mid;
            }
            return ia ? b : a;
        }

        var runs = new List<(bool In, List<(double X, double Y, double Z)> Pts)>();
        var cur = new List<(double X, double Y, double Z)> { P(0) };
        bool curIn = ins[0];
        for (int i = 1; i < m; i++)
        {
            if (ins[i] == curIn) { cur.Add(P(i)); continue; }
            var x = Cross(i - 1, i);
            cur.Add(x);
            runs.Add((curIn, cur));
            cur = new List<(double X, double Y, double Z)> { x, P(i) };
            curIn = ins[i];
        }
        runs.Add((curIn, cur));

        if (pl.Closed && runs.Count >= 2 && runs[0].In == runs[runs.Count - 1].In)
        {
            var head = runs[0]; var tail = runs[runs.Count - 1];
            var merged = new List<(double X, double Y, double Z)>(tail.Pts);
            for (int i = 1; i < head.Pts.Count; i++) merged.Add(head.Pts[i]);
            runs.RemoveAt(runs.Count - 1);
            runs[0] = (head.In, merged);
        }

        for (int r = 0; r < runs.Count; r++)
        {
            var (rIn, pts) = runs[r];
            if (pts.Count < 2) continue;
            if (rIn) { it.Drop.Add(pts); continue; }
            it.Keep.Add(pts);
            it.KeptLength += Length3D(pts);
            bool headCut = pl.Closed || r > 0;
            bool tailCut = pl.Closed || r < runs.Count - 1;
            if (headCut) it.Cuts.Add(new BenchCut(pts[0].X, pts[0].Y, pts[0].Z, new List<(double, double, double)>(pts)));
            if (tailCut)
            {
                var back = new List<(double X, double Y, double Z)>(pts);
                back.Reverse();
                it.Cuts.Add(new BenchCut(back[0].X, back[0].Y, back[0].Z, back));
            }
        }
    }

    // ── 覆盖范围环（示意用）─────────────────────────────────────────────────

    private static double[]? BandRing(WorkLineSamples wl, double lo, double hi, double latTol, double sFrom, double sTo)
    {
        if (wl == null || !wl.Success || wl.Baseline.Count < 2 || wl.Samples.Count < 1) return null;
        int cnt = wl.Baseline.Count;
        int nSeg = Math.Min(wl.Samples.Count, cnt - 1 + (wl.Closed ? 1 : 0));
        if (nSeg < 1) return null;

        var cum = new double[nSeg + 1];
        for (int i = 0; i < nSeg; i++)
        {
            var a = wl.Baseline[i]; var b = wl.Baseline[(i + 1) % cnt];
            double sx = b.X - a.X, sy = b.Y - a.Y;
            double sl = Math.Sqrt(sx * sx + sy * sy);
            cum[i + 1] = cum[i] + (sl < 1e-9 ? 1.0 : sl);
        }
        double total = cum[nSeg];
        double ext = wl.Closed ? 0 : latTol;
        double s0 = Math.Max(sFrom, -ext), s1 = Math.Min(sTo, total + ext);
        if (s1 - s0 < 1e-9) return null;

        var pts = new List<(double X, double Y, double Dx, double Dy)>(cnt + 2);
        pts.Add(SampleBand(wl, cum, nSeg, cnt, s0, -1));
        for (int i = 0; i <= nSeg; i++)
        {
            if (cum[i] <= s0 + 1e-9 || cum[i] >= s1 - 1e-9) continue;
            pts.Add(SampleBand(wl, cum, nSeg, cnt, cum[i], i));
        }
        pts.Add(SampleBand(wl, cum, nSeg, cnt, s1, -1));
        if (pts.Count < 2) return null;

        var ring = new List<double>(pts.Count * 4);
        for (int i = 0; i < pts.Count; i++) { var q = pts[i]; ring.Add(q.X + q.Dx * lo); ring.Add(q.Y + q.Dy * lo); }
        for (int i = pts.Count - 1; i >= 0; i--) { var q = pts[i]; ring.Add(q.X + q.Dx * hi); ring.Add(q.Y + q.Dy * hi); }
        return ring.ToArray();
    }

    private static (double X, double Y, double Dx, double Dy) SampleBand(WorkLineSamples wl, double[] cum, int nSeg, int cnt, double s, int vi)
    {
        int seg; double f;
        if (s <= cum[0]) { seg = 0; f = (s - cum[0]) / Math.Max(1e-9, cum[1] - cum[0]); }
        else if (s >= cum[nSeg]) { seg = nSeg - 1; f = 1 + (s - cum[nSeg]) / Math.Max(1e-9, cum[nSeg] - cum[nSeg - 1]); }
        else
        {
            int a = 0, b = nSeg;
            while (a + 1 < b) { int mid = (a + b) / 2; if (cum[mid] <= s) a = mid; else b = mid; }
            seg = a; f = (s - cum[seg]) / Math.Max(1e-9, cum[seg + 1] - cum[seg]);
        }
        var v0 = wl.Baseline[seg]; var v1 = wl.Baseline[(seg + 1) % cnt];
        double x = v0.X + (v1.X - v0.X) * f, y = v0.Y + (v1.Y - v0.Y) * f;

        double ax = 0, ay = 0;
        if (vi >= 0)
        {
            int sPrev = wl.Closed ? (vi - 1 + nSeg) % nSeg : vi - 1;
            int sNext = wl.Closed ? vi % nSeg : vi;
            if (sPrev >= 0 && sPrev < nSeg) { ax += wl.Samples[sPrev].Dx; ay += wl.Samples[sPrev].Dy; }
            if (sNext >= 0 && sNext < nSeg && sNext != sPrev) { ax += wl.Samples[sNext].Dx; ay += wl.Samples[sNext].Dy; }
        }
        if (ax * ax + ay * ay < 1e-18) { ax = wl.Samples[seg].Dx; ay = wl.Samples[seg].Dy; }
        double al = Math.Sqrt(ax * ax + ay * ay);
        if (al < 1e-9)
        {
            double sx = v1.X - v0.X, sy = v1.Y - v0.Y;
            double sl = Math.Sqrt(sx * sx + sy * sy);
            if (sl < 1e-9) return (x, y, 1, 0);
            return (x, y, -sy / sl, sx / sl);
        }
        return (x, y, ax / al, ay / al);
    }

    // ── 小工具 ─────────────────────────────────────────────────────────────

    private static List<(double X, double Y, double Z)> AllPts(EpPolyline pl)
    {
        int n = pl.Xyz.Length / 3;
        var l = new List<(double, double, double)>(n + 1);
        for (int i = 0; i < n; i++) l.Add((pl.Xyz[i * 3], pl.Xyz[i * 3 + 1], pl.Xyz[i * 3 + 2]));
        if (pl.Closed && n >= 2) l.Add(l[0]);
        return l;
    }

    private static double Length3D(double[] xyz, bool closed)
    {
        int n = xyz.Length / 3;
        double s = 0;
        int limit = closed ? n : n - 1;
        for (int i = 0; i < limit; i++)
        {
            int j = (i + 1) % n;
            double dx = xyz[j * 3] - xyz[i * 3], dy = xyz[j * 3 + 1] - xyz[i * 3 + 1], dz = xyz[j * 3 + 2] - xyz[i * 3 + 2];
            s += Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return s;
    }

    private static double Length3D(List<(double X, double Y, double Z)> pts)
    {
        double s = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double dx = pts[i + 1].X - pts[i].X, dy = pts[i + 1].Y - pts[i].Y, dz = pts[i + 1].Z - pts[i].Z;
            s += Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return s;
    }
}
