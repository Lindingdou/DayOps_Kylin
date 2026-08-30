using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>多边形裁剪(Sutherland-Hodgman)回归。</summary>
public class PolygonClipTests
{
    private static List<(double x, double y)> P(params (double x, double y)[] p) => new(p);

    [Fact]
    public void Inner_clip_yields_clip_rect()
    {
        // 大方(0..10) 被小方(2..8, CCW) 裁 → 结果=小方, 面积 36
        var subject = P((0, 0), (10, 0), (10, 10), (0, 10));
        var clip = P((2, 2), (8, 2), (8, 8), (2, 8));
        var r = PolygonClip.Clip(subject, clip);
        Assert.True(r.Count >= 4);
        Assert.Equal(36, GeomMeasure.Area(r), 3);
    }

    [Fact]
    public void Clip_larger_than_subject_returns_subject_area()
    {
        var subject = P((2, 2), (8, 2), (8, 8), (2, 8));   // 面积 36
        var clip = P((0, 0), (10, 0), (10, 10), (0, 10));  // 更大
        var r = PolygonClip.Clip(subject, clip);
        Assert.Equal(36, GeomMeasure.Area(r), 3);
    }

    [Fact]
    public void Half_overlap_area_halved()
    {
        // 方(0..10) 被右半(5..15) 裁 → 面积 50
        var subject = P((0, 0), (10, 0), (10, 10), (0, 10));
        var clip = P((5, -1), (15, -1), (15, 11), (5, 11));
        var r = PolygonClip.Clip(subject, clip);
        Assert.Equal(50, GeomMeasure.Area(r), 3);
    }
}
