// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IRoadCenterlineSetService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 中心线存档持久化服务(V045)：CRUD。RoadLib「中心线管理」的存档页存 / 列表 / 载入 / 改名 / 删。
/// 与 <see cref="IRoadNetworkService"/> 平行：那边存建完网的图，这边存中线几何本身。
/// </summary>
public interface IRoadCenterlineSetService
{
    /// <summary>全部存档(按存档时刻倒序,最新在前)。</summary>
    IReadOnlyList<RoadCenterlineSet> All();

    /// <summary>取一条(含几何 base64)。列表页只用 <see cref="All"/> 的冗余列，载入时才按 id 取。</summary>
    RoadCenterlineSet? Get(long id);

    /// <summary>插入,返回新主键。</summary>
    long Insert(RoadCenterlineSet entity);

    void Update(RoadCenterlineSet entity);

    void Delete(long id);
}
