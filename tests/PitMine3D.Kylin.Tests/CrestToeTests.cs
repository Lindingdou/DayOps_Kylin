using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>坡顶/坡底线(CrestToe)回归 —— 标准坡度断棱线检测(平-陡边界)。合成台阶坡验 crest 在顶/toe 在底。</summary>
public class CrestToeTests
{
    // 台阶坡横断面(沿 x): flat顶 z=10(x∈[0,4]) → 陡面(x∈[4,6]) → flat底 z=0(x∈[6,10]); 沿 y 拉伸
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) StepSlope()
    {
        var v = new List<(double x, double y, double z)>
        {
            (0,0,10),(4,0,10),(6,0,0),(10,0,0),      // y=0 行
            (0,10,10),(4,10,10),(6,10,0),(10,10,0),  // y=10 行
        };
        var t = new List<(int a, int b, int c)>
        {
            (0,1,5),(0,5,4),   // flat 顶(x0-4)
            (1,2,6),(1,6,5),   // 陡面(x4-6)
            (2,3,7),(2,7,6),   // flat 底(x6-10)
        };
        return (v, t);
    }

    [Fact]
    public void Crest_at_top_edge_toe_at_bottom_edge()
    {
        var (v, t) = StepSlope();
        var (crest, toe) = CrestToe.Extract(v, t, slopeThresholdDeg: 30);
        Assert.Single(crest);
        Assert.Single(toe);
        // 坡顶线在 x=4(平台顶与坡面交), 坡底线在 x=6(坡面与平台底交)
        Assert.Equal(4, crest[0].X0, 6); Assert.Equal(4, crest[0].X1, 6);
        Assert.Equal(6, toe[0].X0, 6); Assert.Equal(6, toe[0].X1, 6);
    }

    [Fact]
    public void Flat_mesh_has_no_break_lines()
    {
        // 全平 → 无平-陡边界
        var v = new List<(double x, double y, double z)> { (0, 0, 5), (10, 0, 5), (10, 10, 5), (0, 10, 5) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        var (crest, toe) = CrestToe.Extract(v, t, 30);
        Assert.Empty(crest); Assert.Empty(toe);
    }

    [Fact]
    public void Threshold_controls_classification()
    {
        var (v, t) = StepSlope();
        // 阈值 85°: 陡面(~78°)被判为平 → 无平-陡边界
        var (crest, toe) = CrestToe.Extract(v, t, slopeThresholdDeg: 85);
        Assert.Empty(crest); Assert.Empty(toe);
    }

    [Fact]
    public void Degenerate_inputs_safe()
    {
        Assert.Equal((0, 0), (CrestToe.Extract(null!, null!).crest.Count, CrestToe.Extract(null!, null!).toe.Count));
        var empty = CrestToe.Extract(new List<(double, double, double)>(), new List<(int, int, int)>());
        Assert.Empty(empty.crest);
    }
}
