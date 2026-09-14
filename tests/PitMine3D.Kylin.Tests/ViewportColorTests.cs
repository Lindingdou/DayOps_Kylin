using PitMine3D.Kylin.Controls;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>视口配色（选项·显示）：格网明暗随背景推、夹点样式可配且默认值不变。</summary>
public class ViewportColorTests
{
    [Fact]
    public void Grid_shades_are_lighter_on_dark_and_darker_on_light_background()
    {
        var (mn, mj) = CadGlViewport.GridShades(CadGlViewport.DefaultBackground);
        Assert.True(mn.r > CadGlViewport.DefaultBackground.r && mj.r > mn.r);      // 深底: 细线比背景亮, 主线更亮
        Assert.Equal(0.300f, mj.r, 3); Assert.Equal(0.325f, mj.g, 3); Assert.Equal(0.360f, mj.b, 3);   // 与老的写死主线色一致

        var (wn, wj) = CadGlViewport.GridShades((1f, 1f, 1f));
        Assert.True(wn.r < 1f && wj.r < wn.r);                                      // 白底: 细线比背景暗, 主线更暗
        Assert.True(wn.r > 0.9f, "白底细线应是浅灰, 不能压成黑");

        var (nn, _) = CadGlViewport.GridShades(CadGlViewport.DarkThemeBackground);
        Assert.True(nn.b > CadGlViewport.DarkThemeBackground.b);                    // 海军蓝底: 提亮
    }

    [Fact]
    public void Grip_glyph_style_is_configurable_and_defaults_match_autocad()
    {
        Assert.Equal(GripGlyph.DefaultCold, GripGlyph.Color(selected: false, hovered: false));
        Assert.Equal(GripGlyph.DefaultHot, GripGlyph.Color(selected: true, hovered: false));
        Assert.Equal(GripGlyph.WarmColor, GripGlyph.Color(selected: false, hovered: true));
        var oldHalf = GripGlyph.HalfSizePx; var oldCold = GripGlyph.ColdColor;
        try
        {
            GripGlyph.HalfSizePx = 8; GripGlyph.ColdColor = (0.2f, 0.9f, 0.9f);
            Assert.Equal(8, GripGlyph.HalfSizePx);
            Assert.Equal((0.2f, 0.9f, 0.9f), GripGlyph.Color(false, false));
        }
        finally { GripGlyph.HalfSizePx = oldHalf; GripGlyph.ColdColor = oldCold; }
    }
}
