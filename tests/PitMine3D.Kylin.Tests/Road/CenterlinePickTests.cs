// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/CenterlinePickTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System;
using System.Collections.Generic;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 「中心线管理」视口点选判定的口径测试。删线是不可逆动作（要 Ctrl+Z 才回得来），
/// 所以判的是三件事：<b>点中的确实是最近那条</b>、<b>点空处不许命中</b>、
/// <b>线段中部也点得中</b>（提取出来的中线一段能几十米长，只按顶点判会让整段点不着）。
/// </summary>
public class CenterlinePickTests
{
    private static readonly double[] North = { 0, 0, 100, 100, 0, 100 };     // y=0 那条
    private static readonly double[] South = { 0, 200, 100, 100, 200, 100 }; // y=200 那条

    private static List<double[]> Pool() => new() { North, South };

    // ── 命中最近的那条：同一份候选，点挪到另一侧就得换一条（不是恒返回第 0 条）──

    [Theory]
    [InlineData(50.0, 10.0, 0)]    // 贴 y=0
    [InlineData(50.0, 190.0, 1)]   // 贴 y=200
    public void NearestIndex_PicksTheCloserLine(double px, double py, int expected)
    {
        int i = CenterlinePick.NearestIndex(px, py, Pool(), maxDistM: 50.0, out double d);
        Assert.Equal(expected, i);
        Assert.Equal(10.0, d, 6);
    }

    // ── 半径外不命中：点空处不能顺手删掉几百米外那条最近的路 ──

    [Fact]
    public void NearestIndex_BeyondRadius_Misses()
    {
        Assert.Equal(-1, CenterlinePick.NearestIndex(50, 60, Pool(), maxDistM: 50.0, out double d));
        Assert.True(double.IsNaN(d));

        // 对照：同一个点，半径放到 60m 就该命中 —— 证明上面的 -1 是半径挡的，不是判定本身空过
        Assert.Equal(0, CenterlinePick.NearestIndex(50, 60, Pool(), maxDistM: 60.0, out double d2));
        Assert.Equal(60.0, d2, 6);
    }

    // ── 判的是到线段的垂距，不是到顶点 ──

    [Fact]
    public void NearestIndex_MeasuresToSegment_NotToVertices()
    {
        var longLine = new List<double[]> { new double[] { 0, 0, 100, 1000, 0, 100 } };
        // 点在段正中偏 5m：到两端顶点都是 ~500m，到线段只有 5m
        int i = CenterlinePick.NearestIndex(500, 5, longLine, maxDistM: 20.0, out double d);
        Assert.Equal(0, i);
        Assert.Equal(5.0, d, 6);
    }

    // ── Z 不参与判距：点击落点的 Z 来自地表/投影面，和中线自己的标高本就对不齐 ──

    [Fact]
    public void NearestIndex_IgnoresElevationGap()
    {
        var high = new List<double[]> { new double[] { 0, 0, 900, 100, 0, 900 } };   // 中线在 900m
        Assert.Equal(0, CenterlinePick.NearestIndex(50, 3, high, maxDistM: 10.0, out double d));
        Assert.Equal(3.0, d, 6);
    }

    // ── 退化输入不抛：空表 / 顶点不足 / 半径非正 ──

    [Fact]
    public void NearestIndex_DegenerateInput_ReturnsMiss()
    {
        Assert.Equal(-1, CenterlinePick.NearestIndex(0, 0, null, 50, out _));
        Assert.Equal(-1, CenterlinePick.NearestIndex(0, 0, new List<double[]>(), 50, out _));
        Assert.Equal(-1, CenterlinePick.NearestIndex(0, 0, new List<double[]> { new double[] { 0, 0, 0 } }, 50, out _));
        Assert.Equal(-1, CenterlinePick.NearestIndex(0, 0, Pool(), maxDistM: 0.0, out _));
    }

    // ── 报给用户的"删掉多长的路"按三维算（爬坡段不能按平距少报）──

    [Fact]
    public void Length3d_CountsElevationGain()
    {
        Assert.Equal(0.0, CenterlinePick.Length3d(null));
        Assert.Equal(0.0, CenterlinePick.Length3d(new double[] { 0, 0, 0 }));
        Assert.Equal(100.0, CenterlinePick.Length3d(North), 6);
        Assert.Equal(Math.Sqrt(100 * 100 + 10 * 10),
            CenterlinePick.Length3d(new double[] { 0, 0, 0, 100, 0, 10 }), 6);
    }
}
