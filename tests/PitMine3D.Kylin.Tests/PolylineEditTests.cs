using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>多段线加密回归（按最大步长匀分插点）。</summary>
public class PolylineEditTests
{
    [Fact]
    public void Densify_line_by_maxstep()
    {
        var pts = new List<(double, double)> { (0, 0), (10, 0) };
        var d = PolylineEdit.Densify(pts, closed: false, maxStep: 2.5);
        Assert.Equal(5, d.Count);   // 分 4 段: 0,2.5,5,7.5,10
        Assert.Equal((2.5, 0.0), d[1]);
        Assert.Equal((10.0, 0.0), d[4]);
    }

    [Fact]
    public void Densify_no_change_when_step_exceeds_length()
    {
        var pts = new List<(double, double)> { (0, 0), (1, 0), (2, 0) };
        var d = PolylineEdit.Densify(pts, closed: false, maxStep: 100);
        Assert.Equal(3, d.Count);   // 无插点
    }

    [Fact]
    public void Densify_closed_includes_closing_segment()
    {
        // 单位正方形(4 点闭合), 步长 0.5 → 每边分 2 段, 含闭合段 → 8 点
        var sq = new List<(double, double)> { (0, 0), (1, 0), (1, 1), (0, 1) };
        var d = PolylineEdit.Densify(sq, closed: true, maxStep: 0.5);
        Assert.Equal(8, d.Count);
    }

    [Fact]
    public void Densify_each_subsegment_within_maxstep()
    {
        var pts = new List<(double, double)> { (0, 0), (7, 0) };
        double step = 2.0;
        var d = PolylineEdit.Densify(pts, closed: false, maxStep: step);
        for (int i = 0; i + 1 < d.Count; i++)
        {
            double dx = d[i + 1].x - d[i].x, dy = d[i + 1].y - d[i].y;
            Assert.True(System.Math.Sqrt(dx * dx + dy * dy) <= step + 1e-9);
        }
        Assert.Equal((0.0, 0.0), d[0]);
        Assert.Equal((7.0, 0.0), d[d.Count - 1]);
    }

    [Fact]
    public void Densify_degenerate_returns_input()
    {
        var pts = new List<(double, double)> { (3, 4) };
        var d = PolylineEdit.Densify(pts, closed: false, maxStep: 1);
        Assert.Single(d);
    }
}
