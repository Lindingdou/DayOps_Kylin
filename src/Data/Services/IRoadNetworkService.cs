// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IRoadNetworkService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>路网存档持久化服务:CRUD(RoadLib「保存路网」存、「路网存档」管理窗列表/载入/改名/删除)。</summary>
public interface IRoadNetworkService
{
    /// <summary>全部存档(按采集时刻倒序,最新在前)。</summary>
    IReadOnlyList<RoadNetwork> All();

    RoadNetwork? Get(long id);

    /// <summary>插入,返回新主键。</summary>
    long Insert(RoadNetwork entity);

    void Update(RoadNetwork entity);

    void Delete(long id);
}
