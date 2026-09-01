using System.Collections.Generic;

namespace PitMine3D.Kylin.Nodes;

/// <summary>单个节点的快照（忠实原 NodeSnapshot：位置/值/端口默认值均入快照，供撤销恢复）。</summary>
public sealed class NodeSnapshot
{
    public int Id { get; init; }
    public NodeKind Kind { get; init; }
    public string Title { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public int InputCount { get; init; }
    public int OutputCount { get; init; }
    public object? Value { get; init; }
    public object?[] InputDefaults { get; init; } = System.Array.Empty<object?>();
}

/// <summary>整图快照 = 节点列表 + 连线列表（忠实原 GraphSnapshot）。</summary>
public sealed class GraphSnapshot
{
    public List<NodeSnapshot> Nodes { get; } = new();
    public List<Connection> Connections { get; } = new();
}

/// <summary>
/// 节点图撤销/重做控制器 —— 忠实移植原 NodeGraphUndoRedo 的快照栈协议：
/// <list type="bullet">
/// <item><see cref="SaveState"/> 在每次改动<b>之前</b>调用：把当前(改动前)快照压入撤销栈并清空重做栈。</item>
/// <item><see cref="Undo"/>：当前(改动后)快照压入重做栈，弹出撤销栈顶并还原。</item>
/// <item><see cref="Redo"/>：当前快照压入撤销栈，弹出重做栈顶并还原。</item>
/// </list>
/// <c>_isRestoring</c> 守卫：还原期间触发的 SaveState 被忽略，避免自我污染（忠实原实现）。
/// </summary>
public sealed class NodeGraphUndoRedo
{
    private readonly NodeGraph _graph;
    private readonly Stack<GraphSnapshot> _undoStack = new();
    private readonly Stack<GraphSnapshot> _redoStack = new();
    private bool _isRestoring;

    public NodeGraphUndoRedo(NodeGraph graph) => _graph = graph;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>改动前调用：记录当前状态为可回退点。还原期间调用无效。</summary>
    public void SaveState()
    {
        if (_isRestoring) return;
        _undoStack.Push(_graph.CaptureState());
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redoStack.Push(_graph.CaptureState());
        Restore(_undoStack.Pop());
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undoStack.Push(_graph.CaptureState());
        Restore(_redoStack.Pop());
    }

    private void Restore(GraphSnapshot snap)
    {
        _isRestoring = true;
        try { _graph.RestoreState(snap); }
        finally { _isRestoring = false; }
    }
}
