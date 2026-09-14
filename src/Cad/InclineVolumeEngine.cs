using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>一层煤的顶/底板面采样器 + 判煤/容重/增量（忠实原 <c>SeamSurfaces</c>）。缺失则为 null（尖灭/未选）。</summary>
public sealed class SeamSurfaces
{
    public string Name = "";
    public TinSampler? Roof;
    public TinSampler? Floor;
    public string Attribute = "";
    /// <summary>该层在属性里对应的类别（类别码数字或类别名）；空 = 走全局煤判据。</summary>
    public string Category = "";
    public double Density = 1.35;
    public double IncrementWt;
    /// <summary>该层煤台阶高度(m)：0 = 随煤厚整层一个台阶。</summary>
    public double BenchHeight;
    public bool Ready => Roof != null && Floor != null;
}

/// <summary>
/// 喂给「驱动量」引擎的块体（Kylin：块体单元表 + 逐块属性列；原版是网格规格 + CellData/Leaves）。
/// </summary>
public sealed class InclineBlockSource
{
    public string Name = "";
    public IReadOnlyList<BlockModel.Block> Blocks = Array.Empty<BlockModel.Block>();
    /// <summary>逐块属性列（列名 → 与 Blocks 同序的值）。</summary>
    public IReadOnlyDictionary<string, double[]>? Attrs;
    /// <summary>类别名表：属性名 → 类别标签（下标 = 类别码）。没有就按裸码解析类别。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? CategoryLabels;
    public Func<int, bool>? IsDeleted;

    /// <summary>从块体仓模型建（属性列 + 类别名表 + 删除集同源）。</summary>
    public static InclineBlockSource FromMeta(BlockModelMeta m)
    {
        var labels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var c in m.PropertySchema) if (c.CategoryLabels is { Count: > 0 }) labels[c.Name] = c.CategoryLabels;
        return new InclineBlockSource
        {
            Name = m.Name, Blocks = m.Blocks, Attrs = m.Attrs, CategoryLabels = labels,
            IsDeleted = m.DeletedIds.Count > 0 ? (i => m.DeletedIds.Contains(i)) : null,
        };
    }

    /// <summary>取属性列；"Grade" / "品位" / 空 → 块体自带品位。列不存在返回 null。</summary>
    public double[]? AttrArray(string attr)
    {
        if (string.IsNullOrEmpty(attr) || attr == "Grade" || attr == "品位")
        {
            var g = new double[Blocks.Count];
            for (int i = 0; i < g.Length; i++) g[i] = Blocks[i].Grade;
            return g;
        }
        return Attrs != null && Attrs.TryGetValue(attr, out var a) ? a : null;
    }

    public bool HasAttribute(string attr) => string.IsNullOrEmpty(attr) || attr == "Grade" || attr == "品位" || (Attrs != null && Attrs.ContainsKey(attr));

    /// <summary>类别 → 类别码：先按类别名表（下标），再按裸数字；解不出返回 −1。</summary>
    public int ResolveCategoryCode(string attr, string category)
    {
        if (string.IsNullOrEmpty(category)) return -1;
        if (CategoryLabels != null && CategoryLabels.TryGetValue(attr, out var labels))
        {
            for (int i = 0; i < labels.Count; i++) if (string.Equals(labels[i], category, StringComparison.Ordinal)) return i;
        }
        return double.TryParse(category, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 0 ? (int)Math.Round(v) : -1;
    }

    public (double minZ, double maxZ, double sx, double sz, long count) Extent()
    {
        double minZ = double.MaxValue, maxZ = double.MinValue, sx = 0, sz = 0;
        foreach (var b in Blocks) { minZ = Math.Min(minZ, b.Z - b.Size * 0.5); maxZ = Math.Max(maxZ, b.Z + b.Size * 0.5); sx = Math.Max(sx, b.Size); sz = Math.Max(sz, b.Size); }
        return (minZ, maxZ, sx, sz, Blocks.Count);
    }
}

/// <summary>逐层煤沿推进轴 s 的累煤剖面（吨/桶）。Stage1/Stage2 都从它求解，块体只扫一遍。忠实原 <c>CoalProfile</c>。</summary>
public sealed class CoalProfile
{
    public bool   Success;
    public string Error = "";
    public double SliceWidth;
    public int    SeamCount;
    public string[] SeamNames = Array.Empty<string>();
    public double[] SeamIncrementWt = Array.Empty<double>();
    public Dictionary<long, double>[] SeamBins = Array.Empty<Dictionary<long, double>>();
    public double TotalMaxWt;
    public long   CoalCellCount;
    public readonly List<string> Notes = new();
    public int    LineCount;
    public Dictionary<long, double>[] LineBins = Array.Empty<Dictionary<long, double>>();
    public RockProfile? Rock;

    public double CoalAtLine(int line, double d)
    {
        if (line < 0 || line >= LineCount || LineBins[line] == null) return 0;
        double dz = SliceWidth > 1e-9 ? SliceWidth : 1.0;
        double lim = d / dz, ton = 0;
        foreach (var kv in LineBins[line])
        {
            if (kv.Key + 1 <= lim) ton += kv.Value;
            else if (kv.Key < lim) ton += kv.Value * (lim - kv.Key);
        }
        return ton / 1e4;
    }

    public double LineMaxWt(int line)
    {
        if (line < 0 || line >= LineCount || LineBins[line] == null) return 0;
        double ton = 0; foreach (var kv in LineBins[line]) ton += kv.Value; return ton / 1e4;
    }
}

public sealed class Stage1Result
{
    public bool   Success;
    public string Error = "";
    public double Advance, TotalCoalWt, TargetWt, MaxCoalWt, SliceWidth;
    public bool   Reached;
    public long   CoalCellCount;
    public double TolPct = 5.0;
    public double DeviationPct => TargetWt > 1e-9 ? (TotalCoalWt - TargetWt) / TargetWt * 100.0 : 0;
    public bool   WithinTol => TargetWt <= 1e-9 || (DeviationPct >= -0.5 && DeviationPct <= TolPct + 1e-6);
}

public sealed class Stage2SeamResult
{
    public string Name = "";
    public double Stage2Advance;
    public double Stage1Wt;
    public double IncrementWt;
    public double AchievedWt;
    public bool   Reached;
    public double TolPct = 5.0;
    public double TotalWt => Stage1Wt + AchievedWt;
    public double DeviationPct => IncrementWt > 1e-9 ? (AchievedWt - IncrementWt) / IncrementWt * 100.0 : 0;
    public bool   WithinTol => IncrementWt <= 1e-9 || (DeviationPct >= -0.5 && DeviationPct <= TolPct + 1e-6);
}

public sealed class Stage2Result
{
    public bool   Success;
    public string Error = "";
    public double Stage1Advance;
    public readonly List<Stage2SeamResult> Seams = new();
    public double TargetTotalWt;
    public double SumSeamTargetWt;
    public double AchievedTotalWt;
    public double TolPct = 5.0;
    public double SeamTargetDeviationPct => TargetTotalWt > 1e-9 ? (SumSeamTargetWt - TargetTotalWt) / TargetTotalWt * 100.0 : 0;
    public bool SeamTargetsMatchTotal => TargetTotalWt <= 1e-9 || Math.Abs(SeamTargetDeviationPct) <= TolPct + 1e-6;
    public double AchievedDeviationPct => TargetTotalWt > 1e-9 ? (AchievedTotalWt - TargetTotalWt) / TargetTotalWt * 100.0 : 0;
    public string TotalCheckText
        => TargetTotalWt <= 1e-9 ? "回采煤量总量未填 —— 未做校验。"
         : SeamTargetsMatchTotal ? $"回采煤量校验✓：各层增量之和 {SumSeamTargetWt:0.0} 万t 对得上总量 {TargetTotalWt:0.0} 万t（偏差 {SeamTargetDeviationPct:+0.0;-0.0}%，容差±{TolPct:0.#}%）。"
         : $"回采煤量校验✗：各层增量之和 {SumSeamTargetWt:0.0} 万t，与总量 {TargetTotalWt:0.0} 万t相差 {SumSeamTargetWt - TargetTotalWt:+0.0;-0.0} 万t（{SeamTargetDeviationPct:+0.0;-0.0}%，容差±{TolPct:0.#}%）。【求解按各层填的数走，不按总量走】—— 总量不会被摊到各层，请自行把各层增量改到加起来等于总量。";
}

/// <summary>前界倒挂体检结果：空 = 自下而上单调不减，没有倒挂。</summary>
public sealed class Stage2OverhangReport
{
    public sealed class Item
    {
        public string LowerName = "", UpperName = "";
        public double LowerAdvance, UpperAdvance, OverhangM, LowerTargetWt, LowerCappedWt;
    }
    public readonly List<Item> Items = new();
    public bool Ok => Items.Count == 0;
    public string Text
        => Ok ? "前界单调✓：逐层 d_seam 自下而上单调不减，帮面无悬挑。"
             : "前界倒挂✗：" + string.Join("；", Items.Select(t => $"下伏「{t.LowerName}」d_seam={t.LowerAdvance:0.#}m 比上覆「{t.UpperName}」={t.UpperAdvance:0.#}m 多推 {t.OverhangM:0.#}m ⇒ 上覆岩体悬挑 {t.OverhangM:0.#}m"
                 + (double.IsNaN(t.LowerCappedWt) ? "" : $"；不倒挂的话「{t.LowerName}」最多只出得了 {t.LowerCappedWt:0.0}万t（目标 {t.LowerTargetWt:0.0}万t）")))
               + "。【求解按各层填的数走，引擎不替你改目标】—— 请把各层增量改到自下而上单调不减。";
}

/// <summary>
/// 「驱动量」（量驱动斜面模板）量驱动引擎（忠实原 <c>InclineVolumeEngine</c>；块体侧改吃 Kylin 单元表）。
/// 逐块判 ①是煤（各层属性/类别）②在现状面下 ③s 投影，累成逐层 s 剖面；Stage1 合并求全局 d（年产量），Stage2 逐层在 d 之外求增量 d_seam。
/// s = a0 − (cz − z_datum)/tanα；多线同步推进同一 d。
/// </summary>
public static class InclineVolumeEngine
{
    public readonly struct SeamRule
    {
        public readonly int Mode; public readonly double Cot; public readonly double Dens;
        public SeamRule(int mode, double cot, double dens) { Mode = mode; Cot = cot; Dens = dens; }
    }

    /// <summary>逐层判煤规则：优先按「类别码」（指定了类别），否则退到全局煤判据 coalMode(1=属性≥阈值 2=全部算煤 其余=非零)。类别解不出 → 永不命中（并记 ◆ 注）。</summary>
    public static SeamRule[] BuildSeamRules(InclineBlockSource src, IReadOnlyList<SeamSurfaces> seams, int coalMode, double coalThreshold, List<string>? notes = null)
    {
        int M = seams.Count;
        var rules = new SeamRule[M];
        for (int m = 0; m < M; m++)
        {
            var s = seams[m];
            double dens = s.Density > 0 ? s.Density : 1.35;
            if (!(s.Density > 0)) notes?.Add($"⚠ 「{s.Name}」容重填的是 {s.Density:0.###} t/m³（须为正），已按 1.35 处理 —— 吨量会按这个算");
            if (!src.HasAttribute(s.Attribute)) notes?.Add($"⚠ 「{s.Name}」的属性列「{s.Attribute}」在块体里没数据 —— 该层多半一吨煤都数不到");
            int mode; double cot;
            if (!string.IsNullOrEmpty(s.Category))
            {
                int code = src.ResolveCategoryCode(s.Attribute, s.Category);
                if (code < 0) notes?.Add($"◆「{s.Name}」的类别「{s.Category}」在属性「{s.Attribute}」里解不出类别码 —— **该层永远数不到煤**，而其他层照常有煤、剖面照样报成功");
                mode = 0; cot = code;
            }
            else if (coalMode == 2) { mode = 2; cot = 0; }
            else if (coalMode == 1) { mode = 1; cot = coalThreshold; }
            else { mode = 3; cot = 0; }
            rules[m] = new SeamRule(mode, cot, dens);
        }
        return rules;
    }

    public static bool Hit(in SeamRule r, double v) => r.Mode switch
    {
        0 => r.Cot >= 0 && (int)Math.Round(v) == (int)r.Cot,
        1 => v >= r.Cot,
        2 => true,
        _ => v != 0,
    };

    public delegate void CellSink(double cx, double cy, double cz, double czSize, double vol, int seam);

    public static CoalProfile BuildProfile(
        InclineBlockSource? src, IReadOnlyList<WorkLineSamples> workLines, TinSampler? currentSurface,
        IReadOnlyList<SeamSurfaces> seams, double alphaDeg, int coalMode, double coalThreshold,
        double rockBenchHeight = 0, CellSink? cellSink = null)
    {
        var p = new CoalProfile();
        if (src == null || src.Blocks.Count == 0) { p.Error = "未选块体（空模型？）"; return p; }
        if (workLines == null || workLines.Count == 0) { p.Error = "无工作线"; return p; }
        if (seams == null || seams.Count == 0) { p.Error = "无煤层"; return p; }
        var (oz, topZ, sxy, szMax, count) = src.Extent();
        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);
        double dz = Math.Max(1e-3, sxy);
        p.SliceWidth = dz;

        int M = seams.Count;
        var rules = BuildSeamRules(src, seams, coalMode, coalThreshold, p.Notes);
        p.SeamNames = new string[M]; p.SeamIncrementWt = new double[M]; p.SeamBins = new Dictionary<long, double>[M];
        for (int m = 0; m < M; m++) { p.SeamNames[m] = seams[m].Name; p.SeamIncrementWt[m] = seams[m].IncrementWt; p.SeamBins[m] = new Dictionary<long, double>(); }
        p.SeamCount = M;
        p.LineCount = workLines.Count;
        p.LineBins = new Dictionary<long, double>[p.LineCount];
        for (int l = 0; l < p.LineCount; l++) p.LineBins[l] = new Dictionary<long, double>();

        RockProfile? rock = null;
        double rockH = rockBenchHeight;
        if (rockH > 1e-6)
        {
            rock = new RockProfile
            {
                SliceWidth = dz, BenchHeight = rockH, SeamCount = M,
                SeamNames = (string[])p.SeamNames.Clone(), GapNames = GapCode.Names(p.SeamNames),
                CoalVolBins = new Dictionary<long, double>[M], CoalZMoment = new Dictionary<long, double>[M], SeamDensity = new double[M],
            };
            for (int m = 0; m < M; m++) { rock.CoalVolBins[m] = new Dictionary<long, double>(); rock.CoalZMoment[m] = new Dictionary<long, double>(); rock.SeamDensity[m] = rules[m].Dens; }
            for (int g = 1; g < M; g++) rock.GapNames[g] = $"层间{g}（自上而下第{g}道）";
            rock.Provenance = BuildProvenance(src, workLines, currentSurface, seams, rules, alphaDeg, coalMode, coalThreshold, dz, rockH, oz, topZ, sxy, szMax);
            p.Rock = rock;
        }

        var proj = WorkLineProjector.Build(workLines, sxy);
        if (!proj.HasAny) { p.Error = "无有效工作线（几何退化）"; return p; }

        var arr = new double[M][];
        for (int m = 0; m < M; m++) arr[m] = src.AttrArray(seams[m].Attribute) ?? Array.Empty<double>();

        // 逐列缓存：投影 + 顶底板层序（同 (x,y) 的块共用）
        var colCache = new Dictionary<(long, long), (bool sw, double a0, double zd, int line, float[]? roof, float[]? floor, int[]? ord, int ordN)>();
        double maxTon = 0; long coalCells = 0;
        var blocks = src.Blocks;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (src.IsDeleted != null && src.IsDeleted(i)) continue;
            var b = blocks[i];
            double sz = b.Size > 0 ? b.Size : 1;
            var key = ((long)Math.Floor(b.X / sz + 1e-9), (long)Math.Floor(b.Y / sz + 1e-9));
            if (!colCache.TryGetValue(key, out var col))
            {
                bool sw = proj.TryProject(b.X, b.Y, out double a0c, out double zdc, out int lnc);
                float[]? rf = null, fl = null; int[]? od = null; int on = 0;
                if (sw && rock != null)
                {
                    rf = new float[M]; fl = new float[M]; od = new int[M];
                    on = SampleSeamOrder(seams, b.X, b.Y, rf, fl, od, 0, out bool crossed);
                    if (crossed) rock.CrossedColumns++;
                }
                col = (sw, a0c, zdc, lnc, rf, fl, od, on);
                colCache[key] = col;
            }
            if (!col.sw) continue;
            double cx = b.X, cy = b.Y, cz = b.Z, vol = sz * sz * sz;

            int seam = -1; double density = 0;
            for (int m = 0; m < M; m++)
            {
                if (i >= arr[m].Length) continue;
                if (Hit(rules[m], arr[m][i])) { seam = m; density = rules[m].Dens; break; }
            }
            if (seam < 0 && rock == null) continue;
            if (currentSurface != null && currentSurface.TrySampleZ(cx, cy, out double surfZ) && cz >= surfZ) continue;   // 现状面之上 = 已采
            double s = col.a0 - (cz - col.zd) / tanA;
            if (s < 0) continue;                                                                                          // 起始工作帮之内，已采掉

            if (seam >= 0)
            {
                long bin = (long)Math.Floor(s / dz);
                double ton = vol * density;
                p.SeamBins[seam].TryGetValue(bin, out double curv); p.SeamBins[seam][bin] = curv + ton;
                if (col.line >= 0 && col.line < p.LineCount) { p.LineBins[col.line].TryGetValue(bin, out double lv2); p.LineBins[col.line][bin] = lv2 + ton; }
                maxTon += ton; coalCells++;
                if (rock != null) rock.AddCoalVol(seam, (long)Math.Floor(col.a0 / dz), vol, cz);
                cellSink?.Invoke(cx, cy, cz, sz, vol, seam);
                continue;
            }
            if (col.ordN <= 0) rock!.UnclassifiedCells++;
            int g = ClassifyGap(cz, col.roof, col.floor, col.ord, 0, col.ordN, M);
            int k = (int)Math.Floor(cz / rockH);
            rock!.AddRock(k, g, (long)Math.Floor(col.a0 / dz), vol);
            rock.RockCellCount++;
            cellSink?.Invoke(cx, cy, cz, sz, vol, -1);
        }

        p.TotalMaxWt = maxTon / 1e4; p.CoalCellCount = coalCells;
        for (int m = 0; m < M; m++) if (p.SeamBins[m].Count == 0) p.Notes.Add($"◆「{p.SeamNames[m]}」一吨煤都没数到 —— 查判煤规则（属性/类别/阈值）或该层是否在扫掠域内");
        if (coalCells == 0)
        {
            p.Error = "工作线扫掠+现状面下没数到煤（方向/位置/属性/类别不对？）" + (p.Notes.Count > 0 ? "；" + string.Join("；", p.Notes) : "");
            return p;
        }
        if (rock != null)
        {
            rock.Success = true;
            if (rock.RockCellCount == 0) rock.Error = "起始斜面外+现状面下没数到岩（整片已剥完？还是块体只覆盖了煤？）";
            if (rock.LevelMin > rock.LevelMax) { rock.LevelMin = 0; rock.LevelMax = 0; }
            if (rock.MinBin > rock.MaxBin) { rock.MinBin = 0; rock.MaxBin = 0; }
        }
        p.Success = true;
        return p;
    }

    private static ProfileProvenance BuildProvenance(InclineBlockSource src, IReadOnlyList<WorkLineSamples> workLines, TinSampler? surface, IReadOnlyList<SeamSurfaces> seams, SeamRule[] rules,
                                                     double alphaDeg, int coalMode, double coalThreshold, double dz, double rockH, double oz, double topZ, double sxy, double sz)
    {
        var pv = new ProfileProvenance
        {
            BlockModelName = src.Name ?? "", BlockCellCount = src.Blocks.Count,
            BlockOz = oz, BlockSx = sxy, BlockSy = sxy, BlockSz = sz, BlockNz = sz > 0 ? (int)Math.Round((topZ - oz) / sz) : 0,
            AlphaDeg = alphaDeg, SliceWidth = dz, BenchHeight = rockH, CoalMode = coalMode, CoalThreshold = coalThreshold,
            CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            Software = typeof(InclineVolumeEngine).Assembly.GetName().Version?.ToString() ?? "",
        };
        pv.WorkLineKey = ProfileProvenance.FoldWorkLines(workLines);
        pv.SurfaceKey = FoldSurface(ProfileProvenance.Seed, surface);
        ulong sh = ProfileProvenance.Seed;
        foreach (var s in seams) { sh = FoldSurface(sh, s.Roof); sh = FoldSurface(sh, s.Floor); }
        pv.SeamSurfaceKey = sh;
        ulong rh = ProfileProvenance.Seed;
        for (int m = 0; m < seams.Count; m++)
        {
            rh = ProfileProvenance.Fold(rh, seams[m].Name); rh = ProfileProvenance.Fold(rh, seams[m].Attribute); rh = ProfileProvenance.Fold(rh, seams[m].Category);
            rh = ProfileProvenance.Fold(rh, rules[m].Dens); rh = ProfileProvenance.Fold(rh, rules[m].Mode); rh = ProfileProvenance.Fold(rh, rules[m].Cot);
        }
        pv.SeamRuleKey = rh;
        return pv;
    }

    private static ulong FoldSurface(ulong h, TinSampler? t)
    {
        if (t == null) return ProfileProvenance.Fold(h, -12345.0);
        h = ProfileProvenance.Fold(h, t.MinX); h = ProfileProvenance.Fold(h, t.MaxX); h = ProfileProvenance.Fold(h, t.MinY); h = ProfileProvenance.Fold(h, t.MaxY); h = ProfileProvenance.Fold(h, t.MinZ); h = ProfileProvenance.Fold(h, t.MaxZ);
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
            {
                double x = t.MinX + (t.MaxX - t.MinX) * (i + 0.5) / 5.0, y = t.MinY + (t.MaxY - t.MinY) * (j + 0.5) / 5.0;
                h = ProfileProvenance.Fold(h, t.TrySampleZ(x, y, out double z) ? z : double.NaN);
            }
        return h;
    }

    /// <summary>在 (x,y) 处采样各层顶/底板，按顶板标高降序排进 ord*（区间 [base, base+返回值)），返回有效层数。</summary>
    public static int SampleSeamOrder(IReadOnlyList<SeamSurfaces> seams, double x, double y, float[] roof, float[] floor, int[] ord, int baseIdx, out bool crossed)
    {
        int n = 0; crossed = false;
        for (int m = 0; m < seams.Count; m++)
        {
            var s = seams[m];
            if (s.Roof == null || s.Floor == null) continue;
            if (!s.Roof.TrySampleZ(x, y, out double rz)) continue;
            if (!s.Floor.TrySampleZ(x, y, out double fz)) continue;
            if (fz > rz) (rz, fz) = (fz, rz);
            int t = n;
            while (t > 0 && roof[baseIdx + t - 1] < rz)
            {
                roof[baseIdx + t] = roof[baseIdx + t - 1]; floor[baseIdx + t] = floor[baseIdx + t - 1]; ord[baseIdx + t] = ord[baseIdx + t - 1];
                t--;
            }
            roof[baseIdx + t] = (float)rz; floor[baseIdx + t] = (float)fz; ord[baseIdx + t] = m;
            n++;
        }
        for (int t = 0; t + 1 < n; t++) if (floor[baseIdx + t] < roof[baseIdx + t + 1]) { crossed = true; break; }
        return n;
    }

    public static int ClassifyGap(double cz, float[]? roof, float[]? floor, int[]? ord, int baseIdx, int n, int seamCount)
    {
        if (n <= 0 || roof == null || floor == null || ord == null) return GapCode.Overburden;
        if (cz >= roof[baseIdx]) return GapCode.Overburden;
        for (int t = 0; t < n; t++)
        {
            if (cz >= floor[baseIdx + t]) return GapCode.Parting(seamCount, ord[baseIdx + t]);
            if (t + 1 < n && cz >= roof[baseIdx + t + 1]) return 1 + t;
        }
        return GapCode.Underburden(seamCount);
    }

    public static Stage1Result SolveStage1(CoalProfile prof, double targetWt, double tolPct = 5.0)
    {
        var r = new Stage1Result { TargetWt = targetWt, MaxCoalWt = prof.TotalMaxWt, SliceWidth = prof.SliceWidth, CoalCellCount = prof.CoalCellCount, TolPct = tolPct > 0 ? tolPct : 5.0 };
        if (!prof.Success) { r.Error = prof.Error; return r; }
        double dz = prof.SliceWidth;
        var total = new Dictionary<long, double>();
        foreach (var bins in prof.SeamBins) foreach (var kv in bins) { total.TryGetValue(kv.Key, out double cv); total[kv.Key] = cv + kv.Value; }
        var keys = new List<long>(total.Keys); keys.Sort();
        double target = targetWt * 1e4, cum = 0, d = keys.Count > 0 ? (keys[keys.Count - 1] + 1) * dz : 0;
        bool reached = false; double totalAtD = prof.TotalMaxWt * 1e4;
        double tolFrac = (tolPct > 0 ? tolPct : 5.0) / 100.0;
        foreach (long bin in keys)
        {
            double t = total[bin];
            if (cum + t >= target)
            {
                double achievedCeil = cum + t;
                if (achievedCeil <= target * (1 + tolFrac)) { d = (bin + 1) * dz; totalAtD = achievedCeil; }
                else
                {
                    double aim = target * (1 + tolFrac * 0.5);
                    double frac = t > 1e-9 ? (aim - cum) / t : 1.0;
                    d = (bin + Math.Max(0, Math.Min(1, frac))) * dz; totalAtD = aim;
                }
                reached = true; break;
            }
            cum += t;
        }
        r.Advance = d; r.TotalCoalWt = totalAtD / 1e4; r.Reached = reached; r.Success = true;
        return r;
    }

    public static Stage2Result SolveStage2(CoalProfile prof, double stage1Advance, double tolPct = 5.0, double totalIncWt = 0)
    {
        double tol = tolPct > 0 ? tolPct : 5.0;
        var r = new Stage2Result { Stage1Advance = stage1Advance, TolPct = tol, TargetTotalWt = totalIncWt > 0 ? totalIncWt : 0 };
        if (!prof.Success) { r.Error = prof.Error; return r; }
        double dz = prof.SliceWidth;
        for (int m = 0; m < prof.SeamCount; m++)
        {
            var bins = prof.SeamBins[m];
            var keys = new List<long>(bins.Keys); keys.Sort();
            double upTo = CoalUpTo(bins, dz, stage1Advance);
            double seamMax = 0; foreach (var kv in bins) seamMax += kv.Value;
            double inc = prof.SeamIncrementWt[m] * 1e4;
            double target = upTo + inc;
            double cum = 0, d = keys.Count > 0 ? (keys[keys.Count - 1] + 1) * dz : stage1Advance; bool reached = false;
            double tolFrac = (tol > 0 ? tol : 5.0) / 100.0;
            foreach (long bin in keys)
            {
                double t = bins[bin];
                if (cum + t >= target)
                {
                    if ((cum + t) - upTo <= inc * (1 + tolFrac)) d = (bin + 1) * dz;
                    else
                    {
                        double aim = upTo + inc * (1 + tolFrac * 0.5);
                        double frac = t > 1e-9 ? (aim - cum) / t : 1.0;
                        d = (bin + Math.Max(0, Math.Min(1, frac))) * dz;
                    }
                    reached = true; break;
                }
                cum += t;
            }
            double achieved = (reached ? Math.Max(0, CoalUpTo(bins, dz, d) - upTo) : Math.Max(0, seamMax - upTo)) / 1e4;
            r.Seams.Add(new Stage2SeamResult { Name = prof.SeamNames[m], Stage2Advance = Math.Max(d, stage1Advance), Stage1Wt = upTo / 1e4, IncrementWt = prof.SeamIncrementWt[m], AchievedWt = achieved, Reached = reached, TolPct = tol });
        }
        foreach (var ss in r.Seams) { r.SumSeamTargetWt += ss.IncrementWt; r.AchievedTotalWt += ss.AchievedWt; }
        r.Success = true;
        return r;
    }

    public static Stage2OverhangReport CheckFrontMonotonicity(CoalProfile prof, Stage2Result s2, IReadOnlyList<SeamSurfaces> seams, double refX, double refY, double stage1Advance)
        => CheckFrontMonotonicity(seams, s2.Seams.Select(x => x.Stage2Advance).ToList(), refX, refY, prof, stage1Advance, s2.Seams.Select(x => x.IncrementWt).ToList());

    /// <summary>层序按底板标高排（不按数组下标）；只体检、不改解。</summary>
    public static Stage2OverhangReport CheckFrontMonotonicity(IReadOnlyList<SeamSurfaces> seams, IReadOnlyList<double> dSeamBySeam, double refX, double refY,
                                                              CoalProfile? prof = null, double stage1Advance = 0, IReadOnlyList<double>? targetWt = null)
    {
        var rep = new Stage2OverhangReport();
        int n = Math.Min(dSeamBySeam.Count, seams.Count);
        if (n < 2) return rep;
        double RepFloor(SeamSurfaces s) => (s.Floor != null && s.Floor.TrySampleZ(refX, refY, out double z)) ? z : 0;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => RepFloor(seams[a]).CompareTo(RepFloor(seams[b])));
        var clamped = new double[n];
        for (int i = 0; i < n; i++) clamped[i] = dSeamBySeam[i];
        for (int k = n - 2; k >= 0; k--) clamped[order[k]] = Math.Min(clamped[order[k]], clamped[order[k + 1]]);
        bool hasProf = prof is { Success: true } && prof.SeamBins.Length >= n;
        double dz = (prof != null && prof.SliceWidth > 1e-9) ? prof.SliceWidth : 1.0;
        for (int k = 1; k < n; k++)
        {
            int lo = order[k - 1], up = order[k];
            double gap = dSeamBySeam[lo] - dSeamBySeam[up];
            if (gap <= 0.5) continue;
            double capped = double.NaN;
            if (hasProf)
            {
                double upTo = CoalUpTo(prof!.SeamBins[lo], dz, stage1Advance);
                capped = Math.Max(0, CoalUpTo(prof.SeamBins[lo], dz, clamped[lo]) - upTo) / 1e4;
            }
            rep.Items.Add(new Stage2OverhangReport.Item
            {
                LowerName = seams[lo].Name, UpperName = seams[up].Name, LowerAdvance = dSeamBySeam[lo], UpperAdvance = dSeamBySeam[up], OverhangM = gap,
                LowerTargetWt = (targetWt != null && lo < targetWt.Count) ? targetWt[lo] : seams[lo].IncrementWt, LowerCappedWt = capped,
            });
        }
        return rep;
    }

    /// <summary>推进调整联动：固定被拖工作线的推进量，其余取统一推进量，二分求总采出煤 = 目标（保总量不变）。</summary>
    public static double[] RelinkKeepTotal(CoalProfile prof, double targetWt, int dragged, double dDragged)
    {
        int L = prof.LineCount;
        var d = new double[L];
        if (L == 0) return d;
        dDragged = Math.Max(0, dDragged);
        if (L == 1) { d[0] = dDragged; return d; }
        double coalDragged = (dragged >= 0 && dragged < L) ? prof.CoalAtLine(dragged, dDragged) : 0;
        double remain = targetWt - coalDragged;
        if (remain <= 0) { for (int l = 0; l < L; l++) d[l] = (l == dragged) ? dDragged : 0; return d; }
        double dz = prof.SliceWidth > 1e-9 ? prof.SliceWidth : 1.0;
        double hi = 0, sumMax = 0;
        for (int l = 0; l < L; l++) if (l != dragged) { hi = Math.Max(hi, MaxAdvance(prof, l, dz)); sumMax += prof.LineMaxWt(l); }
        double dOther;
        if (sumMax <= remain) dOther = hi;
        else
        {
            double lo = 0;
            for (int it = 0; it < 60; it++)
            {
                double mid = 0.5 * (lo + hi), sum = 0;
                for (int l = 0; l < L; l++) if (l != dragged) sum += prof.CoalAtLine(l, mid);
                if (sum < remain) lo = mid; else hi = mid;
            }
            dOther = hi;
        }
        for (int l = 0; l < L; l++) d[l] = (l == dragged) ? dDragged : dOther;
        return d;
    }

    public static double MaxAdvance(CoalProfile prof, int line, double dz)
    {
        if (line < 0 || line >= prof.LineCount || prof.LineBins[line] == null) return 0;
        long mx = 0; bool any = false;
        foreach (var k in prof.LineBins[line].Keys) { if (!any || k > mx) { mx = k; any = true; } }
        return any ? (mx + 1) * dz : 0;
    }

    public static double CoalUpTo(Dictionary<long, double> bins, double dz, double d)
    {
        if (bins == null || bins.Count == 0 || dz <= 1e-9) return 0;
        double lim = d / dz, ton = 0;
        foreach (var kv in bins)
        {
            if (kv.Key + 1 <= lim) ton += kv.Value;
            else if (kv.Key < lim) ton += kv.Value * (lim - kv.Key);
        }
        return ton;
    }
}
