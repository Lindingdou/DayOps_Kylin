// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/MineableRegionService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary><see cref="IMineableRegionService"/> 实现:通用仓储 CRUD,无业务逻辑。</summary>
internal sealed class MineableRegionService : IMineableRegionService
{
    private readonly IRepository<MineableRegion> _repo;
    private bool _purgedLegacy;   // 旧版 wide_bench 遗留只清一次

    public MineableRegionService(ISqlService sql)
    {
        _repo = sql.Repository<MineableRegion>();
    }

    public IReadOnlyList<MineableRegion> All()
    {
        PurgeLegacyWideBenchOnce();
        return _repo.Where("1=1 ORDER BY id");
    }

    /// <summary>
    /// 一次性清除旧版「达标平盘」(category=wide_bench) 遗留记录。该结果已改为在「采场参数识别」窗口
    /// 内列表维护、不再入库；放在唯一读取入口 All() 首次调用时清，保证所有接口
    /// （窗口 / GetMineableRegions 能力 / 各模块对话框）读到的内容一致。幂等、永不抛。
    /// </summary>
    private void PurgeLegacyWideBenchOnce()
    {
        if (_purgedLegacy) return;
        _purgedLegacy = true;
        try
        {
            foreach (var r in _repo.Where("1=1"))
                if (r.Category == "wide_bench")
                    _repo.Delete(r.Id);
        }
        catch { }
    }

    public MineableRegion? Get(long id) => _repo.GetByKey(id);

    public long Insert(MineableRegion entity) => _repo.Insert(entity);

    public void Update(MineableRegion entity) => _repo.Update(entity);

    public void Delete(long id) => _repo.Delete(id);
}
