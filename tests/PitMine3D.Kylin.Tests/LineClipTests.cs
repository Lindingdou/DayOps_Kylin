using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>线对象裁剪(LineClip)回归 —— 忠实原 POLYCLIP「用闭合多段线裁剪其它线对象」。</summary>
public class LineClipTests
{
    // 单位方框边界 [0,10]²
    private static List<(double x, double y)> Square() => new()
    {
        (0, 0), (10, 0), (10, 10), (0, 10),
    };

    [Fact]
    public void Crossing_line_keeps_inside_portion()
    {
        // 水平线 (-5,5)→(15,5) 穿方框 → 界内段 (0,5)→(10,5)
        var line = new List<(double, double)> { (-5, 5), (15, 5) };
        var pieces = LineClip.ByPolygon(line, false, Square(), keepInside: true);
        Assert.Single(pieces);
        var p = pieces[0];
        Assert.Equal(0, p.First().x, 6);
        Assert.Equal(10, p.Last().x, 6);
        Assert.All(p, pt => Assert.Equal(5, pt.y, 6));
    }

    [Fact]
    public void Crossing_line_keep_outside_gives_two_pieces()
    {
        var line = new List<(double, double)> { (-5, 5), (15, 5) };
        var pieces = LineClip.ByPolygon(line, false, Square(), keepInside: false);
        Assert.Equal(2, pieces.Count);
        // 左段 (-5,5)→(0,5), 右段 (10,5)→(15,5)
        Assert.Contains(pieces, pc => System.Math.Abs(pc.First().x - (-5)) < 1e-6 && System.Math.Abs(pc.Last().x - 0) < 1e-6);
        Assert.Contains(pieces, pc => System.Math.Abs(pc.First().x - 10) < 1e-6 && System.Math.Abs(pc.Last().x - 15) < 1e-6);
    }

    [Fact]
    public void Fully_inside_line_unchanged()
    {
        var line = new List<(double, double)> { (2, 2), (8, 8) };
        var pieces = LineClip.ByPolygon(line, false, Square(), keepInside: true);
        Assert.Single(pieces);
        Assert.Equal(2, pieces[0].Count);
        Assert.Equal((2.0, 2.0), pieces[0][0]);
        Assert.Equal((8.0, 8.0), pieces[0][1]);
    }

    [Fact]
    public void Fully_outside_line_empty_when_keep_inside()
    {
        var line = new List<(double, double)> { (20, 20), (30, 30) };
        Assert.Empty(LineClip.ByPolygon(line, false, Square(), keepInside: true));
    }

    [Fact]
    public void Multi_vertex_polyline_zigzag_clipped()
    {
        // 折线 (-2,5)→(5,5)→(5,-2)→(12,-2): 只有 (-2,5)→(5,5)→(5,0) 那截的界内部分保留
        var line = new List<(double, double)> { (-2, 5), (5, 5), (5, -2), (12, -2) };
        var pieces = LineClip.ByPolygon(line, false, Square(), keepInside: true);
        Assert.Single(pieces);
        // 界内路径: (0,5)→(5,5)→(5,0)
        var p = pieces[0];
        Assert.Equal(0, p.First().x, 6);   // 进界点
        Assert.Equal((5.0, 5.0), p[1]);    // 中拐点在界内
        Assert.Equal(0, p.Last().y, 6);    // 出界点 y=0
    }

    [Fact]
    public void Nonconvex_boundary_respected()
    {
        // L 形非凸边界: 排除右上象限。线穿过被排除的凹口 → 该段落界外
        var lshape = new List<(double, double)>
        {
            (0, 0), (10, 0), (10, 4), (4, 4), (4, 10), (0, 10),
        };
        // 水平线 y=7 从 (-2,7) 到 (12,7): 界内只有 x∈[0,4](凹口右侧 x>4,y=7 在界外)
        var line = new List<(double, double)> { (-2, 7), (12, 7) };
        var pieces = LineClip.ByPolygon(line, false, lshape, keepInside: true);
        Assert.Single(pieces);
        Assert.Equal(0, pieces[0].First().x, 6);
        Assert.Equal(4, pieces[0].Last().x, 6);   // 非凸边界正确截在 x=4
    }

    [Fact]
    public void Degenerate_inputs_return_empty()
    {
        Assert.Empty(LineClip.ByPolygon(new List<(double, double)> { (0, 0) }, false, Square(), true));   // 单点
        Assert.Empty(LineClip.ByPolygon(new List<(double, double)> { (0, 0), (1, 1) }, false,
            new List<(double, double)> { (0, 0), (1, 1) }, true));   // 边界<3
    }
}
