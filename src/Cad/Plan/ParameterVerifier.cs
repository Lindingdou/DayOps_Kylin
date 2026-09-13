using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 现状台阶参数自动校核（原 <c>PlanLib.ShortTerm.ParameterVerifier</c>）：把 <see cref="BenchParameterExtractor"/> 量出来的 H/α/W/β
/// 与设计基准（优先模板库 → 规范默认）逐项比对，判定走 <c>parameter_definition</c> 的规范区间 + 验收服务的 ComputeStatus，
/// 服务不可用时本地兜底（±15% 判偏差）。结果可回写 <c>parameter_acceptance</c>。绝不抛。
/// </summary>
public sealed class ParameterVerifier
{
    public sealed class Row
    {
        public string Code = "";
        public string Name = "";
        public string Unit = "";
        public double Measured;
        public double? Design;
        public double? StdMin, StdMax;
        public double? DeviationPct;
        public string Status = "pending";   // pass / warning / fail / pending
        public string Source = "";
    }

    public sealed class Report
    {
        public bool IsDump;
        public string DesignProvenance = "";
        public double? FrictionAngleDeg;
        public double? StabilityF;
        public string OverallStatus = "pending";
        public List<Row> Rows = new();
        public List<string> Notes = new();
    }

    /// <summary>设计基准（H/α/W/β + 来源）。</summary>
    public readonly record struct Design(double H, double A, double W, double Beta, string Provenance);

    /// <summary>
    /// 设计基准解析：优先模板库（原 MineAssLib.BenchTemplateResolver.Resolve —— Kylin 侧对应实现在 Data 层，若已移植则经反射取用，
    /// 免得本文件对它硬依赖），取不到按规范默认（是否排土 × 硬度）。
    /// </summary>
    public static Design ResolveDesign(bool isDump, string? material, string? hardness, System.Data.Common.DbConnection? conn = null)
    {
        try
        {
            var t = Type.GetType("PitMine3D.Kylin.Data.BenchTemplateResolver, PitMine3D.Kylin");
            var m = t?.GetMethod("Resolve", new[] { typeof(System.Data.Common.DbConnection), typeof(bool), typeof(string), typeof(string) });
            if (m != null)
            {
                var r = m.Invoke(null, new object?[] { conn, isDump, material, hardness });
                if (r != null)
                {
                    var rt = r.GetType();
                    double H = Convert.ToDouble(rt.GetProperty("BenchHeight")!.GetValue(r));
                    double A = Convert.ToDouble(rt.GetProperty("FaceAngleDeg")!.GetValue(r));
                    double W = Convert.ToDouble(rt.GetProperty("BermWidth")!.GetValue(r));
                    double B = Convert.ToDouble(rt.GetProperty("OverallSlopeAngleDeg")!.GetValue(r));
                    string prov = rt.GetProperty("Provenance")?.GetValue(r)?.ToString() ?? "模板";
                    if (H > 0 && A > 0) return new Design(H, A, W, B, prov);
                }
            }
        }
        catch { /* 模板层未就绪 → 规范默认 */ }
        var (nh, na, nw) = BenchParameterVerifier.Norm(isDump, hardness);
        return new Design(nh, na, nw, BenchParameterExtractor.OverallSlopeAngleDeg(nh, na, nw),
            isDump ? "规范默认 · 排土场" : $"规范默认 · {HardnessLabel(hardness)}");
    }

    private static string HardnessLabel(string? hardness) => (hardness ?? "").ToLowerInvariant() switch
    {
        "hard" => "硬岩", "soft" => "软岩", "medium" => "中硬岩", _ => "中硬岩(缺省)",
    };

    /// <summary>校核。m = 提取结果；isDump 来自区域类别（采场 false / 排土场 true）；material/hardness 选填以定设计基准；frictionAngleDeg 选填以算稳定性下界。</summary>
    public static Report Verify(BenchParameterExtractor.Result m, bool isDump,
        string? material = null, string? hardness = null, double? frictionAngleDeg = null, System.Data.Common.DbConnection? conn = null)
    {
        var design = ResolveDesign(isDump, material, hardness, conn);
        var rep = new Report { IsDump = isDump, DesignProvenance = design.Provenance, FrictionAngleDeg = frictionAngleDeg };
        if (m == null) { rep.Notes.Add("无提取结果。"); return rep; }

        rep.Rows.Add(BuildRow("bench_height", "台阶高", "m", m.BenchHeight, design.H));
        rep.Rows.Add(BuildRow("bench_slope_angle", "坡面角", "°", m.FaceAngleDeg, design.A));
        rep.Rows.Add(BuildRow("safety_platform_width", "平盘宽", "m", m.BermWidth, design.W));
        rep.Rows.Add(BuildRow("overall_slope_angle", "整体帮坡角", "°", m.OverallSlopeAngleDeg, design.Beta));

        if (frictionAngleDeg is double phi && phi > 0 && m.OverallSlopeAngleDeg > 0)
        {
            rep.StabilityF = BenchParameterVerifier.CohesionlessFactorOfSafety(m.OverallSlopeAngleDeg, phi);
            rep.Notes.Add(rep.StabilityF >= 1.3
                ? $"稳定性 F=tanφ/tanβ={rep.StabilityF:0.00} ≥1.3 ✔（无黏聚力下界，保守）"
                : $"稳定性 F={rep.StabilityF:0.00} <1.3 ⚠（仅摩擦下界，未计黏聚力）");
        }
        rep.Notes.AddRange(m.Warnings);
        rep.OverallStatus = Worst(rep.Rows.Select(r => r.Status));
        return rep;
    }

    private static Row BuildRow(string code, string name, string unit, double measured, double design)
    {
        var row = new Row { Code = code, Name = name, Unit = unit, Measured = measured, Design = design };
        ParameterDefinition? def = null;
        try { def = EquipmentDataContext.ProcessArchitecture.GetParameterByCode(code); } catch { /* GeoDataBase 未就绪 */ }
        if (def != null)
        {
            if (!string.IsNullOrEmpty(def.Name)) row.Name = def.Name;
            if (!string.IsNullOrEmpty(def.Unit)) row.Unit = def.Unit!;
            row.StdMin = def.StandardMin; row.StdMax = def.StandardMax;
            try
            {
                var (dev, status) = EquipmentDataContext.ParameterAcceptance.ComputeStatus(def, design, measured);
                row.DeviationPct = dev; row.Status = status; row.Source = "参数验收·规范";
                return row;
            }
            catch { /* 服务不可用 → 本地兜底 */ }
        }
        FallbackStatus(row, measured, design);
        row.Source = def != null ? "规范(本地兜底)" : "本地兜底·无规范";
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

    /// <summary>把校核行转成验收记录（回写 parameter_acceptance）。PhaseId 取各参数定义自身的环节；无规范定义的行跳过；量不出来的（实测 ≤0）不写。</summary>
    public static List<ParameterAcceptance> ToAcceptanceRecords(Report rep, string locationCode, DateTime measureDate)
    {
        var outList = new List<ParameterAcceptance>();
        foreach (var r in rep.Rows)
        {
            ParameterDefinition? def = null;
            try { def = EquipmentDataContext.ProcessArchitecture.GetParameterByCode(r.Code); } catch { }
            if (def == null) continue;
            if (!(r.Measured > 1e-6)) continue;   // 一条 safety_platform_width=0 会被当成"实测就是 0"，比缺一条更糟
            outList.Add(new ParameterAcceptance
            {
                ParamId = def.ParamId, PhaseId = def.PhaseId, LocationCode = locationCode, MeasureDate = measureDate,
                MeasuredValue = r.Measured, TemplateValue = r.Design, DeviationPct = r.DeviationPct, Status = r.Status, Conclusion = "自动校核提取",
            });
        }
        return outList;
    }
}
