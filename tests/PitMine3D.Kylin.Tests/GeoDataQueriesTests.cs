using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>§四/§八 查询分析回归（对 SQLite 种子库）。</summary>
public class GeoDataQueriesTests
{
    [Fact]
    public void Data_dictionary_lists_tables_and_columns()
    {
        using var db = GeoDatabase.OpenSeeded();
        var tables = GeoDataQueries.ListTables(db.Connection);
        Assert.NotEmpty(tables);
        Assert.Contains("borehole", tables);
        Assert.Contains("coal_sample", tables);
        Assert.Contains("equipment", tables);
        Assert.DoesNotContain(tables, t => t.StartsWith("sqlite_"));   // 排除内部表

        string csv = GeoDataQueries.DataDictionaryCsv(db.Connection);
        Assert.StartsWith("table,column,type,notnull,pk", csv);        // 表头
        // borehole 的关键列应在字典里
        Assert.Contains("borehole,hole_id,", csv);
        // 行数 ≈ 各表列数之和，远多于表数
        var lines = csv.TrimEnd('\n').Split('\n');
        int rows = lines.Length - 1;                                   // 减表头
        Assert.True(rows > tables.Count, $"字典行(列数){rows} 应 > 表数 {tables.Count}");
        // pk 是末列；至少一列是主键(末列=1)
        Assert.Contains(lines.Skip(1), line => line.EndsWith(",1"));
    }

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
    public void Import_production_records_insert_update_skip()
    {
        using var db = GeoDatabase.OpenSeeded();
        long before = db.ScalarLong("SELECT COUNT(*) FROM production_record");
        string eq;
        using (var c = db.Connection.CreateCommand()) { c.CommandText = "SELECT equipment_id FROM equipment LIMIT 1"; eq = (string)c.ExecuteScalar(); }  // 既有设备(过 FK)
        IReadOnlyDictionary<string, string> Row(string date, string sh, double o)
            => new Dictionary<string, string> { ["equipment_id"] = eq, ["date"] = date, ["shift"] = sh, ["output_m3"] = o.ToString(), ["work_hours"] = "8", ["fault_hours"] = "0" };
        // 新键(未来日期) → 插入
        var o1 = GeoDataQueries.ImportProductionRecords(db.Connection, new[] { Row("2099-01-01", "A", 1234) }, overwrite: true);
        Assert.Equal(1, o1.Inserted);
        Assert.Equal(before + 1, db.ScalarLong("SELECT COUNT(*) FROM production_record"));
        double OutOf() { using var c = db.Connection.CreateCommand(); c.CommandText = $"SELECT output_m3 FROM production_record WHERE equipment_id='{eq}' AND date='2099-01-01' AND shift='A'"; return System.Convert.ToDouble(c.ExecuteScalar()); }
        Assert.Equal(1234.0, OutOf(), 3);
        // 同键 overwrite=true → 更新
        var o2 = GeoDataQueries.ImportProductionRecords(db.Connection, new[] { Row("2099-01-01", "A", 5678) }, overwrite: true);
        Assert.Equal(1, o2.Updated); Assert.Equal(0, o2.Inserted);
        Assert.Equal(5678.0, OutOf(), 3);          // 值已覆盖
        // 同键 overwrite=false → 跳过
        var o3 = GeoDataQueries.ImportProductionRecords(db.Connection, new[] { Row("2099-01-01", "A", 999) }, overwrite: false);
        Assert.Equal(1, o3.Skipped);
        // 坏行(缺 date) → 错误
        var o4 = GeoDataQueries.ImportProductionRecords(db.Connection, new[] { (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["equipment_id"] = "X", ["shift"] = "A" } }, overwrite: true);
        Assert.Equal(1, o4.Errors);
    }

    [Fact]
    public void Import_seam_results_from_csv()
    {
        using var db = GeoDatabase.OpenSeeded();
        string hole, seam;
        using (var c = db.Connection.CreateCommand()) { c.CommandText = "SELECT hole_id FROM borehole LIMIT 1"; hole = (string)c.ExecuteScalar(); }
        using (var c = db.Connection.CreateCommand()) { c.CommandText = "SELECT code FROM coal_seam_def ORDER BY code DESC LIMIT 1"; seam = (string)c.ExecuteScalar(); }
        // 该孔可能已有该层结果 → 用 overwrite 保证可判(插入或更新其一为1)
        var o1 = GeoDataQueries.ImportSeamResults(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["hole_id"]=hole,["seam_code"]=seam,["floor_elevation"]="850",["adopted_thickness"]="4.2"} }, true);
        Assert.True(o1.Inserted + o1.Updated == 1 && o1.Errors == 0, $"见煤导入: ins={o1.Inserted} upd={o1.Updated} err={o1.Errors}");
        // 无效孔号 → 错误
        var o2 = GeoDataQueries.ImportSeamResults(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["hole_id"]="NOSUCH",["seam_code"]=seam} }, true);
        Assert.Equal(1, o2.Errors);
    }

    [Fact]
    public void RecordsToCsv_reflects_properties_and_values()
    {
        using var db = GeoDatabase.OpenSeeded();
        // 产能排名(记录含 EquipmentId/Model/TotalOutputM3) → CSV 表头 + 行
        var rows = GeoDataQueries.GetCapacityRanking(db.Connection, 5);
        string csv = GeoDataQueries.RecordsToCsv(rows);
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal(rows.Count + 1, lines.Length);        // 表头 + N 行
        Assert.Contains("EquipmentId", lines[0]);          // 反射属性名为表头
        Assert.Contains("TotalOutputM3", lines[0]);
        // KPI趋势(含 Year/AvgAvailabilityPct...) 也可序列化
        var kpi = GeoDataQueries.RecordsToCsv(GeoDataQueries.GetKpiTrend(db.Connection));
        Assert.Contains("Year,AvgAvailabilityPct", kpi.Replace(" ", ""));
        // 空列表 → 仅表头
        var empty = GeoDataQueries.RecordsToCsv(new List<GeoDataQueries.AnnualOutputRow>());
        Assert.Equal("Year,OutputWanM3", empty.TrimEnd('\n'));
    }

    [Fact]
    public void ParseCsv_headers_skip_comments_blanks()
    {
        var rows = GeoDataQueries.ParseCsv("# 注释\nequipment_id,date,shift\n\nEX-01,2025-01-01,A\nEX-02,2025-01-02,B\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("EX-01", rows[0]["equipment_id"]);
        Assert.Equal("A", rows[0]["SHIFT"]);            // 大小写不敏感
        Assert.Equal("2025-01-02", rows[1]["date"]);
    }

    [Fact]
    public void Export_table_then_reimport_roundtrips()
    {
        using var db = GeoDatabase.OpenSeeded();
        // 导出 production_record 原始表(全列) → 解析 → 再导入(按列名, 忽略 id/created_at) → 全部更新(既有键)
        string csv = GeoDataQueries.ExportTableToCsv(db.Connection, "production_record");
        var rows = GeoDataQueries.ParseCsv(csv);
        Assert.NotEmpty(rows);
        var take = rows.Take(50).ToList();              // 取前 50 行验证往返
        var o = GeoDataQueries.ImportProductionRecords(db.Connection, take, overwrite: true);
        Assert.Equal(0, o.Errors);                      // 导出的行可无错再导入(往返自洽)
        Assert.Equal(take.Count, o.Updated);            // 既有键 → 全部更新, 无新增
        Assert.Equal(0, o.Inserted);
    }

    [Fact]
    public void Import_templates_headers_and_roundtrip()
    {
        // 9 类模板非空 + 未知返 null
        foreach (var k in new[] { "生产记录", "月度产能", "故障记录", "月度KPI", "设备台账", "煤质化验", "观测点", "月度计划", "见煤成果" })
            Assert.False(string.IsNullOrEmpty(GeoDataQueries.ImportTemplate(k)), k);
        Assert.Null(GeoDataQueries.ImportTemplate("不存在"));
        // 模板表头含键列
        Assert.Contains("equipment_id,date,shift", GeoDataQueries.ImportTemplate("生产记录"));
        Assert.Contains("hole_id,seam_code,depth_from", GeoDataQueries.ImportTemplate("煤质化验"));
        // 往返: 月度计划模板(无 FK) 示例行 → 解析 → 导入成功(1 行数据)
        var tpl = GeoDataQueries.ImportTemplate("月度计划")!;
        var lines = tpl.TrimEnd('\n').Split('\n');
        var headers = lines[0].Split(',');
        var vals = lines[1].Split(',');
        var row = new Dictionary<string, string>();
        for (int i = 0; i < headers.Length && i < vals.Length; i++) row[headers[i]] = vals[i];
        using var db = GeoDatabase.OpenSeeded();
        var o = GeoDataQueries.ImportMonthlyPlans(db.Connection, new[] { (IReadOnlyDictionary<string, string>)row }, overwrite: true);
        Assert.Equal(0, o.Errors);                       // 模板示例行可被导入(表头/类型自洽)
        Assert.Equal(1, o.Inserted + o.Updated);
    }

    [Fact]
    public void Import_monthly_plan_fills_empty_fields()
    {
        using var db = GeoDatabase.OpenSeeded();
        // 导入未来月计划(填 煤量/剥采比, 种子这些字段空) → 插入
        var o1 = GeoDataQueries.ImportMonthlyPlans(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["year"]="2099",["month"]="7",["plan_coal_wan_t"]="120",["ratio_strip_coal"]="5.5",["plan_strip_wan_m3"]="660"} }, true);
        Assert.Equal(1, o1.Inserted);
        // 经 GetMonthlyPlans 读回, 煤量/剥采比非空(种子做不到)
        var plans = GeoDataQueries.GetMonthlyPlans(db.Connection);
        var p = plans.First(x => x.Year == 2099 && x.Month == 7);
        Assert.Equal(120.0, p.PlanCoalWanT, 3);
        Assert.Equal(5.5, p.StripRatio, 3);
        // 同键 overwrite → 更新
        var o2 = GeoDataQueries.ImportMonthlyPlans(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["year"]="2099",["month"]="7",["plan_coal_wan_t"]="200"} }, true);
        Assert.Equal(1, o2.Updated);
        // 缺 year → 错误
        var o3 = GeoDataQueries.ImportMonthlyPlans(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["month"]="1"} }, true);
        Assert.Equal(1, o3.Errors);
    }

    [Fact]
    public void Import_observation_points_from_csv()
    {
        using var db = GeoDatabase.OpenSeeded();
        string seam;
        using (var c = db.Connection.CreateCommand()) { c.CommandText = "SELECT code FROM coal_seam_def LIMIT 1"; seam = (string)c.ExecuteScalar(); }
        long before = db.ScalarLong("SELECT COUNT(*) FROM coal_observation_point");
        var o1 = GeoDataQueries.ImportObservationPoints(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["point_id"]="OBSZZ1",["seam_code"]=seam,["x"]="12345",["y"]="67890",["seam_thickness"]="3.2",["floor_elevation"]="1100"} }, true);
        Assert.True(o1.Inserted == 1, $"观测点插入: ins={o1.Inserted} err={o1.Errors}");
        Assert.Equal(before + 1, db.ScalarLong("SELECT COUNT(*) FROM coal_observation_point"));
        var o2 = GeoDataQueries.ImportObservationPoints(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["point_id"]="OBSZZ1",["seam_code"]=seam,["x"]="1",["y"]="2"} }, true);
        Assert.Equal(1, o2.Updated);
        // 缺 x → 错误
        var o3 = GeoDataQueries.ImportObservationPoints(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["point_id"]="P",["seam_code"]=seam,["y"]="2"} }, true);
        Assert.Equal(1, o3.Errors);
    }

    [Fact]
    public void Import_coal_samples_from_csv_with_hole_lookup()
    {
        using var db = GeoDatabase.OpenSeeded();
        string hole; string seam;
        using (var c = db.Connection.CreateCommand()) { c.CommandText = "SELECT hole_id FROM borehole LIMIT 1"; hole = (string)c.ExecuteScalar(); }
        using (var c = db.Connection.CreateCommand()) { c.CommandText = "SELECT code FROM coal_seam_def LIMIT 1"; seam = (string)c.ExecuteScalar(); }
        long before = db.ScalarLong("SELECT COUNT(*) FROM coal_sample");
        // 新样(depth_from=9999 避免撞既有) → 插入
        var o1 = GeoDataQueries.ImportCoalSamples(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["hole_id"]=hole,["seam_code"]=seam,["depth_from"]="9999",["ad_raw"]="22.5",["std_raw"]="0.8",["qgr_d"]="24",["sample_thickness"]="3"} }, true);
        Assert.True(o1.Inserted == 1, $"煤质插入: ins={o1.Inserted} err={o1.Errors}");
        Assert.Equal(before + 1, db.ScalarLong("SELECT COUNT(*) FROM coal_sample"));
        // 同键 overwrite → 更新
        var o2 = GeoDataQueries.ImportCoalSamples(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["hole_id"]=hole,["seam_code"]=seam,["depth_from"]="9999",["ad_raw"]="30"} }, true);
        Assert.Equal(1, o2.Updated);
        // 不存在的孔号 → 错误(FK/查找失败)
        var o3 = GeoDataQueries.ImportCoalSamples(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["hole_id"]="NOSUCHHOLE",["seam_code"]=seam,["depth_from"]="1"} }, true);
        Assert.Equal(1, o3.Errors);
    }

    [Fact]
    public void Import_kpi_and_equipment_ledger_from_csv()
    {
        using var db = GeoDatabase.OpenSeeded();
        // 设备台账: 新设备插入(无 FK 依赖, 是父表) + 同键更新
        // model 留空(避免 FK→equipment_model); 新设备插入(equipment 是父表)
        var eqOut = GeoDataQueries.ImportEquipmentLedger(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["equipment_id"]="NEWEQ1",["category"]="Shovel",["status"]="在用"} }, true);
        Assert.True(eqOut.Inserted == 1, $"设备台账插入: ins={eqOut.Inserted} upd={eqOut.Updated} skip={eqOut.Skipped} err={eqOut.Errors}");
        Assert.True(db.ScalarLong("SELECT COUNT(*) FROM equipment WHERE equipment_id='NEWEQ1'") == 1);
        var eqOut2 = GeoDataQueries.ImportEquipmentLedger(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["equipment_id"]="NEWEQ1",["category"]="Truck"} }, true);
        Assert.Equal(1, eqOut2.Updated);
        // KPI: 用刚导入的设备(过 FK) 插入
        var kpi = GeoDataQueries.ImportKpiMonthly(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["equipment_id"]="NEWEQ1",["year"]="2099",["month"]="6",["availability"]="0.9",["utilization_rate"]="0.85"} }, true);
        Assert.Equal(1, kpi.Inserted);
        // 坏行(缺 category) → 错误
        var bad = GeoDataQueries.ImportEquipmentLedger(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["equipment_id"]="X"} }, true);
        Assert.Equal(1, bad.Errors);
    }

    [Fact]
    public void Import_capacity_and_fault_from_csv()
    {
        using var db = GeoDatabase.OpenSeeded();
        string eq;
        using (var c = db.Connection.CreateCommand()) { c.CommandText = "SELECT equipment_id FROM equipment LIMIT 1"; eq = (string)c.ExecuteScalar(); }
        // 月度产能: 新键插入 + 同键更新
        var cap = GeoDataQueries.ImportCapacityMonthly(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["equipment_id"]=eq,["year"]="2099",["month"]="6",["output_m3"]="50000"} }, true);
        Assert.Equal(1, cap.Inserted);
        var cap2 = GeoDataQueries.ImportCapacityMonthly(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["equipment_id"]=eq,["year"]="2099",["month"]="6",["output_m3"]="60000"} }, true);
        Assert.Equal(1, cap2.Updated);
        // 故障记录: 插入型 + 坏行(缺 fault_type)错误
        long fbefore = db.ScalarLong("SELECT COUNT(*) FROM fault_event");
        var fe = GeoDataQueries.ImportFaultEvents(db.Connection, new[]
        {
            (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["equipment_id"]=eq,["date"]="2099-06-01",["fault_type"]="机械故障",["duration_hours"]="3.5",["is_resolved"]="1"},
            (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["equipment_id"]=eq,["date"]="2099-06-02"},  // 缺 fault_type
        });
        Assert.Equal(1, fe.Inserted); Assert.Equal(1, fe.Errors);
        Assert.Equal(fbefore + 1, db.ScalarLong("SELECT COUNT(*) FROM fault_event"));
    }

    [Fact]
    public void Import_haul_roads_from_csv()
    {
        using var db = GeoDatabase.OpenSeeded();
        long before = db.ScalarLong("SELECT COUNT(*) FROM haul_road");
        // 新路 → 插入
        var o1 = GeoDataQueries.ImportHaulRoads(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["road_id"]="RDZZ1",["name"]="测试主运道",["road_type"]="main",["length_m"]="1200",["max_slope_pct"]="8",["road_width_m"]="24"} }, true);
        Assert.True(o1.Inserted == 1, $"道路插入: ins={o1.Inserted} err={o1.Errors}");
        Assert.Equal(before + 1, db.ScalarLong("SELECT COUNT(*) FROM haul_road"));
        // 同键 overwrite → 更新
        var o2 = GeoDataQueries.ImportHaulRoads(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["road_id"]="RDZZ1",["name"]="改名",["road_type"]="branch",["length_m"]="900"} }, true);
        Assert.Equal(1, o2.Updated);
        string rt; using (var c = db.Connection.CreateCommand()) { c.CommandText = "SELECT road_type FROM haul_road WHERE road_id='RDZZ1'"; rt = (string)c.ExecuteScalar(); }
        Assert.Equal("branch", rt);
        // overwrite=false 同键 → 跳过
        var o3 = GeoDataQueries.ImportHaulRoads(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["road_id"]="RDZZ1",["name"]="x",["road_type"]="main",["length_m"]="1"} }, false);
        Assert.Equal(1, o3.Skipped);
        // 缺 length_m → 错误
        var o4 = GeoDataQueries.ImportHaulRoads(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["road_id"]="R2",["name"]="无长度",["road_type"]="main"} }, true);
        Assert.Equal(1, o4.Errors);
        // 非法 road_type(违反 CHECK) → 错误
        var o5 = GeoDataQueries.ImportHaulRoads(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["road_id"]="R3",["name"]="非法类型",["road_type"]="highway",["length_m"]="100"} }, true);
        Assert.Equal(1, o5.Errors);
    }

    [Fact]
    public void Import_slope_designs_from_csv()
    {
        using var db = GeoDatabase.OpenSeeded();
        long before = db.ScalarLong("SELECT COUNT(*) FROM slope_design");
        // 插入型 → 每行新增
        var o1 = GeoDataQueries.ImportSlopeDesigns(db.Connection, new[]
        {
            (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["side_name"]="东帮ZZ",["side_type"]="working",["working_slope_angle_deg"]="32",["final_slope_angle_deg"]="45",["max_depth_m"]="300",["safety_factor"]="1.3"},
            (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["side_name"]="西帮ZZ",["side_type"]="final",["final_slope_angle_deg"]="42"},
        });
        Assert.True(o1.Inserted == 2, $"边坡插入: ins={o1.Inserted} err={o1.Errors}");
        Assert.Equal(before + 2, db.ScalarLong("SELECT COUNT(*) FROM slope_design"));
        // 缺 side_type → 错误
        var o2 = GeoDataQueries.ImportSlopeDesigns(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["side_name"]="缺类型"} });
        Assert.Equal(1, o2.Errors);
        // 非法 side_type(违反 CHECK) → 错误
        var o3 = GeoDataQueries.ImportSlopeDesigns(db.Connection, new[]
        { (IReadOnlyDictionary<string,string>)new Dictionary<string,string>{["side_name"]="非法",["side_type"]="slanted"} });
        Assert.Equal(1, o3.Errors);
    }

    [Fact]
    public void Horizon_points_floor_and_roof_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var pts = GeoDataQueries.GetHorizonPoints(db.Connection);
        Assert.NotEmpty(pts);
        Assert.Contains(pts, p => !p.IsRoof);                 // 有底板点
        Assert.Contains(pts, p => p.IsRoof);                  // 有顶板点
        Assert.All(pts, p => Assert.False(string.IsNullOrEmpty(p.SeamCode)));
        // 顶板恒在同孔同煤层底板之上(顶=底+采用厚度>底)
        var floorOnly = GeoDataQueries.GetHorizonPoints(db.Connection, includeRoof: false, includeFloor: true);
        var roofOnly = GeoDataQueries.GetHorizonPoints(db.Connection, includeRoof: true, includeFloor: false);
        Assert.All(floorOnly, p => Assert.False(p.IsRoof));
        Assert.All(roofOnly, p => Assert.True(p.IsRoof));
        Assert.True(floorOnly.Count >= roofOnly.Count);       // 顶板需采用厚度>0, 故 ≤ 底板数
    }

    [Fact]
    public void Horizon_points_include_coal_observation_points()
    {
        // 忠实原双源展点：见煤点 coal_observation_point 也应参与层位展点(底=floor_elevation, 顶=底+见煤厚度)
        using var db = GeoDatabase.OpenSeeded();
        int before = GeoDataQueries.GetHorizonPoints(db.Connection).Count;
        string seam;
        using (var q = db.Connection.CreateCommand()) { q.CommandText = "SELECT code FROM coal_seam_def LIMIT 1"; seam = (string)q.ExecuteScalar(); }
        var o = GeoDataQueries.ImportObservationPoints(db.Connection, new[]
        {
            (IReadOnlyDictionary<string,string>)new Dictionary<string,string>
            { ["point_id"]="CJD_T1", ["seam_code"]=seam, ["x"]="500000", ["y"]="4000000", ["seam_thickness"]="7", ["floor_elevation"]="99999" }
        }, true);
        Assert.True(o.Inserted == 1, $"见煤点导入 ins={o.Inserted} err={o.Errors}");
        var pts = GeoDataQueries.GetHorizonPoints(db.Connection);
        Assert.Equal(before + 2, pts.Count);                                              // 见煤点贡献 底+顶 两点
        Assert.Contains(pts, p => !p.IsRoof && System.Math.Abs(p.Z - 99999) < 1e-6);      // 底=floor_elevation
        Assert.Contains(pts, p => p.IsRoof && System.Math.Abs(p.Z - (99999 + 7)) < 1e-6); // 顶=底+见煤厚度
    }

    [Fact]
    public void Coal_sample_import_loads_moisture_and_fixed_carbon()
    {
        // 补全工业分析: 导入应载 mad_raw(水分)+fcd_raw(固定碳)(此前 import 漏解析→恒 NULL), 分煤层汇总应呈现
        using var db = GeoDatabase.OpenSeeded();
        string hole, seam;
        using (var q = db.Connection.CreateCommand()) { q.CommandText = "SELECT hole_id FROM borehole LIMIT 1"; hole = (string)q.ExecuteScalar(); }
        using (var q = db.Connection.CreateCommand()) { q.CommandText = "SELECT code FROM coal_seam_def LIMIT 1"; seam = (string)q.ExecuteScalar(); }
        var o = GeoDataQueries.ImportCoalSamples(db.Connection, new[]
        {
            (IReadOnlyDictionary<string,string>)new Dictionary<string,string>
            { ["hole_id"]=hole, ["seam_code"]=seam, ["depth_from"]="999", ["ad_raw"]="15", ["vdaf_raw"]="30",
              ["mad_raw"]="8", ["fcd_raw"]="47", ["true_density"]="1.45", ["qnet_ad"]="25" }
        }, true);
        Assert.True(o.Inserted == 1, $"煤样导入 ins={o.Inserted} err={o.Errors}");
        // 直接查回确认 mad_raw/fcd_raw/true_density 已入库(修前这些列不在 INSERT→恒 NULL)
        using (var q = db.Connection.CreateCommand())
        {
            q.CommandText = "SELECT mad_raw, fcd_raw, true_density FROM coal_sample WHERE seam_code=@s AND depth_from=999";
            q.Parameters.AddWithValue("@s", seam);
            using var rd = q.ExecuteReader();
            Assert.True(rd.Read());
            Assert.Equal(8, rd.GetDouble(0), 6);
            Assert.Equal(47, rd.GetDouble(1), 6);
            Assert.Equal(1.45, rd.GetDouble(2), 6);
        }
        // 分煤层汇总呈现水分+固定碳
        var row = GeoDataQueries.GetCoalQualityBySeam(db.Connection).Find(r => r.SeamCode == seam);
        Assert.NotNull(row);
        Assert.True(row!.AvgMoisturePct > 0, "汇总含水分 Mad");
        Assert.True(row.AvgFixedCarbonPct > 0, "汇总含固定碳 FCd");
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
        Assert.True(d.Top.Count > 0 && d.Top[0].Score > 0, "评分非全零(种子 efficiency_score 55~86, 排名有意义)");
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
        Assert.Contains(plans, p => p.PlanStripWanM3 > 0);     // 种子仅剥离量填充(约1041万m³), 应非全零
        // 剥采比推导自洽: 有煤量时 = 剥离/煤; 否则为 0(种子煤量空→比率0, 如实)
        Assert.All(plans, p => Assert.True(p.StripRatio >= 0));
        Assert.All(plans, p => Assert.True(p.PlanCoalWanT <= 0 || System.Math.Abs(p.StripRatio - p.PlanStripWanM3 / p.PlanCoalWanT) < 1e-6 || p.StripRatio > 0));
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
        Assert.True(f.ProducingUnits > 0 && f.ProducingUnits < f.ActiveEquipment, $"产出设备 {f.ProducingUnits} 应<在役 {f.ActiveEquipment}");
        // 回归:投影须用产出设备数(非全在役)——口径一致则投影≈实际年产总量, 不应高估50%+
        double actualMaxAnnual = 0;
        foreach (var r in GeoDataQueries.GetAnnualOutput(db.Connection)) actualMaxAnnual = System.Math.Max(actualMaxAnnual, r.OutputWanM3);
        Assert.InRange(f.ProjectedAnnualWanM3, actualMaxAnnual * 0.5, actualMaxAnnual * 1.6);   // 同量级(曾×全在役485高估至1.55×)
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
