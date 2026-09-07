using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>实体特性提取回归。</summary>
[Collection("TextGeometry")]
public class EntityPropertiesTests
{
    private static string Val(System.Collections.Generic.List<(string cat, string label, string value)> r, string label)
        => r.First(x => x.label == label).value;

    [Fact]
    public void Circle_has_common_and_geometry()
    {
        var r = EntityProperties.Describe(new CircleEntity { Cx = 3, Cy = 4, Radius = 5, LayerName = "墙", Cr = 1, Cg = 0, Cb = 0 });
        Assert.Equal("圆", Val(r, "类型"));
        Assert.Equal("墙", Val(r, "图层"));
        Assert.Equal("#FF0000", Val(r, "颜色"));
        Assert.Equal("(3, 4)", Val(r, "圆心"));
        Assert.Equal("5", Val(r, "半径"));
    }

    [Fact]
    public void Line_reports_length()
    {
        var r = EntityProperties.Describe(new LineEntity { X0 = 0, Y0 = 0, X1 = 3, Y1 = 4 });
        Assert.Equal("5", Val(r, "长度"));     // 3-4-5
    }

    [Fact]
    public void Polyline_reports_closed_and_vertex_count()
    {
        var pl = new PolylineEntity { Closed = true };
        pl.Points.Add((0, 0)); pl.Points.Add((1, 0)); pl.Points.Add((1, 1));
        var r = EntityProperties.Describe(pl);
        Assert.Equal("是", Val(r, "闭合"));
        Assert.Equal("3", Val(r, "顶点数"));
    }

    [Fact]
    public void Text_reports_content_and_rotation()
    {
        var r = EntityProperties.Describe(new TextEntity { X = 1, Y = 2, Height = 2.5, Text = "ZK1", Rotation = System.Math.PI / 2 });
        Assert.Equal("ZK1", Val(r, "内容"));
        Assert.Equal("90°", Val(r, "旋转"));
    }
}
