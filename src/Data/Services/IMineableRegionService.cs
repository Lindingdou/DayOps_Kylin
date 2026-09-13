// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IMineableRegionService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>可采区域边界服务:命名采场可采区域多边形的 CRUD（短期「采场/排土场圈定」用）。</summary>
public interface IMineableRegionService
{
    /// <summary>全部可采区域(按 id 升序)。</summary>
    IReadOnlyList<MineableRegion> All();

    MineableRegion? Get(long id);

    /// <summary>插入,返回新主键。</summary>
    long Insert(MineableRegion entity);

    void Update(MineableRegion entity);

    void Delete(long id);
}
