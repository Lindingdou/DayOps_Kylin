using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>坐标转换(Helmert 4参)回归。</summary>
public class CoordTransformTests
{
    [Fact]
    public void Pure_translation()
    {
        var pairs = new List<(double sx, double sy, double dx, double dy)>
        {
            (0, 0, 10, 5), (1, 0, 11, 5), (0, 1, 10, 6)
        };
        var h = CoordTransform.Solve(pairs)!.Value;
        Assert.Equal(1, h.a, 4); Assert.Equal(0, h.b, 4);
        Assert.Equal(10, h.tx, 4); Assert.Equal(5, h.ty, 4);
    }

    [Fact]
    public void Scale_by_two()
    {
        var pairs = new List<(double sx, double sy, double dx, double dy)>
        {
            (0, 0, 0, 0), (1, 0, 2, 0), (0, 1, 0, 2)
        };
        var h = CoordTransform.Solve(pairs)!.Value;
        Assert.Equal(2, h.a, 4); Assert.Equal(0, h.b, 4);
    }

    [Fact]
    public void Rotation_90()
    {
        // (1,0)->(0,1), (0,1)->(-1,0): 90° 旋转 → a=0,b=1
        var pairs = new List<(double sx, double sy, double dx, double dy)>
        {
            (0, 0, 0, 0), (1, 0, 0, 1), (0, 1, -1, 0)
        };
        var h = CoordTransform.Solve(pairs)!.Value;
        Assert.Equal(0, h.a, 4); Assert.Equal(1, h.b, 4);
    }

    [Fact]
    public void Too_few_null()
    {
        Assert.Null(CoordTransform.Solve(new List<(double sx, double sy, double dx, double dy)> { (0, 0, 1, 1) }));
    }

    [Fact]
    public void ToAffine_applies_to_entity()
    {
        var h = (a: 1.0, b: 0.0, tx: 10.0, ty: 5.0);
        var m = CoordTransform.ToAffine(h);
        var line = (LineEntity)new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0 }.Apply(m);
        Assert.Equal(10, line.X0, 4); Assert.Equal(5, line.Y0, 4);
    }
}
