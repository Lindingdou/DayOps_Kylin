// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ICoalReferenceService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>地质模块字典查询（煤层定义 / GB/T 5751 煤类 / 灰分硫分发热量分级）。</summary>
public interface ICoalReferenceService
{
    // ─── coal_seam_def ───
    IReadOnlyList<CoalSeamDef> AllSeams();
    CoalSeamDef? GetSeam(string code);

    // ─── coal_classification ───
    IReadOnlyList<CoalClassification> AllClassifications();
    CoalClassification? GetClassification(string code);

    /// <summary>按 GB/T 5751 反推煤类。</summary>
    string? ResolveCoalType(double? vdaf, double? gIndex, double? plasticY);

    // ─── coal_grade_rule ───
    IReadOnlyList<CoalGradeRule> RulesByType(string ruleType);

    /// <summary>给定指标值查找等级。</summary>
    CoalGradeRule? FindLevel(string ruleType, double value);
}
