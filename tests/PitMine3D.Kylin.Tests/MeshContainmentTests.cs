using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 网格/曲面空间约束 回归 —— 忠实移植原 MeshContainmentTester 的 4 模式已知值验证:
/// 相对开放曲面 上/下(面高程比较) + 相对闭合网格 内/外(广义缠绕数)。
/// </summary>
public class MeshContainmentTests
{
    // 水平面 z=5 (10×10 方形, 2 三角)。
    static (double[] v, int[] t) FlatSurface()
        => MeshContainment.Flatten(
            new List<(double, double, double)> { (0, 0, 5), (10, 0, 5), (10, 10, 5), (0, 10, 5) },
            new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) });

    // 闭合四面体 (0..10)。
    static (double[] v, int[] t) ClosedTetra()
        => MeshContainment.Flatten(
            new List<(double, double, double)> { (0, 0, 0), (10, 0, 0), (0, 10, 0), (0, 0, 10) },
            new List<(int, int, int)> { (0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3) });

    [Fact]
    public void Above_below_open_surface()
    {
        var (v, t) = FlatSurface();
        // (6,4) 在足迹内, 面高程 5。
        Assert.True(MeshContainment.Keep(v, t, null, 6, 4, 10, MeshConstraintMode.KeepAboveSurface));   // 10>5 上
        Assert.False(MeshContainment.Keep(v, t, null, 6, 4, 10, MeshConstraintMode.KeepBelowSurface));
        Assert.True(MeshContainment.Keep(v, t, null, 6, 4, 0, MeshConstraintMode.KeepBelowSurface));    // 0<5 下
        Assert.False(MeshContainment.Keep(v, t, null, 6, 4, 0, MeshConstraintMode.KeepAboveSurface));
        // 面外(取不到面高程) → 上/下均不保留。
        Assert.False(MeshContainment.Keep(v, t, null, 50, 50, 10, MeshConstraintMode.KeepAboveSurface));
        Assert.False(MeshContainment.Keep(v, t, null, 50, 50, 0, MeshConstraintMode.KeepBelowSurface));
    }

    [Fact]
    public void Inside_outside_closed_mesh()
    {
        var (v, t) = ClosedTetra();
        var inside = new WindingNumberTester(v, t);
        // 四面体质心 (2.5,2.5,2.5) 在内。
        Assert.True(MeshContainment.Keep(v, t, inside, 2.5, 2.5, 2.5, MeshConstraintMode.KeepInsideClosed));
        Assert.False(MeshContainment.Keep(v, t, inside, 2.5, 2.5, 2.5, MeshConstraintMode.KeepOutsideClosed));
        // 远点在外。
        Assert.False(MeshContainment.Keep(v, t, inside, 100, 100, 100, MeshConstraintMode.KeepInsideClosed));
        Assert.True(MeshContainment.Keep(v, t, inside, 100, 100, 100, MeshConstraintMode.KeepOutsideClosed));
    }

    [Fact]
    public void KeepIndices_filters_point_list_below_surface()
    {
        var (v, t) = FlatSurface();
        var pts = new List<(double x, double y, double z)>
        {
            (6, 4, 2),    // 0: 下(保留)
            (6, 4, 8),    // 1: 上(不保留)
            (3, 3, 1),    // 2: 下(保留)
            (50, 50, 1),  // 3: 面外(不保留)
        };
        var keep = MeshContainment.KeepIndices(v, t, pts, MeshConstraintMode.KeepBelowSurface);
        Assert.Equal(new[] { 0, 2 }, keep.ToArray());
    }

    [Fact]
    public void KeepIndices_inside_closed()
    {
        var (v, t) = ClosedTetra();
        var pts = new List<(double x, double y, double z)>
        {
            (2.5, 2.5, 2.5),   // 0: 内
            (1, 1, 1),         // 1: 内
            (100, 100, 100),   // 2: 外
        };
        var keep = MeshContainment.KeepIndices(v, t, pts, MeshConstraintMode.KeepInsideClosed);
        Assert.Contains(0, keep);
        Assert.Contains(1, keep);
        Assert.DoesNotContain(2, keep);
    }
}
