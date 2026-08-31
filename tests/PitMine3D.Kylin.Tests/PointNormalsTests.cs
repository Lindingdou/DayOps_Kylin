using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>逐点法向/坡度坡向回归（k 近邻 PCA 局部平面）。</summary>
public class PointNormalsTests
{
    // 水平面 z=const 的网格点
    private static List<(double x, double y, double z)> FlatGrid(int n, double z)
    {
        var pts = new List<(double, double, double)>();
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) pts.Add((i, j, z));
        return pts;
    }

    [Fact]
    public void Flat_surface_zero_slope_up_normal()
    {
        var pts = FlatGrid(6, 100);
        var a = PointNormals.Compute(pts, 8);
        Assert.Equal(pts.Count, a.Count);
        // 内部点(远离边界)坡度≈0、法向≈+Z
        int mid = 3 * 6 + 3;   // (3,3)
        Assert.Equal(0.0, a[mid].slope, 3);
        Assert.Equal(1.0, a[mid].n.z, 3);
    }

    [Fact]
    public void Tilted_plane_slope_matches_tilt()
    {
        // 斜面 z = tan(30°)·x → 坡度应≈30°
        double t = Math.Tan(30 * Math.PI / 180);
        var pts = new List<(double x, double y, double z)>();
        for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) pts.Add((i, j, t * i));
        var a = PointNormals.Compute(pts, 10);
        int mid = 4 * 8 + 4;
        Assert.Equal(30.0, a[mid].slope, 1);   // 坡度≈30°
    }

    [Fact]
    public void Too_few_points_empty()
    {
        Assert.Empty(PointNormals.Compute(new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0) }, 8));
    }
}
