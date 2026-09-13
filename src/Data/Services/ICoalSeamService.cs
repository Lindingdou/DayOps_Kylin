// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ICoalSeamService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>钻孔煤层成果（附表2）+ 见煤点（附表3）。</summary>
public interface ICoalSeamService
{
    // ─── borehole_seam_result CRUD ───
    BoreholeSeamResult? GetSeamResult(long id);
    IReadOnlyList<BoreholeSeamResult> SeamResultsByBorehole(long boreholeId);
    IReadOnlyList<BoreholeSeamResult> SeamResultsBySeam(string seamCode);
    IReadOnlyList<BoreholeSeamResult> SeamResultsByStatus(string status);
    long InsertSeamResult(BoreholeSeamResult entity);
    void UpdateSeamResult(BoreholeSeamResult entity);
    void DeleteSeamResult(long id);
    int BulkInsertSeamResults(IEnumerable<BoreholeSeamResult> rows);

    // ─── coal_observation_point CRUD ───
    CoalObservationPoint? GetObservation(long id);
    IReadOnlyList<CoalObservationPoint> ObservationsBySeam(string seamCode);
    IReadOnlyList<CoalObservationPoint> ObservationsInBounds(double xMin, double xMax, double yMin, double yMax);
    long InsertObservation(CoalObservationPoint entity);
    void UpdateObservation(CoalObservationPoint entity);
    void DeleteObservation(long id);
    int BulkInsertObservations(IEnumerable<CoalObservationPoint> rows);

    // ─── 衍生分析 ───
    /// <summary>按煤层统计：样品数/平均厚度/平均底板标高等。</summary>
    IReadOnlyList<SeamStats> StatsBySeam();

    /// <summary>该煤层在所有钻孔中的厚度分布 (用于直方图/箱线图)。</summary>
    IReadOnlyList<double> ThicknessSeries(string seamCode);
}

public sealed record SeamStats(
    string SeamCode,
    int SampleCount,
    double? AvgThickness,
    double? AvgFloorElevation,
    int NormalCount,
    int AbnormalCount);
