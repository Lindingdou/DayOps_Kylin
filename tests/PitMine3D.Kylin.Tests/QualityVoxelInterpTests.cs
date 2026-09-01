using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using CP = PitMine3D.Kylin.Cad.OrdinaryKriging.ControlPoint;

namespace PitMine3D.Kylin.Tests;

/// <summary>三维体素 IDW 插值回归（忠实原 DefaultIdwInterpolation）。</summary>
public class QualityVoxelInterpTests
{
    // 四角样本, V = x 坐标(线性场), 单层 z=0。
    private static List<CP> Corners() => new()
    {
        new(0, 0, 0, 0), new(10, 0, 0, 10), new(0, 10, 0, 0), new(10, 10, 0, 10),
    };

    [Fact]
    public void Interpolate_fills_grid_exact_at_points_and_linear_between()
    {
        var v = QualityVoxelInterp.Interpolate(Corners(), 0, 0, 0, 10, 10, 0, resolution: 5, power: 2, k: 5);
        Assert.Equal(9, v.Count);                        // 3×3×1 网格全有支撑(自动半径 12.5 覆盖)
        double At(double x, double y) => v.First(t => t.X == x && t.Y == y && t.Z == 0).Value;
        Assert.Equal(0, At(0, 0), 6);                    // 落样本 (0,0,0) V=0 精确
        Assert.Equal(10, At(10, 10), 6);                 // 落样本 (10,10,0) V=10 精确
        Assert.Equal(5, At(5, 5), 6);                    // 中心等权 → (0+10+0+10)/4 = 5
        Assert.Equal(5, At(5, 0), 6);                    // x=5 线上 → 5
    }

    [Fact]
    public void Interpolate_radius_clip_skips_unsupported_voxels()
    {
        // 显式半径 3: 只有落在样本≤3 内的体素保留 → 仅四角(dist 0)。
        var v = QualityVoxelInterp.Interpolate(Corners(), 0, 0, 0, 10, 10, 0, resolution: 5, power: 2, k: 5, radius: 3);
        Assert.Equal(4, v.Count);
        Assert.All(v, t => Assert.True((t.X == 0 || t.X == 10) && (t.Y == 0 || t.Y == 10)));
    }

    [Fact]
    public void Interpolate_multi_layer_z_and_slice()
    {
        // 两层 z=0/10, 值随层不同: 复制四角到两层, 层0 值=x, 层10 值=x+100。
        var pts = new List<CP>
        {
            new(0,0,0,0), new(10,0,0,10), new(0,10,0,0), new(10,10,0,10),
            new(0,0,10,100), new(10,0,10,110), new(0,10,10,100), new(10,10,10,110),
        };
        // k=4: 中心体素取本层最近 4 角(本层 dist 7.07 < 邻层 12.25), z 维把值分开。
        var v = QualityVoxelInterp.Interpolate(pts, 0, 0, 0, 10, 10, 10, resolution: 5, power: 2, k: 4);
        Assert.Equal(27, v.Count);                       // 3×3×3
        var s0 = QualityVoxelInterp.ZSlice(v, 0, 0.1);
        var s10 = QualityVoxelInterp.ZSlice(v, 10, 0.1);
        Assert.Equal(9, s0.Count); Assert.Equal(9, s10.Count);
        // 层 0 中心 → 本层四角均 5; 层 10 中心 → 本层四角均 105。
        Assert.Equal(5, s0.First(t => t.X == 5 && t.Y == 5).Value, 4);
        Assert.Equal(105, s10.First(t => t.X == 5 && t.Y == 5).Value, 4);
    }

    [Fact]
    public void Interpolate_empty_points_and_maxvoxels_guard()
    {
        Assert.Empty(QualityVoxelInterp.Interpolate(new List<CP>(), 0, 0, 0, 10, 10, 0, 5));       // 无点
        // 过密(2001³ ≫ 上限) → 空(防爆内存)。
        Assert.Empty(QualityVoxelInterp.Interpolate(Corners(), 0, 0, 0, 1000, 1000, 1000, 0.5, maxVoxels: 2_000_000));
    }

    [Fact]
    public void Interpolate_k_limits_neighbours()
    {
        // 一堆离群高值远处 + 近处两低值; k=2 只用最近两 → 不被远离群污染。
        var pts = new List<CP> { new(0, 0, 0, 10), new(2, 0, 0, 12) };
        for (int i = 0; i < 20; i++) pts.Add(new CP(40 + i, 40, 0, 999));
        var v = QualityVoxelInterp.Interpolate(pts, 1, 0, 0, 1, 0, 0, resolution: 1, power: 2, k: 2, radius: 1000);
        var cell = Assert.Single(v);
        Assert.InRange(cell.Value, 10, 12);
    }

    [Fact]
    public void ToCsv_has_header_and_row_per_voxel()
    {
        var v = QualityVoxelInterp.Interpolate(Corners(), 0, 0, 0, 10, 10, 0, 5);
        var csv = QualityVoxelInterp.ToCsv(v);
        Assert.Contains("x,y,z,value", csv);
        Assert.Equal(1 + v.Count, csv.Trim().Split('\n').Length);
    }
}
