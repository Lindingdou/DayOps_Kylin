using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Data.Common;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 地质/生产数据库（§四 地质数据库 / §八 日常生产组织的数据层）。
/// 原 PitMine3D 走 SqlLib(**SQLite**) + GeoDataBase 迁移(建表 + 真实种子数据)。
/// 此忠实移植其数据基座: 内嵌 50 个迁移 SQL(与原逐字一致) → 运行时建库自足, 跨平台、可单测。
///
/// 具体用哪种库已抽到 <see cref="GeoDbDialect"/>: 默认 SQLite(嵌入式, 本机全可跑, 无需外部服务),
/// 设 PITMINE_DB=dm 则连达梦。本类只负责"按版本号顺序把迁移喂进去"这件与库无关的事。
/// </summary>
public sealed class GeoDatabase : IDisposable
{
    private readonly DbConnection _conn;
    public DbConnection Connection => _conn;

    private GeoDatabase(DbConnection conn) => _conn = conn;

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
        var conn = GeoDbDialect.Current.CreateConnection(path);
        conn.Open();
        ApplyMigrations(conn);
        return new GeoDatabase(conn);
    }

    /// <summary>已应用的迁移版本数（用于校验建库成功）。</summary>
    public int AppliedMigrationCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {GeoDbDialect.Current.SchemaMigrationTable}";
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

    private static void ApplyMigrations(DbConnection conn)
    {
        var d = GeoDbDialect.Current;

        d.BeforeMigrations(conn);                 // SQLite: 迁移期临时关外键(种子跨表插入顺序会临时违约)
        d.EnsureSchemaMigrationTable(conn);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var q = conn.CreateCommand())
        {
            q.CommandText = $"SELECT version FROM {d.SchemaMigrationTable}";
            using var rd = q.ExecuteReader();
            while (rd.Read()) applied.Add(rd.GetString(0));
        }

        var asm = typeof(GeoDatabase).Assembly;
        // 资源名形如 PitMine3D.Kylin.Data.Migrations.V001_initial.sql(DM 版在 .MigrationsDm.)
        var migrations = asm.GetManifestResourceNames()
            .Where(n => n.Contains(d.MigrationMarker) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(n => (res: n, ver: VersionKey(n, d.MigrationMarker)))
            .OrderBy(t => t.ver, StringComparer.Ordinal)
            .ToList();

        if (migrations.Count == 0)
            throw new InvalidOperationException(
                $"没有找到 {d.Name} 方言的迁移脚本(资源标记 {d.MigrationMarker})。" +
                "DM 版迁移需先由 SQLite 版翻译生成。");

        foreach (var (res, ver) in migrations)
        {
            if (applied.Contains(ver)) continue;
            string sql = ReadResource(asm, res);
            using var tx = conn.BeginTransaction();
            try
            {
                // SQLite 可以一次吃整批; 达梦一次只吃一条, 故按方言拆开喂。
                foreach (var stmt in d.SplitBatch(sql))
                    Exec(conn, stmt, tx);

                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = $"INSERT INTO {d.SchemaMigrationTable}(version) VALUES({d.ParamPrefix}v)";
                ins.AddWithValue("v", ver);
                ins.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                throw new InvalidOperationException($"迁移 {ver} 应用失败：{ex.Message}", ex);
            }
        }

        d.AfterMigrations(conn);                  // SQLite: 恢复外键(仅约束后续写入, 不校验既有行)
    }

    // 从资源名取版本键（"…Migrations.V001_initial.sql" → "V001_initial"）。
    private static string VersionKey(string resourceName, string marker)
    {
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

    // 一条(或 SQLite 下的一批)语句。批量与否由方言的 SplitBatch 决定。
    private static void Exec(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
