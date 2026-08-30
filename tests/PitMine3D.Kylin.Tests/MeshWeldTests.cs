using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>网格焊接回归（空间哈希合并重合顶点 + 剔退化/去重复三角 + OFF 序列化）。</summary>
public class MeshWeldTests
{
    [Fact]
    public void Coincident_vertices_merge()
    {
        // 两三角本应共享一条边, 但边上两顶点被重复录入(索引 1、4 同坐标; 2、5 同坐标)
        var v = new List<(double, double, double)>
        {
            (0, 0, 0), (1, 0, 0), (0, 1, 0),   // 三角 0
            (1, 1, 0), (1, 0, 0), (0, 1, 0),   // 三角 1: 顶点 4=顶点1, 顶点5=顶点2
        };
        var t = new List<(int, int, int)> { (0, 1, 2), (3, 4, 5) };
        var w = MeshWeld.Weld(v, t, tolerance: 1e-6);
        Assert.Equal(6, w.InputVerts);
        Assert.Equal(4, w.OutputVerts);   // 6 → 4（合并两对重合点）
        Assert.Equal(2, w.OutputTris);
    }

    [Fact]
    public void Degenerate_after_weld_dropped()
    {
        // 三角三点中两点重合(容差内) → 焊后塌成线, 丢弃
        var v = new List<(double, double, double)> { (0, 0, 0), (0, 0, 0), (1, 0, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2) };
        var w = MeshWeld.Weld(v, t, tolerance: 1e-6);
        Assert.Equal(2, w.OutputVerts);
        Assert.Equal(0, w.OutputTris);
        Assert.Equal(1, w.DroppedDegenerate);
    }

    [Fact]
    public void Duplicate_triangles_removed_when_requested()
    {
        // 同一三角录两次(缠绕相反) → dropDuplicateTris 只留一个
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2), (0, 2, 1) };
        var w = MeshWeld.Weld(v, t, tolerance: 1e-6, dropDuplicateTris: true);
        Assert.Equal(1, w.OutputTris);
        Assert.Equal(1, w.DuplicateTris);
    }

    [Fact]
    public void Tolerance_respected()
    {
        // 顶点 0 与 3 相距 1e-4；4=1、5=2 完全重合
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0.0001, 0, 0), (1, 0, 0), (0, 1, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2), (3, 4, 5) };
        // 小容差：0 与 3 不合并 → 4 个不同点(0,3,1,2)
        Assert.Equal(4, MeshWeld.Weld(v, t, tolerance: 1e-6).OutputVerts);
        // 大容差：0 与 3 合并 → 3 个点
        Assert.Equal(3, MeshWeld.Weld(v, t, tolerance: 1e-2).OutputVerts);
    }

    [Fact]
    public void ToOff_roundtrips_through_ParseOff()
    {
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var t = new List<(int, int, int)> { (0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3) };
        string off = MeshWeld.ToOff(v, t);
        var (v2, t2) = MeshMetrics.ParseOff(off);
        Assert.Equal(4, v2.Count);
        Assert.Equal(4, t2.Count);
        Assert.Equal(v[1].Item1, v2[1].x, 6);
        Assert.Equal(t[3], t2[3]);
    }
}
