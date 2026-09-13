// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/DispatchRuleService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class DispatchRuleService : IDispatchRuleService
{
    private readonly IRepository<DispatchRule> _repo;

    public DispatchRuleService(ISqlService sql) => _repo = sql.Repository<DispatchRule>();

    public DispatchRule? GetById(long id) => _repo.GetByKey(id);

    public IReadOnlyList<DispatchRule> All(bool activeOnly = true)
        => activeOnly ? _repo.Where("is_active = 1") : _repo.All();

    public IReadOnlyList<DispatchRule> ByShovelModel(string shovelModel)
        => _repo.Where("shovel_model = @m AND is_active = 1", new { m = shovelModel });

    public IReadOnlyList<DispatchRule> ByTruckModel(string truckModel)
        => _repo.Where("truck_model = @m AND is_active = 1", new { m = truckModel });

    public void Insert(DispatchRule entity) => _repo.Insert(entity);
    public void Update(DispatchRule entity) => _repo.Update(entity);
    public void Delete(long id) => _repo.Delete(id);
}
