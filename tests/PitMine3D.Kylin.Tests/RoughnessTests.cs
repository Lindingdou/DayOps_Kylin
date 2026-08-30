using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>地表粗糙度回归。</summary>
public class RoughnessTests
{
    [Fact]
    public void Flat_grid_zero_roughness()
    {
        var g = new double[4, 4];   // 全 0
        var r = Roughness.Compute(g);
        foreach (var v in r) Assert.Equal(0, v, 6);
    }

    [Fact]
    public void Spike_creates_local_roughness()
    {
        var g = new double[3, 3];
        g[1, 1] = 10;               // 中心尖峰
        var r = Roughness.Compute(g);
        Assert.Equal(10, r[1, 1], 6);   // 中心 3×3 邻域极差 = 10
        Assert.Equal(10, r[0, 0], 6);   // 角点邻域含中心 → 也 10
    }
}
