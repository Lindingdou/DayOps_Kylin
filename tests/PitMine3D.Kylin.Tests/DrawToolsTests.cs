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

    [Fact]
    public void LineEntity_distance_to_point()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        Assert.Equal(2, line.DistanceTo(5, 2), 4);   // 垂距
        Assert.Equal(0, line.DistanceTo(5, 0), 4);   // 线上
    }

    [Fact]
    public void Scene_pick_within_tolerance()
    {
        var s = new Scene();
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        s.Add(line);
        Assert.Same(line, s.Pick(5, 0.5, 1.0));   // 容差内命中
        Assert.Null(s.Pick(5, 5, 1.0));           // 容差外
    }

    [Fact]
    public void Affine_translate_moves_line()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var moved = (LineEntity)line.Apply(Affine2.Translate(5, 3));
        Assert.Equal(5, moved.X0); Assert.Equal(3, moved.Y0);
        Assert.Equal(15, moved.X1); Assert.Equal(3, moved.Y1);
    }

    [Fact]
    public void Affine_rotate90_about_origin()
    {
        var line = new LineEntity { X0 = 1, Y0 = 0, X1 = 2, Y1 = 0 };
        var r = (LineEntity)line.Apply(Affine2.Rotate(System.Math.PI / 2, 0, 0));
        Assert.Equal(0, r.X0, 4); Assert.Equal(1, r.Y0, 4);   // (1,0)→(0,1)
        Assert.Equal(0, r.X1, 4); Assert.Equal(2, r.Y1, 4);   // (2,0)→(0,2)
    }

    [Fact]
    public void Affine_mirror_across_x_axis()
    {
        var line = new LineEntity { X0 = 0, Y0 = 5, X1 = 10, Y1 = 5 };
        var mir = (LineEntity)line.Apply(Affine2.MirrorLine(0, 0, 1, 0));   // 沿 X 轴镜像
        Assert.Equal(-5, mir.Y0, 4); Assert.Equal(-5, mir.Y1, 4);
    }

    [Fact]
    public void Affine_scale_circle_radius()
    {
        var c = new CircleEntity { Cx = 0, Cy = 0, Radius = 2 };
        var s = (CircleEntity)c.Apply(Affine2.Scale(3, 0, 0));
        Assert.Equal(6, s.Radius, 4);
    }
}
