using System.Collections.Generic;
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
    public void Hide_objects_removes_from_geometry_pick_and_snap_then_restore()
    {
        var s = new Scene();
        var a = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var b = new LineEntity { X0 = 0, Y0 = 5, X1 = 10, Y1 = 5 };
        s.Add(a); s.Add(b);
        int geomBoth = s.BuildGeometry().Length;
        Assert.Equal(24, geomBoth);                         // 2 段
        int snapBoth = s.SnapCandidates().Length;
        // 隐藏 a
        Assert.Equal(1, s.HideEntities(new[] { a }));
        Assert.Equal(1, s.HiddenCount);
        Assert.Equal(12, s.BuildGeometry().Length);         // 只剩 b 上屏
        Assert.True(s.SnapCandidates().Length < snapBoth);  // a 的捕捉点消失
        Assert.Null(s.Pick(5, 0, 0.5));                     // a 所在处不可拾取
        Assert.Same(b, s.Pick(5, 5, 0.5));                  // b 仍可拾取
        // 重复隐藏不双计
        Assert.Equal(0, s.HideEntities(new[] { a }));
        // 结束隐藏 → 全恢复
        Assert.Equal(1, s.ShowAllHidden());
        Assert.Equal(0, s.HiddenCount);
        Assert.Equal(geomBoth, s.BuildGeometry().Length);
        Assert.Same(a, s.Pick(5, 0, 0.5));                  // a 复现可拾取
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
    public void Transform_preserves_style_attributes()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0,
            Dash = DashPattern.ByName("虚线"), LineWeight = 25, Visible = false };
        var moved = (LineEntity)line.Apply(Affine2.Translate(5, 3));
        Assert.Equal(new[] { 6.0, 3.0 }, moved.Dash!);   // 线型随变换保留
        Assert.Equal(25, moved.LineWeight);              // 线宽随变换保留
        Assert.False(moved.Visible);                     // 可见性随变换保留(Colored 统一)
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

    [Fact]
    public void LineEntity_grips_and_move_endpoint()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        Assert.Equal(3, line.Grips().Count);                  // 两端 + 中点
        Assert.Equal((5, 0), line.Grips()[1]);                // 中点
        var m = (LineEntity)line.MoveGrip(2, 10, 5)!;         // 拖终点
        Assert.Equal(5, m.Y1, 6); Assert.Equal(0, m.Y0, 6);  // 只动终点
    }

    [Fact]
    public void LineEntity_move_midpoint_translates_whole()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var m = (LineEntity)line.MoveGrip(1, 5, 3)!;          // 中点上移 3
        Assert.Equal(3, m.Y0, 6); Assert.Equal(3, m.Y1, 6);  // 整体平移
    }

    [Fact]
    public void CircleEntity_grips_and_quadrant_sets_radius()
    {
        var c = new CircleEntity { Cx = 0, Cy = 0, Radius = 5 };
        Assert.Equal(5, c.Grips().Count);                     // 心 + 4 象限
        var m = (CircleEntity)c.MoveGrip(1, 8, 0)!;           // 拖右象限
        Assert.Equal(8, m.Radius, 6);
        var mc = (CircleEntity)c.MoveGrip(0, 3, 4)!;          // 拖圆心
        Assert.Equal(3, mc.Cx, 6); Assert.Equal(5, mc.Radius, 6);
    }

    [Fact]
    public void RectEntity_move_corner_keeps_opposite()
    {
        var r = new RectEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 10 };
        var m = (RectEntity)r.MoveGrip(2, 15, 20)!;           // 拖 (X1,Y1) 角
        Assert.Equal(15, m.X1, 6); Assert.Equal(20, m.Y1, 6);
        Assert.Equal(0, m.X0, 6); Assert.Equal(0, m.Y0, 6);  // 对角固定
    }

    [Fact]
    public void PolylineEntity_move_vertex()
    {
        var pl = new PolylineEntity();
        pl.Points.Add((0, 0)); pl.Points.Add((10, 0)); pl.Points.Add((10, 10));
        Assert.Equal(3, pl.Grips().Count);
        var m = (PolylineEntity)pl.MoveGrip(1, 5, 5)!;
        Assert.Equal((5, 5), m.Points[1]);
        Assert.Equal((0, 0), m.Points[0]);                    // 其余不变
    }

    [Fact]
    public void PolygonEntity_grip_vertex_sets_radius_and_rotation()
    {
        var pg = new PolygonEntity { Cx = 0, Cy = 0, Radius = 5, Sides = 6, Rotation = 0 };
        Assert.Equal(2, pg.Grips().Count);                    // 心 + 首顶点
        var m = (PolygonEntity)pg.MoveGrip(1, 0, 10)!;        // 首顶点移到 +Y
        Assert.Equal(10, m.Radius, 6);
        Assert.Equal(System.Math.PI / 2, m.Rotation, 4);
    }

    [Fact]
    public void PolylineEntity_offset_miters_corner()
    {
        var pl = new PolylineEntity();
        pl.Points.Add((0, 0)); pl.Points.Add((10, 0)); pl.Points.Add((10, 10));
        var off = (PolylineEntity)pl.Offset(5, -3)!;          // 点击第一段下方 3
        Assert.Equal(3, off.Points.Count);
        Assert.Equal(0, off.Points[0].x, 4); Assert.Equal(-3, off.Points[0].y, 4);
        Assert.Equal(13, off.Points[1].x, 4); Assert.Equal(-3, off.Points[1].y, 4);   // 外角 miter 交点
    }

    [Fact]
    public void PolylineEntity_break_splits_into_two()
    {
        var pl = new PolylineEntity();
        pl.Points.Add((0, 0)); pl.Points.Add((10, 0)); pl.Points.Add((10, 10));
        var parts = pl.Break(3, 0, 7, 0)!;                    // 在第一段 x=3..7 间打断
        Assert.Equal(2, parts.Count);
        var a = (PolylineEntity)parts[0]; var b = (PolylineEntity)parts[1];
        Assert.Equal(2, a.Points.Count);                     // (0,0),(3,0)
        Assert.Equal(3, b.Points.Count);                     // (7,0),(10,0),(10,10)
        Assert.Equal(3, a.Points[^1].x, 4);
        Assert.Equal(7, b.Points[0].x, 4);
    }

    [Fact]
    public void ArcEntity_offset_concentric_through_point()
    {
        var arc = new ArcEntity { X1 = 1, Y1 = 0, X2 = 0, Y2 = 1, X3 = -1, Y3 = 0 };   // 上半单位圆
        var off = (ArcEntity)arc.Offset(0, 2)!;              // 过 (0,2) → 半径 2
        Assert.Equal(2, off.X1, 4); Assert.Equal(0, off.Y1, 4);
        Assert.Equal(-2, off.X3, 4); Assert.Equal(0, off.Y3, 4);
    }

    [Fact]
    public void ArcEntity_break_into_two_arcs()
    {
        var arc = new ArcEntity { X1 = 1, Y1 = 0, X2 = 0, Y2 = 1, X3 = -1, Y3 = 0 };
        double c = System.Math.Cos(System.Math.PI / 4);
        var parts = arc.Break(c, c, -c, c)!;                 // 在 45°/135° 打断
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.IsType<ArcEntity>(p));
    }

    [Fact]
    public void CircleEntity_break_into_single_arc()
    {
        var circle = new CircleEntity { Cx = 0, Cy = 0, Radius = 1 };
        var parts = circle.Break(1, 0, 0, 1)!;              // 在 0°/90° 打断：移除 CCW 第一象限，留 270° 大弧
        Assert.Single(parts);
        var arc = Assert.IsType<ArcEntity>(parts[0]);
        // 保留段 = CCW 第二点(90°)→第一点(0°)：起点(0,1) 端点(1,0)
        Assert.Equal(0, arc.X1, 4); Assert.Equal(1, arc.Y1, 4);
        Assert.Equal(1, arc.X3, 4); Assert.Equal(0, arc.Y3, 4);
        // 中点在第三象限(225°)——证明保留的是大弧、移除的是第一象限
        Assert.True(arc.X2 < 0 && arc.Y2 < 0, $"中点应在第三象限, 实为({arc.X2:0.###},{arc.Y2:0.###})");
        // 三点均在圆上(半径 1)
        foreach (var (px, py) in new[] { (arc.X1, arc.Y1), (arc.X2, arc.Y2), (arc.X3, arc.Y3) })
            Assert.Equal(1.0, System.Math.Sqrt(px * px + py * py), 4);
    }

    [Fact]
    public void PolygonEntity_offset_concentric_keeps_sides_rotation()
    {
        var poly = new PolygonEntity { Cx = 0, Cy = 0, Radius = 5, Sides = 6, Rotation = 0.3 };
        var off = (PolygonEntity)poly.Offset(0, 10)!;        // 过 (0,10) → 新半径 10
        Assert.Equal(10, off.Radius, 4);
        Assert.Equal(6, off.Sides);                          // 边数不变
        Assert.Equal(0.3, off.Rotation, 6);                  // 朝向不变
        Assert.Equal(0, off.Cx, 6); Assert.Equal(0, off.Cy, 6);   // 同心
        Assert.Null(poly.Offset(0, 0));                      // 心上一点 → 半径0, 无效
    }

    [Fact]
    public void CircleEntity_break_coincident_points_returns_null()
    {
        var circle = new CircleEntity { Cx = 0, Cy = 0, Radius = 1 };
        Assert.Null(circle.Break(1, 0, 1, 0));             // 两点重合 → 无法打断
    }

    [Fact]
    public void RectEntity_break_into_open_polyline()
    {
        var rect = new RectEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 10 };
        // 两点都在左边(闭合边 (0,10)->(0,0))：验证闭合边可打断。移除 (0,7)..(0,3) 短段，
        // 保留补段=从(0,3)经(0,0)绕右侧回到(0,7)——含全部 4 角。
        var parts = rect.Break(0, 3, 0, 7)!;
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(parts));
        Assert.False(pl.Closed);                              // 开口
        var first = pl.Points[0]; var last = pl.Points[^1];
        bool ends = System.Math.Abs(first.x) < 1e-6 && System.Math.Abs(last.x) < 1e-6
                 && System.Math.Abs((first.y - 3) * (first.y - 7)) < 1e-6 && System.Math.Abs((last.y - 3) * (last.y - 7)) < 1e-6
                 && System.Math.Abs(first.y - last.y) > 1e-6;
        Assert.True(ends, $"端点应为(0,3)与(0,7), 实为({first.x},{first.y})..({last.x},{last.y})");
        // 保留段绕另一侧 → 含全部 4 角(闭合边被正确纳入投影)
        foreach (var (cx, cy) in new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0) })
            Assert.Contains(pl.Points, p => System.Math.Abs(p.x - cx) < 1e-6 && System.Math.Abs(p.y - cy) < 1e-6);
    }

    [Fact]
    public void PolygonEntity_break_into_open_polyline()
    {
        var poly = new PolygonEntity { Cx = 0, Cy = 0, Radius = 5, Sides = 4, Rotation = 0 };  // 顶点在 0/90/180/270°
        var parts = poly.Break(5, 0, 0, 5)!;                  // 在 (5,0)&(0,5) 附近打断
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(parts));
        Assert.False(pl.Closed);
        Assert.True(pl.Points.Count >= 2);
    }

    [Fact]
    public void SegmentsIntersect_detects_crossing()
    {
        Assert.True(LineMath.SegmentsIntersect(0, 0, 10, 0, 5, -5, 5, 5));    // 十字相交
        Assert.False(LineMath.SegmentsIntersect(0, 0, 10, 0, 0, 5, 10, 5));   // 平行
        Assert.False(LineMath.SegmentsIntersect(0, 0, 4, 0, 5, -5, 5, 5));    // 不够长
    }

    [Fact]
    public void SelectionBox_window_needs_full_containment()
    {
        var inside = new LineEntity { X0 = 2, Y0 = 2, X1 = 8, Y1 = 8 };
        var partial = new LineEntity { X0 = 2, Y0 = 2, X1 = 20, Y1 = 20 };
        Assert.True(SelectionBox.Match(inside, 0, 0, 10, 10, crossing: false));    // 全含 → 窗口选中
        Assert.False(SelectionBox.Match(partial, 0, 0, 10, 10, crossing: false));  // 部分 → 窗口不选
        Assert.True(SelectionBox.Match(partial, 0, 0, 10, 10, crossing: true));    // 部分 → 交叉选中
    }

    [Fact]
    public void SelectionBox_polygon_window_needs_all_inside()
    {
        var poly = new System.Collections.Generic.List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        var inside = new LineEntity { X0 = 2, Y0 = 2, X1 = 8, Y1 = 8 };
        var partial = new LineEntity { X0 = 5, Y0 = 5, X1 = 20, Y1 = 5 };
        Assert.True(SelectionBox.MatchPolygon(inside, poly, crossing: false));    // 全含
        Assert.False(SelectionBox.MatchPolygon(partial, poly, crossing: false));  // 部分出界
        Assert.True(SelectionBox.MatchPolygon(partial, poly, crossing: true));    // 交叉含部分
    }

    [Fact]
    public void SelectionBox_crossing_catches_passthrough()
    {
        var through = new LineEntity { X0 = -5, Y0 = 5, X1 = 15, Y1 = 5 };   // 横穿两端在外
        Assert.True(SelectionBox.Match(through, 0, 0, 10, 10, crossing: true));
        Assert.False(SelectionBox.Match(through, 0, 0, 10, 10, crossing: false));
        var outside = new LineEntity { X0 = 20, Y0 = 0, X1 = 20, Y1 = 20 };  // 完全在外
        Assert.False(SelectionBox.Match(outside, 0, 0, 10, 10, crossing: true));
    }

    [Fact]
    public void IntersectInfiniteWithSegment_hits_within_segment()
    {
        // 无限竖线 x=0 × 水平段 (-5,5)-(5,5) → (0,5)
        var p = LineMath.IntersectInfiniteWithSegment(0, 0, 0, 10, -5, 5, 5, 5);
        Assert.NotNull(p);
        Assert.Equal(0, p!.Value.x, 4); Assert.Equal(5, p!.Value.y, 4);
    }

    [Fact]
    public void IntersectInfiniteWithSegment_misses_outside_or_parallel()
    {
        Assert.Null(LineMath.IntersectInfiniteWithSegment(0, 0, 0, 10, 2, 5, 8, 5));   // 交点不在边界段内
        Assert.Null(LineMath.IntersectInfiniteWithSegment(0, 0, 10, 0, 0, 5, 10, 5));  // 平行
    }

    [Fact]
    public void TtrCenter_tangent_to_two_axes()
    {
        // x轴(点击上方) + y轴(点击右侧), r=2 → 第一象限内切圆 心(2,2)
        var c = LineMath.TtrCenter(0, 0, 10, 0, 5, 1, 0, 0, 0, 10, 1, 5, 2);
        Assert.NotNull(c);
        Assert.Equal(2, c!.Value.x, 4);
        Assert.Equal(2, c!.Value.y, 4);
    }

    [Fact]
    public void TtrCenter_parallel_lines_is_null()
    {
        Assert.Null(LineMath.TtrCenter(0, 0, 10, 0, 5, 1, 0, 5, 10, 5, 5, 6, 2));   // 两平行线
    }

    [Fact]
    public void IntersectLineCircle_two_points()
    {
        // 竖线 x=0 × 单位圆 → (0,1)/(0,-1)
        var ps = LineMath.IntersectLineCircle(0, -5, 0, 5, 0, 0, 1);
        Assert.Equal(2, ps.Count);
        Assert.Contains(ps, p => System.Math.Abs(p.y - 1) < 1e-4);
        Assert.Contains(ps, p => System.Math.Abs(p.y + 1) < 1e-4);
    }

    [Fact]
    public void IntersectLineCircle_miss_is_empty()
    {
        Assert.Empty(LineMath.IntersectLineCircle(5, -5, 5, 5, 0, 0, 1));   // x=5 离圆太远
    }

    [Fact]
    public void IntersectCircleCircle_two_points()
    {
        // 圆(0,0,1) 与 圆(1,0,1) → 两交点 x=0.5
        var ps = LineMath.IntersectCircleCircle(0, 0, 1, 1, 0, 1);
        Assert.Equal(2, ps.Count);
        Assert.All(ps, p => Assert.Equal(0.5, p.x, 4));
    }

    [Fact]
    public void IntersectCircleCircle_disjoint_is_empty()
    {
        Assert.Empty(LineMath.IntersectCircleCircle(0, 0, 1, 10, 0, 1));   // 相离
    }

    [Fact]
    public void ArcMath_from_start_center_end_projects_and_bisects()
    {
        // 起点(1,0) 心(0,0) 端点(0,5)→投影到半径1的(0,1)，中点在45°
        var t = ArcMath.FromStartCenterEnd(1, 0, 0, 0, 0, 5)!.Value;
        Assert.Equal(1, t.x1, 4); Assert.Equal(0, t.y1, 4);
        Assert.Equal(0, t.x3, 4); Assert.Equal(1, t.y3, 4);
        Assert.Equal(System.Math.Cos(System.Math.PI / 4), t.x2, 4);
        Assert.Equal(System.Math.Sin(System.Math.PI / 4), t.y2, 4);
    }

    [Fact]
    public void ArcSceTool_start_center_end_makes_arc()
    {
        var t = new ArcSceTool();
        Assert.Null(t.AddPoint(1, 0));      // 起点
        Assert.Null(t.AddPoint(0, 0));      // 圆心
        var arc = Assert.IsType<ArcEntity>(t.AddPoint(0, 1));   // 端点 → 四分之一弧
        Assert.Equal(1, arc.X1, 4);
    }

    [Fact]
    public void ArcCseTool_center_start_end_makes_arc()
    {
        var t = new ArcCseTool();
        Assert.Null(t.AddPoint(0, 0));      // 圆心
        Assert.Null(t.AddPoint(1, 0));      // 起点
        Assert.IsType<ArcEntity>(t.AddPoint(0, 1));   // 端点
    }

    [Fact]
    public void EntityTypeName_maps_types()
    {
        Assert.Equal("直线", EntityTypeName.Of(new LineEntity()));
        Assert.Equal("圆", EntityTypeName.Of(new CircleEntity()));
        Assert.Equal("圆弧", EntityTypeName.Of(new ArcEntity()));
        Assert.Equal("多段线", EntityTypeName.Of(new PolylineEntity()));
        Assert.Equal("正多边形", EntityTypeName.Of(new PolygonEntity()));
        Assert.Equal("点", EntityTypeName.Of(new PointEntity()));
    }

    [Fact]
    public void SelectSimilar_matches_same_type()
    {
        // 快速选择的核心筛选：同类型判定
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0 });
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 0, Y1 = 1 });
        s.Add(new CircleEntity { Cx = 0, Cy = 0, Radius = 1 });
        var types = new System.Collections.Generic.HashSet<string> { "直线" };
        int n = s.Entities.FindAll(e => types.Contains(EntityTypeName.Of(e))).Count;
        Assert.Equal(2, n);   // 两条线, 不含圆
    }

    [Fact]
    public void ArcMath_from_start_end_radius_reconstructs_circle()
    {
        var t = ArcMath.FromStartEndRadius(1, 0, 0, 1, 1)!.Value;   // 单位圆上两点, r=1
        var cc = ArcMath.Circumcircle(t.x1, t.y1, t.x2, t.y2, t.x3, t.y3)!.Value;
        Assert.Equal(1, cc.r, 3);
        Assert.Equal(0, cc.cx, 3); Assert.Equal(0, cc.cy, 3);
    }

    [Fact]
    public void ArcMath_from_start_end_radius_too_small_is_null()
    {
        Assert.Null(ArcMath.FromStartEndRadius(0, 0, 10, 0, 1));   // 弦长10 > 2r → 无解
    }

    [Fact]
    public void TrimTools_extends_polyline_end_to_boundary()
    {
        // 多段线 (0,0)-(5,0)，边界竖线 x=8；点击靠近末端 → 末端延伸到 (8,0)
        var pl = new PolylineEntity();
        pl.Points.Add((0, 0)); pl.Points.Add((5, 0));
        var boundary = new LineEntity { X0 = 8, Y0 = -5, X1 = 8, Y1 = 5 };
        var r = TrimTools.TrimExtendPolylineEnd(pl, boundary, 5, 0)!;
        Assert.Equal(8, r.Points[^1].x, 4); Assert.Equal(0, r.Points[^1].y, 4);
        Assert.Equal(0, r.Points[0].x, 4);   // 起点不动
    }

    [Fact]
    public void TrimTools_trims_polyline_end_at_boundary()
    {
        // 多段线 (0,0)-(10,0)，边界竖线 x=6；点击靠近末端 → 末端缩到 (6,0)
        var pl = new PolylineEntity();
        pl.Points.Add((0, 0)); pl.Points.Add((10, 0));
        var boundary = new LineEntity { X0 = 6, Y0 = -5, X1 = 6, Y1 = 5 };
        var r = TrimTools.TrimExtendPolylineEnd(pl, boundary, 10, 0)!;
        Assert.Equal(6, r.Points[^1].x, 4);
    }

    [Fact]
    public void TrimTools_trims_arc_end_to_boundary()
    {
        double s = System.Math.Cos(System.Math.PI / 4);
        var arc = new ArcEntity { X1 = 1, Y1 = 0, X2 = s, Y2 = s, X3 = 0, Y3 = 1 };   // 上象限单位弧
        var boundary = new LineEntity { X0 = -2, Y0 = 0.5, X1 = 2, Y1 = 0.5 };         // 水平线 y=0.5
        var r = TrimTools.TrimExtendArc(arc, boundary, 1, 0)!;                          // 点击近起点
        Assert.Equal(0.866, r.X1, 2); Assert.Equal(0.5, r.Y1, 2);                       // 起点移到 (0.866,0.5)
        Assert.Equal(1, r.X1 * r.X1 + r.Y1 * r.Y1, 2);                                  // 仍在单位圆
        Assert.Equal(0, r.X3, 4); Assert.Equal(1, r.Y3, 4);                             // 端点不动
    }

    [Fact]
    public void Scene_snap_candidates_include_midpoint_and_center()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 });
        s.Add(new CircleEntity { Cx = 3, Cy = 4, Radius = 2 });
        var c = s.SnapCandidates();
        var pts = new System.Collections.Generic.List<(float x, float y)>();
        for (int i = 0; i + 5 < c.Length; i += 6) pts.Add((c[i], c[i + 1]));
        Assert.Contains(pts, p => System.Math.Abs(p.x - 5) < 1e-3 && System.Math.Abs(p.y) < 1e-3);       // 线中点
        Assert.Contains(pts, p => System.Math.Abs(p.x - 3) < 1e-3 && System.Math.Abs(p.y - 4) < 1e-3);   // 圆心
        Assert.Contains(pts, p => System.Math.Abs(p.x - 5) < 1e-3 && System.Math.Abs(p.y - 4) < 1e-3);   // 圆右象限(3+2,4)
    }

    [Fact]
    public void Scene_recolor_layer_updates_only_that_layer()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0, LayerName = "A" });
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 0, Y1 = 1, LayerName = "A" });
        s.Add(new CircleEntity { Cx = 0, Cy = 0, Radius = 1, LayerName = "B" });
        int n = s.RecolorLayer("A", 1f, 0f, 0f);
        Assert.Equal(2, n);
        Assert.Equal(1f, s.Entities[0].Cr, 4);          // A 层变红
        Assert.Equal(0.86f, s.Entities[2].Cr, 4);       // B 层保持默认色
    }

    // ── 文字对齐(TextEntity HAlign/VAlign) ──
    private static (double minX, double minY) TextBounds(TextEntity t)
    {
        var o = new List<float>(); t.Tessellate(o);
        double mnX = double.MaxValue, mnY = double.MaxValue;
        for (int i = 0; i + 1 < o.Count; i += 6) { if (o[i] < mnX) mnX = o[i]; if (o[i + 1] < mnY) mnY = o[i + 1]; }
        return (mnX, mnY);
    }

    [Fact]
    public void Text_halign_shifts_by_width()
    {
        // "AB" 高1: 宽=2×0.8=1.6。右对齐(2)较左对齐(0)左移一个宽度
        var left = new TextEntity { X = 0, Y = 0, Height = 1, Text = "AB", HAlign = 0 };
        var right = new TextEntity { X = 0, Y = 0, Height = 1, Text = "AB", HAlign = 2 };
        var center = new TextEntity { X = 0, Y = 0, Height = 1, Text = "AB", HAlign = 1 };
        Assert.Equal(TextBounds(left).minX - 1.6, TextBounds(right).minX, 4);
        Assert.Equal(TextBounds(left).minX - 0.8, TextBounds(center).minX, 4);   // 居中移半宽
    }

    [Fact]
    public void Text_width_factor_stretches_horizontally()
    {
        double MaxX(TextEntity t) { var o = new List<float>(); t.Tessellate(o); double m = double.MinValue; for (int i = 0; i + 1 < o.Count; i += 6) if (o[i] > m) m = o[i]; return m; }
        var normal = new TextEntity { X = 0, Y = 0, Height = 1, Text = "AB", WidthFactor = 1 };
        var wide = new TextEntity { X = 0, Y = 0, Height = 1, Text = "AB", WidthFactor = 2 };
        Assert.Equal(MaxX(normal) * 2, MaxX(wide), 4);   // 字宽系数2 → 水平尺寸加倍
    }

    [Fact]
    public void Text_oblique_slants_upper_points_right()
    {
        double MaxX(TextEntity t) { var o = new List<float>(); t.Tessellate(o); double m = double.MinValue; for (int i = 0; i + 1 < o.Count; i += 6) if (o[i] > m) m = o[i]; return m; }
        var upright = new TextEntity { X = 0, Y = 0, Height = 1, Text = "A", ObliqueAngle = 0 };
        var slanted = new TextEntity { X = 0, Y = 0, Height = 1, Text = "A", ObliqueAngle = System.Math.PI / 4 };
        Assert.True(MaxX(slanted) > MaxX(upright), "倾斜应把上部点右移, 增大水平范围");
    }

    [Fact]
    public void Text_valign_shifts_down_and_default_is_backward_compatible()
    {
        var baseline = new TextEntity { X = 0, Y = 0, Height = 1, Text = "A", VAlign = 0 };
        var top = new TextEntity { X = 0, Y = 0, Height = 1, Text = "A", VAlign = 2 };
        Assert.Equal(TextBounds(baseline).minY - 1.0, TextBounds(top).minY, 4);   // 顶对齐下移一个高度
        // 默认(HAlign=VAlign=0) 与不设对齐同(向后兼容)
        Assert.Equal(TextBounds(baseline).minX, TextBounds(new TextEntity { X = 0, Y = 0, Height = 1, Text = "A" }).minX, 6);
    }
}
