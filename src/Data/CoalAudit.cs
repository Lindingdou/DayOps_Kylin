using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  煤质数据审核 —— 忠实移植原 CoalQualityService.RunAudit「一键 N 类规则审核」的纯规则逻辑。
//  原 7 规则中 Kylin 数据可支撑者(CoalSample 有 Ad/St/Qnet/CleanCoalYield/G/Y/CoalType):
//    ② 物理范围: St,d∈[0,10]·A_d∈[0,60]·Q_net,ad≤50   (硬越界=Error)
//    ③ 原煤vs浮煤: 浮煤灰分 > 原煤灰分 = 物理不可能        (Error)
//    ④ 煤类反推: GB/T 5751 反推 ≠ 标注                    (Warning, 复用 CoalTypeInference)
//    ⑤ 同层离群: 同煤层 A_d 3σ(该层样本≥30 方启用)        (Warning)
//    ⑦ 浮煤回收率: yield∈[0,100]                          (Error)
//  数据不支撑者记录不做: ① 工分自洽(M+A+V+FC≈100 需 Mad/FCd, CoalSample 无) · ⑥ 钻探-测井煤厚一致(需 drill/log 厚度)。
//  纯逻辑、可单测(给定样本→findings)。审核只读, 不写库。
// ─────────────────────────────────────────────────────────────────────────────

public static class CoalAudit
{
    public enum Severity { Info, Warning, Error }

    public sealed record Finding(long SampleId, string HoleId, string SeamCode, string Category, Severity Severity, string Message);

    public sealed record AuditSummary(int Samples, int Findings, int Errors, int Warnings,
        IReadOnlyList<(string Category, int Count)> ByCategory, IReadOnlyList<Finding> Items);

    /// <summary>
    /// 跑可支撑的 5 类规则。ranges 为 GB/T 5751 分类区间(供规则④, null 则跳过反推规则)。
    /// minSeamForOutlier=同层离群统计的最小层样本数(忠实原 ≥30)。
    /// </summary>
    public static AuditSummary Run(IReadOnlyList<CoalSample> samples, IReadOnlyList<CoalTypeInference.ClassRange>? ranges = null,
        int minSeamForOutlier = 30)
    {
        var f = new List<Finding>();

        foreach (var s in samples)
        {
            // ② 物理范围
            if (s.StdRaw is < 0 or > 10)
                f.Add(new Finding(s.Id, s.HoleId, s.SeamCode, "物理范围", Severity.Error, $"原煤 S_t,d = {s.StdRaw:0.##} 超出 [0,10]"));
            if (s.AdRaw is < 0 or > 60)
                f.Add(new Finding(s.Id, s.HoleId, s.SeamCode, "物理范围", Severity.Error, $"原煤 A_d = {s.AdRaw:0.##} 超出 [0,60]"));
            if (s.QnetAd is > 50)
                f.Add(new Finding(s.Id, s.HoleId, s.SeamCode, "物理范围", Severity.Error, $"Q_net,ad = {s.QnetAd:0.##} > 50 MJ/kg"));

            // ③ 原煤 vs 浮煤(浮煤灰分不应高于原煤)
            if (s.AdRaw is not null && s.AdClean is not null && s.AdClean > s.AdRaw)
                f.Add(new Finding(s.Id, s.HoleId, s.SeamCode, "原煤vs浮煤", Severity.Error,
                    $"浮煤灰分 {s.AdClean:0.##} > 原煤灰分 {s.AdRaw:0.##}（物理上不可能）"));

            // ④ 煤类反推 ≠ 标注
            if (ranges is { Count: > 0 } && !string.IsNullOrWhiteSpace(s.CoalType))
            {
                var resolved = CoalTypeInference.ResolveCoalType(s.VdafRaw, s.CakingG, s.PlasticYMm, ranges);
                if (resolved is not null && !string.Equals(resolved, s.CoalType, StringComparison.OrdinalIgnoreCase))
                    f.Add(new Finding(s.Id, s.HoleId, s.SeamCode, "煤类反推", Severity.Warning,
                        $"反推煤类 {resolved}，原表标注 {s.CoalType}"));
            }

            // ⑦ 浮煤回收率范围
            if (s.CleanCoalYield is < 0 or > 100)
                f.Add(new Finding(s.Id, s.HoleId, s.SeamCode, "浮煤回收率", Severity.Error, $"浮煤回收率 {s.CleanCoalYield:0.##} 越界 [0,100]"));
        }

        // ⑤ 同层离群: 每煤层 A_d 3σ(该层样本≥minSeamForOutlier 才统计, 忠实原 ≥30)
        foreach (var g in samples.Where(s => s.AdRaw is not null).GroupBy(s => s.SeamCode))
        {
            var vals = g.Select(s => s.AdRaw!.Value).ToList();
            if (vals.Count < minSeamForOutlier) continue;
            double mean = vals.Average();
            double sd = Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / vals.Count);
            if (sd < 0.01) continue;
            foreach (var s in g)
            {
                double z = (s.AdRaw!.Value - mean) / sd;
                if (Math.Abs(z) > 3)
                    f.Add(new Finding(s.Id, s.HoleId, s.SeamCode, "同层离群", Severity.Warning,
                        $"A_d = {s.AdRaw:0.##}（层均 {mean:0.##}±{sd:0.##}, z={z:0.#}）"));
            }
        }

        var byCat = f.GroupBy(x => x.Category).Select(gr => (gr.Key, gr.Count()))
            .OrderByDescending(t => t.Item2).ToList();
        int err = f.Count(x => x.Severity == Severity.Error), warn = f.Count(x => x.Severity == Severity.Warning);
        return new AuditSummary(samples.Count, f.Count, err, warn, byCat, f);
    }

    /// <summary>findings → CSV。</summary>
    public static string ToCsv(AuditSummary a)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("sample_id,hole_id,seam,category,severity,message\n");
        string Q(string? s) => s == null ? "" : (s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);
        foreach (var x in a.Items)
            sb.Append($"{x.SampleId},{Q(x.HoleId)},{Q(x.SeamCode)},{Q(x.Category)},{x.Severity},{Q(x.Message)}\n");
        return sb.ToString();
    }
}
