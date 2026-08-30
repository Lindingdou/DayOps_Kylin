using System;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>基本几何体回归（拓扑闭合 + 体积校核）。</summary>
public class PrimitiveBodiesTests
{
    [Fact]
    public void Box_is_closed_with_correct_volume()
    {
        var (v, t) = PrimitiveBodies.Box(0, 0, 0, 2, 3, 4);
        Assert.Equal(8, v.Count);
        Assert.Equal(12, t.Count);
        var d = MeshDiagnose.Analyze(v, t);
        Assert.True(d.IsClosed);
        var m = MeshMetrics.Compute(v, t);
        Assert.Equal(24.0, m.Volume, 6);           // 2×3×4
        Assert.Equal(2 * (2 * 3 + 3 * 4 + 2 * 4), m.SurfaceArea, 6);   // 表面积 52
    }

    [Fact]
    public void Sphere_is_closed_and_volume_approaches_formula()
    {
        var (v, t) = PrimitiveBodies.Sphere(0, 0, 0, 5, 32, 48);
        var d = MeshDiagnose.Analyze(v, t);
        Assert.True(d.IsClosed);
        var m = MeshMetrics.Compute(v, t);
        double exact = 4.0 / 3.0 * Math.PI * 125;   // 523.6
        Assert.InRange(m.Volume, exact * 0.97, exact);   // 内接多面体略小于精确球
    }

    [Fact]
    public void Cylinder_is_closed_and_volume_approaches_formula()
    {
        var (v, t) = PrimitiveBodies.Cylinder(0, 0, 0, 4, 10, 64);
        var d = MeshDiagnose.Analyze(v, t);
        Assert.True(d.IsClosed);
        var m = MeshMetrics.Compute(v, t);
        double exact = Math.PI * 16 * 10;           // 502.7
        Assert.InRange(m.Volume, exact * 0.98, exact);   // 内接棱柱略小
    }

    [Fact]
    public void Sphere_clamps_low_resolution()
    {
        var (v, t) = PrimitiveBodies.Sphere(0, 0, 0, 1, 1, 1);   // 被夹到 3×3
        Assert.True(v.Count > 0 && t.Count > 0);
        Assert.True(MeshDiagnose.Analyze(v, t).IsClosed);
    }

    [Fact]
    public void Box_centered_at_offset()
    {
        var (v, _) = PrimitiveBodies.Box(100, 200, 50, 10, 10, 10);
        var m = MeshMetrics.Compute(v, new System.Collections.Generic.List<(int, int, int)>());
        Assert.Equal(95, m.MinX, 6); Assert.Equal(105, m.MaxX, 6);
        Assert.Equal(45, m.MinZ, 6); Assert.Equal(55, m.MaxZ, 6);
    }
}
