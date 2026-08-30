using PitMine3D.Kylin.Views;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>空命令行 + Enter 重复上次命令的解析回归（AutoCAD 行为）。</summary>
public class RepeatCommandTests
{
    [Fact]
    public void Empty_uses_last_command()
        => Assert.Equal("LINE", MainWindow.RepeatCommand("", "LINE"));

    [Fact]
    public void Typed_overrides_last()
        => Assert.Equal("CIRCLE", MainWindow.RepeatCommand("CIRCLE", "LINE"));

    [Fact]
    public void Empty_with_no_last_returns_null()
        => Assert.Null(MainWindow.RepeatCommand("", null));
}
