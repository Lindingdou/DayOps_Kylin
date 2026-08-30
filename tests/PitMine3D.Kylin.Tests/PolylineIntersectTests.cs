using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>两线交点回归（线段真交点 + 折线两两求交去重）。</summary>
public class PolylineIntersectTests
{
    [Fact]
    public void SegSeg_crossing_at_origin()
    {
        var p = PolylineIntersect.SegSeg((-1, 0), (1, 0), (0, -1), (0, 1));
        Assert.True(p.HasValue);
        Assert.Equal(0.0, p.Value.x, 9);
        Assert.Equal(0.0, p.Value.y, 9);
    }

    [Fact]
    public void SegSeg_parallel_is_null()
    {
        Assert.Null(PolylineIntersect.SegSeg((0, 0), (1, 0), (0, 1), (1, 1)));
    }

    [Fact]
    public void SegSeg_no_overlap_is_null()
    {
        // 两段所在直线相交, 但交点落在段外
        Assert.Null(PolylineIntersect.SegSeg((0, 0), (1, 0), (2, -1), (2, 1)));
    }

    [Fact]
    public void SegSeg_endpoint_touch_counts()
    {
        var p = PolylineIntersect.SegSeg((0, 0), (1, 0), (1, 0), (1, 1));
        Assert.True(p.HasValue);
        Assert.Equal(1.0, p.Value.x, 9);
        Assert.Equal(0.0, p.Value.y, 9);
    }

    [Fact]
    public void Between_two_polylines_cross_twice()
    {
        // 水平线 y=0 (x:0..4) 与 W 形折线上下穿越两次
        var a = new List<(double, double)> { (0, 0), (4, 0) };
        var b = new List<(double, double)> { (1, -1), (2, 1), (3, -1) };
        var pts = PolylineIntersect.Between(a, false, b, false, 1e-6);
        Assert.Equal(2, pts.Count);
    }

    [Fact]
    public void Between_dedups_coincident_hits()
    {
        // 交点恰在 b 的顶点(两段共享), 只应计一次
        var a = new List<(double, double)> { (-1, 0), (1, 0) };
        var b = new List<(double, double)> { (0, -1), (0, 0), (0, 1) };   // 顶点 (0,0) 落在 a 上
        var pts = PolylineIntersect.Between(a, false, b, false, 1e-6);
        Assert.Single(pts);
    }

    [Fact]
    public void Between_closed_square_and_diagonal_line()
    {
        // 单位方形(闭合) 与 对角穿越的长线段 → 2 个交点
        var sq = new List<(double, double)> { (0, 0), (1, 0), (1, 1), (0, 1) };
        var line = new List<(double, double)> { (-1, -1), (2, 2) };
        var pts = PolylineIntersect.Between(sq, true, line, false, 1e-6);
        Assert.Equal(2, pts.Count);
    }
}
