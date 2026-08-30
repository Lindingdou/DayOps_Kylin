using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>C2C 点云比对回归。</summary>
public class CloudCompareTests
{
    [Fact]
    public void Nearest_distance_3d()
    {
        var a = new List<(double x, double y, double z)> { (0, 0, 0) };
        var b = new List<(double x, double y, double z)> { (3, 4, 0), (100, 0, 0) };
        var d = CloudCompare.Distances(a, b);
        Assert.Single(d);
        Assert.Equal(5, d[0], 4);      // 到 (3,4,0)
    }

    [Fact]
    public void Per_point_and_stats()
    {
        var a = new List<(double x, double y, double z)> { (0, 0, 0), (0, 0, 10) };
        var b = new List<(double x, double y, double z)> { (0, 0, 0) };
        var d = CloudCompare.Distances(a, b);
        Assert.Equal(0, d[0], 4);
        Assert.Equal(10, d[1], 4);
        var (max, mean) = CloudCompare.Stats(d);
        Assert.Equal(10, max, 4);
        Assert.Equal(5, mean, 4);
    }
}
