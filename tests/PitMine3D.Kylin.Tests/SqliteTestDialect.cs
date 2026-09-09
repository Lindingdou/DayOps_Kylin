using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 嵌入式 SQLite 方言 —— **只存在于测试程序集**。
///
/// 为什么在这儿而不在主程序里: 交付到麒麟的产品不含 SQLite(信创要求 + 避免"中心库连不上就
/// 静默退回本机文件"这种会丢数据的行为)。但测试需要一个开箱即用、互相隔离、不依赖外部服务的库,
/// 否则 2000 多个用例每跑一次都得先有一台 openGauss, 且彼此串数据。
///
/// 于是 SQLite 降级为**测试专用依赖**: 包引用在测试工程, 迁移脚本嵌进测试程序集
/// (靠 <see cref="MigrationAssembly"/> 指过来), 产品的发布产物里一个字节都不含。
/// </summary>
public sealed class SqliteDialect : GeoDbDialect
{
    public override string Name => "sqlite";
    public override char ParamPrefix => '@';
    public override string MigrationMarker => ".Data.Migrations.";

    /// <summary>SQLite 版迁移随测试程序集走, 不在产品里。</summary>
    public override Assembly MigrationAssembly => typeof(SqliteDialect).Assembly;

    public override bool MatchesConnection(DbConnection conn) => conn is SqliteConnection;

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

    public override List<string> ListTables(DbConnection conn)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    public override List<(string Name, string Type, bool NotNull, bool Pk)> TableColumns(DbConnection conn, string table)
    {
        var list = new List<(string, string, bool, bool)>();
        using var cmd = conn.CreateCommand();
        // PRAGMA 不接受参数, 只能拼名字。表名来自 ListTables(系统目录), 非用户输入; 仍按规矩转义双引号。
        cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add((rd.GetString(1),                          // name
                      rd.IsDBNull(2) ? "" : rd.GetString(2),    // type
                      rd.GetInt64(3) != 0,                      // notnull
                      rd.GetInt64(5) != 0));                    // pk
        return list;
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// 测试统一的建库入口。取代原先满仓库直接调的 <c>GeoDatabase.OpenSeeded()</c> ——
/// 那个方法现在解析到产品方言(openGauss), 测试再调它就会连到真库上去。
/// 事故先例: 2026-09-08 一次全量测试因此往局域网共享库写进了 13 行测试数据。
/// </summary>
public static class TestDb
{
    /// <summary>开一个已建好表灌好种子的 SQLite 库。path=null → 内存库。</summary>
    public static GeoDatabase Open(string? path = null)
        => GeoDatabase.OpenWith(new SqliteDialect(), path);
}
