// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/CoalSeamService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 钻孔煤层成果（附表2）+ 见煤点（附表3）。
/// 把两类"煤层观察"放一个 Service 里，统计窗体可一站取数。
/// </summary>
internal sealed class CoalSeamService : ICoalSeamService
{
    private readonly ISqlService _sql;
    private readonly IRepository<BoreholeSeamResult> _seamRepo;
    private readonly IRepository<CoalObservationPoint> _pointRepo;

    public CoalSeamService(ISqlService sql)
    {
        _sql = sql;
        _seamRepo = sql.Repository<BoreholeSeamResult>();
        _pointRepo = sql.Repository<CoalObservationPoint>();
    }

    // ─── borehole_seam_result ───
    public BoreholeSeamResult? GetSeamResult(long id) => _seamRepo.GetByKey(id);

    public IReadOnlyList<BoreholeSeamResult> SeamResultsByBorehole(long boreholeId)
        => _seamRepo.Where("borehole_id = @id", new { id = boreholeId });

    public IReadOnlyList<BoreholeSeamResult> SeamResultsBySeam(string seamCode)
        => _seamRepo.Where("seam_code = @c ORDER BY borehole_id", new { c = seamCode });

    public IReadOnlyList<BoreholeSeamResult> SeamResultsByStatus(string status)
        => _seamRepo.Where("status = @s", new { s = status });

    public long InsertSeamResult(BoreholeSeamResult entity) => _seamRepo.Insert(entity);
    public void UpdateSeamResult(BoreholeSeamResult entity) => _seamRepo.Update(entity);
    public void DeleteSeamResult(long id) => _seamRepo.Delete(id);

    public int BulkInsertSeamResults(IEnumerable<BoreholeSeamResult> rows)
    {
        var list = rows as IList<BoreholeSeamResult> ?? rows.ToList();
        _sql.BulkInsert("borehole_seam_result", list);
        return list.Count;
    }

    // ─── coal_observation_point ───
    public CoalObservationPoint? GetObservation(long id) => _pointRepo.GetByKey(id);

    public IReadOnlyList<CoalObservationPoint> ObservationsBySeam(string seamCode)
        => _pointRepo.Where("seam_code = @c ORDER BY point_id", new { c = seamCode });

    public IReadOnlyList<CoalObservationPoint> ObservationsInBounds(
        double xMin, double xMax, double yMin, double yMax)
        => _pointRepo.Where(
            "x BETWEEN @x1 AND @x2 AND y BETWEEN @y1 AND @y2 ORDER BY point_id",
            new { x1 = xMin, x2 = xMax, y1 = yMin, y2 = yMax });

    public long InsertObservation(CoalObservationPoint entity) => _pointRepo.Insert(entity);
    public void UpdateObservation(CoalObservationPoint entity) => _pointRepo.Update(entity);
    public void DeleteObservation(long id) => _pointRepo.Delete(id);

    public int BulkInsertObservations(IEnumerable<CoalObservationPoint> rows)
    {
        var list = rows as IList<CoalObservationPoint> ?? rows.ToList();
        _sql.BulkInsert("coal_observation_point", list);
        return list.Count;
    }

    // ─── 衍生统计 ───
    public IReadOnlyList<SeamStats> StatsBySeam()
    {
        const string sql = @"
            SELECT seam_code                                    AS SeamCode,
                   COUNT(*)                                     AS SampleCount,
                   AVG(overall_thickness)                       AS AvgThickness,
                   AVG(floor_elevation)                         AS AvgFloorElevation,
                   SUM(CASE WHEN status = '正常' THEN 1 ELSE 0 END) AS NormalCount,
                   SUM(CASE WHEN status != '正常' THEN 1 ELSE 0 END) AS AbnormalCount
              FROM borehole_seam_result
             GROUP BY seam_code
             ORDER BY seam_code";
        return _sql.Query<SeamStats>(sql).ToList();
    }

    public IReadOnlyList<double> ThicknessSeries(string seamCode)
    {
        const string sql = @"
            SELECT overall_thickness
              FROM borehole_seam_result
             WHERE seam_code = @c
               AND status = '正常'
               AND overall_thickness IS NOT NULL";
        return _sql.Query<double>(sql, new { c = seamCode }).ToList();
    }
}
