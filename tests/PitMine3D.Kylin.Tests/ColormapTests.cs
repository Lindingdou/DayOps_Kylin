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
}
