using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>台阶坡面提取回归 —— 现状面按坡度分陡/缓, 连通域分片, 每片台阶高/坡度/面积 + 坡顶/坡底线。</summary>
public class BenchFaceExtractorTests
{
    // 单台阶: 顶平台(z=10) + 陡坡面(x 20→25, z 10→0, 坡≈63°) + 底平台(z=0)。6 三角。
    static (double[] v, int[] t) OneBench() => (
        new double[] { 0, 0, 10, 20, 0, 10, 25, 0, 0, 45, 0, 0, 0, 40, 10, 20, 40, 10, 25, 40, 0, 45, 40, 0 },
        new int[] { 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6 });

    [Fact]
    public void One_bench_extracts_single_face_with_correct_metrics()
    {
        var (v, t) = OneBench();
        var r = BenchFaceExtractor.Extract(v, t, new BenchFaceExtractor.Options { MinSlopeDeg = 20 });
        Assert.True(r.Ok, r.Message);
        Assert.Single(r.Faces);
        Assert.Equal(2, r.SteepTris);                     // 仅坡面 2 三角为陡
        var f = r.Faces[0];
        Assert.Equal(10.0, f.BenchHeightM, 6);            // z 10→0
        Assert.Equal(63.43, f.MeanSlopeDeg, 1);           // atan(10/5)=63.43°(cosα=投影200/真实447)
        Assert.Equal(5.0, f.FaceRunM, 3);                 // 台阶高10 / tan63.43 = 5m 水平投影
    }

    [Fact]
    public void Crest_is_the_upper_edge_toe_is_the_lower_edge()
    {
        var (v, t) = OneBench();
        var r = BenchFaceExtractor.Extract(v, t, new BenchFaceExtractor.Options { MinSlopeDeg = 20 });
        var f = r.Faces[0];
        Assert.True(f.CrestPointCount >= 2 && f.ToePointCount >= 2);
        // 坡顶线全在高处(z=10, x=20); 坡底线全在低处(z=0, x=25)
        for (int i = 0; i < f.CrestPointCount; i++)
        {
            Assert.Equal(10.0, f.CrestXyz[i * 3 + 2], 6);
            Assert.Equal(20.0, f.CrestXyz[i * 3], 6);
        }
        for (int i = 0; i < f.ToePointCount; i++)
        {
            Assert.Equal(0.0, f.ToeXyz[i * 3 + 2], 6);
            Assert.Equal(25.0, f.ToeXyz[i * 3], 6);
        }
    }

    [Fact]
    public void Flat_surface_yields_no_faces()
    {
        // 全 z=0 平面 → 无陡三角
        var v = new double[] { 0, 0, 0, 20, 0, 0, 0, 40, 0, 20, 40, 0 };
        var t = new int[] { 0, 1, 3, 0, 3, 2 };
        var r = BenchFaceExtractor.Extract(v, t, new BenchFaceExtractor.Options { MinSlopeDeg = 20 });
        Assert.False(r.Ok);
        Assert.Equal(0, r.SteepTris);
        Assert.Empty(r.Faces);
    }

    [Fact]
    public void Face_below_min_area_is_dropped()
    {
        var (v, t) = OneBench();
        // 坡面真实面积≈447 m² → 下限 1000 会把它丢掉
        var r = BenchFaceExtractor.Extract(v, t, new BenchFaceExtractor.Options { MinSlopeDeg = 20, MinAreaM2 = 1000 });
        Assert.Empty(r.Faces);
        Assert.True(r.DroppedSmall >= 1);
    }

    [Fact]
    public void Face_below_min_bench_height_is_dropped()
    {
        var (v, t) = OneBench();
        // 台阶高 10m → 下限 20m 会把它丢掉
        var r = BenchFaceExtractor.Extract(v, t, new BenchFaceExtractor.Options { MinSlopeDeg = 20, MinBenchHeightM = 20 });
        Assert.Empty(r.Faces);
        Assert.True(r.DroppedLow >= 1);
    }

    [Fact]
    public void Clip_ring_excludes_faces_outside_pit()
    {
        var (v, t) = OneBench();
        // 采场环只圈底平台一侧(x 30~50), 坡面质心 x≈22 落在环外 → 不计
        var ring = new double[] { 30, -5, 50, -5, 50, 45, 30, 45 };
        var r = BenchFaceExtractor.Extract(v, t, new BenchFaceExtractor.Options { MinSlopeDeg = 20, ClipRingXy = ring });
        Assert.Equal(0, r.SteepTris);
        Assert.False(r.Ok);
    }
}
