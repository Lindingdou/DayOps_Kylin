using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网拓扑修复流水线(焊接→朝向→补洞)回归 —— 开口盒补成水密 + 重合顶点焊接。</summary>
public class MeshRepairTests
{
    // 单位立方体缺顶面(10 三角) → 顶部开口(4 边界边)
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) OpenBox()
    {
        var v = new List<(double, double, double)>
        {
            (0,0,0),(1,0,0),(1,1,0),(0,1,0), (0,0,1),(1,0,1),(1,1,1),(0,1,1),
        };
        var t = new List<(int, int, int)>
        {
            (0,1,2),(0,2,3),                 // 底
            (0,1,5),(0,5,4),                 // 前
            (2,3,7),(2,7,6),                 // 后
            (0,3,7),(0,7,4),                 // 左
            (1,2,6),(1,6,5),                 // 右 —— 缺顶面(4,5,6),(4,6,7)
        };
        return (v, t);
    }

    [Fact]
    public void Open_box_repaired_to_watertight()
    {
        var (v, t) = OpenBox();
        Assert.Equal(4, MeshDiagnose.Analyze(v, t).BoundaryEdges);   // 顶开口 4 边界边
        var r = MeshRepair.Repair(v, t);
        Assert.Equal(4, r.BoundaryBefore);
        Assert.Equal(0, r.BoundaryAfter);                            // 补洞后水密
        Assert.Equal(1, r.FilledHoles);
    }

    [Fact]
    public void Coincident_vertices_are_welded()
    {
        // 两三角本应共享边 v1-v2, 但 v3≡v1、v4≡v2 是重复顶点(6 顶点 → 应焊成 4)
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0) };
        var t = new List<(int, int, int)> { (0, 1, 2), (3, 5, 4) };
        var r = MeshRepair.Repair(v, t);
        Assert.Equal(6, r.VertsBefore);
        Assert.True(r.VertsAfter < 6, $"重合顶点应被焊接: {r.VertsAfter} < 6");
    }

    [Fact]
    public void Already_clean_mesh_stays_watertight()
    {
        // 已水密网格(用 MeshOrient 测试的完整立方体) → 修复后仍水密, 无洞可补
        var v = new List<(double, double, double)>
        { (0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1) };
        var t = new List<(int, int, int)>
        { (0,1,2),(0,2,3),(4,5,6),(4,6,7),(0,1,5),(0,5,4),(2,3,7),(2,7,6),(0,3,7),(0,7,4),(1,2,6),(1,6,5) };
        var r = MeshRepair.Repair(v, t);
        Assert.Equal(0, r.BoundaryBefore);
        Assert.Equal(0, r.BoundaryAfter);
        Assert.Equal(0, r.FilledHoles);
    }

    [Fact]
    public void Degenerate_input_safe()
    {
        var r = MeshRepair.Repair(new List<(double, double, double)>(), new List<(int, int, int)>());
        Assert.Empty(r.Tris);
    }
}
