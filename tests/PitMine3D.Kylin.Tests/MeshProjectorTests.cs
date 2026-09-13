using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点/线落到面回归（网格重心 Z 投影）。</summary>
public class MeshProjectorTests
{
    // 倾斜平面：z = x（三角覆盖 XY [-100,100]²）
    private static (double[] v, int[] t) SlopedPlane()
    {
        var v = new double[]
        {
            -100, -100, -100,   100, -100, 100,   100, 100, 100,   -100, 100, -100
        };
        var t = new[] { 0, 1, 2, 0, 2, 3 };   // 两三角, z=x 处处成立
        return (v, t);
    }

    [Fact]
    public void Drape_samples_z_from_sloped_plane()
    {
        var (v, t) = SlopedPlane();
        var pts = new List<(double x, double y)> { (0, 0), (50, -30), (-40, 20) };
        var (draped, missed) = MeshProjector.Drape(v, t, pts);
        Assert.Equal(0, missed);
        Assert.Equal(0.0, draped[0].z, 6);     // z=x → 0
        Assert.Equal(50.0, draped[1].z, 6);    // z=x → 50
        Assert.Equal(-40.0, draped[2].z, 6);   // z=x → -40
    }

    [Fact]
    public void Drape_counts_misses_outside_mesh()
    {
        var (v, t) = SlopedPlane();
        var pts = new List<(double x, double y)> { (0, 0), (10000, 10000) };
        var (draped, missed) = MeshProjector.Drape(v, t, pts);
        Assert.Equal(1, missed);
        Assert.Equal(0.0, draped[1].z, 6);     // 未命中 Z 保持 0
        Assert.Equal(2, draped.Count);         // 未命中点仍保留
    }

    [Fact]
    public void Drape_empty_points()
    {
        var (v, t) = SlopedPlane();
        var (draped, missed) = MeshProjector.Drape(v, t, new List<(double x, double y)>());
        Assert.Empty(draped);
        Assert.Equal(0, missed);
    }

    [Fact]
    public void DrapePolyline_inserts_node_where_segment_crosses_triangle_edge()
    {
        var (v, t) = SlopedPlane();   // 两三角共边 = 对角线 (-100,-100)→(100,100)
        var pts = new List<(double x, double y)> { (-50, 20), (60, -30) };   // 斜穿对角线
        var (draped, missed) = MeshProjector.DrapePolyline(v, t, pts);
        Assert.Equal(0, missed);
        Assert.Equal(3, draped.Count);                       // 两端 + 对角线交点
        Assert.Equal(-50.0, draped[0].z, 6); Assert.Equal(60.0, draped[2].z, 6);
        Assert.Equal(draped[1].x, draped[1].y, 6);           // 交点在对角线上
        Assert.Equal(draped[1].x, draped[1].z, 6);           // z=x 贴面
        Assert.True(draped[1].x > -50 && draped[1].x < 60);
    }

    [Fact]
    public void Drape_preserves_xy()
    {
        var (v, t) = SlopedPlane();
        var pts = new List<(double x, double y)> { (12.5, -7.25) };
        var (draped, _) = MeshProjector.Drape(v, t, pts);
        Assert.Equal(12.5, draped[0].x, 9);
        Assert.Equal(-7.25, draped[0].y, 9);
    }
}
