using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 趋势整合现状台阶 回归 —— 忠实移植原 TrendBenchIntegrator 的已知值验证:
/// 趋势∩台阶求交 → 交点取台阶代表标高 → 按标高聚级 → 每级压平到 z_k + 断头接平。
/// </summary>
public class TrendBenchIntegratorTests
{
    // 水平台阶线: y=Y, z=Z, x 从 x0 到 x1。
    static double[] H(double y, double z, double x0, double x1) => new[] { x0, y, z, x1, y, z };
    static List<(double X, double Y, double Z)> Trend(params (double, double)[] xy)
        => xy.Select(p => (p.Item1, p.Item2, 0.0)).ToList();

    [Fact]
    public void Vertical_trend_crosses_three_distinct_levels()
    {
        // 竖直趋势 x=5, 穿三条水平台阶(y=10/30/50, z=100/110/120)。
        var trend = Trend((5, -10), (5, 100));
        var benches = new List<double[]> { H(10, 100, 0, 10), H(30, 110, 0, 10), H(50, 120, 0, 10) };
        var r = TrendBenchIntegrator.Integrate(trend, benches, bandwidth: 5);
        Assert.True(r.Success, r.Error);
        Assert.Equal(3, r.CrossingCount);
        Assert.Equal(3, r.Benches.Count);
        // 按站号(=沿趋势 y)升序 → 标高 100/110/120。
        Assert.Equal(100, r.Benches[0].Elevation, 6);
        Assert.Equal(110, r.Benches[1].Elevation, 6);
        Assert.Equal(120, r.Benches[2].Elevation, 6);
        Assert.All(r.Benches[0].Line, p => Assert.Equal(100, p.Z, 6));   // 压平到 z_k
    }

    [Fact]
    public void Same_level_pieces_chained_into_one_line()
    {
        // 两条同标高(z=100)台阶段(y=10 与 y=20), 趋势都穿 → 同级 → 压平+接平成一条(4 点)。
        var trend = Trend((5, -10), (5, 100));
        var benches = new List<double[]> { H(10, 100, 0, 10), H(20, 100, 0, 10) };
        var r = TrendBenchIntegrator.Integrate(trend, benches, bandwidth: 5);
        Assert.True(r.Success);
        Assert.Single(r.Benches);
        Assert.Equal(2, r.Benches[0].SourceCount);
        Assert.Equal(4, r.Benches[0].Line.Count);
        Assert.Equal(100, r.Benches[0].Elevation, 6);
    }

    [Fact]
    public void No_crossing_fails()
    {
        var trend = Trend((100, 0), (100, 100));   // x=100, 台阶在 x0..10
        var benches = new List<double[]> { H(10, 100, 0, 10) };
        var r = TrendBenchIntegrator.Integrate(trend, benches, bandwidth: 5);
        Assert.False(r.Success);
        Assert.Contains("没穿过", r.Error);
    }

    [Fact]
    public void Invalid_trend_fails()
    {
        var r = TrendBenchIntegrator.Integrate(Trend((5, 0)), new List<double[]> { H(10, 100, 0, 10) }, 5);
        Assert.False(r.Success);
        Assert.Contains("趋势线无效", r.Error);
    }

    [Fact]
    public void ExtractPlatformLevels_clusters_descending()
    {
        // z 100/110/120/120.5 → 后两并级(带宽5); 降序 [120.5,110,100]。
        var benches = new List<double[]> { H(10, 100, 0, 10), H(20, 110, 0, 10), H(30, 120, 0, 10), H(40, 120.5, 0, 10) };
        var levels = TrendBenchIntegrator.ExtractPlatformLevels(benches, 5);
        Assert.Equal(3, levels.Length);
        Assert.Equal(120.5, levels[0], 6);   // 顶在前
        Assert.Equal(110, levels[1], 6);
        Assert.Equal(100, levels[2], 6);
    }

    [Fact]
    public void ExtractLevelsAlongTrend_only_counts_crossed()
    {
        // 趋势只穿 y=10/30 两条(x=5), 不到 y=50。
        var trend = Trend((5, 0), (5, 35));
        var benches = new List<double[]> { H(10, 100, 0, 10), H(30, 110, 0, 10), H(50, 120, 0, 10) };
        var levels = TrendBenchIntegrator.ExtractPlatformLevelsAlongTrend(trend, benches, 5);
        Assert.Equal(2, levels.Length);      // 只 100/110, 不含 120
        Assert.Equal(110, levels[0], 6);
        Assert.Equal(100, levels[1], 6);
    }
}
