using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;
using M = PitMine3D.Kylin.Cad.SelectionOps.Modifier;

namespace PitMine3D.Kylin.Tests;

/// <summary>点选/框选修饰键语义（忠实原 Picking::DoPointPick / UpdateBoxSelection）：Ctrl 切换/并入，Shift 剔除，普通替换。</summary>
public class SelectionOpsTests
{
    private sealed class E { public string N; public E(string n) => N = n; public override string ToString() => N; }
    private static readonly E A = new("A"), B = new("B"), C = new("C");

    // ── 点选 ──
    [Fact]
    public void Plain_click_replaces_selection_and_empty_click_clears()
    {
        var sel = new List<E> { A, B };
        SelectionOps.ApplyPick(sel, C, M.None, false);
        Assert.Equal(new[] { C }, sel);
        SelectionOps.ApplyPick(sel, C, M.None, false);   // 再点已选中的：仍是它(原版替换, 不是取消)
        Assert.Equal(new[] { C }, sel);
        SelectionOps.ApplyPick(sel, null, M.None, false);
        Assert.Empty(sel);
    }

    [Fact]
    public void Ctrl_click_toggles_and_keeps_others()
    {
        var sel = new List<E> { A };
        SelectionOps.ApplyPick(sel, B, M.Ctrl, false);
        Assert.Equal(new[] { A, B }, sel);                 // 加选
        SelectionOps.ApplyPick(sel, C, M.Ctrl, false);
        Assert.Equal(new[] { A, B, C }, sel);
        SelectionOps.ApplyPick(sel, A, M.Ctrl, false);
        Assert.Equal(new[] { B, C }, sel);                 // 减选
    }

    [Fact]
    public void Ctrl_click_on_empty_keeps_selection()
    {
        var sel = new List<E> { A, B };
        SelectionOps.ApplyPick(sel, null, M.Ctrl, false);
        Assert.Equal(new[] { A, B }, sel);
    }

    [Fact]
    public void Shift_click_behaves_like_plain_click_for_picking()
    {
        var sel = new List<E> { A, B };
        SelectionOps.ApplyPick(sel, C, M.Shift, false);
        Assert.Equal(new[] { C }, sel);
    }

    [Fact]
    public void Accumulate_mode_toggles_regardless_of_modifier_and_keeps_on_empty()
    {
        var sel = new List<E> { A };
        SelectionOps.ApplyPick(sel, B, M.None, true);
        Assert.Equal(new[] { A, B }, sel);
        SelectionOps.ApplyPick(sel, A, M.None, true);
        Assert.Equal(new[] { B }, sel);
        SelectionOps.ApplyPick(sel, null, M.None, true);
        Assert.Equal(new[] { B }, sel);
    }

    // ── 框选 ──
    [Fact]
    public void Plain_box_replaces()
    {
        var sel = new List<E> { A };
        SelectionOps.ApplyBox(sel, new[] { B, C, C }, M.None, false);
        Assert.Equal(new[] { B, C }, sel);                 // 去重
    }

    [Fact]
    public void Ctrl_box_merges_without_duplicates()
    {
        var sel = new List<E> { A, B };
        SelectionOps.ApplyBox(sel, new[] { B, C }, M.Ctrl, false);
        Assert.Equal(new[] { A, B, C }, sel);
    }

    [Fact]
    public void Shift_box_removes_matched_only()
    {
        var sel = new List<E> { A, B, C };
        SelectionOps.ApplyBox(sel, new[] { B }, M.Shift, false);
        Assert.Equal(new[] { A, C }, sel);
        SelectionOps.ApplyBox(sel, new E[0], M.Shift, false);
        Assert.Equal(new[] { A, C }, sel);                 // 空框不动
    }

    [Fact]
    public void Accumulate_box_merges_even_with_shift()
    {
        var sel = new List<E> { A };
        SelectionOps.ApplyBox(sel, new[] { B }, M.Shift, true);
        Assert.Equal(new[] { A, B }, sel);
    }
}
