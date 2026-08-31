using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>坐标标注(DimTools.BuildCoordLabel)回归 —— 忠实原 CAD「坐标标注」注记(点→引线+X/Y 文字)。</summary>
public class CoordLabelTests
{
    [Fact]
    public void Text_shows_formatted_xy()
    {
        var ents = DimTools.BuildCoordLabel(100, 200, 10, 10, 1.0);
        var txt = ents.OfType<TextEntity>().Single();
        Assert.Equal("X=100 Y=200", txt.Text);   // 默认 0.## → 整数去零
    }

    [Fact]
    public void Decimal_places_follow_dimstyle()
    {
        var style = new DimStyle { DecimalPlaces = 3 };
        var txt = DimTools.BuildCoordLabel(12.3456, 7.8, 5, 5, 1.0, style).OfType<TextEntity>().Single();
        Assert.Equal("X=12.346 Y=7.8", txt.Text);   // 3 位, 末零裁剪
    }

    [Fact]
    public void Optional_z_appended()
    {
        var txt = DimTools.BuildCoordLabel(1, 2, 3, 3, 1.0, null, z: 50).OfType<TextEntity>().Single();
        Assert.Equal("X=1 Y=2 Z=50", txt.Text);
    }

    [Fact]
    public void Leader_connects_point_to_anchor()
    {
        var ents = DimTools.BuildCoordLabel(100, 200, 10, 20, 1.0);
        // 引线 = 从点(100,200)到锚点(110,220)的直线
        var leader = ents.OfType<LineEntity>().FirstOrDefault(l =>
            System.Math.Abs(l.X0 - 100) < 1e-9 && System.Math.Abs(l.Y0 - 200) < 1e-9 &&
            System.Math.Abs(l.X1 - 110) < 1e-9 && System.Math.Abs(l.Y1 - 220) < 1e-9);
        Assert.NotNull(leader);
    }

    [Fact]
    public void Has_cross_marker_at_point()
    {
        var ents = DimTools.BuildCoordLabel(100, 200, 10, 10, 2.0);
        double m = 2.0 * 0.4;   // H*0.4
        // 水平臂: (100-m,200)->(100+m,200)
        Assert.Contains(ents.OfType<LineEntity>(), l =>
            System.Math.Abs(l.X0 - (100 - m)) < 1e-9 && System.Math.Abs(l.X1 - (100 + m)) < 1e-9 &&
            System.Math.Abs(l.Y0 - 200) < 1e-9 && System.Math.Abs(l.Y1 - 200) < 1e-9);
        // 竖直臂: (100,200-m)->(100,200+m)
        Assert.Contains(ents.OfType<LineEntity>(), l =>
            System.Math.Abs(l.Y0 - (200 - m)) < 1e-9 && System.Math.Abs(l.Y1 - (200 + m)) < 1e-9 &&
            System.Math.Abs(l.X0 - 100) < 1e-9 && System.Math.Abs(l.X1 - 100) < 1e-9);
    }

    [Fact]
    public void Left_leader_shifts_text_left_for_right_align()
    {
        // 引线朝右: 文字 X 在锚点右侧; 引线朝左: 文字 X 在锚点左侧(留出文字宽)
        var right = DimTools.BuildCoordLabel(0, 0, 10, 0, 1.0).OfType<TextEntity>().Single();
        var left = DimTools.BuildCoordLabel(0, 0, -10, 0, 1.0).OfType<TextEntity>().Single();
        Assert.True(right.X > 10, "朝右: 文字在锚点(10)右");
        Assert.True(left.X < -10, "朝左: 文字在锚点(-10)左(右对齐)");
    }
}
