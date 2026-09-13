// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IProductionService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IProductionService
{
    // 班次粒度
    ProductionRecord? Get(string equipmentId, DateTime date, string shift);
    IReadOnlyList<ProductionRecord> ByEquipment(string equipmentId);
    IReadOnlyList<ProductionRecord> ByDate(DateTime date);
    IReadOnlyList<ProductionRecord> InRange(string equipmentId, DateTime start, DateTime end);
    IReadOnlyList<ProductionRecord> All();

    // 月度粒度
    CapacityMonthly? GetMonthly(string equipmentId, int year, int month);
    IReadOnlyList<CapacityMonthly> MonthlyByYear(string equipmentId, int year);
    IReadOnlyList<CapacityMonthly> MonthlyAll();

    // 写
    void Insert(ProductionRecord entity);
    void Update(ProductionRecord entity);
    void Delete(long id);
    void UpsertMonthly(CapacityMonthly entity);
    void BulkInsertProduction(IEnumerable<ProductionRecord> entities);
}
