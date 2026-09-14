using System;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>文件管理器树（同原版 fileSystemTree）：懒加载 / 按格式分组 / 刷新保展开 / 工作目录增删。</summary>
public class FileSystemTreeTests : IDisposable
{
    private readonly string _dir;

    public FileSystemTreeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pm_fstree_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Touch(string rel)
    {
        string p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "");
    }

    private FileSystemNode DirNode(string path) => new()
    {
        Name = Path.GetFileName(path), FullPath = path, IsDirectory = true, Icon = "📁",
    };

    [Fact]
    public void 目录未展开时不读盘_展开才加载()
    {
        Touch("a.dxf");
        var node = DirNode(_dir);
        node.EnsureExpanderVisible();

        Assert.Single(node.Children);
        Assert.Equal("加载中...", node.Children[0].Name);   // 只有占位, 撑出展开箭头

        node.IsExpanded = true;                              // 展开即读盘

        Assert.Single(node.Children);
        Assert.True(node.Children[0].IsGroup);
        Assert.Equal("dxf文件 (1)", node.Children[0].Name);
    }

    [Fact]
    public void 文件按格式分组_只列支持的格式_组内排序()
    {
        Touch("b.dxf"); Touch("a.dxf"); Touch("m.dwg");
        Touch("t.txt"); Touch("s.pdf");                      // 不支持 → 不显示

        var node = DirNode(_dir);
        node.LoadChildren();

        var groups = node.Children.Where(c => c.IsGroup).ToList();
        Assert.Equal(2, groups.Count);
        Assert.Equal("dwg文件 (1)", groups[0].Name);         // 分组顺序同原版 FileGroupOrder: dwg 在 dxf 前
        Assert.Equal("dxf文件 (2)", groups[1].Name);
        Assert.Equal(new[] { "a.dxf", "b.dxf" }, groups[1].Children.Select(c => c.Name).ToArray());
        Assert.True(groups[1].IsExpanded);                   // 分组默认展开
        Assert.All(groups[1].Children, c => Assert.Equal("📐", c.Icon));   // 同组同图标
        Assert.DoesNotContain(node.Children, c => c.Name.EndsWith(".txt"));
    }

    [Fact]
    public void 子目录在前_始终显示以便导航()
    {
        Touch("sub/inner.dxf");
        Touch("top.dxf");

        var node = DirNode(_dir);
        node.LoadChildren();

        Assert.True(node.Children[0].IsDirectory);
        Assert.Equal("sub", node.Children[0].Name);
        Assert.True(node.Children[1].IsGroup);
        // 子目录只放占位, 内容等展开
        Assert.Equal("加载中...", node.Children[0].Children[0].Name);
    }

    [Fact]
    public void 刷新拾取新增文件并保留展开层级()
    {
        Touch("sub/one.dxf");
        var node = DirNode(_dir);
        node.IsExpanded = true;
        var sub = node.Children.First(c => c.IsDirectory);
        sub.IsExpanded = true;
        Assert.Equal("dxf文件 (1)", sub.Children[0].Name);

        Touch("sub/two.dxf");
        Touch("three.dwg");
        node.Refresh();

        var sub2 = node.Children.First(c => c.IsDirectory);
        Assert.True(sub2.IsExpanded);                        // 展开层级保住
        Assert.Equal("dxf文件 (2)", sub2.Children[0].Name);   // 新文件已拾取
        Assert.Contains(node.Children, c => c.IsGroup && c.Name == "dwg文件 (1)");
    }

    [Fact]
    public void 缺失目录不抛异常_给出错误节点()
    {
        var node = DirNode(Path.Combine(_dir, "no_such_dir"));
        node.LoadChildren();
        Assert.Single(node.Children);
        Assert.Equal("⚠️", node.Children[0].Icon);
    }

    [Fact]
    public void 两个根_工作目录默认展开_我的电脑默认折叠()
    {
        var vm = new FileSystemViewModel();
        vm.Refresh();

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.True(vm.RootNodes[0].IsWorkingDirRoot);
        Assert.Equal("工作目录 (0)", vm.RootNodes[0].Name);
        Assert.True(vm.RootNodes[0].IsExpanded);
        Assert.True(vm.RootNodes[1].IsMyComputer);
        Assert.Equal("我的电脑", vm.RootNodes[1].Name);
        Assert.False(vm.RootNodes[1].IsExpanded);
        Assert.NotEmpty(vm.RootNodes[1].Children);           // 至少一个驱动器/挂载点
    }

    [Fact]
    public void 工作目录增删去重_无效路径不收()
    {
        var vm = new FileSystemViewModel();

        Assert.True(vm.AddWorkingDirectory(_dir));
        Assert.False(vm.AddWorkingDirectory(_dir));                          // 重复
        Assert.False(vm.AddWorkingDirectory(_dir + Path.DirectorySeparatorChar));  // 末尾分隔符 = 同一个
        Assert.False(vm.AddWorkingDirectory(Path.Combine(_dir, "nope")));    // 不存在
        Assert.False(vm.AddWorkingDirectory(""));
        Assert.Single(vm.WorkingDirectories);

        Assert.True(vm.RemoveWorkingDirectory(_dir));
        Assert.False(vm.RemoveWorkingDirectory(_dir));
        Assert.Empty(vm.WorkingDirectories);
    }

    [Fact]
    public void 工作目录子节点自动展开并列出内容()
    {
        Touch("a.dxf");
        var vm = new FileSystemViewModel();
        vm.AddWorkingDirectory(_dir);
        vm.Refresh();

        var workRoot = vm.RootNodes[0];
        Assert.Equal("工作目录 (1)", workRoot.Name);
        var child = Assert.Single(workRoot.Children);
        Assert.True(child.IsWorkingDir);
        Assert.StartsWith("⭐ ", child.Name);
        Assert.True(child.IsExpanded);                       // 工作目录默认展开
        Assert.Contains(child.Children, c => c.IsGroup && c.Name == "dxf文件 (1)");
    }
}
