using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>Delaunay 三角剖分回归。</summary>
public class DelaunayTests
{
    [Fact]
    public void Square_gives_two_triangles()
    {
        var pts = new List<(double x, double y)> { (0, 0), (1, 0), (1, 1), (0, 1) };
        var tris = Delaunay.Triangulate(pts);
        Assert.Equal(2, tris.Count);
        Assert.All(tris, t =>
        {
            Assert.InRange(t.a, 0, 3); Assert.InRange(t.b, 0, 3); Assert.InRange(t.c, 0, 3);
        });
    }

    [Fact]
    public void Single_triangle()
    {
        var pts = new List<(double x, double y)> { (0, 0), (4, 0), (2, 3) };
        Assert.Single(Delaunay.Triangulate(pts));
    }

    [Fact]
    public void Too_few_points_empty()
    {
        Assert.Empty(Delaunay.Triangulate(new List<(double x, double y)> { (0, 0), (1, 1) }));
    }

    [Fact]
    public void Grid_of_points_triangulated()
    {
        // 3x3 网格 9 点 → 2*(2*2)=8 三角形（矩形网每格 2 个）
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                pts.Add((i, j));
        var tris = Delaunay.Triangulate(pts);
        Assert.Equal(8, tris.Count);
    }

    [Fact]
    public void BuildEdges_dedups_shared_edges()
    {
        // 方形 2 三角共享 1 对角边 → 4 边 + 1 对角 = 5 条唯一边
        var pts = new List<(double x, double y)> { (0, 0), (1, 0), (1, 1), (0, 1) };
        var tris = Delaunay.Triangulate(pts);
        var edges = Delaunay.BuildEdges(pts, tris, 0.5f, 0.5f, 0.5f);
        Assert.Equal(5, edges.Count);
        Assert.All(edges, e => Assert.IsType<PitMine3D.Kylin.Cad.Draw.LineEntity>(e));
    }
}
