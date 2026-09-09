using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云实体（渲染通道 / 拾取 / 框选 / 变换 / 存档）回归。</summary>
public class PointCloudEntityTests
{
    private static PointCloudEntity Sample(int n = 25, double z = 5)
    {
        var pc = new PointCloudEntity { Name = "样例" };
        for (int i = 0; i < n; i++) pc.Pts.Add((i % 5, i / 5, z + i * 0.1));
        return pc;
    }

    [Fact]
    public void Bounds_and_counts()
    {
        var pc = Sample();
        var b = pc.Bounds;
        Assert.Equal(0, b.minX, 6);
        Assert.Equal(4, b.maxX, 6);
        Assert.Equal(0, b.minY, 6);
        Assert.Equal(4, b.maxY, 6);
        Assert.Equal(5, b.minZ, 6);
        Assert.Equal(5 + 24 * 0.1, b.maxZ, 6);
        Assert.Equal(25, pc.PointCount);
        Assert.Equal(new[] { 0.0, 0.0, 4.0, 4.0 }, pc.Bounds2D);
    }

    [Fact]
    public void Points_go_to_point_channel_not_line_channel()
    {
        var pc = Sample(3);
        var lines = new List<float>();
        pc.Tessellate(lines);
        Assert.Empty(lines);   // 点云不进线段通道 —— 否则每点要画个小十字, 顶点数翻十几倍

        var pts = new List<float>();
        pc.TessellatePoints(pts);
        Assert.Equal(3 * 6, pts.Count);                 // 每点 P3_C3
        Assert.Equal(pc.Pts[1].x, pts[6], 5);
        Assert.Equal(pc.Pts[1].z, pts[8], 5);
        Assert.Equal(pc.Cr, pts[9], 5);                 // 无逐点色 → 用实体基色
    }

    [Fact]
    public void Per_point_colors_and_elevation_offset_applied()
    {
        var pc = Sample(2);
        pc.Colors = new List<(float r, float g, float b)> { (1, 0, 0), (0, 1, 0) };
        pc.Elevation = 100;
        var o = new List<float>();
        pc.TessellatePoints(o);
        Assert.Equal(pc.Pts[0].z + 100, o[2], 5);
        Assert.Equal(1f, o[3], 5); Assert.Equal(0f, o[4], 5);
        Assert.Equal(0f, o[9], 5); Assert.Equal(1f, o[10], 5);

        // 缓存按点数/基色/标高键: 换了逐点色要 Invalidate 才重建
        pc.Colors[0] = (0, 0, 1);
        pc.Invalidate();
        var o2 = new List<float>();
        pc.TessellatePoints(o2);
        Assert.Equal(1f, o2[5], 5);
    }

    [Fact]
    public void SolidColor_clears_per_point_colors()
    {
        var pc = Sample(3);
        pc.Colors = new List<(float r, float g, float b)> { (1, 0, 0), (1, 0, 0), (1, 0, 0) };
        pc.SetSolidColor(0.2f, 0.4f, 0.6f);
        Assert.False(pc.HasColors);
        Assert.Equal(0.2f, pc.Cr, 5);
        var o = new List<float>();
        pc.TessellatePoints(o);
        Assert.Equal(0.4f, o[4], 5);
    }

    [Fact]
    public void DistanceTo_zero_on_point_and_rejects_outside_bbox()
    {
        var pc = Sample();
        Assert.Equal(0, pc.DistanceTo(2, 2), 6);          // 正落在某个点上
        Assert.True(pc.DistanceTo(2.5, 2.5) < 0.75);      // 盒内: 到最近点的真距离
        Assert.True(pc.DistanceTo(1000, 1000) > 1e8);     // 盒外: 明确落选(同三角网口径)
    }

    [Fact]
    public void Bounds_box_highlight_has_twelve_edges()
    {
        var pc = Sample();
        var o = new List<float>();
        pc.TessellateBoundsBox(o);
        Assert.Equal(12 * 2 * 6, o.Count);   // 立方体 12 条棱 × 2 端点 × P3_C3
    }

    [Fact]
    public void Apply_transforms_xy_keeps_z_and_colors()
    {
        var pc = Sample(4);
        pc.Colors = new List<(float r, float g, float b)> { (1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 0) };
        pc.LayerName = "点云层";
        var moved = (PointCloudEntity)pc.Apply(Affine2.Translate(10, -5));
        Assert.Equal(pc.PointCount, moved.PointCount);
        Assert.Equal(pc.Pts[2].x + 10, moved.Pts[2].x, 6);
        Assert.Equal(pc.Pts[2].y - 5, moved.Pts[2].y, 6);
        Assert.Equal(pc.Pts[2].z, moved.Pts[2].z, 6);      // 仿射只动 XY
        Assert.True(moved.HasColors);
        Assert.Equal("点云层", moved.LayerName);            // 变换保留样式/图层
    }

    [Fact]
    public void Clone_is_deep()
    {
        var pc = Sample(3);
        pc.Colors = new List<(float r, float g, float b)> { (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        pc.RgbColors = new List<(float r, float g, float b)>(pc.Colors);
        pc.Normals = new List<(double x, double y, double z)> { (0, 0, 1), (0, 0, 1), (0, 0, 1) };
        var c = pc.Clone();
        c.Pts[0] = (99, 99, 99);
        c.Colors![0] = (0.5f, 0.5f, 0.5f);
        Assert.NotEqual(99, pc.Pts[0].x);
        Assert.Equal(1f, pc.Colors[0].r, 5);
        Assert.True(c.HasRgb && c.HasNormals);
    }

    [Fact]
    public void Explode_to_points_keeps_absolute_z()
    {
        var pc = Sample(3);
        pc.Elevation = 20;
        var parts = pc.Explode();
        Assert.NotNull(parts);
        Assert.Equal(3, parts!.Count);
        var p0 = Assert.IsType<PointEntity>(parts[0]);
        Assert.Equal(pc.Pts[0].z + 20, p0.Elevation, 6);
    }

    [Fact]
    public void Scene_cloud_channel_only_collects_visible_shown_clouds()
    {
        var scene = new Scene();
        var a = Sample(4); a.Name = "A";
        var b = Sample(4); b.Name = "B"; b.Visible = false;
        var c = Sample(4); c.Name = "C"; c.LayerName = "隐藏层";
        scene.Add(a); scene.Add(b); scene.Add(c);
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1 });

        var all = scene.BuildCloudPoints(null);
        Assert.Equal((4 + 4) * 6, all.Length);            // 隐藏实体 b 不上屏

        var shown = scene.BuildCloudPoints(l => l != "隐藏层");
        Assert.Equal(4 * 6, shown.Length);                // 图层隐藏的 c 也不上屏

        // 线段通道里不该混进点云顶点(只有那条直线的 2 端点)
        Assert.Equal(2 * 6, scene.BuildGeometry(null).Length);
    }

    [Fact]
    public void Entity_type_name_is_point_cloud()
        => Assert.Equal("点云", EntityTypeName.Of(Sample(1)));

    [Fact]
    public void SceneIO_roundtrip_keeps_points_colors_name_layer()
    {
        var scene = new Scene();
        var pc = Sample(6);
        pc.Name = "去噪结果";
        pc.LayerName = "点云";
        pc.Elevation = 3.5;
        pc.PointPixels = 4;
        pc.Colors = Enumerable.Range(0, 6).Select(i => (i / 6f, 0.5f, 1f - i / 6f)).ToList();
        scene.Add(pc);

        var back = SceneIO.Load(SceneIO.Save(scene));
        var got = Assert.IsType<PointCloudEntity>(Assert.Single(back.Entities));
        Assert.Equal("去噪结果", got.Name);
        Assert.Equal("点云", got.LayerName);
        Assert.Equal(3.5, got.Elevation, 6);
        Assert.Equal(4f, got.PointPixels, 3);
        Assert.Equal(6, got.PointCount);
        Assert.Equal(pc.Pts[3].x, got.Pts[3].x, 6);
        Assert.Equal(pc.Pts[3].z, got.Pts[3].z, 6);
        Assert.True(got.HasColors);
        Assert.Equal(pc.Colors[4].r, got.Colors![4].r, 4);
        Assert.Equal(pc.Colors[4].b, got.Colors[4].b, 4);
    }

    // ── 框选 / 圈选 ──

    [Fact]
    public void Window_select_needs_whole_cloud_inside_crossing_needs_any_point()
    {
        var pc = Sample();   // XY 落在 [0,4]×[0,4]
        Assert.True(SelectionBox.Match(pc, -1, -1, 5, 5, crossing: false));
        Assert.False(SelectionBox.Match(pc, -1, -1, 2, 2, crossing: false));   // 只框住一部分 → 窗口选不中
        Assert.True(SelectionBox.Match(pc, -1, -1, 2, 2, crossing: true));     // 交叉选中
        Assert.False(SelectionBox.Match(pc, 100, 100, 200, 200, crossing: true));
    }

    [Fact]
    public void Polygon_select_uses_points()
    {
        var pc = Sample();
        var big = new List<(double x, double y)> { (-1, -1), (5, -1), (5, 5), (-1, 5) };
        var half = new List<(double x, double y)> { (-1, -1), (2.5, -1), (2.5, 5), (-1, 5) };
        Assert.True(SelectionBox.MatchPolygon(pc, big, crossing: false));
        Assert.False(SelectionBox.MatchPolygon(pc, half, crossing: false));
        Assert.True(SelectionBox.MatchPolygon(pc, half, crossing: true));
    }

    [Fact]
    public void Pick_stride_caps_sampling()
    {
        Assert.Equal(1, PointCloudEntity.PickStride(20000));
        Assert.True(PointCloudEntity.PickStride(1_000_000) > 1);
        Assert.True(1_000_000 / PointCloudEntity.PickStride(1_000_000) <= 20001);
    }
}
