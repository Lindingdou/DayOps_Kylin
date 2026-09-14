using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 夹点方块必须画在**节点自己的高程**上。
/// 一律画在 Z=0：图元只要有标高（等高线/台阶线/三维多段线都有），三维视图里方块就飘离节点，
/// 看起来就是"夹点位置不对"。这几条把它钉住。
/// </summary>
[Collection("TextGeometry")]
public class GripZTests
{
    private static IEnumerable<float> Zs(List<float> buf)
    {
        for (int i = 2; i < buf.Count; i += 6) yield return buf[i];   // 交错 P3_C3 的 z 分量
    }

    [Fact]
    public void Flat_entity_grip_sits_at_its_elevation()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, Elevation = 137.5 };
        Assert.Equal(137.5, line.GripZ(0), 6);
        Assert.Equal(137.5, line.GripZ(1), 6);

        var o = new List<float>();
        GripGlyph.Append(o, 0, 0, 1, 1, 1, 1, line.GripZ(0));
        Assert.All(Zs(o), z => Assert.Equal(137.5f, z, 3));
    }

    /// <summary>三维多段线逐顶点高程不同，夹点要各自跟着自己那一点。</summary>
    [Fact]
    public void Polyline3d_grip_follows_per_vertex_z()
    {
        var pl = new PolylineEntity { Elevation = 100 };
        pl.Points.AddRange(new[] { (0.0, 0.0), (10.0, 0.0), (20.0, 0.0) });
        pl.Zs = new List<double> { 1, 2, 3 };

        Assert.True(pl.Has3D);
        Assert.Equal(101, pl.GripZ(0), 6);       // Elevation + Zs[i]，与 ZAt 同口径
        Assert.Equal(102, pl.GripZ(1), 6);
        Assert.Equal(103, pl.GripZ(2), 6);
        Assert.Equal(pl.ZAt(1), pl.GripZ(1), 9);
    }

    [Fact]
    public void Flat_polyline_grips_all_use_the_entity_elevation()
    {
        var pl = new PolylineEntity { Elevation = 42 };
        pl.Points.AddRange(new[] { (0.0, 0.0), (5.0, 5.0) });
        Assert.False(pl.Has3D);
        Assert.Equal(42, pl.GripZ(0), 6);
        Assert.Equal(42, pl.GripZ(1), 6);
    }

    [Fact]
    public void Out_of_range_index_falls_back_to_elevation()
    {
        var pl = new PolylineEntity { Elevation = 7 };
        pl.Points.Add((0, 0));
        pl.Zs = new List<double> { 3 };
        Assert.Equal(7, pl.GripZ(-1), 6);
        Assert.Equal(7, pl.GripZ(99), 6);
    }

    /// <summary>方块的 z 全部相同（是个平面小方块，不该被拉成斜的）。</summary>
    [Fact]
    public void Square_is_planar_at_the_given_z()
    {
        var o = new List<float>();
        GripGlyph.Append(o, 3, 4, 0.5, 1, 1, 1, -12.25);
        var zs = Zs(o).Distinct().ToList();
        Assert.Single(zs);
        Assert.Equal(-12.25f, zs[0], 3);
    }

    [Fact]
    public void Default_z_is_zero_for_plain_2d_use()
    {
        var o = new List<float>();
        GripGlyph.Append(o, 0, 0, 1, 1, 1, 1);
        Assert.All(Zs(o), z => Assert.Equal(0f, z));
    }

    /// <summary>
    /// 夹点方块必须跟图元镶嵌用同一个渲染局部原点。少减这一下，方块就整体平移一个原点的量 ——
    /// 大坐标图纸（X≈62 万 / Y≈438 万）上就是"夹点全跑到别处去了"。
    /// </summary>
    [Fact]
    public void Grip_square_is_rebased_by_the_render_origin()
    {
        var world = new List<float>();
        GripGlyph.Append(world, 620000, 4380000, 1, 1, 1, 1);

        var local = new List<float>();
        using (var _ = RenderOriginScope(620000, 4380000))
            GripGlyph.Append(local, 620000, 4380000, 1, 1, 1, 1);

        Assert.Equal(world.Count, local.Count);
        for (int i = 0; i < local.Count; i += 6)
        {
            Assert.True(System.Math.Abs(local[i]) <= 1.001f, $"减过原点后 x 该落在 ±1 内, 实际 {local[i]}");
            Assert.True(System.Math.Abs(local[i + 1]) <= 1.001f, $"减过原点后 y 该落在 ±1 内, 实际 {local[i + 1]}");
        }
        // 而没减原点时, 坐标就是原始大数(float32 在这个量级已经量化到 0.5m, 顺带说明为什么必须 rebase)
        Assert.True(world[0] > 619000f);
    }

    /// <summary>把 RenderOrigin 设成给定值，作用域结束还原（RenderOrigin 是 ThreadStatic，测试间不会互染）。</summary>
    private static System.IDisposable RenderOriginScope(double x, double y)
    {
        var restore = RenderOrigin.Suspend();     // 先归零并记住旧值
        RenderOrigin.Set(x, y);
        return restore;
    }
}
