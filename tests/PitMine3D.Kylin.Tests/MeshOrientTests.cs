using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网朝向一致化(BFS 传播 + 外向规范)回归 —— 混乱朝向的单位立方体 → 一致 + 体积正确。</summary>
public class MeshOrientTests
{
    // 单位立方体 8 顶点 + 12 三角(面顶点集正确, 但各三角绕向故意混乱)
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) MixedCube()
    {
        var v = new List<(double, double, double)>
        {
            (0,0,0),(1,0,0),(1,1,0),(0,1,0),   // 0..3 底
            (0,0,1),(1,0,1),(1,1,1),(0,1,1),   // 4..7 顶
        };
        var t = new List<(int, int, int)>
        {
            (0,1,2),(0,2,3),   // 底(z=0)
            (4,5,6),(4,6,7),   // 顶(z=1)
            (0,1,5),(0,5,4),   // 前(y=0)
            (2,3,7),(2,7,6),   // 后(y=1)
            (0,3,7),(0,7,4),   // 左(x=0)
            (1,2,6),(1,6,5),   // 右(x=1)
        };
        return (v, t);
    }

    [Fact]
    public void MixedWinding_cube_becomes_consistent_and_outward_unit_volume()
    {
        var (v, t) = MixedCube();
        var o = MeshOrient.MakeConsistent(v, t);
        Assert.True(MeshOrient.IsConsistent(o), "朝向应一致(每有向边至多一次)");
        // 外向规范 → 带符号体积 6× = +6(体积 1)
        Assert.Equal(6.0, MeshOrient.SignedVolume6(v, o), 6);
        Assert.Equal(12, o.Count);                       // 三角数不变
    }

    [Fact]
    public void One_flipped_face_is_corrected()
    {
        var (v, t) = MixedCube();
        // 先一致化得基准
        var baseT = MeshOrient.MakeConsistent(v, t);
        double v0 = MeshOrient.SignedVolume6(v, baseT);
        // 翻转其中一面 → 不一致
        var bad = new List<(int, int, int)>(baseT);
        var f = bad[3]; bad[3] = (f.Item1, f.Item3, f.Item2);
        Assert.False(MeshOrient.IsConsistent(bad), "翻一面后应不一致");
        // 再一致化 → 恢复一致 + 同体积
        var fixedT = MeshOrient.MakeConsistent(v, bad);
        Assert.True(MeshOrient.IsConsistent(fixedT));
        Assert.Equal(v0, MeshOrient.SignedVolume6(v, fixedT), 6);
    }

    [Fact]
    public void Idempotent_on_already_consistent()
    {
        var (v, t) = MixedCube();
        var once = MeshOrient.MakeConsistent(v, t);
        var twice = MeshOrient.MakeConsistent(v, once);
        Assert.Equal(MeshOrient.SignedVolume6(v, once), MeshOrient.SignedVolume6(v, twice), 6);
        Assert.True(MeshOrient.IsConsistent(twice));
    }

    [Fact]
    public void Empty_and_single_triangle_safe()
    {
        Assert.Empty(MeshOrient.MakeConsistent(new List<(double, double, double)>(), new List<(int, int, int)>()));
        var v = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        var one = MeshOrient.MakeConsistent(v, new List<(int, int, int)> { (0, 1, 2) });
        Assert.Single(one);   // 单三角不崩
    }
}
