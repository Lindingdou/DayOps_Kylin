using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>对象捕捉最近点回归。</summary>
public class SnapPointsTests
{
    // 两个顶点 (0,0) 和 (10,0)，交错 P3_C3
    private static readonly float[] V = { 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0 };

    [Fact]
    public void Finds_nearest_within_tolerance()
    {
        var s = SnapPoints.FindNearest(V, 0.5, 0.2, 1.0);
        Assert.NotNull(s);
        Assert.Equal(0, s!.Value.x, 4);
        Assert.Equal(0, s!.Value.y, 4);
    }

    [Fact]
    public void Picks_the_closer_vertex()
    {
        var s = SnapPoints.FindNearest(V, 9.7, 0.1, 1.0);
        Assert.NotNull(s);
        Assert.Equal(10, s!.Value.x, 4);
    }

    [Fact]
    public void Returns_null_outside_tolerance()
    {
        Assert.Null(SnapPoints.FindNearest(V, 5, 5, 1.0));
    }
}
