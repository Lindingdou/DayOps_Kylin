using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using BEA = PitMine3D.Kylin.Cad.BenchElevationAnnotator;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 标注台阶标高 回归 —— 忠实移植原 BenchElevationAnnotator 放置算法的已知值验证:
/// 代表点 / 平盘居中(同高程异线中点) / 网格去重 / 高程标签符号 / 类别配色 / ▽符号几何。
/// </summary>
public class BenchElevationAnnotatorTests
{
    static BEA.BenchLine BL(string cat, params double[] xyz) => new() { Xyz = xyz, Category = cat };

    // 一条沿 Y∈[0,10] 的等高线(常 X,Z)。
    static BEA.BenchLine Line(double x, double z, string cat = "") => BL(cat, x, 0, z, x, 10, z);

    [Fact]
    public void Crest_and_toe_same_elevation_center_and_dedup_to_one()
    {
        // 坡顶(X=0,Z=100)与坡底(X=10,Z=100)同高程: 各自与对向边取中点 → 都落 X=5 →
        // 同格同高程去重 → 1 处标注; 两次居中计数。
        var r = BEA.Build(new List<BEA.BenchLine> { Line(0, 100), Line(10, 100) });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, r.MarkerCount);
        Assert.Equal(2, r.CenteredCount);
        Assert.Single(r.Markers);
        Assert.Equal(5, r.Markers[0].X, 3);
        Assert.Equal(100, r.Markers[0].Elevation);
        Assert.Equal("+100", r.Markers[0].Label);
    }

    [Fact]
    public void No_centering_keeps_two_separate_markers()
    {
        var opt = new BEA.Options { PlaceOnBenchCenter = false, SymbolSize = 1 };
        var r = BEA.Build(new List<BEA.BenchLine> { Line(0, 100), Line(10, 100) }, opt);
        Assert.Equal(2, r.MarkerCount);
        Assert.Equal(0, r.CenteredCount);
    }

    [Fact]
    public void Dedup_grid_merges_near_same_elevation_but_keeps_far_apart()
    {
        var opt = new BEA.Options { PlaceOnBenchCenter = false, SymbolSize = 1 };  // cell=3
        // 近(X=0,1)同格同高程 → 并 1; 远(X=100)另格 → 独立。
        var near = BEA.Build(new List<BEA.BenchLine> { Line(0, 100), Line(1, 100) }, opt);
        Assert.Equal(1, near.MarkerCount);
        var far = BEA.Build(new List<BEA.BenchLine> { Line(0, 100), Line(100, 100) }, opt);
        Assert.Equal(2, far.MarkerCount);
    }

    [Fact]
    public void Elevation_label_sign()
    {
        var opt = new BEA.Options { PlaceOnBenchCenter = false, SymbolSize = 1 };
        Assert.Equal("+100", BEA.Build(new List<BEA.BenchLine> { Line(0, 100) }, opt).Markers[0].Label);
        Assert.Equal("-25", BEA.Build(new List<BEA.BenchLine> { Line(0, -25) }, opt).Markers[0].Label);
        Assert.Equal("0", BEA.Build(new List<BEA.BenchLine> { Line(0, 0) }, opt).Markers[0].Label);
    }

    [Fact]
    public void Category_and_fixed_color()
    {
        var opt = new BEA.Options { PlaceOnBenchCenter = false, SymbolSize = 1 };
        var pit = BEA.Build(new List<BEA.BenchLine> { Line(0, 100, "pit") }, opt).Markers[0];
        Assert.Equal(0xD8, pit.R); Assert.Equal(0x5A, pit.G); Assert.Equal(0x30, pit.B);   // 采场橙
        var ext = BEA.Build(new List<BEA.BenchLine> { Line(0, 100, "external_dump") }, opt).Markers[0];
        Assert.Equal(0x2E, ext.R); Assert.Equal(0x6F, ext.G); Assert.Equal(0xCF, ext.B);   // 外排蓝
        var fixedOpt = new BEA.Options { PlaceOnBenchCenter = false, SymbolSize = 1, FixedColorRgb = 0x123456 };
        var fx = BEA.Build(new List<BEA.BenchLine> { Line(0, 100, "pit") }, fixedOpt).Markers[0];
        Assert.Equal(0x12, fx.R); Assert.Equal(0x34, fx.G); Assert.Equal(0x56, fx.B);      // 固定色压过类别
    }

    [Fact]
    public void Representative_point_is_vertex_nearest_centroid()
    {
        // 折线 (0,0)-(0,10)-(20,10): 质心 ≈(6.67,6.67); 最近顶点 = (0,10)。无居中/单线。
        var opt = new BEA.Options { PlaceOnBenchCenter = false, SymbolSize = 1 };
        var r = BEA.Build(new List<BEA.BenchLine> { BL("", 0, 0, 50, 0, 10, 50, 20, 10, 50) }, opt);
        Assert.Single(r.Markers);
        Assert.Equal(0, r.Markers[0].X, 3);
        Assert.Equal(10, r.Markers[0].Y, 3);
    }

    [Fact]
    public void Empty_and_invalid_return_not_ok()
    {
        Assert.False(BEA.Build(null).Ok);
        Assert.False(BEA.Build(new List<BEA.BenchLine>()).Ok);
        Assert.False(BEA.Build(new List<BEA.BenchLine> { new() { Xyz = System.Array.Empty<double>() } }).Ok);
    }

    [Fact]
    public void Triangle_leader_text_geometry_known_values()
    {
        var tri = BEA.TriangleXY(0, 0, 10);
        Assert.Equal((0.0, 0.0), tri[0]);
        Assert.Equal(-4.5, tri[1].x, 3); Assert.Equal(8.5, tri[1].y, 3);
        Assert.Equal(4.5, tri[2].x, 3); Assert.Equal(8.5, tri[2].y, 3);
        var (x0, y0, x1, y1) = BEA.LeaderXY(0, 0, 10, 4);
        Assert.Equal(4.5, x0, 3); Assert.Equal(8.5, y0, 3);
        Assert.True(x1 > x0);                          // 引线沿 +X
        var (tx, ty) = BEA.TextAnchorXY(0, 0, 10);
        Assert.Equal(6.0, tx, 3);                      // triHalf(4.5) + size*0.15(1.5)
        Assert.Equal(10.3, ty, 3);                     // triH(8.5) + size*0.18(1.8)
    }
}
