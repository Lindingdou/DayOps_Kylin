using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云抽稀（体素网格 + 自适应保特征）回归。</summary>
public class PointThinTests
{
    [Fact]
    public void Points_in_same_cell_reduced_to_one()
    {
        var pts = new List<(double x, double y, double z)>
        {
            (0, 0, 0), (0.1, 0.2, 0.1),   // 同一 cell(1) → 保留 1
            (5, 5, 0)                      // 另一 cell
        };
        var t = PointThin.Thin(pts, 1.0);
        Assert.Equal(2, t.Count);
    }

    [Fact]
    public void Larger_cell_thins_more()
    {
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i < 10; i++) pts.Add((i, 0, 0));   // 0..9
        Assert.Equal(10, PointThin.Thin(pts, 0.5).Count);   // 细格全保留
        Assert.Equal(2, PointThin.Thin(pts, 5.0).Count);    // 粗格 [0..4][5..9] → 2
    }

    [Fact]
    public void Zero_cell_keeps_all()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 0), (0, 0, 0) };
        Assert.Equal(2, PointThin.Thin(pts, 0).Count);
    }

    // ── 自适应保特征抽稀 ──
    [Fact]
    public void Adaptive_keeps_features_denser_than_flat()
    {
        var pts = new List<(double x, double y, double z)>();
        // 平坦区: 20×20 网格 z=0(密, 400 点)
        for (int i = 0; i < 20; i++) for (int j = 0; j < 20; j++) pts.Add((i * 1.0, j * 1.0, 0));
        // 尖脊: 沿 x=10 一列, z 交替 0/5(高曲率, 20 点)
        int ridgeCount = 0;
        for (int j = 0; j < 20; j++) { pts.Add((10, j + 0.5, (j % 2 == 0) ? 5 : 0)); ridgeCount++; }

        var kept = PointThin.ThinAdaptive(pts, cell: 2.0, maxThin: 4.0);
        Assert.True(kept.Count < pts.Count, "应有抽稀");
        // 保留点里高 z(脊, |z|>2) 的比例 应显著高于其在原始的比例(特征被优先保留)
        int keptRidge = kept.Count(p => System.Math.Abs(p.z) > 2);
        int allRidge = pts.Count(p => System.Math.Abs(p.z) > 2);
        double keptRidgeFrac = (double)keptRidge / allRidge;
        int keptFlat = kept.Count(p => System.Math.Abs(p.z) <= 1e-9);
        int allFlat = pts.Count(p => System.Math.Abs(p.z) <= 1e-9);
        double keptFlatFrac = (double)keptFlat / allFlat;
        Assert.True(keptRidgeFrac > keptFlatFrac, $"特征保留率 {keptRidgeFrac:0.##} 应 > 平坦保留率 {keptFlatFrac:0.##}");
    }

    [Fact]
    public void Adaptive_flat_falls_back_to_voxel_and_degenerate()
    {
        // 全平 → 退回体素(等价 Thin 计数)
        var flat = new List<(double x, double y, double z)>();
        for (int i = 0; i < 10; i++) for (int j = 0; j < 10; j++) flat.Add((i, j, 0));
        Assert.Equal(PointThin.Thin(flat, 2.0).Count, PointThin.ThinAdaptive(flat, 2.0).Count);
        Assert.Empty(PointThin.ThinAdaptive(new List<(double x, double y, double z)>(), 1.0));
    }
}
