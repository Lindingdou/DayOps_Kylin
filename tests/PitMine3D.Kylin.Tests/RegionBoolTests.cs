using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>区域布尔回归（重叠检测 / 求差保最大块）。栅格化含离散误差, 用容差断言。</summary>
public class RegionBoolTests
{
    private static List<(double, double)> Rect(double x0, double y0, double x1, double y1)
        => new() { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };

    private static double Area(IReadOnlyList<(double x, double y)> p)
    {
        int n = p.Count; double s = 0;
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; s += p[i].x * p[j].y - p[j].x * p[i].y; }
        return Math.Abs(0.5 * s);
    }

    [Fact]
    public void Overlaps_true_for_overlapping_squares()
    {
        var a = Rect(0, 0, 10, 10);
        var b = Rect(5, 5, 15, 15);   // 重叠 25 / min 100 = 25%
        Assert.True(RegionBool.Overlaps(a, b));
    }

    [Fact]
    public void Overlaps_false_for_disjoint_squares()
    {
        var a = Rect(0, 0, 10, 10);
        var b = Rect(20, 20, 30, 30);
        Assert.False(RegionBool.Overlaps(a, b));
    }

    [Fact]
    public void Overlaps_false_for_abutting_squares()
    {
        var a = Rect(0, 0, 10, 10);
        var b = Rect(10, 0, 20, 10);   // 仅共边
        Assert.False(RegionBool.Overlaps(a, b));
    }

    [Fact]
    public void Subtract_corner_gives_L_shape_area()
    {
        var subject = Rect(0, 0, 10, 10);      // 面积 100
        var clip = Rect(5, 5, 15, 15);         // 与 subject 交 = 5×5 = 25
        var diff = RegionBool.SubtractKeepLargest(subject, clip);
        Assert.True(diff.Count >= 4);
        double a = Area(diff);
        Assert.InRange(a, 75 * 0.9, 75 * 1.1);   // ≈ 100 - 25 = 75(栅格误差 ±10%)
    }

    [Fact]
    public void Subtract_full_cover_returns_empty()
    {
        var subject = Rect(2, 2, 8, 8);
        var clip = Rect(0, 0, 10, 10);         // 完全覆盖 subject
        Assert.Empty(RegionBool.SubtractKeepLargest(subject, clip));
    }

    [Fact]
    public void Subtract_disjoint_clip_returns_subject_like_area()
    {
        var subject = Rect(0, 0, 10, 10);
        var clip = Rect(50, 50, 60, 60);       // 与 subject 无交
        var diff = RegionBool.SubtractKeepLargest(subject, clip);
        Assert.InRange(Area(diff), 100 * 0.9, 100 * 1.1);
    }
}
