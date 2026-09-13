// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IBoreholeService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 钻孔基础信息与柱状图所需查询。
/// 数据源：附表1 + 附表2 + borehole_lithology_segment 综合。
/// </summary>
public interface IBoreholeService
{
    // ─── 基础 CRUD ───
    Borehole? GetById(long id);
    Borehole? GetByHoleId(string holeId);
    IReadOnlyList<Borehole> All();
    IReadOnlyList<Borehole> ByCategory(string category);
    IReadOnlyList<Borehole> InBounds(double xMin, double xMax, double yMin, double yMax);

    long Insert(Borehole entity);
    void Update(Borehole entity);
    void Delete(long id);
    int BulkInsert(IEnumerable<Borehole> rows);

    // ─── 柱状图所需 ───
    /// <summary>
    /// 一孔多层成果（按煤层排序）。柱状图按 (seam_top_z → seam_floor_z) 画段。
    /// </summary>
    IReadOnlyList<BoreholeColumnRow> GetColumnRows(long boreholeId);

    /// <summary>
    /// 一孔所有岩性分层段 + 煤层段 (按深度合并 union)。
    /// </summary>
    IReadOnlyList<BoreholeSegmentRow> GetSegments(long boreholeId);

    /// <summary>
    /// 化验孔列表 (只列在 coal_sample 中出现过的孔, 用于柱状图窗口的左侧"已化验"过滤)。
    /// </summary>
    IReadOnlyList<Borehole> WithCoalSamples();
}

/// <summary>柱状图原始行 (映射 v_borehole_column 视图)。</summary>
public sealed record BoreholeColumnRow(
    long BoreholeId,
    string HoleId,
    double X, double Y,
    double? ZCollar,
    double? DepthTotal,
    string? TerminateHorizon,
    string? SeamCode,
    string? SeamName,
    string? SeamColor,
    int? SeamOrder,
    double? LogEndDepth,
    double? OverallThickness,
    double? AdoptedThickness,
    double? SeamTopZ,
    double? SeamFloorZ,
    string? RoofLithology,
    string? FloorLithology,
    string? OverallRating,
    string? Status);

/// <summary>柱状图段序列行 (映射 v_borehole_segments 视图)。</summary>
public sealed record BoreholeSegmentRow(
    long BoreholeId,
    double DepthFrom,
    double DepthTo,
    string LayerName,
    string? ColorHex,
    string LayerType);   // "岩性" / "煤层"
