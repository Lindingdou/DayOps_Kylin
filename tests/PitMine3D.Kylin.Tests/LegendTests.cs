using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>色带图例 —— 色条渐变 + 值标签 生成回归。</summary>
[Collection("TextGeometry")]
public class LegendTests
{
    [Fact]
    public void Build_produces_color_strip_and_value_labels()
    {
        var cmap = Colormap.Terrain;
        var ents = Legend.Build(cmap, 10, 50, 0, 0, 2, 20, 1);

        var texts = ents.OfType<TextEntity>().ToList();
        var lines = ents.OfType<LineEntity>().ToList();
        Assert.Equal(3, texts.Count);                       // min/中/max 三标签
        Assert.Contains(texts, t => t.Text == "10");        // 底=min
        Assert.Contains(texts, t => t.Text == "30");        // 中=(10+50)/2
        Assert.Contains(texts, t => t.Text == "50");        // 顶=max
        Assert.True(lines.Count >= 40);                     // 色条≥40 带 + 边框
        // 色条底色≠顶色(渐变)
        var bottom = lines.OrderBy(l => l.Y0).First();
        var top = lines.OrderByDescending(l => l.Y0).First(l => l.Cr < 0.85f || l.Cg < 0.85f || l.Cb < 0.85f);
        Assert.True(bottom.Cr != top.Cr || bottom.Cg != top.Cg || bottom.Cb != top.Cb);
    }

    [Fact]
    public void Build_empty_colormap_or_zero_size_returns_empty()
    {
        Assert.Empty(Legend.Build(System.Array.Empty<(byte, byte, byte)>(), 0, 1, 0, 0, 2, 10, 1));
        Assert.Empty(Legend.Build(Colormap.Terrain, 0, 1, 0, 0, 2, 0, 1));   // 高度 0
    }
}
