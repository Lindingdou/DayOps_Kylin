using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>顶底成体 + 连续多层建模(LayerSolid)回归 —— 抽取自 QuickModelAsync 的 2 面→体核。</summary>
public class LayerSolidTests
{
    // [0,10]² 上 z 恒定的方形面(4 顶点 2 三角, 边界环=四边)
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Quad(double z) =>
    (
        new List<(double, double, double)> { (0, 0, z), (10, 0, z), (10, 10, z), (0, 10, z) },
        new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) }
    );

    [Fact]
    public void Two_quads_weld_into_watertight_box()
    {
        var top = Quad(5); var bot = Quad(0);
        var solid = LayerSolid.FromSurfaces(top.v, top.t, bot.v, bot.t);
        Assert.NotNull(solid);
        var (v, t) = solid!.Value;
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(0, d.BoundaryEdges);        // 水密：无开边
        Assert.Equal(0, d.NonManifoldEdges);
    }

    [Fact]
    public void Box_structure_bbox_tricount_and_exact_volume()
    {
        // 顶+底各 2 三角 + 4 侧壁×2 = 12 三角; 包围盒 = [0,10]²×[0,5]
        var solid = LayerSolid.FromSurfaces(Quad(5).v, Quad(5).t, Quad(0).v, Quad(0).t);
        Assert.NotNull(solid);
        var (v, t) = solid!.Value;
        Assert.Equal(12, t.Count);
        var m = MeshMetrics.Compute(v, t);
        Assert.Equal(0, m.MinZ, 6); Assert.Equal(5, m.MaxZ, 6);
        Assert.Equal(0, m.MinX, 6); Assert.Equal(10, m.MaxX, 6);
        Assert.Equal(0, m.MinY, 6); Assert.Equal(10, m.MaxY, 6);
        // MeshOrient 修朝向后, 散度体积可靠: 10×10×5=500 → 6× 带符号体积=3000(外向正)
        Assert.True(MeshOrient.IsConsistent(t), "成体后朝向应一致");
        Assert.Equal(3000.0, MeshOrient.SignedVolume6(v, t), 3);
    }

    [Fact]
    public void MeanZ_averages_vertices()
    {
        Assert.Equal(5, LayerSolid.MeanZ(Quad(5).v), 6);
        Assert.Equal(0, LayerSolid.MeanZ(Quad(0).v), 6);
    }

    [Fact]
    public void MultiLayer_n_surfaces_makes_n_minus_1_solids_top_down()
    {
        // 3 层 z=0/5/10 → 2 夹层体; 均高降序(顶10→底0)
        var surfaces = new List<(System.Collections.Generic.IReadOnlyList<(double x, double y, double z)> v, System.Collections.Generic.IReadOnlyList<(int a, int b, int c)> t)>
        {
            (Quad(0).v, Quad(0).t), (Quad(10).v, Quad(10).t), (Quad(5).v, Quad(5).t),   // 乱序输入
        };
        var solids = LayerSolid.MultiLayer(surfaces);
        Assert.Equal(2, solids.Count);
        Assert.All(solids, s => Assert.NotNull(s));
        // 顶→底两夹层体各水密, 高各 5(包围盒 z 跨度)
        double[][] expectZ = { new[] { 5.0, 10.0 }, new[] { 0.0, 5.0 } };   // 降序: (10,5) 再 (5,0)
        for (int i = 0; i < solids.Count; i++)
        {
            var (v, t) = solids[i]!.Value;
            var d = MeshDiagnose.Analyze(v, t);
            Assert.Equal(0, d.BoundaryEdges);            // 各夹层体水密
            var m = MeshMetrics.Compute(v, t);
            Assert.Equal(expectZ[i][0], m.MinZ, 6);
            Assert.Equal(expectZ[i][1], m.MaxZ, 6);
        }
    }

    [Fact]
    public void Degenerate_inputs_return_null_or_empty()
    {
        Assert.Null(LayerSolid.FromSurfaces(
            new List<(double, double, double)>(), new List<(int, int, int)>(),
            Quad(0).v, Quad(0).t));                                          // 顶面无三角
        Assert.Empty(LayerSolid.MultiLayer(
            new List<(System.Collections.Generic.IReadOnlyList<(double x, double y, double z)>, System.Collections.Generic.IReadOnlyList<(int a, int b, int c)>)>
            { (Quad(0).v, Quad(0).t) }));                                    // 单面 → 无夹层
    }
}
