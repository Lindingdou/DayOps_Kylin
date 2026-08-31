using System.IO;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>文件管理器：磁盘 .dxf 枚举回归。</summary>
public class FileBrowserTests
{
    [Fact]
    public void ListDxf_lists_only_dxf_sorted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_filebrowser_test");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "b.dxf"), "");
        File.WriteAllText(Path.Combine(dir, "a.dxf"), "");
        File.WriteAllText(Path.Combine(dir, "c.txt"), "");   // 非 dxf，应忽略

        var list = CadFileBrowser.ListDxf(dir);

        Assert.Equal(2, list.Count);
        Assert.Equal("a.dxf", list[0].Name);   // 已排序
        Assert.Equal("b.dxf", list[1].Name);

        try { Directory.Delete(dir, true); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void ListDxf_missing_dir_returns_empty()
    {
        var list = CadFileBrowser.ListDxf(Path.Combine(Path.GetTempPath(), "pm_no_such_dir_xyz"));
        Assert.Empty(list);
    }

    [Fact]
    public void ListImportable_lists_all_import_formats()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_filebrowser_importable_" + System.Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(dir);
        try
        {
            // 各可导入格式 + 一个非导入格式
            foreach (var name in new[] { "a.dxf", "b.dwg", "c.off", "d.wl", "e.wt", "f.wp", "g.mpj", "h.kdf", "i.3dm", "skip.txt", "skip.pdf" })
                File.WriteAllText(Path.Combine(dir, name), "");

            var list = CadFileBrowser.ListImportable(dir);

            Assert.Equal(9, list.Count);                                   // 9 种可导入格式全列, .txt/.pdf 忽略
            Assert.Contains(list, x => x.Name == "d.wl");                  // MapGIS
            Assert.Contains(list, x => x.Name == "h.kdf");                 // KDF
            Assert.Contains(list, x => x.Name == "i.3dm");                 // 3DMine
            Assert.DoesNotContain(list, x => x.Name == "skip.txt");
            Assert.DoesNotContain(list, x => x.Name == "skip.pdf");
            // 大小写不敏感
            File.WriteAllText(Path.Combine(dir, "J.KDF"), "");
            Assert.Contains(CadFileBrowser.ListImportable(dir), x => x.Name == "J.KDF");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
