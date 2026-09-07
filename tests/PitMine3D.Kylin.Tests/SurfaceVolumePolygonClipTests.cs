using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>两期三角网算量 + 闭合线裁剪/删除三角面 的纯计算核。</summary>
public class SurfaceVolumePolygonClipTests
{
    // 10×10 平面, z 常数
    private static (double[] v, int[] t) Plane(double z, double size = 10)
    {
        var v = new double[] { 0, 0, z, size, 0, z, size, size, z, 0, size, z };
        var t = new int[] { 0, 1, 2, 0, 2, 3 };
        return (v, t);
    }

    [Fact]
    public void CutFill_TwoFlatPlanes_VolumeEqualsAreaTimesDz()
    {
        var (va, ta) = Plane(100); var (vb, tb) = Plane(103);
        var r = SurfaceVolume.CutFill(va, ta, vb, tb, cell: 0.5);
        Assert.Equal(400, r.ValidCells);
        Assert.Equal(300, r.FillM3, 6);          // 10×10×3
        Assert.Equal(0, r.CutM3, 6);
        Assert.Equal(100, r.OverlapAreaM2, 6);
        Assert.Equal(3, r.MinDz, 9); Assert.Equal(3, r.MaxDz, 9);
        var r2 = SurfaceVolume.CutFill(vb, tb, va, ta, cell: 0.5);
        Assert.Equal(300, r2.CutM3, 6);
        Assert.Equal(-300, r2.NetM3, 6);
    }

    [Fact]
    public void CutFill_PartialOverlap_OnlyCommonCellsCount()
    {
        var (va, ta) = Plane(0, 10);
        var vb = new double[] { 5, 5, 1, 15, 5, 1, 15, 15, 1, 5, 15, 1 }; var tb = new int[] { 0, 1, 2, 0, 2, 3 };
        var r = SurfaceVolume.CutFill(va, ta, vb, tb, cell: 1);
        Assert.Equal(25, r.ValidCells);
        Assert.Equal(25, r.FillM3, 6);
    }

    [Fact]
    public void TriGrid_SamplesBarycentricZ()
    {
        var v = new double[] { 0, 0, 0, 10, 0, 10, 0, 10, 20 }; var t = new int[] { 0, 1, 2 };
        var g = new SurfaceVolume.TriGrid(v, t, 1);
        Assert.Equal(5, g.Sample(5, 0)!.Value, 9);
        Assert.Equal(10, g.Sample(0, 5)!.Value, 9);
        Assert.Null(g.Sample(8, 8));
    }

    [Fact]
    public void Contains_AndCrossings()
    {
        var sq = new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        Assert.True(PolylineClipper.Contains(sq, 5, 5));
        Assert.False(PolylineClipper.Contains(sq, 15, 5));
        var ts = PolylineClipper.EdgeCrossings(sq, (-5, 5), (15, 5));
        Assert.Equal(new[] { 0.25, 0.75 }, ts.ToArray());
    }

    [Fact]
    public void ClipPolyline_KeepInside_CutsAtBoundary()
    {
        var sq = new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        var line = new List<(double x, double y, double z)> { (-5, 5, 0), (15, 5, 20) };
        var inside = PolylineClipper.ClipPolyline(sq, line, false, keepInside: true);
        var piece = Assert.Single(inside);
        Assert.Equal((0, 5, 5), piece.First());
        Assert.Equal((10, 5, 15), piece.Last());
        var outside = PolylineClipper.ClipPolyline(sq, line, false, keepInside: false);
        Assert.Equal(2, outside.Count);
        Assert.Equal(-5, outside[0][0].x); Assert.Equal(0, outside[0][^1].x, 9);
        Assert.Equal(10, outside[1][0].x, 9); Assert.Equal(15, outside[1][^1].x);
    }

    [Fact]
    public void TrianglesInside_AndRemove_CompactsVertices()
    {
        var verts = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0), (20, 0, 0) };
        var tris = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3), (1, 4, 2) };
        var poly = new List<(double x, double y)> { (-1, -1), (11, -1), (11, 11), (-1, 11) };
        var inside = PolylineClipper.TrianglesInside(verts, tris, poly);
        Assert.Equal(new[] { 0, 1 }, inside.ToArray());
        var (nv, nt) = PolylineClipper.RemoveTriangles(verts, tris, inside);
        Assert.Single(nt);
        Assert.Equal(3, nv.Count);
        Assert.All(nt, tr => Assert.True(tr.a < 3 && tr.b < 3 && tr.c < 3));
    }
}
