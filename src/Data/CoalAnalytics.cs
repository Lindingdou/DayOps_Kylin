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
    double? QgrD, double? QnetAd, double? VdafRaw, double? VdafClean);

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
}
