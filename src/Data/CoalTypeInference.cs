using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  GB/T 5751 煤类反推 + 单指标分级 —— 忠实移植原 CoalReferenceService.ResolveCoalType /
//  FindLevel 的「纯逻辑」区间匹配(原类注: GB/T 5751 反推等纯逻辑委托 ICoalReferenceService)。
//    · 反推: V_daf / G(粘结指数) / Y(胶质层) 三维区间都命中 → 该煤类; 首个命中者胜, 无命中 null。
//    · 分级: value ∈ [ValueMin, ValueMax) → 该级(min=null 即 −∞, max=null 即 +∞)。
//  分类/分级阈值表是国标数据(原存 DB 字典 coal_classification / coal_grade_rule, Kylin 已同库同列种子),
//  由调用方(DB/CSV)喂入区间; 此处只移可验证的匹配算法本身, 精确国标表值不臆造。纯逻辑、可单测。
// ─────────────────────────────────────────────────────────────────────────────

public static class CoalTypeInference
{
    /// <summary>一条煤类分类区间(Vdaf/G/Y 三维, [min,max) 半开; min=null 即 −∞, max=null 即 +∞)→ 煤类代号。</summary>
    public sealed record ClassRange(string Code,
        double? VdafMin, double? VdafMax, double? GMin, double? GMax, double? YMin, double? YMax);

    /// <summary>一条单指标分级规则([ValueMin,ValueMax) → 级名), 用于灰分/硫分/发热量分级。</summary>
    public sealed record GradeRule(string LevelName, double? ValueMin, double? ValueMax);

    // 忠实原 InRange: [min,max) 半开; min=null −∞, max=null +∞; value<min 或 value>=max 均出界; value=null 出界。
    private static bool InRange(double? value, double? min, double? max)
    {
        if (value is null) return false;
        if (min is not null && value < min) return false;
        if (max is not null && value >= max) return false;
        return true;
    }

    /// <summary>
    /// GB/T 5751 反推煤类(忠实原 ResolveCoalType)。Vdaf 必需; G/Y 若样本提供则必须同时命中区间(未提供则跳过该维,
    /// 忠实原可选判定)。首个三维全命中的区间胜; 无命中返回 null(调用方决定是否回退标注值)。
    /// </summary>
    public static string? ResolveCoalType(double? vdaf, double? gIndex, double? plasticY, IReadOnlyList<ClassRange> ranges)
    {
        if (vdaf is null || ranges is null) return null;
        foreach (var c in ranges)
        {
            if (!InRange(vdaf, c.VdafMin, c.VdafMax)) continue;
            if (gIndex is not null && !InRange(gIndex, c.GMin, c.GMax)) continue;
            if (plasticY is not null && !InRange(plasticY, c.YMin, c.YMax)) continue;
            return c.Code;
        }
        return null;
    }

    /// <summary>按分级规则表查指标所属级名(忠实原 FindLevel: [min,max) 首命中)。无命中/无值返回 null。</summary>
    public static string? FindGradeLevel(double? value, IReadOnlyList<GradeRule> rules)
    {
        if (value is null || rules is null) return null;
        foreach (var r in rules)
            if (InRange(value, r.ValueMin, r.ValueMax)) return r.LevelName;
        return null;
    }

    /// <summary>逐样本反推结果 + 与标注比对(null 表示无法判定)。</summary>
    public sealed record InferRow(long Id, string HoleId, string SeamCode,
        double? Vdaf, double? G, double? Y, string? Labeled, string? Inferred, bool? Match);

    /// <summary>煤类反推一致率(忠实原 CoalQuality 审核「煤类反推」QC): 反推==标注 的占比。分母=两者都有的样本。</summary>
    public sealed record ConsistencyResult(int Total, int Consistent, int Inconclusive, double RatePct,
        IReadOnlyList<InferRow> Rows);

    /// <summary>
    /// 逐煤样反推煤类并与标注 CoalType 比对。缺 Vdaf/反推 null 或缺标注 → 无法判定(计入 Inconclusive, 不入分母)。
    /// useClean=true 用浮煤 Vdaf(VdafClean), 否则原煤(VdafRaw)。
    /// </summary>
    public static ConsistencyResult InferConsistency(IEnumerable<CoalSample> samples, IReadOnlyList<ClassRange> ranges, bool useClean = false)
    {
        var rows = new List<InferRow>();
        int total = 0, consistent = 0, inconclusive = 0;
        foreach (var s in samples)
        {
            double? vd = useClean ? s.VdafClean : s.VdafRaw;
            string? inferred = ResolveCoalType(vd, s.CakingG, s.PlasticYMm, ranges);
            bool? match = null;
            if (inferred is null || string.IsNullOrWhiteSpace(s.CoalType)) inconclusive++;
            else
            {
                total++;
                match = string.Equals(inferred, s.CoalType, StringComparison.OrdinalIgnoreCase);
                if (match == true) consistent++;
            }
            rows.Add(new InferRow(s.Id, s.HoleId, s.SeamCode, vd, s.CakingG, s.PlasticYMm, s.CoalType, inferred, match));
        }
        double rate = total > 0 ? 100.0 * consistent / total : 0;
        return new ConsistencyResult(total, consistent, inconclusive, rate, rows);
    }
}
