using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>夹点表(多实体/多夹点选择) + 夹点拖拽四模式 —— 对照原 xllAcEd GripManager/GripEditor 语义。</summary>
public class GripTableTests
{
    private static PolylineEntity Ring(int n, bool closed)
    {
        var pl = new PolylineEntity { Closed = closed };
        for (int i = 0; i < n; i++) pl.Points.Add((i, 0));
        return pl;
    }

    [Fact]
    public void Rebuild_collects_grips_of_all_selected_entities()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var circ = new CircleEntity { Cx = 20, Cy = 0, Radius = 5 };
        var t = new GripTable();
        t.Rebuild(new List<SceneEntity> { line, circ });
        Assert.Equal(3 + 5, t.Count);                                   // 线 3 + 圆 5
        Assert.Same(circ, t.Grips[3].Owner);
        Assert.Equal(0, t.Grips[3].Index);
    }

    [Fact]
    public void Rebuild_skips_when_selection_exceeds_limit()
    {
        var sel = new List<SceneEntity>();
        for (int i = 0; i <= GripTable.ObjLimit; i++) sel.Add(new PointEntity { X = i, Y = 0 });
        var t = new GripTable();
        t.Rebuild(sel);
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void HitTest_returns_nearest_within_tolerance()
    {
        var t = new GripTable();
        t.Rebuild(new List<SceneEntity> { new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 } });
        Assert.Equal(2, t.HitTest(9.7, 0.2, 0.5));                     // 终点
        Assert.Equal(-1, t.HitTest(7, 0, 0.5));                        // 都不在容差内
    }

    [Fact]
    public void SelectOnly_Toggle_and_anchor_semantics()
    {
        var t = new GripTable();
        t.Rebuild(new List<SceneEntity> { Ring(5, false) });
        t.SelectOnly(1);
        Assert.True(t.IsSelected(1)); Assert.Equal(1, t.Anchor);
        t.ToggleGrip(3);                                                // Ctrl 加选 → 锚点跟到 3
        Assert.Equal(2, t.SelectedCount()); Assert.Equal(3, t.Anchor);
        t.ToggleGrip(3);                                                // Ctrl 减选 → 锚点失效
        Assert.False(t.IsSelected(3)); Assert.Equal(-1, t.Anchor);
        Assert.True(t.IsSelected(1));                                   // 1 仍选中
    }

    [Fact]
    public void Shift_range_on_open_polyline_selects_between_anchor_and_target()
    {
        var t = new GripTable();
        t.Rebuild(new List<SceneEntity> { Ring(6, false) });
        t.SelectOnly(1);
        t.SelectRangeTo(4);
        Assert.Equal(new List<int> { 1, 2, 3, 4 }, t.SelectedIndices());
        Assert.Equal(1, t.Anchor);                                      // 锚点保持不动
    }

    [Fact]
    public void Shift_range_on_closed_ring_takes_shorter_arc_across_seam()
    {
        var t = new GripTable();
        t.Rebuild(new List<SceneEntity> { Ring(8, true) });
        t.SelectOnly(1);
        t.SelectRangeTo(7);                                             // 直接段跨 6，跨接缝只跨 2 → 取补集 {0,1,7}
        Assert.Equal(new List<int> { 0, 1, 7 }, t.SelectedIndices());
    }

    [Fact]
    public void Shift_range_across_entities_degrades_to_SelectOnly()
    {
        var t = new GripTable();
        t.Rebuild(new List<SceneEntity> { Ring(3, false), Ring(3, false) });
        t.SelectOnly(0);
        t.SelectRangeTo(4);                                             // 另一条线上的点
        Assert.Equal(new List<int> { 4 }, t.SelectedIndices());
        Assert.Equal(4, t.Anchor);
    }

    [Fact]
    public void Rebuild_invalidates_selection()
    {
        var t = new GripTable();
        var pl = Ring(4, false);
        t.Rebuild(new List<SceneEntity> { pl });
        t.SelectOnly(2);
        t.Rebuild(new List<SceneEntity> { pl });
        Assert.Equal(0, t.SelectedCount()); Assert.Equal(-1, t.Anchor);
    }

    // ── GripDrag ────────────────────────────────────────────────────

    [Fact]
    public void Stretch_single_grip_moves_only_that_vertex()
    {
        var pl = Ring(3, false);                                        // (0,0)(1,0)(2,0)
        var t = new GripTable(); t.Rebuild(new List<SceneEntity> { pl });
        t.SelectOnly(1);
        var d = new GripDrag(); d.Begin(t, 1, 1, 0);
        var res = d.Preview(1, 5);
        var m = Assert.IsType<PolylineEntity>(Assert.Single(res).moved);
        Assert.Equal((1.0, 5.0), m.Points[1]); Assert.Equal((0.0, 0.0), m.Points[0]); Assert.Equal((2.0, 0.0), m.Points[2]);
    }

    [Fact]
    public void Stretch_multi_grip_translates_whole_group_by_anchor_delta()
    {
        var pl = Ring(4, false);                                        // x = 0..3
        var t = new GripTable(); t.Rebuild(new List<SceneEntity> { pl });
        t.SelectOnly(1); t.SelectRangeTo(2);                            // 选 1,2
        var d = new GripDrag(); d.Begin(t, 2, 2, 0);                    // 拖锚点 2
        var m = (PolylineEntity)Assert.Single(d.Preview(2, 3)).moved;   // 上移 3
        Assert.Equal((1.0, 3.0), m.Points[1]); Assert.Equal((2.0, 3.0), m.Points[2]);
        Assert.Equal((0.0, 0.0), m.Points[0]); Assert.Equal((3.0, 0.0), m.Points[3]);
    }

    [Fact]
    public void Stretch_multi_grip_across_entities_moves_each_owner()
    {
        var a = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var b = new LineEntity { X0 = 10, Y0 = 0, X1 = 20, Y1 = 0 };
        var t = new GripTable(); t.Rebuild(new List<SceneEntity> { a, b });
        t.SelectOnly(2); t.ToggleGrip(3);                               // a 终点 + b 起点(Ctrl)
        var d = new GripDrag(); d.Begin(t, 3, 10, 0);
        var res = d.Preview(10, 4);
        Assert.Equal(2, res.Count);
        Assert.Equal(4, ((LineEntity)res[0].moved).Y1, 6);
        Assert.Equal(4, ((LineEntity)res[1].moved).Y0, 6);
    }

    [Fact]
    public void Move_mode_translates_entity_by_anchor_delta()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var t = new GripTable(); t.Rebuild(new List<SceneEntity> { line });
        t.SelectOnly(0);
        var d = new GripDrag { Mode = GripMode.Move }; d.Begin(t, 0, 0, 0);
        var m = (LineEntity)Assert.Single(d.Preview(3, 4)).moved;
        Assert.Equal(3, m.X0, 6); Assert.Equal(4, m.Y0, 6); Assert.Equal(13, m.X1, 6); Assert.Equal(4, m.Y1, 6);
    }

    [Fact]
    public void Rotate_mode_uses_relative_angle_from_press_point()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var t = new GripTable(); t.Rebuild(new List<SceneEntity> { line });
        t.SelectOnly(0);
        var d = new GripDrag { Mode = GripMode.Rotate }; d.Begin(t, 0, 5, 0);   // 按下点在 +X 方向
        Assert.Equal(0, d.ValueAt(8, 0), 9);                            // 未转 → 0
        var m = (LineEntity)Assert.Single(d.Preview(0, 5)).moved;       // 光标到 +Y → 转 90°
        Assert.Equal(0, m.X1, 6); Assert.Equal(10, m.Y1, 6);
    }

    [Fact]
    public void Scale_mode_ratio_of_distances_about_anchor()
    {
        var circ = new CircleEntity { Cx = 0, Cy = 0, Radius = 2 };
        var t = new GripTable(); t.Rebuild(new List<SceneEntity> { circ });
        t.SelectOnly(0);
        var d = new GripDrag { Mode = GripMode.Scale }; d.Begin(t, 0, 1, 0);
        Assert.Equal(3, d.ValueAt(3, 0), 9);
        var m = (CircleEntity)Assert.Single(d.Preview(3, 0)).moved;
        Assert.Equal(6, m.Radius, 6);
    }

    /// <summary>
    /// 模式环与提示改为对齐 AutoCAD：比原来多一个「镜像」档（五档环），
    /// 标题也从英文 <c>** STRETCH **</c> 换成中文 <c>** 拉伸 **</c>。详见 GripModeAutoCadTests。
    /// </summary>
    [Fact]
    public void Mode_cycle_and_prompts_follow_autocad_order()
    {
        Assert.Equal(GripMode.Move, GripDrag.NextMode(GripMode.Stretch));
        Assert.Equal(GripMode.Rotate, GripDrag.NextMode(GripMode.Move));
        Assert.Equal(GripMode.Scale, GripDrag.NextMode(GripMode.Rotate));
        Assert.Equal(GripMode.Mirror, GripDrag.NextMode(GripMode.Scale));
        Assert.Equal(GripMode.Stretch, GripDrag.NextMode(GripMode.Mirror));
        Assert.Equal("** 拉伸 **", GripDrag.ModePrompt(GripMode.Stretch));
        var d = new GripDrag { Mode = GripMode.Rotate };
        Assert.StartsWith("** 旋转 **", d.Prompt);
    }

    [Fact]
    public void Cancel_clears_state_without_touching_entities()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var t = new GripTable(); t.Rebuild(new List<SceneEntity> { line });
        t.SelectOnly(2);
        var d = new GripDrag { Mode = GripMode.Rotate }; d.Begin(t, 2, 10, 0);
        d.Preview(10, 9);
        d.Cancel();
        Assert.False(d.Active); Assert.Empty(d.Preview(10, 9));
        Assert.Equal(GripMode.Stretch, d.Mode);                         // 下一次夹点拖动必须从拉伸开始
        Assert.Equal(0, line.Y1, 9);                                    // 原实体从未被改
    }
}

/// <summary>捕捉自避：夹点拖拽时排除被拖点。</summary>
public class SnapExcludeTests
{
    [Fact]
    public void Exclude_removes_only_the_matching_vertex()
    {
        float[] v = { 0, 0, 0, 0, 0, 0,  5, 0, 0, 0, 0, 0,  10, 0, 0, 0, 0, 0 };
        var o = SnapPoints.Exclude(v, 5, 0);
        Assert.Equal(12, o.Length);
        Assert.Null(SnapPoints.FindNearest(o, 5, 0, 0.5));                // 被拖点不再可捕捉
        Assert.Equal((10.0, 0.0), SnapPoints.FindNearest(o, 9.8, 0, 0.5)); // 同线其他顶点仍可捕捉
    }
}
