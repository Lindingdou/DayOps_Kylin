using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 现状台阶参数校核(件二·校核) —— 忠实移植原 PlanLib.ShortTerm.ParameterVerifier 的**兜底校核路径**。
/// 把 <see cref="BenchParameterExtractor"/> 提取的实测台阶参数与「设计基准 + 规范默认」逐项比对, 出偏差%+状态。
///
/// 忠实性说明: 原 Verify 先经 GeoDataBase 参数验收引擎(ComputeStatus, 有 StandardMin/Max 分 pass/warning/fail),
/// GeoDataBase/模板库未就绪时**回退到本地**——设计基准取 BenchTemplateResolver.Norm(采场通用 12/70/4·硬 15/70/8·
/// 中 12/68/6·软 10/60/5; 排土 10/35/3), 状态取 FallbackStatus(|偏差|>15% → warning, 否则 pass)。
/// Kylin 无 GeoDataBase 模板库(大引擎, 记为不可移), 故本类正是原版那条兜底路径的完整移植; 稳定性 F=tanφ/tanβ 同式。
/// 纯逻辑、可单测。
/// </summary>
public sealed class BenchParameterVerifier
{
    public sealed class Row
    {
        public string Code = "";
        public string Name = "";
        public string Unit = "";
        public double Measured;
        public double? Design;
        public double? DeviationPct;
        public string Status = "pending";   // pass / warning / pending
        public string Source = "";
    }

    public sealed class Report
    {
        public bool IsDump;
        public string DesignProvenance = "";
        public double? FrictionAngleDeg;
        public double StabilityF;            // tanφ/tanβ; 0 = 未算
        public string OverallStatus = "pending";
        public List<Row> Rows = new();
        public List<string> Notes = new();
    }

    /// <summary>
    /// 校核。m = 提取结果; isDump 来自区域类别(采场 false/排土场 true); hardness 选填定设计基准(hard/medium/soft);
    /// frictionAngleDeg 选填算稳定性下界。designOverride 选填直接给设计 (H,α,W) 覆盖规范默认。绝不抛。
    /// </summary>
    public static Report Verify(BenchParameterExtractor.Result m, bool isDump,
        string? hardness = null, double? frictionAngleDeg = null,
        (double H, double A, double W)? designOverride = null)
    {
        var rep = new Report { IsDump = isDump, FrictionAngleDeg = frictionAngleDeg };
        if (m == null) { rep.Notes.Add("无提取结果。"); return rep; }

        var (dH, dA, dW) = designOverride ?? Norm(isDump, hardness);
        double dBeta = BenchParameterExtractor.OverallSlopeAngleDeg(dH, dA, dW);
        rep.DesignProvenance = designOverride != null ? "设计基准(调用方给定)"
            : (isDump ? "规范默认 · 排土场" : $"规范默认 · {HardnessLabel(hardness)}");

        rep.Rows.Add(BuildRow("bench_height", "台阶高", "m", m.BenchHeight, dH));
        rep.Rows.Add(BuildRow("bench_slope_angle", "坡面角", "°", m.FaceAngleDeg, dA));
        rep.Rows.Add(BuildRow("safety_platform_width", "平盘宽", "m", m.BermWidth, dW));
        rep.Rows.Add(BuildRow("overall_slope_angle", "整体帮坡角", "°", m.OverallSlopeAngleDeg, dBeta));

        if (frictionAngleDeg is double phi && phi > 0 && m.OverallSlopeAngleDeg > 0)
        {
            rep.StabilityF = CohesionlessFactorOfSafety(m.OverallSlopeAngleDeg, phi);
            rep.Notes.Add(rep.StabilityF >= 1.3
                ? $"稳定性 F=tanφ/tanβ={rep.StabilityF:0.00} ≥1.3 ✔(无黏聚力下界, 保守)"
                : $"稳定性 F={rep.StabilityF:0.00} <1.3 ⚠(仅摩擦下界, 未计黏聚力)");
        }

        rep.Notes.AddRange(m.Warnings);
        rep.OverallStatus = Worst(rep.Rows.Select(r => r.Status));
        return rep;
    }

    /// <summary>校核报表(CSV)。</summary>
    public static string BuildReport(Report rep)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("现状台阶参数校核");
        sb.AppendLine($"边坡类型,{(rep.IsDump ? "排土场" : "采场")}");
        sb.AppendLine("设计依据," + rep.DesignProvenance.Replace(',', '，'));
        sb.AppendLine($"总体状态,{StatusCn(rep.OverallStatus)}");
        if (rep.StabilityF > 0) sb.AppendLine($"稳定性F(tanφ/tanβ),{rep.StabilityF:0.00}");
        sb.AppendLine();
        sb.AppendLine("参数,单位,实测,设计,偏差%,状态,依据");
        foreach (var r in rep.Rows)
            sb.AppendLine($"{r.Name},{r.Unit},{r.Measured:0.##},{(r.Design.HasValue ? r.Design.Value.ToString("0.##") : "")},"
                        + $"{(r.DeviationPct.HasValue ? r.DeviationPct.Value.ToString("0.#") : "")},{StatusCn(r.Status)},{r.Source.Replace(',', '，')}");
        if (rep.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("提示");
            foreach (var n in rep.Notes) sb.AppendLine(n.Replace(',', '，'));
        }
        return sb.ToString();
    }

    // ── 辅助(忠实原版兜底路径) ────────────────────────────
    private static Row BuildRow(string code, string name, string unit, double measured, double design)
    {
        var row = new Row { Code = code, Name = name, Unit = unit, Measured = measured, Design = design, Source = "本地兜底·规范默认" };
        FallbackStatus(row, measured, design);
        return row;
    }

    private static void FallbackStatus(Row row, double measured, double design)
    {
        if (Math.Abs(design) > 1e-6)
        {
            double dev = (measured - design) / design * 100.0;
            row.DeviationPct = dev;
            row.Status = Math.Abs(dev) > 15 ? "warning" : "pass";
        }
        else row.Status = "pending";
    }

    private static string Worst(IEnumerable<string> statuses)
    {
        var set = statuses.ToHashSet();
        if (set.Contains("fail")) return "fail";
        if (set.Contains("warning")) return "warning";
        if (set.Contains("pass")) return "pass";
        return "pending";
    }

    /// <summary>规范默认台阶参数(H,α,W)。忠实原 BenchTemplateResolver.Norm。</summary>
    public static (double H, double A, double W) Norm(bool isDump, string? hardness)
    {
        if (isDump) return (10, 35, 3);
        return (hardness ?? "").ToLowerInvariant() switch
        {
            "hard" => (15, 70, 8),
            "medium" => (12, 68, 6),
            "soft" => (10, 60, 5),
            _ => (12, 70, 4),   // 通用兜底(与历史采场默认一致)
        };
    }

    /// <summary>无黏聚力安全系数 F = tanφ/tanβ(平坡→+∞)。忠实原 BenchTemplateResolver。</summary>
    public static double CohesionlessFactorOfSafety(double overallAngleDeg, double frictionAngleDeg)
    {
        double tb = Math.Tan(overallAngleDeg * Math.PI / 180.0);
        if (tb <= 1e-9) return double.PositiveInfinity;
        return Math.Tan(frictionAngleDeg * Math.PI / 180.0) / tb;
    }

    private static string HardnessLabel(string? h) => (h ?? "").ToLowerInvariant() switch
    {
        "hard" => "硬岩",
        "medium" => "中硬岩",
        "soft" => "软岩",
        _ => "通用",
    };

    private static string StatusCn(string s) => s switch
    {
        "pass" => "合格",
        "warning" => "偏差",
        "fail" => "超标",
        _ => "待定",
    };
}
