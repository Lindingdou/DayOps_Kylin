// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/CoalQualityAnalytics.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;

namespace PitMine3D.Kylin.Data.Services;
using CoalSample = PitMine3D.Kylin.Data.Entities.CoalSample;   // 与外层 Data.CoalAnalytics 的同名 record 消歧（须放在命名空间内才盖得过外层）

/// <summary>
/// 煤质<b>深度分析</b>引擎（纯 C#，无外部依赖）。把化验数据从"描述统计"推进到"生产决策"：
///   · 洗选提质 —— 原煤↔浮煤 降灰率 / 脱硫率 / 挥发分变化 / 浮煤回收率（判要不要洗、洗到哪一档）。
///   · 商品煤符合性 —— 用户自定义 Ad≤ / St≤ / Q≥ / Vdaf 区间 → 达标率 + 超标段清单（带坐标）。
///   · 用途适宜性 —— 动力煤（灰/硫/发热量分级综合）+ 炼焦/配焦（G/Y/Vdaf → 煤类）判定。
///   · 品位-储量曲线 —— 各灰分/硫分限下的累计（视密度×厚度加权）质量分布。
///   · 分标高煤质 —— 按开采标高带做厚度加权均值（哪个台阶高硫高灰，直接接调度）。
///   · 离群质检 —— IQR 离群 + Qgr-Ad 回归残差挑可疑化验。
/// 数据现实（257 段实测）：浮煤配对 92%（洗选可做）、Qnet 仅 6% 但 Qgr 有 69%（发热量默认走 Qgr）、
/// 视密度 61%（储量近似可做）、z_sample 99%（分标高可做）。所有"数据不足"一律显式跳过、不臆造。
/// </summary>
internal sealed class CoalQualityAnalytics
{
    private readonly ICoalQualityService _coal;
    private readonly ICoalReferenceService _ref;

    public CoalQualityAnalytics(ICoalQualityService coal, ICoalReferenceService reference)
    {
        _coal = coal;
        _ref = reference;
    }

    /// <summary>发热量口径：弹筒(Qgr,d 数据密) / 低位(Qnet,ad 数据稀疏)。</summary>
    public enum CalorificKind { Qgr, Qnet }

    private static double? Cal(CoalSample s, CalorificKind k) => k == CalorificKind.Qgr ? s.QgrD : s.QnetAd;
    private static double? Cal(CoalSample3DRow s, CalorificKind k) => k == CalorificKind.Qgr ? s.QgrD : s.QnetAd;
    public static string CalName(CalorificKind k) => k == CalorificKind.Qgr ? "Qgr,d 弹筒发热量" : "Qnet,ad 低位发热量";

    // ═══════════════════════════ 方向1 · 洗选提质 ═══════════════════════════

    /// <summary>逐煤层洗选提质行（原煤↔浮煤对照）。降灰/脱硫/挥发分变化均在"成对样本"子集上算，口径一致。</summary>
    public sealed record WashingRow(
        string SeamCode,
        int PairedAsh, double? AdRaw, double? AdClean, double? DeAshPct,
        int PairedSulfur, double? StRaw, double? StClean, double? DeSulfurPct,
        int PairedVdaf, double? VdafRaw, double? VdafClean, double? VdafShift,
        int YieldN, double? YieldMean);

    /// <summary>按煤层汇总洗选提质；<paramref name="withOverall"/> 追加一行"全矿"。</summary>
    public IReadOnlyList<WashingRow> WashingBySeam(bool withOverall = true)
    {
        var all = _coal.All();
        var rows = new List<WashingRow>();
        foreach (var g in all.GroupBy(s => s.SeamCode).OrderBy(g => g.Key))
            rows.Add(BuildWashing(g.Key, g.ToList()));
        if (withOverall && rows.Count > 1)
            rows.Add(BuildWashing("全矿", all));
        return rows;
    }

    private static WashingRow BuildWashing(string seam, IReadOnlyList<CoalSample> ss)
    {
        // 成对灰分
        var ash = ss.Where(s => s.AdRaw is > 0 && s.AdClean.HasValue).ToList();
        double? adRaw = ash.Count > 0 ? ash.Average(s => s.AdRaw!.Value) : null;
        double? adClean = ash.Count > 0 ? ash.Average(s => s.AdClean!.Value) : null;
        double? deAsh = adRaw is > 0 && adClean.HasValue ? (adRaw.Value - adClean.Value) / adRaw.Value * 100 : null;

        // 成对硫分
        var sul = ss.Where(s => s.StdRaw is > 0 && s.StdClean.HasValue).ToList();
        double? stRaw = sul.Count > 0 ? sul.Average(s => s.StdRaw!.Value) : null;
        double? stClean = sul.Count > 0 ? sul.Average(s => s.StdClean!.Value) : null;
        double? deSul = stRaw is > 0 && stClean.HasValue ? (stRaw.Value - stClean.Value) / stRaw.Value * 100 : null;

        // 成对挥发分（洗选一般略升，因灰分中矿物被脱除）
        var vd = ss.Where(s => s.VdafRaw.HasValue && s.VdafClean.HasValue).ToList();
        double? vRaw = vd.Count > 0 ? vd.Average(s => s.VdafRaw!.Value) : null;
        double? vClean = vd.Count > 0 ? vd.Average(s => s.VdafClean!.Value) : null;
        double? vShift = vRaw.HasValue && vClean.HasValue ? vClean.Value - vRaw.Value : null;

        // 浮煤回收率
        var yld = ss.Where(s => s.CleanCoalYield.HasValue).Select(s => s.CleanCoalYield!.Value).ToList();
        double? yMean = yld.Count > 0 ? yld.Average() : null;

        return new WashingRow(seam,
            ash.Count, adRaw, adClean, deAsh,
            sul.Count, stRaw, stClean, deSul,
            vd.Count, vRaw, vClean, vShift,
            yld.Count, yMean);
    }

    // ═══════════════════════════ 方向1 · 商品煤符合性 ═══════════════════════════

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

    /// <summary>逐化验段判定商品煤限值符合性。用带坐标的 3D 视图行，超标段可上图定位。</summary>
    public ComplianceResult Evaluate(ComplianceLimits lim)
    {
        var rows = _coal.Get3DPoints();
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

            evals.Add(new SampleEval(r.Id, r.HoleId, r.SeamCode, r.X, r.Y, r.ZSample,
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

    // ═══════════════════════════ 方向1 · 用途适宜性 ═══════════════════════════

    public sealed record UtilizationRow(
        string SeamCode, int N,
        double? Ad, double? St, double? Cal, double? Vdaf, double? G, double? PlasticY,
        string SteamGrade, string SteamNote,
        string CokingType, string CokingNote);

    /// <summary>逐煤层用途适宜性评价（动力煤 + 炼焦/配焦）。</summary>
    public IReadOnlyList<UtilizationRow> UtilizationBySeam(CalorificKind cal = CalorificKind.Qgr)
    {
        var rows = new List<UtilizationRow>();
        foreach (var g in _coal.All().GroupBy(s => s.SeamCode).OrderBy(g => g.Key))
        {
            var ss = g.ToList();
            double? ad = Avg(ss.Select(s => s.AdRaw));
            double? st = Avg(ss.Select(s => s.StdRaw));
            double? cv = Avg(ss.Select(s => Cal(s, cal)));
            double? vd = Avg(ss.Select(s => s.VdafRaw));
            double? gi = Avg(ss.Select(s => s.CakingG));
            double? py = Avg(ss.Select(s => s.PlasticYMm));

            var (sg, sn) = SteamVerdict(ad, st, cv, cal);
            var (ct, cn) = CokingVerdict(vd, gi, py);
            rows.Add(new UtilizationRow(g.Key, ss.Count, ad, st, cv, vd, gi, py, sg, sn, ct, cn));
        }
        return rows;
    }

    /// <summary>动力煤评价：灰/硫/发热量分级综合 → 优/良/中/差 + 建议。</summary>
    private (string grade, string note) SteamVerdict(double? ad, double? st, double? cal, CalorificKind kind)
    {
        if (ad is null && st is null && cal is null) return ("—", "数据不足");
        var notes = new List<string>();
        int score = 0, items = 0;

        if (ad is { } a)
        {
            items++;
            if (a <= 16) { score += 2; notes.Add("低灰"); }
            else if (a <= 29) { score += 1; notes.Add("中灰"); }
            else notes.Add("高灰(需洗)");
        }
        if (st is { } s)
        {
            items++;
            if (s <= 1.0) { score += 2; notes.Add("低硫"); }
            else if (s <= 2.0) { score += 1; notes.Add("中硫"); }
            else notes.Add("高硫(需配洗降硫)");
        }
        if (cal is { } c)
        {
            items++;
            // 弹筒 Qgr,d 门槛比低位 Qnet 略高
            double hi = kind == CalorificKind.Qgr ? 26 : 24, mid = kind == CalorificKind.Qgr ? 21 : 19;
            if (c >= hi) { score += 2; notes.Add("高发热"); }
            else if (c >= mid) { score += 1; notes.Add("中发热"); }
            else notes.Add("低发热");
        }
        if (items == 0) return ("—", "数据不足");
        double ratio = score / (2.0 * items);
        string grade = ratio >= 0.83 ? "优" : ratio >= 0.58 ? "良" : ratio >= 0.33 ? "中" : "差";
        return (grade, string.Join("·", notes));
    }

    /// <summary>炼焦/配焦评价：GB/T 5751 反推煤类 + 粘结性判炼焦价值。</summary>
    private (string type, string note) CokingVerdict(double? vdaf, double? g, double? plasticY)
    {
        string? type = _ref.ResolveCoalType(vdaf, g, plasticY);
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

    // ═══════════════════════════ 方向2 · 品位-储量曲线 ═══════════════════════════

    public sealed record GradeTonnagePoint(double Cutoff, double CumMass, double CumMassPct, double CumMeanGrade);

    public sealed record GradeTonnageResult(
        string Indicator, bool BelowCutoff, bool DensityUsed,
        double TotalMass, int N,
        IReadOnlyList<GradeTonnagePoint> Curve,
        IReadOnlyList<(string Level, string Name, double Mass, double Pct, string ColorHex)> ByLevel);

    /// <summary>
    /// 品位(煤质)-储量曲线。质量代理 = 采样厚度 × 视密度（缺密度则退化为厚度，等密度假设）。
    /// 灰分/硫分：累计"≤限值"的质量（越低越好）；发热量：累计"≥限值"。返回累计曲线 + 按 GB 分级的质量占比。
    /// </summary>
    public GradeTonnageResult GradeTonnage(string indicator, bool useClean, int steps = 40)
    {
        var get = Getter(indicator, useClean);
        var all = _coal.All();
        int densPresent = all.Count(s => s.ApparentDensity is > 0);
        bool useDens = densPresent >= all.Count * 0.4;   // 密度覆盖够才用,否则等密度

        var items = all
            .Select(s => (v: get(s), m: MassProxy(s, useDens)))
            .Where(t => t.v.HasValue && t.m > 0)
            .Select(t => (val: t.v!.Value, mass: t.m))
            .OrderBy(t => t.val)
            .ToList();

        bool below = indicator is "ad_raw" or "std_raw" or "vdaf_raw";   // 灰/硫/挥发分：低者优 → 累计"≤"
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

        // 按 GB 分级的质量占比
        var byLevel = new List<(string, string, double, double, string)>();
        var ruleType = RuleFor(indicator);
        if (ruleType != null)
        {
            foreach (var rule in _ref.RulesByType(ruleType))
            {
                double m = items.Where(t => _ref.FindLevel(ruleType, t.val)?.LevelCode == rule.LevelCode).Sum(t => t.mass);
                if (m <= 0) continue;
                byLevel.Add((rule.LevelCode, rule.LevelName, m, total > 0 ? m / total * 100 : 0, rule.ColorHex ?? "#909AA8"));
            }
        }

        return new GradeTonnageResult(indicator, below, useDens, total, items.Count, curve, byLevel);
    }

    private static double MassProxy(CoalSample s, bool useDens)
    {
        double th = s.SampleThickness ?? 0;
        if (th <= 0) return 0;
        double d = useDens ? (s.ApparentDensity is > 0 ? s.ApparentDensity!.Value : 1.35) : 1.0;
        return th * d;
    }

    // ═══════════════════════════ 方向2 · 分标高煤质 ═══════════════════════════

    public sealed record ElevationBand(
        double ZLow, double ZHigh, int N, double WeightedMean, double Min, double Max, double MassWeight);

    /// <summary>按开采标高带做厚度(×密度)加权均值。band=标高带高(m)。</summary>
    public IReadOnlyList<ElevationBand> ByElevation(string indicator, bool useClean, double band, string? seam = null)
    {
        if (band < 1) band = 12;
        var get = Getter(indicator, useClean);
        int densPresent = _coal.All().Count(s => s.ApparentDensity is > 0);
        bool useDens = densPresent >= _coal.All().Count * 0.4;

        var pts = _coal.All()
            .Where(s => seam is null or "全部" || s.SeamCode == seam)
            .Select(s => (v: get(s), z: s.ZSample, w: MassProxy(s, useDens)))
            .Where(t => t.v.HasValue && t.z.HasValue && t.w > 0)
            .Select(t => (val: t.v!.Value, z: t.z!.Value, w: t.w))
            .ToList();
        if (pts.Count == 0) return Array.Empty<ElevationBand>();

        double zmin = pts.Min(p => p.z), zmax = pts.Max(p => p.z);
        var bands = new List<ElevationBand>();
        int nb = Math.Max(1, (int)Math.Ceiling((zmax - zmin) / band));
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

    // ═══════════════════════════ 方向4 · 离群质检 ═══════════════════════════

    public sealed record OutlierRow(long Id, string HoleId, string SeamCode, double Value, double? Z, string Kind, double Severity);

    public sealed record OutlierResult(
        string Indicator, int N, double Q1, double Q3, double Lower, double Upper, double Median,
        IReadOnlyList<OutlierRow> Outliers);

    /// <summary>Tukey IQR 离群检测（1.5×IQR 栅栏）。返回超出栅栏的化验段，带严重度（超出多少个 IQR）。</summary>
    public OutlierResult DetectOutliers(string indicator, bool useClean, string? seam = null)
    {
        var get3 = Getter3D(indicator, useClean);
        var rows = _coal.Get3DPoints()
            .Where(r => seam is null or "全部" || r.SeamCode == seam)
            .Select(r => (r, v: get3(r)))
            .Where(t => t.v.HasValue)
            .Select(t => (t.r, val: t.v!.Value))
            .ToList();
        if (rows.Count < 5)
            return new OutlierResult(indicator, rows.Count, 0, 0, 0, 0, 0, Array.Empty<OutlierRow>());

        var vals = rows.Select(t => t.val).OrderBy(v => v).ToList();
        double q1 = Percentile(vals, 25), q3 = Percentile(vals, 75), med = Percentile(vals, 50);
        double iqr = q3 - q1;
        double lo = q1 - 1.5 * iqr, hi = q3 + 1.5 * iqr;

        var outliers = new List<OutlierRow>();
        foreach (var (r, val) in rows)
        {
            if (val >= lo && val <= hi) continue;
            double sev = iqr > 1e-9 ? (val < lo ? (lo - val) : (val - hi)) / iqr : 0;
            outliers.Add(new OutlierRow(r.Id, r.HoleId, r.SeamCode, val, r.ZSample,
                val < lo ? "偏低" : "偏高", sev));
        }
        outliers.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return new OutlierResult(indicator, rows.Count, q1, q3, lo, hi, med, outliers);
    }

    // ═══════════════════════════ 方向4 · 发热量-灰分回归残差 ═══════════════════════════

    public sealed record RegressionResult(
        string XName, string YName, int N, double Slope, double Intercept, double R2,
        double XMin, double XMax,
        IReadOnlyList<(double X, double Y)> Points,
        IReadOnlyList<(long Id, string HoleId, string SeamCode, double X, double Y, double StdResid)> Suspects);

    /// <summary>
    /// 灰分↑ → 发热量↓ 是硬物理规律；对该回归的大残差段 = 可疑化验（可能录入错误/样品异常）。
    /// 默认 Qgr,d ~ Ad。标准化残差 |z|>2.5 视为可疑。
    /// </summary>
    public RegressionResult AshCalorificRegression(CalorificKind cal = CalorificKind.Qgr)
    {
        var data = _coal.Get3DPoints()
            .Select(r => (r, x: (double?)r.AdRaw, y: Cal(r, cal)))
            .Where(t => t.x.HasValue && t.y.HasValue)
            .Select(t => (t.r, x: t.x!.Value, y: t.y!.Value))
            .ToList();

        string yn = cal == CalorificKind.Qgr ? "Qgr,d" : "Qnet,ad";
        if (data.Count < 5)
            return new RegressionResult("Ad", yn, data.Count, 0, 0, 0, 0, 1,
                Array.Empty<(double, double)>(), Array.Empty<(long, string, string, double, double, double)>());

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
        double rsd = Math.Sqrt(resid.Sum(e => (e - rmean) * (e - rmean)) / Math.Max(1, n - 1));
        if (rsd < 1e-9) rsd = 1;

        var suspects = new List<(long, string, string, double, double, double)>();
        for (int i = 0; i < n; i++)
        {
            double z = (resid[i] - rmean) / rsd;
            if (Math.Abs(z) >= 2.5)
                suspects.Add((data[i].r.Id, data[i].r.HoleId, data[i].r.SeamCode, data[i].x, data[i].y, z));
        }
        suspects.Sort((p, q) => Math.Abs(q.Item6).CompareTo(Math.Abs(p.Item6)));

        return new RegressionResult("Ad", yn, n, b, a, r2,
            data.Min(t => t.x), data.Max(t => t.x),
            data.Select(t => (t.x, t.y)).ToList(), suspects);
    }

    // ═══════════════════════════ 分析结论（各窗复用）═══════════════════════════

    public enum Verdict { Good, Neutral, Warn }
    public sealed record Conclusion(string Category, string Text, Verdict Level);

    /// <summary>综合分析结论：把描述统计综合成"表征/煤层优劣/变异/相关/洗选/用途/数据质量"的可读结论 + 建议。</summary>
    public IReadOnlyList<Conclusion> OverallConclusions(CalorificKind cal = CalorificKind.Qgr)
    {
        var all = _coal.All();
        var outl = new List<Conclusion>();
        if (all.Count == 0) { outl.Add(new("数据", "库内无煤质数据。", Verdict.Warn)); return outl; }

        double? ad = Avg(all.Select(s => s.AdRaw));
        double? st = Avg(all.Select(s => s.StdRaw));
        double? vd = Avg(all.Select(s => s.VdafRaw));
        double? q  = Avg(all.Select(s => Cal(s, cal)));
        double? gi = Avg(all.Select(s => s.CakingG));
        string calTag = cal == CalorificKind.Qgr ? "Qgr,d" : "Qnet,ad";

        // ① 煤质表征
        string adL = ad.HasValue ? (_ref.FindLevel("ash", ad.Value)?.LevelName ?? AshWord(ad.Value)) : "";
        string stL = st.HasValue ? (_ref.FindLevel("sulfur", st.Value)?.LevelName ?? SulfurWord(st.Value)) : "";
        string rank = !vd.HasValue ? "" : vd.Value >= 37 ? "、高挥发分(煤化程度低,近气/长焰煤)" : vd.Value >= 28 ? "、中高挥发分" : vd.Value >= 20 ? "、中挥发分" : "、低挥发分(煤化程度高)";
        var typeTop = all.Where(s => !string.IsNullOrEmpty(s.CoalType)).GroupBy(s => s.CoalType!).OrderByDescending(g => g.Count()).FirstOrDefault();
        int typedN = all.Count(s => s.CoalType != null);
        outl.Add(new("煤质表征",
            $"本矿煤总体属【{adL}{stL}】煤{rank}；均值 Ad={F(ad)}% · St,d={F(st, 2)}% · {calTag}={F(q)}MJ/kg · Vdaf={F(vd)}%" +
            (gi.HasValue ? $" · G={F(gi)}" : "") +
            (typeTop != null ? $"；煤类以 {typeTop.Key} 为主({typeTop.Count()}/{typedN}段)。" : "；煤类多数未标注。"),
            Verdict.Neutral));

        // ② 煤层间优劣
        var seams = all.GroupBy(s => s.SeamCode)
            .Select(g => (Seam: g.Key, Ad: Avg(g.Select(s => s.AdRaw)), St: Avg(g.Select(s => s.StdRaw))))
            .Where(t => t.Ad.HasValue).ToList();
        if (seams.Count >= 2)
        {
            var hiAsh = seams.OrderByDescending(t => t.Ad).First();
            var loAsh = seams.OrderBy(t => t.Ad).First();
            var hiS = seams.Where(t => t.St.HasValue).OrderByDescending(t => t.St).FirstOrDefault();
            string s2 = $"{loAsh.Seam}煤灰分最低({F(loAsh.Ad)}%,质量最优)、{hiAsh.Seam}煤最高({F(hiAsh.Ad)}%)";
            if (hiS.Seam != null) s2 += $"；{hiS.Seam}煤硫分最高({F(hiS.St, 2)}%)";
            outl.Add(new("煤层对比", s2 + "。", Verdict.Neutral));
        }

        // ③ 变异性
        var adVals = all.Select(s => s.AdRaw).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (adVals.Count > 2 && ad is > 0)
        {
            double sd = Math.Sqrt(adVals.Sum(v => (v - ad.Value) * (v - ad.Value)) / (adVals.Count - 1));
            double cv = sd / ad.Value * 100;
            string uni = cv < 15 ? "均匀,煤质稳定" : cv < 30 ? "较均匀" : "波动较大,须注意配采均衡";
            outl.Add(new("均匀性", $"灰分变异系数 CV={cv:F0}%,{uni}(σ={sd:F1}%)。", cv < 30 ? Verdict.Good : Verdict.Warn));
        }

        // ④ 相关性
        var corr = StrongestCorrelation(all);
        if (corr != null) outl.Add(new("相关性", corr, Verdict.Neutral));

        // ⑤ 洗选可选性
        var washRows = WashingBySeam(withOverall: true);
        var wash = washRows.FirstOrDefault(w => w.SeamCode == "全矿") ?? washRows.FirstOrDefault();
        if (wash != null && wash.DeAshPct.HasValue)
        {
            double de = wash.DeAshPct.Value;
            string sel = de >= 60 ? "易选" : de >= 40 ? "中等可选" : de >= 20 ? "较难选" : "难选";
            string txt = $"原煤经洗选平均降灰 {de:F0}%(浮煤灰 {F(wash.AdClean)}%)";
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

    private static string F(double? v, int dec = 1) => v.HasValue ? v.Value.ToString("F" + dec) : "—";
    private static string AshWord(double ad) => ad <= 10 ? "特低灰" : ad <= 16 ? "低灰" : ad <= 29 ? "中灰" : ad <= 40 ? "富灰" : "高灰";
    private static string SulfurWord(double st) => st <= 0.5 ? "特低硫" : st <= 1.0 ? "低硫" : st <= 2.0 ? "中硫" : st <= 3.0 ? "中高硫" : "高硫";

    private string UtilizationVerdictText(double? ad, double? st, double? q, double? vd, double? gi)
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

    private string? StrongestCorrelation(IReadOnlyList<CoalSample> all)
    {
        var pairs = new (string A, string B, Func<CoalSample, double?> Ga, Func<CoalSample, double?> Gb)[]
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
            if (r.HasValue && Math.Abs(r.Value) > bestAbs)
            {
                bestAbs = Math.Abs(r.Value);
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
        double d = Math.Sqrt(sx * sy);
        return d < 1e-12 ? null : Math.Max(-1, Math.Min(1, sxy / d));
    }

    // ═══════════════════════════ 取值器 / 统计小工具 ═══════════════════════════

    /// <summary>指标+原/浮 → CoalSample 取值器。</summary>
    public static Func<CoalSample, double?> Getter(string indicator, bool useClean) => indicator switch
    {
        "ad_raw" => s => useClean ? s.AdClean : s.AdRaw,
        "std_raw" => s => useClean ? s.StdClean : s.StdRaw,
        "vdaf_raw" => s => useClean ? s.VdafClean : s.VdafRaw,
        "qnet_ad" => s => s.QnetAd,
        "qgr_d" => s => s.QgrD,
        "caking_g" => s => s.CakingG,
        _ => s => s.AdRaw,
    };

    private static Func<CoalSample3DRow, double?> Getter3D(string indicator, bool useClean) => indicator switch
    {
        "ad_raw" => r => useClean ? r.AdClean : r.AdRaw,
        "std_raw" => r => useClean ? r.StdClean : r.StdRaw,
        "vdaf_raw" => r => useClean ? r.VdafClean : r.VdafRaw,
        "qnet_ad" => r => r.QnetAd,
        "qgr_d" => r => r.QgrD,
        "caking_g" => r => r.CakingG,
        _ => r => r.AdRaw,
    };

    /// <summary>指标 → GB 分级规则类别（无对应则 null）。</summary>
    public static string? RuleFor(string indicator) => indicator switch
    {
        "ad_raw" => "ash",
        "std_raw" => "sulfur",
        "qnet_ad" => "qnet",
        "vdaf_raw" => "volatile",
        _ => null,
    };

    public static string IndicatorName(string indicator) => indicator switch
    {
        "ad_raw" => "Ad 灰分",
        "std_raw" => "St 全硫",
        "vdaf_raw" => "Vdaf 挥发分",
        "qnet_ad" => "Qnet 低位发热",
        "qgr_d" => "Qgr 弹筒发热",
        "caking_g" => "G 粘结指数",
        _ => indicator,
    };

    public static string IndicatorUnit(string indicator) => indicator switch
    {
        "qnet_ad" or "qgr_d" => "MJ/kg",
        "caking_g" => "",
        _ => "%",
    };

    private static double? Avg(IEnumerable<double?> src)
    {
        var xs = src.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return xs.Count > 0 ? xs.Average() : null;
    }

    /// <summary>线性插值分位数（xs 须已升序）。p ∈ [0,100]。</summary>
    private static double Percentile(IReadOnlyList<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        if (xs.Count == 1) return xs[0];
        double rank = p / 100.0 * (xs.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        if (lo == hi) return xs[lo];
        return xs[lo] + (xs[hi] - xs[lo]) * (rank - lo);
    }
}
