using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 道路中心线提取（单一台阶线池·同高程配对）算法验证 —— 忠实移植原 RoadCenterlineExtractor 的已知值：
/// 同高程对→出一条居中中线；立面对/超距/过近→剔除；收窄→断段；闭合环单线→不自配对；碎段→拼接；两级平盘→两线。
/// </summary>
public class RoadCenterlineExtractorTests
{
    private static RoadCenterlineOptions Opt() => new RoadCenterlineOptions
    {
        MinRoadWidth = 15.0,
        MaxPairDist = 60.0,
        ZTolerance = 3.0,
        SampleStep = 3.0,
        MinLineLen = 30.0,
        MaxNormalAngleDeg = 40.0,
        SimplifyTol = 1.0,
    };

    // 沿 X 走、Y/Z 恒定的直线台阶线（[x,y,z,...]）。
    private static double[] LineAlongX(double x0, double x1, double y, double z)
        => new[] { x0, y, z, x1, y, z };

    [Fact]
    public void SameElevationPair_ProducesOneCenteredLine()
    {
        var a = LineAlongX(0, 200, 0, 100);
        var b = LineAlongX(0, 200, 30, 100);   // 同高程、相距 30∈[15,60]
        var res = RoadCenterlineExtractor.Extract(new[] { a, b }, Opt());

        Assert.Single(res.Centerlines);        // 两条边各采一次 → 去重为 1 条
        var cl = res.Centerlines[0];
        int n = cl.Length / 3;
        Assert.True(n >= 2);
        for (int i = 0; i < n; i++)
        {
            Assert.InRange(cl[3 * i + 1], 14.0, 16.0);   // Y 居中 ≈15
            Assert.InRange(cl[3 * i + 2], 99.0, 101.0);  // Z ≈100
        }
        Assert.True(res.TotalLengthM > 150);             // 长度接近 200
    }

    [Fact]
    public void DifferentElevation_RiserPair_IsRejected()
    {
        var a = LineAlongX(0, 200, 0, 100);
        var b = LineAlongX(0, 200, 30, 80);    // 横距合规但高差 20 > Δz=3 → 立面对，剔除
        var res = RoadCenterlineExtractor.Extract(new[] { a, b }, Opt());
        Assert.Empty(res.Centerlines);
    }

    [Fact]
    public void TooFar_BeyondWmax_IsRejected()
    {
        var a = LineAlongX(0, 200, 0, 100);
        var b = LineAlongX(0, 200, 80, 100);   // 同高程但相距 80 > W_max=60
        var res = RoadCenterlineExtractor.Extract(new[] { a, b }, Opt());
        Assert.Empty(res.Centerlines);
    }

    [Fact]
    public void TooNarrow_BelowWmin_IsRejected()
    {
        var a = LineAlongX(0, 200, 0, 100);
        var b = LineAlongX(0, 200, 10, 100);   // 同高程但相距 10 < W_min=15
        var res = RoadCenterlineExtractor.Extract(new[] { a, b }, Opt());
        Assert.Empty(res.Centerlines);
    }

    [Fact]
    public void SingleClosedRing_DoesNotPairWithItself()
    {
        // 一条闭合环（两长边相距 40，落在 [15,60] 内）：同一条线 → 自配对被跳过 → 0 条。
        var ring = new[] { 0.0, 0, 100, 200, 0, 100, 200, 40, 100, 0, 40, 100, 0, 0, 100 };
        var res = RoadCenterlineExtractor.Extract(new[] { ring }, Opt());
        Assert.Empty(res.Centerlines);
    }

    [Fact]
    public void Pinch_SplitsCenterlineIntoTwoSegments()
    {
        // 上边直线 Y=0；下边两端宽(Y≈40)、中段持续收窄(Y≈8<W_min, X∈[160,240]) →
        // 该段无合规对向点，中线在收窄处断成两段。
        var top = LineAlongX(0, 400, 0, 100);
        var bottom = new[]
        {
            0.0,  40, 100,
            140.0, 40, 100,
            160.0,  8, 100,   // 持续收窄到 8 < W_min=15
            240.0,  8, 100,
            260.0, 40, 100,
            400.0, 40, 100,
        };
        var res = RoadCenterlineExtractor.Extract(new[] { top, bottom }, Opt());
        Assert.Equal(2, res.Centerlines.Count);
    }

    [Fact]
    public void FragmentedBenchLine_StitchedIntoOneContinuousCenterline()
    {
        // 上边界裂成两段(端点 (100,0) 相接)，下边界一整条；拼接后应出≈1 条连续中线，而非碎段。
        var topA = LineAlongX(0, 100, 0, 100);
        var topB = LineAlongX(100, 200, 0, 100);
        var bottom = LineAlongX(0, 200, 30, 100);
        var res = RoadCenterlineExtractor.Extract(new[] { topA, topB, bottom }, Opt());
        Assert.Single(res.Centerlines);
        Assert.True(res.TotalLengthM > 150, $"应≈200m 连续，实际 {res.TotalLengthM:F0}");
    }

    [Fact]
    public void TwoSeparatePlatforms_ProduceTwoCenterlines()
    {
        // 两级平盘（高程不同），各由一对同高程线界定 → 两条互不重合的中心线。
        var p1a = LineAlongX(0, 200, 0, 100);
        var p1b = LineAlongX(0, 200, 30, 100);   // 平盘1 @Z=100
        var p2a = LineAlongX(0, 200, 0, 80);
        var p2b = LineAlongX(0, 200, 30, 80);    // 平盘2 @Z=80
        var res = RoadCenterlineExtractor.Extract(new[] { p1a, p1b, p2a, p2b }, Opt());
        Assert.Equal(2, res.Centerlines.Count);
    }
}
