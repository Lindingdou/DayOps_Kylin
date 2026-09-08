using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Data.Common;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>设备型号 CSV 入库（按 model 主键 upsert）回归。</summary>
public class ImportEquipmentModelsTests
{
    private static DbConnection MemDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:"); conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = @"CREATE TABLE equipment_model(model TEXT PRIMARY KEY, category TEXT NOT NULL, working_weight_t REAL,
            power_kw REAL, bucket_m3 REAL, load_t REAL, dimensions_lwh TEXT, drill_diameter_mm REAL, tire_spec TEXT, std_daily_cap_wan_m3 REAL);";
        c.ExecuteNonQuery(); return conn;
    }
    private static Dictionary<string, string> R(params (string k, string v)[] kv)
    { var d = new Dictionary<string, string>(); foreach (var (k, v) in kv) d[k] = v; return d; }

    [Fact]
    public void Import_upserts_by_model_and_skips_missing_key()
    {
        using var conn = MemDb();
        var outc = GeoDataQueries.ImportEquipmentModels(conn, new List<IReadOnlyDictionary<string, string>>
        {
            R(("model","WK-10"),("category","电铲"),("bucket_m3","10"),("power_kw","1200")),
            R(("model","TR-100"),("category","卡车"),("load_t","100")),
            R(("category","无型号")),                  // 缺 model → err
            R(("model","WK-10"),("category","电铲"),("bucket_m3","12")),   // 同 model → upsert(REPLACE)
        });
        Assert.Equal(3, outc.Inserted);              // 3 次成功 insert/replace
        Assert.Equal(1, outc.Errors);                // 缺 model
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*), (SELECT bucket_m3 FROM equipment_model WHERE model='WK-10') FROM equipment_model";
        using var rd = c.ExecuteReader(); rd.Read();
        Assert.Equal(2, rd.GetInt32(0));             // WK-10 与 TR-100(upsert 未增行)
        Assert.Equal(12.0, rd.GetDouble(1), 6);      // WK-10 被 REPLACE 为 12
    }
}
