using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// openGauss 版迁移脚本(src/Data/MigrationsPg, 由 build/sqlite2pg.py 生成)的静态校验。
///
/// 说明清楚这些测试能证明什么、不能证明什么:
///   能  —— 两套迁移不漂移(版本号一一对应)、没有 SQLite 独有写法漏网、
///           没有用到 openGauss 内核(PG 9.2)不具备的现代 PG 语法、
///           触发器已改成 PG 要求的"函数 + BEFORE 触发器"形式、序列已对齐。
///   不能 —— 证明 openGauss 真的能执行它们。那需要一台真实实例, 只有连上去跑一遍才算数。
/// 所以这里全是语法/结构层面的断言, 不假装是端到端验证。
/// </summary>
public class PgMigrationTests
{
    private const string SqliteMarker = ".Data.Migrations.";
    private const string PgMarker     = ".Data.MigrationsPg.";

    private static IEnumerable<string> Resources(string marker) =>
        typeof(GeoDatabase).Assembly.GetManifestResourceNames()
            .Where(n => n.Contains(marker) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);

    private static string Read(string res)
    {
        using var s = typeof(GeoDatabase).Assembly.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string Version(string res, string marker) =>
        res[(res.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..^4];

    /// <summary>两套迁移必须一一对应 —— 给 SQLite 加了迁移却忘了重跑生成器, 这条会红。</summary>
    [Fact]
    public void openGauss版与SQLite版的迁移版本号完全一致()
    {
        var sqlite = Resources(SqliteMarker).Select(r => Version(r, SqliteMarker)).ToList();
        var pg     = Resources(PgMarker).Select(r => Version(r, PgMarker)).ToList();

        Assert.NotEmpty(sqlite);
        Assert.Equal(sqlite, pg);
    }

    public static IEnumerable<object[]> PG迁移() => Resources(PgMarker).Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(PG迁移))]
    public void 没有SQLite独有写法漏网(string res)
    {
        string sql = Strip(Read(res));

        Assert.DoesNotContain("AUTOINCREMENT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRAGMA", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT OR ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(Regex.IsMatch(sql, @"\bIFNULL\s*\(", RegexOptions.IgnoreCase), $"{res} 还有 IFNULL");
        // SQLite 的 REAL 是 8 字节浮点, PG 的 REAL 只有 4 字节 —— 必须换成 DOUBLE PRECISION,
        // 否则精度悄悄降一半, 高程/坐标这类数据会出问题。
        Assert.False(Regex.IsMatch(sql, @"\bREAL\b", RegexOptions.IgnoreCase), $"{res} 还有 REAL(精度会减半)");
    }

    /// <summary>
    /// openGauss 内核衍生自 PostgreSQL 9.2，不能用现代 PG 语法。
    /// 这条守的是"别哪天有人顺手改成 IDENTITY / EXECUTE FUNCTION 就跑不起来了"。
    /// </summary>
    [Theory]
    [MemberData(nameof(PG迁移))]
    public void 没有用到PG9_2之后才有的语法(string res)
    {
        string sql = Strip(Read(res));

        Assert.False(Regex.IsMatch(sql, @"\bGENERATED\b.*?\bAS\s+IDENTITY\b", RegexOptions.IgnoreCase),
            $"{res} 用了 IDENTITY(PG 10+)");
        Assert.False(Regex.IsMatch(sql, @"\bEXECUTE\s+FUNCTION\b", RegexOptions.IgnoreCase),
            $"{res} 用了 EXECUTE FUNCTION(PG 11+), 应为 EXECUTE PROCEDURE");
        Assert.False(Regex.IsMatch(sql, @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\b", RegexOptions.IgnoreCase),
            $"{res} 用了 CREATE INDEX IF NOT EXISTS(PG 9.5+)");
    }

    /// <summary>
    /// 触发器必须是"共用函数 + BEFORE 触发器"。
    /// 原来的 AFTER UPDATE 里回头 UPDATE 自己, 在 PG 下会递归触发(PG 触发器默认递归)。
    /// </summary>
    [Theory]
    [MemberData(nameof(PG迁移))]
    public void 触发器是BEFORE加函数形式(string res)
    {
        string sql = Strip(Read(res));
        foreach (Match m in Regex.Matches(sql, @"CREATE\s+TRIGGER\b.*?;", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string t = m.Value;
            Assert.True(Regex.IsMatch(t, @"BEFORE\s+UPDATE", RegexOptions.IgnoreCase),
                $"{res} 有触发器不是 BEFORE UPDATE：\n{t[..Math.Min(200, t.Length)]}");
            Assert.True(Regex.IsMatch(t, @"EXECUTE\s+PROCEDURE\s+fn_touch", RegexOptions.IgnoreCase),
                $"{res} 有触发器没走共用函数：\n{t[..Math.Min(200, t.Length)]}");
        }
    }

    /// <summary>用到共用触发器函数的文件, 必须自己先把函数建出来(迁移是各自独立执行的)。</summary>
    [Theory]
    [MemberData(nameof(PG迁移))]
    public void 用到触发器函数的文件都先建了函数(string res)
    {
        string sql = Read(res);
        foreach (var fn in new[] { "fn_touch_updated_at", "fn_touch_row" })
            if (Regex.IsMatch(sql, $@"EXECUTE\s+PROCEDURE\s+{fn}\s*\(", RegexOptions.IgnoreCase))
                Assert.True(sql.Contains($"CREATE OR REPLACE FUNCTION {fn}(", StringComparison.OrdinalIgnoreCase),
                    $"{res} 用了 {fn} 却没有先 CREATE OR REPLACE 它");
    }

    /// <summary>
    /// 种子数据带显式主键插进 SERIAL 列, 序列不会跟着前进；不对齐的话之后第一条
    /// 自增插入就撞主键。最后一个迁移必须把所有 SERIAL 表的序列对齐。
    /// </summary>
    [Fact]
    public void 最后一个迁移对齐了所有序列()
    {
        string last = Read(Resources(PgMarker).Last());

        int setvals = Regex.Matches(last, @"setval\s*\(\s*pg_get_serial_sequence", RegexOptions.IgnoreCase).Count;

        int serials = Resources(PgMarker)
            .Sum(r => Regex.Matches(Strip(Read(r)), @"\bSERIAL\b", RegexOptions.IgnoreCase).Count);

        Assert.True(serials > 0, "应当有 SERIAL 列");
        Assert.Equal(serials, setvals);
    }

    /// <summary>去掉注释与字符串字面量, 免得种子里的中文说明误伤断言。</summary>
    private static string Strip(string s)
    {
        s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"--[^\n]*", " ");
        return Regex.Replace(s, @"'(?:[^']|'')*'", "''");
    }
}
