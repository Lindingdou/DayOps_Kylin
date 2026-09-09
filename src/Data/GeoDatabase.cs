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
/// 具体用哪种库已抽到 <see cref="GeoDbDialect"/>。
/// 产品里只有 openGauss(SQLite 已降级为测试专用)。本类只负责"按版本号顺序把迁移喂进去"这件与库无关的事。
/// </summary>
public sealed class GeoDatabase : IDisposable
{
    private readonly DbConnection _conn;
    private readonly GeoDbDialect _dialect;
    public DbConnection Connection => _conn;

    private GeoDatabase(DbConnection conn, GeoDbDialect dialect)
    {
        _conn = conn;
        _dialect = dialect;
    }

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

    /// <summary>
    /// 打开数据库。path=null → 内存库(连接存活期内有效)。
    ///
    /// 嵌入式库(SQLite)照旧: 顺手把迁移跑完, 一人一个文件, 自足。
    /// 共享的服务器库(openGauss 等)则**不在启动时跑迁移** —— 那是部署动作:
    /// 部署方带 PITMINE_DB_MIGRATE=1 跑一次建好库, 之后各客户端启动只校验版本。
    /// 否则十几台机器早上同时开机会同时建表灌种子, 首次部署就打架。
    /// </summary>
    public static GeoDatabase OpenSeeded(string? path = null)
        => OpenWith(GeoDbDialect.Current, path);

    /// <summary>
    /// 显式指定方言打开。给测试用: 不必去改 <see cref="GeoDbDialect.Current"/> 这个进程级
    /// 懒缓存静态, 就能针对另一种库跑 —— 本仓库有过共享可变 static 引发并行测试偶发失败的
    /// 前科, 不再往里加。生产代码走 <see cref="OpenSeeded"/> 即可。
    /// </summary>
    public static GeoDatabase OpenWith(GeoDbDialect dialect, string? path = null)
    {
        var conn = dialect.CreateConnection(path);
        conn.Open();

        if (dialect.MigrateOnOpen || IsMigrateRequested())
            ApplyMigrations(conn, dialect);
        else
            VerifySchemaUpToDate(conn, dialect);

        return new GeoDatabase(conn, dialect);
    }

    /// <summary>部署方显式要求执行迁移(PITMINE_DB_MIGRATE=1)。</summary>
    private static bool IsMigrateRequested()
    {
        string? v = Environment.GetEnvironmentVariable("PITMINE_DB_MIGRATE");
        return v is "1" or "true" or "TRUE" or "yes";
    }

    /// <summary>
    /// 共享库上客户端启动时的校验: 库里登记的迁移版本必须覆盖本程序内嵌的全部版本。
    /// 缺哪个就报哪个 —— 与其让程序带着不匹配的 schema 半死不活地跑, 不如当场说清楚。
    /// </summary>
    private static void VerifySchemaUpToDate(DbConnection conn, GeoDbDialect d)
    {
        var embedded = EmbeddedMigrations(d).Select(t => t.ver).ToList();

        var applied = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var q = conn.CreateCommand();
            q.CommandText = $"SELECT version FROM {d.SchemaMigrationTable}";
            using var rd = q.ExecuteReader();
            while (rd.Read()) applied.Add(rd.GetString(0));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"连上了 {d.Name} 库, 但读不到迁移登记表 {d.SchemaMigrationTable} —— 这个库还没初始化过。\n" +
                $"请在部署机上执行一次: PITMINE_DB_MIGRATE=1 <启动程序>\n原始错误: {ex.Message}", ex);
        }

        var missing = MissingMigrations(embedded, applied);
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"数据库 schema 落后于本程序: 缺 {missing.Count} 个迁移 " +
                $"({string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? " …" : "")})。\n" +
                $"请由部署方执行一次升级: PITMINE_DB_MIGRATE=1 <启动程序>；" +
                $"在升级完成前请勿使用本客户端, 以免写入与 schema 不符的数据。");
    }

    /// <summary>已应用的迁移版本数（用于校验建库成功）。</summary>
    public int AppliedMigrationCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {_dialect.SchemaMigrationTable}";
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

    private static void ApplyMigrations(DbConnection conn, GeoDbDialect d)
    {
        d.BeforeMigrations(conn);                 // SQLite: 迁移期临时关外键(种子跨表插入顺序会临时违约)
        d.EnsureSchemaMigrationTable(conn);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var q = conn.CreateCommand())
        {
            q.CommandText = $"SELECT version FROM {d.SchemaMigrationTable}";
            using var rd = q.ExecuteReader();
            while (rd.Read()) applied.Add(rd.GetString(0));
        }

        var migrations = EmbeddedMigrations(d);

        foreach (var (res, ver) in migrations)
        {
            if (applied.Contains(ver)) continue;
            string sql = ReadResource(d.MigrationAssembly, res);
            using var tx = conn.BeginTransaction();
            try
            {
                // 整份脚本一次执行: SQLite 的 Provider 与 Npgsql 都支持一个命令里多条语句
                // (Npgsql 还能正确识别 $$ 美元引用的函数体, 不会在函数里的分号处断开)。
                Exec(conn, sql, tx);

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

    /// <summary>
    /// 库里已登记的版本相对本程序内嵌版本, 缺了哪些。
    /// 纯函数, 不碰连接也不碰任何全局状态 —— 校验逻辑的单测打这里，
    /// 不必为了测它去改可变 static(本仓库有过共享 static 引发并发测试偶发失败的前科)。
    /// </summary>
    public static List<string> MissingMigrations(IEnumerable<string> embedded, ICollection<string> applied)
    {
        var miss = new List<string>();
        foreach (var v in embedded) if (!applied.Contains(v)) miss.Add(v);
        return miss;
    }

    /// <summary>
    /// 本程序内嵌的、属于当前方言的全部迁移(按版本号排序)。
    /// 应用迁移和校验版本都要用, 故提出来共用 —— 两边口径必须一致,
    /// 否则会出现"校验说缺, 执行又不跑"这种自相矛盾。
    /// </summary>
    private static List<(string res, string ver)> EmbeddedMigrations(GeoDbDialect d)
    {
        var asm = d.MigrationAssembly;
        // 资源名形如 PitMine3D.Kylin.Data.Migrations.V001_initial.sql(openGauss 版在 .MigrationsPg.)
        var list = asm.GetManifestResourceNames()
            .Where(n => n.Contains(d.MigrationMarker) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(n => (res: n, ver: VersionKey(n, d.MigrationMarker)))
            .OrderBy(t => t.ver, StringComparer.Ordinal)
            .ToList();

        if (list.Count == 0)
            throw new InvalidOperationException(
                $"没有找到 {d.Name} 方言的迁移脚本(资源标记 {d.MigrationMarker})。" +
                "openGauss 版迁移需先由 SQLite 版翻译生成: python build/sqlite2pg.py");
        return list;
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
