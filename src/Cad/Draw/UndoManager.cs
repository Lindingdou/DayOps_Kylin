using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 撤销/重做栈（基于场景快照，配合 SceneIO）。纯逻辑、可单测。
/// 用法：改动前 Push(当前快照)；Undo/Redo 传入当前快照、返回要恢复的快照。
///
/// 快照 = JSON 字符串 + 它引用的重数据 key 列表：点云的几百万个点不进 JSON（否则加载了点云后每一次编辑都
/// 序列化上百 MB 字符串、撤销栈每层各留一份，两千万点直接撞 .NET 字符串上限 OOM），列表本体按引用放在
/// <see cref="Heavy"/> 里，见 <see cref="SceneIO.Snapshot(Scene, SnapshotHeavyStore)"/>。
/// 只有字符串的 Push/Undo/Redo 重载仍在（无重数据的快照，及既有单测）。
/// </summary>
public sealed class UndoManager
{
    /// <summary>一份快照：实体 JSON + 引用的重数据 key（无点云时为空）。</summary>
    public readonly record struct Snapshot(string Json, long[] Keys);

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    /// <summary>本栈引用的重数据（点云列表）仓；随栈一起 Clear。</summary>
    public SnapshotHeavyStore Heavy { get; } = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>改动前调用：记下改动前快照，清空重做栈。</summary>
    public void Push(string stateBefore) => Push(new Snapshot(stateBefore, Array.Empty<long>()));

    /// <summary>改动前调用：记下改动前快照，清空重做栈；顺手清掉两头都不再引用的重数据。</summary>
    public void Push(Snapshot stateBefore)
    {
        _undo.Push(stateBefore);
        _redo.Clear();
        // 只在 Push 时清扫：Undo/Redo 刚弹出的快照正要被恢复成场景、它的列表还没进任何快照，
        // 那时清扫会把正要用的数据扔掉；到下一次 Push 时场景里的列表已随"改动前快照"重新登记。
        Sweep();
    }

    /// <summary>撤销：把 current 存入重做栈，返回上一个快照；无则 null。</summary>
    public string? Undo(string current) => Undo(new Snapshot(current, Array.Empty<long>()))?.Json;

    public Snapshot? Undo(Snapshot current)
    {
        if (_undo.Count == 0) return null;
        _redo.Push(current);
        return _undo.Pop();
    }

    /// <summary>重做：把 current 存回撤销栈，返回下一个快照；无则 null。</summary>
    public string? Redo(string current) => Redo(new Snapshot(current, Array.Empty<long>()))?.Json;

    public Snapshot? Redo(Snapshot current)
    {
        if (_redo.Count == 0) return null;
        _undo.Push(current);
        return _redo.Pop();
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); Heavy.Clear(); }

    private void Sweep()
    {
        if (Heavy.Count == 0) return;
        var live = new HashSet<long>();
        foreach (var s in _undo) foreach (var k in s.Keys) live.Add(k);
        foreach (var s in _redo) foreach (var k in s.Keys) live.Add(k);
        Heavy.Retain(live);
    }
}
