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

    /// <summary>符合性结果 → CSV（表头 + 逐化验段：孔号/煤层/坐标/各指标/判定/超标原因）。供导出定位处置。</summary>
    public static string ComplianceToCsv(ComplianceResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("hole_id,seam,x,y,z,ad,st,cal,vdaf,evaluated,pass,fails\n");
        static string F(double? v) => v.HasValue ? v.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var e in r.Samples)
            sb.Append($"{Q(e.HoleId)},{Q(e.SeamCode)},{F(e.X)},{F(e.Y)},{F(e.Z)},{F(e.Ad)},{F(e.St)},{F(e.Cal)},{F(e.Vdaf)},{(e.Evaluated ? 1 : 0)},{(e.Pass ? 1 : 0)},{Q(e.Fails)}\n");
        return sb.ToString();
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

    /// <summary>离群结果 → CSV（表头 + 逐离群段：孔号/煤层/值/标高/方向/严重度IQR）。按严重度降序。</summary>
    public static string OutliersToCsv(OutlierResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"# indicator={r.Indicator} N={r.N} Q1={r.Q1:0.###} Q3={r.Q3:0.###} lower={r.Lower:0.###} upper={r.Upper:0.###}\n");
        sb.Append("hole_id,seam,value,z,kind,severity_iqr\n");
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var o in r.Outliers)
            sb.Append($"{Q(o.HoleId)},{Q(o.SeamCode)},{o.Value.ToString("0.###", inv)},{(o.Z.HasValue ? o.Z.Value.ToString("0.##", inv) : "")},{Q(o.Kind)},{o.Severity.ToString("0.##", inv)}\n");
        return sb.ToString();
    }

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    /// <summary>品位-储量曲线 → CSV（限值/累计质量/累计占比%/累计均值）。</summary>
    public static string GradeTonnageToCsv(GradeTonnageResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"# indicator={r.Indicator} below_cutoff={r.BelowCutoff} density_used={r.DensityUsed} total_mass={r.TotalMass.ToString("0.###", Inv)} n={r.N}\n");
        sb.Append("cutoff,cum_mass,cum_mass_pct,cum_mean_grade\n");
        foreach (var p in r.Curve)
            sb.Append($"{p.Cutoff.ToString("0.####", Inv)},{p.CumMass.ToString("0.###", Inv)},{p.CumMassPct.ToString("0.##", Inv)},{p.CumMeanGrade.ToString("0.####", Inv)}\n");
        return sb.ToString();
    }

    /// <summary>分标高煤质 → CSV（标高带/段数/加权均值/极值/质量权）。</summary>
    public static string ElevationToCsv(IReadOnlyList<ElevationBand> bands)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("z_low,z_high,n,weighted_mean,min,max,mass_weight\n");
        foreach (var b in bands)
            sb.Append($"{b.ZLow.ToString("0.##", Inv)},{b.ZHigh.ToString("0.##", Inv)},{b.N},{b.WeightedMean.ToString("0.####", Inv)},{b.Min.ToString("0.####", Inv)},{b.Max.ToString("0.####", Inv)},{b.MassWeight.ToString("0.###", Inv)}\n");
        return sb.ToString();
    }

    /// <summary>洗选提质 → CSV（煤层/成对数/原煤·浮煤/降灰率·脱硫率·挥发变化/回收率）。</summary>
    public static string WashingToCsv(IReadOnlyList<WashingRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("seam,paired_ash,ad_raw,ad_clean,deash_pct,paired_sulfur,st_raw,st_clean,desulfur_pct,paired_vdaf,vdaf_raw,vdaf_clean,vdaf_shift,yield_n,yield_mean\n");
        static string F(double? v) => v.HasValue ? v.Value.ToString("0.###", Inv) : "";
        foreach (var w in rows)
            sb.Append($"{Csv(w.SeamCode)},{w.PairedAsh},{F(w.AdRaw)},{F(w.AdClean)},{F(w.DeAshPct)},{w.PairedSulfur},{F(w.StRaw)},{F(w.StClean)},{F(w.DeSulfurPct)},{w.PairedVdaf},{F(w.VdafRaw)},{F(w.VdafClean)},{F(w.VdafShift)},{w.YieldN},{F(w.YieldMean)}\n");
        return sb.ToString();
    }

    /// <summary>用途适宜性 → CSV（煤层/段数/各指标均值/动力煤评级·说明/炼焦煤类·说明）。</summary>
    public static string UtilizationToCsv(IReadOnlyList<UtilizationRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("seam,n,ad,st,cal,vdaf,g,plastic_y,steam_grade,steam_note,coking_type,coking_note\n");
        static string F(double? v) => v.HasValue ? v.Value.ToString("0.###", Inv) : "";
        foreach (var u in rows)
            sb.Append($"{Csv(u.SeamCode)},{u.N},{F(u.Ad)},{F(u.St)},{F(u.Cal)},{F(u.Vdaf)},{F(u.G)},{F(u.PlasticY)},{Csv(u.SteamGrade)},{Csv(u.SteamNote)},{Csv(u.CokingType)},{Csv(u.CokingNote)}\n");
        return sb.ToString();
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

    // ═══════════════════════ 灰分-发热量回归 + 综合结论（忠实 CoalQualityAnalytics 纯 C# 引擎）═══════════════════════

    public sealed record RegressionResult(
        string XName, string YName, int N, double Slope, double Intercept, double R2,
        double XMin, double XMax,
        IReadOnlyList<(double Ad, double Cal)> Points,
        IReadOnlyList<(long Id, string HoleId, string SeamCode, double Ad, double Cal, double ZScore)> Suspects);

    /// <summary>灰分(Ad)→发热量 一元线性回归(OLS) + r² + 残差 z-score 离群(|z|≥2.5)。样本&lt;5 返回空回归。忠实原 AshCalorificRegression。</summary>
    public static RegressionResult AshCalorificRegression(IReadOnlyList<CoalSample> all, CalorificKind cal = CalorificKind.Qgr)
    {
        string yn = cal == CalorificKind.Qgr ? "Qgr,d" : "Qnet,ad";
        var data = (all ?? new List<CoalSample>())
            .Select(r => (r, x: r.AdRaw, y: Cal(r, cal)))
            .Where(t => t.x.HasValue && t.y.HasValue)
            .Select(t => (t.r, x: t.x!.Value, y: t.y!.Value))
            .ToList();
        if (data.Count < 5)
            return new RegressionResult("Ad", yn, data.Count, 0, 0, 0, 0, 0,
                new List<(double, double)>(), new List<(long, string, string, double, double, double)>());

        int n = data.Count;
        double mx = data.Average(t => t.x), my = data.Average(t => t.y);
        double sxx = data.Sum(t => (t.x - mx) * (t.x - mx));
        double sxy = data.Sum(t => (t.x - mx) * (t.y - my));
        double syy = data.Sum(t => (t.y - my) * (t.y - my));
        double b = sxx > 1e-9 ? sxy / sxx : 0;
        double a = my - b * mx;
        double r2 = sxx > 1e-9 && syy > 1e-9 ? (sxy * sxy) / (sxx * syy) : 0;

        var resid = data.Select(t => t.y - (a + b * t.x)).ToList();
        double rmean = resid.Average();
        double rsd = System.Math.Sqrt(resid.Sum(e => (e - rmean) * (e - rmean)) / System.Math.Max(1, n - 1));
        if (rsd < 1e-9) rsd = 1;

        var suspects = new List<(long, string, string, double, double, double)>();
        for (int i = 0; i < n; i++)
        {
            double z = (resid[i] - rmean) / rsd;
            if (System.Math.Abs(z) >= 2.5)
                suspects.Add((data[i].r.Id, data[i].r.HoleId, data[i].r.SeamCode, data[i].x, data[i].y, z));
        }
        suspects.Sort((p, q) => System.Math.Abs(q.Item6).CompareTo(System.Math.Abs(p.Item6)));

        return new RegressionResult("Ad", yn, n, b, a, r2,
            data.Min(t => t.x), data.Max(t => t.x),
            data.Select(t => (t.x, t.y)).ToList(), suspects);
    }

    public enum Verdict { Good, Neutral, Warn }
    public sealed record Conclusion(string Category, string Text, Verdict Level);

    /// <summary>综合分析结论：把描述统计综合成 表征/煤层优劣/变异/相关/洗选/用途/数据质量 的可读结论。忠实原 OverallConclusions(无参考字典→AshWord/SulfurWord 兜底)。</summary>
    public static IReadOnlyList<Conclusion> OverallConclusions(IReadOnlyList<CoalSample> all, CalorificKind cal = CalorificKind.Qgr)
    {
        var outl = new List<Conclusion>();
        if (all == null || all.Count == 0) { outl.Add(new("数据", "库内无煤质数据。", Verdict.Warn)); return outl; }

        double? ad = AvgN(all.Select(s => s.AdRaw));
        double? st = AvgN(all.Select(s => s.StdRaw));
        double? vd = AvgN(all.Select(s => s.VdafRaw));
        double? q = AvgN(all.Select(s => Cal(s, cal)));
        double? gi = AvgN(all.Select(s => s.CakingG));
        string calTag = cal == CalorificKind.Qgr ? "Qgr,d" : "Qnet,ad";

        // ① 煤质表征
        string adL = ad.HasValue ? AshWord(ad.Value) : "";
        string stL = st.HasValue ? SulfurWord(st.Value) : "";
        string rank = !vd.HasValue ? "" : vd.Value >= 37 ? "、高挥发分(煤化程度低,近气/长焰煤)" : vd.Value >= 28 ? "、中高挥发分" : vd.Value >= 20 ? "、中挥发分" : "、低挥发分(煤化程度高)";
        var typeTop = all.Where(s => !string.IsNullOrEmpty(s.CoalType)).GroupBy(s => s.CoalType!).OrderByDescending(g => g.Count()).FirstOrDefault();
        int typedN = all.Count(s => s.CoalType != null);
        outl.Add(new("煤质表征",
            $"本矿煤总体属【{adL}{stL}】煤{rank}；均值 Ad={Fmt(ad)}% · St,d={Fmt(st, 2)}% · {calTag}={Fmt(q)}MJ/kg · Vdaf={Fmt(vd)}%" +
            (gi.HasValue ? $" · G={Fmt(gi)}" : "") +
            (typeTop != null ? $"；煤类以 {typeTop.Key} 为主({typeTop.Count()}/{typedN}段)。" : "；煤类多数未标注。"),
            Verdict.Neutral));

        // ② 煤层间优劣
        var seams = all.GroupBy(s => s.SeamCode)
            .Select(g => (Seam: g.Key, Ad: AvgN(g.Select(s => s.AdRaw)), St: AvgN(g.Select(s => s.StdRaw))))
            .Where(t => t.Ad.HasValue).ToList();
        if (seams.Count >= 2)
        {
            var hiAsh = seams.OrderByDescending(t => t.Ad).First();
            var loAsh = seams.OrderBy(t => t.Ad).First();
            var hiS = seams.Where(t => t.St.HasValue).OrderByDescending(t => t.St).FirstOrDefault();
            string s2 = $"{loAsh.Seam}煤灰分最低({Fmt(loAsh.Ad)}%,质量最优)、{hiAsh.Seam}煤最高({Fmt(hiAsh.Ad)}%)";
            if (hiS.Seam != null) s2 += $"；{hiS.Seam}煤硫分最高({Fmt(hiS.St, 2)}%)";
            outl.Add(new("煤层对比", s2 + "。", Verdict.Neutral));
        }

        // ③ 变异性
        var adVals = all.Select(s => s.AdRaw).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (adVals.Count > 2 && ad is > 0)
        {
            double sd = System.Math.Sqrt(adVals.Sum(v => (v - ad.Value) * (v - ad.Value)) / (adVals.Count - 1));
            double cv = sd / ad.Value * 100;
            string uni = cv < 15 ? "均匀,煤质稳定" : cv < 30 ? "较均匀" : "波动较大,须注意配采均衡";
            outl.Add(new("均匀性", $"灰分变异系数 CV={cv:F0}%,{uni}(σ={sd:F1}%)。", cv < 30 ? Verdict.Good : Verdict.Warn));
        }

        // ④ 相关性
        var corr = StrongestCorrelation(all);
        if (corr != null) outl.Add(new("相关性", corr, Verdict.Neutral));

        // ⑤ 洗选可选性
        var washRows = WashingBySeam(all, withOverall: true);
        var wash = washRows.FirstOrDefault(w => w.SeamCode == "全矿") ?? washRows.FirstOrDefault();
        if (wash != null && wash.DeAshPct.HasValue)
        {
            double de = wash.DeAshPct.Value;
            string sel = de >= 60 ? "易选" : de >= 40 ? "中等可选" : de >= 20 ? "较难选" : "难选";
            string txt = $"原煤经洗选平均降灰 {de:F0}%(浮煤灰 {Fmt(wash.AdClean)}%)";
            if (wash.DeSulfurPct.HasValue) txt += $"、脱硫 {wash.DeSulfurPct:F0}%";
            if (wash.YieldMean.HasValue) txt += $"、浮煤回收 {wash.YieldMean:F0}%";
            txt += $",可选性属【{sel}】。";
            outl.Add(new("洗选提质", txt, de >= 40 ? Verdict.Good : Verdict.Warn));
        }

        // ⑥ 用途建议
        outl.Add(new("用途建议", UtilizationVerdictText(ad, st, q, vd, gi), Verdict.Neutral));

        // ⑦ 数据质量
        int n = all.Count;
        int adN = all.Count(s => s.AdRaw.HasValue), stN = all.Count(s => s.StdRaw.HasValue), qN = all.Count(s => s.QgrD.HasValue);
        outl.Add(new("数据质量",
            $"共 {n} 段化验：灰分 {adN * 100 / n}%、全硫 {stN * 100 / n}%、发热量(Qgr) {qN * 100 / n}% 覆盖" +
            (qN < n * 0.5 ? "；发热量样本偏少,热值结论供参考,以灰/硫为主。" : "。"),
            qN < n * 0.3 ? Verdict.Warn : Verdict.Good));

        return outl;
    }

    /// <summary>综合结论 → CSV(类别,结论,评级)。</summary>
    public static string ConclusionsToCsv(IReadOnlyList<Conclusion> cs)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("类别,结论,评级\n");
        foreach (var c in cs)
            sb.Append($"{Csv(c.Category)},{Csv(c.Text)},{(c.Level == Verdict.Good ? "优" : c.Level == Verdict.Warn ? "注意" : "中性")}\n");
        return sb.ToString();
    }

    /// <summary>离群行 → 样点坐标(按 Id 关联 samples), 供上图定位。忠实原「超标段带坐标可上图定位」。</summary>
    public static List<(double x, double y, string kind, double severity)> OutlierCoords(OutlierResult r, IReadOnlyList<CoalSample> samples)
    {
        var res = new List<(double x, double y, string kind, double severity)>();
        if (r == null || samples == null) return res;
        var byId = new Dictionary<long, CoalSample>();
        foreach (var s in samples) byId[s.Id] = s;   // 同 Id 取后者(样点 Id 唯一)
        foreach (var o in r.Outliers)
            if (byId.TryGetValue(o.Id, out var s)) res.Add((s.X, s.Y, o.Kind, o.Severity));
        return res;
    }

    private static double? AvgN(IEnumerable<double?> xs)
    { var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count > 0 ? v.Average() : null; }
    private static string Fmt(double? v, int dec = 1) => v.HasValue ? v.Value.ToString("F" + dec, Inv) : "—";
    private static string AshWord(double a) => a <= 10 ? "特低灰" : a <= 16 ? "低灰" : a <= 29 ? "中灰" : a <= 40 ? "富灰" : "高灰";
    private static string SulfurWord(double s) => s <= 0.5 ? "特低硫" : s <= 1.0 ? "低硫" : s <= 2.0 ? "中硫" : s <= 3.0 ? "中高硫" : "高硫";

    private static string UtilizationVerdictText(double? ad, double? st, double? q, double? vd, double? gi)
    {
        var parts = new List<string>();
        bool steamOk = (q is null or >= 21) && (ad is null or <= 29);
        parts.Add(steamOk ? "适宜作动力煤/民用煤" : "灰分偏高,动力用宜先洗选降灰");
        if (st is > 2.0) parts.Add("硫分偏高,须配洗/掺烧降硫以满足环保限值");
        else if (st is > 1.0) parts.Add("中硫,关注 SO₂ 排放");
        else if (st.HasValue) parts.Add("低硫,环保友好");
        if (gi is > 50 && vd is >= 20 and <= 37) parts.Add("粘结性较好,可部分配焦");
        else if (gi is < 35 and > 0) parts.Add("粘结性弱,不宜单独炼焦");
        return string.Join("；", parts) + "。";
    }

    private static string? StrongestCorrelation(IReadOnlyList<CoalSample> all)
    {
        var pairs = new (string A, string B, System.Func<CoalSample, double?> Ga, System.Func<CoalSample, double?> Gb)[]
        {
            ("灰分", "发热量", s => s.AdRaw, s => s.QgrD),
            ("灰分", "全硫", s => s.AdRaw, s => s.StdRaw),
            ("灰分", "挥发分", s => s.AdRaw, s => s.VdafRaw),
            ("挥发分", "发热量", s => s.VdafRaw, s => s.QgrD),
        };
        string? best = null; double bestAbs = 0.35;   // 只报中等以上相关
        foreach (var p in pairs)
        {
            var xs = new List<double>(); var ys = new List<double>();
            foreach (var s in all) { var a = p.Ga(s); var b = p.Gb(s); if (a.HasValue && b.HasValue) { xs.Add(a.Value); ys.Add(b.Value); } }
            var r = Pearson(xs, ys);
            if (r.HasValue && System.Math.Abs(r.Value) > bestAbs)
            {
                bestAbs = System.Math.Abs(r.Value);
                string dir = r.Value > 0 ? "正" : "负";
                string note = p.A == "灰分" && p.B == "发热量" ? "(灰分越高发热量越低,符合规律)" : "";
                best = $"{p.A}与{p.B}呈{dir}相关(r={r.Value:F2}){note}。";
            }
        }
        return best;
    }

    private static double? Pearson(List<double> xs, List<double> ys)
    {
        int n = xs.Count;
        if (n < 5) return null;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { double dx = xs[i] - mx, dy = ys[i] - my; sxy += dx * dy; sx += dx * dx; sy += dy * dy; }
        double d = System.Math.Sqrt(sx * sy);
        return d < 1e-12 ? null : System.Math.Max(-1, System.Math.Min(1, sxy / d));
    }
}
