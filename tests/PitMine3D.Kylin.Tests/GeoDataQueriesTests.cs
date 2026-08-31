using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>§四/§八 查询分析回归（对 SQLite 种子库）。</summary>
public class GeoDataQueriesTests
{
    [Fact]
    public void Equipment_roster_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var r = GeoDataQueries.GetEquipmentRoster(db.Connection);
        Assert.True(r.Total >= 5, $"设备总数 {r.Total}");
        Assert.NotEmpty(r.ByCategory);
        Assert.Equal(r.Total, Sum(r));               // 分类计数之和 = 总数
    }

    private static int Sum(GeoDataQueries.EquipmentRoster r)
    {
        int s = 0; foreach (var c in r.ByCategory) s += c.Count; return s;
    }

    [Fact]
    public void Production_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var s = GeoDataQueries.GetProductionStats(db.Connection);
        Assert.True(s.Records > 0, "生产记录数");
        Assert.True(s.OutputM3 > 0, "总产量");
        Assert.InRange(s.UtilizationPct, 0, 100);    // 作业率百分比合理
    }

    [Fact]
    public void Capacity_ranking_sorted_desc()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetCapacityRanking(db.Connection, 5);
        Assert.NotEmpty(rows);
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i - 1].TotalOutputM3 >= rows[i].TotalOutputM3, "按产量降序");
    }

    [Fact]
    public void Fault_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var f = GeoDataQueries.GetFaultStats(db.Connection);
        Assert.True(f.Events > 0, "故障事件数");
        Assert.True(f.DowntimeHours >= 0);
        Assert.True(f.Unresolved <= f.Events);           // 未修复 ≤ 总数
    }

    [Fact]
    public void Kpi_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var k = GeoDataQueries.GetKpiStats(db.Connection);
        Assert.True(k.Records > 0, "KPI 记录数");
        Assert.InRange(k.AvgAvailabilityPct, 0, 100);
        Assert.InRange(k.AvgUtilizationPct, 0, 100);
    }

    [Fact]
    public void Borehole_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var b = GeoDataQueries.GetBoreholeStats(db.Connection);
        Assert.True(b.Holes > 100, $"钻孔数 {b.Holes}");     // 种子 241 孔
        Assert.True(b.TotalDepthM > 0);
        Assert.True(b.SeamResults > 0, "见煤结果");
        Assert.NotEmpty(b.ByCategory);
    }

    [Fact]
    public void Coal_quality_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var q = GeoDataQueries.GetCoalQualityStats(db.Connection);
        Assert.True(q.Samples > 100, $"煤样数 {q.Samples}");   // 种子 257 样
        Assert.True(q.Seams > 0, "煤层数");
        Assert.InRange(q.AvgAshPct, 0, 100);                   // 灰分百分比合理
        Assert.True(q.AvgCalorificMJ > 0, "发热量");
    }

    [Fact]
    public void Coal_seams_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var seams = GeoDataQueries.GetCoalSeams(db.Connection);
        Assert.NotEmpty(seams);                                // 种子 7 煤层
    }

    [Fact]
    public void Seam_intersections_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetSeamIntersections(db.Connection);
        Assert.NotEmpty(rows);                                 // borehole_seam_result 778 行
        Assert.All(rows, r => Assert.True(r.Holes > 0));
        Assert.Equal(rows.Sum(r => r.Holes), (int)db.ScalarLong("SELECT COUNT(*) FROM borehole_seam_result WHERE seam_code IS NOT NULL"));
    }

    [Fact]
    public void Dispatch_rules_sorted_by_score()
    {
        using var db = GeoDatabase.OpenSeeded();
        var d = GeoDataQueries.GetDispatchRules(db.Connection, 6);
        Assert.True(d.Active > 0, "在役规则");
        for (int i = 1; i < d.Top.Count; i++)
            Assert.True(d.Top[i - 1].Score >= d.Top[i].Score, "按评分降序");
    }

    [Fact]
    public void Process_architecture_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var p = GeoDataQueries.GetProcessArchitecture(db.Connection);
        Assert.True(p.Systems > 0, "工艺系统");
        Assert.True(p.Phases > 0, "工序");
    }

    [Fact]
    public void Acceptance_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var a = GeoDataQueries.GetAcceptanceStats(db.Connection);
        Assert.True(a.Records > 0, "验收记录");                 // 种子 156
        Assert.InRange(a.PassPct, 0, 100);
    }

    [Fact]
    public void Working_faces_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var f = GeoDataQueries.GetWorkingFaces(db.Connection);
        Assert.NotEmpty(f);                                    // 种子 5 面
    }

    [Fact]
    public void Param_templates_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var p = GeoDataQueries.GetParamTemplates(db.Connection);
        Assert.True(p.Definitions > 0, "参数定义");            // 种子 29
        Assert.True(p.TemplateValues > 0, "模板取值");         // 种子 42
    }

    [Fact]
    public void Monthly_plans_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var plans = GeoDataQueries.GetMonthlyPlans(db.Connection);
        Assert.NotEmpty(plans);
        for (int i = 1; i < plans.Count; i++)                  // 按年月升序
            Assert.True(plans[i - 1].Year * 100 + plans[i - 1].Month <= plans[i].Year * 100 + plans[i].Month);
    }

    [Fact]
    public void Haul_roads_and_slopes_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        Assert.NotEmpty(GeoDataQueries.GetHaulRoads(db.Connection));      // 种子 6
        Assert.NotEmpty(GeoDataQueries.GetSlopeDesigns(db.Connection));   // 种子 4
    }

    [Fact]
    public void Borehole_coords_all_have_xy()
    {
        using var db = GeoDatabase.OpenSeeded();
        var pts = GeoDataQueries.GetBoreholeCoords(db.Connection);
        Assert.True(pts.Count > 100, $"带坐标钻孔 {pts.Count}");   // 种子 241
        Assert.All(pts, p => Assert.NotEqual(0.0, p.x + p.y));    // 坐标非全零
    }

    [Fact]
    public void Fleet_overview_and_coal_class_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var f = GeoDataQueries.GetFleetOverview(db.Connection);
        Assert.True(f.Total >= 5);
        Assert.NotEmpty(f.ByStatus);
        Assert.NotEmpty(GeoDataQueries.GetCoalClassification(db.Connection));   // 种子 16
    }

    [Fact]
    public void Seam_bench_constraints_grades_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        Assert.NotEmpty(GeoDataQueries.GetSeamBenchParams(db.Connection));        // 种子 7
        Assert.True(GeoDataQueries.GetEquipmentConstraints(db.Connection).Total > 0);  // 种子 15
        Assert.NotEmpty(GeoDataQueries.GetCoalGradeRules(db.Connection));         // 种子 15
    }

    [Fact]
    public void Observation_points_and_locations_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var obs = GeoDataQueries.GetObservationPoints(db.Connection);
        Assert.True(obs.Count > 50, $"观测点 {obs.Count}");                        // 种子 119
        Assert.All(obs, p => Assert.NotEqual(0.0, p.x + p.y));
        Assert.NotEmpty(GeoDataQueries.GetMineLocations(db.Connection));           // 种子 10
    }

    [Fact]
    public void Efficiency_forecast_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var f = GeoDataQueries.GetEfficiencyForecast(db.Connection);
        Assert.True(f.BaselineMonthlyWanM3 > 0, "基线月产");
        Assert.InRange(f.AvgAvailabilityPct, 0, 100);
        Assert.True(f.ProjectedAnnualWanM3 > f.BaselineMonthlyWanM3, "投影年产 > 单台月产");
    }

    [Fact]
    public void Export_table_to_csv()
    {
        using var db = GeoDatabase.OpenSeeded();
        string csv = GeoDataQueries.ExportTableToCsv(db.Connection, "equipment_model");
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.True(lines.Length > 5, "表头 + 数据行");                 // 50 型号 + 表头
        Assert.Contains("model", lines[0]);                            // 表头含列名
        Assert.Throws<System.ArgumentException>(() => GeoDataQueries.ExportTableToCsv(db.Connection, "x; DROP TABLE"));  // 防注入
    }
}
