using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网补洞回归（提边界环→扇形填充→水密）。</summary>
public class MeshHoleFillTests
{
    // 开顶盒（缺顶面）：8 顶点 + 10 三角(底 + 4 侧)，顶面开口 = 一个 4 边洞。
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) OpenBox()
    {
        var v = new List<(double x, double y, double z)>
        {
            (0,0,0),(1,0,0),(1,1,0),(0,1,0),   // 0..3 底
            (0,0,1),(1,0,1),(1,1,1),(0,1,1),   // 4..7 顶
        };
        var t = new List<(int a, int b, int c)>
        {
            (0,1,2),(0,2,3),        // 底
            (0,1,5),(0,5,4),        // 前
            (1,2,6),(1,6,5),        // 右
            (2,3,7),(2,7,6),        // 后
            (3,0,4),(3,4,7),        // 左
            // 顶面缺失 → 洞(4,5,6,7)
        };
        return (v, t);
    }

    private static int BoundaryEdgeCount(IReadOnlyList<(int a, int b, int c)> tris)
    {
        var count = new Dictionary<(int, int), int>();
        void Bump(int u, int w) { var k = u < w ? (u, w) : (w, u); count[k] = count.TryGetValue(k, out var n) ? n + 1 : 1; }
        foreach (var (a, b, c) in tris) { Bump(a, b); Bump(b, c); Bump(c, a); }
        int n = 0; foreach (var kv in count) if (kv.Value == 1) n++;
        return n;
    }

    [Fact]
    public void Fills_single_hole_watertight()
    {
        var (v, t) = OpenBox();
        Assert.Equal(4, BoundaryEdgeCount(t));                       // 开口 4 条边界边
        Assert.NotEmpty(MeshBoundaryLoops.Extract(v, t));            // 有边界环

        var (nv, nt, holes) = MeshHoleFill.Fill(v, t);
        Assert.Equal(1, holes);                                     // 补 1 洞
        Assert.Equal(v.Count + 1, nv.Count);                        // +1 质心顶点
        Assert.Equal(t.Count + 4, nt.Count);                        // +4 扇形三角
        Assert.Equal(0, BoundaryEdgeCount(nt));                     // 补后水密(无边界边)
    }

    [Fact]
    public void Closed_mesh_unchanged()
    {
        // 单个四面体(封闭) → 无洞, 不变。
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var t = new List<(int a, int b, int c)> { (0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3) };
        Assert.Equal(0, BoundaryEdgeCount(t));                      // 已封闭
        var (nv, nt, holes) = MeshHoleFill.Fill(v, t);
        Assert.Equal(0, holes);
        Assert.Equal(v.Count, nv.Count);
        Assert.Equal(t.Count, nt.Count);
    }

    [Fact]
    public void Empty_or_degenerate_safe()
    {
        var (nv, nt, holes) = MeshHoleFill.Fill(new List<(double, double, double)>(), new List<(int, int, int)>());
        Assert.Equal(0, holes);
        Assert.Empty(nt);
    }
}
