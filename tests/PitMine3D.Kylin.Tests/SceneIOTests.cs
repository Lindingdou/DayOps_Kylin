using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>绘制场景内部格式（保存/打开）round-trip 回归。</summary>
public class SceneIOTests
{
    [Fact]
    public void Save_load_roundtrip_preserves_entities()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 5 });
        s.Add(new CircleEntity { Cx = 3, Cy = 4, Radius = 7 });
        var pl = new PolylineEntity { Closed = true };
        pl.Points.Add((0, 0)); pl.Points.Add((10, 0)); pl.Points.Add((10, 10));
        s.Add(pl);

        var s2 = SceneIO.Load(SceneIO.Save(s));

        Assert.Equal(3, s2.Count);
        var line = Assert.IsType<LineEntity>(s2.Entities[0]);
        Assert.Equal(10, line.X1); Assert.Equal(5, line.Y1);
        var circle = Assert.IsType<CircleEntity>(s2.Entities[1]);
        Assert.Equal(7, circle.Radius);
        var poly = Assert.IsType<PolylineEntity>(s2.Entities[2]);
        Assert.Equal(3, poly.Points.Count);
        Assert.True(poly.Closed);
    }

    [Fact]
    public void Load_empty_or_garbage_is_safe()
    {
        Assert.Equal(0, SceneIO.Load("[]").Count);
    }
}
