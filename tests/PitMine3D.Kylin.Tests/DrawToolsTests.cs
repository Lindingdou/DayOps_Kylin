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

    [Fact]
    public void Circumcircle_of_unit_points()
    {
        var c = ArcMath.Circumcircle(1, 0, 0, 1, -1, 0);
        Assert.NotNull(c);
        Assert.Equal(0, c!.Value.cx, 4);
        Assert.Equal(0, c!.Value.cy, 4);
        Assert.Equal(1, c!.Value.r, 4);
    }

    [Fact]
    public void Circumcircle_collinear_is_null()
    {
        Assert.Null(ArcMath.Circumcircle(0, 0, 1, 0, 2, 0));
    }

    [Fact]
    public void ArcTool_three_points_make_arc()
    {
        var t = new ArcTool();
        Assert.Null(t.AddPoint(1, 0));
        Assert.Null(t.AddPoint(0, 1));
        var arc = Assert.IsType<ArcEntity>(t.AddPoint(-1, 0));
        Assert.Equal(-1, arc.X3);
    }

    [Fact]
    public void PolylineTool_accumulates_and_finishes()
    {
        var t = new PolylineTool();
        Assert.Null(t.AddPoint(0, 0));
        Assert.Null(t.AddPoint(10, 0));
        Assert.Null(t.AddPoint(10, 10));
        var pl = Assert.IsType<PolylineEntity>(t.Finish());
        Assert.Equal(3, pl.Points.Count);
    }

    [Fact]
    public void PolylineTool_finish_too_few_is_null()
    {
        var t = new PolylineTool();
        t.AddPoint(0, 0);
        Assert.Null(t.Finish());   // 只 1 点，不成线
    }
}
