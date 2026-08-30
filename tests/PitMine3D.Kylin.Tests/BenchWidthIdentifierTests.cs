using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>平盘宽度识别集成回归（合成平盘台阶线）。</summary>
public class BenchWidthIdentifierTests
{
    // 200×200 等高(z=100)方形台阶线, 其内部 DEM 填成常值 → 一整块宽平盘
    private static List<double[]> Square200()
        => new() { new double[] { 0, 0, 100, 200, 0, 100, 200, 200, 100, 0, 200, 100, 0, 0, 100 } };

    [Fact]
    public void Finds_wide_bench_in_flat_square()
    {
        var r = BenchWidthIdentifier.Identify(Square200(), wTargetM: 20.0);
        Assert.True(r.Ok);
        Assert.True(r.Regions.Count >= 1);
        var big = r.Regions[0];
        Assert.True(big.RepWidthM >= 20.0);        // 代表宽度达标
        Assert.True(big.AreaHa > 1.0);             // 一整块宽平盘(面积随形态学膨胀而定, 仅作下限校核)
    }

    [Fact]
    public void Empty_input_is_ok_false_not_throw()
    {
        var r = BenchWidthIdentifier.Identify(new List<double[]>(), wTargetM: 20.0);
        Assert.False(r.Ok);
        Assert.Contains("台阶线", r.Message);
    }

    [Fact]
    public void Nonpositive_target_rejected()
    {
        var r = BenchWidthIdentifier.Identify(Square200(), wTargetM: 0);
        Assert.False(r.Ok);
    }
}
