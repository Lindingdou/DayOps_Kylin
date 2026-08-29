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
}
