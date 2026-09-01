using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>爆破事件 CSV 入库（导入→GetBlastStats 往返 + 缺单耗自算）回归。</summary>
public class ImportBlastEventsTests
{
    private static SqliteConnection MemDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:"); conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = @"CREATE TABLE blast_event(id INTEGER PRIMARY KEY AUTOINCREMENT, blast_date TEXT, blast_time TEXT,
            blast_seq INT, drill_id TEXT, location_code TEXT, material TEXT, diameter_mm REAL, hole_count INT,
            total_hole_length_m REAL, explosive_kg REAL, blast_volume_m3 REAL, unit_consumption_kg_m3 REAL);";
        c.ExecuteNonQuery();
        return conn;
    }

    private static Dictionary<string, string> R(params (string k, string v)[] kv)
    { var d = new Dictionary<string, string>(); foreach (var (k, v) in kv) d[k] = v; return d; }

    [Fact]
    public void Import_inserts_valid_skips_missing_date_and_roundtrips()
    {
        using var conn = MemDb();
        var rows = new List<IReadOnlyDictionary<string, string>>
        {
            R(("blast_date","2023-01-05"),("location_code","A"),("hole_count","100"),("total_hole_length_m","1000"),("explosive_kg","2000"),("blast_volume_m3","10000")), // 单耗缺→算 0.2
            R(("blast_date","2023-02-10"),("location_code","B"),("explosive_kg","3000"),("blast_volume_m3","15000"),("unit_consumption_kg_m3","0.25")),                    // 单耗给
            R(("location_code","C"),("explosive_kg","500")),                                                                                                              // 缺 blast_date → err
        };
        var outc = GeoDataQueries.ImportBlastEvents(conn, rows);
        Assert.Equal(2, outc.Inserted);
        Assert.Equal(1, outc.Errors);

        var b = GeoDataQueries.GetBlastStats(conn);
        Assert.Equal(2, b.Events);
        Assert.Equal(25000, b.TotalVolumeM3, 4);        // 10000+15000
        Assert.Equal(5000, b.TotalExplosiveKg, 4);      // 2000+3000
        Assert.Equal(1000, b.TotalHoleLengthM, 4);      // 1000 (第二行缺→0)
        Assert.Equal(2, b.Locations);                   // A,B
        Assert.Equal(5000.0 / 25000, b.OverallUnitKgM3, 6);  // 综合单耗
    }

    [Fact]
    public void Unit_consumption_computed_when_missing()
    {
        using var conn = MemDb();
        GeoDataQueries.ImportBlastEvents(conn, new List<IReadOnlyDictionary<string, string>>
        { R(("blast_date","2023-03-01"),("explosive_kg","1200"),("blast_volume_m3","4000")) });   // 缺单耗 → 1200/4000=0.3
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT unit_consumption_kg_m3 FROM blast_event";
        Assert.Equal(0.3, System.Convert.ToDouble(c.ExecuteScalar()), 6);
    }
}
