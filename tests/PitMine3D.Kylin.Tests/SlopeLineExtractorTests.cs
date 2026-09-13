using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 点云坡顶/坡底线提取(原 laslib::bench::extract_bench_lines 现行等值线通路的托管移植)回归：
/// 合成台阶点云 → 坡顶线在平台顶缘、坡底线在坡脚，标高各归各；平地不出线；作业区域裁剪只留一侧。
/// </summary>
public class SlopeLineExtractorTests
{
    /// <summary>
    /// 一个台阶：x∈[0,150] 平台 z=100，x∈[150,160] 坡面(60°级：10m 走 15m 高)… 简化为 x 150→160 直线降 12m，x∈[160,320] 平台 z=88。
    /// 沿 y 拉 300m。0.5m 点距 + 小噪声。整体平移到大坐标(4 000 000 级)验 double 精度。
    /// </summary>
    private static List<(double x, double y, double z)> OneBench(double ox = 4_500_000, double oy = 38_600_000)
    {
        var rnd = new Random(7);
        var pts = new List<(double, double, double)>();
        for (double x = 0; x <= 320; x += 0.5)
            for (double y = 0; y <= 300; y += 0.5)
            {
                double z = x < 150 ? 100 : x > 160 ? 88 : 100 - (x - 150) * 1.2;
                z += (rnd.NextDouble() - 0.5) * 0.04;
                pts.Add((ox + x, oy + y, z));
            }
        return pts;
    }

    [Fact]
    public void One_bench_gives_crest_on_top_edge_and_toe_at_foot()
    {
        var pts = OneBench();
        var opt = new SlopeLineExtractor.Options { CellSize = 1.0 };
        var st = new SlopeLineExtractor.Stats();
        bool ok = SlopeLineExtractor.Extract(pts, opt, out var crest, out var toe, st, out string err);
        Assert.True(ok, err);
        Assert.NotEmpty(crest);
        Assert.NotEmpty(toe);
        // 最长的坡顶线/坡底线都应顺 y 方向贯穿台阶(≥200m)
        var c = crest.OrderByDescending(SlopeLineExtractor.PolyLen).First();
        var t = toe.OrderByDescending(SlopeLineExtractor.PolyLen).First();
        Assert.True(SlopeLineExtractor.PolyLen(c) > 200, $"crest len {SlopeLineExtractor.PolyLen(c)}");
        Assert.True(SlopeLineExtractor.PolyLen(t) > 200, $"toe len {SlopeLineExtractor.PolyLen(t)}");
        // 坡顶线落在坡顶缘附近 (x≈150)，坡底线落在坡脚附近 (x≈160)；容许等值线阈值/平滑带来的几米偏移
        double cx = c.Xs.Average() - 4_500_000, tx = t.Xs.Average() - 4_500_000;
        Assert.InRange(cx, 143, 157);
        Assert.InRange(tx, 153, 167);
        Assert.True(cx < tx, $"crest x {cx} should be uphill of toe x {tx}");
        // 标高各归各：坡顶线在上平台附近，坡底线在下平台附近
        Assert.InRange(c.Zs.Average(), 96, 101);
        Assert.InRange(t.Zs.Average(), 87, 92);
        Assert.Equal(c.Xs.Count, c.Zs.Count);
        Assert.True(st.DemW > 300 && st.DemH > 290);
    }

    /// <summary>
    /// 多级台阶(300m 一级、12m 高、50° 坡面)贯穿到点云矩形边界：每级都要有一条坡顶线 + 一条坡底线，
    /// 且线长 ≈ 台阶全长。回归"贴 DEM 边界行的顶点被终次覆盖裁剪判出界、两顶点直线整条丢"的坑。
    /// </summary>
    [Fact]
    public void Stacked_benches_reaching_dem_border_keep_every_crest_and_toe()
    {
        var rnd = new Random(1);
        var pts = new List<(double x, double y, double z)>();
        for (double x = 0; x <= 900; x += 0.7)
            for (double y = 0; y <= 200; y += 0.7)
            {
                double k = Math.Floor(x / 300), lx = x - k * 300;
                double z = 500 - k * 12 - (lx < 290 ? 0 : (lx - 290) * 1.2);
                pts.Add((x, y, z + (rnd.NextDouble() - 0.5) * 0.05));
            }
        var st = new SlopeLineExtractor.Stats();
        bool ok = SlopeLineExtractor.Extract(pts, new SlopeLineExtractor.Options { CellSize = 1 }, out var crest, out var toe, st, out string err);
        Assert.True(ok, err);
        // 坡面在 x=290~300 / 590~600 两处（第三级 890~900 贴右边界，坡脚落在点云外，不强求）
        Assert.True(crest.Count >= 2, $"crest {crest.Count}");
        Assert.True(toe.Count >= 2, $"toe {toe.Count}");
        Assert.All(crest, c => Assert.True(SlopeLineExtractor.PolyLen(c) > 180, $"crest len {SlopeLineExtractor.PolyLen(c)}"));
        Assert.All(toe, t => Assert.True(SlopeLineExtractor.PolyLen(t) > 180, $"toe len {SlopeLineExtractor.PolyLen(t)}"));
        Assert.Contains(crest, c => Math.Abs(c.Xs.Average() - 290) < 8);
        Assert.Contains(crest, c => Math.Abs(c.Xs.Average() - 590) < 8);
        Assert.Contains(toe, t => Math.Abs(t.Xs.Average() - 300) < 8);
        Assert.Contains(toe, t => Math.Abs(t.Xs.Average() - 600) < 8);
    }

    [Fact]
    public void Flat_ground_gives_no_lines()
    {
        var rnd = new Random(3);
        var pts = new List<(double x, double y, double z)>();
        for (double x = 0; x <= 120; x += 1)
            for (double y = 0; y <= 120; y += 1)
                pts.Add((x, y, 50 + (rnd.NextDouble() - 0.5) * 0.05));
        var st = new SlopeLineExtractor.Stats();
        bool ok = SlopeLineExtractor.Extract(pts, new SlopeLineExtractor.Options(), out var crest, out var toe, st, out string err);
        Assert.True(ok, err);
        Assert.Empty(crest);
        Assert.Empty(toe);
    }

    [Fact]
    public void Empty_cloud_fails_with_message()
    {
        var st = new SlopeLineExtractor.Stats();
        bool ok = SlopeLineExtractor.Extract(new List<(double x, double y, double z)>(), new SlopeLineExtractor.Options(), out _, out _, st, out string err);
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Grid_cap_refuses_oversized_dem()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 0), (10_000, 10_000, 0) };
        var st = new SlopeLineExtractor.Stats();
        bool ok = SlopeLineExtractor.Extract(pts, new SlopeLineExtractor.Options { CellSize = 1, MaxCells = 1_000_000 }, out _, out _, st, out string err);
        Assert.False(ok);
        Assert.Contains("DEM", err);
    }

    [Fact]
    public void Region_clip_keeps_only_inside_or_outside()
    {
        // 一条沿 x 的线 0..100，区域 = x∈[30,60] 的方框
        var line = new SlopeLineExtractor.Polyline3();
        for (int x = 0; x <= 100; x += 10) line.Add(x, 5, x * 0.1);
        var ring = new double[] { 30, 0, 60, 0, 60, 10, 30, 10 };

        var inside = new List<SlopeLineExtractor.Polyline3> { line };
        SlopeLineExtractor.ClipToRegions(inside, new[] { ring }, wantInside: true, minKeepLen: 5);
        Assert.Single(inside);
        Assert.Equal(30, inside[0].Xs.Min(), 6);
        Assert.Equal(60, inside[0].Xs.Max(), 6);
        Assert.Equal(3.0, inside[0].Zs.First(), 6);   // 边界处 Z 线性插值

        var outside = new List<SlopeLineExtractor.Polyline3> { line };
        SlopeLineExtractor.ClipToRegions(outside, new[] { ring }, wantInside: false, minKeepLen: 5);
        Assert.Equal(2, outside.Count);
        Assert.True(outside.All(p => p.Xs.All(x => x <= 30 + 1e-6 || x >= 60 - 1e-6)));
    }

    [Fact]
    public void Marching_squares_traces_one_continuous_contour()
    {
        // 场 f = x：level 4.5 → 一条竖线 x=4.5 贯穿全高
        int w = 10, h = 8;
        var f = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) f[y * w + x] = x;
        var lines = SlopeLineExtractor.MarchingSquares(f, w, h, 4.5f);
        Assert.Single(lines);
        Assert.Equal(h, lines[0].Count);
        Assert.All(lines[0], p => Assert.Equal(4.5f, p.x, 5));
    }

    [Fact]
    public void Cancellation_throws()
    {
        var pts = OneBench(0, 0);
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var st = new SlopeLineExtractor.Stats();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SlopeLineExtractor.Extract(pts, new SlopeLineExtractor.Options(), out _, out _, st, out _, null, cts.Token));
    }
}
