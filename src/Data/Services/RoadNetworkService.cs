// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/RoadNetworkService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary><see cref="IRoadNetworkService"/> 实现:通用仓储 CRUD。</summary>
internal sealed class RoadNetworkService : IRoadNetworkService
{
    private readonly IRepository<RoadNetwork> _repo;

    public RoadNetworkService(ISqlService sql)
    {
        _repo = sql.Repository<RoadNetwork>();
    }

    public IReadOnlyList<RoadNetwork> All() => _repo.Where("1=1 ORDER BY captured_at DESC, id DESC");

    public RoadNetwork? Get(long id) => _repo.GetByKey(id);

    public long Insert(RoadNetwork entity) => _repo.Insert(entity);

    public void Update(RoadNetwork entity) => _repo.Update(entity);

    public void Delete(long id) => _repo.Delete(id);
}
