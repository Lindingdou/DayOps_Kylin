// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/DailyMineService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class DailyMineService : IDailyMineService
{
    private readonly IRepository<DailyMineSummary> _repo;

    public DailyMineService(ISqlService sql) => _repo = sql.Repository<DailyMineSummary>();

    // 主键也是业务日期列 ⇒ 同样得按 yyyy-MM-dd 传（见 BusinessDate）
    public DailyMineSummary? Get(DateTime date) => _repo.GetByKey(BusinessDate.P(date));

    public IReadOnlyList<DailyMineSummary> InRange(DateTime start, DateTime end)
        => _repo.Where("date BETWEEN @s AND @e ORDER BY date",
            new { s = BusinessDate.P(start), e = BusinessDate.P(end) });

    public IReadOnlyList<DailyMineSummary> All() => _repo.All();
    public void Upsert(DailyMineSummary entity) => _repo.Upsert(entity);
}
