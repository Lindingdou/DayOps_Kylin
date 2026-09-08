using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// §四/§八 数据基座回归：内嵌迁移 SQL 建 SQLite 库 + 真实种子数据。
/// 证明该层非 DM8 阻——SQLite 嵌入式, 本机(Windows)全可跑可验证。
/// </summary>
public class GeoDatabaseTests
{
    [Fact]
    public void Builds_all_migrations_in_memory()
    {
        using var db = GeoDatabase.OpenSeeded();
        // 断言"内嵌几个就应用几个", 不写死数字 —— 写死的话每加一个迁移这里都要红一次,
        // 而它想守的其实是"没有迁移被漏掉", 与总数具体是多少无关。
        Assert.Equal(EmbeddedMigrationCount(), db.AppliedMigrationCount());
    }

    [Fact]
    public void Core_tables_created()
    {
        using var db = GeoDatabase.OpenSeeded();
        foreach (var t in new[] { "equipment_model", "equipment", "production_record",
                                  "capacity_monthly", "fault_event", "monthly_plan", "dispatch_rule" })
        {
            long n = db.ScalarLong($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{t}'");
            Assert.True(n == 1, $"表 {t} 应存在");
        }
    }

    [Fact]
    public void Seed_data_present()
    {
        using var db = GeoDatabase.OpenSeeded();
        // V002 灌 9 型号 + 15 设备；V020+ 灌真实车队/产能/生产
        Assert.True(db.ScalarLong("SELECT COUNT(*) FROM equipment_model") >= 5, "设备型号种子");
        Assert.True(db.ScalarLong("SELECT COUNT(*) FROM equipment") >= 5, "设备台账种子");
        Assert.True(db.ScalarLong("SELECT COUNT(*) FROM capacity_monthly") > 0, "产能月度种子");
        Assert.True(db.ScalarLong("SELECT COUNT(*) FROM production_record") > 0, "生产记录种子");
    }

    [Fact]
    public void Idempotent_reopen_file_db_skips_applied()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pm_geo_test_{System.Guid.NewGuid():N}.db");
        try
        {
            int n = EmbeddedMigrationCount();
            using (var db1 = GeoDatabase.OpenSeeded(path)) Assert.Equal(n, db1.AppliedMigrationCount());
            using (var db2 = GeoDatabase.OpenSeeded(path)) Assert.Equal(n, db2.AppliedMigrationCount());  // 重开不重复应用
        }
        finally { try { System.IO.File.Delete(path); } catch { /* 清理失败无碍 */ } }
    }

    /// <summary>程序内嵌的 SQLite 迁移份数(资源名形如 ….Data.Migrations.V001_initial.sql)。</summary>
    private static int EmbeddedMigrationCount()
    {
        int n = 0;
        foreach (var r in typeof(GeoDatabase).Assembly.GetManifestResourceNames())
            if (r.Contains(".Data.Migrations.") && r.EndsWith(".sql", System.StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }
}
