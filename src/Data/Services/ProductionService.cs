// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/ProductionService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class ProductionService : IProductionService
{
    private readonly ISqlService _sql;
    private readonly IRepository<ProductionRecord> _prodRepo;
    private readonly IRepository<CapacityMonthly> _capRepo;

    public ProductionService(ISqlService sql)
    {
        _sql = sql;
        _prodRepo = sql.Repository<ProductionRecord>();
        _capRepo = sql.Repository<CapacityMonthly>();
    }

    public ProductionRecord? Get(string equipmentId, DateTime date, string shift)
        => _prodRepo.Where(
            "equipment_id = @id AND date = @d AND shift = @s",
            new { id = equipmentId, d = BusinessDate.P(date), s = shift }).FirstOrDefault();

    public IReadOnlyList<ProductionRecord> ByEquipment(string equipmentId)
        => _prodRepo.Where("equipment_id = @id ORDER BY date, shift", new { id = equipmentId });

    public IReadOnlyList<ProductionRecord> ByDate(DateTime date)
        => _prodRepo.Where("date = @d ORDER BY equipment_id, shift", new { d = BusinessDate.P(date) });

    public IReadOnlyList<ProductionRecord> InRange(string equipmentId, DateTime start, DateTime end)
        => _prodRepo.Where(
            "equipment_id = @id AND date BETWEEN @s AND @e ORDER BY date, shift",
            new { id = equipmentId, s = BusinessDate.P(start), e = BusinessDate.P(end) });

    public IReadOnlyList<ProductionRecord> All() => _prodRepo.All();

    public CapacityMonthly? GetMonthly(string equipmentId, int year, int month)
        => _capRepo.GetByKey(new { EquipmentId = equipmentId, Year = year, Month = month });

    public IReadOnlyList<CapacityMonthly> MonthlyByYear(string equipmentId, int year)
        => _capRepo.Where("equipment_id = @id AND year = @y ORDER BY month",
            new { id = equipmentId, y = year });

    public IReadOnlyList<CapacityMonthly> MonthlyAll() => _capRepo.All();

    public void Insert(ProductionRecord entity) => _prodRepo.Insert(entity);
    public void Update(ProductionRecord entity) => _prodRepo.Update(entity);
    public void Delete(long id) => _prodRepo.Delete(id);
    public void UpsertMonthly(CapacityMonthly entity) => _capRepo.Upsert(entity);
    public void BulkInsertProduction(IEnumerable<ProductionRecord> entities)
        => _prodRepo.BulkInsert(entities);
}
