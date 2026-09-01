using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 中线交点分类 回归 —— 忠实移植原 RoadLib.Network.CenterlineJunctions 的已知值验证:
/// X 十字 / T 丁字 / 接缝 / 汇合口 四型分类 + 立交识别 + 就近合并累计度 + 最近可捕捉点。
/// </summary>
public class CenterlineJunctionsTests
{
    [Fact]
    public void X_cross_interior_intersection()
    {
        // 水平 (0,5)-(10,5) 与 竖直 (5,0)-(5,10) 内部相交于 (5,5), 同高程。
        var lines = new List<double[]>
        {
            new double[] { 0, 5, 10, 10, 5, 10 },
            new double[] { 5, 0, 10, 5, 10, 10 },
        };
        var set = CenterlineJunctions.Build(lines);
        Assert.Equal(1, set.CrossCount);
        Assert.Equal(0, set.SeamCount);
        var j = set.All.Single();
        Assert.Equal(JunctionKind.Cross, j.Kind);
        Assert.Equal(5, j.X, 6);
        Assert.Equal(5, j.Y, 6);
        Assert.Equal(0, j.GapM, 6);
        Assert.False(j.GradeSeparated);
        Assert.True(j.IsRealJunction);
    }

    [Fact]
    public void Grade_separated_crossing_flagged()
    {
        // 同上但竖线抬到 Z=20 → 高差 10 > 4 闸门 → 立交(建网不连通)。
        var lines = new List<double[]>
        {
            new double[] { 0, 5, 10, 10, 5, 10 },
            new double[] { 5, 0, 20, 5, 10, 20 },
        };
        var set = CenterlineJunctions.Build(lines);
        Assert.Equal(1, set.CrossCount);
        Assert.Equal(1, set.GradeSeparatedCount);
        Assert.True(set.All.Single().GradeSeparated);
        Assert.Equal(10, set.All.Single().DzM, 6);
    }

    [Fact]
    public void T_junction_endpoint_on_body()
    {
        // A 水平 (0,0)-(20,0); B 竖直端点 (10,0) 落在 A 身上(离 A 两端各 10>容差)。
        var lines = new List<double[]>
        {
            new double[] { 0, 0, 0, 20, 0, 0 },
            new double[] { 10, 0, 0, 10, 8, 0 },
        };
        var set = CenterlineJunctions.Build(lines);
        Assert.Equal(1, set.TeeCount);
        Assert.Equal(0, set.CrossCount);
        var j = set.All.Single();
        Assert.Equal(JunctionKind.Tee, j.Kind);
        Assert.Equal(10, j.X, 6);
        Assert.Equal(0, j.Y, 6);
    }

    [Fact]
    public void Seam_two_endpoints_meet_is_not_real_junction()
    {
        // A (0,0)-(10,0) 末端接 B (10,0)-(20,5) 首端 → 接缝(仅两线, 非路口)。
        var lines = new List<double[]>
        {
            new double[] { 0, 0, 0, 10, 0, 0 },
            new double[] { 10, 0, 0, 20, 5, 0 },
        };
        var set = CenterlineJunctions.Build(lines);
        Assert.Equal(1, set.SeamCount);
        Assert.Equal(0, set.JunctionCount);
        var j = set.All.Single();
        Assert.Equal(JunctionKind.Seam, j.Kind);
        Assert.False(j.IsRealJunction);
        Assert.Equal(2, j.LineCount);
    }

    [Fact]
    public void Three_lines_meeting_is_confluence_after_merge()
    {
        // 三条线端点全汇于 (10,10) → 就近合并成 1 个 3 度汇合口(真路口), 非 3 条接缝。
        var lines = new List<double[]>
        {
            new double[] { 0, 0, 0, 10, 10, 0 },
            new double[] { 20, 0, 0, 10, 10, 0 },
            new double[] { 10, 0, 0, 10, 10, 0 },
        };
        var set = CenterlineJunctions.Build(lines);
        Assert.Equal(1, set.ConfluenceCount);
        Assert.Equal(0, set.SeamCount);
        var j = set.All.Single(x => x.Kind == JunctionKind.Seam);   // 底层仍是 Seam 型, 但 3 度
        Assert.Equal(3, j.LineCount);
        Assert.True(j.IsRealJunction);
        Assert.Equal(2, set.MergedCount);
        Assert.Contains("汇合", j.KindLabel);
    }

    [Fact]
    public void Nearest_prefers_real_junction_over_seam()
    {
        // 一个 X 十字(真路口)在 (5,5) + 一条接缝在别处; 在 (5,5) 附近取点应命中十字。
        var lines = new List<double[]>
        {
            new double[] { 0, 5, 0, 10, 5, 0 },
            new double[] { 5, 0, 0, 5, 10, 0 },
            new double[] { 40, 40, 0, 50, 40, 0 },
            new double[] { 50, 40, 0, 60, 45, 0 },
        };
        var set = CenterlineJunctions.Build(lines);
        var j = set.Nearest(5.4, 5.4, 3.0, out double d);
        Assert.NotNull(j);
        Assert.Equal(JunctionKind.Cross, j!.Kind);
        Assert.True(d < 1.0);
        Assert.Null(set.Nearest(500, 500, 3.0, out _));   // 半径外
    }

    [Fact]
    public void Empty_and_degenerate_are_safe()
    {
        Assert.Equal(0, CenterlineJunctions.Build(null).Count);
        Assert.Equal(0, CenterlineJunctions.Build(new List<double[]>()).Count);
        Assert.Equal(0, CenterlineJunctions.Build(new List<double[]> { new double[] { 0, 0, 0 } }).Count);   // 单点
    }
}
