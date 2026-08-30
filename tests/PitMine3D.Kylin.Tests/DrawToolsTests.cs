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

    [Fact]
    public void LineEntity_offset_perpendicular()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };   // 沿 X 轴
        var off = (LineEntity)line.Offset(5, 3)!;                        // 点击上方 3
        Assert.Equal(3, off.Y0, 4);
        Assert.Equal(3, off.Y1, 4);
    }

    [Fact]
    public void CircleEntity_offset_to_click_radius()
    {
        var c = new CircleEntity { Cx = 0, Cy = 0, Radius = 2 };
        var off = (CircleEntity)c.Offset(5, 0)!;                         // 点击距心 5
        Assert.Equal(5, off.Radius, 4);
    }

    [Fact]
    public void LineMath_intersect_crossing_lines()
    {
        // 水平线 y=0 与 竖直线 x=5 → (5,0)
        var p = LineMath.IntersectInfinite(0, 0, 10, 0, 5, -5, 5, 5);
        Assert.NotNull(p);
        Assert.Equal(5, p!.Value.x, 4);
        Assert.Equal(0, p!.Value.y, 4);
    }

    [Fact]
    public void LineMath_parallel_is_null()
    {
        Assert.Null(LineMath.IntersectInfinite(0, 0, 10, 0, 0, 5, 10, 5));   // 两条水平线
    }

    [Fact]
    public void RectEntity_explodes_to_4_lines()
    {
        var parts = new RectEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 5 }.Explode();
        Assert.NotNull(parts);
        Assert.Equal(4, parts!.Count);
        Assert.All(parts, p => Assert.IsType<LineEntity>(p));
    }

    [Fact]
    public void PolylineEntity_explodes_to_segments()
    {
        var pl = new PolylineEntity { Closed = false };
        pl.Points.Add((0, 0)); pl.Points.Add((10, 0)); pl.Points.Add((10, 10));
        Assert.Equal(2, pl.Explode()!.Count);       // 3 点 → 2 段
        pl.Closed = true;
        Assert.Equal(3, pl.Explode()!.Count);       // 闭合 → +1
    }

    [Fact]
    public void LineEntity_not_explodable()
    {
        Assert.Null(new LineEntity().Explode());
    }

    [Fact]
    public void LineTool_preview_rubber_band_needs_cursor()
    {
        var t = new LineTool();
        t.AddPoint(0, 0);                              // 已点起点
        var o = new System.Collections.Generic.List<float>();
        t.AppendPreview(o, null);                      // 无光标 → 无预览
        Assert.Empty(o);
        t.AppendPreview(o, (10, 5));                   // 有光标 → 1 段 = 12 float
        Assert.Equal(12, o.Count);
    }

    [Fact]
    public void PolylineTool_preview_adds_cursor_segment()
    {
        var t = new PolylineTool();
        t.AddPoint(0, 0); t.AddPoint(10, 0);           // 1 已点段
        var o = new System.Collections.Generic.List<float>();
        t.AppendPreview(o, (10, 10));                  // + 橡皮筋段 → 2 段 = 24 float
        Assert.Equal(24, o.Count);
    }

    [Fact]
    public void PolygonTool_center_vertex_make_polygon()
    {
        var t = new PolygonTool { Sides = 6 };
        Assert.Null(t.AddPoint(0, 0));                 // 中心
        var p = Assert.IsType<PolygonEntity>(t.AddPoint(10, 0));   // 顶点方向 +X
        Assert.Equal(6, p.Sides);
        Assert.Equal(10, p.Radius, 6);
        Assert.Equal(0, p.Rotation, 6);
    }

    [Fact]
    public void PolygonEntity_explode_and_tessellate_n_edges()
    {
        var p = new PolygonEntity { Cx = 0, Cy = 0, Radius = 5, Sides = 5 };
        Assert.Equal(5, p.Explode()!.Count);          // 五边形 → 5 边
        var o = new System.Collections.Generic.List<float>();
        p.Tessellate(o);
        Assert.Equal(5 * 12, o.Count);                // 5 段 × 12 float
    }

    [Fact]
    public void PolygonEntity_move_preserves_polygon()
    {
        var p = new PolygonEntity { Cx = 0, Cy = 0, Radius = 5, Sides = 4 };
        var moved = Assert.IsType<PolygonEntity>(p.Apply(Affine2.Translate(10, 0)));
        Assert.Equal(10, moved.Cx, 6);
        Assert.Equal(5, moved.Radius, 6);
        Assert.Equal(4, moved.Sides);
    }

    [Fact]
    public void LineEntity_break_removes_middle()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var parts = line.Break(3, 0, 7, 0);           // 移除 [3,7]
        Assert.NotNull(parts);
        Assert.Equal(2, parts!.Count);
        var a = Assert.IsType<LineEntity>(parts[0]);
        Assert.Equal(0, a.X0, 6); Assert.Equal(3, a.X1, 6);
        var b = Assert.IsType<LineEntity>(parts[1]);
        Assert.Equal(7, b.X0, 6); Assert.Equal(10, b.X1, 6);
    }

    [Fact]
    public void LineEntity_break_at_end_leaves_one()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var parts = line.Break(0, 0, 4, 0);           // 从起点断到 4 → 只剩 [4,10]
        Assert.Single(parts!);
        Assert.Equal(4, ((LineEntity)parts![0]).X0, 6);
        Assert.Equal(10, ((LineEntity)parts![0]).X1, 6);
    }

    [Fact]
    public void Circle2PTool_diameter_endpoints()
    {
        var t = new Circle2PTool();
        Assert.Null(t.AddPoint(0, 0));
        var c = Assert.IsType<CircleEntity>(t.AddPoint(10, 0));   // 直径两端
        Assert.Equal(5, c.Cx, 6); Assert.Equal(0, c.Cy, 6);
        Assert.Equal(5, c.Radius, 6);
    }

    [Fact]
    public void Circle3PTool_through_three_points()
    {
        var t = new Circle3PTool();
        Assert.Null(t.AddPoint(1, 0));
        Assert.Null(t.AddPoint(0, 1));
        var c = Assert.IsType<CircleEntity>(t.AddPoint(-1, 0));   // 单位圆
        Assert.Equal(0, c.Cx, 4); Assert.Equal(0, c.Cy, 4);
        Assert.Equal(1, c.Radius, 4);
    }

    [Fact]
    public void Circle3PTool_collinear_yields_null()
    {
        var t = new Circle3PTool();
        t.AddPoint(0, 0); t.AddPoint(1, 0);
        Assert.Null(t.AddPoint(2, 0));   // 三点共线 → 无圆
    }

    [Fact]
    public void Scene_buildgeometry_skips_hidden_layer()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0, LayerName = "on" });
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0, LayerName = "off" });
        Assert.Equal(24, s.BuildGeometry().Length);              // 2 段全画
        Assert.Equal(12, s.BuildGeometry(n => n != "off").Length); // 隐藏 off → 1 段
    }

    [Fact]
    public void Scene_pick_skips_locked_layer()
    {
        var s = new Scene();
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, LayerName = "locked" };
        s.Add(line);
        Assert.Null(s.Pick(5, 0, 1.0, n => n != "locked"));   // 锁定层不可选
        Assert.Same(line, s.Pick(5, 0, 1.0, n => true));      // 无过滤可选
    }
}
