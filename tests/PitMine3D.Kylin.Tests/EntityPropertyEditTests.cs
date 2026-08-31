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
    public void Edit_preserves_full_style()
    {
        var l = new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, LayerName = "L1",
            Dash = DashPattern.ByName("虚线"), LineWeight = 25, Visible = false };
        var e = EntityProperties.WithEdited(l, "终点", "5, 7") as LineEntity;
        Assert.NotNull(e);
        Assert.Equal(new[] { 6.0, 3.0 }, e!.Dash!);   // 编辑属性不丢线型
        Assert.Equal(25, e.LineWeight);               // 不丢线宽
        Assert.False(e.Visible);                      // 不丢可见性
    }

    [Fact]
    public void Describe_shows_linetype_and_lineweight()
    {
        var l = new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0, Dash = DashPattern.ByName("虚线"), LineWeight = 25 };
        var rows = EntityProperties.Describe(l);
        Assert.Contains(rows, r => r.label == "线型" && r.value == "虚线");
        Assert.Contains(rows, r => r.label == "线宽" && r.value == "0.25 mm");
        var solid = EntityProperties.Describe(new CircleEntity { Cx = 0, Cy = 0, Radius = 1 });
        Assert.Contains(solid, r => r.label == "线型" && r.value == "实线");
        Assert.Contains(solid, r => r.label == "线宽" && r.value == "随层");   // 默认 -1
    }

    [Fact]
    public void LineWeightUtil_display_parse_snap()
    {
        Assert.Equal("随层", LineWeightUtil.Display(-1));
        Assert.Equal("默认", LineWeightUtil.Display(-3));
        Assert.Equal("0.25 mm", LineWeightUtil.Display(25));
        Assert.True(LineWeightUtil.TryParse("0.30", out short a) && a == 30);
        Assert.True(LineWeightUtil.TryParse("0.35 mm", out short b) && b == 35);
        Assert.True(LineWeightUtil.TryParse("随层", out short c) && c == -1);
        Assert.Equal(25, LineWeightUtil.Snap(26));         // 26 → 最近标准档 25
        Assert.Equal(211, LineWeightUtil.Snap(500));       // 越界钳到 211
        Assert.False(LineWeightUtil.TryParse("abc", out _));
    }

    [Fact]
    public void Edit_linetype_and_lineweight()
    {
        var l = new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0 };
        var e1 = EntityProperties.WithEdited(l, "线型", "点划线") as LineEntity;
        Assert.NotNull(e1);
        Assert.Equal(new[] { 9.0, 3.0, 0.3, 3.0 }, e1!.Dash!);
        var e2 = EntityProperties.WithEdited(l, "线宽", "0.5") as LineEntity;
        Assert.NotNull(e2);
        Assert.Equal(50, e2!.LineWeight);                 // 0.5mm → 50
        Assert.Null(EntityProperties.WithEdited(l, "线型", "波浪线"));   // 未知名拒绝
        var e3 = EntityProperties.WithEdited(l, "线型", "实线") as LineEntity;
        Assert.Null(e3!.Dash);                            // 实线 → null
    }

    [Fact]
    public void Edit_visibility_via_panel()
    {
        var l = new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0, Visible = true };
        var rows = EntityProperties.Describe(l);
        Assert.Contains(rows, r => r.label == "可见" && r.value == "是");
        var hidden = EntityProperties.WithEdited(l, "可见", "否");
        Assert.NotNull(hidden);
        Assert.False(hidden!.Visible);
        var shown = EntityProperties.WithEdited(hidden, "可见", "是");
        Assert.True(shown!.Visible);
        Assert.Null(EntityProperties.WithEdited(l, "可见", "也许"));   // 无法解析拒绝
    }

    [Fact]
    public void Edit_text_content_preserves_alignment_and_shape()
    {
        var t = new TextEntity { X = 0, Y = 0, Height = 2, Text = "旧", Rotation = 0.5,
            HAlign = 1, VAlign = 2, WidthFactor = 0.8, ObliqueAngle = 0.3 };
        var e = EntityProperties.WithEdited(t, "内容", "新") as TextEntity;
        Assert.NotNull(e);
        Assert.Equal("新", e!.Text);
        Assert.Equal(0.5, e.Rotation, 6);             // 旋转保留(改内容不动)
        Assert.Equal(1, e.HAlign); Assert.Equal(2, e.VAlign);   // 对齐保留
        Assert.Equal(0.8, e.WidthFactor, 6);          // 字宽保留
        Assert.Equal(0.3, e.ObliqueAngle, 6);         // 倾斜保留
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
