using System;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 三轴变换手柄(Gizmo)几何回归：像素尺度、轴命中、沿轴拖拽量、2D 只画 X/Y、悬停变黄，
/// 以及与真相机(Camera.ScreenRay + 投影)配合时"拖到哪算哪"的一致性。
/// </summary>
public class GizmoGlyphTests
{
    // 正交俯视投影: 1 世界单位 = k 像素, 屏幕 y 向下
    private static Func<double, double, double, (double sx, double sy)?> Ortho(double k, double cx = 400, double cy = 300)
        => (x, y, z) => (cx + x * k, cy - y * k);

    [Fact]
    public void WorldPerPixel_orthographic_is_inverse_scale()
    {
        Assert.Equal(0.25, GizmoGlyph.WorldPerPixel(Ortho(4), 10, 20, 0), 6);
        Assert.Equal(1.0 / 0.02, GizmoGlyph.WorldPerPixel(Ortho(0.02), 0, 0, 0), 3);   // 缩得很小: 探针自适应重探
    }

    [Fact]
    public void WorldPerPixel_perspective_matches_depth()
    {
        // 针孔透视: 相机在 (0,0,0) 看 +Y, 焦距 f 像素; 深度 d 处一单位 = f/d 像素
        const double f = 800;
        (double sx, double sy)? Proj(double x, double y, double z) => y <= 0 ? null : (400 + f * x / y, 300 - f * z / y);
        Assert.Equal(50.0 / f, GizmoGlyph.WorldPerPixel(Proj, 0, 50, 0), 4);
        Assert.Equal(5.0 / f, GizmoGlyph.WorldPerPixel(Proj, 3, 5, -1), 4);
        Assert.Equal(0, GizmoGlyph.WorldPerPixel(Proj, 0, -5, 0));   // 相机后方
    }

    [Fact]
    public void Build_2D_skips_Z_and_hover_turns_axis_yellow()
    {
        var c = (10.0, 20.0, 5.0);
        var all = GizmoGlyph.Build(c, 0.5, (0, 0, -1), only2D: false, hover: -1, dragging: -1);
        var xy = GizmoGlyph.Build(c, 0.5, (0, 0, -1), only2D: true, hover: 1, dragging: -1);
        Assert.True(all.Count > xy.Count);
        // 2D 不画 Z 轴且箭头是平面三角: 所有顶点都在中心高程上; 3D 则到 Z 轴尖
        Assert.All(xy, pl => Assert.All(pl.Zs!, z => Assert.Equal(5.0, z, 9)));
        Assert.Equal(5 + GizmoGlyph.AxisPx * 0.5, all.Max(pl => pl.Zs!.Max()), 6);
        // 悬停 Y: 存在金黄线, 且没有纯绿线
        var hov = GizmoGlyph.HoverColor;
        Assert.Contains(xy, pl => Math.Abs(pl.Cr - hov.r) < 1e-6 && Math.Abs(pl.Cg - hov.g) < 1e-6);
        var green = GizmoGlyph.AxisColors[1];
        Assert.DoesNotContain(xy, pl => Math.Abs(pl.Cr - green.r) < 1e-6 && Math.Abs(pl.Cg - green.g) < 1e-6 && Math.Abs(pl.Cb - green.b) < 1e-6);
        // X 轴尖端在 c + 90px × wpp
        double maxX = all.Max(pl => pl.Points.Max(p => p.x));
        Assert.Equal(10 + GizmoGlyph.AxisPx * 0.5, maxX, 6);
        // 拖拽中多一条贯穿约束线
        var drag = GizmoGlyph.Build(c, 0.5, (0, 0, -1), only2D: false, hover: -1, dragging: 0);
        Assert.Equal(all.Count + 1, drag.Count);
        Assert.Equal(10 - GizmoGlyph.GuideLenFactor * GizmoGlyph.AxisPx * 0.5, drag.Min(pl => pl.Points.Min(p => p.x)), 6);
    }

    [Fact]
    public void HitAxis_picks_nearest_axis_and_respects_dead_zone()
    {
        var proj = Ortho(2);           // 中心 (0,0,0) → 屏幕 (400,300); wpp=1 → 轴长 90 世界 = 180px: X 向右, Y 向上
        var c = (0.0, 0.0, 0.0);
        double wpp = 1.0;
        Assert.Equal(0, GizmoGlyph.HitAxis(proj, c, wpp, false, 500, 303));   // X 轴上, 偏 3px
        Assert.Equal(1, GizmoGlyph.HitAxis(proj, c, wpp, false, 396, 200));   // Y 轴上
        Assert.Equal(-1, GizmoGlyph.HitAxis(proj, c, wpp, false, 500, 320));  // 离 X 轴 20px
        Assert.Equal(-1, GizmoGlyph.HitAxis(proj, c, wpp, false, 403, 301));  // 中心死区
        Assert.Equal(-1, GizmoGlyph.HitAxis(proj, c, wpp, false, 700, 300));  // 轴尖之外
        Assert.Equal(-1, GizmoGlyph.HitAxis(proj, c, wpp, true, 400, 300));   // 2D 俯视 Z 轴投成一点, 不命中
    }

    [Fact]
    public void AxisParam_is_closest_point_on_axis_and_null_when_parallel()
    {
        var c = (10.0, 20.0, 5.0);
        var down = (12.0, 27.0, 100.0, 0.0, 0.0, -1.0);   // 竖直向下射线经 (12,27)
        Assert.Equal(2.0, GizmoGlyph.AxisParam(down, c, 0)!.Value, 9);    // 沿 X: 12-10
        Assert.Equal(7.0, GizmoGlyph.AxisParam(down, c, 1)!.Value, 9);    // 沿 Y: 27-20
        Assert.Null(GizmoGlyph.AxisParam(down, c, 2));                    // 与 Z 轴平行
        var toward = (5.0, 10.0, 0.0, 0.0, -2.0, 0.0);                    // 从 (5,10) 朝 -Y 的射线(未单位化也行), 交 X 轴于 (5,0)
        Assert.Equal(5.0, GizmoGlyph.AxisParam(toward, (0.0, 0.0, 0.0), 0)!.Value, 9);
        var skew = (5.0, 10.0, 3.0, 0.0, -1.0, 0.0);                      // 抬高 3 的异面射线: 最近点仍在 x=5
        Assert.Equal(5.0, GizmoGlyph.AxisParam(skew, (0.0, 0.0, 0.0), 0)!.Value, 9);
    }

    [Fact]
    public void AxisParam_with_real_camera_ray_recovers_axis_hit_point()
    {
        // 真相机: 3D 轨道, 沿 X 轴上一点投到屏幕, 再从该像素发射线求 X 轴参数, 应回到同一点
        var cam = new Camera();
        cam.SetMode(false);
        cam.FitBounds(new double[] { -50, -50, 50, 50 });
        const double vw = 800, vh = 600;
        var proj = cam.MakeProjector(vw, vh);
        var c = (3.0, -4.0, 2.0);
        var onAxis = (c.Item1 + 12.5, c.Item2, c.Item3);
        var s = proj(onAxis.Item1, onAxis.Item2, onAxis.Item3)!.Value;
        var ray = cam.ScreenRay(s.sx, s.sy, vw, vh)!.Value;
        Assert.Equal(12.5, GizmoGlyph.AxisParam(ray, c, 0)!.Value, 1);   // float 矩阵求逆, 千分之一量级误差
        // 2D 正交: 射线竖直向下
        cam.SetMode(true);
        var r2 = cam.ScreenRay(vw / 2, vh / 2, vw, vh)!.Value;
        Assert.Equal(0, r2.dx, 6); Assert.Equal(0, r2.dy, 6); Assert.Equal(-1, r2.dz, 6);
    }

    [Fact]
    public void BoxEdges_gives_twelve_segments()
    {
        var edges = GizmoGlyph.BoxEdges(0, 0, 0, 1, 2, 3, (1, 1, 1));
        Assert.Equal(12, edges.Count);
        Assert.All(edges, e => { Assert.Equal(2, e.Points.Count); Assert.Equal(2, e.Zs!.Count); });
        Assert.Equal(4, edges.Count(e => Math.Abs(e.Zs![0] - e.Zs![1]) > 1e-9));   // 4 根立柱
    }
}
