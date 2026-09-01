using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>2.5D TIN 建面回归（Delaunay 拓扑 + 保留高程 → OFF 面, 忠实原创建三角网建面）。</summary>
public class TinSurfaceTests
{
    [Fact]
    public void Describe_square_projected_area_and_z_range()
    {
        // 单位方 10×10, 高程 0/0/5/5 → Delaunay 2 三角, XY 投影面积恒 100, z∈[0,5]。
        var verts = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (0, 10, 5), (10, 10, 5) };
        var xy = verts.Select(v => (v.x, v.y)).ToList();
        var tris = Delaunay.Triangulate(xy);
        var s = TinSurface.Describe(verts, tris);
        Assert.Equal(4, s.Verts);
        Assert.Equal(2, s.Tris);
        Assert.Equal(100.0, s.ProjectedAreaXY, 6);   // 两三角覆盖方形, 面积和=100(与对角线无关)
        Assert.Equal(0, s.ZMin, 6);
        Assert.Equal(5, s.ZMax, 6);
    }

    [Fact]
    public void Describe_empty_and_degenerate_safe()
    {
        var s = TinSurface.Describe(new List<(double, double, double)>(), new List<(int, int, int)>());
        Assert.Equal(0, s.Verts); Assert.Equal(0, s.ProjectedAreaXY, 6);
        Assert.Equal(0, s.ZMin, 6); Assert.Equal(0, s.ZMax, 6);
    }

    [Fact]
    public void Pipeline_points_to_off_preserves_z_and_topology()
    {
        // 建面 → ToOff → ParseOff 往返: 顶点数/三角数/高程保真(2.5D 面可复用)。
        var verts = new List<(double x, double y, double z)>
        {
            (0, 0, 100), (20, 0, 108), (0, 20, 104), (20, 20, 112), (10, 10, 106),
        };
        var xy = verts.Select(v => (v.x, v.y)).ToList();
        var tris = Delaunay.Triangulate(xy);
        Assert.True(tris.Count >= 3);
        var off = MeshWeld.ToOff(verts, tris);
        var (rv, rt) = MeshMetrics.ParseOff(off);
        Assert.Equal(verts.Count, rv.Count);
        Assert.Equal(tris.Count, rt.Count);
        // 高程被保留(非丢成 0)——找回 (10,10) 顶点其 z=106。
        var mid = rv.First(v => System.Math.Abs(v.x - 10) < 1e-6 && System.Math.Abs(v.y - 10) < 1e-6);
        Assert.Equal(106, mid.z, 6);
        // 投影面积 = 大方 20×20 = 400(内点不改覆盖域)。
        Assert.Equal(400.0, TinSurface.Describe(verts, tris).ProjectedAreaXY, 6);
    }
}
