using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>快速建模组合回归（顶面+底面 → 边界环放样侧壁 → 焊成闭合体）。</summary>
public class MeshQuickModelTests
{
    private static int BoundaryEdges(IReadOnlyList<(int a, int b, int c)> tris)
    {
        var count = new Dictionary<(int, int), int>();
        void Bump(int u, int w) { var k = u < w ? (u, w) : (w, u); count[k] = count.TryGetValue(k, out var n) ? n + 1 : 1; }
        foreach (var (a, b, c) in tris) { Bump(a, b); Bump(b, c); Bump(c, a); }
        int n = 0; foreach (var kv in count) if (kv.Value == 1) n++;
        return n;
    }

    // z 高度的 10×10 方形面(2 三角), 边界环 = 4 角。
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Square(double z, bool up)
    {
        var v = new List<(double x, double y, double z)> { (0, 0, z), (10, 0, z), (10, 10, z), (0, 10, z) };
        var t = up ? new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) }
                   : new List<(int a, int b, int c)> { (0, 2, 1), (0, 3, 2) };
        return (v, t);
    }

    [Fact]
    public void Top_bottom_loft_welds_watertight_box()
    {
        var (tv, tt) = Square(1, up: true);    // 顶面 z=1
        var (bv, bt) = Square(0, up: false);   // 底面 z=0
        var topLoop = MeshBoundaryLoops.Extract(tv, tt);
        var botLoop = MeshBoundaryLoops.Extract(bv, bt);
        Assert.NotEmpty(topLoop); Assert.NotEmpty(botLoop);

        var (sv, st) = SideSurface.Loft(topLoop[0], botLoop[0], closed: true, flip: false);
        Assert.NotEmpty(st);                   // 侧壁非空

        var (verts, tris) = MeshWeld.Concat(new List<(System.Collections.Generic.IReadOnlyList<(double x, double y, double z)>, System.Collections.Generic.IReadOnlyList<(int a, int b, int c)>)>
            { (tv, tt), (bv, bt), (sv, st) });
        var w = MeshWeld.Weld(verts, tris, 1e-4, dropDuplicateTris: true);
        Assert.Equal(0, BoundaryEdges(w.Tris));   // 焊后水密(闭合盒无开放边)
    }
}
