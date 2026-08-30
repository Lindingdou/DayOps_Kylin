using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>合并三角网 / 固化成体 管线回归（Concat 偏移拼接 + 跨网焊接 + 水密自检）。</summary>
public class MeshMergeTests
{
    [Fact]
    public void Concat_offsets_triangle_indices()
    {
        var a = (
            (IReadOnlyList<(double x, double y, double z)>)new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) },
            (IReadOnlyList<(int a, int b, int c)>)new List<(int, int, int)> { (0, 1, 2) });
        var b = (
            (IReadOnlyList<(double x, double y, double z)>)new List<(double, double, double)> { (5, 5, 0), (6, 5, 0), (5, 6, 0) },
            (IReadOnlyList<(int a, int b, int c)>)new List<(int, int, int)> { (0, 1, 2) });
        var (verts, tris) = MeshWeld.Concat(new[] { a, b });
        Assert.Equal(6, verts.Count);
        Assert.Equal((0, 1, 2), tris[0]);
        Assert.Equal((3, 4, 5), tris[1]);   // 第二网索引 +3
    }

    [Fact]
    public void Merge_two_meshes_sharing_edge_welds_verts()
    {
        // 两三角共享边 (1,0,0)-(0,1,0)：拼接后 6 顶点, 焊接后 4
        var a = (
            (IReadOnlyList<(double x, double y, double z)>)new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) },
            (IReadOnlyList<(int a, int b, int c)>)new List<(int, int, int)> { (0, 1, 2) });
        var b = (
            (IReadOnlyList<(double x, double y, double z)>)new List<(double, double, double)> { (1, 1, 0), (1, 0, 0), (0, 1, 0) },
            (IReadOnlyList<(int a, int b, int c)>)new List<(int, int, int)> { (0, 1, 2) });
        var (verts, tris) = MeshWeld.Concat(new[] { a, b });
        var w = MeshWeld.Weld(verts, tris, 1e-6, dropDuplicateTris: true);
        Assert.Equal(6, w.InputVerts);
        Assert.Equal(4, w.OutputVerts);
        Assert.Equal(2, w.OutputTris);
    }

    [Fact]
    public void Solidify_split_tetra_welds_back_watertight()
    {
        // 把闭合四面体拆成 4 份单三角网(各自独立顶点), 合并焊接后应水密
        var faces = new (IReadOnlyList<(double x, double y, double z)>, IReadOnlyList<(int a, int b, int c)>)[4];
        var v = new (double, double, double)[] { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var t = new (int, int, int)[] { (0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3) };
        for (int i = 0; i < 4; i++)
        {
            var (a, b, c) = t[i];
            faces[i] = (
                new List<(double, double, double)> { v[a], v[b], v[c] },
                new List<(int, int, int)> { (0, 1, 2) });
        }
        var (verts, tris) = MeshWeld.Concat(faces);
        Assert.Equal(12, verts.Count);   // 4 面 × 3 独立顶点
        var w = MeshWeld.Weld(verts, tris, 1e-6, dropDuplicateTris: false);
        Assert.Equal(4, w.OutputVerts);  // 焊回 4 顶点
        var d = MeshDiagnose.Analyze(w.Verts, w.Tris);
        Assert.True(d.IsClosed);          // 水密
        Assert.Equal(0, d.BoundaryEdges);
    }

    [Fact]
    public void Concat_empty_is_empty()
    {
        var (verts, tris) = MeshWeld.Concat(new (IReadOnlyList<(double x, double y, double z)>, IReadOnlyList<(int a, int b, int c)>)[0]);
        Assert.Empty(verts);
        Assert.Empty(tris);
    }
}
