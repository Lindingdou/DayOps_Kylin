using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「修复拓扑关系」参数面板 6 个开关的回归：每个开关都必须真的管用（关掉就不做、打开才做），
/// 免得像原版那样面板上摆着、底下走的还是默认值。
/// </summary>
public class MeshRepairOptionsTests
{
    // 单位立方体缺顶面：4 条开放边
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) OpenBox()
    {
        var v = new List<(double, double, double)>
        { (0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1) };
        var t = new List<(int, int, int)>
        {
            (0,1,2),(0,2,3), (0,1,5),(0,5,4), (2,3,7),(2,7,6), (0,3,7),(0,7,4), (1,2,6),(1,6,5),
        };
        return (v, t);
    }

    [Fact]
    public void 补洞开关_打开才补上顶面()
    {
        var (v, t) = OpenBox();
        Assert.Equal(4, MeshDiagnose.Analyze(v, t).BoundaryEdges);

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options { FillHoles = false });
        Assert.Equal(0, off.FilledHoles);
        Assert.Equal(4, off.BoundaryAfter);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options { FillHoles = true });
        Assert.True(on.FilledHoles >= 1);
        Assert.Equal(0, on.BoundaryAfter);
    }

    [Fact]
    public void 退化面开关_打开才删掉零面积三角()
    {
        var (v, t) = OpenBox();
        v.Add((0.5, 0, 0));                       // 落在边 (0,0,0)-(1,0,0) 上
        t.Add((0, 1, v.Count - 1));               // 三点共线 → 零面积

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options
        { RemoveDegenerate = false, WeldVertices = false, FillHoles = false, RemoveIsolated = false, SplitNonManifold = false });
        Assert.Equal(0, off.RemovedDegenerate);
        Assert.Equal(t.Count, off.Tris.Count);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options
        { RemoveDegenerate = true, WeldVertices = false, FillHoles = false, RemoveIsolated = false, SplitNonManifold = false });
        Assert.Equal(1, on.RemovedDegenerate);
        Assert.Equal(t.Count - 1, on.Tris.Count);
    }

    [Fact]
    public void 焊接开关_打开才合并重合顶点()
    {
        var (v, t) = OpenBox();
        int dup = v.Count; v.Add(v[0]);           // 与 0 号点完全重合
        t.Add((dup, 1, 2));                       // 用重复点再画一个面

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options
        { WeldVertices = false, FillHoles = false, RemoveIsolated = false, SplitNonManifold = false });
        Assert.Equal(0, off.WeldedVertices);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options
        { WeldVertices = true, FillHoles = false, RemoveIsolated = false, SplitNonManifold = false });
        Assert.True(on.WeldedVertices >= 1);
    }

    [Fact]
    public void 孤立点开关_打开才移除未被引用的顶点()
    {
        var (v, t) = OpenBox();
        v.Add((99, 99, 99));                      // 谁都不用它

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options
        { RemoveIsolated = false, WeldVertices = false, FillHoles = false, SplitNonManifold = false });
        Assert.Equal(0, off.RemovedIsolated);
        Assert.Equal(v.Count, off.Verts.Count);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options
        { RemoveIsolated = true, WeldVertices = false, FillHoles = false, SplitNonManifold = false });
        Assert.Equal(1, on.RemovedIsolated);
        Assert.Equal(v.Count - 1, on.Verts.Count);
    }

    [Fact]
    public void 非流形开关_打开才把三片共边拆开()
    {
        // 一条边 (0-1) 上挂 3 个三角 → 非流形边
        var v = new List<(double x, double y, double z)>
        { (0,0,0),(1,0,0),(0,1,0),(0,-1,0),(0,0,1) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 1, 3), (0, 1, 4) };
        Assert.Equal(1, MeshDiagnose.Analyze(v, t).NonManifoldEdges);

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options
        { SplitNonManifold = false, WeldVertices = false, FillHoles = false, RemoveIsolated = false });
        Assert.Equal(0, off.SplitNonManifoldEdges);
        Assert.Equal(1, MeshDiagnose.Analyze(off.Verts, off.Tris).NonManifoldEdges);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options
        { SplitNonManifold = true, WeldVertices = false, FillHoles = false, RemoveIsolated = false });
        Assert.Equal(1, on.SplitNonManifoldEdges);
        Assert.Equal(0, MeshDiagnose.Analyze(on.Verts, on.Tris).NonManifoldEdges);
        Assert.Equal(3, on.Tris.Count);           // 面不减, 只是把顶点复制开
    }

    [Fact]
    public void 全关_什么都不动()
    {
        var (v, t) = OpenBox();
        var r = MeshRepair.Repair(v, t, new MeshRepair.Options
        {
            RemoveDegenerate = false, WeldVertices = false, FillHoles = false,
            RemoveIsolated = false, SplitNonManifold = false, FlipInverted = false,
        });
        Assert.Equal(0, r.TotalChanges);
        Assert.Equal(t.Count, r.Tris.Count);
        Assert.Equal(v.Count, r.Verts.Count);
    }
}
