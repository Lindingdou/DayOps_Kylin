using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网竖直剖面回归（MeshPlaneSection：网格∩竖直面→精确断面）。</summary>
public class MeshPlaneSectionTests
{
    // 斜面 z=x 的 [0,10]² 方形(2 三角)
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) TiltZeqX()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 10), (10, 10, 10), (0, 10, 0) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return (v, t);
    }

    [Fact]
    public void Section_of_tilted_plane_gives_linear_profile()
    {
        var (v, t) = TiltZeqX();
        // 剖面线 (0,5)→(10,5)：沿线 dist=x, 斜面 z=x → 剖面 z≈dist
        var prof = MeshPlaneSection.Profile(v, t, (0, 5), (10, 5));
        Assert.NotEmpty(prof);
        foreach (var (dist, z) in prof) Assert.Equal(dist, z, 4);      // z=x=dist 精确
        // 覆盖 [0,10]
        Assert.True(prof.First().dist < 1.0 && prof.Last().dist > 9.0);
    }

    [Fact]
    public void Section_off_the_mesh_is_empty()
    {
        var (v, t) = TiltZeqX();
        var prof = MeshPlaneSection.Profile(v, t, (100, 100), (110, 100));   // 网外
        Assert.Empty(prof);
    }

    [Fact]
    public void Degenerate_section_line_empty()
    {
        var (v, t) = TiltZeqX();
        Assert.Empty(MeshPlaneSection.Profile(v, t, (5, 5), (5, 5)));        // 零长剖面线
    }
}
