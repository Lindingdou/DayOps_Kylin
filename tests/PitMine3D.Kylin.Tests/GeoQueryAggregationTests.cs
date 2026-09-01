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
}
