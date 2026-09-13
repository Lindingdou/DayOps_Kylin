// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/RoadCenterlineSetService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary><see cref="IRoadCenterlineSetService"/> 实现:通用仓储 CRUD(同 <see cref="RoadNetworkService"/>)。</summary>
internal sealed class RoadCenterlineSetService : IRoadCenterlineSetService
{
    private readonly IRepository<RoadCenterlineSet> _repo;

    public RoadCenterlineSetService(ISqlService sql)
    {
        _repo = sql.Repository<RoadCenterlineSet>();
    }

    public IReadOnlyList<RoadCenterlineSet> All() => _repo.Where("1=1 ORDER BY captured_at DESC, id DESC");

    public RoadCenterlineSet? Get(long id) => _repo.GetByKey(id);

    public long Insert(RoadCenterlineSet entity) => _repo.Insert(entity);

    public void Update(RoadCenterlineSet entity) => _repo.Update(entity);

    public void Delete(long id) => _repo.Delete(id);
}
