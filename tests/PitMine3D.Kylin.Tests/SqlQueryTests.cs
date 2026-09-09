using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>只读 SQL 查询执行(GeoDataQueries.RunSelectCsv)回归 —— 忠实原 SqlLib SQL Console 查询侧。</summary>
public class SqlQueryTests
{
    [Fact]
    public void Select_returns_header_and_rows()
    {
        using var db = TestDb.Open();
        var (ok, text, rows) = GeoDataQueries.RunSelectCsv(db.Connection,
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT 3");
        Assert.True(ok, text);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal("name", lines[0]);          // 表头=列名
        Assert.Equal(3, rows);
        Assert.Equal(4, lines.Length);           // 表头 + 3 行
    }

    [Fact]
    public void Aggregate_query_runs()
    {
        using var db = TestDb.Open();
        var (ok, text, rows) = GeoDataQueries.RunSelectCsv(db.Connection,
            "SELECT COUNT(*) AS n FROM sqlite_master WHERE type='table'");
        Assert.True(ok, text);
        Assert.Equal(1, rows);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal("n", lines[0]);
        Assert.True(int.Parse(lines[1]) > 0);    // 种子库有表
    }

    [Fact]
    public void Pragma_allowed()
    {
        using var db = TestDb.Open();
        var (ok, _, _) = GeoDataQueries.RunSelectCsv(db.Connection, "PRAGMA table_list");
        Assert.True(ok);
    }

    [Theory]
    [InlineData("DELETE FROM equipment")]
    [InlineData("UPDATE equipment SET x=1")]
    [InlineData("DROP TABLE equipment")]
    [InlineData("INSERT INTO equipment VALUES (1)")]
    public void Non_readonly_rejected(string sql)
    {
        using var db = TestDb.Open();
        var (ok, text, _) = GeoDataQueries.RunSelectCsv(db.Connection, sql);
        Assert.False(ok);
        Assert.Contains("只读", text);
    }

    [Fact]
    public void IsReadOnly_classifies_correctly()
    {
        Assert.True(GeoDataQueries.IsReadOnlySql("  select 1"));           // 前导空格+小写
        Assert.True(GeoDataQueries.IsReadOnlySql("WITH t AS (SELECT 1) SELECT * FROM t"));
        Assert.True(GeoDataQueries.IsReadOnlySql("-- 注释\nSELECT 1"));    // 前导注释
        Assert.False(GeoDataQueries.IsReadOnlySql("delete from x"));
        Assert.False(GeoDataQueries.IsReadOnlySql(""));
    }

    [Fact]
    public void Bad_sql_returns_error_not_throw()
    {
        using var db = TestDb.Open();
        var (ok, text, _) = GeoDataQueries.RunSelectCsv(db.Connection, "SELECT * FROM no_such_table_xyz");
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(text));   // 返回错误信息而非抛出
    }
}
