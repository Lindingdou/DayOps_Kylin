using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>两三角网求交线回归（MeshIntersect：tri-tri 相交段）。</summary>
public class MeshIntersectTests
{
    // 平面 z=0 的 [0,10]² 方形(2 三角)
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) FlatZ0()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return (v, t);
    }
    // 斜面 z = y-5 的 [0,10]² 方形(2 三角)——与 z=0 交于 y=5,z=0
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) TiltZeqYm5()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, -5), (10, 0, -5), (10, 10, 5), (0, 10, 5) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return (v, t);
    }

    [Fact]
    public void Two_crossing_planes_intersect_on_known_line()
    {
        var (v1, t1) = FlatZ0();
        var (v2, t2) = TiltZeqYm5();
        var segs = MeshIntersect.IntersectionSegments(v1, t1, v2, t2);
        Assert.NotEmpty(segs);
        // 交线应为 y=5, z=0（两面在此相交）
        foreach (var s in segs)
        {
            Assert.Equal(5.0, s.A.y, 6); Assert.Equal(5.0, s.B.y, 6);
            Assert.Equal(0.0, s.A.z, 6); Assert.Equal(0.0, s.B.z, 6);
        }
        // x 跨度应覆盖 [0,10] 大部
        double xmin = segs.Min(s => Math.Min(s.A.x, s.B.x));
        double xmax = segs.Max(s => Math.Max(s.A.x, s.B.x));
        Assert.True(xmin < 1.0 && xmax > 9.0, $"交线 x 跨度应≈[0,10]，实 [{xmin:0.#},{xmax:0.#}]");
    }

    [Fact]
    public void Separated_meshes_have_no_intersection()
    {
        var (v1, t1) = FlatZ0();
        // 高高在上的平面 z=100 → 与 z=0 无交
        var v2 = new List<(double x, double y, double z)> { (0, 0, 100), (10, 0, 100), (10, 10, 100), (0, 10, 100) };
        var t2 = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        Assert.Empty(MeshIntersect.IntersectionSegments(v1, t1, v2, t2));
    }

    [Fact]
    public void Coplanar_meshes_skipped_no_crash()
    {
        var (v1, t1) = FlatZ0();
        var (v2, t2) = FlatZ0();   // 完全共面 → 跳过(不崩)
        var segs = MeshIntersect.IntersectionSegments(v1, t1, v2, t2);
        Assert.Empty(segs);        // 共面三角被 SameSide/近共面 跳过
    }
}
