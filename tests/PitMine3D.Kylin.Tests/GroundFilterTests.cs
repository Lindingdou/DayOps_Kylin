using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>地面点滤波（每格取最低）回归。</summary>
public class GroundFilterTests
{
    [Fact]
    public void Keeps_lowest_per_cell()
    {
        var pts = new List<(double x, double y, double z)>
        {
            (0.1, 0.1, 10),   // 同 XY 格, 高
            (0.2, 0.2, 3),    // 同 XY 格, 低 → 保留
            (5, 5, 7)         // 另一格
        };
        var g = GroundFilter.LowestPerCell(pts, 1.0);
        Assert.Equal(2, g.Count);
        Assert.Contains(g, p => p.z == 3);       // 低点保留
        Assert.DoesNotContain(g, p => p.z == 10); // 高点滤除
    }

    [Fact]
    public void Zero_cell_keeps_all()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 1), (0, 0, 2) };
        Assert.Equal(2, GroundFilter.LowestPerCell(pts, 0).Count);
    }
}
