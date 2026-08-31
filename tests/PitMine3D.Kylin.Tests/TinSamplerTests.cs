using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>TIN 竖直求交采高回归（顶底板求交核 TinSampler：点在三角内 + 重心插值 Z）。</summary>
public class TinSamplerTests
{
    // 一个倾斜平面 z = x + 2y 上的 4 点(方形)，任意内点采高应精确落在该平面。
    private static readonly List<(double x, double y, double z)> Plane = new()
    {
        (0, 0, 0), (10, 0, 10), (0, 10, 20), (10, 10, 30),
    };

    [Fact]
    public void Samples_planar_tin_exactly()
    {
        // 平面 z=x+2y：点(5,5)→15, (2,8)→18, (8,2)→12
        Assert.Equal(15.0, TinSampler.SampleZ(Plane, 5, 5)!.Value, 6);
        Assert.Equal(18.0, TinSampler.SampleZ(Plane, 2, 8)!.Value, 6);
        Assert.Equal(12.0, TinSampler.SampleZ(Plane, 8, 2)!.Value, 6);
    }

    [Fact]
    public void Vertex_and_edge_hit_return_vertex_z()
    {
        Assert.Equal(0.0, TinSampler.SampleZ(Plane, 0, 0)!.Value, 6);    // 顶点
        Assert.Equal(30.0, TinSampler.SampleZ(Plane, 10, 10)!.Value, 6); // 顶点
        Assert.Equal(5.0, TinSampler.SampleZ(Plane, 5, 0)!.Value, 6);    // 边上 z=x
    }

    [Fact]
    public void Point_outside_tin_returns_null()
    {
        Assert.Null(TinSampler.SampleZ(Plane, -5, -5));    // 网外
        Assert.Null(TinSampler.SampleZ(Plane, 100, 3));    // 网外
    }

    [Fact]
    public void Too_few_points_returns_null()
    {
        Assert.Null(TinSampler.SampleZ(new List<(double, double, double)> { (0, 0, 0), (1, 1, 1) }, 0.5, 0.5));
    }

    [Fact]
    public void Explicit_triangles_barycentric()
    {
        // 单三角 (0,0,0)-(10,0,0)-(0,10,30)：重心(内点)插值。质心(10/3,10/3) → z=10
        var pts = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (0, 10, 30) };
        var tris = new List<(int a, int b, int c)> { (0, 1, 2) };
        Assert.Equal(10.0, TinSampler.SampleZ(pts, tris, 10.0 / 3, 10.0 / 3)!.Value, 5);
        Assert.Null(TinSampler.SampleZ(pts, tris, 9, 9));   // 三角外
    }
}
