using System;
using System.IO;
using PitMine3D.Kylin.Data;
using Xunit;

/// <summary>
/// 部署路径回归：装到 /opt 后程序目录属 root 不可写、临时目录重启会被清 ——
/// 库文件必须落在用户数据目录且可写。
/// </summary>
namespace PitMine3D.Kylin.Tests;

public class GeoDatabasePathTests
{
    [Fact]
    public void DefaultPath_IsWritableUserDataFile_NotAppDir()
    {
        string p = GeoDatabase.DefaultPath();
        Assert.False(string.IsNullOrWhiteSpace(p));
        Assert.Equal("geo.db", Path.GetFileName(p));
        var dir = Path.GetDirectoryName(p)!;
        Assert.True(Directory.Exists(dir), $"目录应已创建: {dir}");
        Assert.Contains("PitMine3D.Kylin", dir);
        // 不能落在程序安装目录(那里在麒麟上属 root)
        Assert.DoesNotContain(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), dir);
        // 真能写
        string probe = Path.Combine(dir, "write-probe.tmp");
        File.WriteAllText(probe, "x");
        Assert.True(File.Exists(probe));
        File.Delete(probe);
    }

    [Fact]
    public void OpenSeeded_AtDefaultPath_CreatesUsableDatabase()
    {
        string p = Path.Combine(Path.GetDirectoryName(GeoDatabase.DefaultPath())!, $"test-{Guid.NewGuid():N}.db");
        try
        {
            using var db = GeoDatabase.OpenSeeded(p);
            Assert.True(File.Exists(p));
            var tables = GeoDataQueries.ListTables(db.Connection);
            Assert.Contains("equipment", tables);
            Assert.True(tables.Count > 30, $"种子库应有数十张表, 实 {tables.Count}");
        }
        finally { try { File.Delete(p); } catch { } }
    }
}
