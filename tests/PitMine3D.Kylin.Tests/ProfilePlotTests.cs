using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>剖面图框架（里程/标高 轴+网格+刻度）回归。</summary>
[Collection("TextGeometry")]
public class ProfilePlotTests
{
    [Fact]
    public void Frame_has_box_axes_names_and_value_labels()
    {
        // 里程 0..500, 标高 100..150, 曲线原点 (0,0) → 标高 100 画在 y=0
        var ents = ProfilePlot.Frame(0, 0, 500, 100, 150, 5);
        var texts = ents.OfType<TextEntity>().ToList();
        var lines = ents.OfType<LineEntity>().ToList();

        Assert.Contains(texts, t => t.Text == "里程");           // X 轴名
        Assert.Contains(texts, t => t.Text == "标高");           // Y 轴名
        Assert.Contains(texts, t => t.Text == "0");              // 里程起点
        Assert.Contains(texts, t => t.Text == "100");            // 标高最低(真实值, 非 y 坐标 0)
        Assert.True(lines.Count >= 4);                           // ≥外框 4 边
        // 标高刻度值是真实标高(100..150), 画在 y = z-100
        Assert.Contains(texts, t => t.Text == "150");
    }

    [Fact]
    public void Frame_degenerate_returns_empty()
    {
        Assert.Empty(ProfilePlot.Frame(0, 0, 0, 100, 150, 5));      // 零里程
        Assert.Empty(ProfilePlot.Frame(0, 0, 500, 150, 150, 5));    // 零标高域
    }
}
