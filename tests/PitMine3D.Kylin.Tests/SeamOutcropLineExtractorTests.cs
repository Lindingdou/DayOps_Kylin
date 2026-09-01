using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>煤层露头线抽取回归 —— 现状面∩顶/底板等值线(marching triangles): 坡顶=顶板露头, 坡底=底板露头。</summary>
public class SeamOutcropLineExtractorTests
{
    // 现状面: 沿 x 倾斜平面 z=10−0.5x（x=0→z10, x=20→z0）, 覆盖 x∈{0,8,12,20}×y∈{0,20}
    static (double[] v, int[] t) TiltedSurface()
    {
        double Z(double x) => 10 - 0.5 * x;
        var v = new double[] {
            0,0,Z(0), 8,0,Z(8), 12,0,Z(12), 20,0,Z(20),
            0,20,Z(0), 8,20,Z(8), 12,20,Z(12), 20,20,Z(20) };
        var t = new int[] { 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6 };
        return (v, t);
    }

    static SeamOutcropLineExtractor.SampleZ Flat(double z)
        => (double x, double y, out double zz) => { zz = z; return true; };

    [Fact]
    public void Tilted_surface_crossing_flat_seam_yields_crest_at_roof_and_toe_at_floor()
    {
        // 平煤层 顶板 z=5 / 底板 z=2。面 z=10−0.5x 交顶板于 x=10(坡顶), 交底板于 x=16(坡底)。
        var (v, t) = TiltedSurface();
        var r = SeamOutcropLineExtractor.Extract(v, t, Flat(5), Flat(2),
            new SeamOutcropLineExtractor.Options { MinLengthM = 1 });
        Assert.True(r.Ok, r.Message);
        Assert.Single(r.CrestLines);
        Assert.Single(r.ToeLines);

        var crest = r.CrestLines[0];
        for (int i = 0; i < crest.PointCount; i++)
        {
            Assert.Equal(10.0, crest.Xyz[i * 3], 6);         // 顶板露头在 x=10
            Assert.Equal(5.0, crest.Xyz[i * 3 + 2], 6);      // 该处现状 z = 顶板 z = 5
        }
        Assert.Equal(20.0, crest.PlanLengthM, 6);            // 沿 y 走满 20

        var toe = r.ToeLines[0];
        for (int i = 0; i < toe.PointCount; i++)
        {
            Assert.Equal(16.0, toe.Xyz[i * 3], 6);           // 底板露头在 x=16
            Assert.Equal(2.0, toe.Xyz[i * 3 + 2], 6);        // 该处现状 z = 底板 z = 2
        }
    }

    [Fact]
    public void Surface_entirely_above_seam_yields_no_outcrop()
    {
        // 现状面全在 z=10（平）, 煤层[2,5]整层在其下 → 面不与顶/底板相交 → 无露头线
        var v = new double[] { 0, 0, 10, 20, 0, 10, 0, 20, 10, 20, 20, 10 };
        var t = new int[] { 0, 1, 3, 0, 3, 2 };
        var r = SeamOutcropLineExtractor.Extract(v, t, Flat(5), Flat(2));
        Assert.False(r.Ok);
        Assert.Empty(r.CrestLines);
        Assert.Empty(r.ToeLines);
    }

    [Fact]
    public void No_data_vertices_are_skipped_not_fabricated()
    {
        // 底板采样器在 x>10 处无数据 → 含该顶点的三角跳过, 露头线在那里断开(不凭空造点)
        SeamOutcropLineExtractor.SampleZ floorPartial = (double x, double y, out double zz) =>
        { zz = 2; return x <= 10; };
        var (v, t) = TiltedSurface();
        var r = SeamOutcropLineExtractor.Extract(v, t, Flat(5), floorPartial,
            new SeamOutcropLineExtractor.Options { MinLengthM = 1 });
        Assert.Single(r.CrestLines);            // 顶板全覆盖 → 坡顶线照出
        Assert.Empty(r.ToeLines);               // 底板露头在 x=16 处顶点无数据 → 抽不到
        Assert.NotEmpty(r.Warnings);            // 报"采不到"
    }

    [Fact]
    public void Pair_into_bands_matches_parallel_crest_and_toe()
    {
        var (v, t) = TiltedSurface();
        var r = SeamOutcropLineExtractor.Extract(v, t, Flat(5), Flat(2),
            new SeamOutcropLineExtractor.Options { MinLengthM = 1 });
        // 坡顶 x=10、坡底 x=16 平行, 水平间距 6; 煤厚 3 → 合理区间 [1.73, 34.3] 内且平稳
        var bands = SeamOutcropLineExtractor.PairIntoBands(r.CrestLines, r.ToeLines, 3.0, out var notes);
        Assert.Single(bands);
        Assert.Equal(6.0, bands[0].MedianSpacingM, 3);
    }
}
