using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 撤销/重做栈（基于场景快照字符串，配合 SceneIO）。纯逻辑、可单测。
/// 用法：改动前 Push(当前快照)；Undo/Redo 传入当前快照、返回要恢复的快照。
/// </summary>
public sealed class UndoManager
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>改动前调用：记下改动前快照，清空重做栈。</summary>
    public void Push(string stateBefore)
    {
        _undo.Push(stateBefore);
        _redo.Clear();
    }

    /// <summary>撤销：把 current 存入重做栈，返回上一个快照；无则 null。</summary>
    public string? Undo(string current)
    {
        if (_undo.Count == 0) return null;
        _redo.Push(current);
        return _undo.Pop();
    }

    /// <summary>重做：把 current 存回撤销栈，返回下一个快照；无则 null。</summary>
    public string? Redo(string current)
    {
        if (_redo.Count == 0) return null;
        _undo.Push(current);
        return _redo.Pop();
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); }
}
