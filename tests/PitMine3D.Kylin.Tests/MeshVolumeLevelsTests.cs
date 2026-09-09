using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 三角网体积「解析散度法 + 分标高」回归：长方体 / 四棱锥的分层量都有解析解；
/// 并验"逐层求和 == 整体体积"这条自检恒等式（面板里给用户看的就是它的残差）。
/// </summary>
public class MeshVolumeLevelsTests
{
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Box(
        double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var v = new List<(double, double, double)>
        {
            (x0,y0,z0),(x1,y0,z0),(x1,y1,z0),(x0,y1,z0),
            (x0,y0,z1),(x1,y0,z1),(x1,y1,z1),(x0,y1,z1),
        };
        var t = new List<(int, int, int)>
        {
            (0,2,1),(0,3,2), (4,5,6),(4,6,7),
            (0,1,5),(0,5,4), (2,3,7),(2,7,6),
            (3,0,4),(3,4,7), (1,2,6),(1,6,5),
        };
        return (v, t);
    }

    [Fact]
    public void 某标高以下体积_长方体线性增长()
    {
        var b = Box(0, 0, 0, 10, 10, 10);
        Assert.Equal(0.0, MeshVolumeLevels.VolumeBelow(b.v, b.t, 0), 6);
        Assert.Equal(300.0, MeshVolumeLevels.VolumeBelow(b.v, b.t, 3), 6);
        Assert.Equal(1000.0, MeshVolumeLevels.VolumeBelow(b.v, b.t, 10), 6);
        Assert.Equal(1000.0, MeshVolumeLevels.VolumeBelow(b.v, b.t, 99), 6);   // 超出顶面不再增长
    }

    [Fact]
    public void 分标高报量_每层等厚等量且求和等于整体()
    {
        var b = Box(0, 0, 0, 10, 10, 10);
        var r = MeshVolumeLevels.Compute(b.v, b.t, new double[] { 0, 2.5, 5, 7.5, 10 });
        Assert.True(r.Closed);
        Assert.Equal(1000.0, r.TotalVolume, 6);
        Assert.Equal(4, r.Bands.Count);
        foreach (var band in r.Bands) Assert.Equal(250.0, band.Volume, 6);
        Assert.Equal(r.TotalVolume, r.BandSum, 6);                              // 自检恒等式
    }

    [Fact]
    public void 分标高报量_四棱锥按解析解分层()
    {
        // 底 10×10 于 z=0，顶点 (5,5,10)：总体积 = 1/3·100·10 = 333.333…
        // z 以下体积 = ∫0^z A(h) dh，A(h) = 100·(1−h/10)² → V(z) = 1000/3·(1−(1−z/10)³)
        var v = new List<(double x, double y, double z)>
        { (0,0,0),(10,0,0),(10,10,0),(0,10,0),(5,5,10) };
        var t = new List<(int a, int b, int c)>
        {
            (0,2,1),(0,3,2),                       // 底 (-Z)
            (0,1,4),(1,2,4),(2,3,4),(3,0,4),       // 四个侧面
        };
        double Exact(double z) => 1000.0 / 3.0 * (1 - System.Math.Pow(1 - z / 10.0, 3));

        Assert.Equal(1000.0 / 3.0, MeshMetrics.RobustVolume(v, t), 6);
        Assert.Equal(Exact(4), MeshVolumeLevels.VolumeBelow(v, t, 4), 6);
        Assert.Equal(Exact(7), MeshVolumeLevels.VolumeBelow(v, t, 7), 6);

        var r = MeshVolumeLevels.Compute(v, t, new double[] { 0, 2, 4, 6, 8, 10 });
        Assert.Equal(5, r.Bands.Count);
        Assert.Equal(Exact(4) - Exact(2), r.Bands[1].Volume, 6);
        Assert.Equal(r.TotalVolume, r.BandSum, 6);
        // 下层比上层厚重（锥体越往上越细）
        Assert.True(r.Bands[0].Volume > r.Bands[^1].Volume);
    }

    [Fact]
    public void 等间距标高_对齐到间距整数倍且覆盖全高()
    {
        var levels = MeshVolumeLevels.EvenLevels(1023.4, 1067.2, 10);
        Assert.Equal(1020.0, levels[0]);                       // 向下对齐
        Assert.True(levels[^1] >= 1067.2);                     // 覆盖顶
        Assert.True(levels.Zip(levels.Skip(1)).All(p => p.Second > p.First));
    }

    [Fact]
    public void 空标高_只报整体不分层()
    {
        var b = Box(0, 0, 0, 2, 2, 2);
        var r = MeshVolumeLevels.Compute(b.v, b.t, null);
        Assert.Equal(8.0, r.TotalVolume, 6);
        Assert.Empty(r.Bands);
    }
}
