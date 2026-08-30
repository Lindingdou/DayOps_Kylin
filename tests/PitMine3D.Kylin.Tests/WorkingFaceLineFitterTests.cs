using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>工作面线拟合回归（移植自 BlockModelLib.WorkingFaceLineFitter）。</summary>
public class WorkingFaceLineFitterTests
{
    [Fact]
    public void Diagonal_points_give_45deg_strike_one_segment()
    {
        var pts = new List<(double X, double Y)>
        { (0, 0), (10, 10), (20, 20), (30, 30), (40, 40), (50, 50) };   // y=x, 间距 14m < LinkDist
        var r = WorkingFaceLineFitter.Fit(pts);
        Assert.True(r.Ok, r.Message);
        Assert.Single(r.Segments);
        Assert.Equal(45.0, r.StrikeAzimuthDeg, 1);
    }

    [Fact]
    public void East_west_line_gives_90deg_strike()
    {
        var pts = new List<(double X, double Y)>
        { (0, 0), (10, 0), (20, 0), (30, 0), (40, 0), (50, 0) };        // 沿 X 轴 → 走向 90°(正东)
        var r = WorkingFaceLineFitter.Fit(pts);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(90.0, r.StrikeAzimuthDeg, 1);
    }

    [Fact]
    public void Isolated_speck_is_dropped()
    {
        var pts = new List<(double X, double Y)>
        { (0, 0), (10, 10), (20, 20), (30, 30), (40, 40), (50, 50),     // 主簇 6 点
          (200, 200) };                                                 // 远处孤立点(沿走向间隔 >LinkDist)
        var r = WorkingFaceLineFitter.Fit(pts);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, r.DroppedSpeckle);                              // 孤立点被当斑点丢弃
        Assert.Single(r.Segments);
    }

    [Fact]
    public void Too_few_points_fails_gracefully()
    {
        var r = WorkingFaceLineFitter.Fit(new List<(double X, double Y)> { (0, 0) });
        Assert.False(r.Ok);
        Assert.Empty(r.Segments);
    }
}
