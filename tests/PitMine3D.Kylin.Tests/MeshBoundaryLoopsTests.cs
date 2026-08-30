using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>网格边界环提取回归（开放边串环 / 闭合网无环 / 多环）。</summary>
public class MeshBoundaryLoopsTests
{
    [Fact]
    public void Single_triangle_yields_one_loop_of_three()
    {
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2) };
        var loops = MeshBoundaryLoops.Extract(v, t);
        Assert.Single(loops);
        Assert.Equal(3, loops[0].Count);
    }

    [Fact]
    public void Quad_two_triangles_yields_one_outer_loop_of_four()
    {
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) };   // 共享对角边 0-2
        var loops = MeshBoundaryLoops.Extract(v, t);
        Assert.Single(loops);
        Assert.Equal(4, loops[0].Count);   // 外轮廓 4 边
    }

    [Fact]
    public void Closed_tetra_has_no_boundary()
    {
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var t = new List<(int, int, int)> { (0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3) };
        Assert.Empty(MeshBoundaryLoops.Extract(v, t));
    }

    [Fact]
    public void Two_disjoint_triangles_yield_two_loops()
    {
        var v = new List<(double, double, double)>
        {
            (0, 0, 0), (1, 0, 0), (0, 1, 0),        // 三角 A
            (5, 5, 0), (6, 5, 0), (5, 6, 0),        // 三角 B(远离)
        };
        var t = new List<(int, int, int)> { (0, 1, 2), (3, 4, 5) };
        var loops = MeshBoundaryLoops.Extract(v, t);
        Assert.Equal(2, loops.Count);
    }

    [Fact]
    public void Loop_vertices_lie_on_input_coordinates()
    {
        var v = new List<(double, double, double)> { (2, 3, 7), (4, 3, 7), (2, 5, 7) };
        var t = new List<(int, int, int)> { (0, 1, 2) };
        var loop = MeshBoundaryLoops.Extract(v, t)[0];
        // 环点应是输入三点(顺序可从任一边起, 但集合一致)
        Assert.Contains((2.0, 3.0, 7.0), loop);
        Assert.Contains((4.0, 3.0, 7.0), loop);
        Assert.Contains((2.0, 5.0, 7.0), loop);
    }
}
