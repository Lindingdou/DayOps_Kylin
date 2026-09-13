using System;
using PitMine3D.Kylin.Cad.Dump;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>排土条带壳子体（托管等价内核 CarveDumpStripsByRails）：直段壳子体积 = 走向长 × W × 台阶高（平行四边形截面，不是梯形）。</summary>
public class DumpStripShellTests
{
    [Fact]
    public void StraightStrip_VolumeIsLengthTimesWidthTimesHeight()
    {
        // 坡顶轨 y=0 z=100，坡底轨 y=10 z=80（坡面投影 10m，台阶高 20m），走向长 100m，W=40
        var crest = new double[] { 0, 0, 100, 50, 0, 100, 100, 0, 100 };
        var toe = new double[] { 0, 10, 80, 50, 10, 80, 100, 10, 80 };
        var m = DumpStripShell.Build(crest, toe, 40);
        Assert.True(m.Ok, m.Error);
        Assert.Equal(100 * 40 * 20, m.VolumeM3, 0);
        Assert.Equal(12, m.Verts.Count);
        Assert.Equal(2 * 4 * 2 + 4, m.Tris.Count);   // 2 段 × 4 面 × 2 三角 + 两端各 2
    }

    [Fact]
    public void Resample_KeepsEndpoints_AndSpacesByArcLength()
    {
        var pts = DumpStripShell.Resample(new double[] { 0, 0, 0, 10, 0, 0, 10, 10, 0 }, 5);
        Assert.Equal(5, pts.Count);
        Assert.Equal((0, 0, 0), pts[0]);
        Assert.Equal((10, 10, 0), pts[^1]);
        Assert.Equal(10, pts[2].x, 6); Assert.Equal(0, pts[2].y, 6);   // 中点落在拐角
    }

    [Fact]
    public void BadInput_ReportsError()
    {
        Assert.False(DumpStripShell.Build(new double[] { 0, 0, 0 }, new double[] { 0, 1, 0 }, 40).Ok);
        Assert.False(DumpStripShell.Build(new double[] { 0, 0, 0, 1, 0, 0 }, new double[] { 0, 1, 0, 1, 1, 0 }, 0).Ok);
    }
}
