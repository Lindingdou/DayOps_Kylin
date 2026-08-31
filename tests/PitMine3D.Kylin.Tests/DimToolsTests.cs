using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>线性标注回归。</summary>
public class DimToolsTests
{
    [Fact]
    public void Builds_line_ticks_and_distance_text()
    {
        var dim = DimTools.Build(0, 0, 10, 0, 1);
        // 尺寸线 + 2 端刻度 + 文字 = 4 实体
        Assert.Equal(4, dim.Count);
        var tx = Assert.IsType<TextEntity>(dim[3]);
        Assert.Equal("10", tx.Text);            // 距离文字
    }

    [Fact]
    public void Distance_text_formats_decimals()
    {
        var dim = DimTools.Build(0, 0, 3, 4, 1);   // 距离 5
        var tx = (TextEntity)dim[^1];
        Assert.Equal("5", tx.Text);
    }

    [Fact]
    public void Zero_length_only_line()
    {
        var dim = DimTools.Build(2, 2, 2, 2, 1);
        Assert.Single(dim);                        // 退化 → 只线
    }

    [Fact]
    public void BuildLinear_offset_has_extension_lines_and_offset_dimline()
    {
        var dim = DimTools.BuildLinear(0, 0, 10, 0, 5, 3, 1);   // 测(0,0)-(10,0), 尺寸线偏移过(5,3)→ y=3
        var lines = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<LineEntity>(dim));
        var txt = System.Linq.Enumerable.Single(System.Linq.Enumerable.OfType<TextEntity>(dim));
        Assert.Equal("10", txt.Text);
        // 尺寸线在偏移处 y=3, 长度=10
        Assert.Contains(lines, l => System.Math.Abs(l.Y0 - 3) < 1e-6 && System.Math.Abs(l.Y1 - 3) < 1e-6 && System.Math.Abs(System.Math.Abs(l.X1 - l.X0) - 10) < 1e-6);
        // 延伸线从测点(y≈0)跨到尺寸线+超出(y>3)
        Assert.Contains(lines, l => System.Math.Min(l.Y0, l.Y1) < 1 && System.Math.Max(l.Y0, l.Y1) > 3);
        Assert.True(txt.Y > 3);                    // 文字在偏移侧
    }

    [Fact]
    public void BuildLinear_negative_offset_flips_side()
    {
        var dim = DimTools.BuildLinear(0, 0, 10, 0, 5, -3, 1);   // 偏移到 y=-3
        var lines = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<LineEntity>(dim));
        Assert.Contains(lines, l => System.Math.Abs(l.Y0 + 3) < 1e-6 && System.Math.Abs(l.Y1 + 3) < 1e-6);   // 尺寸线 y=-3
    }

    [Fact]
    public void BuildLinearAxis_slanted_measures_x_component_when_offset_vertical()
    {
        // 斜线 (0,0)-(10,5), 偏移点 (5,8) 偏竖直 → 水平尺寸线, 量 |Δx|=10(非真距 11.18)
        var dim = DimTools.BuildLinearAxis(0, 0, 10, 5, 5, 8, 1);
        var txt = System.Linq.Enumerable.Single(System.Linq.Enumerable.OfType<TextEntity>(dim));
        Assert.Equal("10", txt.Text);              // X 分量, 非真距
        var lines = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<LineEntity>(dim));
        Assert.Contains(lines, l => System.Math.Abs(l.Y0 - 8) < 1e-6 && System.Math.Abs(l.Y1 - 8) < 1e-6);   // 水平尺寸线 y=8
    }

    [Fact]
    public void BuildLinearAxis_measures_y_component_when_offset_horizontal()
    {
        // 斜线 (0,0)-(10,5), 偏移点 (13,2) 偏水平 → 竖直尺寸线, 量 |Δy|=5
        var dim = DimTools.BuildLinearAxis(0, 0, 10, 5, 13, 2, 1);
        var txt = System.Linq.Enumerable.Single(System.Linq.Enumerable.OfType<TextEntity>(dim));
        Assert.Equal("5", txt.Text);               // Y 分量
        var lines = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<LineEntity>(dim));
        Assert.Contains(lines, l => System.Math.Abs(l.X0 - 13) < 1e-6 && System.Math.Abs(l.X1 - 13) < 1e-6);   // 竖直尺寸线 x=13
    }

    [Fact]
    public void BuildLinear_vertical_measure_offset_sideways()
    {
        var dim = DimTools.BuildLinear(0, 0, 0, 10, 3, 5, 1);   // 竖直测(0,0)-(0,10), 偏移过(3,5)→ x=3
        var lines = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<LineEntity>(dim));
        var txt = System.Linq.Enumerable.Single(System.Linq.Enumerable.OfType<TextEntity>(dim));
        Assert.Equal("10", txt.Text);
        Assert.Contains(lines, l => System.Math.Abs(l.X0 - 3) < 1e-6 && System.Math.Abs(l.X1 - 3) < 1e-6 && System.Math.Abs(System.Math.Abs(l.Y1 - l.Y0) - 10) < 1e-6);   // 尺寸线 x=3
    }

    [Fact]
    public void Radial_line_reaches_circumference_with_R_text()
    {
        // 圆心(0,0) 半径5，方向 +X → 径向线终点在 (5,0)，文字 "R5"
        var dim = DimTools.BuildRadial(0, 0, 5, 1, 0, 1);
        var line = Assert.IsType<LineEntity>(dim[0]);
        Assert.Equal(0, line.X0, 4); Assert.Equal(0, line.Y0, 4);
        Assert.Equal(5, line.X1, 4); Assert.Equal(0, line.Y1, 4);
        var tx = Assert.IsType<TextEntity>(dim[^1]);
        Assert.Equal("R5", tx.Text);
    }

    [Fact]
    public void Radial_has_arrowhead_and_text()
    {
        // 径向线 + 2 箭头线 + 文字 = 4 实体
        var dim = DimTools.BuildRadial(1, 1, 2, 0, 1, 0.5);
        Assert.Equal(4, dim.Count);
        Assert.Equal("R2", ((TextEntity)dim[^1]).Text);
    }

    // ── 标注样式(DIM 变量) ──
    [Fact]
    public void DimStyle_decimal_places_control_text_format()
    {
        var s0 = new DimStyle { DecimalPlaces = 0 };
        Assert.Equal("12", ((TextEntity)DimTools.Build(0, 0, 12.34, 0, 1, s0)[^1]).Text);   // 0 位=整数
        var s3 = new DimStyle { DecimalPlaces = 3 };
        Assert.Equal("12.34", ((TextEntity)DimTools.Build(0, 0, 12.34, 0, 1, s3)[^1]).Text); // 3 位(末尾零裁剪)
        var s1 = new DimStyle { DecimalPlaces = 1 };
        Assert.Equal("12.3", ((TextEntity)DimTools.Build(0, 0, 12.34, 0, 1, s1)[^1]).Text);  // 1 位
    }

    [Fact]
    public void DimStyle_fixed_text_height_overrides_passed_h()
    {
        var s = new DimStyle { TextHeight = 7.0 };
        var tx = (TextEntity)DimTools.Build(0, 0, 10, 0, 1.0, s)[^1];   // 传入 h=1 但固定高 7
        Assert.Equal(7.0, tx.Height, 6);
        // TextHeight=0 → 用传入 h
        var tx2 = (TextEntity)DimTools.Build(0, 0, 10, 0, 3.0, new DimStyle { TextHeight = 0 })[^1];
        Assert.Equal(3.0, tx2.Height, 6);
    }

    [Fact]
    public void DimStyle_arrow_ratio_scales_radial_arrow()
    {
        // 箭头比越大 → 箭头线越长(端点到箭底距离)。比对两种比例的箭头线长。
        double LenOf(DimStyle st)
        {
            var dim = DimTools.BuildRadial(0, 0, 10, 1, 0, 5, st);   // H=5
            var a = (LineEntity)dim[1];                               // 第一条箭头线
            double dx = a.X1 - a.X0, dy = a.Y1 - a.Y0;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }
        Assert.True(LenOf(new DimStyle { ArrowRatio = 1.0 }) > LenOf(new DimStyle { ArrowRatio = 0.3 }), "箭头比大→箭头长");
    }

    [Fact]
    public void DimStyle_default_preserves_legacy_behavior()
    {
        // 默认样式与旧行为一致(2 位裁剪 → "0.##")
        Assert.Equal(DimStyle.Default.DecimalPlaces, 2);
        Assert.Equal("2.5", ((TextEntity)DimTools.Build(0, 0, 2.5, 0, 1)[^1]).Text);
        Assert.Equal("10", ((TextEntity)DimTools.Build(0, 0, 10, 0, 1)[^1]).Text);
    }

    // ── 直径标注(DIMDIAMETER) ──
    [Fact]
    public void Diameter_line_through_center_with_diameter_text()
    {
        var dim = DimTools.BuildDiameter(0, 0, 5, 1, 0, 1);   // 半径5, +x 方向
        var line = Assert.IsType<LineEntity>(dim[0]);
        // 直径线过圆心: 端点 (5,0)↔(-5,0), 长=2r=10
        Assert.Equal(5, line.X0, 6); Assert.Equal(-5, line.X1, 6);
        double len = System.Math.Sqrt(System.Math.Pow(line.X1 - line.X0, 2) + System.Math.Pow(line.Y1 - line.Y0, 2));
        Assert.Equal(10, len, 6);
        var tx = Assert.IsType<TextEntity>(dim[^1]);
        Assert.Equal("Ø10", tx.Text);                          // 直径值=2r, 带 Ø
    }

    [Fact]
    public void Diameter_has_arrowheads_both_ends()
    {
        var dim = DimTools.BuildDiameter(0, 0, 3, 0, 1, 1);
        // 直径线(1) + 两端各 2 箭头段(4) + 文字(1) = 6
        Assert.Equal(6, dim.Count);
        Assert.Equal("Ø6", ((TextEntity)dim[^1]).Text);
    }

    // ── 角度标注(DIMANGULAR) ──
    [Fact]
    public void Angular_right_angle_is_90_degrees()
    {
        // 顶点(0,0), 射线 +x 与 +y → 90°
        var dim = DimTools.BuildAngular(0, 0, 10, 0, 0, 10, arcR: 3, h: 1);
        var tx = Assert.IsType<TextEntity>(dim[^1]);
        Assert.Equal("90°", tx.Text);
    }

    [Fact]
    public void Angular_takes_minor_arc_le_180()
    {
        // 射线夹角 270° 的两方向应取劣弧 90°(而非 270°)
        var dim = DimTools.BuildAngular(0, 0, 1, 0, 0, -1, arcR: 2, h: 1);   // +x 与 -y: 顺/劣弧 90°
        Assert.Equal("90°", ((TextEntity)dim[^1]).Text);
        // 反向对射 → 180°
        var flat = DimTools.BuildAngular(0, 0, 1, 0, -1, 0, arcR: 2, h: 1);
        Assert.Equal("180°", ((TextEntity)flat[^1]).Text);
    }

    [Fact]
    public void Angular_arc_points_lie_on_radius()
    {
        // 弧折线各端点应距顶点 = arcR
        double arcR = 4;
        var dim = DimTools.BuildAngular(0, 0, 10, 0, 0, 10, arcR, 1);
        // 前两条是延长线, 之后是弧段; 取一条弧段验证端点在半径上
        foreach (var e in dim)
            if (e is LineEntity le)
            {
                double d1 = System.Math.Sqrt(le.X1 * le.X1 + le.Y1 * le.Y1);
                // 弧段端点(非顶点、非延长线远端 4.4)应≈arcR
                if (System.Math.Abs(d1 - arcR) < 1e-6) { Assert.Equal(arcR, d1, 6); return; }
            }
        Assert.True(false, "未找到半径上的弧点");
    }

    // ── 字体新字形: Ø / ° / = 有笔画 ──
    [Fact]
    public void Font_has_diameter_degree_equals_glyphs()
    {
        Assert.NotEmpty(StrokeFont.Strokes('Ø'));
        Assert.NotEmpty(StrokeFont.Strokes('°'));
        Assert.NotEmpty(StrokeFont.Strokes('='));   // 顺带补的等号(供坐标标注)
    }
}
