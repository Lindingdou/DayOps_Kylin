using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 2.5D 建面用的 XY 最低点抽稀（忠实原 LasLib voxel_downsample_xy_minz / adaptive_voxel_downsample_minz）。
/// 返回的是源点索引 —— 建面顶点真实色靠它带过去。
/// </summary>
public class PointThinXyMinZTests
{
    [Fact]
    public void XyMinZ_keeps_lowest_point_per_xy_cell_regardless_of_z()
    {
        // 同一 XY 格里三点 z 各异(树冠 12 / 车顶 3 / 地面 0.5) → 只留地面那个；三维体素抽稀会把它们分到 3 格全留
        var pts = new List<(double x, double y, double z)> { (0.2, 0.3, 12), (0.6, 0.1, 3), (0.9, 0.8, 0.5), (5, 5, 7) };
        var idx = PointThin.ThinXyMinZ(pts, 1.0);
        Assert.Equal(new[] { 2, 3 }, idx.OrderBy(i => i));
        Assert.Equal(4, PointThin.Thin(pts, 1.0).Count);   // 对照：旧三维体素法留 4 个
    }

    [Fact]
    public void XyMinZ_zero_voxel_returns_identity()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 0), (0, 0, 1), (3, 3, 3) };
        Assert.Equal(new[] { 0, 1, 2 }, PointThin.ThinXyMinZ(pts, 0));
    }

    [Fact]
    public void XyMinZ_negative_coordinates_do_not_collide()
    {
        // 负坐标 floor 到负格号；(-1,y) 与 (+1,y)、(x,-1) 与 (x,+1) 必须是不同格（打包 long 键的符号位）
        var pts = new List<(double x, double y, double z)> { (-0.5, 0.5, 0), (0.5, 0.5, 0), (0.5, -0.5, 0), (-0.5, -0.5, 0) };
        Assert.Equal(4, PointThin.ThinXyMinZ(pts, 1.0).Count);
    }

    [Fact]
    public void Adaptive_flat_cell_keeps_one_steep_cell_keeps_fine_grid()
    {
        var pts = new List<(double x, double y, double z)>();
        // 平地粗格 [0,4)²：16 个点 z 落差 0.2m(< 1m) → 只留 1 个最低点
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) pts.Add((i + 0.5, j + 0.5, 0.05 * (i + j)));
        // 坡面粗格 [8,12)×[0,4)：16 个点 z 从 0 到 6(≥ 1m) → 细格 1m 各留 1 → 16 个
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) pts.Add((8 + i + 0.5, j + 0.5, 2.0 * i));
        var idx = PointThin.ThinAdaptiveXyMinZ(pts, 4.0, out long flat, out long steep, fineRatio: 0.25, flatZRange: 1.0);
        Assert.Equal(1, flat);
        Assert.Equal(1, steep);
        Assert.Equal(17, idx.Count);
        Assert.Contains(0, idx);                        // 平地格留的是最低点 (0.5,0.5,0)
        Assert.Equal(16, idx.Count(i => pts[i].x >= 8));  // 坡面格全留
    }

    [Fact]
    public void Adaptive_all_flat_equals_plain_xy_minz()
    {
        var pts = new List<(double x, double y, double z)>();
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++) pts.Add((rng.NextDouble() * 100, rng.NextDouble() * 100, rng.NextDouble() * 0.3));
        var a = PointThin.ThinAdaptiveXyMinZ(pts, 5.0, out long flat, out long steep);
        var b = PointThin.ThinXyMinZ(pts, 5.0);
        Assert.Equal(0, steep);
        Assert.Equal(b.OrderBy(i => i), a.OrderBy(i => i));
        Assert.Equal(flat, b.Count);
    }

    [Fact]
    public void Adaptive_two_million_points_finishes_in_seconds()
    {
        // 2.5D 建面「剖分慢」的回归护栏：旧 ThinAdaptive 在这个量级是分钟级；新法两遍哈希应在数秒内
        const int n = 2_000_000;
        var pts = new List<(double x, double y, double z)>(n);
        var rng = new Random(1);
        for (int i = 0; i < n; i++)
        {
            double x = 500_000 + rng.NextDouble() * 2000, y = 4_000_000 + rng.NextDouble() * 2000;
            double z = 800 + 0.02 * (x - 500_000) + (((int)(x / 100) % 2 == 0) ? 15 : 0) + rng.NextDouble() * 0.2;   // 台阶地形
            pts.Add((x, y, z));
        }
        var sw = Stopwatch.StartNew();
        var idx = PointThin.ThinAdaptiveXyMinZ(pts, 3.0, out long flat, out long steep);
        sw.Stop();
        Assert.True(idx.Count > 100_000 && idx.Count < n);
        Assert.True(flat > 0 && steep > 0);
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"adaptive thin took {sw.Elapsed.TotalSeconds:0.0}s");
    }
}
