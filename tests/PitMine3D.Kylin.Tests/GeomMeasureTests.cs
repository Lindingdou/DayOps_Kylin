using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>面积/周长测量回归。</summary>
public class GeomMeasureTests
{
    [Fact]
    public void Unit_square_area_and_perimeter()
    {
        var sq = new List<(double x, double y)> { (0, 0), (1, 0), (1, 1), (0, 1) };
        Assert.Equal(1, GeomMeasure.Area(sq), 6);
        Assert.Equal(4, GeomMeasure.Perimeter(sq, true), 6);
    }

    [Fact]
    public void Triangle_area()
    {
        var tri = new List<(double x, double y)> { (0, 0), (4, 0), (0, 3) };
        Assert.Equal(6, GeomMeasure.Area(tri), 6);   // 底4高3/2
    }

    [Fact]
    public void Area_orientation_independent()
    {
        var cw = new List<(double x, double y)> { (0, 0), (0, 1), (1, 1), (1, 0) };   // 顺时针
        Assert.Equal(1, GeomMeasure.Area(cw), 6);
    }

    [Fact]
    public void Open_perimeter_excludes_closing()
    {
        var l = new List<(double x, double y)> { (0, 0), (3, 0), (3, 4) };
        Assert.Equal(7, GeomMeasure.Perimeter(l, false), 6);      // 3+4
        Assert.Equal(12, GeomMeasure.Perimeter(l, true), 6);     // +5 闭合
    }
}
