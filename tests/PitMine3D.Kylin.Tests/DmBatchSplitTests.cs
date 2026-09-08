using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 达梦迁移的语句拆分器。
///
/// 为什么单独测: SQLite 的 Provider 允许一次 ExecuteNonQuery 吞整份脚本, 达梦不行 ——
/// 必须按分号拆开逐条喂。而分号并不都是语句边界: 触发器 BEGIN…END 体内的分号、
/// 字符串字面量里的分号、注释里的分号, 拆错了迁移就崩在半路。
/// 这是纯函数, 不碰任何数据库实例, 所以在没有 DM 服务器的情况下也能把这一环钉死。
/// </summary>
public class DmBatchSplitTests
{
    private static IReadOnlyList<string> Split(string sql) => new DmDialect().SplitBatch(sql);

    [Fact]
    public void 两条普通语句_拆成两条()
    {
        var parts = Split("CREATE TABLE a(x INT); INSERT INTO a VALUES(1);");
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("CREATE TABLE", parts[0]);
        Assert.StartsWith("INSERT INTO", parts[1]);
    }

    [Fact]
    public void 末尾没有分号_最后一条也不丢()
    {
        var parts = Split("CREATE TABLE a(x INT); INSERT INTO a VALUES(1)");
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("INSERT INTO", parts[1]);
    }

    [Fact]
    public void 字符串字面量里的分号_不拆()
    {
        var parts = Split("INSERT INTO t(note) VALUES('前段;后段');");
        Assert.Single(parts);
        Assert.Contains("前段;后段", parts[0]);
    }

    [Fact]
    public void 字面量里的转义单引号_不会误判字符串提前结束()
    {
        // 'it''s; here' 是一个完整字面量, 里面的分号不是边界
        var parts = Split("INSERT INTO t(v) VALUES('it''s; here'); SELECT 1;");
        Assert.Equal(2, parts.Count);
        Assert.Contains("it''s; here", parts[0]);
        Assert.StartsWith("SELECT", parts[1]);
    }

    [Fact]
    public void 触发器体内的分号_不拆()
    {
        const string sql = @"
CREATE TRIGGER trg_x AFTER UPDATE ON equipment
FOR EACH ROW
BEGIN
    UPDATE equipment SET updated_at = CURRENT_TIMESTAMP WHERE equipment_id = NEW.equipment_id;
END;
INSERT INTO a VALUES(1);";
        var parts = Split(sql);
        Assert.Equal(2, parts.Count);
        Assert.Contains("CREATE TRIGGER", parts[0]);
        Assert.Contains("END", parts[0]);
        Assert.StartsWith("INSERT INTO", parts[1]);
    }

    [Fact]
    public void 行注释里的分号_不拆()
    {
        var parts = Split("-- 这里有个分号; 但它在注释里\nCREATE TABLE a(x INT);");
        Assert.Single(parts);
        Assert.Contains("CREATE TABLE", parts[0]);
    }

    [Fact]
    public void 块注释里的分号_不拆()
    {
        var parts = Split("/* a; b */ CREATE TABLE a(x INT);");
        Assert.Single(parts);
        Assert.Contains("CREATE TABLE", parts[0]);
    }

    [Fact]
    public void 空语句与多余分号_被丢弃()
    {
        var parts = Split("CREATE TABLE a(x INT);;;\n\n;");
        Assert.Single(parts);
    }

    // ── 拿 50 份真实迁移当真数据跑 ────────────────────────────────────────────

    public static IEnumerable<object[]> 真实迁移脚本()
    {
        var asm = typeof(GeoDatabase).Assembly;
        foreach (var res in asm.GetManifestResourceNames()
                     .Where(n => n.Contains(".Data.Migrations.") && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
            yield return new object[] { res };
    }

    private static string Read(string res)
    {
        using var s = typeof(GeoDatabase).Assembly.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>
    /// 每条拆出来的语句, 去掉前导注释/空白后, 都必须以一个合法的 SQL 起始关键字开头。
    /// 拆错(比如在触发器中间断开)会留下 "END"、"UPDATE …" 这类残片, 这条就会红。
    /// </summary>
    [Theory]
    [MemberData(nameof(真实迁移脚本))]
    public void 真实迁移_每条语句都以合法关键字开头(string res)
    {
        string[] 合法起手 = { "CREATE", "INSERT", "UPDATE", "DELETE", "ALTER", "DROP", "PRAGMA", "WITH", "SELECT", "REPLACE" };

        foreach (var stmt in Split(Read(res)))
        {
            string head = StripLeadingComments(stmt);
            if (head.Length == 0) continue;
            Assert.True(
                合法起手.Any(k => head.StartsWith(k, StringComparison.OrdinalIgnoreCase)),
                $"{res} 拆出不合法语句片段：\n{head[..Math.Min(160, head.Length)]}");
        }
    }

    /// <summary>拆分不得丢字符：把所有片段拼回去, 去掉空白和分号后应与原文一致。</summary>
    [Theory]
    [MemberData(nameof(真实迁移脚本))]
    public void 真实迁移_拆分不丢内容(string res)
    {
        string src = Read(res);
        string joined = string.Concat(Split(src));
        Assert.Equal(Canon(src), Canon(joined));
    }

    private static string Canon(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c) && c != ';') sb.Append(c);
        return sb.ToString();
    }

    private static string StripLeadingComments(string stmt)
    {
        int i = 0;
        while (i < stmt.Length)
        {
            while (i < stmt.Length && char.IsWhiteSpace(stmt[i])) i++;
            if (i + 1 < stmt.Length && stmt[i] == '-' && stmt[i + 1] == '-')
            {
                while (i < stmt.Length && stmt[i] != '\n') i++;
                continue;
            }
            if (i + 1 < stmt.Length && stmt[i] == '/' && stmt[i + 1] == '*')
            {
                int e = stmt.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (e < 0) return "";
                i = e + 2;
                continue;
            }
            break;
        }
        return stmt.Substring(i);
    }
}
