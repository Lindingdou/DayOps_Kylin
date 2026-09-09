using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>导入后定位视图用的「图元密集区」范围。</summary>
public class RobustExtentTests
{
    /// <summary>在 [x0,x1]×[y0,y1] 里铺 n 个规则分布的中心点。</summary>
    private static List<(double x, double y)> Grid(int n, double x0, double y0, double x1, double y1)
    {
        var list = new List<(double x, double y)>(n);
        int side = (int)Math.Ceiling(Math.Sqrt(n));
        for (int i = 0; i < n; i++)
        {
            double u = (i % side) / (double)(side - 1), v = (i / side) / (double)(side - 1);
            list.Add((x0 + u * (x1 - x0), y0 + v * (y1 - y0)));
        }
        return list;
    }

    private static double[] Raw(IReadOnlyList<(double x, double y)> pts)
    {
        double a = double.MaxValue, b = double.MaxValue, c = double.MinValue, d = double.MinValue;
        foreach (var p in pts) { a = Math.Min(a, p.x); b = Math.Min(b, p.y); c = Math.Max(c, p.x); d = Math.Max(d, p.y); }
        return new[] { a, b, c, d };
    }

    /// <summary>正常图纸（没有远处离群图元）必须原样返回——这条防的是"好图也被剪"。</summary>
    [Fact]
    public void Normal_drawing_is_left_alone()
    {
        var pts = Grid(2000, 620000, 4378000, 628000, 4384000);
        var raw = Raw(pts);
        var r = RobustExtent.Compute(pts, raw);
        Assert.False(r.Trimmed);
        Assert.Equal(raw, r.Bounds);
        Assert.Equal(0, r.Outliers);
    }

    /// <summary>
    /// 现场实测那张 51MB 接续计划的形状：8km 的采剥图 + 几件跑到原点附近的孤立图元，
    /// 真实包围盒被拉成 4392km。按真实包围盒缩放，图就成了屏幕上的一个点。
    /// </summary>
    [Fact]
    public void A_few_far_away_entities_do_not_get_to_define_the_view()
    {
        var pts = Grid(5000, 620000, 4378000, 628000, 4384000);
        pts.Add((0, 0));                    // 图框/图例被放在原点
        pts.Add((-5517, -2247));
        pts.Add((921898, 4390443));
        var raw = Raw(pts);

        var r = RobustExtent.Compute(pts, raw);

        Assert.True(r.Trimmed);
        Assert.True(r.Bounds[0] > 600000 && r.Bounds[2] < 650000, $"X 应回到采剥图那一带, 实际 [{r.Bounds[0]}, {r.Bounds[2]}]");
        Assert.True(r.Bounds[1] > 4300000, $"Y 应回到采剥图那一带, 实际 {r.Bounds[1]}");
        Assert.True(r.Bounds[2] - r.Bounds[0] < 12000, "剪后跨度应回到 8km 量级");
        Assert.True(r.Outliers >= 3, $"至少要数出那 3 个离群图元, 实际 {r.Outliers}");
        // 主体仍要完整落在视野里(留了余量, 不能把边上的图元切掉)
        Assert.True(r.Bounds[0] <= 620000 && r.Bounds[2] >= 628000);
        Assert.True(r.Bounds[1] <= 4378000 && r.Bounds[3] >= 4384000);
    }

    /// <summary>图元太少时分位数没有意义（"1%" 剪掉的可能正是主体），原样返回。</summary>
    [Fact]
    public void Tiny_drawings_are_not_trimmed()
    {
        var pts = Grid(50, 0, 0, 100, 100);
        pts.Add((1000000, 1000000));
        var raw = Raw(pts);
        var r = RobustExtent.Compute(pts, raw);
        Assert.False(r.Trimmed);
        Assert.Equal(raw, r.Bounds);
    }

    /// <summary>离群的量大到不算"离群"时（一半在这边一半在那边），不该擅自砍掉一半图纸。</summary>
    [Fact]
    public void Two_equally_populated_clusters_are_both_kept()
    {
        var pts = Grid(1000, 0, 0, 1000, 1000);
        pts.AddRange(Grid(1000, 500000, 500000, 501000, 501000));
        var raw = Raw(pts);
        var r = RobustExtent.Compute(pts, raw);
        Assert.False(r.Trimmed);      // 两簇各占一半, 分位数剪不掉, 保持全图
    }

    [Fact]
    public void Degenerate_inputs_do_not_throw()
    {
        Assert.False(RobustExtent.Compute(new List<(double, double)>(), new double[] { 0, 0, 1, 1 }).Trimmed);
        Assert.False(RobustExtent.Compute(null!, new double[] { 0, 0, 1, 1 }).Trimmed);
        var all = new List<(double x, double y)>();
        for (int i = 0; i < 500; i++) all.Add((7, 9));      // 全部重合 → 密集区退化, 不敢剪
        Assert.False(RobustExtent.Compute(all, new double[] { 7, 9, 7, 9 }).Trimmed);
    }

    [Theory]
    [InlineData(0, 0, 8000, 6000, "8 km × 6 km")]
    [InlineData(0, 0, 800, 600, "800 m × 600 m")]
    public void Describe_reads_like_a_distance(double a, double b, double c, double d, string expected)
        => Assert.Equal(expected, RobustExtent.Describe(new[] { a, b, c, d }));
}
