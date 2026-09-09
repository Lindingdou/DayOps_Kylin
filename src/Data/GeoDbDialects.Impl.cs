using System.Collections.Generic;
using System;
using System.Data.Common;

namespace PitMine3D.Kylin.Data;

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
        // 环境变量优先(部署脚本用); 否则用界面/站点配置里存的那份。
        string? cs = Environment.GetEnvironmentVariable("PITMINE_DB_CONN");
        if (string.IsNullOrWhiteSpace(cs))
        {
            var s = DbConnectionSettings.LoadEffective();
            if (s.IsRemote) cs = s.BuildConnectionString();
        }
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "选了 openGauss 但没有可用的连接信息。" +
                "请在「数据库连接」里填写主机/库名/账号, 或设置 PITMINE_DB_CONN。");

        // Npgsql 把连接还给连接池时会发 DISCARD ALL 重置会话状态, 而 openGauss 不认:
        //   0A000: DISCARD statement is not yet supported
        // 于是每次复用连接都炸。关掉这个重置即可 —— 实测(openGauss 6.0.0-lite)必须加,
        // 所以在这里兜底补上, 不指望部署方记得往连接串里写。
        if (cs.IndexOf("No Reset On Close", StringComparison.OrdinalIgnoreCase) < 0)
            cs = cs.TrimEnd(';') + ";No Reset On Close=true";

        // Npgsql 默认连接超时 15 秒。服务器没开或地址填错时, 界面要僵这么久 ——
        // 用户会以为程序死了。8 秒足够局域网握手, 失败也能早点把话说清楚。
        if (cs.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) < 0)
            cs = cs.TrimEnd(';') + ";Timeout=8";

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

    public override List<string> ListTables(DbConnection conn)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    public override List<(string Name, string Type, bool NotNull, bool Pk)> TableColumns(DbConnection conn, string table)
    {
        var list = new List<(string, string, bool, bool)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.column_name,
                   c.data_type,
                   CASE WHEN c.is_nullable = 'NO' THEN 1 ELSE 0 END AS notnull,
                   CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS ispk
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                     ON kcu.constraint_name = tc.constraint_name
                    AND kcu.table_schema   = tc.table_schema
                WHERE tc.table_schema = 'public'
                  AND tc.table_name   = @t
                  AND tc.constraint_type = 'PRIMARY KEY'
            ) pk ON pk.column_name = c.column_name
            WHERE c.table_schema = 'public' AND c.table_name = @t
            ORDER BY c.ordinal_position";
        cmd.AddWithValue("t", table);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add((rd.GetString(0), rd.GetString(1),
                      Convert.ToInt64(rd.GetValue(2)) != 0,
                      Convert.ToInt64(rd.GetValue(3)) != 0));
        return list;
    }

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
