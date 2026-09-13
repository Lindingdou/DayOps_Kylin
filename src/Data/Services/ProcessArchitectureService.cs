// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/ProcessArchitectureService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class ProcessArchitectureService : IProcessArchitectureService
{
    private readonly ISqlService _sql;
    private readonly IRepository<ProcessSystem> _systemRepo;
    private readonly IRepository<ProcessPhase> _phaseRepo;
    private readonly IRepository<ParameterDefinition> _paramRepo;
    private readonly IRepository<EquipmentConstraint> _constraintRepo;

    public ProcessArchitectureService(ISqlService sql)
    {
        _sql = sql;
        _systemRepo = sql.Repository<ProcessSystem>();
        _phaseRepo = sql.Repository<ProcessPhase>();
        _paramRepo = sql.Repository<ParameterDefinition>();
        _constraintRepo = sql.Repository<EquipmentConstraint>();
    }

    // ─── 系统 ──────────────────────────────────────────────────────────
    public IReadOnlyList<ProcessSystem> AllSystems(bool activeOnly = true)
        => activeOnly
            ? _systemRepo.Where("is_active = 1 ORDER BY display_order, system_id")
            : _systemRepo.Where("1=1 ORDER BY display_order, system_id");

    public ProcessSystem? GetSystem(long systemId) => _systemRepo.GetByKey(systemId);

    public ProcessSystem? GetSystemByCode(string code)
        => _systemRepo.Where("code = @c", new { c = code }).FirstOrDefault();

    public long InsertSystem(ProcessSystem entity) => _systemRepo.Insert(entity);
    public void UpdateSystem(ProcessSystem entity) => _systemRepo.Update(entity);
    public void DeleteSystem(long systemId) => _systemRepo.Delete(systemId);

    // ─── 环节 ──────────────────────────────────────────────────────────
    public IReadOnlyList<ProcessPhase> PhasesBySystem(long systemId)
        => _phaseRepo.Where("system_id = @s ORDER BY sequence_order, phase_id", new { s = systemId });

    public IReadOnlyList<ProcessPhase> AllPhases(bool activeOnly = true)
        => activeOnly
            ? _phaseRepo.Where("is_active = 1 ORDER BY system_id, sequence_order")
            : _phaseRepo.Where("1=1 ORDER BY system_id, sequence_order");

    public ProcessPhase? GetPhase(long phaseId) => _phaseRepo.GetByKey(phaseId);

    public ProcessPhase? GetPhaseByCode(long systemId, string code)
        => _phaseRepo.Where("system_id = @s AND code = @c", new { s = systemId, c = code }).FirstOrDefault();

    public long InsertPhase(ProcessPhase entity) => _phaseRepo.Insert(entity);
    public void UpdatePhase(ProcessPhase entity) => _phaseRepo.Update(entity);
    public void DeletePhase(long phaseId) => _phaseRepo.Delete(phaseId);

    // ─── 参数 ──────────────────────────────────────────────────────────
    public IReadOnlyList<ParameterDefinition> ParametersByPhase(long phaseId)
        => _paramRepo.Where("phase_id = @p ORDER BY display_order, param_id", new { p = phaseId });

    public IReadOnlyList<ParameterDefinition> AllParameters(bool activeOnly = true)
        => activeOnly
            ? _paramRepo.Where("is_active = 1 ORDER BY phase_id, display_order")
            : _paramRepo.Where("1=1 ORDER BY phase_id, display_order");

    public ParameterDefinition? GetParameter(long paramId) => _paramRepo.GetByKey(paramId);

    public ParameterDefinition? GetParameterByCode(string code)
        => _paramRepo.Where("code = @c", new { c = code }).FirstOrDefault();

    public long InsertParameter(ParameterDefinition entity) => _paramRepo.Insert(entity);
    public void UpdateParameter(ParameterDefinition entity) => _paramRepo.Update(entity);
    public void DeleteParameter(long paramId) => _paramRepo.Delete(paramId);

    // ─── 约束 ──────────────────────────────────────────────────────────
    public IReadOnlyList<EquipmentConstraint> ConstraintsByParam(long paramId)
        => _constraintRepo.Where("param_id = @p AND is_active = 1", new { p = paramId });

    public IReadOnlyList<EquipmentConstraint> ConstraintsByModel(string equipmentModel)
        => _constraintRepo.Where("equipment_model = @m AND is_active = 1", new { m = equipmentModel });

    public IReadOnlyList<EquipmentConstraint> AllConstraints(bool activeOnly = true)
        => activeOnly ? _constraintRepo.Where("is_active = 1") : _constraintRepo.All();

    public EquipmentConstraint? GetConstraint(long id) => _constraintRepo.GetByKey(id);
    public long InsertConstraint(EquipmentConstraint entity) => _constraintRepo.Insert(entity);
    public void UpdateConstraint(EquipmentConstraint entity) => _constraintRepo.Update(entity);
    public void DeleteConstraint(long id) => _constraintRepo.Delete(id);

    public IReadOnlyList<string> CompatibleModels(long paramId, double measuredValue)
    {
        var hardConstraints = _constraintRepo.Where(
            "param_id = @p AND consequence = 'hard' AND is_active = 1",
            new { p = paramId });
        var blocked = new HashSet<string>();
        foreach (var c in hardConstraints)
        {
            if (Violates(c, measuredValue))
                blocked.Add(c.EquipmentModel);
        }

        // 从 equipment_model 取全部型号,减去 blocked
        var allModels = _sql.Query<string>("SELECT model FROM equipment_model").ToList();
        return allModels.Where(m => !blocked.Contains(m)).ToList();
    }

    private static bool Violates(EquipmentConstraint c, double v)
    {
        return c.ConstraintType switch
        {
            "min"    => c.LimitValue.HasValue && v < c.LimitValue.Value,
            "max"    => c.LimitValue.HasValue && v > c.LimitValue.Value,
            "range"  => (c.LimitMin.HasValue && v < c.LimitMin.Value) ||
                       (c.LimitMax.HasValue && v > c.LimitMax.Value),
            "equals" => c.LimitValue.HasValue && System.Math.Abs(v - c.LimitValue.Value) > 1e-6,
            _        => false
        };
    }
}
