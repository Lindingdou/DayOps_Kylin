using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 岩量剖面的「层间标签」编码（忠实原 <c>GapCode</c>）。M = 煤层数（标高序）：
/// 0 覆岩 / 1..M-1 层间 g / M 底板下 / M+1+i 第 i 个输入煤层的夹矸。
/// </summary>
public static class GapCode
{
    public const int Overburden = 0;
    public static int Underburden(int seamCount) => Math.Max(1, seamCount);
    public static int Parting(int seamCount, int seamIndex) => Math.Max(1, seamCount) + 1 + seamIndex;
    public static int Count(int seamCount) => Math.Max(1, seamCount) * 2 + 1;

    public static string[] Names(IReadOnlyList<string> seamNames)
    {
        int m = Math.Max(1, seamNames?.Count ?? 1);
        var names = new string[Count(m)];
        names[Overburden] = "覆岩";
        for (int g = 1; g < m; g++) names[g] = $"层间{g}";
        names[Underburden(m)] = "底板下";
        for (int i = 0; i < m; i++) names[Parting(m, i)] = $"{(seamNames != null && i < seamNames.Count ? seamNames[i] : $"层{i}")}·夹矸";
        return names;
    }
}

/// <summary>剖面的指纹：它是从什么算出来的（忠实原 <c>ProfileProvenance</c>）。加载缓存时逐项比对，不匹配就明确报出是哪一项变了并重扫。</summary>
public sealed class ProfileProvenance
{
    public string BlockModelName = "";
    public long   BlockCellCount;
    public double BlockOx, BlockOy, BlockOz, BlockSx, BlockSy, BlockSz;
    public int    BlockNx, BlockNy, BlockNz;
    public ulong  WorkLineKey;
    public ulong  SurfaceKey;
    public ulong  SeamSurfaceKey;
    public double AlphaDeg, SliceWidth, BenchHeight;
    public int    CoalMode;
    public double CoalThreshold;
    public ulong  SeamRuleKey;
    public string CreatedUtc = "";
    public string Software = "";

    public string Text()
        => $"块体「{BlockModelName}」{BlockNx}×{BlockNy}×{BlockNz}({BlockCellCount:N0} cell) · α={AlphaDeg:0.###}° · Δ={SliceWidth:0.###}m · 台阶高={BenchHeight:0.#}m "
         + $"· 判煤 mode{CoalMode}/{CoalThreshold:0.###} · 线{WorkLineKey:X8} 面{SurfaceKey:X8} 层{SeamSurfaceKey:X8} 规则{SeamRuleKey:X8}"
         + (string.IsNullOrEmpty(CreatedUtc) ? "" : $" · {CreatedUtc}");

    public bool Matches(ProfileProvenance? o, out string reason)
    {
        reason = "";
        if (o == null) { reason = "对方没有指纹（旧版剖面？）"; return false; }
        static bool D(double a, double b) => Math.Abs(a - b) > 1e-9;
        if (BlockModelName != o.BlockModelName) { reason = $"块体名 {o.BlockModelName} → {BlockModelName}"; return false; }
        if (BlockCellCount != o.BlockCellCount || BlockNx != o.BlockNx || BlockNy != o.BlockNy || BlockNz != o.BlockNz) { reason = $"块体维度 {o.BlockNx}×{o.BlockNy}×{o.BlockNz} → {BlockNx}×{BlockNy}×{BlockNz}"; return false; }
        if (D(BlockOx, o.BlockOx) || D(BlockOy, o.BlockOy) || D(BlockOz, o.BlockOz) || D(BlockSx, o.BlockSx) || D(BlockSy, o.BlockSy) || D(BlockSz, o.BlockSz)) { reason = "块体原点/单元尺寸变了"; return false; }
        if (WorkLineKey != o.WorkLineKey) { reason = "工作线变了（条数或几何）"; return false; }
        if (SurfaceKey != o.SurfaceKey) { reason = "现状面变了"; return false; }
        if (SeamSurfaceKey != o.SeamSurfaceKey) { reason = "煤层顶/底板变了"; return false; }
        if (D(AlphaDeg, o.AlphaDeg)) { reason = $"工作帮坡角 α {o.AlphaDeg:0.###}° → {AlphaDeg:0.###}°"; return false; }
        if (D(SliceWidth, o.SliceWidth)) { reason = $"分箱宽 Δ {o.SliceWidth:0.###} → {SliceWidth:0.###}m"; return false; }
        if (D(BenchHeight, o.BenchHeight)) { reason = $"岩台阶高 {o.BenchHeight:0.#} → {BenchHeight:0.#}m"; return false; }
        if (CoalMode != o.CoalMode || D(CoalThreshold, o.CoalThreshold)) { reason = "判煤规则变了"; return false; }
        if (SeamRuleKey != o.SeamRuleKey) { reason = "煤层清单/属性/类别/容重变了"; return false; }
        return true;
    }

    public static ulong Seed => 14695981039346656037UL;
    public static ulong Fold(ulong h, double v)
    {
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(Math.Round(v, 6));
        for (int i = 0; i < 8; i++) { h ^= (bits >> (i * 8)) & 0xFF; h *= 1099511628211UL; }
        return h;
    }
    public static ulong Fold(ulong h, string? s)
    {
        if (s == null) return Fold(h, -1.0);
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>折叠一组工作线：只折基线点与推进方向（决定推进轴）。必须只有这一份实现。</summary>
    public static ulong FoldWorkLines(IReadOnlyList<WorkLineSamples>? workLines)
    {
        ulong h = Seed;
        h = Fold(h, workLines?.Count ?? 0);
        if (workLines == null) return h;
        foreach (var wl in workLines)
        {
            if (wl == null) { h = Fold(h, -1.0); continue; }
            h = Fold(h, wl.Baseline.Count);
            foreach (var b in wl.Baseline) { h = Fold(h, b.X); h = Fold(h, b.Y); h = Fold(h, b.Z); }
            foreach (var s in wl.Samples) { h = Fold(h, s.Dx); h = Fold(h, s.Dy); }
        }
        return h;
    }
}

/// <summary>
/// 岩量剖面（忠实原 <c>RockProfile</c>）：沿水平推进轴 u 分箱的原位岩量，按 (标高格 k, 层间 g, u桶 b) 三维稀疏累加。
/// ⚠ u = a0（水平投影距，不带 α 折算）；<see cref="CoalProfile"/> 的煤桶用 s 轴 —— 不是同一根轴。
/// 只存原位实方 m³；吨量 / 松方 / 排弃占容留给下游按物料折算。
/// </summary>
public sealed class RockProfile
{
    public bool   Success;
    public string Error = "";
    public ProfileProvenance? Provenance;
    public double SliceWidth;
    public double BenchHeight;
    public int      SeamCount;
    public string[] SeamNames = Array.Empty<string>();
    public string[] GapNames = Array.Empty<string>();
    public readonly Dictionary<long, double> Bins = new();
    public Dictionary<long, double>[] CoalVolBins = Array.Empty<Dictionary<long, double>>();
    public Dictionary<long, double>[] CoalZMoment = Array.Empty<Dictionary<long, double>>();

    public bool TryCoalZ(int seam, long b, out double z)
    {
        z = 0;
        if (seam < 0 || seam >= CoalVolBins.Length) return false;
        if (!CoalVolBins[seam].TryGetValue(b, out double v) || v <= 1e-9) return false;
        if (!CoalZMoment[seam].TryGetValue(b, out double mz)) return false;
        z = mz / v; return true;
    }

    public bool TryCoalZNear(int seam, double u, out double z, int span = 64)
    {
        z = 0;
        if (SliceWidth <= 1e-9) return false;
        long b0 = (long)Math.Floor(u / SliceWidth);
        for (int d = 0; d <= span; d++)
        {
            if (TryCoalZ(seam, b0 - d, out z)) return true;
            if (d > 0 && TryCoalZ(seam, b0 + d, out z)) return true;
        }
        return false;
    }

    public double[] SeamDensity = Array.Empty<double>();
    public double TotalRockM3;
    public long   RockCellCount;
    public int    LevelMin = int.MaxValue, LevelMax = int.MinValue;
    public long   MinBin = long.MaxValue, MaxBin = long.MinValue;
    public long   UnclassifiedCells;
    public long   CrossedColumns;

    private const int KBias = 2048, KMax = 4095, GMax = 255;

    public static long Pack(int k, int g, long b)
        => ((long)Math.Clamp(k + KBias, 0, KMax) << 40) | ((long)Math.Clamp(g, 0, GMax) << 32) | (uint)(int)Math.Clamp(b, int.MinValue, int.MaxValue);

    public static void Unpack(long key, out int k, out int g, out long b)
    {
        b = (int)(uint)(key & 0xFFFFFFFFL);
        g = (int)((key >> 32) & GMax);
        k = (int)((key >> 40) & KMax) - KBias;
    }

    private void Touch(long b) { if (b < MinBin) MinBin = b; if (b > MaxBin) MaxBin = b; }

    public void AddRock(int k, int g, long b, double volM3)
    {
        _idx = null;
        long key = Pack(k, g, b);
        Bins.TryGetValue(key, out double cur);
        Bins[key] = cur + volM3;
        TotalRockM3 += volM3;
        if (k < LevelMin) LevelMin = k;
        if (k > LevelMax) LevelMax = k;
        Touch(b);
    }

    public void AddCoalVol(int seam, long b, double volM3, double cz)
    {
        if (seam < 0 || seam >= CoalVolBins.Length) return;
        var d = CoalVolBins[seam];
        d.TryGetValue(b, out double cur); d[b] = cur + volM3;
        var mz = CoalZMoment[seam];
        mz.TryGetValue(b, out double curz); mz[b] = curz + volM3 * cz;
        Touch(b);
    }

    public double TotalCoalVolM3() { double v = 0; foreach (var d in CoalVolBins) foreach (var kv in d) v += kv.Value; return v; }

    public double TotalCoalWt()
    {
        double t = 0;
        for (int m = 0; m < CoalVolBins.Length; m++)
        {
            double v = 0; foreach (var kv in CoalVolBins[m]) v += kv.Value;
            double dens = m < SeamDensity.Length && SeamDensity[m] > 0 ? SeamDensity[m] : 1.35;
            t += v * dens;
        }
        return t / 1e4;
    }

    /// <summary>体积加权的平均煤容重 t/m³（下游把"采出多少万t"折回采空区体积用）。</summary>
    public double EffectiveCoalDensity()
    {
        double v = 0;
        for (int m = 0; m < CoalVolBins.Length; m++) foreach (var kv in CoalVolBins[m]) v += kv.Value;
        return v > 1e-9 ? TotalCoalWt() * 1e4 / v : 0.0;
    }

    /// <summary>标高格 k 上推进到 u 的累计岩量（m³实方）。gap &lt; 0 = 不分层间全算。整桶全计、跨界桶按占比插值。</summary>
    public double VolUpTo(int k, int gap, double u)
    {
        if (SliceWidth <= 1e-9) return 0;
        var ix = GetIdx(k, gap);
        if (ix.B.Length == 0) return 0;
        double lim = u / SliceWidth;
        int nFull = UpperBound(ix.B, lim - 1);
        double vol = nFull > 0 ? ix.C[nFull - 1] : 0;
        if (nFull < ix.B.Length && ix.B[nFull] < lim) vol += ix.V[nFull] * (lim - ix.B[nFull]);
        return vol;
    }

    private sealed class Idx { public long[] B = Array.Empty<long>(); public double[] V = Array.Empty<double>(); public double[] C = Array.Empty<double>(); }
    private Dictionary<long, Idx>? _idx;
    private static readonly Idx EmptyIdx = new();
    private static long IdxKey(int k, int gap) => ((long)(k + 4096) << 12) | (uint)(gap + 1);

    private Idx GetIdx(int k, int gap)
    {
        if (_idx == null)
        {
            var acc = new Dictionary<long, Dictionary<long, double>>();
            void Put(long key, long b, double v)
            {
                if (!acc.TryGetValue(key, out var d)) acc[key] = d = new Dictionary<long, double>();
                d.TryGetValue(b, out double c); d[b] = c + v;
            }
            foreach (var kv in Bins)
            {
                Unpack(kv.Key, out int kk, out int gg, out long b);
                Put(IdxKey(kk, gg), b, kv.Value);
                Put(IdxKey(kk, -1), b, kv.Value);
            }
            var built = new Dictionary<long, Idx>(acc.Count);
            foreach (var kv in acc)
            {
                var bs = kv.Value.Keys.ToArray(); Array.Sort(bs);
                var vs = new double[bs.Length]; var cs = new double[bs.Length];
                double run = 0;
                for (int i = 0; i < bs.Length; i++) { vs[i] = kv.Value[bs[i]]; run += vs[i]; cs[i] = run; }
                built[kv.Key] = new Idx { B = bs, V = vs, C = cs };
            }
            _idx = built;
        }
        return _idx.TryGetValue(IdxKey(k, gap), out var idx) ? idx : EmptyIdx;
    }

    private static int UpperBound(long[] a, double x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] <= x) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>一趟遍历把「各标高格推进到各自位置」的累计岩量按层间标签分账累进 outByGap（不清零）。</summary>
    public void AccumByGap(IReadOnlyList<int> levels, double[] xByLevel, double[] outByGap, double sign = 1)
    {
        if (SliceWidth <= 1e-9 || levels == null || xByLevel == null || outByGap == null) return;
        var idx = new Dictionary<int, int>(levels.Count);
        for (int i = 0; i < levels.Count; i++) idx[levels[i]] = i;
        foreach (var kv in Bins)
        {
            Unpack(kv.Key, out int k, out int g, out long b);
            if (!idx.TryGetValue(k, out int i)) continue;
            if (g < 0 || g >= outByGap.Length) continue;
            double lim = xByLevel[i] / SliceWidth;
            if (b + 1 <= lim) outByGap[g] += sign * kv.Value;
            else if (b < lim) outByGap[g] += sign * kv.Value * (lim - b);
        }
    }

    public double LevelMaxVol(int k) { var ix = GetIdx(k, -1); return ix.C.Length > 0 ? ix.C[^1] : 0; }

    public double CoalWtUpTo(int seam, double u)
    {
        if (seam < 0 || seam >= CoalVolBins.Length || SliceWidth <= 1e-9) return 0;
        double lim = u / SliceWidth, vol = 0;
        foreach (var kv in CoalVolBins[seam])
        {
            if (kv.Key + 1 <= lim) vol += kv.Value;
            else if (kv.Key < lim) vol += kv.Value * (lim - kv.Key);
        }
        double dens = seam < SeamDensity.Length && SeamDensity[seam] > 0 ? SeamDensity[seam] : 1.35;
        return vol * dens / 1e4;
    }

    /// <summary>自洽校核（守恒 / 有序 / 非有限 / 范围与引用 / 完备性）。</summary>
    public List<string> Validate(double tolPct = 0.5)
    {
        var bad = new List<string>();
        if (!Success) { bad.Add("剖面本身是失败的：" + Error); return bad; }
        static bool Bad(double d) => double.IsNaN(d) || double.IsInfinity(d);
        if (Bad(TotalRockM3)) bad.Add($"总岩量 = {TotalRockM3}");
        if (Bad(BenchHeight)) bad.Add($"台阶高 = {BenchHeight}");
        if (Bad(SliceWidth)) bad.Add($"采样步长 = {SliceWidth}");
        foreach (var kv in Bins) if (Bad(kv.Value)) { bad.Add($"岩量桶里有非有限值（key={kv.Key}）"); break; }
        for (int m = 0; m < SeamDensity.Length; m++) if (Bad(SeamDensity[m])) { bad.Add($"第{m}层容重 = {SeamDensity[m]}"); break; }
        double sum = 0; foreach (var kv in Bins) sum += kv.Value;
        double rel = Math.Abs(TotalRockM3) < 1e-9 && Math.Abs(sum) < 1e-9 ? 0 : Math.Abs(sum - TotalRockM3) / Math.Max(1e-9, Math.Max(Math.Abs(sum), Math.Abs(TotalRockM3))) * 100;
        if (rel > tolPct) bad.Add($"总岩量 {TotalRockM3 / 1e4:0.##}万m³ ≠ 逐桶之和 {sum / 1e4:0.##}万m³ —— 汇总与数据漂了");
        if (Bins.Count > 0 && LevelMin > LevelMax) bad.Add($"标高格区间倒置 [{LevelMin},{LevelMax}]");
        if (Bins.Count > 0 && MinBin > MaxBin) bad.Add($"u 桶区间倒置 [{MinBin},{MaxBin}]");
        int gapCount = GapCode.Count(SeamCount);
        if (SeamNames.Length != SeamCount) bad.Add($"层名 {SeamNames.Length} 个 ≠ 层数 {SeamCount}");
        if (SeamDensity.Length != SeamCount) bad.Add($"层容重 {SeamDensity.Length} 个 ≠ 层数 {SeamCount}");
        if (CoalVolBins.Length != SeamCount) bad.Add($"逐层煤桶 {CoalVolBins.Length} 组 ≠ 层数 {SeamCount}");
        if (GapNames.Length > 0 && GapNames.Length != gapCount) bad.Add($"层间名 {GapNames.Length} 个 ≠ 应有 {gapCount} 个（层数 {SeamCount}）");
        if (BenchHeight <= 0) bad.Add($"台阶高 {BenchHeight} ≤ 0 —— 岩量按台阶高分标高格，给 0 就没有岩剖面");
        if (SliceWidth <= 0) bad.Add($"采样步长 {SliceWidth} ≤ 0");
        for (int m = 0; m < SeamDensity.Length; m++) if (SeamDensity[m] <= 0) { bad.Add($"第{m}层容重 {SeamDensity[m]} ≤ 0"); break; }
        foreach (var kv in Bins)
        {
            if (kv.Value < -1e-9) { bad.Add("岩量桶里有负值"); break; }
            Unpack(kv.Key, out int k, out int g, out _);
            if (g < 0 || g >= gapCount) { bad.Add($"桶的层间标签 {g} 越界（应在 [0,{gapCount})，层数 {SeamCount}）"); break; }
            if (k < LevelMin || k > LevelMax) { bad.Add($"桶的标高格 {k} 落在区间 [{LevelMin},{LevelMax}] 之外"); break; }
        }
        return bad;
    }

    public List<int> Levels()
    {
        var set = new HashSet<int>();
        foreach (var key in Bins.Keys) { Unpack(key, out int k, out _, out _); set.Add(k); }
        var list = new List<int>(set); list.Sort(); return list;
    }

    public double[] VolByGap()
    {
        var arr = new double[GapNames.Length > 0 ? GapNames.Length : GapCode.Count(SeamCount)];
        foreach (var kv in Bins) { Unpack(kv.Key, out _, out int g, out _); if (g >= 0 && g < arr.Length) arr[g] += kv.Value; }
        return arr;
    }

    public string Summary()
    {
        if (!Success) return $"岩量剖面失败：{Error}";
        var byGap = VolByGap();
        var parts = new List<string>();
        for (int g = 0; g < byGap.Length; g++) if (byGap[g] > 1e-6) parts.Add($"{(g < GapNames.Length ? GapNames[g] : $"g{g}")} {byGap[g] / 1e4:0.0}");
        return $"岩量剖面: Δ={SliceWidth:0.###}m 台阶高={BenchHeight:0.#}m 标高格 {LevelMin}~{LevelMax}({LevelMax - LevelMin + 1}级) u桶 {MinBin}~{MaxBin} "
             + $"总岩 {TotalRockM3 / 1e4:0.0}万m³ 岩cell={RockCellCount:N0} 煤(u轴) {TotalCoalWt():0.0}万t"
             + (parts.Count > 0 ? $" | 按层间(万m³): {string.Join(" · ", parts)}" : "")
             + (UnclassifiedCells > 0 ? $" | ⚠未分类 {UnclassifiedCells:N0} 单元(顶底板没盖到)" : "")
             + (CrossedColumns > 0 ? $" | ⚠层序穿插 {CrossedColumns:N0} 列" : "");
    }
}
