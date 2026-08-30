using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>托管绘制工具 + 场景回归。</summary>
public class DrawToolsTests
{
    [Fact]
    public void LineTool_two_points_make_line()
    {
        var t = new LineTool();
        Assert.Null(t.AddPoint(0, 0));                 // 起点
        var line = Assert.IsType<LineEntity>(t.AddPoint(10, 5));
        Assert.Equal(0, line.X0);
        Assert.Equal(10, line.X1);
        Assert.Equal(5, line.Y1);
    }

    [Fact]
    public void CircleTool_center_radius_make_circle()
    {
        var t = new CircleTool();
        Assert.Null(t.AddPoint(0, 0));                 // 圆心
        var c = Assert.IsType<CircleEntity>(t.AddPoint(3, 4));  // 半径点 → 5
        Assert.Equal(0, c.Cx);
        Assert.Equal(5, c.Radius, 6);
    }

    [Fact]
    public void RectTool_two_corners_make_rect()
    {
        var t = new RectTool();
        Assert.Null(t.AddPoint(0, 0));
        var r = Assert.IsType<RectEntity>(t.AddPoint(10, 20));
        Assert.Equal(10, r.X1);
        Assert.Equal(20, r.Y1);
    }

    [Fact]
    public void PointTool_single_click_makes_point()
    {
        var p = Assert.IsType<PointEntity>(new PointTool().AddPoint(3, 7));
        Assert.Equal(3, p.X);
        Assert.Equal(7, p.Y);
    }

    [Fact]
    public void Scene_builds_geometry_and_removes_last()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 });
        Assert.Equal(1, s.Count);
        Assert.Equal(12, s.BuildGeometry().Length);    // 1 段 = 2 顶点 × 6 float
        Assert.True(s.RemoveLast());
        Assert.Equal(0, s.Count);
    }
}
