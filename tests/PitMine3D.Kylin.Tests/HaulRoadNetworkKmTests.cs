using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Data;
using Xunit;

/// <summary>运输道路网总里程(GeoDataQueries.GetHaulRoadNetworkKm)已知值回归。忠实原 HaulRoadService.TotalNetworkKm(Σ length_m/1000, 排 closed)。</summary>
public class HaulRoadNetworkKmTests
{
    [Fact]
    public void Sums_active_roads_and_excludes_closed()
    {
        using var c = new SqliteConnection("Data Source=:memory:"); c.Open();
        using (var cmd = c.CreateCommand())
        { cmd.CommandText = "CREATE TABLE haul_road(road_id TEXT, length_m REAL, condition TEXT)"; cmd.ExecuteNonQuery(); }
        using (var cmd = c.CreateCommand())
        { cmd.CommandText = "INSERT INTO haul_road VALUES ('a',1000,'good'),('b',2000,'fair'),('c',5000,'closed')"; cmd.ExecuteNonQuery(); }
        // 在役 1000+2000=3000m=3km; closed 5000 排除。
        Assert.Equal(3.0, GeoDataQueries.GetHaulRoadNetworkKm(c), 6);
    }

    [Fact]
    public void Empty_table_returns_zero()
    {
        using var c = new SqliteConnection("Data Source=:memory:"); c.Open();
        using (var cmd = c.CreateCommand())
        { cmd.CommandText = "CREATE TABLE haul_road(road_id TEXT, length_m REAL, condition TEXT)"; cmd.ExecuteNonQuery(); }
        Assert.Equal(0.0, GeoDataQueries.GetHaulRoadNetworkKm(c), 6);
    }
}
