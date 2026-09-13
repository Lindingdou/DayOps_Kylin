using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 「提取道路中心线」编排（RoadCenterlineRunner，忠实原 PointCloudLib.RoadCenterline.RoadCenterlineRunner.RunAsync）：
/// 台阶线不足 / 2D 无高程守卫 / 区域预筛与精裁 / TIN 与块状面口径 / 连通增强与手动补充线并入。
/// 骨架算法本身的已知值回归在 RoadSkeletonExtractorTests，这里只钉编排语义。
/// </summary>
public class RoadCenterlineRunnerTests
{
    // 一条 100×20 的缓坡平盘走廊（左 z=0 → 右 z=2，坡 ≈1.1° ≪ 14°），四面被 z=30 的高台阶封住：
    // 台阶面 28m 高差落在 10m 内 → 坡度 ≫ 14° 被掩膜剔掉，可行驶域只剩走廊本身，且不贴 DEM 边界
    // （贴边的实心块 Zhang-Suen 削不动 —— 那是退化输入不是算法问题）。骨架应是沿 y≈10 的一根中轴。
    static List<double[]> SlopedRectangle() => new()
    {
        new double[] { 0, 0, 0, 100, 0, 2 },            // 平盘坡底线
        new double[] { 0, 20, 0, 100, 20, 2 },          // 平盘坡顶线
        new double[] { -10, -10, 30, 110, -10, 30 },    // 外侧高台阶(下)
        new double[] { -10, 30, 30, 110, 30, 30 },      // 外侧高台阶(上)
        new double[] { -10, -10, 30, -10, 30, 30 },     // 左封
        new double[] { 110, -10, 30, 110, 30, 30 },     // 右封
    };

    static RoadCenterlineRunOptions Opts(bool connect = true) => new()
    {
        Skeleton = new RoadSkeletonOptions
        {
            CellSize = 2.0,
            TrunkOnly = false,       // 测试域小于 MinRouteComponentCells，跳过干线路由
            UseBenchBarrier = false, // 不挖挡墙，直接骨架化整片可行驶域
            BridgeGapMeters = 0,
            MinComponentCells = 5,
            MinLineLen = 5,
            MinRoadWidth = 4,
        },
        EnableConnect = connect,
    };

    [Fact]
    public void Run_rejects_fewer_than_two_bench_lines()
    {
        var r = RoadCenterlineRunner.Run(new List<double[]> { new double[] { 0, 0, 0, 10, 0, 5 } }, new List<double[]>(), null, null, Opts());
        Assert.False(r.Ok);
        Assert.Empty(r.Lines);
        Assert.Contains(r.Notes, n => n.Level == RoadCenterlineSeverity.Warn && n.Text.Contains("未找到足够多段线作为台阶线"));
    }

    [Fact]
    public void Run_rejects_flat_2d_lines()
    {
        // 全部 z=0：原版守卫 —— 没有真实高程分不出平盘/立面，直接提示，不去跑骨架。
        var flat = SlopedRectangle().Select(l => { var c = (double[])l.Clone(); for (int i = 2; i < c.Length; i += 3) c[i] = 0; return c; }).ToList();
        var r = RoadCenterlineRunner.Run(flat, new List<double[]>(), null, null, Opts());
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Level == RoadCenterlineSeverity.Warn && n.Text.Contains("2D 无高程"));
        Assert.DoesNotContain(r.Notes, n => n.Text.Contains("计算中"));
    }

    [Fact]
    public void Run_end_to_end_yields_lines_with_block_face_when_no_tin()
    {
        var r = RoadCenterlineRunner.Run(SlopedRectangle(), new List<double[]>(), null, null, Opts());
        Assert.True(r.Ok, string.Join(" | ", r.Notes.Select(n => n.Text)));
        var l = Assert.Single(r.Lines);                      // 一条走廊 = 一根中轴(无同心"花圈")
        Assert.True(l.Length >= 6 && l.Length % 3 == 0);
        Assert.InRange(l[1], 8, 12);                          // 中轴落在走廊中线 y≈10 附近
        Assert.True(l[l.Length - 1] > l[2], "Z 应沿走向抬升(左低右高)");
        Assert.Contains("块状面", r.FaceSource);
        Assert.Contains(r.Notes, n => n.Text.Contains("台阶线 6 条") && n.Text.Contains("块状面"));
        Assert.Equal(0, r.ManualCount);
    }

    [Fact]
    public void Run_reports_tin_face_when_mesh_given()
    {
        // 同一缓坡面的两三角 TIN：口径应写成 TIN插值面(4点)。
        var verts = new double[] { 0, 0, 0, 100, 0, 2, 100, 20, 2, 0, 20, 0 };
        var tris = new[] { 0, 1, 2, 0, 2, 3 };
        var r = RoadCenterlineRunner.Run(SlopedRectangle(), new List<double[]>(), verts, tris, Opts());
        Assert.True(r.Ok, string.Join(" | ", r.Notes.Select(n => n.Text)));
        Assert.Equal("TIN插值面(4点)", r.FaceSource);
    }

    [Fact]
    public void Run_clip_region_far_away_warns_and_stops()
    {
        var o = Opts();
        o.ClipRegions = new[] { new RoadClipRegion { Name = "远处", RingXy = new double[] { 1000, 1000, 1100, 1000, 1100, 1100, 1000, 1100 } } };
        var r = RoadCenterlineRunner.Run(SlopedRectangle(), new List<double[]>(), null, null, o);
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Text.Contains("按 1 块区域裁剪（远处）") && n.Text.Contains("台阶线 6 → 0 条"));
        Assert.Contains(r.Notes, n => n.Level == RoadCenterlineSeverity.Warn && n.Text.Contains("所选作业区域内的台阶线不足"));
    }

    [Fact]
    public void Run_clip_region_covering_keeps_lines_and_names_region()
    {
        var o = Opts();
        o.ClipRegions = new[] { new RoadClipRegion { Name = "采场A", Active = false, RingXy = new double[] { -5, -5, 105, -5, 105, 25, -5, 25 } } };
        var r = RoadCenterlineRunner.Run(SlopedRectangle(), new List<double[]>(), null, null, o);
        Assert.True(r.Ok, string.Join(" | ", r.Notes.Select(n => n.Text)));
        Assert.NotEmpty(r.Lines);
        // 本期未选定的区域临时用：回显要点名 + 说明台账未改。
        Assert.Contains(r.Notes, n => n.Text.Contains("采场A") && n.Text.Contains("1 块是本期未选定的"));
        Assert.Contains(r.Notes, n => n.Text.StartsWith("作业区域裁剪：中心线"));
        // 精裁后每个顶点都在区域内。
        foreach (var l in r.Lines)
            for (int i = 0; i + 2 < l.Length; i += 3)
                Assert.True(RegionClip.PointInPolygon(l[i], l[i + 1], o.ClipRegions[0].RingXy), $"({l[i]},{l[i + 1]}) 应在区域内");
    }

    [Fact]
    public void Run_without_connect_appends_manual_lines_as_is()
    {
        var manual = new List<double[]> { new double[] { 100, 100, 5, 130, 100, 5 } };
        var r0 = RoadCenterlineRunner.Run(SlopedRectangle(), new List<double[]>(), null, null, Opts(connect: false));
        var r1 = RoadCenterlineRunner.Run(SlopedRectangle(), manual, null, null, Opts(connect: false));
        Assert.True(r1.Ok);
        Assert.Equal(r0.Lines.Count + 1, r1.Lines.Count);   // 仅并入不连通
        Assert.Equal(1, r1.ManualCount);
        Assert.Same(manual[0], r1.Lines[^1]);
        Assert.DoesNotContain(r1.Notes, n => n.Text.Contains("铺贴"));
    }

    [Fact]
    public void Run_with_connect_echoes_connector_summary()
    {
        var manual = new List<double[]> { new double[] { 100, 100, 5, 130, 100, 5 } };
        var r = RoadCenterlineRunner.Run(SlopedRectangle(), manual, null, null, Opts(connect: true));
        Assert.True(r.Ok, string.Join(" | ", r.Notes.Select(n => n.Text)));
        Assert.Equal(1, r.ManualCount);
        // 无 TIN 但有台阶线 → 台阶线场铺贴：回显要说明补充线已按现状地形铺贴。
        Assert.Contains(r.Notes, n => n.Text.Contains("补充线已按现状地形铺贴"));
    }
}
