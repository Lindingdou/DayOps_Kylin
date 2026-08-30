using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>多段线合并（组合工作线）回归。</summary>
public class PolylineJoinTests
{
    private static List<(double x, double y)> P(params (double x, double y)[] p) => new(p);

    [Fact]
    public void Joins_end_to_start()
    {
        var r = PolylineJoin.Join(new[] { P((0, 0), (1, 0)), P((1, 0), (2, 0)) }, 1e-6);
        Assert.Single(r);
        Assert.Equal(3, r[0].Count);
        Assert.Equal((2, 0), r[0][2]);
    }

    [Fact]
    public void Joins_reversed_segment()
    {
        // 第二条端点反向也应连上: (0,0)-(1,0) + (2,0)-(1,0)
        var r = PolylineJoin.Join(new[] { P((0, 0), (1, 0)), P((2, 0), (1, 0)) }, 1e-6);
        Assert.Single(r);
        Assert.Equal(3, r[0].Count);
        Assert.Equal((0, 0), r[0][0]);
        Assert.Equal((2, 0), r[0][2]);
    }

    [Fact]
    public void Disjoint_stay_separate()
    {
        var r = PolylineJoin.Join(new[] { P((0, 0), (1, 0)), P((10, 10), (11, 10)) }, 1e-6);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void Prepend_when_matches_chain_start()
    {
        // 链首相接: 先 (1,0)-(2,0), 再 (0,0)-(1,0) 应前接
        var r = PolylineJoin.Join(new[] { P((1, 0), (2, 0)), P((0, 0), (1, 0)) }, 1e-6);
        Assert.Single(r);
        Assert.Equal((0, 0), r[0][0]);
        Assert.Equal((2, 0), r[0][^1]);
    }
}
