using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网场景实体：边去重镶嵌、拾取距离、存档往返、分解、特性描述。</summary>
[Collection("MeshRenderMode")]
public class MeshEntityTests
{
    private static MeshEntity Quad()
    {
        // 两三角组成的 1×1 方形, 共享对角边 → 唯一边 5 条
        var v = new List<(double x, double y, double z)> { (0, 0, 10), (1, 0, 11), (1, 1, 12), (0, 1, 13) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return new MeshEntity("测试网", v, t);
    }

    [Fact]
    public void Edges_DedupSharedEdge_AndTessellateUsesVertexZ()
    {
        var m = Quad();
        Assert.Equal(5, m.Edges.Count);
        var o = new List<float>();
        m.TessellateEdges(o);   // 不受全局显示模式影响
        Assert.Equal(5 * 12, o.Count);
        var zs = new HashSet<float>();
        for (int i = 2; i < o.Count; i += 6) zs.Add(o[i]);
        Assert.Equal(new[] { 10f, 11f, 12f, 13f }, zs.OrderBy(z => z).ToArray());
    }

    [Fact]
    public void DistanceTo_InsideNearEdge_OutsideRejected()
    {
        var m = Quad();
        Assert.True(m.DistanceTo(0.5, 0.02) < 0.03);        // 贴近底边
        Assert.True(m.DistanceTo(5, 5) > 1e8);              // 包围盒外明确落选
        var b = m.Bounds;
        Assert.Equal((0, 0, 1, 1, 10, 13), (b.minX, b.minY, b.maxX, b.maxY, b.minZ, b.maxZ));
    }

    [Fact]
    public void SceneIO_RoundTrip_KeepsNameGeometryStyle()
    {
        var scene = new Scene();
        var m = Quad(); m.LayerName = "地表"; m.Cr = 0.1f; m.Cg = 0.2f; m.Cb = 0.3f;
        scene.Add(m);
        var json = SceneIO.Save(scene);
        var back = SceneIO.Load(json);
        var me = Assert.IsType<MeshEntity>(Assert.Single(back.Entities));
        Assert.Equal("测试网", me.Name);
        Assert.Equal(4, me.VertexCount); Assert.Equal(2, me.TriangleCount);
        Assert.Equal((1.0, 1.0, 12.0), me.Verts[2]);
        Assert.Equal((0, 2, 3), me.Tris[1]);
        Assert.Equal("地表", me.LayerName);
        Assert.Equal(0.2f, me.Cg);
    }

    [Fact]
    public void Explode_Apply_Properties()
    {
        var m = Quad();
        var ex = m.Explode()!;
        Assert.Equal(5, ex.Count);
        Assert.All(ex, e => Assert.IsType<LineEntity>(e));
        var moved = (MeshEntity)m.Apply(Affine2.Translate(10, 0));
        Assert.Equal(10.0, moved.Verts[0].x); Assert.Equal(10.0, moved.Verts[0].z);
        Assert.Equal(1.0, m.SurfaceArea() > 1.0 ? 1.0 : 1.0);   // 斜面面积 ≥ 投影面积 1
        Assert.True(m.SurfaceArea() >= 1.0);
        var props = EntityProperties.Describe(m);
        Assert.Contains(props, p => p.Item2 == "类型" && p.Item3 == "三角网");
        Assert.Contains(props, p => p.Item2 == "三角数" && p.Item3 == "2");
        Assert.Empty(m.Grips());
    }
}
