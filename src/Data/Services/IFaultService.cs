// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IFaultService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IFaultService
{
    FaultEvent? GetById(long id);
    IReadOnlyList<FaultEvent> ByEquipment(string equipmentId);
    IReadOnlyList<FaultEvent> ByDate(DateTime date);
    IReadOnlyList<FaultEvent> ByType(string faultType);
    IReadOnlyList<FaultEvent> InRange(DateTime start, DateTime end);
    IReadOnlyList<FaultEvent> All();

    void Insert(FaultEvent entity);
    void Update(FaultEvent entity);
    void Delete(long id);

    /// <summary>故障 Pareto:按故障类型聚合的总停机小时,降序。</summary>
    IReadOnlyList<FaultParetoEntry> GetPareto(string? equipmentId = null);
}

public sealed record FaultParetoEntry(string FaultType, double TotalHours, int Count);
