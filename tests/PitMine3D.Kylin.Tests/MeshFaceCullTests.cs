using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网剔面(MeshFaceCull)回归 —— 忠实原「按坡度/离地高丢弃三角面」。</summary>
public class MeshFaceCullTests
{
    // 共享顶点; 三角: A 水平(0°) / B 竖直(90°) / C 45°
    private static readonly List<(double x, double y, double z)> V = new()
    {
        (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1), (0, 1, 1),
    };
    private static readonly List<(int a, int b, int c)> T = new()
    {
        (0, 1, 2),   // A: z=0 水平 → 0°
        (0, 1, 3),   // B: xz 面 → 90°
        (0, 1, 4),   // C: 法向(0,-1,1) → 45°
    };

    [Fact]
    public void Slope_degrees_computed_correctly()
    {
        Assert.True(MeshFaceCull.SlopeDeg(V[0], V[1], V[2], out double a)); Assert.Equal(0, a, 4);
        Assert.True(MeshFaceCull.SlopeDeg(V[0], V[1], V[3], out double b)); Assert.Equal(90, b, 4);
        Assert.True(MeshFaceCull.SlopeDeg(V[0], V[1], V[4], out double c)); Assert.Equal(45, c, 4);
    }

    [Fact]
    public void BySlope_60_keeps_flat_and_45_drops_vertical()
    {
        var kept = MeshFaceCull.BySlope(V, T, 60);
        Assert.Equal(2, kept.Count);
        Assert.Contains((0, 1, 2), kept);   // 0°
        Assert.Contains((0, 1, 4), kept);   // 45°
        Assert.DoesNotContain((0, 1, 3), kept);   // 90° 删
    }

    [Fact]
    public void BySlope_90_keeps_all_and_0_keeps_only_horizontal()
    {
        Assert.Equal(3, MeshFaceCull.BySlope(V, T, 90).Count);
        var flat = MeshFaceCull.BySlope(V, T, 0);
        Assert.Single(flat);
        Assert.Contains((0, 1, 2), flat);   // 仅水平面
    }

    [Fact]
    public void ByHeight_drops_triangles_above_datum()
    {
        // datum=minZ=0; A 形心 z=0, B/C 形心 z=1/3
        var kept = MeshFaceCull.ByHeight(V, T, 0.2);   // 阈 0.2 → 删 B,C(0.333)
        Assert.Single(kept);
        Assert.Contains((0, 1, 2), kept);
        // 阈 1 → 全留
        Assert.Equal(3, MeshFaceCull.ByHeight(V, T, 1.0).Count);
    }

    [Fact]
    public void Degenerate_triangle_slope_false()
    {
        // 共线三点 → 零面积 → SlopeDeg false, BySlope 跳过
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (2, 0, 0) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2) };
        Assert.False(MeshFaceCull.SlopeDeg(v[0], v[1], v[2], out _));
        Assert.Empty(MeshFaceCull.BySlope(v, t, 90));
    }
}
