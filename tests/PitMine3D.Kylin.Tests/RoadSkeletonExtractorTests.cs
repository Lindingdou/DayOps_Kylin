using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 骨架法道路中心线 RoadSkeletonExtractor 已知值回归 —— 忠实原 PointCloudLib.RoadCenterline.RoadSkeletonExtractor。
/// 确定性子算法(ZhangSuen 细化 / DpSimplify DP 简化 / ComputeSlopeDeg 坡度 / DistanceTransformCells / RemoveSmallComponents)
/// 各以已知值钉住 + Extract 端到端(错误分支 / 简单矩形可行驶域 / 确定性)。
/// </summary>
public class RoadSkeletonExtractorTests
{
    // ── ZhangSuen ──
    [Fact]
    public void ZhangSuen_preserves_one_pixel_line()
    {
        // 7×3, y=1 行 x=1..5 已是 1px 线 → 细化不动（端点 b<2 跳过，内部不满足删除条件）。
        int w = 7, h = 3;
        var m = new bool[w * h];
        for (int x = 1; x <= 5; x++) m[1 * w + x] = true;
        RoadSkeletonExtractor.ZhangSuen(m, w, h);
        for (int x = 1; x <= 5; x++) Assert.True(m[1 * w + x], $"cell ({x},1) 应保留");
        Assert.Equal(5, m.Count(b => b));   // 无新增、无删除
    }

    [Fact]
    public void ZhangSuen_thins_thick_bar()
    {
        // 7×4, 两行(y=1,2) x=1..5 = 10 格 → 细化到更细(应 <10 且非空，连通保留)。
        int w = 7, h = 4;
        var m = new bool[w * h];
        for (int x = 1; x <= 5; x++) { m[1 * w + x] = true; m[2 * w + x] = true; }
        RoadSkeletonExtractor.ZhangSuen(m, w, h);
        int cnt = m.Count(b => b);
        Assert.True(cnt is > 0 and < 10, $"细化后 {cnt} 应在 (0,10)");
    }

    // ── DpSimplify ──
    [Fact]
    public void DpSimplify_drops_collinear_keeps_endpoints_and_Z()
    {
        var xs = new List<double> { 0, 1, 2, 3 };
        var ys = new List<double> { 0, 0, 0, 0 };
        var zs = new List<double> { 10, 20, 30, 40 };
        var r = RoadSkeletonExtractor.DpSimplify(xs, ys, zs, 0.5);
        Assert.Equal(6, r.Length);                                   // 2 点 × (x,y,z)
        Assert.Equal(new double[] { 0, 0, 10, 3, 0, 40 }, r);        // 只留端点，Z 随点保留
    }

    [Fact]
    public void DpSimplify_keeps_point_off_the_line()
    {
        var xs = new List<double> { 0, 1, 2 };
        var ys = new List<double> { 0, 5, 0 };   // 中点离基线 (0,0)-(2,0) 距 5 > tol
        var zs = new List<double> { 0, 0, 0 };
        var r = RoadSkeletonExtractor.DpSimplify(xs, ys, zs, 0.5);
        Assert.Equal(9, r.Length);               // 3 点全保留
        Assert.Equal(5, r[4], 6);                // 中点 y=5 在
    }

    // ── ComputeSlopeDeg ──
    [Fact]
    public void ComputeSlopeDeg_flat_is_zero_ramp_is_45deg()
    {
        // 平坦 → 0°。
        int w = 4, h = 3;
        var flat = new float[w * h];   // 全 0
        var s0 = RoadSkeletonExtractor.ComputeSlopeDeg(flat, w, h, 1f);
        Assert.All(s0, v => Assert.Equal(0, v, 4));

        // z=x 斜面, cs=1 → 内部中心差商 gx=1 → atan(1)=45°。
        var ramp = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) ramp[y * w + x] = x;
        var s1 = RoadSkeletonExtractor.ComputeSlopeDeg(ramp, w, h, 1f);
        Assert.Equal(45, s1[1 * w + 1], 3);   // 内部格 (1,1)
        Assert.Equal(45, s1[1 * w + 2], 3);   // 内部格 (2,1)
    }

    // ── DistanceTransformCells ──
    [Fact]
    public void DistanceTransform_center_cell_distance_is_one()
    {
        // 3×3, 仅中心可行驶 → 到最近非行驶格(相邻)距离=1；非行驶格 dt=0。
        int w = 3, h = 3;
        var mask = new bool[w * h];
        mask[1 * w + 1] = true;
        var dt = RoadSkeletonExtractor.DistanceTransformCells(mask, w, h);
        Assert.Equal(1f, dt[1 * w + 1], 4);
        Assert.Equal(0f, dt[0], 4);
    }

    // ── RemoveSmallComponents ──
    [Fact]
    public void RemoveSmallComponents_drops_small_keeps_large()
    {
        // 10×3：左 3×3 块(9 格) + 右孤立 1 格；minCells=5 → 删小留大。
        int w = 10, h = 3;
        var mask = new bool[w * h];
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) mask[y * w + x] = true;  // 9 格块
        mask[1 * w + 8] = true;                                                          // 孤立 1 格
        RoadSkeletonExtractor.RemoveSmallComponents(mask, w, h, minCells: 5);
        Assert.True(mask[1 * w + 1]);          // 大块留
        Assert.False(mask[1 * w + 8]);         // 小块删
        Assert.Equal(9, mask.Count(b => b));
    }

    // ── Extract 端到端 ──
    [Fact]
    public void Extract_rejects_empty_and_degenerate_input()
    {
        var opt = new RoadSkeletonOptions();
        var r0 = RoadSkeletonExtractor.Extract(new List<double[]>(), opt);
        Assert.False(r0.Ok);
        Assert.Contains("台阶线", r0.Error);

        // 单点(退化包围盒)。
        var r1 = RoadSkeletonExtractor.Extract(new List<double[]> { new double[] { 5, 5, 0 } }, opt);
        Assert.False(r1.Ok);
    }

    static List<double[]> RectangleBenchLines(double wm, double hm)
        => new()
        {
            new double[] { 0, 0, 0, wm, 0, 0 },      // 下
            new double[] { 0, hm, 0, wm, hm, 0 },    // 上
            new double[] { 0, 0, 0, 0, hm, 0 },      // 左
            new double[] { wm, 0, 0, wm, hm, 0 },    // 右
        };

    static RoadSkeletonOptions FlatTestOptions() => new()
    {
        CellSize = 2.0,
        TrunkOnly = false,       // 跳过干线路由(测试域小于 MinRouteComponentCells)
        UseBenchBarrier = false, // 不用挡墙挖除，直接骨架化整片可行驶域
        BridgeGapMeters = 0,     // 跳过桥接
        MinComponentCells = 5,
        MinLineLen = 5,
    };

    [Fact]
    public void Extract_flat_rectangle_yields_a_centerline()
    {
        var r = RoadSkeletonExtractor.Extract(RectangleBenchLines(60, 20), FlatTestOptions());
        Assert.True(r.Ok, r.Error);
        Assert.Equal(31, r.DemW);          // 60/2+1
        Assert.Equal(11, r.DemH);          // 20/2+1
        Assert.True(r.MaskCells > 0);
        Assert.True(r.Centerlines.Count >= 1, "平坦矩形应出至少一条中心线");
        // 每条中心线是扁平 [x,y,z,...]，≥2 点。
        Assert.All(r.Centerlines, cl => Assert.True(cl.Length >= 6 && cl.Length % 3 == 0));
    }

    [Fact]
    public void Extract_is_deterministic()
    {
        var a = RoadSkeletonExtractor.Extract(RectangleBenchLines(60, 20), FlatTestOptions());
        var b = RoadSkeletonExtractor.Extract(RectangleBenchLines(60, 20), FlatTestOptions());
        Assert.Equal(a.Centerlines.Count, b.Centerlines.Count);
        Assert.Equal(a.SkeletonCells, b.SkeletonCells);
        for (int i = 0; i < a.Centerlines.Count; i++)
            Assert.Equal(a.Centerlines[i], b.Centerlines[i]);
    }
}
