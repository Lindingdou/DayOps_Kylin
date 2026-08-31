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
    double? SampleThickness = null, double? ApparentDensity = null);

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
