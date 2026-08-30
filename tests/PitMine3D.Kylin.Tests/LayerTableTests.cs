using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>图层表回归。</summary>
public class LayerTableTests
{
    [Fact]
    public void Default_layer_is_zero_and_current()
    {
        var t = new LayerTable();
        Assert.Single(t.Layers);
        Assert.Equal("0", t.Current.Name);
    }

    [Fact]
    public void New_adds_and_sets_current_distinct_color()
    {
        var t = new LayerTable();
        var c0 = (t.Current.Cr, t.Current.Cg, t.Current.Cb);
        var l = t.New();
        Assert.Equal(2, t.Layers.Count);
        Assert.Same(l, t.Current);
        Assert.NotEqual(c0, (l.Cr, l.Cg, l.Cb));   // 轮转配色不同
    }

    [Fact]
    public void SetCurrent_and_cycle()
    {
        var t = new LayerTable();
        t.New("A"); t.New("B");
        Assert.True(t.SetCurrent("A"));
        Assert.Equal("A", t.Current.Name);
        Assert.NotEqual("A", t.CycleCurrent().Name);
    }

    [Fact]
    public void Remove_default_blocked_current_falls_back()
    {
        var t = new LayerTable();
        t.New("A");                    // current = A
        Assert.False(t.Remove("0"));   // 默认层不可删
        Assert.True(t.Remove("A"));    // 删当前 → 回落到 "0"
        Assert.Equal("0", t.Current.Name);
    }
}
