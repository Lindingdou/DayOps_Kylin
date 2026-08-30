using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>地形分析（坡度着色）回归。</summary>
public class TerrainAnalysisTests
{
    [Fact]
    public void Flat_triangle_zero_slope()
    {
        double s = TerrainAnalysis.SlopeDegrees((0, 0, 0), (1, 0, 0), (0, 1, 0));
        Assert.Equal(0, s, 3);
    }

    [Fact]
    public void Vertical_triangle_ninety_slope()
    {
        double s = TerrainAnalysis.SlopeDegrees((0, 0, 0), (1, 0, 0), (0, 0, 1));   // XZ 平面
        Assert.Equal(90, s, 3);
    }

    [Fact]
    public void Forty_five_degree_slope()
    {
        double s = TerrainAnalysis.SlopeDegrees((0, 0, 0), (1, 0, 0), (0, 1, 1));   // 沿 Y 抬升
        Assert.Equal(45, s, 2);
    }

    [Fact]
    public void Slope_color_green_flat_red_steep()
    {
        var flat = TerrainAnalysis.SlopeColor(0);
        var steep = TerrainAnalysis.SlopeColor(60);
        Assert.True(flat.g > flat.r);      // 平=绿主导
        Assert.True(steep.r > steep.g);    // 陡=红主导
    }

    [Fact]
    public void SlopeMap_three_edges_per_triangle()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        var tris = new List<(int a, int b, int c)> { (0, 1, 2) };
        Assert.Equal(3, TerrainAnalysis.BuildSlopeMap(pts, tris).Count);
    }

    [Fact]
    public void Flat_triangle_has_no_aspect()
    {
        Assert.Equal(-1, TerrainAnalysis.AspectDegrees((0, 0, 0), (1, 0, 0), (0, 1, 0)), 3);
    }

    [Fact]
    public void Sloped_triangle_aspect_in_range()
    {
        double asp = TerrainAnalysis.AspectDegrees((0, 0, 0), (1, 0, 0), (0, 1, 1));   // 有坡
        Assert.InRange(asp, 0, 360);
    }

    [Fact]
    public void Hsv_primaries()
    {
        var red = TerrainAnalysis.HsvToRgb(0, 1, 1);
        Assert.True(red.r > 0.9f && red.g < 0.1f && red.b < 0.1f);
        var green = TerrainAnalysis.HsvToRgb(120, 1, 1);
        Assert.True(green.g > 0.9f && green.r < 0.1f);
        var blue = TerrainAnalysis.HsvToRgb(240, 1, 1);
        Assert.True(blue.b > 0.9f && blue.g < 0.1f);
    }
}
