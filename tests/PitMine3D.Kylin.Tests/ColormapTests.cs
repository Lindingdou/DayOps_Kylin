using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>色带插值回归（移植自 TinColormap 锚点线性插值核）。</summary>
public class ColormapTests
{
    [Fact]
    public void Ends_map_to_first_and_last_stop()
    {
        Assert.Equal((byte)0, Colormap.Sample(Colormap.Terrain, 0).r);
        Assert.Equal((byte)97, Colormap.Sample(Colormap.Terrain, 0).g);
        Assert.Equal((byte)255, Colormap.Sample(Colormap.Terrain, 1).r);   // 末锚点 白
        Assert.Equal((byte)255, Colormap.Sample(Colormap.Terrain, 1).b);
    }

    [Fact]
    public void Grayscale_midpoint_is_mid_gray()
    {
        var (r, g, b) = Colormap.Sample(Colormap.Grayscale, 0.5);
        Assert.Equal((byte)127, r);   // 0 + 255*0.5 = 127.5 → (byte)127
        Assert.Equal((byte)127, g);
        Assert.Equal((byte)127, b);
    }

    [Fact]
    public void Out_of_range_clamps()
    {
        Assert.Equal(Colormap.Sample(Colormap.Jet, 0), Colormap.Sample(Colormap.Jet, -1));
        Assert.Equal(Colormap.Sample(Colormap.Jet, 1), Colormap.Sample(Colormap.Jet, 2));
    }

    [Fact]
    public void Empty_stops_white()
    {
        Assert.Equal(((byte)255, (byte)255, (byte)255), Colormap.Sample(new (byte, byte, byte)[0], 0.5));
    }

    // ── 感知均匀色带 + ByName ──
    [Fact]
    public void Perceptual_colormaps_endpoints_correct()
    {
        Assert.Equal(((byte)68, (byte)1, (byte)84), Colormap.Sample(Colormap.Viridis, 0));    // Viridis 起=深紫
        Assert.Equal(((byte)253, (byte)231, (byte)37), Colormap.Sample(Colormap.Viridis, 1)); // 终=黄
        Assert.Equal(((byte)0, (byte)0, (byte)4), Colormap.Sample(Colormap.Magma, 0));         // Magma 起≈黑
        Assert.Equal(((byte)240, (byte)249, (byte)33), Colormap.Sample(Colormap.Plasma, 1));   // Plasma 终=黄
        Assert.True(Colormap.Turbo.Length >= 4);
    }

    [Fact]
    public void ByName_dispatches_and_defaults()
    {
        Assert.Same(Colormap.Viridis, Colormap.ByName("viridis"));
        Assert.Same(Colormap.Viridis, Colormap.ByName("VIRIDIS"));   // 大小写不敏感
        Assert.Same(Colormap.Turbo, Colormap.ByName("Turbo"));
        Assert.Same(Colormap.Jet, Colormap.ByName("jet"));
        Assert.Same(Colormap.Terrain, Colormap.ByName("unknown"));   // 未知→Terrain
    }
}
