using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>网格简化(顶点聚类)回归 —— 忠实原「网格简化(顶点聚类占位)」。</summary>
namespace PitMine3D.Kylin.Tests;

public class MeshSimplifyTests
{
    // n×n 单元密网格 over [0,10]²(z=0), (n+1)² 顶点 2n² 三角
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Grid(int n)
    {
        var v = new List<(double x, double y, double z)>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
                v.Add((i * 10.0 / n, j * 10.0 / n, 0));
        var t = new List<(int a, int b, int c)>();
        int stride = n + 1;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                t.Add((a, b, d)); t.Add((a, d, c));
            }
        return (v, t);
    }

    [Fact]
    public void Large_ratio_reduces_vertices_and_triangles()
    {
        var (v, t) = Grid(10);   // 121 顶点, 200 三角
        var r = MeshSimplify.ByClustering(v, t, 0.1);   // 容差≈对角14.1×0.1≈1.41 → 并相邻格
        Assert.Equal(121, r.InputVerts);
        Assert.Equal(200, r.InputTris);
        Assert.True(r.OutputVerts < r.InputVerts * 0.6, $"顶点应显著降({r.OutputVerts}/{r.InputVerts})");
        Assert.True(r.OutputTris < r.InputTris, $"三角应降({r.OutputTris}/{r.InputTris})");
        Assert.True(r.DroppedDegenerate > 0, "应有塌陷三角被丢");
    }

    [Fact]
    public void Bounding_box_preserved_corners_survive()
    {
        var (v, t) = Grid(10);
        var r = MeshSimplify.ByClustering(v, t, 0.1);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in r.Verts) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; }
        Assert.Equal(0, minX, 6); Assert.Equal(0, minY, 6);      // 角点相距远(>容差)不并 → 包围盒守住
        Assert.Equal(10, maxX, 6); Assert.Equal(10, maxY, 6);
    }

    [Fact]
    public void Tiny_ratio_keeps_all_vertices()
    {
        var (v, t) = Grid(10);
        var r = MeshSimplify.ByClustering(v, t, 0.0001);   // 容差≈0.0014 « 格距1 → 不并
        Assert.Equal(121, r.OutputVerts);
        Assert.Equal(200, r.OutputTris);
    }

    [Fact]
    public void Monotone_more_reduction_with_bigger_ratio()
    {
        var (v, t) = Grid(12);
        var mild = MeshSimplify.ByClustering(v, t, 0.05).OutputVerts;
        var hard = MeshSimplify.ByClustering(v, t, 0.2).OutputVerts;
        Assert.True(hard <= mild, $"大容差简化更狠({hard}≤{mild})");
    }

    [Fact]
    public void Empty_is_safe()
    {
        var r = MeshSimplify.ByClustering(new List<(double x, double y, double z)>(), new List<(int a, int b, int c)>(), 0.1);
        Assert.Equal(0, r.OutputVerts);
        Assert.Equal(0, r.OutputTris);
    }
}
