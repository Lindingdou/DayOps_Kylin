using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 对着**真实 openGauss 实例**跑的集成测试。默认不跑 —— 只有设了环境变量
/// <c>PITMINE_TEST_PG_CONN</c> 才会真正连库, 否则每个用例直接返回。
///
/// 为什么要有它: 迁移和方言层的正确性有一批东西**静态检查证明不了**, 只能连上去跑一遍。
/// 这些正是 db/pg/PORTING.md 里标着"必须在真机上验证"的假设:
///   1. 认证 —— openGauss 默认 SHA256, 标准 Npgsql 握不了手;
///   2. ON CONFLICT 到底能不能用(官方文档说支持, 社区有人说轻量版不支持);
///   3. Npgsql 8 接不接受 openGauss 报出的 PG 9.2 版本号;
///   4. SERIAL 与种子里的显式主键共存, 序列对齐有没有生效;
///   5. 触发器(共用函数 + BEFORE)在 PG 语义下是否真的自增 row_version。
///
/// 本地怎么起实例见 db/pg/LOCAL-TEST.md。跑法:
///   PITMINE_TEST_PG_CONN='Host=localhost;Port=15432;Database=pitmine;Username=pitmine;Password=***' \
///   dotnet test --filter OpenGaussIntegrationTests
/// </summary>
// 同一个 collection = 不并行。扫描依赖迁移先把表建好, 并行跑会扫到空库,
// 报一堆 relation does not exist —— 那是测试顺序问题, 不是移植问题。
[Collection("openGauss")]
public class OpenGaussIntegrationTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("PITMINE_TEST_PG_CONN");

    /// <summary>没配连接串就直接返回(用例算通过)。这类"需要外部依赖"的测试不该拦住日常全量跑。</summary>
    private static bool Skip => string.IsNullOrWhiteSpace(Conn);

    /// <summary>
    /// 走方言建连, 而不是自己 new NpgsqlConnection —— 方言会补上 openGauss 必需的
    /// No Reset On Close=true(否则连接池归还时发 DISCARD ALL, openGauss 直接报
    /// 0A000 not yet supported)。测试要走和生产同一条路径, 否则测的不是真实行为。
    /// </summary>
    private static DbConnection Open()
    {
        Environment.SetEnvironmentVariable("PITMINE_DB_CONN", Conn);
        var c = new OpenGaussDialect().CreateConnection(null);
        c.Open();
        return c;
    }

    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    // ── 假设 1 & 3: 连得上吗 ──────────────────────────────────────────────────

    [Fact]
    public void 假设1和3_Npgsql能连上openGauss并报出服务器版本()
    {
        if (Skip) return;
        using var c = Open();

        string ver = Scalar(c, "SELECT version()")?.ToString() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(ver), "取不到版本串");
        // 只断言连通与能取版本 —— 具体版本号是多少不该写死, openGauss 各版本报法不一。
    }

    // ── 假设 2: ON CONFLICT ──────────────────────────────────────────────────

    [Fact]
    public void 假设2_冲突跳过_ON_DUPLICATE_KEY_UPDATE自赋值()
    {
        if (Skip) return;
        using var c = Open();
        Exec(c, "DROP TABLE IF EXISTS _probe_conflict");
        Exec(c, "CREATE TABLE _probe_conflict (k TEXT PRIMARY KEY, v INTEGER)");
        try
        {
            Exec(c, "INSERT INTO _probe_conflict (k, v) VALUES ('a', 1)");
            // 实测(openGauss 6.0.0-lite): **不支持 ON CONFLICT**, 报 syntax error;
            // 但支持 MySQL 风格的 ON DUPLICATE KEY UPDATE。要"冲突就跳过"就自赋值做空操作。
            // 额外的好处: 不必指定冲突键 —— 种子里有 6 张表的 INSERT OR REPLACE 压根没提供主键。
            //
            // 注意自赋值必须挑**非键列**: 拿主键自赋值会被拒 ——
            //   0A000: INSERT ON DUPLICATE KEY UPDATE don't allow update on primary key or unique key
            // 所以这里是 v = v(非键), 不是 k = k(主键)。生成器同此。
            Exec(c, "INSERT INTO _probe_conflict (k, v) VALUES ('a', 2) ON DUPLICATE KEY UPDATE v = v");

            Assert.Equal(1L, Convert.ToInt64(Scalar(c, "SELECT v FROM _probe_conflict WHERE k='a'")));
        }
        finally { Exec(c, "DROP TABLE IF EXISTS _probe_conflict"); }
    }

    [Fact]
    public void 假设2_冲突覆盖_ON_DUPLICATE_KEY_UPDATE取新值()
    {
        if (Skip) return;
        using var c = Open();
        Exec(c, "DROP TABLE IF EXISTS _probe_upsert");
        Exec(c, "CREATE TABLE _probe_upsert (k TEXT PRIMARY KEY, v INTEGER)");
        try
        {
            Exec(c, "INSERT INTO _probe_upsert (k, v) VALUES ('a', 1)");
            // 同上: 用 ON DUPLICATE KEY UPDATE 取代 ON CONFLICT ... DO UPDATE。
            // 引用新行的值用 VALUES(列), 不是 PG 的 EXCLUDED.列。
            Exec(c, "INSERT INTO _probe_upsert (k, v) VALUES ('a', 2) " +
                    "ON DUPLICATE KEY UPDATE v = VALUES(v)");

            Assert.Equal(2L, Convert.ToInt64(Scalar(c, "SELECT v FROM _probe_upsert WHERE k='a'")));
        }
        finally { Exec(c, "DROP TABLE IF EXISTS _probe_upsert"); }
    }

    // ── 假设 4: SERIAL + 显式主键 + 序列对齐 ──────────────────────────────────

    [Fact]
    public void 假设4_SERIAL列显式插值后setval能把序列接上()
    {
        if (Skip) return;
        using var c = Open();
        Exec(c, "DROP TABLE IF EXISTS _probe_serial");
        Exec(c, "CREATE TABLE _probe_serial (id SERIAL PRIMARY KEY, note TEXT)");
        try
        {
            // 模拟种子: 带显式 id 插入, 序列不会跟着走
            Exec(c, "INSERT INTO _probe_serial (id, note) VALUES (1, 'seed'), (2, 'seed'), (3, 'seed')");
            // 迁移末尾做的正是这件事
            Exec(c, "SELECT setval(pg_get_serial_sequence('_probe_serial','id'), " +
                    "COALESCE((SELECT MAX(id) FROM _probe_serial), 1))");
            // 对齐之后的自增插入不该撞主键
            Exec(c, "INSERT INTO _probe_serial (note) VALUES ('auto')");

            Assert.Equal(4L, Convert.ToInt64(Scalar(c, "SELECT MAX(id) FROM _probe_serial")));
        }
        finally { Exec(c, "DROP TABLE IF EXISTS _probe_serial"); }
    }

    // ── 假设 5: 触发器 ───────────────────────────────────────────────────────

    [Fact]
    public void 假设5_共用触发器函数能自增行版本号()
    {
        if (Skip) return;
        using var c = Open();
        Exec(c, "DROP TABLE IF EXISTS _probe_trg");
        Exec(c, "CREATE TABLE _probe_trg (k TEXT PRIMARY KEY, v TEXT, " +
                "updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP, row_version INTEGER NOT NULL DEFAULT 0)");
        try
        {
            // 与生成的迁移里逐字一致的写法
            Exec(c, @"CREATE OR REPLACE FUNCTION fn_probe_touch() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    NEW.row_version := OLD.row_version + 1;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;");
            Exec(c, "CREATE TRIGGER trg_probe BEFORE UPDATE ON _probe_trg " +
                    "FOR EACH ROW EXECUTE PROCEDURE fn_probe_touch()");

            Exec(c, "INSERT INTO _probe_trg (k, v) VALUES ('a', '1')");
            Assert.Equal(0L, Convert.ToInt64(Scalar(c, "SELECT row_version FROM _probe_trg WHERE k='a'")));

            Exec(c, "UPDATE _probe_trg SET v='2' WHERE k='a'");
            // 恰好 +1: 若触发器递归了会大于 1
            Assert.Equal(1L, Convert.ToInt64(Scalar(c, "SELECT row_version FROM _probe_trg WHERE k='a'")));

            // 乐观锁: 拿过期版本号更新必须影响 0 行
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE _probe_trg SET v='3' WHERE k='a' AND row_version=0";
            Assert.Equal(0, cmd.ExecuteNonQuery());
        }
        finally
        {
            Exec(c, "DROP TABLE IF EXISTS _probe_trg");
            Exec(c, "DROP FUNCTION IF EXISTS fn_probe_touch()");
        }
    }

    // ── 全量: 52 份迁移真的能建起库来 ────────────────────────────────────────

    /// <summary>
    /// 把 52 份 openGauss 迁移在真库上跑一遍。这是**唯一**能证明移植成立的测试,
    /// 前面所有静态校验加起来也替代不了它。
    ///
    /// 注意: 会往目标库里建 50+ 张表并灌 3 万多行种子, 请指向**专用测试库**, 别指生产。
    /// </summary>
    [Fact]
    public void 全部迁移能在真实openGauss上建起库()
    {
        if (Skip) return;
        if (Environment.GetEnvironmentVariable("PITMINE_TEST_PG_MIGRATE") is not ("1" or "true"))
            return;   // 建库是破坏性操作, 再加一道显式开关

        // 走显式方言入口, 不去改 PITMINE_DB 这类进程级环境变量 ——
        // 那会污染并行跑的其它用例(GeoDbDialect.Current 是懒缓存的静态)。
        Environment.SetEnvironmentVariable("PITMINE_DB_CONN", Conn);   // 方言从这里取连接串
        Environment.SetEnvironmentVariable("PITMINE_DB_MIGRATE", "1"); // 共享库默认不迁移, 这里显式要求

        using var db = GeoDatabase.OpenWith(new OpenGaussDialect());

        int expected = typeof(GeoDatabase).Assembly.GetManifestResourceNames()
            .Count(n => n.Contains(".Data.MigrationsPg.") &&
                        n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, db.AppliedMigrationCount());
        AssertRowCountsMatchSqlite(db);
        Assert.True(db.ScalarLong("SELECT COUNT(*) FROM equipment") > 0, "设备种子应已灌入");
    }

    /// <summary>
    /// 逐表比对 openGauss 与 SQLite 的行数。
    ///
    /// 这一条比"迁移全跑完了"更要紧: 冲突处理是从 INSERT OR REPLACE/IGNORE 翻成
    /// ON DUPLICATE KEY UPDATE 的, 一旦哪处的键判断错了, 表现不是报错而是**静默少插几行** ——
    /// 迁移照样绿, 数据却对不上。只有拿原库当基准逐表核对才能发现。
    /// </summary>
    private static void AssertRowCountsMatchSqlite(GeoDatabase pg)
    {
        using var lite = GeoDatabase.OpenWith(new SqliteDialect());   // 内存库, 现建现比

        var tables = new List<string>();
        using (var q = lite.Connection.CreateCommand())
        {
            q.CommandText = "SELECT name FROM sqlite_master WHERE type='table' " +
                            "AND name NOT LIKE 'sqlite_%' AND name <> '_schema_migration' ORDER BY name";
            using var rd = q.ExecuteReader();
            while (rd.Read()) tables.Add(rd.GetString(0));
        }
        Assert.NotEmpty(tables);

        var diffs = new List<string>();
        foreach (var t in tables)
        {
            long a = lite.ScalarLong($"SELECT COUNT(*) FROM {t}");
            long b;
            try { b = pg.ScalarLong($"SELECT COUNT(*) FROM {t}"); }
            catch (Exception ex) { diffs.Add($"{t}: openGauss 侧查不到 ({ex.Message.Split('\n')[0]})"); continue; }
            if (a != b) diffs.Add($"{t}: SQLite {a} 行 vs openGauss {b} 行");
        }

        Assert.True(diffs.Count == 0,
            $"两边行数不一致的表 {diffs.Count} 张（共比对 {tables.Count} 张）:\n  " +
            string.Join("\n  ", diffs.Take(20)));
    }
}
