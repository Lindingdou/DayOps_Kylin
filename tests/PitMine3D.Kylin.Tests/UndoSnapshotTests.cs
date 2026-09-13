using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 撤销快照不逐点复制点云：点/逐点色/真实色/法向按引用进 <see cref="SnapshotHeavyStore"/>，JSON 只记 key。
/// 语义与整份 JSON 快照一致（着色可撤、删点云可撤、真实色/法向随撤销保住），存档格式(Save/SaveDoc)不变。
/// </summary>
public class UndoSnapshotTests
{
    private static PointCloudEntity BigCloud(int n = 100_000)
    {
        var pts = new List<(double x, double y, double z)>(n);
        var rgb = new List<(float r, float g, float b)>(n);
        for (int i = 0; i < n; i++) { pts.Add((4_500_000 + i * 0.1, 38_600_000 + (i % 97), 1200 + i % 13)); rgb.Add((i % 3 / 2f, 0.5f, 0.25f)); }
        var pc = new PointCloudEntity("航测", pts) { Source = @"C:\x\a.las", SourceTotalPoints = 3_979_969, RgbColors = rgb, Colors = new List<(float, float, float)>(rgb), Elevation = 2.5, PointPixels = 3 };
        pc.Normals = pts.Select(_ => (0.0, 0.0, 1.0)).ToList();
        pc.LayerName = "点云";
        return pc;
    }

    [Fact]
    public void Snapshot_is_tiny_and_restores_same_list_instances()
    {
        var scene = new Scene();
        var pc = BigCloud();
        scene.Add(pc);
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1 });
        var heavy = new SnapshotHeavyStore();

        var snap = SceneIO.Snapshot(scene, heavy);
        Assert.True(snap.Json.Length < 2000, $"snapshot json {snap.Json.Length} chars");   // 整份 Save 是 ~8MB
        Assert.Equal(4, snap.Keys.Length);
        Assert.Equal(4, heavy.Count);
        Assert.DoesNotContain("\"V\"", snap.Json);

        var back = SceneIO.Restore(snap, heavy);
        Assert.Equal(2, back.Entities.Count);
        var got = Assert.IsType<PointCloudEntity>(back.Entities[0]);
        Assert.NotSame(pc, got);                       // 同旧快照：撤销回来的是新实体
        Assert.Same(pc.Pts, got.Pts);                  // 但列表不复制
        Assert.Same(pc.Colors, got.Colors);
        Assert.Same(pc.RgbColors, got.RgbColors);     // 真实色/法向也随撤销保住(旧 JSON 快照会丢)
        Assert.Same(pc.Normals, got.Normals);
        Assert.Equal("航测", got.Name);
        Assert.Equal(@"C:\x\a.las", got.Source);
        Assert.Equal(3_979_969, got.SourceTotalPoints);   // 抽样封顶信息随撤销保住 → 撤销后仍能回源文件全量算
        Assert.Equal("点云", got.LayerName);
        Assert.Equal(2.5, got.Elevation, 9);
        Assert.Equal(3f, got.PointPixels, 3);
    }

    [Fact]
    public void Recolor_then_undo_restores_previous_colors_reference()
    {
        var scene = new Scene();
        var pc = BigCloud(1000);
        scene.Add(pc);
        var undo = new UndoManager();
        var oldColors = pc.Colors;

        undo.Push(SceneIO.Snapshot(scene, undo.Heavy));           // BeginChange
        pc.Colors = pc.Pts.Select(p => (1f, 0f, 0f)).ToList();      // 点云着色 = 整份换新 List
        var restored = SceneIO.Restore(undo.Undo(SceneIO.Snapshot(scene, undo.Heavy))!.Value, undo.Heavy);
        var got = Assert.IsType<PointCloudEntity>(Assert.Single(restored.Entities));
        Assert.Same(oldColors, got.Colors);

        // 再 Redo → 新色
        var redone = SceneIO.Restore(undo.Redo(SceneIO.Snapshot(restored, undo.Heavy))!.Value, undo.Heavy);
        Assert.Equal(1f, Assert.IsType<PointCloudEntity>(redone.Entities[0]).Colors![0].r);
    }

    [Fact]
    public void Remove_cloud_then_undo_brings_it_back_without_copy()
    {
        var scene = new Scene();
        var pc = BigCloud(1000);
        scene.Add(pc);
        var undo = new UndoManager();

        undo.Push(SceneIO.Snapshot(scene, undo.Heavy));
        scene.Entities.Remove(pc);                                 // 点云管理「移除」
        undo.Push(SceneIO.Snapshot(scene, undo.Heavy));            // 又一次改动(画条线)
        scene.Add(new LineEntity());
        Assert.True(undo.Heavy.Count >= 4);                        // 被删点云的列表仍被第一份快照引用 → 留着

        var s1 = undo.Undo(SceneIO.Snapshot(scene, undo.Heavy))!.Value;   // 撤画线
        Assert.Empty(SceneIO.Restore(s1, undo.Heavy).Entities.OfType<PointCloudEntity>());
        var s2 = undo.Undo(SceneIO.Snapshot(SceneIO.Restore(s1, undo.Heavy), undo.Heavy))!.Value;   // 撤删点云
        var got = Assert.IsType<PointCloudEntity>(Assert.Single(SceneIO.Restore(s2, undo.Heavy).Entities));
        Assert.Same(pc.Pts, got.Pts);
    }

    [Fact]
    public void Sweep_drops_heavy_only_referenced_by_cleared_redo_stack()
    {
        var scene = new Scene();
        var pc = BigCloud(500);
        scene.Add(pc);
        var undo = new UndoManager();
        undo.Push(SceneIO.Snapshot(scene, undo.Heavy));            // 快照 A(含点云)
        scene.Entities.Remove(pc);
        var a = undo.Undo(SceneIO.Snapshot(scene, undo.Heavy));    // 撤销：A 弹出，B(无点云)进 redo
        Assert.NotNull(a);
        // 此刻 A 已不在任何栈上，但清扫只在 Push 时做 → 列表仍在，正要拿它恢复场景
        Assert.True(undo.Heavy.Count >= 4);
        var restored = SceneIO.Restore(a!.Value, undo.Heavy);
        Assert.Single(restored.Entities);

        // 恢复后场景没点云、再 Push 一份无点云快照 → 两头都不引用点云列表 → 清扫掉
        undo.Push(SceneIO.Snapshot(new Scene(), undo.Heavy));
        Assert.Equal(0, undo.Heavy.Count);

        undo.Clear();
        Assert.Equal(0, undo.Heavy.Count);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Store_dedupes_by_reference_and_keeps_key_after_sweep()
    {
        var store = new SnapshotHeavyStore();
        var list = new List<int> { 1 };
        long k1 = store.Put(list), k2 = store.Put(list);
        Assert.Equal(k1, k2);
        Assert.Equal(1, store.Count);
        store.Retain(new HashSet<long>());
        Assert.Equal(0, store.Count);
        Assert.Null(store.Get<List<int>>(k1));
        Assert.Equal(k1, store.Put(list));                          // 回归沿用旧 key
        Assert.Same(list, store.Get<List<int>>(k1));
        Assert.NotEqual(k1, store.Put(new List<int>()));
    }

    [Fact]
    public void Save_and_SaveDoc_still_write_full_points_without_keys()
    {
        var scene = new Scene();
        scene.Add(BigCloud(50));
        string json = SceneIO.Save(scene);
        Assert.Contains("\"V\"", json);
        Assert.DoesNotContain("\"K\"", json);
        Assert.DoesNotContain("\"Src\"", json);
        string doc = SceneIO.SaveDoc(scene, new List<Layer>(), "0");
        Assert.Contains("\"V\"", doc);
        Assert.DoesNotContain("\"K\"", doc);
        var back = Assert.IsType<PointCloudEntity>(Assert.Single(SceneIO.Load(json).Entities));
        Assert.Equal(50, back.PointCount);
    }

    [Fact]
    public void String_api_still_works_for_plain_snapshots()
    {
        var u = new UndoManager();
        u.Push("A");
        Assert.Equal("A", u.Undo("B"));
        Assert.Equal("B", u.Redo("A"));
        Assert.Equal(0, u.Heavy.Count);
    }
}
