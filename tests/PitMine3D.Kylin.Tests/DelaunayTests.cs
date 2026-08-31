using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>Delaunay 三角剖分回归。</summary>
public class DelaunayTests
{
    [Fact]
    public void Sample_6x6_grid_triangulates_and_builds_edges()
    {
        // TRIMESH 示例三角网核：6×6 网格(与 GenerateSampleTrimesh 同)→ 非退化三角网 + 边
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
                pts.Add((i * 20.0, j * 20.0));
        var tris = Delaunay.Triangulate(pts);
        Assert.True(tris.Count >= 30, $"6×6 网格应产可观三角数, 实得 {tris.Count}");
        var edges = Delaunay.BuildEdges(pts, tris, 0.5f, 0.7f, 0.8f);
        Assert.NotEmpty(edges);
    }

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

    // ── 约束 Delaunay ──
    private static bool HasEdge(List<(int a, int b, int c)> tris, int u, int v)
    {
        foreach (var t in tris)
            foreach (var (p, q) in new[] { (t.a, t.b), (t.b, t.c), (t.c, t.a) })
                if ((p == u && q == v) || (p == v && q == u)) return true;
        return false;
    }
    private static double TriArea(IReadOnlyList<(double x, double y)> pts, List<(int a, int b, int c)> tris)
    {
        double s = 0;
        foreach (var t in tris)
        {
            var A = pts[t.a]; var B = pts[t.b]; var C = pts[t.c];
            s += System.Math.Abs((B.x - A.x) * (C.y - A.y) - (B.y - A.y) * (C.x - A.x)) / 2;
        }
        return s;
    }

    [Fact]
    public void Constrained_edge_appears_and_area_conserved()
    {
        // 方形 5 点(四角 + 偏心点(6,4))，约束一条对角 0→2((0,0)→(10,10))。(6,4) 不在该对角上 → 直边。
        var pts = new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10), (6, 4) };
        double hullArea = 100.0;   // 10×10 正方形凸包
        var con = Delaunay.TriangulateConstrained(pts, new[] { (0, 2) });
        Assert.True(HasEdge(con, 0, 2), "约束边 0-2 应出现在三角网中");
        Assert.Equal(hullArea, TriArea(pts, con), 3);      // 覆盖凸包、无缝隙/重叠
        Assert.Equal(hullArea, TriArea(pts, Delaunay.Triangulate(pts)), 3);   // 无约束对照也覆盖凸包
    }

    [Fact]
    public void Constraint_through_collinear_vertex_splits_into_chain()
    {
        // 约束边穿过中心点(5,5)恰在对角 0-2 上 → 应分段成链 0-4 与 4-2(而非直边 0-2)。
        var pts = new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10), (5, 5) };
        var con = Delaunay.TriangulateConstrained(pts, new[] { (0, 2) });
        Assert.True(HasEdge(con, 0, 4) && HasEdge(con, 4, 2), "穿点约束应成链 0-4-2");
        Assert.Equal(100.0, TriArea(pts, con), 3);
    }

    [Fact]
    public void Constrained_breakline_through_grid_embedded()
    {
        // 5×5 网格(x,y∈{0,10,20,30,40}) + 一条不过网点的斜约束(0,0)→(40,30)(斜率3/4，中间无网点)。
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                pts.Add((i * 10.0, j * 10.0));
        int p00 = 0;               // (0,0)
        int p40_30 = 4 * 5 + 3;    // (40,30)
        var con = Delaunay.TriangulateConstrained(pts, new[] { (p00, p40_30) });
        Assert.True(HasEdge(con, p00, p40_30), "斜约束边应嵌入");
        Assert.Equal(1600.0, TriArea(pts, con), 2);   // 40×40 凸包面积守恒
    }

    [Fact]
    public void Constraint_already_an_edge_is_noop()
    {
        var pts = new List<(double x, double y)> { (0, 0), (10, 0), (5, 10) };   // 单三角
        var con = Delaunay.TriangulateConstrained(pts, new[] { (0, 1) });        // 0-1 已是边
        Assert.Single(con);
        Assert.True(HasEdge(con, 0, 1));
    }
}
