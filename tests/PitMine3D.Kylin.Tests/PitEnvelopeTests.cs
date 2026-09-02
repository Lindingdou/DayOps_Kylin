using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 几何圈定 PitEnvelope 已知值回归 —— 忠实移植原 PlanLib.BoundaryOptimization.PitEnvelope 纯几何核:
/// 顶口足迹 + 逐帮放坡内缩 drop/tanβ + 方位映射帮 + 几何深度封顶。方形顶口 + 均一/分帮 β 解析可算。
/// </summary>
public class PitEnvelopeTests
{
    static TopOutline Square() => TopOutline.FromBounds(0, 0, 100, 100, 50);

    [Fact]
    public void FromBounds_builds_ccw_rectangle_with_centroid()
    {
        var t = Square();
        Assert.Equal(4, t.Count);
        Assert.Equal(50, t.Cx, 6); Assert.Equal(50, t.Cy, 6); Assert.Equal(50, t.Zsurface, 6);
        Assert.Equal(10000.0, PitEnvelope.PolygonArea(t.X.ToArray(), t.Y.ToArray()), 6);   // 100×100
    }

    [Fact]
    public void InsetPerWall_uniform_beta45_shrinks_by_drop_each_edge()
    {
        var t = Square();
        var betas = Enumerable.Repeat(45.0, 4).ToArray();   // tan45=1 → inset = drop
        var (bx, by) = PitEnvelope.InsetPerWall(t, 10, betas);
        // 各边内缩 10 → 底 (10,10)-(90,90) 80×80。
        Assert.Equal(6400.0, PitEnvelope.PolygonArea(bx, by), 4);
        Assert.Equal(10.0, bx.Min(), 4); Assert.Equal(90.0, bx.Max(), 4);
        Assert.Equal(10.0, by.Min(), 4); Assert.Equal(90.0, by.Max(), 4);
    }

    [Fact]
    public void GeometricDepthCap_square_minbottom20_beta45_is_40()
    {
        var t = Square();
        var betas = Enumerable.Repeat(45.0, 4).ToArray();
        // 半宽50 − 底半宽10 = 40 展距; ×tan45 = 40m 深封顶。
        Assert.Equal(40.0, PitEnvelope.GeometricDepthCapPerWall(t, 20, betas), 4);
        // 到封顶深度内缩后底宽恰 20。
        var (bx, by) = PitEnvelope.InsetPerWall(t, 40, betas);
        Assert.Equal(20.0, bx.Max() - bx.Min(), 3);
        Assert.Equal(400.0, PitEnvelope.PolygonArea(bx, by), 2);   // 20×20
    }

    [Fact]
    public void BetaForAzimuth_matches_side_by_direction_else_average()
    {
        var walls = new[] { new WallAngle("东帮", 60), new WallAngle("西帮", 50) };
        Assert.Equal(60, PitEnvelope.BetaForAzimuth(walls, 0), 6);     // 东(E)
        Assert.Equal(50, PitEnvelope.BetaForAzimuth(walls, 180), 6);   // 西(W)
        Assert.Equal(55, PitEnvelope.BetaForAzimuth(walls, 90), 6);    // 北(N) 无匹配 → 平均(60+50)/2
        Assert.Equal(40.0, PitEnvelope.BetaForAzimuth(System.Array.Empty<WallAngle>(), 0), 6);   // 空 → 40 兜底
    }

    [Fact]
    public void EdgeBetas_assigns_per_wall_by_edge_azimuth()
    {
        var t = Square();
        // 方形四边外法向: 南(y=0)/东(x=100)/北(y=100)/西(x=0)。
        var walls = new[] { new WallAngle("东帮", 60), new WallAngle("西帮", 50), new WallAngle("南帮", 40), new WallAngle("北帮", 45) };
        var b = PitEnvelope.EdgeBetas(t, walls);
        Assert.Equal(40, b[0], 6);   // 底边 y=0 → 南
        Assert.Equal(60, b[1], 6);   // 右边 x=100 → 东
        Assert.Equal(45, b[2], 6);   // 顶边 y=100 → 北
        Assert.Equal(50, b[3], 6);   // 左边 x=0 → 西
    }

    [Fact]
    public void MaterializeRings_structure_bench_count_and_descending_z()
    {
        var t = Square();
        var betas = Enumerable.Repeat(45.0, 4).ToArray();
        // H=10, depth=40 → n=4 台阶; 环 = crest_0 + 4×toe + 3×crest = 8。
        var rings = PitEnvelope.MaterializeRings(t, betas, benchH: 10, faceAngleDeg: 70, depth: 40);
        Assert.Equal(8, rings.Count);
        Assert.Equal(4, rings.Count(r => !r.Crest));   // 4 坡底(toe)=台阶数
        Assert.Equal(4, rings.Count(r => r.Crest));    // crest_0 + 3
        Assert.True(rings[0].Crest);                   // 顶口是 crest
        // Z 自顶向下: 50, 40,40, 30,30, 20,20, 10。
        Assert.Equal(new[] { 50.0, 40, 40, 30, 30, 20, 20, 10 }, rings.Select(r => r.Z).ToArray());
    }

    [Fact]
    public void MaterializeRings_shrink_inward_monotonic()
    {
        var t = Square();
        var betas = Enumerable.Repeat(45.0, 4).ToArray();
        var rings = PitEnvelope.MaterializeRings(t, betas, benchH: 10, faceAngleDeg: 70, depth: 40);
        double prev = double.MaxValue;
        foreach (var r in rings)
        {
            double area = PitEnvelope.PolygonArea(r.X, r.Y);
            Assert.True(area <= prev + 1e-6);   // 逐环向内收缩(面积单调不增)
            prev = area;
        }
        Assert.True(PitEnvelope.PolygonArea(rings[^1].X, rings[^1].Y) < 10000);   // 坑底 < 顶口
    }

    [Fact]
    public void LoftMesh_vertex_and_triangle_counts_match_rings()
    {
        var t = Square();
        var betas = Enumerable.Repeat(45.0, 4).ToArray();
        var rings = PitEnvelope.MaterializeRings(t, betas, benchH: 10, faceAngleDeg: 70, depth: 40);
        var (verts, tris) = PitEnvelope.LoftMesh(rings);
        Assert.Equal(rings.Count * 4, verts.Count);          // 8 环 × 4 顶点/环 = 32
        Assert.Equal((rings.Count - 1) * 4 * 2, tris.Count);  // 7 条带 × 4 边 × 2 三角 = 56
    }

    [Fact]
    public void MaterializeRings_degenerate_stops_before_full_depth()
    {
        var t = TopOutline.FromBounds(0, 0, 30, 30, 50);     // 小足迹 30×30
        var betas = Enumerable.Repeat(45.0, 4).ToArray();
        // depth=200 名义 20 台阶, 但环缩到短边<5m 即止 → 远少于 2×20=40 环。
        var rings = PitEnvelope.MaterializeRings(t, betas, benchH: 10, faceAngleDeg: 70, depth: 200);
        Assert.True(rings.Count < 8, $"应提前退化止, 实际 {rings.Count} 环");
        Assert.True(rings.Count >= 2);
    }

    [Fact]
    public void Per_wall_slopes_give_asymmetric_bottom()
    {
        var t = Square();
        // 东西缓(β 大→内缩小)、南北陡(β 小→内缩大); 各边内缩 = 10/tanβ。
        var betas = PitEnvelope.EdgeBetas(t, new[]
        {
            new WallAngle("东帮", 45), new WallAngle("西帮", 45),   // tan45=1 → 内缩10
            new WallAngle("南帮", 63.4349), new WallAngle("北帮", 63.4349)   // tan=2 → 内缩5
        });
        var (bx, by) = PitEnvelope.InsetPerWall(t, 10, betas);
        // 东西(x 向)内缩 10 → x∈[10,90]; 南北(y 向)内缩 5 → y∈[5,95]。
        Assert.Equal(10.0, bx.Min(), 3); Assert.Equal(90.0, bx.Max(), 3);
        Assert.Equal(5.0, by.Min(), 3); Assert.Equal(95.0, by.Max(), 3);
    }
}
