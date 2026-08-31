using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>按闭合边界把既有三角网分内/外两片(pc_tin_split)回归 —— 质心判别 + 守恒 + 顶点重映射有效。</summary>
public class MeshBoundarySplitTests
{
    // 4 个分离小三角, 质心分别在四象限角: T0(0.33,0.33)/T1(3.33,0.33)/T2(0.33,3.33)/T3(3.33,3.33)
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) FourTris() =>
    (
        new List<(double, double, double)>
        {
            (0,0,0),(1,0,0),(0,1,0),      // T0 质心(0.33,0.33)
            (3,0,0),(4,0,0),(3,1,0),      // T1 质心(3.33,0.33)
            (0,3,0),(1,3,0),(0,4,0),      // T2 质心(0.33,3.33)
            (3,3,0),(4,3,0),(3,4,0),      // T3 质心(3.33,3.33)
        },
        new List<(int, int, int)> { (0,1,2),(3,4,5),(6,7,8),(9,10,11) }
    );

    private static readonly List<(double x, double y)> LowerLeftSquare = new() { (0, 0), (2, 0), (2, 2), (0, 2) };

    [Fact]
    public void Splits_by_centroid_conserving_triangle_count()
    {
        var (v, t) = FourTris();
        var (inside, outside) = MeshBoundarySplit.ByPolygon(v, t, LowerLeftSquare);
        Assert.Equal(1, inside.Tris.Count);                 // 仅 T0 质心在 [0,2]²
        Assert.Equal(3, outside.Tris.Count);
        Assert.Equal(t.Count, inside.Tris.Count + outside.Tris.Count);   // 守恒: 不重不漏
    }

    [Fact]
    public void Each_piece_is_standalone_with_valid_remapped_indices()
    {
        var (v, t) = FourTris();
        var (inside, outside) = MeshBoundarySplit.ByPolygon(v, t, LowerLeftSquare);
        Assert.Equal(3, inside.Verts.Count);                // T0 的 3 顶点
        Assert.Equal(9, outside.Verts.Count);               // T1/T2/T3 共 9 顶点
        foreach (var piece in new[] { inside, outside })
            foreach (var (a, b, c) in piece.Tris)
            {
                Assert.InRange(a, 0, piece.Verts.Count - 1); // 索引在自身顶点表内(重映射正确)
                Assert.InRange(b, 0, piece.Verts.Count - 1);
                Assert.InRange(c, 0, piece.Verts.Count - 1);
            }
    }

    [Fact]
    public void Inside_piece_centroids_all_within_boundary()
    {
        var (v, t) = FourTris();
        var (inside, _) = MeshBoundarySplit.ByPolygon(v, t, LowerLeftSquare);
        foreach (var (a, b, c) in inside.Tris)
        {
            double cx = (inside.Verts[a].x + inside.Verts[b].x + inside.Verts[c].x) / 3;
            double cy = (inside.Verts[a].y + inside.Verts[b].y + inside.Verts[c].y) / 3;
            Assert.True(PitMine3D.Kylin.Cad.Draw.LineMath.PointInPolygon(cx, cy, LowerLeftSquare));
        }
    }

    [Fact]
    public void No_boundary_puts_all_outside()
    {
        var (v, t) = FourTris();
        var (inside, outside) = MeshBoundarySplit.ByPolygon(v, t, new List<(double x, double y)>());   // <3 点
        Assert.Empty(inside.Tris);
        Assert.Equal(4, outside.Tris.Count);
    }

    [Fact]
    public void Null_inputs_safe()
    {
        var (inside, outside) = MeshBoundarySplit.ByPolygon(null!, null!, LowerLeftSquare);
        Assert.Empty(inside.Tris); Assert.Empty(outside.Tris);
    }
}
