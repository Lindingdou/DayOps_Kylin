using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>结构路面 ribbon（中线等宽外扩闭合带）回归。</summary>
public class StructurePavementTests
{
    [Fact]
    public void Straight_horizontal_line_offsets_to_known_band()
    {
        // 中线 (0,0)-(10,0), 宽 4 → 半宽 2。切向 +X, 法向(左)=(0,1)。
        // 左: (0,2)(10,2); 右逆: (10,-2)(0,-2); 闭合回 (0,2)。
        var line = new List<(double, double)> { (0, 0), (10, 0) };
        var r = StructurePavement.BuildRibbon(line, 4);
        Assert.NotNull(r);
        Assert.Equal(5, r!.Count);                 // 2n+1 = 5
        Assert.Equal((0.0, 2.0), r[0]);
        Assert.Equal((10.0, 2.0), r[1]);
        Assert.Equal((10.0, -2.0), r[2]);
        Assert.Equal((0.0, -2.0), r[3]);
        Assert.Equal(r[0], r[^1]);                 // 闭合
    }

    [Fact]
    public void Vertical_line_normal_points_left_minus_x()
    {
        // 中线 (0,0)-(0,10) 切向 +Y → 法向(左)=(-1,0)。左 x=-h, 右 x=+h。
        var r = StructurePavement.BuildRibbon(new List<(double, double)> { (0, 0), (0, 10) }, 6);
        Assert.NotNull(r);
        Assert.Equal(-3.0, r![0].x, 6); Assert.Equal(0.0, r[0].y, 6);      // 左 (-3,0)
        Assert.Equal(3.0, r[3].x, 6); Assert.Equal(0.0, r[3].y, 6);        // 右末 (3,0)
    }

    [Fact]
    public void Degenerate_and_invalid_inputs()
    {
        Assert.Null(StructurePavement.BuildRibbon(new List<(double, double)> { (1, 1) }, 4));   // 单点
        Assert.Null(StructurePavement.BuildRibbon(new List<(double, double)> { (1, 1), (2, 2) }, 0)); // 零宽
        // 重合点(全退化)
        Assert.Null(StructurePavement.BuildRibbon(new List<(double, double)> { (5, 5), (5, 5) }, 4));
    }

    [Fact]
    public void BuildRibbons_skips_short_lines()
    {
        var lines = new List<IReadOnlyList<(double, double)>>
        {
            new List<(double, double)> { (0, 0), (10, 0) },   // 有效
            new List<(double, double)> { (1, 1) },            // 单点跳
        };
        var rs = StructurePavement.BuildRibbons(lines, 4);
        Assert.Single(rs);
    }
}
