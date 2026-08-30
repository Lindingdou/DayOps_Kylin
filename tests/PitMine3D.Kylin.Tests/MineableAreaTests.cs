using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>确定可采区域回归（网格 Z 采样 / 面积 / 环距 / 采煤台阶识别）。</summary>
public class MineableAreaTests
{
    // 一个位于 z=100 平面的大三角(覆盖 XY 原点附近)
    private static (double[] v, int[] t) FloorAt100()
    {
        var v = new double[] { -100, -100, 100, 100, -100, 100, 0, 100, 100 };
        var t = new[] { 0, 1, 2 };
        return (v, t);
    }

    [Fact]
    public void SampleMeshZ_barycentric_on_flat()
    {
        var (v, t) = FloorAt100();
        var z = MineableAreaIdentifier.SampleMeshZ(v, t, 0, 0);
        Assert.NotNull(z);
        Assert.Equal(100.0, z!.Value, 6);
    }

    [Fact]
    public void SampleMeshZ_outside_returns_null()
    {
        var (v, t) = FloorAt100();
        Assert.Null(MineableAreaIdentifier.SampleMeshZ(v, t, 10000, 10000));
    }

    [Fact]
    public void PolygonAreaXY_unit_square()
    {
        var ring = new double[] { 0, 0, 5, 10, 0, 5, 10, 10, 5, 0, 10, 5 };   // 10×10 @ z=5
        Assert.Equal(100.0, System.Math.Abs(MineableAreaIdentifier.PolygonAreaXY(ring)), 6);
    }

    [Fact]
    public void MinRingDistanceXY_between_two_rings()
    {
        var a = new double[] { 0, 0, 0, 10, 0, 0 };
        var b = new double[] { 0, 5, 0, 10, 5, 0 };   // 平行, 相距 5
        Assert.Equal(5.0, MineableAreaIdentifier.MinRingDistanceXY(a, b), 6);
    }

    [Fact]
    public void Identify_picks_coal_bench_nearest_floor()
    {
        var (v, t) = FloorAt100();
        // 采煤台阶：闭合方形在 z=100 附近(与底板匹配); 上覆台阶在 z=130
        var coal = MineableAreaIdentifier.BenchLine.From(
            new double[] { -10, -10, 100, 10, -10, 100, 10, 10, 100, -10, 10, 100, -10, -10, 100 }, closed: true);
        var upper = MineableAreaIdentifier.BenchLine.From(
            new double[] { -30, -30, 130, 30, -30, 130 }, closed: false);
        var r = MineableAreaIdentifier.Identify(v, t, new[] { upper, coal }, wMin: 20, benchH: 15, faceAngleDeg: 65, bermW: 5);
        Assert.True(r.Ok);
        Assert.Same(coal, r.CoalBench);            // 选中与底板标高最近者
        Assert.Equal(100.0, r.CoalFloorZ, 6);
        Assert.True(r.MineableAreaM2 > 300);       // 20×20=400 附近
        Assert.Single(r.Overburden);               // 一级上覆
    }
}
