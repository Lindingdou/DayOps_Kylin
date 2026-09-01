using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>网格拓扑诊断回归（边界边/非流形边/退化三角/洞数/闭合）。</summary>
public class MeshDiagnoseTests
{
    [Fact]
    public void Closed_tetra_is_watertight()
    {
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var t = new List<(int, int, int)> { (0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3) };
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(6, d.EdgeCount);
        Assert.Equal(0, d.BoundaryEdges);
        Assert.Equal(0, d.NonManifoldEdges);
        Assert.Equal(0, d.BoundaryLoops);
        Assert.True(d.IsClosed);
    }

    [Fact]
    public void Single_triangle_has_three_boundary_edges_one_loop()
    {
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2) };
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(3, d.EdgeCount);
        Assert.Equal(3, d.BoundaryEdges);
        Assert.Equal(1, d.BoundaryLoops);
        Assert.False(d.IsClosed);
    }

    [Fact]
    public void Shared_edge_quad_has_four_boundary_edges()
    {
        // 两三角共享对角边 → 5 边：1 内部(inc2) + 4 边界
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) };
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(5, d.EdgeCount);
        Assert.Equal(4, d.BoundaryEdges);
        Assert.Equal(0, d.NonManifoldEdges);
        Assert.Equal(1, d.BoundaryLoops);
    }

    [Fact]
    public void Three_triangles_sharing_edge_is_non_manifold()
    {
        // 边 (0,1) 被 3 个三角共享 → 非流形
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1) };
        var t = new List<(int, int, int)> { (0, 1, 2), (0, 1, 3), (0, 1, 4) };
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(1, d.NonManifoldEdges);
        Assert.False(d.IsClosed);
    }

    [Fact]
    public void Degenerate_triangle_counted_and_skipped()
    {
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2), (0, 0, 1) };   // 第二个索引重复 → 退化
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(1, d.DegenerateTriangles);
        Assert.Equal(1, d.TriangleCount);
    }

    [Fact]
    public void Isolated_vertex_detected()
    {
        // 第 4 点(0,0,5)不被任何三角引用 → 孤立点 1
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 5) };
        var t = new List<(int, int, int)> { (0, 1, 2) };
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(1, d.IsolatedVertices);
        Assert.Equal(0, d.DuplicateVertices);
    }

    [Fact]
    public void Duplicate_vertex_detected()
    {
        // 第 4 点与第 1 点坐标重合 → 重复点 1
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2), (3, 1, 2) };   // 两三角都被引用(无孤立)
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(1, d.DuplicateVertices);
        Assert.Equal(0, d.IsolatedVertices);
    }
}
