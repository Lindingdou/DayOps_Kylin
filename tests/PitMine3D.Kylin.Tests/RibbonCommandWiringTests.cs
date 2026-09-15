using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PitMine3D.Kylin.Tests;

public sealed class RibbonCommandWiringTests
{
    [Fact]
    public void 主标注按钮直接派发标注命令而不是只进入下拉选择对象()
    {
        string axaml = File.ReadAllText(FindRepoFile("src", "Views", "MainWindow.axaml"));
        int start = axaml.IndexOf("x:Name=\"DimFlyoutBtn\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到标注主按钮");
        int end = axaml.IndexOf("</Button>", start, StringComparison.Ordinal);
        Assert.True(end > start, "标注主按钮 XAML 不完整");

        string button = axaml[start..end];
        Assert.Contains("Tag=\"标注\"", button);
        Assert.Contains("Click=\"OnRibbonCommand\"", button);
    }

    [Fact]
    public void 标注取点流程不再调用对象边提取或提示选择对象()
    {
        string code = File.ReadAllText(FindRepoFile("src", "Views", "MainWindow.axaml.cs"));
        Assert.DoesNotContain("DimensionObjectTarget.Find", code);
        Assert.DoesNotContain("继续选择下一个对象", code);
    }

    [Fact]
    public void 标注取点状态使用十字光标而不是拾取方框()
    {
        string code = File.ReadAllText(FindRepoFile("src", "Views", "MainWindow.axaml.cs"));
        Assert.Contains("if (_dimActive) return CO;", code);
        Assert.DoesNotContain("if (_dimActive) return _dimAligned && (_dimP1 == null || _dimP2 == null) ? PB : CO;", code);
    }

    [Fact]
    public void 清理标记必须清除多段线临时标识并刷新视图()
    {
        string code = File.ReadAllText(FindRepoFile("src", "Views", "MainWindow.axaml.cs"));
        int start = code.IndexOf("private void ClrMark()", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到清理标记处理器");
        int end = code.IndexOf("\n    //", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "清理标记处理器边界不完整");
        string branch = code[start..end];
        Assert.Contains("_active.Overlay.Clear()", branch);
        Assert.Contains("RefreshScene()", branch);
    }

    [Fact]
    public void 多段线标识必须走独立的视图标记通道()
    {
        string code = File.ReadAllText(FindRepoFile("src", "Views", "MainWindow.axaml.cs"));
        Assert.Contains("Viewport.SetMarkerGeometry", code);
    }

    [Fact]
    public void 测量菜单事件必须拦截以免父按钮再次触发快速测量()
    {
        string code = File.ReadAllText(FindRepoFile("src", "Views", "MainWindow.axaml.cs"));
        int start = code.IndexOf("private async void OnRibbonCommand", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 Ribbon 命令处理器");
        int end = code.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "Ribbon 命令处理器边界不完整");
        Assert.Contains("e.Handled = true;", code[start..end]);
    }

    [Fact]
    public void 体积命令必须清理父按钮遗留的快速测距状态()
    {
        string code = File.ReadAllText(FindRepoFile("src", "Views", "MainWindow.Properties.cs"));
        int start = code.IndexOf("case \"测量体积\":", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到体积测量命令分支");
        int end = code.IndexOf("return true;", start, StringComparison.Ordinal);
        Assert.True(end > start, "体积测量命令分支不完整");
        string branch = code[start..end];
        Assert.Contains("_measure = null", branch);
        Assert.Contains("_angle = null", branch);
        Assert.Contains("_area = null", branch);
    }

    [Fact]
    public void 设备管理功能区不再显示机群驾驶舱和设备综合评分()
    {
        string axaml = File.ReadAllText(FindRepoFile("src", "Views", "MainWindow.axaml"));

        Assert.DoesNotContain("Tag=\"机群驾驶舱\"", axaml);
        Assert.DoesNotContain("Text=\"机群驾驶舱\"", axaml);
        Assert.DoesNotContain("Tag=\"设备综合评分\"", axaml);
        Assert.DoesNotContain("Text=\"设备综合评分\"", axaml);
    }

    private static string FindRepoFile(params string[] parts)
    {
        string? repoRoot = Environment.GetEnvironmentVariable("DAYOPS_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            string fromEnvironment = Path.Combine(new[] { repoRoot }.Concat(parts).ToArray());
            if (File.Exists(fromEnvironment)) return fromEnvironment;
        }

        string fromCurrentDirectory = Path.Combine(new[] { Directory.GetCurrentDirectory() }.Concat(parts).ToArray());
        if (File.Exists(fromCurrentDirectory)) return fromCurrentDirectory;

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string root = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(root)) return root;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("无法定位工作区文件", Path.Combine(parts));
    }
}
