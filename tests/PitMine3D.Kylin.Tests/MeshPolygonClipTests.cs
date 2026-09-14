using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 闭合线精确裁剪三角网(原内核 clip_tin_by_polygon 移植)回归：
/// 跨界三角必须沿裁刀边切开——保留侧面积 == 裁刀与网的真实交集面积、无顶点越界、Z 沿平面插值不变。
/// </summary>
public class MeshPolygonClipTests
{
    // 10×10 方形 TIN，4×4 格 → 32 三角；z = 2x + 3y + 5 (平面, 便于核对插值)
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) GridTin(int n = 4, double size = 10)
    {
        var v = new List<(double, double, double)>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                double x = i * size / n, y = j * size / n;
                v.Add((x, y, 2 * x + 3 * y + 5));
            }
        var t = new List<(int, int, int)>();
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                t.Add((a, b, d)); t.Add((a, d, c));   // 全 CCW
            }
        return (v, t);
    }

    private static double AreaXY(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        double s = 0;
        foreach (var (a, b, c) in t)
            s += Math.Abs((v[b].x - v[a].x) * (v[c].y - v[a].y) - (v[c].x - v[a].x) * (v[b].y - v[a].y)) / 2;
        return s;
    }

    private static double SignedArea(IReadOnlyList<(double x, double y, double z)> v, (int a, int b, int c) t)
        => ((v[t.b].x - v[t.a].x) * (v[t.c].y - v[t.a].y) - (v[t.c].x - v[t.a].x) * (v[t.b].y - v[t.a].y)) / 2;

    // 裁刀：斜着放的四边形, 边都不与网格线重合 → 几乎每个跨界三角都要被切
    private static readonly List<(double x, double y)> Diamond = new() { (5, 1.3), (8.7, 5), (5, 8.7), (1.3, 5) };
    private const double DiamondArea = 3.7 * 3.7 * 2;   // 对角线 7.4 → 面积 d²/2

    [Fact]
    public void Keep_inside_area_equals_polygon_area_and_no_vertex_outside()
    {
        var (v, t) = GridTin();
        var r = MeshPolygonClip.Clip(v, t, Diamond, keepInside: true);
        Assert.NotNull(r);
        Assert.Equal(DiamondArea, AreaXY(r!.Value.Verts, r.Value.Tris), 6);
        // 质心判定会留下伸出裁刀的尖刺；精确裁剪后每个顶点都在裁刀内(含边)
        foreach (var p in r.Value.Verts)
            Assert.True(PolylineClipper.Contains(Diamond, p.x, p.y), $"顶点 ({p.x},{p.y}) 越出裁刀");
    }

    [Fact]
    public void Keep_outside_area_is_complement_and_no_output_centroid_inside()
    {
        var (v, t) = GridTin();
        var r = MeshPolygonClip.Clip(v, t, Diamond, keepInside: false);
        Assert.NotNull(r);
        Assert.Equal(100 - DiamondArea, AreaXY(r!.Value.Verts, r.Value.Tris), 6);
        foreach (var tri in r.Value.Tris)
        {
            double cx = (r.Value.Verts[tri.a].x + r.Value.Verts[tri.b].x + r.Value.Verts[tri.c].x) / 3;
            double cy = (r.Value.Verts[tri.a].y + r.Value.Verts[tri.b].y + r.Value.Verts[tri.c].y) / 3;
            Assert.False(LineMath.PointInPolygon(cx, cy, Diamond), $"质心 ({cx},{cy}) 落在裁刀内");
        }
    }

    [Fact]
    public void Inside_plus_outside_tile_the_source_exactly()
    {
        var (v, t) = GridTin();
        var a = MeshPolygonClip.Clip(v, t, Diamond, true)!.Value;
        var b = MeshPolygonClip.Clip(v, t, Diamond, false)!.Value;
        Assert.Equal(100, AreaXY(a.Verts, a.Tris) + AreaXY(b.Verts, b.Tris), 6);
    }

    [Fact]
    public void Z_is_interpolated_on_the_source_plane_for_new_boundary_vertices()
    {
        var (v, t) = GridTin();
        foreach (bool keep in new[] { true, false })
        {
            var r = MeshPolygonClip.Clip(v, t, Diamond, keep)!.Value;
            Assert.Contains(r.Verts, p => !v.Any(q => Math.Abs(q.x - p.x) < 1e-9 && Math.Abs(q.y - p.y) < 1e-9));   // 确有新生边界点
            foreach (var p in r.Verts) Assert.Equal(2 * p.x + 3 * p.y + 5, p.z, 9);
        }
    }

    [Fact]
    public void Winding_of_source_is_preserved_on_both_sides()
    {
        var (v, t) = GridTin();
        foreach (bool keep in new[] { true, false })
        {
            var r = MeshPolygonClip.Clip(v, t, Diamond, keep)!.Value;
            Assert.All(r.Tris, tri => Assert.True(SignedArea(r.Verts, tri) > 0, "输出三角绕向翻了"));
        }
    }

    [Fact]
    public void Concave_polygon_is_supported_via_ear_clipping()
    {
        var (v, t) = GridTin();
        // L 形: [1,9]×[1,9] 挖掉右上 [5,9]×[5,9] → 面积 64-16 = 48
        var L = new List<(double x, double y)> { (1, 1), (9, 1), (9, 5), (5, 5), (5, 9), (1, 9) };
        var rin = MeshPolygonClip.Clip(v, t, L, true)!.Value;
        var rout = MeshPolygonClip.Clip(v, t, L, false)!.Value;
        Assert.Equal(48, AreaXY(rin.Verts, rin.Tris), 6);
        Assert.Equal(52, AreaXY(rout.Verts, rout.Tris), 6);
        Assert.DoesNotContain(rin.Verts, p => p.x > 5 + 1e-9 && p.y > 5 + 1e-9);   // 凹口里没有面
    }

    [Fact]
    public void Polygon_with_explicit_closing_point_and_cw_order_works()
    {
        var (v, t) = GridTin();
        var cwClosed = Enumerable.Reverse(Diamond).Append(Diamond[^1]).ToList();   // CW + 末点重复首点
        var r = MeshPolygonClip.Clip(v, t, cwClosed, true)!.Value;
        Assert.Equal(DiamondArea, AreaXY(r.Verts, r.Tris), 6);
    }

    [Fact]
    public void Triangles_wholly_outside_or_inside_are_kept_or_dropped_whole()
    {
        var (v, t) = GridTin();
        var small = new List<(double x, double y)> { (0.5, 0.5), (1.5, 0.5), (1.5, 1.5), (0.5, 1.5) };   // 只碰左下角两块
        var r = MeshPolygonClip.Clip(v, t, small, false)!.Value;
        Assert.Equal(99, AreaXY(r.Verts, r.Tris), 6);
        // 远离裁刀的三角原样保留(顶点仍是网格点)
        int gridVerts = r.Verts.Count(p => Math.Abs(p.x % 2.5) < 1e-9 && Math.Abs(p.y % 2.5) < 1e-9);
        Assert.Equal(25, gridVerts);
    }

    [Fact]
    public void Polygon_outside_mesh_gives_empty_inside_and_full_outside()
    {
        var (v, t) = GridTin();
        var far = new List<(double x, double y)> { (20, 20), (30, 20), (30, 30), (20, 30) };
        var rin = MeshPolygonClip.Clip(v, t, far, true)!.Value;
        var rout = MeshPolygonClip.Clip(v, t, far, false)!.Value;
        Assert.Empty(rin.Tris);
        Assert.Equal(t.Count, rout.Tris.Count);
        Assert.Equal(100, AreaXY(rout.Verts, rout.Tris), 9);
    }

    [Fact]
    public void Degenerate_polygon_returns_null()
    {
        var (v, t) = GridTin();
        Assert.Null(MeshPolygonClip.Clip(v, t, new List<(double x, double y)> { (1, 1), (2, 2) }, true));
        Assert.Null(MeshPolygonClip.Clip(v, t, new List<(double x, double y)> { (1, 1), (2, 2), (3, 3) }, true));   // 共线耳切失败
    }

    [Fact]
    public void Single_valued_guard_rejects_closed_solid_but_accepts_terrain()
    {
        var (v, t) = GridTin();
        Assert.True(MeshPolygonClip.IsSingleValuedSurface(v, t));
        // 单位立方体(6 面 12 三角): 顶面朝上、底面朝下各 1/6 > 10%
        var cv = new List<(double x, double y, double z)> { (0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1) };
        var ct = new List<(int a, int b, int c)>
        {
            (0,2,1),(0,3,2), (4,5,6),(4,6,7), (0,1,5),(0,5,4), (1,2,6),(1,6,5), (2,3,7),(2,7,6), (3,0,4),(3,4,7)
        };
        Assert.False(MeshPolygonClip.IsSingleValuedSurface(cv, ct));
    }

    [Fact]
    public void Large_world_coordinates_still_clip_exactly()
    {
        // 真实矿区量级 4.4e6：精确裁剪与焊接容差按范围缩放, 不能因绝对阈值失效
        var (v0, t) = GridTin();
        const double ox = 4_400_000, oy = 3_900_000;
        var v = v0.Select(p => (p.x + ox, p.y + oy, p.z)).ToList();
        var poly = Diamond.Select(p => (p.x + ox, p.y + oy)).ToList();
        var r = MeshPolygonClip.Clip(v, t, poly, true)!.Value;
        Assert.Equal(DiamondArea, AreaXY(r.Verts, r.Tris), 4);
        foreach (var p in r.Verts) Assert.True(PolylineClipper.Contains(poly, p.x, p.y));
    }
}
