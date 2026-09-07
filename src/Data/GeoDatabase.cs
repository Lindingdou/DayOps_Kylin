using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 地质/生产数据库（§四 地质数据库 / §八 日常生产组织的数据层）。
/// 原 PitMine3D 走 SqlLib(**SQLite**, 非 DM8) + GeoDataBase 迁移(建表 + 真实种子数据)。
/// 此忠实移植其数据基座: 内嵌 50 个迁移 SQL(与原逐字一致) → 运行时建库自足, 跨平台、可单测。
/// 数据本机全可跑（SQLite 嵌入式, 无需外部数据库服务器）。
/// </summary>
public sealed class GeoDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    public SqliteConnection Connection => _conn;

    private GeoDatabase(SqliteConnection conn) => _conn = conn;

    /// <summary>
    /// 默认库文件路径：用户数据目录 (Linux: ~/.local/share/PitMine3D.Kylin/geo.db, Windows: %LocalAppData%\PitMine3D.Kylin\geo.db)。
    /// 装到 /opt 后程序目录属 root 不可写, 临时目录又会被清理(重启丢数据), 故落用户目录; 目录建不了再退临时目录。
    /// </summary>
    public static string DefaultPath()
    {
        try
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(baseDir))
            {
                string dir = Path.Combine(baseDir, "PitMine3D.Kylin");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "geo.db");
            }
        }
        catch { }
        return Path.Combine(Path.GetTempPath(), "pmkylin_geo.db");
    }

    /// <summary>打开并建库(应用全部迁移)。path=null → 内存库(连接存活期内有效)。</summary>
    public static GeoDatabase OpenSeeded(string? path = null)
    {
        var connStr = string.IsNullOrEmpty(path) ? "Data Source=:memory:" : $"Data Source={path}";
        var conn = new SqliteConnection(connStr);
        conn.Open();
        ApplyMigrations(conn);
        return new GeoDatabase(conn);
    }

    /// <summary>已应用的迁移版本数（用于校验建库成功）。</summary>
    public int AppliedMigrationCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM _schema_migration";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>标量查询便捷方法。</summary>
    public long ScalarLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
    }

    private static void ApplyMigrations(SqliteConnection conn)
    {
        // 迁移期关外键(种子跨表插入顺序会临时违约; 原 SqlLib.MigrationRunner 同此)。PRAGMA 须在事务外设。
        Exec(conn, "PRAGMA foreign_keys=OFF;");

        Exec(conn, @"CREATE TABLE IF NOT EXISTS _schema_migration (
            version TEXT NOT NULL PRIMARY KEY,
            applied_at TEXT NOT NULL DEFAULT (datetime('now'))
        );");

        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT version FROM _schema_migration";
            using var rd = q.ExecuteReader();
            while (rd.Read()) applied.Add(rd.GetString(0));
        }

        var asm = typeof(GeoDatabase).Assembly;
        // 资源名形如 PitMine3D.Kylin.Data.Migrations.V001_initial.sql
        var migrations = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".Data.Migrations.") && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(n => (res: n, ver: VersionKey(n)))
            .OrderBy(t => t.ver, StringComparer.Ordinal)
            .ToList();

        foreach (var (res, ver) in migrations)
        {
            if (applied.Contains(ver)) continue;
            string sql = ReadResource(asm, res);
            using var tx = conn.BeginTransaction();
            try
            {
                Exec(conn, sql, tx);
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO _schema_migration(version) VALUES(@v)";
                ins.Parameters.AddWithValue("@v", ver);
                ins.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                throw new InvalidOperationException($"迁移 {ver} 应用失败：{ex.Message}", ex);
            }
        }

        Exec(conn, "PRAGMA foreign_keys=ON;");   // 迁移完恢复外键(仅约束后续写入, 不校验既有行)
    }

    // 从资源名取版本键（"…Migrations.V001_initial.sql" → "V001_initial"）。
    private static string VersionKey(string resourceName)
    {
        const string marker = ".Data.Migrations.";
        int i = resourceName.IndexOf(marker, StringComparison.Ordinal);
        string tail = i >= 0 ? resourceName.Substring(i + marker.Length) : resourceName;
        return tail.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? tail[..^4] : tail;
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"找不到迁移资源 {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // Microsoft.Data.Sqlite 的 ExecuteNonQuery 会执行整批多语句（含触发器 BEGIN…END）。
    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
