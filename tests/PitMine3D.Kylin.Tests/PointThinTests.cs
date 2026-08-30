using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云抽稀（体素网格）回归。</summary>
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
}
