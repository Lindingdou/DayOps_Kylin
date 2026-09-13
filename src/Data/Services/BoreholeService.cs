// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/BoreholeService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>钻孔基础信息 + 柱状图所需查询。</summary>
internal sealed class BoreholeService : IBoreholeService
{
    private readonly ISqlService _sql;
    private readonly IRepository<Borehole> _repo;

    public BoreholeService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<Borehole>();
    }

    // ─── 基础 CRUD ───
    public Borehole? GetById(long id) => _repo.GetByKey(id);

    public Borehole? GetByHoleId(string holeId)
        => _repo.Where("hole_id = @h", new { h = holeId }).FirstOrDefault();

    public IReadOnlyList<Borehole> All()
        => _repo.Where("1=1 ORDER BY hole_id");

    public IReadOnlyList<Borehole> ByCategory(string category)
        => _repo.Where("category = @c ORDER BY hole_id", new { c = category });

    public IReadOnlyList<Borehole> InBounds(double xMin, double xMax, double yMin, double yMax)
        => _repo.Where(
            "x BETWEEN @x1 AND @x2 AND y BETWEEN @y1 AND @y2 ORDER BY hole_id",
            new { x1 = xMin, x2 = xMax, y1 = yMin, y2 = yMax });

    public long Insert(Borehole entity) => _repo.Insert(entity);
    public void Update(Borehole entity) => _repo.Update(entity);
    public void Delete(long id) => _repo.Delete(id);

    public int BulkInsert(IEnumerable<Borehole> rows)
    {
        var list = rows as IList<Borehole> ?? rows.ToList();
        _sql.BulkInsert("borehole", list);
        return list.Count;
    }

    // ─── 柱状图所需 ───
    public IReadOnlyList<BoreholeColumnRow> GetColumnRows(long boreholeId)
    {
        const string sql = @"
            SELECT borehole_id     AS BoreholeId,
                   hole_id         AS HoleId,
                   x               AS X,
                   y               AS Y,
                   z_collar        AS ZCollar,
                   depth_total     AS DepthTotal,
                   terminate_horizon AS TerminateHorizon,
                   seam_code       AS SeamCode,
                   seam_name       AS SeamName,
                   seam_color      AS SeamColor,
                   seam_order      AS SeamOrder,
                   log_end_depth   AS LogEndDepth,
                   overall_thickness AS OverallThickness,
                   adopted_thickness AS AdoptedThickness,
                   seam_top_z      AS SeamTopZ,
                   seam_floor_z    AS SeamFloorZ,
                   roof_lithology  AS RoofLithology,
                   floor_lithology AS FloorLithology,
                   overall_rating  AS OverallRating,
                   status          AS Status
              FROM v_borehole_column
             WHERE borehole_id = @id
             ORDER BY seam_order";
        return _sql.Query<BoreholeColumnRow>(sql, new { id = boreholeId }).ToList();
    }

    public IReadOnlyList<BoreholeSegmentRow> GetSegments(long boreholeId)
    {
        const string sql = @"
            SELECT borehole_id  AS BoreholeId,
                   depth_from   AS DepthFrom,
                   depth_to     AS DepthTo,
                   layer_name   AS LayerName,
                   color_hex    AS ColorHex,
                   layer_type   AS LayerType
              FROM v_borehole_segments
             WHERE borehole_id = @id
             ORDER BY depth_from";
        return _sql.Query<BoreholeSegmentRow>(sql, new { id = boreholeId }).ToList();
    }

    public IReadOnlyList<Borehole> WithCoalSamples()
    {
        // 把化验过的孔挑出来（hole_id 在 coal_sample 出现过）
        const string sql = @"
            SELECT bh.*
              FROM borehole bh
             WHERE EXISTS (SELECT 1 FROM coal_sample cs WHERE cs.borehole_id = bh.id)
             ORDER BY bh.hole_id";
        return _sql.Query<Borehole>(sql).ToList();
    }
}
