// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/HaulRoadService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class HaulRoadService : IHaulRoadService
{
    private readonly ISqlService _sql;
    private readonly IRepository<HaulRoad> _repo;

    public HaulRoadService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<HaulRoad>();
    }

    public HaulRoad? Get(string roadId) => _repo.GetByKey(roadId);
    public IReadOnlyList<HaulRoad> All() => _repo.All();
    public IReadOnlyList<HaulRoad> Active() => _repo.Where("condition != 'closed'");
    public IReadOnlyList<HaulRoad> ByType(string roadType) => _repo.Where("road_type = @t", new { t = roadType });
    public IReadOnlyList<HaulRoad> ByTruckModel(string truckModel) => _repo.Where("primary_truck_model = @m", new { m = truckModel });

    public void Upsert(HaulRoad entity) => _repo.Upsert(entity);
    public void Delete(string roadId) => _repo.Delete(roadId);

    public double TotalNetworkKm()
        => (_sql.ExecuteScalar<double?>("SELECT COALESCE(SUM(length_m), 0) FROM haul_road WHERE condition != 'closed'") ?? 0) / 1000.0;
}
