using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>线型虚线化(DashPattern)回归 —— 样式切段 / 常见线型名 / 边界。</summary>
public class DashPatternTests
{
    [Fact]
    public void Dashes_split_line_into_drawn_segments()
    {
        // (0,0)->(10,0), 样式 [2 画,2 空] → 画段 [0,2],[4,6],[8,10] = 3 段
        var d = DashPattern.Dashes(0, 0, 10, 0, new double[] { 2, 2 });
        Assert.Equal(3, d.Count);
        Assert.Equal(0, d[0].sx, 6); Assert.Equal(2, d[0].ex, 6);
        Assert.Equal(4, d[1].sx, 6); Assert.Equal(6, d[1].ex, 6);
        Assert.Equal(8, d[2].sx, 6); Assert.Equal(10, d[2].ex, 6);
    }

    [Fact]
    public void Dashes_total_drawn_length_matches_duty_cycle()
    {
        // 样式 [3 画,1 空] 占空比 3/4; 12 长线 → 画总长 ≈ 9
        var d = DashPattern.Dashes(0, 0, 12, 0, new double[] { 3, 1 });
        double drawn = d.Sum(s => System.Math.Abs(s.ex - s.sx));
        Assert.Equal(9, drawn, 3);
    }

    [Fact]
    public void Null_or_empty_pattern_is_single_solid_segment()
    {
        Assert.Single(DashPattern.Dashes(0, 0, 10, 0, null));
        Assert.Single(DashPattern.Dashes(0, 0, 10, 0, new double[0]));
    }

    [Fact]
    public void Zero_length_line_yields_nothing()
    {
        Assert.Empty(DashPattern.Dashes(5, 5, 5, 5, new double[] { 2, 2 }));
    }

    [Fact]
    public void ByName_maps_common_linetypes_and_scales()
    {
        Assert.Null(DashPattern.ByName("实线"));                       // 实线=无样式
        Assert.Equal(new[] { 6.0, 3.0 }, DashPattern.ByName("虚线"));
        Assert.Equal(4, DashPattern.ByName("点划线")!.Length);         // 画/空/点/空
        // 缩放 2×
        Assert.Equal(new[] { 12.0, 6.0 }, DashPattern.ByName("虚线", 2.0));
    }
}
