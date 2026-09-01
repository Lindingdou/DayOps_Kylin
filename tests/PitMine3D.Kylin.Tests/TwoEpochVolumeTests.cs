using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 两期填挖方算量（DEM 格差：栅格化两期点云 → 逐格 Δz×格面积 → 挖/填/净）已知值回归。
/// 原版此算量走 native(PMVC), Kylin 托管重算 —— 用解析面(平升/半升半降/同面)合成已知体积校核。
/// </summary>
public class TwoEpochVolumeTests
{
    // [0,10]×[0,10] 上 11×11 规则点面，z 由 zf 给。
    private static List<(double x, double y, double z)> Surface(Func<double, double, double> zf)
    {
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 10; j++)
                pts.Add((i, j, zf(i, j)));
        return pts;
    }

    [Fact]
    public void Uniform_rise_is_pure_fill()
    {
        // 面从 z=0 整体升到 z=5，面积 100 → 填方 = 100×5 = 500，挖方 0。
        var a = Surface((x, y) => 0);
        var b = Surface((x, y) => 5);
        var (cut, fill, net) = TerrainAnalysis.TwoEpochVolume(a, b, 16);
        Assert.InRange(fill, 480, 520);   // ≈500（栅格边缘微差）
        Assert.InRange(cut, 0, 5);         // 无下降 → 挖≈0
        Assert.InRange(net, 480, 520);     // net = fill − cut ≈ 500
    }

    [Fact]
    public void Uniform_drop_is_pure_cut()
    {
        // 面从 z=5 整体降到 z=0 → 挖方 ≈500，填方 0，净 ≈ −500。
        var a = Surface((x, y) => 5);
        var b = Surface((x, y) => 0);
        var (cut, fill, net) = TerrainAnalysis.TwoEpochVolume(a, b, 16);
        Assert.InRange(cut, 480, 520);
        Assert.InRange(fill, 0, 5);
        Assert.InRange(net, -520, -480);
    }

    [Fact]
    public void Linear_ramp_gives_symmetric_cut_fill()
    {
        // b = x−5（关于 x=5 反对称的线性斜坡，DEM 线性精确表示；n=11 使 x=5 落格点→严格对称）：
        //   填 = ∫∫ max(x−5,0) = 125，挖 = ∫∫ max(5−x,0) = 125，净 = 0。
        var a = Surface((x, y) => 0);
        var b = Surface((x, y) => x - 5);
        var (cut, fill, net) = TerrainAnalysis.TwoEpochVolume(a, b, 11);
        Assert.InRange(fill, 115, 135);         // ≈125
        Assert.InRange(cut, 115, 135);          // ≈125
        Assert.True(Math.Abs(fill - cut) < 5, $"对称斜坡应挖≈填, 实差 {Math.Abs(fill - cut):F2}");
        Assert.InRange(net, -8, 8);             // ≈0
    }

    [Fact]
    public void Identical_surfaces_zero_volume()
    {
        var a = Surface((x, y) => 3);
        var (cut, fill, net) = TerrainAnalysis.TwoEpochVolume(a, a, 16);
        Assert.Equal(0, cut, 6);
        Assert.Equal(0, fill, 6);
        Assert.Equal(0, net, 6);
    }

    [Fact]
    public void Elevation_bands_conserve_total_cut_and_fill()
    {
        // 分标高带各带挖/填之和 == 整体挖/填（守恒，忠实原「按标高带」）。
        var a = Surface((x, y) => 0);
        var b = Surface((x, y) => x <= 5 ? 4 : -4);
        var (cut, fill, _) = TerrainAnalysis.TwoEpochVolume(a, b, 16);
        var bands = TerrainAnalysis.TwoEpochVolumeByElevation(a, b, 16, bandHeight: 0);
        double bc = 0, bf = 0;
        foreach (var band in bands) { bc += band.Cut; bf += band.Fill; }
        Assert.Equal(cut, bc, 3);
        Assert.Equal(fill, bf, 3);
    }
}
