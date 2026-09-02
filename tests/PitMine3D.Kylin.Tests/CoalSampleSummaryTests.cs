using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Data;
using Xunit;

/// <summary>钻孔煤质汇总(GeoDataQueries.GetCoalSampleSummaries)——读衍生表 coal_sample_summary(每孔每层化验平均, 忠实原 CoalQualityService.AllSummary)。</summary>
public class CoalSampleSummaryTests
{
    private static SqliteConnection Db()
    {
        var c = new SqliteConnection("Data Source=:memory:"); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"CREATE TABLE coal_sample_summary(id INTEGER, borehole_id INTEGER, seam_code TEXT,
            sample_count INTEGER, avg_thickness REAL, avg_ad_raw REAL, avg_vdaf_raw REAL,
            avg_std_raw REAL, avg_qgr_d REAL, avg_qnet_ad REAL, dominant_coal_type TEXT);";
        cmd.ExecuteNonQuery();
        return c;
    }

    [Fact]
    public void Reads_rows_ordered_by_seam_with_fields()
    {
        using var c = Db();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO coal_sample_summary VALUES
                (1, 101, '4', 5, 6.5, 12.3, 28.0, 0.45, 24.5, 22.0, '气煤'),
                (2, 102, '2', 3, 8.0, 15.0, 30.0, 0.60, 22.0, 20.0, '肥煤');";
            cmd.ExecuteNonQuery();
        }
        var rows = GeoDataQueries.GetCoalSampleSummaries(c);
        Assert.Equal(2, rows.Count);
        Assert.Equal("2", rows[0].SeamCode);       // ORDER BY seam_code → '2' 先
        Assert.Equal("4", rows[1].SeamCode);
        var s4 = rows[1];
        Assert.Equal(101, s4.BoreholeId);
        Assert.Equal(5, s4.SampleCount);
        Assert.Equal(6.5, s4.AvgThicknessM!.Value, 6);
        Assert.Equal(12.3, s4.AvgAdRawPct!.Value, 6);
        Assert.Equal(0.45, s4.AvgStdRawPct!.Value, 6);
        Assert.Equal(24.5, s4.AvgQgrDMjKg!.Value, 6);
        Assert.Equal("气煤", s4.DominantCoalType);
    }

    [Fact]
    public void Null_fields_preserved()
    {
        using var c = Db();
        using (var cmd = c.CreateCommand())
        { cmd.CommandText = "INSERT INTO coal_sample_summary VALUES (1, 1, '9', 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL);"; cmd.ExecuteNonQuery(); }
        var s = Assert.Single(GeoDataQueries.GetCoalSampleSummaries(c));
        Assert.Null(s.AvgAdRawPct);
        Assert.Null(s.DominantCoalType);
        Assert.Equal(0, s.SampleCount);
    }
}
