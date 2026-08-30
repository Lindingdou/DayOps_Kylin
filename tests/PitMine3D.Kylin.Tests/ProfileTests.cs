using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>剖面分析（沿线高程采样）回归。</summary>
public class ProfileTests
{
    [Fact]
    public void Flat_terrain_profile_is_constant()
    {
        var section = new List<(double x, double y)> { (0, 0), (10, 0) };
        var terrain = new List<(double x, double y, double z)> { (0, 0, 5), (10, 0, 5), (0, 10, 5), (10, 10, 5) };
        var prof = Profile.Sample(section, terrain, 5);
        Assert.Equal(5, prof.Count);
        Assert.Equal(0, prof[0].dist, 4);
        Assert.Equal(10, prof[^1].dist, 4);   // 剖面长 = 线长
        Assert.All(prof, p => Assert.Equal(5, p.z, 3));   // 平地高程恒定
    }

    [Fact]
    public void Distance_monotonic_increasing()
    {
        var section = new List<(double x, double y)> { (0, 0), (3, 4) };   // 长 5
        var terrain = new List<(double x, double y, double z)> { (0, 0, 1), (3, 4, 9) };
        var prof = Profile.Sample(section, terrain, 6);
        Assert.Equal(5, prof[^1].dist, 3);
        for (int i = 1; i < prof.Count; i++) Assert.True(prof[i].dist > prof[i - 1].dist);
    }

    [Fact]
    public void Degenerate_returns_empty()
    {
        Assert.Empty(Profile.Sample(new List<(double x, double y)> { (0, 0) }, new List<(double x, double y, double z)> { (0, 0, 1) }, 5));
    }
}
