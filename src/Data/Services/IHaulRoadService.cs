// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IHaulRoadService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>运输道路网络管理。</summary>
public interface IHaulRoadService
{
    HaulRoad? Get(string roadId);
    IReadOnlyList<HaulRoad> All();
    IReadOnlyList<HaulRoad> Active();   // condition != 'closed'
    IReadOnlyList<HaulRoad> ByType(string roadType);
    IReadOnlyList<HaulRoad> ByTruckModel(string truckModel);

    void Upsert(HaulRoad entity);
    void Delete(string roadId);

    /// <summary>道路网络总长度(km)。</summary>
    double TotalNetworkKm();
}
