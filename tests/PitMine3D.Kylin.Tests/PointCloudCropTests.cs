using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云边界裁剪回归（PointCloudCrop：闭合多边形内/外过滤）。</summary>
public class PointCloudCropTests
{
    // 10×10 方形边界
    private static readonly List<(double x, double y)> Square = new() { (0, 0), (10, 0), (10, 10), (0, 10) };

    private static List<(double x, double y, double z)> Pts() => new()
    {
        (5, 5, 1),    // 内
        (2, 8, 2),    // 内
        (-3, 5, 3),   // 外(左)
        (15, 5, 4),   // 外(右)
        (5, 20, 5),   // 外(上)
    };

    [Fact]
    public void Keeps_only_points_inside_boundary()
    {
        var kept = PointCloudCrop.ByPolygon(Pts(), Square, keepInside: true);
        Assert.Equal(2, kept.Count);                       // 只留 2 内点
        Assert.All(kept, p => Assert.InRange(p.x, 0, 10));
        Assert.All(kept, p => Assert.InRange(p.y, 0, 10));
        Assert.Contains(kept, p => p.z == 1);
        Assert.Contains(kept, p => p.z == 2);
    }

    [Fact]
    public void KeepInside_false_keeps_outside()
    {
        var outside = PointCloudCrop.ByPolygon(Pts(), Square, keepInside: false);
        Assert.Equal(3, outside.Count);                    // 3 外点
        Assert.DoesNotContain(outside, p => p.z == 1);
    }

    [Fact]
    public void Degenerate_boundary_returns_all()
    {
        var boundary2 = new List<(double x, double y)> { (0, 0), (1, 1) };   // <3 点
        Assert.Equal(5, PointCloudCrop.ByPolygon(Pts(), boundary2).Count);   // 原样
        Assert.Empty(PointCloudCrop.ByPolygon(new List<(double x, double y, double z)>(), Square));
    }
}
