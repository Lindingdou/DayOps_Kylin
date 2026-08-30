using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>台阶参数分析回归（移植自 PointCloudLib.BenchAnalyzer）。</summary>
public class BenchAnalyzerTests
{
    [Fact]
    public void Staircase_splits_berm_and_face()
    {
        // 平盘(0→10 平) → 坡面(10→11 落 10) → 平盘(11→21 平)
        var d = new List<double> { 0, 10, 11, 21 };
        var z = new List<double> { 10, 10, 0, 0 };
        var r = BenchAnalyzer.Analyze(d, z);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(1, r.FaceCount);
        Assert.Equal(2, r.BermCount);
        var face = r.Rows.First(x => x.Kind == "坡面");
        Assert.Equal(10, face.Height, 4);
        Assert.Equal(Math.Atan(10.0) * 180 / Math.PI, face.FaceAngleDeg, 3);   // 陡, ~84.3°
        Assert.Equal(1, face.Width, 4);    // 坡面水平投影 d 10→11 = 1
    }

    [Fact]
    public void Face_width_is_horizontal_run()
    {
        var d = new List<double> { 0, 10, 11, 21 };
        var z = new List<double> { 10, 10, 0, 0 };
        var face = BenchAnalyzer.Analyze(d, z).Rows.First(x => x.Kind == "坡面");
        Assert.Equal(1, face.Width, 4);    // 10→11 水平跨度 1
    }

    [Fact]
    public void Overall_slope_uses_total_run_and_height()
    {
        var d = new List<double> { 0, 10, 11, 21 };
        var z = new List<double> { 10, 10, 0, 0 };
        var r = BenchAnalyzer.Analyze(d, z);
        Assert.Equal(21, r.TotalRun, 4);
        Assert.Equal(10, r.TotalHeight, 4);
        Assert.Equal(Math.Atan(10.0 / 21.0) * 180 / Math.PI, r.OverallSlopeDeg, 3);
    }

    [Fact]
    public void Too_few_points_empty()
    {
        Assert.Empty(BenchAnalyzer.Analyze(new List<double> { 0 }, new List<double> { 0 }).Rows);
    }
}
