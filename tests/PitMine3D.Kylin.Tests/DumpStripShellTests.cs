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

/// <summary>排土场容量校核算量核：填方 = 坡面高出现状面的柱体；穿地部分记挖方不静默；只算两面都采到的格。</summary>
public class DumpCapacityCalcTests
{
    private sealed class Plane : PitMine3D.Kylin.Cad.IRoadZSampler
    {
        private readonly Func<double, double, double?> _f;
        public Plane(Func<double, double, double?> f) => _f = f;
        public bool TrySample(double x, double y, out double z) { var v = _f(x, y); z = v ?? 0; return v.HasValue; }
    }

    [Fact]
    public void FlatDump_FillEqualsAreaTimesHeight()
    {
        var terrain = new Plane((_, _) => 100);
        var face = new Plane((_, _) => 110);
        var r = DumpCapacityCalc.Compute(terrain, face, 0, 0, 100, 50, 2, 0.05);
        Assert.True(r.Ok);
        Assert.Equal(100 * 50 * 10, r.FillM3, 0);
        Assert.Equal(0, r.CutM3, 6);
        Assert.Equal(5000, r.AreaFillM2, 0);
    }

    [Fact]
    public void FaceBelowTerrain_CountsAsCut_AndUnsampledCellsSkipped()
    {
        var terrain = new Plane((x, _) => x < 50 ? 100 : null);   // 东半边没有现状面
        var face = new Plane((_, _) => 95);
        var r = DumpCapacityCalc.Compute(terrain, face, 0, 0, 100, 50, 2, 0.05);
        Assert.Equal(50 * 50 * 5, r.CutM3, 0);
        Assert.Equal(0, r.FillM3, 6);
        Assert.Equal(r.Cells / 2, r.Sampled);
    }
}
