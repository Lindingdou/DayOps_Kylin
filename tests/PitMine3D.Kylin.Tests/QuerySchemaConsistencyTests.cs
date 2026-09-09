using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 全部 conn-only GeoDataQueries 查询 对真实迁移+种子库 schema 一致性守卫 ——
/// 逐一执行, 验列名/表名与迁移一致(任一列/表不符会抛 no such column/table)。
/// 手写内存 schema 测只验逻辑, 测不到迁移漂移; 此测覆盖全 45 个 conn-only 查询。
/// </summary>
public class QuerySchemaConsistencyTests
{
    [Fact]
    public void All_conn_only_queries_run_against_real_migrated_schema()
    {
        using var db = TestDb.Open();
        var c = db.Connection;
        Assert.Null(Record.Exception(() => GeoDataQueries.GetAcceptanceByPhase(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetAcceptanceStats(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetAnnualOutput(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetAshVerticalTrend(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetBlastStats(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetBoreholeCoords(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetBoreholeStats(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCapacityByCategory(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalClassification(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalClassificationRanges(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalDataHealth(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalGradeRules(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalQualityBySeam(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalQualityStats(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalSamples(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalSeams(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalTypeDistribution(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetDrillLogRows(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetEfficiencyForecast(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetEquipmentConstraints(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetEquipmentFactorRows(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetEquipmentRoster(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetFaultByType(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetFaultShare(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetFaultStats(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetFleetCockpit(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetFleetDispatchRules(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetFleetOverview(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetHaulRoads(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetKpiByModel(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetKpiStats(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetKpiTrend(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetMineLocations(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetMonthlyOutputSeries(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetMonthlyPlans(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetObservationPoints(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetParamTemplates(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetProcessArchitecture(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetProductionByShift(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetProductionStats(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetProximateRows(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetSeamBenchParams(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetSeamIntersections(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetSlopeDesigns(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetWorkingFaces(c)));
    }
}
