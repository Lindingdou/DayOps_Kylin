// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/FaultService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class FaultService : IFaultService
{
    private readonly ISqlService _sql;
    private readonly IRepository<FaultEvent> _repo;

    public FaultService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<FaultEvent>();
    }

    public FaultEvent? GetById(long id) => _repo.GetByKey(id);

    public IReadOnlyList<FaultEvent> ByEquipment(string equipmentId)
        => _repo.Where("equipment_id = @id ORDER BY date DESC", new { id = equipmentId });

    public IReadOnlyList<FaultEvent> ByDate(DateTime date)
        => _repo.Where("date = @d", new { d = BusinessDate.P(date) });

    public IReadOnlyList<FaultEvent> ByType(string faultType)
        => _repo.Where("fault_type = @t ORDER BY date DESC", new { t = faultType });

    public IReadOnlyList<FaultEvent> InRange(DateTime start, DateTime end)
        => _repo.Where("date BETWEEN @s AND @e ORDER BY date",
            new { s = BusinessDate.P(start), e = BusinessDate.P(end) });

    public IReadOnlyList<FaultEvent> All() => _repo.All();

    public void Insert(FaultEvent entity) => _repo.Insert(entity);
    public void Update(FaultEvent entity) => _repo.Update(entity);
    public void Delete(long id) => _repo.Delete(id);

    public IReadOnlyList<FaultParetoEntry> GetPareto(string? equipmentId = null)
    {
        var where = equipmentId == null ? "" : "WHERE equipment_id = @id";
        var sql = $@"SELECT fault_type AS FaultType,
                            SUM(duration_hours) AS TotalHours,
                            COUNT(*) AS Count
                     FROM fault_event {where}
                     GROUP BY fault_type
                     ORDER BY TotalHours DESC";
        return new List<FaultParetoEntry>(_sql.Query<FaultParetoEntry>(sql, new { id = equipmentId }));
    }
}
