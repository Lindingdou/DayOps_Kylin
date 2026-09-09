using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 消 T 型接缝回归：两个三角形共一条边，其中一侧被拆成两段（中点被引用）——
/// 顶点是重合的、焊接看不出问题，但边对不上，诊断出来就是开放边。修完必须闭合且面积守恒。
/// </summary>
public class MeshTJunctionTests
{
    // 正方形拆成 4 个三角: 左半 1 个整三角, 右半沿中点拆成 2 个 → 共用边一侧有中点、一侧没有
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) TSeam()
    {
        var v = new List<(double x, double y, double z)>
        {
            (0,0,0),   // 0
            (2,0,0),   // 1
            (2,2,0),   // 2
            (0,2,0),   // 3
            (1,1,0),   // 4 —— 落在对角线 0-2 的中点
        };
        var t = new List<(int a, int b, int c)>
        {
            (0,1,2),          // 右下整三角（用整条对角线 0-2）
            (0,4,3),(4,2,3),  // 左上被中点 4 拆成两片
        };
        return (v, t);
    }

    private static double AreaXY(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        double s = 0;
        foreach (var (a, b, c) in t)
            s += System.Math.Abs((v[b].x - v[a].x) * (v[c].y - v[a].y) - (v[c].x - v[a].x) * (v[b].y - v[a].y)) / 2;
        return s;
    }

    [Fact]
    public void T型接缝_修前有开放边_修后边对齐且面积守恒()
    {
        var (v, t) = TSeam();
        double areaBefore = AreaXY(v, t);
        Assert.Equal(4.0, areaBefore, 9);

        // 修前：中点 4 只被左上两片用到，右下那片仍用整条 0-2 → 半边 0-4 / 4-2 各只被一个三角用
        var before = MeshDiagnose.Analyze(v, t);
        Assert.True(before.BoundaryEdges > 4, $"应有多余开放边（实际 {before.BoundaryEdges}）");

        var (nv, nt, split) = MeshTJunction.Fix(v, t);
        Assert.Equal(1, split);                             // 只有右下那片需要重剖
        Assert.Equal(areaBefore, AreaXY(nv, nt), 9);        // 面积守恒

        // 修后：外框 4 条边仍是开放边（这是块开放面片），但内部不再有对不上的半边
        var after = MeshDiagnose.Analyze(nv, nt);
        Assert.Equal(4, after.BoundaryEdges);
    }

    [Fact]
    public void 没有悬挂点时_原样返回不加面()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (1, 3, 2) };
        var (nv, nt, split) = MeshTJunction.Fix(v, t);
        Assert.Equal(0, split);
        Assert.Equal(v.Count, nv.Count);
        Assert.Equal(t.Count, nt.Count);
    }
}
