using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>网格度量回归（表面积/有向体积 + OFF 解析）。</summary>
public class MeshMetricsTests
{
    // 单位四面体：原点 + 三轴单位点
    private static (List<(double x, double y, double z)>, List<(int a, int b, int c)>) Tetra()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var t = new List<(int a, int b, int c)> { (0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3) };   // 外向
        return (v, t);
    }

    [Fact]
    public void Tetra_volume_is_one_sixth()
    {
        var (v, t) = Tetra();
        var m = MeshMetrics.Compute(v, t);
        Assert.Equal(4, m.VertexCount);
        Assert.Equal(4, m.TriangleCount);
        Assert.Equal(1.0 / 6.0, m.Volume, 6);
    }

    [Fact]
    public void Tetra_surface_area()
    {
        var (v, t) = Tetra();
        var m = MeshMetrics.Compute(v, t);
        Assert.Equal(1.5 + Math.Sqrt(3) / 2, m.SurfaceArea, 6);   // 3 直角面 0.5 + 斜面 √3/2
    }

    [Fact]
    public void Bounds_cover_unit()
    {
        var m = MeshMetrics.Compute(Tetra().Item1, Tetra().Item2);
        Assert.Equal(0, m.MinX, 6); Assert.Equal(1, m.MaxX, 6);
        Assert.Equal(1, m.MaxZ, 6);
    }

    [Fact]
    public void ParseOff_reads_verts_and_triangulates_quad()
    {
        // 一个正方形面(4顶点1四边形) → 扇形三角化成 2 三角
        string off = "OFF\n4 1 0\n0 0 0\n1 0 0\n1 1 0\n0 1 0\n4 0 1 2 3\n";
        var (verts, tris) = MeshMetrics.ParseOff(off);
        Assert.Equal(4, verts.Count);
        Assert.Equal(2, tris.Count);
        var m = MeshMetrics.Compute(verts, tris);
        Assert.Equal(1.0, m.SurfaceArea, 6);   // 单位正方形面积 1
    }

    [Fact]
    public void Empty_mesh_zero()
    {
        var m = MeshMetrics.Compute(new List<(double, double, double)>(), new List<(int, int, int)>());
        Assert.Equal(0, m.SurfaceArea, 6);
        Assert.Equal(0, m.Volume, 6);
    }

    // 单位四面体(0,0,0)(1,0,0)(0,1,0)(0,0,1), 外向绕序, 体积=1/6
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Tet(bool closed)
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var t = new List<(int a, int b, int c)> { (0, 2, 1), (0, 1, 3), (0, 3, 2) };
        if (closed) t.Add((1, 2, 3));   // 第4面(斜面)
        return (v, t);
    }

    [Fact]
    public void RobustVolume_watertight_tetra_exact()
    {
        var (v, t) = Tet(closed: true);
        Assert.True(MeshDiagnose.Analyze(v, t).IsClosed);
        Assert.Equal(1.0 / 6.0, MeshMetrics.RobustVolume(v, t), 6);   // 严密 1/6
    }

    [Fact]
    public void RobustVolume_open_tetra_caps_and_recovers()
    {
        var (v, t) = Tet(closed: false);          // 缺斜面 → 非水密
        Assert.False(MeshDiagnose.Analyze(v, t).IsClosed);
        // 补洞封盖后恢复 ≈ 1/6(扇形补洞在缺面平面上, 体积不变)
        Assert.Equal(1.0 / 6.0, MeshMetrics.RobustVolume(v, t), 4);
    }
}
