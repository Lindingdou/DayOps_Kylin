using System;
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

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// openGauss（华为系, PostgreSQL 内核衍生）—— 局域网内一个共享库 + 多客户端的目标形态。
///
/// 相比 SQLite 的实质差异都收在这里:
///   1. 要连接串(外部服务器), 不是本地文件;
///   2. 库是**共享**的 —— 迁移不在启动时跑, 见 <see cref="MigrateOnOpen"/>;
///   3. 迁移期没有 SQLite 那种外键总开关, 改用会话级 session_replication_role。
///
/// 而下面这些原以为要改的, 实际都不用:
///   · 参数前缀仍是 '@' —— Npgsql 原生认, 全库 407 处参数一处不动;
///   · TEXT/INTEGER 是 PG 原生类型, 列定义基本照搬;
///   · date/status/value/year 这些列名在 PG 里是非保留关键字, 不必加引号;
///   · Npgsql 能在一个命令里执行多条语句(且正确识别 $$ 美元引用), 迁移脚本不必拆开喂。
///
/// 驱动用主流 Npgsql 而非 openGauss 专用分支。代价是 openGauss 默认的 SHA256 认证
/// 标准 PG 驱动握不了手, 需在服务端设 password_encryption_type=1 并**重设一次用户密码**
/// 以启用 MD5(见 db/pg/DEPLOY.md)。若现场不允许开 MD5, 把本类的 NpgsqlConnection
/// 换成 OpenGauss.NET 的连接类型即可 —— 方言层就是为这种替换准备的。
/// </summary>
public sealed class OpenGaussDialect : GeoDbDialect
{
    public override string Name => "opengauss";
    public override char ParamPrefix => '@';          // Npgsql 原生支持 @, 与既有 SQL 文本一致
    public override string MigrationMarker => ".Data.MigrationsPg.";
    public override bool MigrateOnOpen => false;      // 共享库: 迁移是部署动作, 见基类说明

    public override DbConnection CreateConnection(string? path)
    {
        // path 对 openGauss 无意义(不是文件库)。连接串必须由环境给, 不硬编码任何凭据。
        string? cs = Environment.GetEnvironmentVariable("PITMINE_DB_CONN");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "PITMINE_DB=opengauss 但没给 PITMINE_DB_CONN。" +
                "例: Host=192.168.1.10;Port=5432;Database=pitmine;Username=pitmine;Password=***");
        return new Npgsql.NpgsqlConnection(cs);
    }

    public override void EnsureSchemaMigrationTable(DbConnection conn)
    {
        using var c = conn.CreateCommand();
        c.CommandText = @"CREATE TABLE IF NOT EXISTS _schema_migration (
            version    VARCHAR(128) NOT NULL PRIMARY KEY,
            applied_at TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP
        )";
        c.ExecuteNonQuery();
    }

    /// <summary>
    /// 迁移期关外键。PG 没有 SQLite 的 PRAGMA foreign_keys, 但把会话的
    /// session_replication_role 设成 replica 会跳过触发器与外键检查, 效果等价;
    /// 只作用于当前会话, 不影响其它客户端。需要相应权限, 拿不到就跳过 ——
    /// 那时种子的跨表插入顺序必须自洽, 由迁移脚本自身保证。
    /// </summary>
    public override void BeforeMigrations(DbConnection conn) => TrySet(conn, "replica");
    public override void AfterMigrations(DbConnection conn) => TrySet(conn, "origin");

    private static void TrySet(DbConnection conn, string role)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET session_replication_role = '{role}'";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 权限不足: 不致命, 继续跑迁移。真有外键顺序问题会在具体那条语句上报错。
        }
    }
}
