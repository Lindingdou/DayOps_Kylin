// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/WorkforceService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class WorkforceService : IWorkforceService
{
    private readonly IRepository<WorkforceMonthly> _repo;

    public WorkforceService(ISqlService sql) => _repo = sql.Repository<WorkforceMonthly>();

    public WorkforceMonthly? Get(int year, int month) => _repo.GetByKey(new { Year = year, Month = month });
    public IReadOnlyList<WorkforceMonthly> ByYear(int year) => _repo.Where("year = @y ORDER BY month", new { y = year });
    public IReadOnlyList<WorkforceMonthly> All() => _repo.All();
    public void Upsert(WorkforceMonthly entity) => _repo.Upsert(entity);
}
