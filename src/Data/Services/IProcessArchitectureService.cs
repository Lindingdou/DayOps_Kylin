// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IProcessArchitectureService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>工艺架构服务:管理 process_system / process_phase / parameter_definition / equipment_constraint。</summary>
public interface IProcessArchitectureService
{
    // ─── 工艺系统(顶层)─────────────────────────────────────────────────
    IReadOnlyList<ProcessSystem> AllSystems(bool activeOnly = true);
    ProcessSystem? GetSystem(long systemId);
    ProcessSystem? GetSystemByCode(string code);
    long InsertSystem(ProcessSystem entity);
    void UpdateSystem(ProcessSystem entity);
    void DeleteSystem(long systemId);

    // ─── 工艺环节(系统下)──────────────────────────────────────────────
    IReadOnlyList<ProcessPhase> PhasesBySystem(long systemId);
    IReadOnlyList<ProcessPhase> AllPhases(bool activeOnly = true);
    ProcessPhase? GetPhase(long phaseId);
    ProcessPhase? GetPhaseByCode(long systemId, string code);
    long InsertPhase(ProcessPhase entity);
    void UpdatePhase(ProcessPhase entity);
    void DeletePhase(long phaseId);

    // ─── 参数定义(环节下)──────────────────────────────────────────────
    IReadOnlyList<ParameterDefinition> ParametersByPhase(long phaseId);
    IReadOnlyList<ParameterDefinition> AllParameters(bool activeOnly = true);
    ParameterDefinition? GetParameter(long paramId);
    ParameterDefinition? GetParameterByCode(string code);
    long InsertParameter(ParameterDefinition entity);
    void UpdateParameter(ParameterDefinition entity);
    void DeleteParameter(long paramId);

    // ─── 设备约束(参数下)──────────────────────────────────────────────
    IReadOnlyList<EquipmentConstraint> ConstraintsByParam(long paramId);
    IReadOnlyList<EquipmentConstraint> ConstraintsByModel(string equipmentModel);
    IReadOnlyList<EquipmentConstraint> AllConstraints(bool activeOnly = true);
    EquipmentConstraint? GetConstraint(long id);
    long InsertConstraint(EquipmentConstraint entity);
    void UpdateConstraint(EquipmentConstraint entity);
    void DeleteConstraint(long id);

    /// <summary>给定参数实测值,返回适配的设备型号清单(过滤掉 hard 约束违反的)。</summary>
    IReadOnlyList<string> CompatibleModels(long paramId, double measuredValue);
}
