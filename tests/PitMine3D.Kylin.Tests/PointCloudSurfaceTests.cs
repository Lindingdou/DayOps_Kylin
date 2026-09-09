using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 点云 → 面（补有界小洞 + 栅格转三角网）与「两期点云算量」链路回归：
/// 两期点云 → 各自成面 → CutFillSolids → 挖/填独立封闭体与体积。
/// </summary>
public class PointCloudSurfaceTests
{
    /// <summary>造一片 nx×ny 米的水平点阵（每米一个点），高程 z。</summary>
    private static List<(double x, double y, double z)> Flat(double nx, double ny, double z, double step = 1.0)
    {
        var pts = new List<(double x, double y, double z)>();
        for (double x = 0; x <= nx + 1e-9; x += step)
            for (double y = 0; y <= ny + 1e-9; y += step) pts.Add((x, y, z));
        return pts;
    }

    // ── 有界小洞填充 ──

    [Fact]
    public void FillSmallHoles_fills_enclosed_cell_but_not_open_region()
    {
        var pts = Flat(4, 4, 100);
        // 挖掉正中一格的点 → 该格无数据，四面都有数据 = 有界小洞
        pts.RemoveAll(p => Math.Abs(p.x - 2) < 1e-9 && Math.Abs(p.y - 2) < 1e-9);
        var r = PointCloudOps.Rasterize(pts, 1.0, 0);
        int ix = (int)Math.Round((2 - r.MinX) / r.Cell), iy = (int)Math.Round((2 - r.MinY) / r.Cell);
        Assert.False(r.HasAt(ix, iy));

        int filled = PointCloudOps.FillSmallHoles(r, 1);
        Assert.Equal(1, filled);
        Assert.True(r.HasAt(ix, iy));
        Assert.Equal(100, r.At(ix, iy), 6);   // 邻域均值 = 100

        // 大片无数据区不被虚构：整块右半边没点时，补洞不应把它填出来
        var half = Flat(4, 4, 100);
        half.RemoveAll(p => p.x > 2.5);
        var r2 = PointCloudOps.Rasterize(half, 1.0, 0);
        int before = r2.Filled;
        PointCloudOps.FillSmallHoles(r2, 1);
        Assert.Equal(before, r2.Filled);      // 边界外没有"被包住"的格 → 一格没补
    }

    [Fact]
    public void FillSmallHoles_zero_radius_is_noop()
    {
        var pts = Flat(3, 3, 50);
        pts.RemoveAll(p => Math.Abs(p.x - 1) < 1e-9 && Math.Abs(p.y - 1) < 1e-9);
        var r = PointCloudOps.Rasterize(pts, 1.0, 0);
        int before = r.Filled;
        Assert.Equal(0, PointCloudOps.FillSmallHoles(r, 0));
        Assert.Equal(before, r.Filled);
    }

    // ── 栅格 → 三角网 ──

    [Fact]
    public void RasterToMesh_makes_two_triangles_per_full_quad()
    {
        var r = PointCloudOps.Rasterize(Flat(2, 2, 10), 1.0, 0);   // 3×3 格全有数据
        var (v, t) = PointCloudOps.RasterToMesh(r);
        Assert.Equal(9 * 3, v.Length);          // 9 个格中心
        Assert.Equal(2 * 2 * 2 * 3, t.Length);  // 2×2 个四边形 × 2 三角 × 3 索引
        for (int i = 2; i < v.Length; i += 3) Assert.Equal(10, v[i], 6);
    }

    [Fact]
    public void RasterToMesh_leaves_hole_where_no_data()
    {
        var pts = Flat(2, 2, 10);
        pts.RemoveAll(p => Math.Abs(p.x - 1) < 1e-9 && Math.Abs(p.y - 1) < 1e-9);   // 中心格没数据
        var r = PointCloudOps.Rasterize(pts, 1.0, 0);
        var (_, t) = PointCloudOps.RasterToMesh(r);
        Assert.Empty(t);   // 四个四边形都缺一个角 → 一个三角都不出，空洞不被桥接
    }

    // ── 两期算量：与「两期三角网算量」同一套封闭体算法 ──

    [Fact]
    public void Two_epoch_from_clouds_reports_fill_volume_and_one_solid()
    {
        // 第一期平地 z=100；第二期同范围整体抬高 3m 的一块 30×30 台（其余仍 100）
        var a = Flat(60, 60, 100);
        var b = new List<(double x, double y, double z)>();
        foreach (var p in a) b.Add((p.x, p.y, p.x >= 15 && p.x <= 45 && p.y >= 15 && p.y <= 45 ? 103 : 100));

        var ra = PointCloudOps.Rasterize(a, 1.0, 0);
        var rb = PointCloudOps.Rasterize(b, 1.0, 0);
        var (va, ta) = PointCloudOps.RasterToMesh(ra);
        var (vb, tb) = PointCloudOps.RasterToMesh(rb);

        var res = CutFillSolids.Compute(va, ta, vb, tb,
            new CutFillSolids.Options { GridCell = 1.0, RenderCell = 3.0, MinDz = 1.0, OpenRadius = 0, MinBenchH = 0 });

        Assert.Equal("", res.Error);
        // 抬高的那块 ≈ 30×30×3 = 2700 m³（格心采样，边界一圈有半格出入）
        Assert.InRange(res.FillM3, 2200, 3200);
        Assert.Equal(0, res.CutM3, 3);
        Assert.NotEmpty(res.Bodies);
        var body = Assert.Single(res.Bodies);
        Assert.True(body.IsFill);
        Assert.InRange(body.MaxDz, 2.9, 3.1);
        Assert.InRange(body.AreaM2, 700, 1100);      // ≈ 30×30 的投影面积
        Assert.True(body.Tris.Count > 0, "封闭体必须有三角面(挖/填体要能入场景显示)");
        Assert.True(body.Verts.Count > 0);
    }

    [Fact]
    public void Two_epoch_cut_is_reported_when_second_epoch_is_lower()
    {
        var a = Flat(40, 40, 100);
        var b = new List<(double x, double y, double z)>();
        foreach (var p in a) b.Add((p.x, p.y, p.x >= 10 && p.x <= 30 && p.y >= 10 && p.y <= 30 ? 95 : 100));

        var ra = PointCloudOps.Rasterize(a, 1.0, 0);
        var rb = PointCloudOps.Rasterize(b, 1.0, 0);
        var (va, ta) = PointCloudOps.RasterToMesh(ra);
        var (vb, tb) = PointCloudOps.RasterToMesh(rb);
        var res = CutFillSolids.Compute(va, ta, vb, tb,
            new CutFillSolids.Options { GridCell = 1.0, RenderCell = 2.0, MinDz = 1.0, OpenRadius = 0, MinBenchH = 0 });

        Assert.Equal("", res.Error);
        Assert.InRange(res.CutM3, 1600, 2400);   // ≈ 20×20×5 = 2000
        Assert.Equal(0, res.FillM3, 3);
        Assert.All(res.Bodies, bd => Assert.False(bd.IsFill));
        Assert.True(res.NetM3 < 0, "第二期变低 → 净值应为负(挖多于填)");
    }

    [Fact]
    public void Min_bench_height_drops_shallow_change()
    {
        // 只抬高 1.2m：最小高差 1.0 判为变化，但最小台阶高 3m 会把整块丢掉
        var a = Flat(30, 30, 200);
        var b = new List<(double x, double y, double z)>();
        foreach (var p in a) b.Add((p.x, p.y, p.x >= 10 && p.x <= 20 && p.y >= 10 && p.y <= 20 ? 201.2 : 200));

        var ra = PointCloudOps.Rasterize(a, 1.0, 0);
        var rb = PointCloudOps.Rasterize(b, 1.0, 0);
        var (va, ta) = PointCloudOps.RasterToMesh(ra);
        var (vb, tb) = PointCloudOps.RasterToMesh(rb);

        var loose = CutFillSolids.Compute(va, ta, vb, tb,
            new CutFillSolids.Options { GridCell = 1.0, RenderCell = 2.0, MinDz = 1.0, OpenRadius = 0, MinBenchH = 0 });
        Assert.NotEmpty(loose.Bodies);
        Assert.True(loose.FillM3 > 100);

        var strict = CutFillSolids.Compute(va, ta, vb, tb,
            new CutFillSolids.Options { GridCell = 1.0, RenderCell = 2.0, MinDz = 1.0, OpenRadius = 0, MinBenchH = 3.0 });
        Assert.Empty(strict.Bodies);
        Assert.Equal(0, strict.FillM3, 6);
        Assert.True(strict.DroppedLowBench > 0, "被最小台阶高丢掉的块要计数, 好在结果里交代清楚");
    }
}
