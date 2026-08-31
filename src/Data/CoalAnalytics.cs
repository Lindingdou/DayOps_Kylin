using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  煤质深度分析（忠实移植原 GeoDataBase CoalQualityAnalytics 的商品煤符合性核）。
//  逐化验段判定 Ad≤ / St≤ / Q≥ / Vdaf∈区间 → 达标率 + 超标段清单(带坐标) + 按煤层。
//  数据不足(启用限值却缺该项)显式跳过, 不臆造(不算达标也不算超标)。
// ─────────────────────────────────────────────────────────────────────────────

public enum CalorificKind { Qgr, Qnet }

/// <summary>煤质化验段（3D 视图行）：raw=原煤, clean=浮煤。</summary>
public sealed record CoalSample(
    long Id, string HoleId, string SeamCode, double X, double Y, double? Z,
    double? AdRaw, double? AdClean, double? StdRaw, double? StdClean,
    double? QgrD, double? QnetAd, double? VdafRaw, double? VdafClean,
    double? SampleThickness = null, double? ApparentDensity = null,
    double? CleanCoalYield = null, double? CakingG = null, double? PlasticYMm = null, string? CoalType = null);

public sealed record ComplianceLimits(
    bool UseClean,
    bool AshOn, double AshMax,
    bool SulfurOn, double SulfurMax,
    bool CalorificOn, double CalorificMin, CalorificKind Calorific,
    bool VdafOn, double VdafMin, double VdafMax);

public sealed record SampleEval(
    long Id, string HoleId, string SeamCode, double X, double Y, double? Z,
    double? Ad, double? St, double? Cal, double? Vdaf,
    bool Evaluated, bool Pass, string Fails);

public sealed record SeamCompliance(string SeamCode, int Evaluated, int Pass, double PassPct);

public sealed record ComplianceResult(
    IReadOnlyList<SampleEval> Samples,
    IReadOnlyList<SeamCompliance> BySeam,
    int Evaluated, int Pass, double PassPct, int Insufficient);

public static class CoalAnalytics
{
    private static double? Cal(CoalSample r, CalorificKind k) => k == CalorificKind.Qgr ? r.QgrD : r.QnetAd;

    /// <summary>逐化验段判定商品煤限值符合性。超标段带坐标可上图定位。</summary>
    public static ComplianceResult Evaluate(IReadOnlyList<CoalSample> rows, ComplianceLimits lim)
    {
        var evals = new List<SampleEval>(rows.Count);
        int insufficient = 0;

        foreach (var r in rows)
        {
            double? ad = lim.UseClean ? r.AdClean : r.AdRaw;
            double? st = lim.UseClean ? r.StdClean : r.StdRaw;
            double? cal = Cal(r, lim.Calorific);
            double? vd = lim.UseClean ? r.VdafClean : r.VdafRaw;

            // 只要有一项启用限值却缺该项数据 → 不足以判定，跳过（不算达标也不算超标）
            bool enough = true;
            if (lim.AshOn && ad is null) enough = false;
            if (lim.SulfurOn && st is null) enough = false;
            if (lim.CalorificOn && cal is null) enough = false;
            if (lim.VdafOn && vd is null) enough = false;

            var fails = new List<string>();
            bool pass = true;
            if (enough)
            {
                if (lim.AshOn && ad!.Value > lim.AshMax) { pass = false; fails.Add($"Ad {ad:F1}>{lim.AshMax:F1}"); }
                if (lim.SulfurOn && st!.Value > lim.SulfurMax) { pass = false; fails.Add($"St {st:F2}>{lim.SulfurMax:F2}"); }
                if (lim.CalorificOn && cal!.Value < lim.CalorificMin) { pass = false; fails.Add($"Q {cal:F1}<{lim.CalorificMin:F1}"); }
                if (lim.VdafOn && (vd!.Value < lim.VdafMin || vd.Value > lim.VdafMax)) { pass = false; fails.Add($"Vdaf {vd:F1}∉[{lim.VdafMin:F0},{lim.VdafMax:F0}]"); }
            }
            else insufficient++;

            evals.Add(new SampleEval(r.Id, r.HoleId, r.SeamCode, r.X, r.Y, r.Z,
                ad, st, cal, vd, enough, enough && pass, string.Join(" · ", fails)));
        }

        var bySeam = evals.Where(e => e.Evaluated)
            .GroupBy(e => e.SeamCode).OrderBy(g => g.Key)
            .Select(g =>
            {
                int n = g.Count(), p = g.Count(e => e.Pass);
                return new SeamCompliance(g.Key, n, p, n > 0 ? p * 100.0 / n : 0);
            }).ToList();

        int evaluated = evals.Count(e => e.Evaluated);
        int passCount = evals.Count(e => e.Pass);
        return new ComplianceResult(evals, bySeam, evaluated, passCount,
            evaluated > 0 ? passCount * 100.0 / evaluated : 0, insufficient);
    }

    // ── 指标选择器 + 质量代理 ──
    /// <summary>取指标值：ad/std/vdaf 支持 raw|clean; qgr/qnet 无洗选分。</summary>
    public static double? Value(CoalSample s, string indicator, bool useClean) => indicator switch
    {
        "ad" => useClean ? s.AdClean : s.AdRaw,
        "std" => useClean ? s.StdClean : s.StdRaw,
        "vdaf" => useClean ? s.VdafClean : s.VdafRaw,
        "qgr" => s.QgrD,
        "qnet" => s.QnetAd,
        _ => null,
    };

    /// <summary>质量代理 = 采样厚度 × 视密度（缺密度退化为厚度，等密度假设）。</summary>
    private static double MassProxy(CoalSample s, bool useDens)
    {
        double th = s.SampleThickness ?? 0;
        if (th <= 0) return 0;
        double d = useDens ? (s.ApparentDensity is > 0 ? s.ApparentDensity!.Value : 1.35) : 1.0;
        return th * d;
    }

    // ═══════════════ 品位(煤质)-储量曲线 ═══════════════
    public sealed record GradeTonnagePoint(double Cutoff, double CumMass, double CumMassPct, double CumMeanGrade);
    public sealed record GradeTonnageResult(string Indicator, bool BelowCutoff, bool DensityUsed, double TotalMass, int N, IReadOnlyList<GradeTonnagePoint> Curve);

    /// <summary>品位-储量曲线。质量代理=厚度×密度。灰/硫/挥发：累计"≤限值"(低者优)；发热量：累计"≥限值"。</summary>
    public static GradeTonnageResult GradeTonnage(IReadOnlyList<CoalSample> all, string indicator, bool useClean, int steps = 40)
    {
        int densPresent = all.Count(s => s.ApparentDensity is > 0);
        bool useDens = all.Count > 0 && densPresent >= all.Count * 0.4;
        var items = all.Select(s => (v: Value(s, indicator, useClean), m: MassProxy(s, useDens)))
                       .Where(t => t.v.HasValue && t.m > 0).Select(t => (val: t.v!.Value, mass: t.m))
                       .OrderBy(t => t.val).ToList();
        bool below = indicator is "ad" or "std" or "vdaf";   // 低者优 → 累计"≤"
        double total = items.Sum(t => t.mass);
        var curve = new List<GradeTonnagePoint>();
        if (items.Count > 0)
        {
            double min = items.First().val, max = items.Last().val;
            if (max - min < 1e-9) max = min + 1;
            for (int i = 0; i <= steps; i++)
            {
                double cut = min + (max - min) * i / steps;
                var sel = below ? items.Where(t => t.val <= cut) : items.Where(t => t.val >= cut);
                double m = sel.Sum(t => t.mass);
                double wg = m > 0 ? sel.Sum(t => t.val * t.mass) / m : 0;
                curve.Add(new GradeTonnagePoint(cut, m, total > 0 ? m / total * 100 : 0, wg));
            }
        }
        return new GradeTonnageResult(indicator, below, useDens, total, items.Count, curve);
    }

    // ═══════════════ 分标高煤质 ═══════════════
    public sealed record ElevationBand(double ZLow, double ZHigh, int N, double WeightedMean, double Min, double Max, double MassWeight);

    /// <summary>按开采标高带做厚度(×密度)加权均值。band=标高带高(m)。</summary>
    public static IReadOnlyList<ElevationBand> ByElevation(IReadOnlyList<CoalSample> all, string indicator, bool useClean, double band, string? seam = null)
    {
        if (band < 1) band = 12;
        int densPresent = all.Count(s => s.ApparentDensity is > 0);
        bool useDens = all.Count > 0 && densPresent >= all.Count * 0.4;
        var pts = all.Where(s => seam is null or "全部" || s.SeamCode == seam)
                     .Select(s => (v: Value(s, indicator, useClean), z: s.Z, w: MassProxy(s, useDens)))
                     .Where(t => t.v.HasValue && t.z.HasValue && t.w > 0)
                     .Select(t => (val: t.v!.Value, z: t.z!.Value, w: t.w)).ToList();
        if (pts.Count == 0) return new List<ElevationBand>();
        double zmin = pts.Min(p => p.z), zmax = pts.Max(p => p.z);
        var bands = new List<ElevationBand>();
        int nb = System.Math.Max(1, (int)System.Math.Ceiling((zmax - zmin) / band));
        for (int i = 0; i < nb; i++)
        {
            double lo = zmin + i * band, hi = lo + band;
            var sel = pts.Where(p => p.z >= lo && (p.z < hi || (i == nb - 1 && p.z <= hi + 1e-6))).ToList();
            if (sel.Count == 0) continue;
            double wsum = sel.Sum(p => p.w);
            double wmean = wsum > 0 ? sel.Sum(p => p.val * p.w) / wsum : sel.Average(p => p.val);
            bands.Add(new ElevationBand(lo, hi, sel.Count, wmean, sel.Min(p => p.val), sel.Max(p => p.val), wsum));
        }
        return bands;
    }

    // ═══════════════ 离群质检（Tukey IQR 1.5×IQR 栅栏）═══════════════
    public sealed record OutlierRow(long Id, string HoleId, string SeamCode, double Value, double? Z, string Kind, double Severity);
    public sealed record OutlierResult(string Indicator, int N, double Q1, double Q3, double Lower, double Upper, double Median, IReadOnlyList<OutlierRow> Outliers);

    /// <summary>Tukey IQR 离群检测（1.5×IQR 栅栏）。返回超出栅栏的化验段 + 严重度(超出几个 IQR)，降序。</summary>
    public static OutlierResult DetectOutliers(IReadOnlyList<CoalSample> all, string indicator, bool useClean, string? seam = null)
    {
        var rows = all.Where(r => seam is null or "全部" || r.SeamCode == seam)
                      .Select(r => (r, v: Value(r, indicator, useClean)))
                      .Where(t => t.v.HasValue).Select(t => (t.r, val: t.v!.Value)).ToList();
        if (rows.Count < 5)
            return new OutlierResult(indicator, rows.Count, 0, 0, 0, 0, 0, new List<OutlierRow>());
        var vals = rows.Select(t => t.val).OrderBy(v => v).ToList();
        double q1 = Percentile(vals, 25), q3 = Percentile(vals, 75), med = Percentile(vals, 50);
        double iqr = q3 - q1;
        double lo = q1 - 1.5 * iqr, hi = q3 + 1.5 * iqr;
        var outliers = new List<OutlierRow>();
        foreach (var (r, val) in rows)
        {
            if (val >= lo && val <= hi) continue;
            double sev = iqr > 1e-9 ? (val < lo ? (lo - val) : (val - hi)) / iqr : 0;
            outliers.Add(new OutlierRow(r.Id, r.HoleId, r.SeamCode, val, r.Z, val < lo ? "偏低" : "偏高", sev));
        }
        outliers.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return new OutlierResult(indicator, rows.Count, q1, q3, lo, hi, med, outliers);
    }

    // ═══════════════ 洗选提质 ═══════════════
    public sealed record WashingRow(
        string SeamCode,
        int PairedAsh, double? AdRaw, double? AdClean, double? DeAshPct,
        int PairedSulfur, double? StRaw, double? StClean, double? DeSulfurPct,
        int PairedVdaf, double? VdafRaw, double? VdafClean, double? VdafShift,
        int YieldN, double? YieldMean);

    /// <summary>按煤层汇总洗选提质（成对原煤↔浮煤 → 降灰率/脱硫率/挥发变化/回收率）。withOverall 追加"全矿"。</summary>
    public static IReadOnlyList<WashingRow> WashingBySeam(IReadOnlyList<CoalSample> all, bool withOverall = true)
    {
        var rows = new List<WashingRow>();
        foreach (var g in all.GroupBy(s => s.SeamCode).OrderBy(g => g.Key))
            rows.Add(BuildWashing(g.Key, g.ToList()));
        if (withOverall && rows.Count > 1)
            rows.Add(BuildWashing("全矿", all));
        return rows;
    }

    private static WashingRow BuildWashing(string seam, IReadOnlyList<CoalSample> ss)
    {
        var ash = ss.Where(s => s.AdRaw is > 0 && s.AdClean.HasValue).ToList();
        double? adRaw = ash.Count > 0 ? ash.Average(s => s.AdRaw!.Value) : null;
        double? adClean = ash.Count > 0 ? ash.Average(s => s.AdClean!.Value) : null;
        double? deAsh = adRaw is > 0 && adClean.HasValue ? (adRaw.Value - adClean.Value) / adRaw.Value * 100 : null;

        var sul = ss.Where(s => s.StdRaw is > 0 && s.StdClean.HasValue).ToList();
        double? stRaw = sul.Count > 0 ? sul.Average(s => s.StdRaw!.Value) : null;
        double? stClean = sul.Count > 0 ? sul.Average(s => s.StdClean!.Value) : null;
        double? deSul = stRaw is > 0 && stClean.HasValue ? (stRaw.Value - stClean.Value) / stRaw.Value * 100 : null;

        var vd = ss.Where(s => s.VdafRaw.HasValue && s.VdafClean.HasValue).ToList();
        double? vRaw = vd.Count > 0 ? vd.Average(s => s.VdafRaw!.Value) : null;
        double? vClean = vd.Count > 0 ? vd.Average(s => s.VdafClean!.Value) : null;
        double? vShift = vRaw.HasValue && vClean.HasValue ? vClean.Value - vRaw.Value : null;

        var yld = ss.Where(s => s.CleanCoalYield.HasValue).Select(s => s.CleanCoalYield!.Value).ToList();
        double? yMean = yld.Count > 0 ? yld.Average() : null;

        return new WashingRow(seam, ash.Count, adRaw, adClean, deAsh, sul.Count, stRaw, stClean, deSul,
            vd.Count, vRaw, vClean, vShift, yld.Count, yMean);
    }

    // ═══════════════ 用途适宜性（动力煤 + 炼焦/配焦）═══════════════
    public sealed record UtilizationRow(
        string SeamCode, int N, double? Ad, double? St, double? Cal, double? Vdaf, double? G, double? PlasticY,
        string SteamGrade, string SteamNote, string CokingType, string CokingNote);

    /// <summary>逐煤层用途适宜性评价。cal=发热量口径(Qgr/Qnet)。</summary>
    public static IReadOnlyList<UtilizationRow> UtilizationBySeam(IReadOnlyList<CoalSample> all, CalorificKind cal = CalorificKind.Qgr)
    {
        double? Avg(IEnumerable<double?> xs) { var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count > 0 ? v.Average() : null; }
        var rows = new List<UtilizationRow>();
        foreach (var g in all.GroupBy(s => s.SeamCode).OrderBy(g => g.Key))
        {
            var ss = g.ToList();
            double? ad = Avg(ss.Select(s => s.AdRaw));
            double? st = Avg(ss.Select(s => s.StdRaw));
            double? cv = Avg(ss.Select(s => Cal(s, cal)));
            double? vd = Avg(ss.Select(s => s.VdafRaw));
            double? gi = Avg(ss.Select(s => s.CakingG));
            double? py = Avg(ss.Select(s => s.PlasticYMm));
            string? storedType = ss.Select(s => s.CoalType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var (sg, sn) = SteamVerdict(ad, st, cv, cal);
            var (ct, cn) = CokingVerdict(vd, gi, py, storedType);
            rows.Add(new UtilizationRow(g.Key, ss.Count, ad, st, cv, vd, gi, py, sg, sn, ct, cn));
        }
        return rows;
    }

    /// <summary>动力煤评价：灰/硫/发热量分级综合 → 优/良/中/差 + 建议。</summary>
    public static (string grade, string note) SteamVerdict(double? ad, double? st, double? cal, CalorificKind kind)
    {
        if (ad is null && st is null && cal is null) return ("—", "数据不足");
        var notes = new List<string>();
        int score = 0, items = 0;
        if (ad is { } a) { items++; if (a <= 16) { score += 2; notes.Add("低灰"); } else if (a <= 29) { score += 1; notes.Add("中灰"); } else notes.Add("高灰(需洗)"); }
        if (st is { } s) { items++; if (s <= 1.0) { score += 2; notes.Add("低硫"); } else if (s <= 2.0) { score += 1; notes.Add("中硫"); } else notes.Add("高硫(需配洗降硫)"); }
        if (cal is { } c)
        {
            items++;
            double hi = kind == CalorificKind.Qgr ? 26 : 24, mid = kind == CalorificKind.Qgr ? 21 : 19;
            if (c >= hi) { score += 2; notes.Add("高发热"); } else if (c >= mid) { score += 1; notes.Add("中发热"); } else notes.Add("低发热");
        }
        if (items == 0) return ("—", "数据不足");
        double ratio = score / (2.0 * items);
        string grade = ratio >= 0.83 ? "优" : ratio >= 0.58 ? "良" : ratio >= 0.33 ? "中" : "差";
        return (grade, string.Join("·", notes));
    }

    /// <summary>炼焦/配焦评价：粘结指数 G 判炼焦价值；煤类名用样本存的 GB/T5751 coal_type(缺则按 G/挥发粗判)。</summary>
    public static (string type, string note) CokingVerdict(double? vdaf, double? g, double? plasticY, string? storedType)
    {
        string? type = string.IsNullOrWhiteSpace(storedType) ? null : storedType;
        if (g is null && vdaf is null) return (type ?? "—", "数据不足");
        if (g is { } gi)
        {
            if (gi < 5) return (type ?? "不粘结", "不粘结/微粘,宜动力用");
            if (gi < 35) return (type ?? "弱粘结", "弱粘结,配焦比例受限");
            if (gi < 65) return (type ?? "中粘结", "中等粘结,可配焦");
            return (type ?? "强粘结", "强粘结,炼焦价值高");
        }
        return (type ?? "—", "缺 G 值,按挥发分粗判");
    }

    /// <summary>线性插值分位数（p∈[0,100]）；vals 须已升序。</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Count - 1);
        int lo = (int)System.Math.Floor(rank), hi = (int)System.Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (rank - lo) * (sorted[hi] - sorted[lo]);
    }
}
