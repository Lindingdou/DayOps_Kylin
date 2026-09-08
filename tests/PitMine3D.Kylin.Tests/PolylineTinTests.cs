using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 由多段线建三角网（等值线建面）。此前「创建三角网」只收点实体，
/// 只选多段线会直接提示"需 ≥3 个点"——这里验线的顶点与约束边收集正确。
/// </summary>
public class PolylineTinTests
{
    private static PolylineTin.Line L(bool closed, double z, params (double x, double y)[] pts)
        => new() { Points = pts, FlatZ = z, Closed = closed };

    private static PolylineTin.Line L3(bool closed, (double x, double y, double z)[] pts)
        => new()
        {
            Points = pts.Select(p => (p.x, p.y)).ToList(),
            Z = pts.Select(p => p.z).ToList(),
            Closed = closed,
        };

    [Fact]
    public void OpenLine_takesEveryVertex_andEachSegmentIsAConstraint()
    {
        var r = PolylineTin.Collect(new[] { L(false, 100, (0, 0), (10, 0), (20, 0)) });
        Assert.Equal(3, r.Verts.Count);
        Assert.Equal(2, r.Constraints.Count);          // 3 点 2 段, 不闭合不补收尾段
        Assert.All(r.Verts, v => Assert.Equal(100, v.z, 9));
        Assert.Equal(0, r.MergedVertices);
    }

    [Fact]
    public void ClosedLine_getsTheWrapAroundConstraint()
    {
        var r = PolylineTin.Collect(new[] { L(true, 50, (0, 0), (10, 0), (10, 10)) });
        Assert.Equal(3, r.Verts.Count);
        Assert.Equal(3, r.Constraints.Count);          // 2 段 + 收尾段
        Assert.Contains((2, 0), r.Constraints);
    }

    [Fact]
    public void ClosedLine_thatAlreadyRepeatsFirstPoint_doesNotGetADuplicateConstraint()
    {
        // 首尾点重合的闭合线：末点会被并到首点上，不该再补一条自环约束
        var r = PolylineTin.Collect(new[] { L(true, 0, (0, 0), (10, 0), (10, 10), (0, 0)) });
        Assert.Equal(3, r.Verts.Count);
        Assert.Equal(1, r.MergedVertices);
        Assert.Equal(3, r.Constraints.Count);
        Assert.DoesNotContain(r.Constraints, c => c.u == c.v);
    }

    [Fact]
    public void PerVertexElevation_isKept()
    {
        var r = PolylineTin.Collect(new[] { L3(false, new[] { (0.0, 0.0, 10.0), (5.0, 0.0, 20.0), (9.0, 0.0, 30.0) }) });
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, r.Verts.Select(v => v.z).ToArray());
    }

    [Fact]
    public void SharedEndpoints_acrossLines_areMergedIntoOneVertex()
    {
        // 等值线常在端点处首尾相接；不合并会给三角化喂入重合点, 剖分直接失败
        var a = L(false, 0, (0, 0), (10, 0));
        var b = L(false, 0, (10, 0), (20, 0));
        var r = PolylineTin.Collect(new[] { a, b });
        Assert.Equal(3, r.Verts.Count);                // 不是 4
        Assert.Equal(1, r.MergedVertices);
        Assert.Equal(2, r.Constraints.Count);
        Assert.Contains((1, 2), r.Constraints);        // 第二条线接在合并后的顶点上
    }

    [Fact]
    public void ConstraintsSurviveTriangulation_andContourEdgesAreHonoured()
    {
        // 两条等高线：约束边必须出现在剖分结果的边集中，否则地形结构线被跨过
        var lines = new[]
        {
            L(false, 100, (0, 0), (10, 0), (20, 0)),
            L(false, 110, (0, 10), (10, 10), (20, 10)),
        };
        var r = PolylineTin.Collect(lines);
        Assert.Equal(6, r.Verts.Count);
        var tris = Delaunay.TriangulateConstrained(r.Verts.Select(v => (v.x, v.y)).ToList(), r.Constraints);
        Assert.NotEmpty(tris);

        var edges = new HashSet<(int, int)>();
        foreach (var (a, b, c) in tris)
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                edges.Add(u < v ? (u, v) : (v, u));
        foreach (var (u, v) in r.Constraints)
            Assert.Contains(u < v ? (u, v) : (v, u), edges);
    }

    [Fact]
    public void ShortOrEmptyInput_isIgnoredNotCrashed()
    {
        Assert.Empty(PolylineTin.Collect(Array.Empty<PolylineTin.Line>()).Verts);
        var r = PolylineTin.Collect(new[] { L(false, 0, (1, 1)) });   // 只有一个点的线跳过
        Assert.Empty(r.Verts);
        Assert.Empty(r.Constraints);
    }
}
