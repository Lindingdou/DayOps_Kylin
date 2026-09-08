using System;
using System.Collections.Generic;
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 嵌入式 SQLite —— 移植前的原行为, 保持逐字不变(默认方言)。
/// 库文件随程序走, 无需外部服务, 单测直接开内存库。
/// </summary>
public sealed class SqliteDialect : GeoDbDialect
{
    public override string Name => "sqlite";
    public override char ParamPrefix => '@';
    public override string MigrationMarker => ".Data.Migrations.";

    public override DbConnection CreateConnection(string? path)
        => new SqliteConnection(string.IsNullOrEmpty(path) ? "Data Source=:memory:" : $"Data Source={path}");

    public override void EnsureSchemaMigrationTable(DbConnection conn)
        => Exec(conn, @"CREATE TABLE IF NOT EXISTS _schema_migration (
            version TEXT NOT NULL PRIMARY KEY,
            applied_at TEXT NOT NULL DEFAULT (datetime('now'))
        );");

    // 迁移期关外键(种子跨表插入顺序会临时违约; 原 SqlLib.MigrationRunner 同此)。PRAGMA 须在事务外设。
    public override void BeforeMigrations(DbConnection conn) => Exec(conn, "PRAGMA foreign_keys=OFF;");
    public override void AfterMigrations(DbConnection conn) => Exec(conn, "PRAGMA foreign_keys=ON;");

    // Microsoft.Data.Sqlite 的 ExecuteNonQuery 会执行整批多语句(含触发器 BEGIN…END), 不拆。
    public override IReadOnlyList<string> SplitBatch(string script) => new[] { script };

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// 达梦 DM(DM8 / DM9)。驱动走达梦官方 NuGet 包 DM.DmProvider(有 net8.0 目标)。
///
/// 与 SQLite 的实质差异, 全在这个类里:
///   1. 要连接串(外部服务器), 不是本地文件;
///   2. 没有 CREATE TABLE IF NOT EXISTS —— 先查数据字典;
///   3. 一次 ExecuteNonQuery 只吃一条语句 —— 迁移脚本必须拆开喂;
///   4. 参数前缀是 ':' 不是 '@';
///   5. 没有 PRAGMA foreign_keys 这种全局外键总开关。
/// </summary>
public sealed class DmDialect : GeoDbDialect
{
    public override string Name => "dm";
    public override char ParamPrefix => ':';
    public override string MigrationMarker => ".Data.MigrationsDm.";
    public override string SchemaMigrationTable => "\"_schema_migration\"";

    public override DbConnection CreateConnection(string? path)
    {
        // path 对 DM 无意义(不是文件库)。连接串必须由环境给, 不硬编码任何凭据。
        string? cs = Environment.GetEnvironmentVariable("PITMINE_DB_CONN");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "PITMINE_DB=dm 但没给 PITMINE_DB_CONN。" +
                "例: Server=192.168.1.10:5236;User Id=SYSDBA;PWD=***;Schema=PITMINE");
        return new Dm.DmConnection(cs);
    }

    public override void EnsureSchemaMigrationTable(DbConnection conn)
    {
        // 达梦没有 IF NOT EXISTS: 先问数据字典(Oracle 兼容视图), 没有再建。
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = '_schema_migration'";
            if (Convert.ToInt32(q.ExecuteScalar()) > 0) return;
        }
        using var c = conn.CreateCommand();
        c.CommandText = @"CREATE TABLE ""_schema_migration"" (
            version    VARCHAR(128) NOT NULL PRIMARY KEY,
            applied_at TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP
        )";
        c.ExecuteNonQuery();
    }

    // 达梦无全局外键开关。种子数据的插入顺序因此必须自洽 —— 这一点由 DM 版迁移脚本保证,
    // 而不是靠运行期把约束关掉。
    public override void BeforeMigrations(DbConnection conn) { }
    public override void AfterMigrations(DbConnection conn) { }

    public override IReadOnlyList<string> SplitBatch(string script) => SplitOnSemicolons(script);
}
