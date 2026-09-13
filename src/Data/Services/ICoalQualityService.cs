// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ICoalQualityService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;
using CoalSample = PitMine3D.Kylin.Data.Entities.CoalSample;   // 与外层 Data.CoalAnalytics 的同名 record 消歧（须放在命名空间内才盖得过外层）

/// <summary>煤芯煤样化验数据 + 衍生统计。</summary>
public interface ICoalQualityService
{
    // ─── coal_sample CRUD ───
    CoalSample? GetSample(long id);
    IReadOnlyList<CoalSample> SamplesByBorehole(long boreholeId);
    IReadOnlyList<CoalSample> SamplesBySeam(string seamCode);
    IReadOnlyList<CoalSample> SamplesByCoalType(string coalType);
    IReadOnlyList<CoalSample> All();

    long InsertSample(CoalSample entity);
    void UpdateSample(CoalSample entity);
    void DeleteSample(long id);
    int BulkInsertSamples(IEnumerable<CoalSample> rows);

    // ─── 3D 视图 (映射 v_coal_sample_3d) ───
    IReadOnlyList<CoalSample3DRow> Get3DPoints(string? seamCode = null);

    // ─── 衍生统计 ───
    /// <summary>按煤层算 Ad/Vdaf/S/Q 等均值与标准差。</summary>
    IReadOnlyList<QualityStats> StatsBySeam(string? indicator = null);

    /// <summary>重新计算并写入 coal_sample_summary。</summary>
    int RebuildSummary();

    IReadOnlyList<CoalSampleSummary> AllSummary();

    // ─── 衍生计算 (单条) ───
    string? ClassifyByGB5751(double? vdaf, double? gIndex, double? plasticY);
    string GradeAsh(double? ad);
    string GradeSulfur(double? std);
    string GradeQnet(double? qnet);
}

/// <summary>v_coal_sample_3d 视图行。</summary>
public sealed record CoalSample3DRow(
    long Id,
    long BoreholeId,
    string HoleId,
    string SeamCode,
    double X, double Y,
    double? ZSample,
    double? DepthFrom, double? DepthTo,
    double? SampleThickness,
    double? AdRaw, double? AdClean,
    double? VdafRaw, double? VdafClean,
    double? StdRaw, double? StdClean,
    double? QgrD, double? QnetAd,
    double? CakingG, double? PlasticYMm,
    string? CoalType,
    string? CoalTypeName);

/// <summary>分组统计结果。</summary>
public sealed record QualityStats(
    string SeamCode,
    string Indicator,
    int Count,
    double? Mean,
    double? Std,
    double? Min,
    double? Max,
    double? P25,
    double? P50,
    double? P75);
