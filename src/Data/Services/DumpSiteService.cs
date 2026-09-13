// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/DumpSiteService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class DumpSiteService : IDumpSiteService
{
    private readonly ISqlService _sql;
    private readonly IRepository<DumpSite> _repo;

    public DumpSiteService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<DumpSite>();
    }

    public DumpSite? Get(string dumpId) => _repo.GetByKey(dumpId);

    public IReadOnlyList<DumpSite> All(bool activeOnly = true)
        => activeOnly ? _repo.Where("status = 'active'") : _repo.All();

    public IReadOnlyList<DumpSite> Internal() => _repo.Where("dump_type = 'internal'");
    public IReadOnlyList<DumpSite> External() => _repo.Where("dump_type = 'external'");

    public void Upsert(DumpSite entity) => _repo.Upsert(entity);
    public void Delete(string dumpId) => _repo.Delete(dumpId);

    // 定向 UPDATE:只动指定列。整行 Upsert 会重写所有列(含带外键的 responsible_dozer_id),
    // 那一行但凡原本就有悬空外键,改任何无关字段都会 FOREIGN KEY constraint failed。
    public bool UpdateDesignCapacity(string dumpId, double wanM3)
    {
        if (string.IsNullOrEmpty(dumpId)) return false;
        return _sql.Execute(
            "UPDATE dump_site SET design_capacity_wan_m3 = @v, updated_at = datetime('now','localtime') "
            + "WHERE dump_id = @id",
            new { v = wanM3, id = dumpId }) > 0;
    }

    public bool UpdateSlopeParams(string dumpId, double benchHeightM, double benchSlopeDeg, double overallSlopeDeg)
    {
        if (string.IsNullOrEmpty(dumpId)) return false;
        return _sql.Execute(
            "UPDATE dump_site SET bench_height_m = @h, bench_slope_angle_deg = @a, "
            + "overall_slope_angle_deg = @o, updated_at = datetime('now','localtime') "
            + "WHERE dump_id = @id",
            new { h = benchHeightM, a = benchSlopeDeg, o = overallSlopeDeg, id = dumpId }) > 0;
    }

    public double AddFilledVolume(string dumpId, double wanM3)
    {
        var d = _repo.GetByKey(dumpId);
        if (d == null) return 0;
        d.CurrentFilledWanM3 += wanM3;
        _repo.Update(d);
        return d.FillRate;
    }
}
