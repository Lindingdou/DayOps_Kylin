// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/CoalReferenceService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 字典查询服务实现:煤层 / GB/T 5751 煤类 / 灰分硫分发热量分级。
/// 字典只读，且仅几十行，整体加载 + LINQ 过滤即可，不必每次查 DB。
/// </summary>
internal sealed class CoalReferenceService : ICoalReferenceService
{
    private readonly IRepository<CoalSeamDef> _seamRepo;
    private readonly IRepository<CoalClassification> _classRepo;
    private readonly IRepository<CoalGradeRule> _gradeRepo;

    public CoalReferenceService(ISqlService sql)
    {
        _seamRepo = sql.Repository<CoalSeamDef>();
        _classRepo = sql.Repository<CoalClassification>();
        _gradeRepo = sql.Repository<CoalGradeRule>();
    }

    public IReadOnlyList<CoalSeamDef> AllSeams()
        => _seamRepo.Where("1=1 ORDER BY sort_order");

    public CoalSeamDef? GetSeam(string code)
        => _seamRepo.Where("code = @c", new { c = code }).FirstOrDefault();

    public IReadOnlyList<CoalClassification> AllClassifications()
        => _classRepo.Where("1=1 ORDER BY sort_order");

    public CoalClassification? GetClassification(string code)
        => _classRepo.GetByKey(code);

    /// <summary>
    /// GB/T 5751 反推。规则：
    ///   V_daf, G(粘结指数), Y(胶质层) 三个区间都满足 → 命中。
    ///   找不到则返回 null（让调用方决定是否回退）。
    /// </summary>
    public string? ResolveCoalType(double? vdaf, double? gIndex, double? plasticY)
    {
        if (vdaf is null) return null;
        foreach (var c in AllClassifications())
        {
            if (!InRange(vdaf, c.VdafMin, c.VdafMax)) continue;
            if (gIndex is not null && !InRange(gIndex, c.GMin, c.GMax)) continue;
            if (plasticY is not null && !InRange(plasticY, c.YMin, c.YMax)) continue;
            return c.Code;
        }
        return null;
    }

    public IReadOnlyList<CoalGradeRule> RulesByType(string ruleType)
        => _gradeRepo.Where("rule_type = @t ORDER BY sort_order", new { t = ruleType });

    public CoalGradeRule? FindLevel(string ruleType, double value)
    {
        foreach (var r in RulesByType(ruleType))
        {
            // value_min = null 表示 −∞；value_max = null 表示 +∞
            bool minOk = r.ValueMin is null || value >= r.ValueMin.Value;
            bool maxOk = r.ValueMax is null || value < r.ValueMax.Value;
            if (minOk && maxOk) return r;
        }
        return null;
    }

    private static bool InRange(double? value, double? min, double? max)
    {
        if (value is null) return false;
        if (min is not null && value < min) return false;
        if (max is not null && value >= max) return false;
        return true;
    }
}
