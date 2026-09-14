using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>夹点系统对齐 AutoCAD：五模式环、提示格式、镜像语义、改基点。</summary>
[Collection("TextGeometry")]
public class GripModeAutoCadTests
{
    private static (GripTable table, GripDrag drag, PolylineEntity line) Setup()
    {
        var pl = new PolylineEntity();
        pl.Points.AddRange(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0) });
        var t = new GripTable();
        t.Rebuild(new List<SceneEntity> { pl });
        var d = new GripDrag();
        return (t, d, pl);
    }

    /// <summary>AutoCAD 的模式环是五个：拉伸 → 移动 → 旋转 → 比例缩放 → 镜像 → 回到拉伸。</summary>
    [Fact]
    public void Mode_ring_matches_autocad_five_modes()
    {
        var seen = new List<GripMode>();
        var m = GripMode.Stretch;
        for (int i = 0; i < 5; i++) { seen.Add(m); m = GripDrag.NextMode(m); }

        Assert.Equal(new[] { GripMode.Stretch, GripMode.Move, GripMode.Rotate, GripMode.Scale, GripMode.Mirror }, seen);
        Assert.Equal(GripMode.Stretch, m);                  // 第五次之后转回拉伸
    }

    [Fact]
    public void Space_cycles_through_all_five_then_wraps()
    {
        var d = new GripDrag();
        Assert.Equal(GripMode.Stretch, d.Mode);
        var got = new List<GripMode>();
        for (int i = 0; i < 5; i++) { d.CycleMode(); got.Add(d.Mode); }
        Assert.Equal(new[] { GripMode.Move, GripMode.Rotate, GripMode.Scale, GripMode.Mirror, GripMode.Stretch }, got);
    }

    /// <summary>提示行格式同 AutoCAD：<c>** 模式 ** 指定… 或 [选项]</c>，旋转/缩放多一项 参照(R)。</summary>
    [Theory]
    [InlineData(GripMode.Stretch, "** 拉伸 **", false)]
    [InlineData(GripMode.Move, "** 移动 **", false)]
    [InlineData(GripMode.Rotate, "** 旋转 **", true)]
    [InlineData(GripMode.Scale, "** 比例缩放 **", true)]
    [InlineData(GripMode.Mirror, "** 镜像 **", false)]
    public void Prompt_shows_mode_title_and_options(GripMode mode, string title, bool hasReference)
    {
        var d = new GripDrag { Mode = mode };
        Assert.StartsWith(title, d.Prompt);
        foreach (var kw in new[] { "基点(B)", "复制(C)", "放弃(U)", "退出(X)" })
            Assert.Contains(kw, d.Prompt);
        Assert.Equal(hasReference, d.Prompt.Contains("参照(R)"));
    }

    /// <summary>镜像：热夹点是镜像线第一点、光标是第二点。沿 X 轴镜像应把 y 取反。</summary>
    [Fact]
    public void Mirror_reflects_across_the_line_from_grip_to_cursor()
    {
        var (t, d, pl) = Setup();
        d.Mode = GripMode.Mirror;
        t.SelectOnly(0);                                   // 夹点 0 = (0,0)
        d.Begin(t, 0, 0, 0);

        var res = d.Preview(10, 0);                        // 镜像线 = 过 (0,0) 的水平线
        var moved = Assert.IsType<PolylineEntity>(Assert.Single(res).moved);

        Assert.Equal(0.0, moved.Points[0].x, 6); Assert.Equal(0.0, moved.Points[0].y, 6);
        Assert.Equal(10.0, moved.Points[1].x, 6); Assert.Equal(0.0, moved.Points[1].y, 6);
        Assert.Equal(10.0, moved.Points[2].x, 6); Assert.Equal(-10.0, moved.Points[2].y, 6);   // y 取反
    }

    [Fact]
    public void Mirror_value_is_the_mirror_line_angle()
    {
        var (t, d, _) = Setup();
        d.Mode = GripMode.Mirror;
        t.SelectOnly(0);
        d.Begin(t, 0, 0, 0);
        Assert.Equal(0.0, d.ValueAt(5, 0), 6);                                    // 水平
        Assert.Equal(System.Math.PI / 2, d.ValueAt(0, 5), 6);                     // 垂直
    }

    /// <summary>改基点后，位移要从新基点算起（旋转/缩放的起始参照也一并挪，否则会跳一下）。</summary>
    [Fact]
    public void SetBase_moves_the_transform_origin()
    {
        var (t, d, _) = Setup();
        d.Mode = GripMode.Move;
        t.SelectOnly(0);
        d.Begin(t, 0, 0, 0);

        d.SetBase(4, 4);
        Assert.Equal((4.0, 4.0), d.Base);
        Assert.Equal((4.0, 4.0), d.StartMouse);

        var moved = Assert.IsType<PolylineEntity>(Assert.Single(d.Preview(6, 4)).moved);
        Assert.Equal(2.0, moved.Points[0].x, 6);           // 位移 = 光标 − 新基点 = (2,0)
        Assert.Equal(0.0, moved.Points[0].y, 6);
    }

    [Fact]
    public void SetBase_on_an_inactive_drag_does_nothing()
    {
        var d = new GripDrag();
        d.SetBase(9, 9);
        Assert.NotEqual((9.0, 9.0), d.Base);
    }

    /// <summary>旋转改基点后角度从新基点重新起算，不该因为换了基点就整体跳一个角。</summary>
    [Fact]
    public void Rotate_after_SetBase_starts_from_zero_angle()
    {
        var (t, d, _) = Setup();
        d.Mode = GripMode.Rotate;
        t.SelectOnly(0);
        d.Begin(t, 0, 0, 0);
        d.SetBase(5, 5);
        Assert.Equal(0.0, d.ValueAt(5, 5), 6);             // 光标停在新基点上 → 角度增量 0
    }
}
