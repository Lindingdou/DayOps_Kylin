using PitMine3D.Kylin.Views;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>命令行精确坐标解析（绝对/相对/极坐标）。</summary>
public class CoordInputTests
{
    [Fact]
    public void Absolute_xy()
    {
        var p = MainWindow.ParseCoord("10,20", null);
        Assert.NotNull(p);
        Assert.Equal(10, p!.Value.x, 6);
        Assert.Equal(20, p!.Value.y, 6);
    }

    [Fact]
    public void Relative_xy_adds_to_last()
    {
        var p = MainWindow.ParseCoord("@5,3", (10, 20));
        Assert.Equal(15, p!.Value.x, 6);
        Assert.Equal(23, p!.Value.y, 6);
    }

    [Fact]
    public void Relative_without_last_is_null()
    {
        Assert.Null(MainWindow.ParseCoord("@5,3", null));
    }

    [Fact]
    public void Polar_absolute_from_origin()
    {
        var p = MainWindow.ParseCoord("10<90", null);   // 90° → +Y
        Assert.Equal(0, p!.Value.x, 4);
        Assert.Equal(10, p!.Value.y, 4);
    }

    [Fact]
    public void Polar_relative_from_last()
    {
        var p = MainWindow.ParseCoord("@10<0", (5, 5));  // 0° → +X
        Assert.Equal(15, p!.Value.x, 4);
        Assert.Equal(5, p!.Value.y, 4);
    }

    [Fact]
    public void Negative_and_decimal()
    {
        var p = MainWindow.ParseCoord("-2.5,3.5", null);
        Assert.Equal(-2.5, p!.Value.x, 6);
        Assert.Equal(3.5, p!.Value.y, 6);
    }

    [Theory]
    [InlineData("LINE")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("10")]
    public void Non_coordinate_is_null(string s)
    {
        Assert.Null(MainWindow.ParseCoord(s, (0, 0)));
    }
}
