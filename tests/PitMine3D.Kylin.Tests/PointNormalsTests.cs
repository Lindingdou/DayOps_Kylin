using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>逐点法向/坡度坡向回归（k 近邻 PCA 局部平面）。</summary>
public class PointNormalsTests
{
    // 水平面 z=const 的网格点
    private static List<(double x, double y, double z)> FlatGrid(int n, double z)
    {
        var pts = new List<(double, double, double)>();
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) pts.Add((i, j, z));
        return pts;
    }

    [Fact]
    public void Flat_surface_zero_slope_up_normal()
    {
        var pts = FlatGrid(6, 100);
        var a = PointNormals.Compute(pts, 8);
        Assert.Equal(pts.Count, a.Count);
        // 内部点(远离边界)坡度≈0、法向≈+Z
        int mid = 3 * 6 + 3;   // (3,3)
        Assert.Equal(0.0, a[mid].slope, 3);
        Assert.Equal(1.0, a[mid].n.z, 3);
    }

    [Fact]
    public void Tilted_plane_slope_matches_tilt()
    {
        // 斜面 z = tan(30°)·x → 坡度应≈30°
        double t = Math.Tan(30 * Math.PI / 180);
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) pts.Add((i, j, t * i));
        var a = PointNormals.Compute(pts, 10);
        int mid = 4 * 8 + 4;
        Assert.Equal(30.0, a[mid].slope, 1);   // 坡度≈30°
    }

    [Fact]
    public void Too_few_points_empty()
    {
        Assert.Empty(PointNormals.Compute(new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0) }, 8));
    }

    [Fact]
    public void Degenerate_flagged_only_when_normal_unavailable()
    {
        // 正常网格：没有退化点；退化数进「法向估计完成：成功 M / 退化 K」那行
        var pts = FlatGrid(6, 0);
        var a = PointNormals.ComputeFull(pts, 8);
        Assert.Equal(pts.Count, a.Count);
        Assert.All(a, x => Assert.False(x.Degenerate));
    }

    [Fact]
    public void Normal_segments_sampled_uniformly_and_along_normal()
    {
        // 40×40 平面点(1600), 上限 100 根 → 网格均匀抽样：≤100 根、覆盖四个象限而非只取文件前段
        var pts = FlatGrid(40, 50);
        var nrm = new List<(double x, double y, double z)>();
        foreach (var _ in pts) nrm.Add((0, 0, 1));
        var segs = PointNormals.SampleNormalSegments(pts, nrm, 100, 0);
        Assert.InRange(segs.Count, 50, 100);
        int q = 0;
        foreach (var (a, b) in segs)
        {
            Assert.Equal(a.x, b.x, 9); Assert.Equal(a.y, b.y, 9);     // 沿 +Z 法向
            Assert.Equal(Math.Sqrt(39.0 * 39.0 / 1600) * 2, b.z - a.z, 6);   // 自动长度 = 平均点距 √(面积/n) × 2
            if (a.x >= 20 && a.y >= 20) q++;
        }
        Assert.InRange(q, segs.Count / 6, segs.Count / 2);            // 东北象限约占 1/4, 不是 0
        // 指定长度 + 点数不超上限 → 全取
        var all = PointNormals.SampleNormalSegments(pts, nrm, 5000, 0.5);
        Assert.Equal(pts.Count, all.Count);
        Assert.Equal(0.5, all[0].b.z - all[0].a.z, 9);
    }
}
