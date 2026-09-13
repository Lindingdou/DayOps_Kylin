// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/SlopeDesignService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class SlopeDesignService : ISlopeDesignService
{
    private readonly IRepository<SlopeDesign> _repo;

    public SlopeDesignService(ISqlService sql) => _repo = sql.Repository<SlopeDesign>();

    public SlopeDesign? Get(long id) => _repo.GetByKey(id);
    public IReadOnlyList<SlopeDesign> All() => _repo.All();

    public IReadOnlyList<SlopeDesign> CurrentDesigns()
        => _repo.Where("effective_to IS NULL OR effective_to >= date('now') ORDER BY side_name");

    public IReadOnlyList<SlopeDesign> BySide(string sideName)
        => _repo.Where("side_name = @s ORDER BY effective_from DESC", new { s = sideName });

    public long Insert(SlopeDesign entity) => _repo.Insert(entity);
    public void Update(SlopeDesign entity) => _repo.Update(entity);
    public void Delete(long id) => _repo.Delete(id);
}
