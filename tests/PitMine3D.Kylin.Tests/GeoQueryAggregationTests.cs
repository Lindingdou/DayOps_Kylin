using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Data;
using Xunit;

/// <summary>
/// GeoDataQueries 聚合查询（GetFaultShare 故障占比 / GetMonthlyOutputSeries 月产量序列）已知值回归。
/// 这些 SQL 聚合喂效能模拟(§257)/时序预测——列名或聚合写错会静默污染下游, 故直测。
/// </summary>
public class GeoQueryAggregationTests
{
    private static SqliteConnection Db(string ddl)
    {
        var c = new SqliteConnection("Data Source=:memory:"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = ddl; cmd.ExecuteNonQuery();
        return c;
    }
    private static void Exec(SqliteConnection c, string sql)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    [Fact]
    public void GetFaultShare_is_sum_fault_over_sum_plan()
    {
        using var c = Db("CREATE TABLE equipment_kpi_monthly(fault_hours REAL, plan_hours REAL);");
        Exec(c, "INSERT INTO equipment_kpi_monthly(fault_hours,plan_hours) VALUES (10,100),(20,100);");
        // ΣF=30, ΣP=200 → 0.15
        Assert.Equal(0.15, GeoDataQueries.GetFaultShare(c), 6);
    }

    [Fact]
    public void GetFaultShare_empty_falls_back_to_default()
    {
        using var c = Db("CREATE TABLE equipment_kpi_monthly(fault_hours REAL, plan_hours REAL);");
        Assert.Equal(0.1, GeoDataQueries.GetFaultShare(c), 6);   // ΣP≈0 → 兜底 0.1
    }

    [Fact]
    public void GetMonthlyOutputSeries_groups_by_month_scaled_to_wan()
    {
        using var c = Db("CREATE TABLE capacity_monthly(year INT, month INT, output_m3 REAL);");
        // (2023,1): 10000+5000=15000 → 1.5万; (2023,2): 20000 → 2.0万; 按 年,月 升序。
        Exec(c, "INSERT INTO capacity_monthly(year,month,output_m3) VALUES (2023,1,10000),(2023,2,20000),(2023,1,5000);");
        var s = GeoDataQueries.GetMonthlyOutputSeries(c);
        Assert.Equal(2, s.Count);
        Assert.Equal(1.5, s[0], 6);
        Assert.Equal(2.0, s[1], 6);
    }

    [Fact]
    public void GetEquipmentFactorRows_joins_kpi_capacity_on_eq_year_month()
    {
        using var c = Db(@"CREATE TABLE equipment_kpi_monthly(equipment_id INT, year INT, month INT,
            availability REAL, actual_run_rate REAL, utilization_rate REAL,
            internal_fault_rate_pct REAL, external_fault_rate_pct REAL);
            CREATE TABLE capacity_monthly(equipment_id INT, year INT, month INT, output_m3 REAL);");
        Exec(c, "INSERT INTO equipment_kpi_monthly VALUES (1,2023,1, 0.9,0.8,0.85, 2.0,1.0);");
        // 同设备同年月才 join；m=2 无 KPI 匹配应被排除。
        Exec(c, "INSERT INTO capacity_monthly VALUES (1,2023,1,5000),(1,2023,2,6000);");
        var rows = GeoDataQueries.GetEquipmentFactorRows(c);
        var r = Assert.Single(rows);            // 仅 (1,2023,1) 对齐
        Assert.Equal(0.9, r.Availability, 6);
        Assert.Equal(0.8, r.RunRate, 6);
        Assert.Equal(0.85, r.Utilization, 6);
        Assert.Equal(2.0, r.InternalFaultPct, 6);
        Assert.Equal(1.0, r.ExternalFaultPct, 6);
        Assert.Equal(5000, r.Output, 6);        // 列映射正确(c.output_m3 → Output)
    }

    [Fact]
    public void GetCoalClassificationRanges_maps_columns_orders_and_handles_nulls()
    {
        using var c = Db(@"CREATE TABLE coal_classification(code TEXT, vdaf_min REAL, vdaf_max REAL,
            g_min REAL, g_max REAL, y_min REAL, y_max REAL, sort_order INT);");
        // 乱序插入验 ORDER BY sort_order; y_min 留 NULL 验空处理; 列顺序验映射不错位(GB5751 分类正确性所系)。
        Exec(c, "INSERT INTO coal_classification VALUES ('气煤',28,37,35,100,NULL,NULL,2);");
        Exec(c, "INSERT INTO coal_classification VALUES ('焦煤',10,28,50,100,NULL,25,1);");
        var r = GeoDataQueries.GetCoalClassificationRanges(c);
        Assert.Equal(2, r.Count);
        Assert.Equal("焦煤", r[0].Code);        // sort_order 1 在前
        Assert.Equal(10.0, r[0].VdafMin!.Value, 6);
        Assert.Equal(28.0, r[0].VdafMax!.Value, 6);
        Assert.Equal(50.0, r[0].GMin!.Value, 6);
        Assert.Equal(100.0, r[0].GMax!.Value, 6);
        Assert.Null(r[0].YMin);                 // NULL → null
        Assert.Equal(25.0, r[0].YMax!.Value, 6);
        Assert.Equal("气煤", r[1].Code);
        Assert.Null(r[1].YMax);
    }

    [Fact]
    public void GetProximateRows_left_joins_borehole_and_maps_proximate()
    {
        using var c = Db(@"CREATE TABLE coal_sample(id INT, borehole_id INT, seam_code TEXT,
            mad_raw REAL, ad_raw REAL, vdaf_raw REAL, fcd_raw REAL);
            CREATE TABLE borehole(id INT, hole_id TEXT);");
        Exec(c, "INSERT INTO borehole VALUES (1,'ZK01');");
        Exec(c, "INSERT INTO coal_sample VALUES (10,1,'M1', 1.5,15.0,30.0,53.5);");   // 有孔
        Exec(c, "INSERT INTO coal_sample VALUES (11,99,'M2', 2.0,20.0,28.0,50.0);");  // borehole_id 无匹配 → LEFT JOIN 保留
        var r = GeoDataQueries.GetProximateRows(c);
        Assert.Equal(2, r.Count);
        var m1 = Assert.Single(r, x => x.SeamCode == "M1");
        Assert.Equal("ZK01", m1.HoleId);
        Assert.Equal(1.5, m1.Mad!.Value, 6);
        Assert.Equal(15.0, m1.Ad!.Value, 6);      // 灰分
        Assert.Equal(30.0, m1.Vdaf!.Value, 6);    // 挥发分(未与灰分错位)
        Assert.Equal(53.5, m1.Fcd!.Value, 6);
        var m2 = Assert.Single(r, x => x.SeamCode == "M2");
        Assert.Equal("", m2.HoleId);              // LEFT JOIN 无孔 → COALESCE ''
    }

    [Fact]
    public void GetDrillLogRows_maps_drill_and_log_thickness_not_swapped()
    {
        using var c = Db(@"CREATE TABLE borehole_seam_result(borehole_id INT, seam_code TEXT,
            drill_seam_thickness REAL, log_seam_thickness REAL);
            CREATE TABLE borehole(id INT, hole_id TEXT);");
        Exec(c, "INSERT INTO borehole VALUES (1,'ZK01');");
        Exec(c, "INSERT INTO borehole_seam_result VALUES (1,'M1', 3.2, 3.5);");   // 钻厚3.2/测厚3.5
        var r = Assert.Single(GeoDataQueries.GetDrillLogRows(c));
        Assert.Equal("ZK01", r.HoleId);
        Assert.Equal("M1", r.SeamCode);
        Assert.Equal(3.2, r.DrillThicknessM!.Value, 6);   // 钻厚(未与测厚错位——测井一致检查所系)
        Assert.Equal(3.5, r.LogThicknessM!.Value, 6);     // 测厚
    }

    [Fact]
    public void Queries_run_against_real_migrated_schema()
    {
        // 上面各测用手写内存 schema, 验的是查询逻辑; 此测对真实迁移 schema 跑同批查询,
        // 验列名/表名与迁移一致(迁移与查询列名不符会在此抛 "no such column/table")。
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        Assert.Null(Record.Exception(() => GeoDataQueries.GetFaultShare(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetMonthlyOutputSeries(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetEquipmentFactorRows(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetCoalClassificationRanges(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetProximateRows(c)));
        Assert.Null(Record.Exception(() => GeoDataQueries.GetDrillLogRows(c)));
    }

    [Fact]
    public void Imports_insert_into_real_migrated_schema()
    {
        // 手写 schema 的导入测只验解析逻辑; 此测对真实迁移库插入, 验 INSERT 列名与迁移一致
        // (列不符则 Import 内 try/catch 吞异常→Inserted=0→此断言失败, 抓迁移-导入漂移)。
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var blast = GeoDataQueries.ImportBlastEvents(c, new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["blast_date"] = "2023-01-01", ["explosive_kg"] = "1000", ["blast_volume_m3"] = "5000" }
        });
        Assert.Equal(1, blast.Inserted);    // 真实 blast_event schema 接受插入
        var models = GeoDataQueries.ImportEquipmentModels(c, new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["model"] = "TEST-GUARD-1", ["category"] = "truck" }
        });
        Assert.Equal(1, models.Inserted);   // 真实 equipment_model schema 接受
    }
}
