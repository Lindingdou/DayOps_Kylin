// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/BlastService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class BlastService : IBlastService
{
    private readonly ISqlService _sql;
    private readonly IRepository<BlastEvent> _repo;

    public BlastService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<BlastEvent>();
    }

    public BlastEvent? GetById(long id) => _repo.GetByKey(id);
    public IReadOnlyList<BlastEvent> ByDate(DateTime date)
        => _repo.Where("blast_date = @d", new { d = BusinessDate.P(date) });
    public IReadOnlyList<BlastEvent> InRange(DateTime start, DateTime end)
        => _repo.Where("blast_date BETWEEN @s AND @e ORDER BY blast_date, blast_seq",
            new { s = BusinessDate.P(start), e = BusinessDate.P(end) });
    public IReadOnlyList<BlastEvent> ByDrill(string drillId)
        => _repo.Where("drill_id = @id ORDER BY blast_date", new { id = drillId });
    public IReadOnlyList<BlastEvent> ByLocation(string locationCode)
        => _repo.Where("location_code = @c ORDER BY blast_date", new { c = locationCode });
    public IReadOnlyList<BlastEvent> All() => _repo.All();

    public void Insert(BlastEvent entity) => _repo.Insert(entity);
    public void Update(BlastEvent entity) => _repo.Update(entity);
    public void Delete(long id) => _repo.Delete(id);

    public IReadOnlyList<BlastMonthlyAggregate> GetMonthlyAggregate(int year)
    {
        var sql = @"SELECT CAST(SUBSTR(blast_date, 1, 4) AS INTEGER)  AS Year,
                          CAST(SUBSTR(blast_date, 6, 2) AS INTEGER)  AS Month,
                          COALESCE(SUM(blast_volume_m3), 0)          AS TotalVolumeM3,
                          COALESCE(SUM(explosive_kg), 0)             AS TotalExplosiveKg,
                          COALESCE(AVG(unit_consumption_kg_m3), 0)   AS AvgUnitConsumptionKgM3,
                          COUNT(*)                                   AS EventCount
                    FROM blast_event
                    WHERE blast_date LIKE @yr
                    GROUP BY Year, Month
                    ORDER BY Year, Month";
        return new List<BlastMonthlyAggregate>(_sql.Query<BlastMonthlyAggregate>(sql, new { yr = year + "%" }));
    }
}
