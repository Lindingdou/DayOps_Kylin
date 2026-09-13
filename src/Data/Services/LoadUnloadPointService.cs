// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/LoadUnloadPointService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary><see cref="ILoadUnloadPointService"/> 实现:通用仓储 CRUD + 整组替换。</summary>
internal sealed class LoadUnloadPointService : ILoadUnloadPointService
{
    private readonly IRepository<LoadUnloadPoint> _repo;

    public LoadUnloadPointService(ISqlService sql)
    {
        _repo = sql.Repository<LoadUnloadPoint>();
    }

    public IReadOnlyList<LoadUnloadPoint> All() => _repo.Where("1=1 ORDER BY id");

    public LoadUnloadPoint? Get(long id) => _repo.GetByKey(id);

    public long Insert(LoadUnloadPoint entity) => _repo.Insert(entity);

    public void Update(LoadUnloadPoint entity) => _repo.Update(entity);

    public void Delete(long id) => _repo.Delete(id);

    public void ReplaceAll(IEnumerable<LoadUnloadPoint> entities)
    {
        foreach (var e in All()) _repo.Delete(e.Id);
        foreach (var e in entities) { e.Id = 0; _repo.Insert(e); }
    }
}
