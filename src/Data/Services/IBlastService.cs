// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IBlastService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IBlastService
{
    BlastEvent? GetById(long id);
    IReadOnlyList<BlastEvent> ByDate(DateTime date);
    IReadOnlyList<BlastEvent> InRange(DateTime start, DateTime end);
    IReadOnlyList<BlastEvent> ByDrill(string drillId);
    IReadOnlyList<BlastEvent> ByLocation(string locationCode);
    IReadOnlyList<BlastEvent> All();

    void Insert(BlastEvent entity);
    void Update(BlastEvent entity);
    void Delete(long id);

    /// <summary>按月汇总爆破方量与单耗。</summary>
    IReadOnlyList<BlastMonthlyAggregate> GetMonthlyAggregate(int year);
}

public sealed record BlastMonthlyAggregate(
    int Year, int Month,
    double TotalVolumeM3,
    double TotalExplosiveKg,
    double AvgUnitConsumptionKgM3,
    int EventCount);
