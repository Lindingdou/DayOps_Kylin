using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云算子（色带 / 逐点属性区间 / C2C / 点云剖面 / 栅格化 / 最近邻索引）回归。</summary>
public class PointCloudOpsTests
{
    // ── 色带 ──

    [Fact]
    public void Ramp_clamps_and_differs_per_colormap()
    {
        // 越界钳到端点(与 t=0 / t=1 完全一致)
        Assert.Equal(PointCloudOps.Ramp(0, 0), PointCloudOps.Ramp(0, -5));
        Assert.Equal(PointCloudOps.Ramp(0, 1), PointCloudOps.Ramp(0, 7));

        // 灰阶：三通道相等且随 t 单调变亮
        var g0 = PointCloudOps.Ramp(1, 0);
        var g1 = PointCloudOps.Ramp(1, 1);
        Assert.Equal(g0.r, g0.g, 5);
        Assert.Equal(g0.g, g0.b, 5);
        Assert.True(g1.r > g0.r + 0.5f, $"灰阶跨度太小: {g0.r} → {g1.r}");

        // 分歧色：中点近白(三通道都 ≥0.95)，两端分别偏蓝、偏红
        var mid = PointCloudOps.Ramp(2, 0.5);
        Assert.True(mid.r > 0.95f && mid.g > 0.95f && mid.b > 0.95f, $"分歧色中点应近白, 实为 {mid}");
        var neg = PointCloudOps.Ramp(2, 0);
        var pos = PointCloudOps.Ramp(2, 1);
        Assert.True(neg.b > neg.r + 0.4f, "分歧色低端应偏蓝");
        Assert.True(pos.r > pos.b + 0.4f, "分歧色高端应偏红");

        // 地形色：低端偏蓝、高端偏红
        var t0 = PointCloudOps.Ramp(0, 0);
        var t1 = PointCloudOps.Ramp(0, 1);
        Assert.True(t0.b > t0.r, "地形色低端应偏蓝");
        Assert.True(t1.r > t1.b, "地形色高端应偏红");
    }

    [Fact]
    public void Colorize_maps_range_ends_and_clamps_outside()
    {
        var vals = new double[] { -10, 0, 45, 90, 200 };
        var cols = PointCloudOps.Colorize(vals, 0, 0, 90);
        Assert.Equal(5, cols.Count);
        Assert.Equal(PointCloudOps.Ramp(0, 0), cols[0]);   // 区间外钳到下端
        Assert.Equal(PointCloudOps.Ramp(0, 0), cols[1]);
        Assert.Equal(PointCloudOps.Ramp(0, 0.5), cols[2]);
        Assert.Equal(PointCloudOps.Ramp(0, 1), cols[3]);
        Assert.Equal(PointCloudOps.Ramp(0, 1), cols[4]);   // 区间外钳到上端
    }

    [Fact]
    public void DefaultRange_per_attribute()
    {
        var vals = new double[100];
        for (int i = 0; i < vals.Length; i++) vals[i] = i * 0.001;   // 0 ~ 0.099
        Assert.Equal((0.0, 90.0), PointCloudOps.DefaultRange(0, vals));    // 坡度 0–90（跨数据集可比）
        Assert.Equal((0.0, 360.0), PointCloudOps.DefaultRange(1, vals));   // 坡向 0–360
        var (lo, hi) = PointCloudOps.DefaultRange(2, vals);                // 曲率按 P99 截断
        Assert.Equal(0, lo);
        Assert.InRange(hi, 0.09, 0.1);
    }

    // ── C2C 位移监测 ──

    [Fact]
    public void C2c_signed_by_z_and_stats()
    {
        // 基准期：z=0 的一片格网点；对比期：同 XY 但 +2m(隆起) 与 −1m(沉降) 各一半
        var a = new List<(double x, double y, double z)>();
        for (int i = 0; i < 10; i++)
            for (int j = 0; j < 10; j++) a.Add((i, j, 0));
        var b = new List<(double x, double y, double z)>();
        for (int i = 0; i < 10; i++)
            for (int j = 0; j < 10; j++) b.Add((i, j, i < 5 ? 2 : -1));

        var r = PointCloudOps.CloudToCloud(a, b, 0, signedByZ: true);
        Assert.Equal(100, r.Matched);
        Assert.Equal(0, r.Unmatched);
        Assert.Equal(2, r.Max, 6);       // 隆起 +2
        Assert.Equal(-1, r.Min, 6);      // 沉降 −1
        Assert.Equal(0.5, r.Mean, 6);    // (50×2 + 50×(−1)) / 100
        Assert.True(r.Std > 1.0, $"两簇位移的标准差应显著大于 0, 实为 {r.Std}");
        Assert.Equal(2, r.AbsP95, 6);    // |位移| 的 95 分位 = 2
        Assert.Equal(20, r.Histogram.Length);
        Assert.Equal(100, r.Histogram.Sum());

        // 不带符号：全是正距离
        var abs = PointCloudOps.CloudToCloud(a, b, 0, signedByZ: false);
        Assert.Equal(1, abs.Min, 6);
        Assert.Equal(2, abs.Max, 6);
    }

    [Fact]
    public void C2c_maxdist_marks_unmatched_instead_of_faking_displacement()
    {
        var a = new List<(double x, double y, double z)> { (0, 0, 0) };
        var b = new List<(double x, double y, double z)> { (0, 0, 0.5), (0, 0, 50) };

        var r = PointCloudOps.CloudToCloud(a, b, maxDist: 1.0, signedByZ: true);
        Assert.Equal(1, r.Matched);
        Assert.Equal(1, r.Unmatched);
        Assert.Equal(0.5, r.Dist[0], 6);
        Assert.True(double.IsNaN(r.Dist[1]), "超出最大匹配距离的点必须记为未匹配(NaN), 不能伪造出 50m 位移");
        Assert.Equal(0.5, r.Max, 6);   // 统计只算匹配上的
    }

    // ── 最近邻索引：与暴力解对拍 ──

    [Fact]
    public void XyGrid_nearest_matches_bruteforce()
    {
        var rnd = new Random(20260909);
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i < 400; i++) pts.Add((rnd.NextDouble() * 100, rnd.NextDouble() * 100, rnd.NextDouble() * 20));
        var grid = new PointCloudOps.XyGrid(pts);

        for (int q = 0; q < 60; q++)
        {
            var p = (x: rnd.NextDouble() * 120 - 10, y: rnd.NextDouble() * 120 - 10, z: rnd.NextDouble() * 20);
            int idx = grid.Nearest(p, 0, out double d);
            double best = double.MaxValue;
            foreach (var t in pts)
            {
                double dx = t.x - p.x, dy = t.y - p.y, dz = t.z - p.z;
                best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            Assert.True(idx >= 0);
            Assert.Equal(best, d, 6);
        }
    }

    // ── 点云直接剖面 ──

    [Fact]
    public void CloudProfile_aggregates_and_keeps_gaps()
    {
        // 沿 X 的一条带：0~10m 每 0.5m 一个地面点(z=100)，另在 x∈[2,3] 叠一层 z=105 的"设备"
        var pts = new List<(double x, double y, double z)>();
        for (double x = 0; x <= 10.001; x += 0.5)
        {
            if (x > 5.9 && x < 8.1) continue;   // 6~8m 段没测到 → 剖面应留缺口
            pts.Add((x, 0, 100));
            if (x >= 2 && x <= 3) pts.Add((x, 0, 105));
        }
        var line = new List<(double x, double y)> { (0, 0), (10, 0) };

        var low = PointCloudOps.CloudProfile(pts, line, halfWidth: 0.4, step: 1.0, agg: 0);
        var high = PointCloudOps.CloudProfile(pts, line, halfWidth: 0.4, step: 1.0, agg: 2);
        var mean = PointCloudOps.CloudProfile(pts, line, halfWidth: 0.4, step: 1.0, agg: 1);
        Assert.Equal(11, low.Count);                       // 0..10 每米一站

        // 取最低点 = 地面；取最高点在设备段抬到 105
        Assert.Equal(100, low[2].Z, 6);
        Assert.Equal(105, high[2].Z, 6);
        Assert.True(mean[2].Z > 100 && mean[2].Z < 105, $"均值应落在地面与顶面之间, 实为 {mean[2].Z}");

        // 空洞段留缺口(Count=0 / Z=NaN), 而不是被插值成假地面
        Assert.Equal(0, low[7].Count);
        Assert.True(double.IsNaN(low[7].Z));
        Assert.True(low[0].Count > 0 && low[10].Count > 0);
    }

    // ── 栅格化 ──

    [Fact]
    public void Rasterize_agg_modes_and_no_data_cells()
    {
        var pts = new List<(double x, double y, double z)>
        {
            (0.1, 0.1, 10), (0.9, 0.9, 20),    // 同一格(0,0)
            (2.5, 0.5, 30),                     // 格(2,0)
        };
        var min = PointCloudOps.Rasterize(pts, 1.0, 0);
        var mean = PointCloudOps.Rasterize(pts, 1.0, 1);
        var max = PointCloudOps.Rasterize(pts, 1.0, 2);

        Assert.Equal(3, min.Nx);
        Assert.Equal(1, min.Ny);
        Assert.Equal(10, min.At(0, 0), 6);
        Assert.Equal(15, mean.At(0, 0), 6);
        Assert.Equal(20, max.At(0, 0), 6);
        Assert.False(min.HasAt(1, 0));          // 中间那格没数据 —— 不虚构地形
        Assert.Equal(2, min.Filled);
        Assert.Equal(2, PointCloudOps.RasterPoints(min).Count);
    }

    [Fact]
    public void ZHistogram_and_mean_spacing()
    {
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i < 100; i++) pts.Add((i % 10, i / 10, i));
        var (hist, lo, hi) = PointCloudOps.ZHistogram(pts, 10);
        Assert.Equal(10, hist.Length);
        Assert.Equal(0, lo, 6);
        Assert.Equal(99, hi, 6);
        Assert.Equal(100, hist.Sum());
        foreach (int h in hist) Assert.InRange(h, 5, 15);   // 均匀分布 → 各桶约 10

        Assert.Equal(1.0, PointCloudOps.MeanSpacing(100, 100), 6);   // √(100/100)
        Assert.Equal(0, PointCloudOps.MeanSpacing(0, 100));
    }

    // ── 逐点属性(法向/坡度/坡向/曲率) ──

    [Fact]
    public void ComputeFull_flat_plane_has_zero_slope_and_curvature()
    {
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i < 12; i++)
            for (int j = 0; j < 12; j++) pts.Add((i, j, 50));
        var a = PointNormals.ComputeFull(pts, 12);
        Assert.Equal(pts.Count, a.Count);
        foreach (var p in a)
        {
            Assert.InRange(p.SlopeDeg, 0, 1e-6);
            Assert.InRange(p.Curvature, 0, 1e-9);   // 平面 λmin ≈ 0
        }
    }

    [Fact]
    public void ComputeFull_45deg_ramp_slope_and_curvature_magnitude()
    {
        // z = x 的斜面 → 坡度 45°；朝向 −X（下坡方向）
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i < 12; i++)
            for (int j = 0; j < 12; j++) pts.Add((i, j, i));
        var a = PointNormals.ComputeFull(pts, 12);
        double mid = a[a.Count / 2].SlopeDeg;
        Assert.InRange(mid, 44.0, 46.0);
        Assert.InRange(a[a.Count / 2].Curvature, 0, 1e-6);   // 斜面仍是平面, 曲率 ≈ 0

        // 折棱：一半平、一半陡 —— 折线处曲率必须显著大于平面处
        var bent = new List<(double x, double y, double z)>();
        for (int i = 0; i < 16; i++)
            for (int j = 0; j < 16; j++) bent.Add((i, j, i < 8 ? 0 : (i - 8) * 3.0));
        var b = PointNormals.ComputeFull(bent, 12);
        double flatCurv = b[2 * 16 + 8].Curvature;                       // x=2 平坦区
        double edgeCurv = 0;
        for (int j = 0; j < 16; j++) edgeCurv = Math.Max(edgeCurv, b[8 * 16 + j].Curvature);   // x=8 折棱
        Assert.True(edgeCurv > flatCurv + 1e-4, $"折棱曲率({edgeCurv}) 应显著高于平坦区({flatCurv})");
        Assert.True(edgeCurv > 1e-3, $"折棱曲率量级过小({edgeCurv}), 着色会全成一个色");
    }

    [Fact]
    public void AttributeValues_picks_requested_attribute()
    {
        var attribs = new List<PointNormals.PointAttrib>
        {
            new((0, 0, 1), 12.5, 200.0, 0.03),
            new((0, 0, 1), 40.0, 10.0, 0.10),
        };
        Assert.Equal(new[] { 12.5, 40.0 }, PointCloudOps.AttributeValues(attribs, 0));
        Assert.Equal(new[] { 200.0, 10.0 }, PointCloudOps.AttributeValues(attribs, 1));
        Assert.Equal(new[] { 0.03, 0.10 }, PointCloudOps.AttributeValues(attribs, 2));
        Assert.Equal("坡度", PointCloudOps.AttrName(0));
        Assert.Equal("坡向", PointCloudOps.AttrName(1));
        Assert.Equal("曲率", PointCloudOps.AttrName(2));
    }
}

/// <summary>等值线在"无数据格"上的行为回归（空洞不该插值出 NaN 坐标）。</summary>
public class ContourNoDataTests
{
    [Fact]
    public void No_data_cells_produce_no_segments_and_never_nan()
    {
        // 3×3 网格：右上角一格没数据(NaN)，其余是一个跨过 level 的斜坡
        var g = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) g[i, j] = i + j;
        g[2, 2] = double.NaN;

        var segs = PitMine3D.Kylin.Cad.Contour.MarchingSquares(g, 0, 0, 1, 1, 2.5);
        Assert.NotEmpty(segs);   // 有数据的那部分照样出线
        foreach (var s in segs)
        {
            Assert.False(double.IsNaN(s.x0) || double.IsNaN(s.y0) || double.IsNaN(s.x1) || double.IsNaN(s.y1),
                         "无数据格必须整格跳过, 不能插值出 NaN 坐标(会让工程存不了档)");
            Assert.InRange(s.x0, 0, 2);
            Assert.InRange(s.y0, 0, 2);
        }
    }
}
