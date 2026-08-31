using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>确定境界·经济最优坑深回归（逐层净值最大定坑底）。</summary>
public class PitDepthSolverTests
{
    private static ResourceProfileLite Profile(double[] coal, double[] waste, double dz = 12, double dens = 1.35)
        => new() { Nz = coal.Length, Dz = dz, Density = dens, CoalVol = coal, WasteVol = waste };

    [Fact]
    public void Stops_where_marginal_layer_turns_unprofitable()
    {
        // 3 层(k2=顶): 上两层富煤划算, 最深层纯岩(无煤)不划算 → 坑底停在 k1(不挖 k0)
        // k: 0=底(纯岩), 1=煤多岩少, 2=顶煤多岩少
        var p = Profile(
            coal:  new[] { 0.0, 100.0, 100.0 },
            waste: new[] { 1000.0, 50.0, 50.0 });
        // 净收益 220/t, 剥离 20/m³, ρ=1.35
        var r = SectionSolver.SolveDepth(p, 220, 20);
        Assert.Equal(1, r.BottomK);        // 停在 k1(挖 k2,k1; 不挖纯岩 k0)
        Assert.True(r.NetValueYuan > 0);
    }

    [Fact]
    public void Deeper_when_coal_rich_throughout()
    {
        // 各层都富煤 → 挖到底 k0
        var p = Profile(
            coal:  new[] { 100.0, 100.0, 100.0 },
            waste: new[] { 30.0, 30.0, 30.0 });
        var r = SectionSolver.SolveDepth(p, 220, 20);
        Assert.Equal(0, r.BottomK);
        Assert.Equal(3 * 12, r.DepthM, 6);   // 挖满 3 层
    }

    [Fact]
    public void All_uneconomic_reports_zero_net()
    {
        // 全是岩、没煤 → 净值必负, 输出钳到 0(经济信号: 不值得挖); 忠实算法取最小负净(顶层)
        var p = Profile(coal: new[] { 0.0, 0.0 }, waste: new[] { 500.0, 500.0 });
        var r = SectionSolver.SolveDepth(p, 220, 20);
        Assert.Equal(0.0, r.NetValueYuan, 6);   // 净值钳 0 = 不经济
        Assert.Equal(0.0, r.CoalT, 6);          // 无煤圈入
    }

    [Fact]
    public void MaxDepth_caps_bottom()
    {
        var p = Profile(
            coal:  new[] { 100.0, 100.0, 100.0 },
            waste: new[] { 10.0, 10.0, 10.0 }, dz: 12);
        // 限深 24m → 最多挖 2 层 → kFloor = 3-2 = 1
        var r = SectionSolver.SolveDepth(p, 220, 20, maxDepthM: 24);
        Assert.True(r.BottomK >= 1);
    }

    [Fact]
    public void FromBlocks_layers_by_z()
    {
        // 4 块 2 层: z=0 层{煤,岩}, z=1 层{煤,煤}; cutoff=1
        var blocks = new List<(double X, double Y, double Z, double Size, double Grade)>
        {
            (0, 0, 0, 1, 2), (1, 0, 0, 1, 0), (0, 0, 1, 1, 2), (1, 0, 1, 1, 2)
        };
        var p = ResourceProfileLite.FromBlocks(blocks, cutoff: 1, density: 1.35);
        Assert.NotNull(p);
        Assert.Equal(2, p!.Nz);
        Assert.Equal(1.0, p.CoalVol[0], 6); Assert.Equal(1.0, p.WasteVol[0], 6);   // k0(z=0): 1煤1岩
        Assert.Equal(2.0, p.CoalVol[1], 6); Assert.Equal(0.0, p.WasteVol[1], 6);   // k1(z=1): 2煤
    }
}
