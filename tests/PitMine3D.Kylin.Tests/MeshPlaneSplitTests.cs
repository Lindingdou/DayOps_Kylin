using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网竖直面切分回归（三角形-平面裁剪，面积守恒）。</summary>
public class MeshPlaneSplitTests
{
    private static double Area(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        double s = 0;
        foreach (var (a, b, c) in t)
        {
            var (ax, ay, az) = v[a]; var (bx, by, bz) = v[b]; var (cx, cy, cz) = v[c];
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double wx = cx - ax, wy = cy - ay, wz = cz - az;
            double cxp = uy * wz - uz * wy, cyp = uz * wx - ux * wz, czp = ux * wy - uy * wx;
            s += 0.5 * Math.Sqrt(cxp * cxp + cyp * cyp + czp * czp);
        }
        return s;
    }

    // 10×10 平板(z=0), 2 三角。
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Slab()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return (v, t);
    }

    [Fact]
    public void Split_preserves_total_area()
    {
        var (v, t) = Slab();
        double orig = Area(v, t);
        Assert.Equal(100.0, orig, 6);
        var (l, r) = MeshPlaneSplit.Split(v, t, 5, 0, 5, 10);   // x=5 竖直面
        Assert.NotEmpty(l.t); Assert.NotEmpty(r.t);             // 两片都非空
        Assert.Equal(orig, Area(l.v, l.t) + Area(r.v, r.t), 4); // 面积守恒
    }

    [Fact]
    public void Cut_vertices_lie_on_plane()
    {
        var (v, t) = Slab();
        var (l, r) = MeshPlaneSplit.Split(v, t, 5, 0, 5, 10);
        // 约定 左=符号距离<0 侧: 法向 n=(-1,0)、d=5-x → d<0 即 x>5。故 左片 x>=5, 右片 x<=5。
        Assert.All(l.v, p => Assert.True(p.x >= 5 - 1e-6, $"左片 x={p.x}"));
        Assert.All(r.v, p => Assert.True(p.x <= 5 + 1e-6, $"右片 x={p.x}"));
    }

    [Fact]
    public void No_crossing_keeps_one_side_empty()
    {
        var (v, t) = Slab();
        var (l, r) = MeshPlaneSplit.Split(v, t, 20, 0, 20, 10);  // x=20 面, 整板在一侧
        Assert.True(l.t.Count == 0 || r.t.Count == 0);           // 一侧空
        Assert.Equal(100.0, Area(l.v, l.t) + Area(r.v, r.t), 4);
    }

    [Fact]
    public void Degenerate_line_safe()
    {
        var (v, t) = Slab();
        var (l, r) = MeshPlaneSplit.Split(v, t, 5, 5, 5, 5);     // 退化线(单点)
        Assert.Empty(l.t); Assert.Empty(r.t);
    }
}
