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
}
