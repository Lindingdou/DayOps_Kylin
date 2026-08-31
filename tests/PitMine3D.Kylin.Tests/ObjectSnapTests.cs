using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>对象捕捉六模式几何回归（端点/中点/圆心/交点/垂足/最近）。</summary>
public class ObjectSnapTests
{
    private static readonly List<ObjectSnap.Seg> NoSeg = new();
    private static readonly List<ObjectSnap.Circ> NoCirc = new();
    private static readonly List<ObjectSnap.ArcP> NoArc = new();
    private static readonly List<(double, double)> NoPt = new();

    [Fact]
    public void Endpoint_wins_near_vertex()
    {
        var segs = new List<ObjectSnap.Seg> { new(0, 0, 10, 0) };
        var h = ObjectSnap.Find(segs, NoCirc, NoArc, NoPt, 0.2, 0.1, 0.5, ObjectSnap.AllModes, null);
        Assert.NotNull(h);
        Assert.Equal(ObjectSnap.Mode.Endpoint, h!.Value.Mode);
        Assert.Equal(0, h.Value.X, 6); Assert.Equal(0, h.Value.Y, 6);
    }

    [Fact]
    public void Midpoint_of_segment()
    {
        var segs = new List<ObjectSnap.Seg> { new(0, 0, 10, 0) };
        // 光标靠中点、远离端点 → 中点
        var h = ObjectSnap.Find(segs, NoCirc, NoArc, NoPt, 5.0, 0.1, 0.5,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Midpoint), null);
        Assert.NotNull(h);
        Assert.Equal(ObjectSnap.Mode.Midpoint, h!.Value.Mode);
        Assert.Equal(5, h.Value.X, 6);
    }

    [Fact]
    public void Center_of_circle()
    {
        var circ = new List<ObjectSnap.Circ> { new(3, 4, 2) };
        var h = ObjectSnap.Find(NoSeg, circ, NoArc, NoPt, 3.1, 4.05, 0.5,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Center), null);
        Assert.NotNull(h);
        Assert.Equal(ObjectSnap.Mode.Center, h!.Value.Mode);
        Assert.Equal(3, h.Value.X, 6); Assert.Equal(4, h.Value.Y, 6);
    }

    [Fact]
    public void Nearest_projects_onto_segment()
    {
        var segs = new List<ObjectSnap.Seg> { new(0, 0, 10, 0) };
        // 光标 (5,0.3)、只开最近 → 投影到 (5,0)
        var h = ObjectSnap.Find(segs, NoCirc, NoArc, NoPt, 5.0, 0.3, 0.5,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Nearest), null);
        Assert.NotNull(h);
        Assert.Equal(ObjectSnap.Mode.Nearest, h!.Value.Mode);
        Assert.Equal(5, h.Value.X, 6); Assert.Equal(0, h.Value.Y, 6);
    }

    [Fact]
    public void Intersection_of_two_crossing_segments()
    {
        var segs = new List<ObjectSnap.Seg>
        {
            new(0, 0, 10, 10),   // 对角
            new(0, 10, 10, 0),   // 反对角 → 交于 (5,5)
        };
        var h = ObjectSnap.Find(segs, NoCirc, NoArc, NoPt, 5.1, 4.9, 0.5,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection), null);
        Assert.NotNull(h);
        Assert.Equal(ObjectSnap.Mode.Intersection, h!.Value.Mode);
        Assert.Equal(5, h.Value.X, 6); Assert.Equal(5, h.Value.Y, 6);
    }

    [Fact]
    public void Perpendicular_foot_from_anchor()
    {
        var segs = new List<ObjectSnap.Seg> { new(0, 0, 10, 0) };
        // 锚点 (3,5)，垂足应落 (3,0)；光标靠近 (3,0)
        var h = ObjectSnap.Find(segs, NoCirc, NoArc, NoPt, 3.1, 0.2, 0.5,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Perpendicular), (3, 5));
        Assert.NotNull(h);
        Assert.Equal(ObjectSnap.Mode.Perpendicular, h!.Value.Mode);
        Assert.Equal(3, h.Value.X, 6); Assert.Equal(0, h.Value.Y, 6);
    }

    [Fact]
    public void Priority_endpoint_beats_nearest()
    {
        // 光标靠端点：端点与最近都在容差内 → 端点优先
        var segs = new List<ObjectSnap.Seg> { new(0, 0, 10, 0) };
        var h = ObjectSnap.Find(segs, NoCirc, NoArc, NoPt, 0.05, 0.05, 0.5, ObjectSnap.AllModes, null);
        Assert.Equal(ObjectSnap.Mode.Endpoint, h!.Value.Mode);
    }

    [Fact]
    public void Mask_disables_mode()
    {
        var segs = new List<ObjectSnap.Seg> { new(0, 0, 10, 0) };
        // 只开中点：光标在端点附近但端点关 → 无命中(超中点容差)
        var h = ObjectSnap.Find(segs, NoCirc, NoArc, NoPt, 0.1, 0.1, 0.5,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Midpoint), null);
        Assert.Null(h);
    }

    [Fact]
    public void Out_of_tolerance_returns_null()
    {
        var segs = new List<ObjectSnap.Seg> { new(0, 0, 10, 0) };
        var h = ObjectSnap.Find(segs, NoCirc, NoArc, NoPt, 5, 9, 0.5, ObjectSnap.AllModes, null);
        Assert.Null(h);
    }

    [Fact]
    public void Arc_endpoints_and_center()
    {
        // 半圆弧：中心(0,0) r=5, 0→π；端点 (5,0),(-5,0)；中点 (0,5)
        var arcs = new List<ObjectSnap.ArcP> { new(0, 0, 5, 0, Math.PI) };
        var he = ObjectSnap.Find(NoSeg, NoCirc, arcs, NoPt, 5.05, 0.05, 0.5,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Endpoint), null);
        Assert.Equal(ObjectSnap.Mode.Endpoint, he!.Value.Mode);
        Assert.Equal(5, he.Value.X, 6);
        var hm = ObjectSnap.Find(NoSeg, NoCirc, arcs, NoPt, 0.05, 4.95, 0.5,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Midpoint), null);
        Assert.Equal(ObjectSnap.Mode.Midpoint, hm!.Value.Mode);
        Assert.Equal(5, hm.Value.Y, 6);
    }
}
