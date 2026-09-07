using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>视口鼠标绑定：3D 默认左键旋转、选择模式下只框选/点选、2D 恒为框选、中键恒平移。</summary>
public class NavBindingTests
{
    private static ViewportPress Press(bool is2D, bool selectMode, bool shift = false, bool left = true, bool middle = false, bool selectable = true)
        => NavBinding.OnPress(left, middle, shift, is2D, selectMode, selectable);

    [Fact]
    public void ThreeD_DefaultLeftDrag_Orbits()
    {
        Assert.Equal(ViewportPress.Orbit, Press(is2D: false, selectMode: false));
        Assert.Equal(ViewportPress.Pan, Press(is2D: false, selectMode: false, left: false, middle: true));
        Assert.Equal(ViewportPress.Pan, Press(is2D: false, selectMode: true, left: false, middle: true));   // 选择模式不影响中键平移
    }

    [Fact]
    public void ThreeD_SelectMode_LeftIsBoxSelect_NeverOrbits()
    {
        Assert.Equal(ViewportPress.BoxSelect, Press(is2D: false, selectMode: true));
        Assert.Equal(ViewportPress.BoxSelect, Press(is2D: false, selectMode: true, shift: true));
        // 绘制/编辑取点等占用态下不抢左键(交给命令)
        Assert.Equal(ViewportPress.Orbit, Press(is2D: false, selectMode: true, selectable: false));
    }

    [Fact]
    public void ThreeD_ShiftLeft_TemporaryBoxSelect()
    {
        Assert.Equal(ViewportPress.BoxSelect, Press(is2D: false, selectMode: false, shift: true));
    }

    [Fact]
    public void TwoD_LeftAlwaysBoxSelect_ModeIrrelevant()
    {
        Assert.Equal(ViewportPress.BoxSelect, Press(is2D: true, selectMode: false));
        Assert.Equal(ViewportPress.BoxSelect, Press(is2D: true, selectMode: true));
        Assert.Equal(ViewportPress.BoxSelect, Press(is2D: true, selectMode: false, shift: true));
        Assert.Equal(ViewportPress.Pan, Press(is2D: true, selectMode: false, left: false, middle: true));
    }

    [Fact]
    public void RightButton_NotHandled_AndClickThreshold()
    {
        Assert.Equal(ViewportPress.None, Press(is2D: false, selectMode: false, left: false));
        Assert.True(NavBinding.IsClick(0, 0));
        Assert.True(NavBinding.IsClick(3, -3));
        Assert.False(NavBinding.IsClick(5, 0));
        Assert.False(NavBinding.IsClick(0, 9));
    }
}
