using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 批量扩坑（按煤层层位分层放坡，托管等价原内核 BuildSeamPitMultiSeam）+ 动态调整 jig 的纯逻辑。
///   ① 顶板以上出岩台阶、顶板~底板之间出煤台阶、底板以下不出；
///   ② 顶板倾斜时同一级既有岩段也有煤段，分界处落尖灭点（在两顶点之间线性内插）；
///   ③ 整圈都在底板以下就停，不多出一级。
/// </summary>
public class SeamPitBuilderTests
{
    private sealed class Plane : IRoadZSampler
    {
        private readonly Func<double, double, double> _f;
        public Plane(Func<double, double, double> f) => _f = f;
        public bool TrySample(double x, double y, out double z) { z = _f(x, y); return true; }
    }

    private static List<(double x, double y)> Square(double half) => new() { (-half, -half), (half, -half), (half, half), (-half, half) };

    [Fact]
    public void FlatSeam_RockAboveTop_CoalBetween_NothingBelowFloor()
    {
        // 境界 Z=100；顶板 z=75、底板 z=55；H=10、α=63.43°(run=5)、W=5 ⇒ 每级平面内收 10
        var top = new Plane((_, _) => 75); var bot = new Plane((_, _) => 55);
        var r = SeamPitBuilder.Build(Square(100), 100, rockH: 10, coalH: 10, faceAngleDeg: 63.4349488, bermW: 5, top, bot);
        Assert.True(r.Ok, r.Error);
        // 级 Z: 90(岩) 80(岩) 70(煤) 60(煤) 50(底板以下→停)
        Assert.Equal(2, r.RockLevels);
        Assert.Equal(2, r.CoalLevels);
        Assert.Equal(4, r.Runs.Count);
        Assert.All(r.Runs, run => Assert.True(run.FullRing));
        Assert.Equal(new[] { false, false, true, true }, r.Runs.Select(x => x.IsCoal).ToArray());
        Assert.Equal(1, r.SkippedBelowFloor);
        Assert.Empty(r.TopPinchPoints);
    }

    [Fact]
    public void InclinedTop_SplitsLevelIntoRockAndCoal_WithPinchPoints()
    {
        // 顶板沿 x 倾斜：z = 90 + 0.2·x（x=-90 → 72，x=+90 → 108）；底板深埋 z=0
        var top = new Plane((x, _) => 90 + 0.2 * x); var bot = new Plane((_, _) => 0);
        var r = SeamPitBuilder.Build(Square(100), 100, 10, 10, 63.4349488, 5, top, bot, maxLevels: 1);
        Assert.True(r.Ok, r.Error);
        // 第一级 Z=90，坡脚环 ±95：东侧顶板 109 > 90 ⇒ 煤；西侧顶板 71 < 90 ⇒ 岩 ⇒ 同一级两段
        Assert.Contains(r.Runs, x => x.IsCoal);
        Assert.Contains(r.Runs, x => !x.IsCoal);
        Assert.All(r.Runs, x => Assert.False(x.FullRing));
        Assert.Equal(2, r.TopPinchPoints.Count);
        // 分界在 x = 0（90 = 90 + 0.2x ⇒ x=0）
        Assert.All(r.TopPinchPoints, p => Assert.InRange(p.x, -1e-6, 1e-6));
    }

    [Fact]
    public void BoundaryAlreadyBelowFloor_Fails()
    {
        var top = new Plane((_, _) => 200); var bot = new Plane((_, _) => 150);
        var r = SeamPitBuilder.Build(Square(100), 100, 10, 10, 63.4349488, 5, top, bot);
        Assert.False(r.Ok);
        Assert.Equal(1, r.SkippedBelowFloor);
    }

    [Fact]
    public void BenchJig_DistanceToLevels()
    {
        var ring = Square(100);
        Assert.Equal(0, BenchJig.DistanceToPolyline(ring, true, 100, 0), 9);
        Assert.Equal(20, BenchJig.DistanceToPolyline(ring, true, 80, 0), 9);
        // run = 10/tan63.43 + 5 = 10 ⇒ 距 0~5→1 级，25→2 级(round 2.5→2, banker's), 47→5 级；封顶 3
        Assert.Equal(1, BenchJig.LevelsForDistance(0, 10, 63.4349488, 5, 40));
        Assert.Equal(1, BenchJig.LevelsForDistance(4, 10, 63.4349488, 5, 40));
        Assert.Equal(5, BenchJig.LevelsForDistance(47, 10, 63.4349488, 5, 40));
        Assert.Equal(3, BenchJig.LevelsForDistance(47, 10, 63.4349488, 5, 3));
        Assert.Equal(1, BenchJig.LevelsForDistance(double.NaN, 10, 63.4349488, 5, 40));
    }
}
