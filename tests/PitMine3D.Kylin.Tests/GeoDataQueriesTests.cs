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
        Assert.True(r.InService > 0 && r.InService <= r.Total, $"在役 {r.InService}");   // 回归:status 词表在用/租赁, 曾误用在役恒0
        Assert.True(r.InService >= r.Total / 2, "在役应占多数(种子 在用477/518)");
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
    public void Coal_quality_by_seam_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetCoalQualityBySeam(db.Connection);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.Samples > 0));
        Assert.All(rows, r => Assert.InRange(r.AvgAshPct, 0, 100));   // 灰分合理
    }

    [Fact]
    public void Production_by_shift_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetProductionByShift(db.Connection);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.InRange(r.UtilizationPct, 0, 100));
        Assert.Equal(rows.Sum(r => r.Records), (int)db.ScalarLong("SELECT COUNT(*) FROM production_record"));
    }

    [Fact]
    public void Capacity_by_category_shares_sum_to_100()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetCapacityByCategory(db.Connection);
        Assert.NotEmpty(rows);
        for (int i = 1; i < rows.Count; i++) Assert.True(rows[i - 1].TotalOutputM3 >= rows[i].TotalOutputM3);   // 按产量降序
        Assert.Equal(100.0, rows.Sum(r => r.SharePct), 3);                                                      // 占比之和=100
        Assert.All(rows, r => Assert.True(r.Units > 0));
    }

    [Fact]
    public void Kpi_trend_by_year_ratios_normalized()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetKpiTrend(db.Connection);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.InRange(r.AvgAvailabilityPct, 0, 100));   // 比率归一到百分比
        Assert.All(rows, r => Assert.InRange(r.AvgUtilizationPct, 0, 100));
        for (int i = 1; i < rows.Count; i++) Assert.True(rows[i - 1].Year < rows[i].Year);   // 按年升序
    }

    [Fact]
    public void Fault_by_equipment_ranked_by_downtime()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetFaultByEquipment(db.Connection, 8);
        Assert.NotEmpty(rows);
        for (int i = 1; i < rows.Count; i++) Assert.True(rows[i - 1].DowntimeHours >= rows[i].DowntimeHours);   // 停机时降序
    }

    [Fact]
    public void Annual_output_from_seed_sorted_by_year()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetAnnualOutput(db.Connection);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.OutputWanM3 > 0));
        for (int i = 1; i < rows.Count; i++) Assert.True(rows[i - 1].Year < rows[i].Year);   // 按年升序
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
        Assert.True(a.PassPct > 0, "合格率非0(status='pass' 英文枚举, 曾误用中文匹配)");   // 回归:修复恒0 bug
    }

    [Fact]
    public void Acceptance_by_phase_pass_rate_ascending()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetAcceptanceByPhase(db.Connection);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.Passed <= r.Records));
        Assert.All(rows, r => Assert.InRange(r.PassPct, 0, 100));
        for (int i = 1; i < rows.Count; i++) Assert.True(rows[i - 1].PassPct <= rows[i].PassPct);   // 合格率升序(薄弱在前)
    }

    [Fact]
    public void Fault_by_type_downtime_shares_sum_to_100()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetFaultByType(db.Connection);
        Assert.NotEmpty(rows);
        for (int i = 1; i < rows.Count; i++) Assert.True(rows[i - 1].DowntimeHours >= rows[i].DowntimeHours);   // 停机时降序
        Assert.Equal(100.0, rows.Sum(r => r.DowntimeSharePct), 3);
        Assert.Equal(rows.Sum(r => r.Events), (int)db.ScalarLong("SELECT COUNT(*) FROM fault_event"));
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
        Assert.True(f.ActiveEquipment > 1, $"在役台数 {f.ActiveEquipment}(回归:曾恒0被兜底成1台)");   // 种子约485台
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
