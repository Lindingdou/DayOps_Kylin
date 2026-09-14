using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 数据库浏览（§三二六）：表格式结果集 `RunSelect`、行数、标识符加引号、表树分组。
/// 与 <see cref="SqlQueryTests"/>（CSV 出口）是同一条路的两个出口，守则一致：**非只读语句一律拒绝**。
/// </summary>
public class SqlBrowserTests
{
    [Fact]
    public void RunSelect_出列名与行()
    {
        using var db = TestDb.Open();
        var r = GeoDataQueries.RunSelect(db.Connection,
            "SELECT name, type FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT 3");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(new[] { "name", "type" }, r.Columns.ToArray());
        Assert.Equal(3, r.Rows.Count);
        Assert.All(r.Rows, row => Assert.Equal(2, row.Length));
        Assert.False(r.Truncated);
    }

    [Fact]
    public void RunSelect_拒绝写入语句()
    {
        using var db = TestDb.Open();
        foreach (var sql in new[] { "DELETE FROM equipment", "DROP TABLE equipment", "UPDATE equipment SET name='x'", "INSERT INTO equipment(id) VALUES(1)" })
        {
            var r = GeoDataQueries.RunSelect(db.Connection, sql);
            Assert.False(r.Ok, $"「{sql}」竟被放行了");
            Assert.Contains("只读", r.Error);
        }
    }

    [Fact]
    public void RunSelect_截断时如实标注()
    {
        using var db = TestDb.Open();
        var r = GeoDataQueries.RunSelect(db.Connection, "SELECT name FROM sqlite_master", maxRows: 2);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, r.Rows.Count);
        Assert.True(r.Truncated, "截断了却没标注 —— 会让人以为表就这么大");
    }

    [Fact]
    public void RunSelect_行数正好等于上限时不算截断()
    {
        // 边界：恰好取满不该报"截断"，否则每次拉满页都会挂一个假的"还有更多"
        using var db = TestDb.Open();
        int total = GeoDataQueries.RunSelect(db.Connection, "SELECT name FROM sqlite_master", 100000).Rows.Count;
        var r = GeoDataQueries.RunSelect(db.Connection, "SELECT name FROM sqlite_master", total);
        Assert.Equal(total, r.Rows.Count);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void RunSelect_语法错时回错不抛()
    {
        using var db = TestDb.Open();
        var r = GeoDataQueries.RunSelect(db.Connection, "SELECT * FROM 查无此表");
        Assert.False(r.Ok);
        Assert.NotEmpty(r.Error);
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void RunSelect_空连接与空查询都不崩()
    {
        Assert.False(GeoDataQueries.RunSelect(null!, "SELECT 1").Ok);
        using var db = TestDb.Open();
        Assert.False(GeoDataQueries.RunSelect(db.Connection, "   ").Ok);
    }

    [Fact]
    public void TableRowCount_能数出行数_查不到给负一()
    {
        using var db = TestDb.Open();
        var t = GeoDataQueries.ListTables(db.Connection, new SqliteDialect()).First();
        long n = GeoDataQueries.TableRowCount(db.Connection, t);
        Assert.True(n >= 0, $"「{t}」数不出行数");

        Assert.Equal(-1, GeoDataQueries.TableRowCount(db.Connection, "查无此表"));   // 不抛, 由调用方决定怎么显示
    }

    [Fact]
    public void 标识符加引号_内部引号要翻倍()
    {
        Assert.Equal("\"equipment\"", GeoDataQueries.QuoteIdent("equipment"));
        // 不翻倍的话, 名字里带引号的表就成了注入点
        Assert.Equal("\"a\"\"b\"", GeoDataQueries.QuoteIdent("a\"b"));
        Assert.Equal("\"\"", GeoDataQueries.QuoteIdent(""));
        Assert.Equal("\"\"", GeoDataQueries.QuoteIdent(null!));
    }

    [Fact]
    public void 加引号后的表名能真跑起来()
    {
        using var db = TestDb.Open();
        var t = GeoDataQueries.ListTables(db.Connection, new SqliteDialect()).First();
        var r = GeoDataQueries.RunSelect(db.Connection, $"SELECT * FROM {GeoDataQueries.QuoteIdent(t)} LIMIT 1");
        Assert.True(r.Ok, r.Error);
    }

    // ── 表树分组 ──────────────────────────────────────────────
    [Theory]
    [InlineData("equipment_kpi_monthly", "equipment")]
    [InlineData("coal_sample", "coal")]
    [InlineData("borehole", "borehole")]      // 无下划线 → 表名本身即组名
    [InlineData("_schema_migration", "系统")]  // 下划线开头归系统
    [InlineData("", "其它")]
    public void 表树按前缀分组(string table, string group)
        => Assert.Equal(group, PitMine3D.Kylin.Views.GeoDb.SqlBrowserWindow.GroupOf(table));

    [Fact]
    public void 种子库分组不会把所有表塞进一组()
    {
        using var db = TestDb.Open();
        var tables = GeoDataQueries.ListTables(db.Connection, new SqliteDialect());
        var groups = tables.Select(PitMine3D.Kylin.Views.GeoDb.SqlBrowserWindow.GroupOf).Distinct().ToList();
        Assert.True(groups.Count > 1, $"{tables.Count} 张表只分出 {groups.Count} 组, 树就没意义了");
    }
}
