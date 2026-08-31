using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>特性面板编辑（改值→重建实体）回归。</summary>
public class EntityPropertyEditTests
{
    [Fact]
    public void Edit_line_endpoint()
    {
        var l = new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, LayerName = "L1", Cr = 0.2f };
        var e = EntityProperties.WithEdited(l, "终点", "5, 7") as LineEntity;
        Assert.NotNull(e);
        Assert.Equal(5, e!.X1, 6); Assert.Equal(7, e.Y1, 6);
        Assert.Equal(0, e.X0, 6);                         // 起点不变
        Assert.Equal("L1", e.LayerName);                  // 层保留
        Assert.Equal(0.2f, e.Cr, 3);                      // 色保留
    }

    [Fact]
    public void Edit_circle_radius()
    {
        var c = new CircleEntity { Cx = 1, Cy = 2, Radius = 3 };
        var e = EntityProperties.WithEdited(c, "半径", "8.5") as CircleEntity;
        Assert.Equal(8.5, e!.Radius, 6);
        Assert.Equal(1, e.Cx, 6);
    }

    [Fact]
    public void Edit_layer_and_color()
    {
        var c = new CircleEntity { Cx = 0, Cy = 0, Radius = 1, LayerName = "0" };
        var e1 = EntityProperties.WithEdited(c, "图层", "钻孔");
        Assert.Equal("钻孔", e1!.LayerName);
        var e2 = EntityProperties.WithEdited(c, "颜色", "#FF8000");
        Assert.Equal(1.0f, e2!.Cr, 2);
        Assert.Equal(0.5f, e2.Cg, 2);
        Assert.Equal(0.0f, e2.Cb, 2);
    }

    [Fact]
    public void Edit_text_content_and_rotation()
    {
        var t = new TextEntity { X = 0, Y = 0, Height = 2, Text = "旧", Rotation = 0 };
        var e1 = EntityProperties.WithEdited(t, "内容", "新标注") as TextEntity;
        Assert.Equal("新标注", e1!.Text);
        var e2 = EntityProperties.WithEdited(t, "旋转", "90") as TextEntity;
        Assert.Equal(System.Math.PI / 2, e2!.Rotation, 4);
    }

    [Fact]
    public void Edit_polygon_sides()
    {
        var pg = new PolygonEntity { Cx = 0, Cy = 0, Radius = 5, Sides = 6 };
        var e = EntityProperties.WithEdited(pg, "边数", "8") as PolygonEntity;
        Assert.Equal(8, e!.Sides);
    }

    [Fact]
    public void Coordinate_accepts_paren_format()
    {
        var p = new PointEntity { X = 0, Y = 0 };
        var e = EntityProperties.WithEdited(p, "坐标", "(3.5, -2)") as PointEntity;
        Assert.Equal(3.5, e!.X, 6); Assert.Equal(-2, e.Y, 6);
    }

    [Fact]
    public void Readonly_or_bad_input_returns_null()
    {
        var l = new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1 };
        Assert.Null(EntityProperties.WithEdited(l, "长度", "99"));      // 长度只读(派生)
        Assert.Null(EntityProperties.WithEdited(l, "终点", "abc"));     // 解析失败
        var c = new CircleEntity { Cx = 0, Cy = 0, Radius = 1 };
        Assert.Null(EntityProperties.WithEdited(c, "半径", "-3"));      // 负半径拒绝
        Assert.Null(EntityProperties.WithEdited(c, "颜色", "#ZZZ"));    // 坏颜色
    }

    [Fact]
    public void Editable_labels_match_type()
    {
        Assert.Contains("半径", EntityProperties.EditableLabels(new CircleEntity()));
        Assert.DoesNotContain("终点", EntityProperties.EditableLabels(new CircleEntity()));
        Assert.Contains("图层", EntityProperties.EditableLabels(new PointEntity()));   // 常规恒可编辑
    }
}
