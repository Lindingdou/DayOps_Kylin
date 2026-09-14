using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 渲染局部原点（大坐标 float32 精度）回归。
///
/// 矿区坐标 X≈62 万 / Y≈438 万，而顶点缓冲与整条矩阵链都是 float32 —— float32 在 4,194,304
/// 以上的最小间隔正好 0.5 m，等于块体模型的层厚，于是煤层带断断续续、顶面发花。
/// 修法（同原版 renderOrigin rebase）：镶嵌成顶点时先减局部原点，再转 float。
/// 凡是拿镶嵌结果做世界坐标运算的（拾取/框选/空间索引/包围盒）须 <see cref="RenderOrigin.Suspend"/>。
/// </summary>
public class RenderOriginTests : IDisposable
{
    public void Dispose() => RenderOrigin.Set(0, 0);   // 静态态，测完复位免得污染别的用例

    private const double Ox = 623100, Oy = 4382450;    // 东露天块体模型的 XY 中心

    private static List<float> Tess(SceneEntity e) { var o = new List<float>(); e.Tessellate(o); return o; }

    [Fact]
    public void Vertices_are_emitted_relative_to_origin()
    {
        var line = new LineEntity { X0 = Ox - 100, Y0 = Oy - 50, X1 = Ox + 100, Y1 = Oy + 50 };
        RenderOrigin.Set(Ox, Oy);
        var o = Tess(line);
        Assert.Equal(-100f, o[0], 3); Assert.Equal(-50f, o[1], 3);
        Assert.Equal(100f, o[6], 3); Assert.Equal(50f, o[7], 3);
    }

    [Fact]
    public void Suspend_restores_world_coordinates_and_puts_the_origin_back()
    {
        var line = new LineEntity { X0 = Ox, Y0 = Oy, X1 = Ox + 10, Y1 = Oy };
        RenderOrigin.Set(Ox, Oy);
        using (RenderOrigin.Suspend())
        {
            var w = Tess(line);
            Assert.Equal(Ox, w[0], 0);      // 作用域内是世界坐标
            Assert.Equal(Oy, w[1], 0);
        }
        Assert.Equal(Ox, RenderOrigin.X, 9);   // 出作用域原样恢复
        Assert.Equal(Oy, RenderOrigin.Y, 9);
        Assert.Equal(0f, Tess(line)[0], 3);
    }

    /// <summary>
    /// 关键收益：0.5 m 量级的细节在大坐标下不再被 float32 抹平。
    /// 不减原点时 Y≈438 万的 float32 间隔就是 0.5 m —— 块体 0.5 m 的层厚整个丢掉。
    /// </summary>
    [Fact]
    public void Half_metre_detail_survives_at_mine_coordinates()
    {
        // 两条只差 0.1 m 的线：减原点后必须还能分开，不减就被量化到同一个 float
        var a = new LineEntity { X0 = Ox, Y0 = Oy, X1 = Ox + 1, Y1 = Oy };
        var b = new LineEntity { X0 = Ox, Y0 = Oy + 0.1, X1 = Ox + 1, Y1 = Oy + 0.1 };

        using (RenderOrigin.Suspend())    // 世界系 = 老口径
        {
            Assert.Equal(Tess(a)[1], Tess(b)[1]);          // 0.1 m 之差被 float32 吃掉，两条线重合
        }

        RenderOrigin.Set(Ox, Oy);
        float ya = Tess(a)[1], yb = Tess(b)[1];
        Assert.NotEqual(ya, yb);
        Assert.Equal(0.1, yb - ya, 4);                     // 减原点后 0.1 m 完整保留
    }

    /// <summary>拾取距离按世界坐标算 —— 忘了 Suspend 的话原点一设就整体偏几十万米。</summary>
    [Fact]
    public void DistanceTo_stays_in_world_space_while_origin_is_set()
    {
        var line = new LineEntity { X0 = Ox, Y0 = Oy, X1 = Ox + 100, Y1 = Oy };
        RenderOrigin.Set(Ox, Oy);
        Assert.Equal(0, line.DistanceTo(Ox + 50, Oy), 3);
        Assert.Equal(10, line.DistanceTo(Ox + 50, Oy + 10), 3);
    }

    /// <summary>框选按世界坐标判 —— 同上。</summary>
    [Fact]
    public void SelectionBox_stays_in_world_space_while_origin_is_set()
    {
        var line = new LineEntity { X0 = Ox + 10, Y0 = Oy + 10, X1 = Ox + 20, Y1 = Oy + 20 };
        RenderOrigin.Set(Ox, Oy);
        Assert.True(SelectionBox.Match(line, Ox, Oy, Ox + 100, Oy + 100, crossing: false));
        Assert.False(SelectionBox.Match(line, Ox + 1000, Oy + 1000, Ox + 2000, Oy + 2000, crossing: true));
    }

    /// <summary>定原点用的场景包围盒必须是世界系（否则第二次刷新会拿局部盒再定一次原点，模型跑飞）。</summary>
    [Fact]
    public void Scene_world_bounds_are_world_space_while_origin_is_set()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = Ox - 500, Y0 = Oy - 300, X1 = Ox + 500, Y1 = Oy + 300 });
        RenderOrigin.Set(Ox, Oy);
        var b = s.WorldBoundsXY()!;
        Assert.Equal(Ox - 500, b[0], 2); Assert.Equal(Oy - 300, b[1], 2);
        Assert.Equal(Ox + 500, b[2], 2); Assert.Equal(Oy + 300, b[3], 2);
    }
}
