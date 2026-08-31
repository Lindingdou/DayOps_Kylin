using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>图层表回归。</summary>
public class LayerTableTests
{
    [Fact]
    public void Default_layer_is_zero_and_current()
    {
        var t = new LayerTable();
        Assert.Single(t.Layers);
        Assert.Equal("0", t.Current.Name);
    }

    [Fact]
    public void New_adds_and_sets_current_distinct_color()
    {
        var t = new LayerTable();
        var c0 = (t.Current.Cr, t.Current.Cg, t.Current.Cb);
        var l = t.New();
        Assert.Equal(2, t.Layers.Count);
        Assert.Same(l, t.Current);
        Assert.NotEqual(c0, (l.Cr, l.Cg, l.Cb));   // 轮转配色不同
    }

    [Fact]
    public void Rename_changes_name_in_place_preserving_color_and_state()
    {
        var t = new LayerTable();
        var l = t.New("旧层");
        l.Cr = 0.3f; l.Frozen = true; l.Locked = true;
        Assert.True(t.Rename("旧层", "新层"));
        Assert.Null(t.Get("旧层"));
        var r = t.Get("新层");
        Assert.NotNull(r);
        Assert.Equal(0.3f, r!.Cr, 3);          // 色保留
        Assert.True(r.Frozen); Assert.True(r.Locked);   // 状态保留
    }

    [Fact]
    public void Rename_rejects_default_empty_same_and_collision()
    {
        var t = new LayerTable();
        t.New("A"); t.New("B");
        Assert.False(t.Rename("0", "别的"));       // 默认层不可改名
        Assert.False(t.Rename("A", ""));           // 空名
        Assert.False(t.Rename("A", "A"));          // 与原名同
        Assert.False(t.Rename("A", "B"));          // 目标已存在
        Assert.True(t.Rename("A", "C"));           // 正常
    }

    [Fact]
    public void Merge_moves_entities_and_removes_source_layer()
    {
        var t = new LayerTable();
        t.New("源"); t.New("目标");
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0, LayerName = "源" });
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 2, Y1 = 0, LayerName = "源" });
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 3, Y1 = 0, LayerName = "目标" });
        // 合并 源 → 目标: 实体改指派 + 删源层
        int moved = scene.ReassignLayer("源", "目标");
        Assert.True(t.Remove("源"));
        Assert.Equal(2, moved);
        Assert.Null(t.Get("源"));
        Assert.Equal(3, System.Linq.Enumerable.Count(scene.Entities, e => e.LayerName == "目标"));
    }

    [Fact]
    public void Isolate_shows_only_target_and_allon_restores()
    {
        var t = new LayerTable();
        t.New("A"); t.New("B"); t.New("C");
        int hidden = t.Isolate("B");
        Assert.Equal(3, hidden);                       // 0/A/C 关闭
        Assert.True(t.Get("B")!.Visible);
        Assert.False(t.Get("A")!.Visible);
        Assert.False(t.Get("0")!.Visible);
        t.AllOn();                                     // 取消隔离
        Assert.True(t.Get("A")!.Visible);
        Assert.True(t.Get("0")!.Visible);
    }

    [Fact]
    public void SetCurrent_and_cycle()
    {
        var t = new LayerTable();
        t.New("A"); t.New("B");
        Assert.True(t.SetCurrent("A"));
        Assert.Equal("A", t.Current.Name);
        Assert.NotEqual("A", t.CycleCurrent().Name);
    }

    [Fact]
    public void Remove_default_blocked_current_falls_back()
    {
        var t = new LayerTable();
        t.New("A");                    // current = A
        Assert.False(t.Remove("0"));   // 默认层不可删
        Assert.True(t.Remove("A"));    // 删当前 → 回落到 "0"
        Assert.Equal("0", t.Current.Name);
    }

    [Fact]
    public void Freeze_hides_and_blocks_select()
    {
        var t = new LayerTable();
        t.New("A"); t.Current.Frozen = true;
        Assert.False(t.IsShown("A"));
        Assert.False(t.IsSelectable("A"));
        Assert.True(t.IsShown("0"));      // 其它层不受影响
    }

    [Fact]
    public void Lock_shows_but_blocks_select()
    {
        var t = new LayerTable();
        t.New("A"); t.Current.Locked = true;
        Assert.True(t.IsShown("A"));       // 仍显示
        Assert.False(t.IsSelectable("A")); // 但不可选
    }

    [Fact]
    public void AllOn_thaws_and_shows()
    {
        var t = new LayerTable();
        t.New("A"); t.Current.Frozen = true; t.Current.Visible = false;
        t.AllOn();
        Assert.True(t.IsShown("A"));
    }

    [Fact]
    public void Unknown_layer_defaults_shown_and_selectable()
    {
        var t = new LayerTable();
        Assert.True(t.IsShown("不存在"));
        Assert.True(t.IsSelectable("不存在"));
    }

    [Fact]
    public void Reset_returns_to_single_default_layer()
    {
        var t = new LayerTable();
        t.New("A"); t.New("B");
        Assert.Equal(3, t.Layers.Count);
        t.Reset();
        Assert.Single(t.Layers);          // 只剩 "0"
        Assert.Equal("0", t.Current.Name);
    }

    [Fact]
    public void Remove_protects_default_layer_and_removes_others()
    {
        var t = new LayerTable();
        t.New("A");
        Assert.False(t.Remove("0"));       // 默认层不可删
        Assert.True(t.Remove("A"));        // 普通层可删
        Assert.Null(t.Get("A"));
    }

    [Fact]
    public void Remove_current_layer_falls_back_to_default()
    {
        var t = new LayerTable();
        var a = t.New("A");
        t.SetCurrent("A");
        Assert.Equal("A", t.Current.Name);
        t.Remove("A");
        Assert.Equal("0", t.Current.Name);  // 删当前层 → 回退到 "0"
    }

    [Fact]
    public void Scene_reassign_layer_moves_entities()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, LayerName = "钻孔" });
        s.Add(new LineEntity { X0 = 1, Y0 = 1, X1 = 2, Y1 = 2, LayerName = "钻孔" });
        s.Add(new LineEntity { X0 = 2, Y0 = 2, X1 = 3, Y1 = 3, LayerName = "0" });
        int moved = s.ReassignLayer("钻孔", "0");
        Assert.Equal(2, moved);                                        // 2 个实体移出
        Assert.All(s.Entities, e => Assert.Equal("0", e.LayerName));   // 全部在 "0"
        Assert.Equal(0, s.ReassignLayer("钻孔", "0"));                 // 已无该层实体
    }
}
