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

    // 3×3 格网 TIN(8 三角), 中心顶点 v4 抬成倒刺
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Grid3x3(double centerZ)
    {
        var v = new List<(double x, double y, double z)>
        {
            (0,0,0),(1,0,0),(2,0,0), (0,1,0),(1,1,centerZ),(2,1,0), (0,2,0),(1,2,0),(2,2,0),
        };
        var t = new List<(int a, int b, int c)>
        { (0,1,4),(0,4,3),(1,2,5),(1,5,4),(3,4,7),(3,7,6),(4,5,8),(4,8,7) };
        return (v, t);
    }

    [Fact]
    public void BySpike_removes_triangles_touching_local_high_spike()
    {
        var (v, t) = Grid3x3(centerZ: 10);   // 中心 v4 抬高 10(倒刺)
        var kept = MeshFaceCull.BySpike(v, t, heightTol: 1);
        // v4 是倒刺(z=10 vs 邻居中位 0) → 含 v4 的 6 三角删, 留 2 (1,2,5)/(3,7,6)
        Assert.Equal(2, kept.Count);
        Assert.All(kept, tr => Assert.True(tr.a != 4 && tr.b != 4 && tr.c != 4));
    }

    [Fact]
    public void BySpike_keeps_all_on_flat_or_gentle()
    {
        var (v, t) = Grid3x3(centerZ: 0);    // 全平 → 无倒刺
        Assert.Equal(t.Count, MeshFaceCull.BySpike(v, t, heightTol: 1).Count);
    }

    [Fact]
    public void BySpike_preserves_uniform_slope()
    {
        // 均匀斜坡 z=x：每顶点 z≈邻居中位, 无倒刺 → 全留(区别于绝对高度剔面会误删高处)
        var v = new List<(double x, double y, double z)>
        { (0,0,0),(1,0,1),(2,0,2), (0,1,0),(1,1,1),(2,1,2), (0,2,0),(1,2,1),(2,2,2) };
        var t = new List<(int a, int b, int c)>
        { (0,1,4),(0,4,3),(1,2,5),(1,5,4),(3,4,7),(3,7,6),(4,5,8),(4,8,7) };
        Assert.Equal(t.Count, MeshFaceCull.BySpike(v, t, heightTol: 0.5).Count);
    }
}
