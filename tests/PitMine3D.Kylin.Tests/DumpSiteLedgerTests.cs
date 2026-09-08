using Microsoft.Data.Sqlite;
using System.Data.Common;
using PitMine3D.Kylin.Data;
using Xunit;

/// <summary>排土场台账(GeoDataQueries.GetDumpSites)已知值回归 —— 充填率=已填÷设计×100(忠实原 DumpSite.FillRate)。</summary>
public class DumpSiteLedgerTests
{
    private static DbConnection Db()
    {
        var c = new SqliteConnection("Data Source=:memory:"); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"CREATE TABLE dump_site(dump_id TEXT, name TEXT, dump_type TEXT,
            design_capacity_wan_m3 REAL, current_filled_wan_m3 REAL,
            overall_slope_angle_deg REAL, service_years_remaining REAL, status TEXT);";
        cmd.ExecuteNonQuery();
        return c;
    }

    [Fact]
    public void FillRate_is_filled_over_design_percent()
    {
        using var c = Db();
        using (var cmd = c.CreateCommand())
        { cmd.CommandText = "INSERT INTO dump_site VALUES ('D1','北排','external',1000,250,33,10,'active')"; cmd.ExecuteNonQuery(); }
        var rows = GeoDataQueries.GetDumpSites(c);
        var s = Assert.Single(rows);
        Assert.Equal("北排", s.Name);
        Assert.Equal("external", s.DumpType);
        Assert.Equal(1000, s.DesignCapacityWanM3, 6);
        Assert.Equal(250, s.CurrentFilledWanM3, 6);
        Assert.Equal(25.0, s.FillRatePct, 6);            // 250/1000×100
    }

    [Fact]
    public void Zero_design_capacity_fill_rate_zero()
    {
        using var c = Db();
        using (var cmd = c.CreateCommand())
        { cmd.CommandText = "INSERT INTO dump_site VALUES ('D2','内排','internal',0,0,NULL,NULL,'active')"; cmd.ExecuteNonQuery(); }
        var s = Assert.Single(GeoDataQueries.GetDumpSites(c));
        Assert.Equal(0.0, s.FillRatePct, 6);             // 设计 0 → 不除零
        Assert.Null(s.OverallSlopeAngleDeg);
        Assert.Null(s.ServiceYearsRemaining);
    }
}
