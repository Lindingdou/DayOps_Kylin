using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 境界圈定·深度版（SectionSolver.SolveDepth + ResourceProfileLite.FromBlocks）已知值回归。
/// 忠实原 PlanLib.BoundaryOptimization.SectionSolver.SolveDepth：从顶向下逐层累加煤/岩，按净值最大定坑底。
/// </summary>
public class PitDepthSolverTests
{
    private static ResourceProfileLite Profile(double[] coalVol, double[] wasteVol, double dz = 1, double density = 1)
        => new() { Nz = coalVol.Length, Dz = dz, Density = density, CoalVol = coalVol, WasteVol = wasteVol };

    [Fact]
    public void FromBlocks_aggregates_coal_waste_by_layer()
    {
        // 三层 1³ 块: z=0 品位1(煤)、z=1 品位0.2(岩)、z=2 品位1(煤)；cutoff 0.5。
        var blocks = new List<(double X, double Y, double Z, double Size, double Grade)>
        {
            (0, 0, 0, 1, 1.0), (0, 0, 1, 1, 0.2), (0, 0, 2, 1, 1.0)
        };
        var p = ResourceProfileLite.FromBlocks(blocks, cutoff: 0.5, density: 1.35);
        Assert.NotNull(p);
        Assert.Equal(3, p!.Nz);
        Assert.Equal(1, p.Dz, 6);
        Assert.Equal(1.35, p.Density, 6);
        Assert.Equal(1.0, p.CoalVol[0], 6);   // z=0 煤(k=0 最深)
        Assert.Equal(0.0, p.CoalVol[1], 6);
        Assert.Equal(1.0, p.CoalVol[2], 6);   // z=2 煤(k=2 最顶)
        Assert.Equal(1.0, p.WasteVol[1], 6);  // z=1 岩
    }

    [Fact]
    public void SolveDepth_digs_full_when_all_coal_no_waste()
    {
        // 两层全煤无岩 → 挖到底(BottomK=0)，煤 20t，净 2000，深 2m。
        var p = Profile(coalVol: new[] { 10.0, 10.0 }, wasteVol: new[] { 0.0, 0.0 }, density: 1);
        var r = SectionSolver.SolveDepth(p, revenuePerCoalT: 100, stripCostPerM3: 10);
        Assert.Equal(0, r.BottomK);            // 挖到最深层
        Assert.Equal(20, r.CoalT, 6);          // 10+10 层煤 × 密度1
        Assert.Equal(2000, r.NetValueYuan, 6); // 20×100 − 0
        Assert.Equal(2, r.DepthM, 6);          // (Nz−BottomK)·Dz = (2−0)·1
    }

    [Fact]
    public void SolveDepth_stops_before_expensive_waste()
    {
        // 顶层煤 10、底层纯岩 100(剥离亏)：挖底 net=10×100−100×10=0 < 只挖顶 1000 → 止于顶层(BottomK=1)。
        var p = Profile(coalVol: new[] { 0.0, 10.0 }, wasteVol: new[] { 100.0, 0.0 }, density: 1);
        var r = SectionSolver.SolveDepth(p, revenuePerCoalT: 100, stripCostPerM3: 10);
        Assert.Equal(1, r.BottomK);            // 不挖到亏损底层
        Assert.Equal(10, r.CoalT, 6);
        Assert.Equal(1, r.DepthM, 6);          // 只挖顶层, 深 1m
        Assert.Equal(1000, r.NetValueYuan, 6);
    }

    [Fact]
    public void SolveDepth_respects_max_depth_constraint()
    {
        // 全煤本应挖到底(深2), 但几何限深 maxDepth=1 → 坑底受限于 BottomK=1、深 1m。
        var p = Profile(coalVol: new[] { 10.0, 10.0 }, wasteVol: new[] { 0.0, 0.0 }, density: 1);
        var r = SectionSolver.SolveDepth(p, revenuePerCoalT: 100, stripCostPerM3: 10, maxDepthM: 1);
        Assert.Equal(1, r.BottomK);
        Assert.Equal(1, r.DepthM, 6);
        Assert.Equal(10, r.CoalT, 6);          // 只圈入顶层煤
    }
}
