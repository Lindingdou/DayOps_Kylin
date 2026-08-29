using System.Collections.Generic;

namespace PitMine3D.Kylin.Nodes;

/// <summary>节点类型（对应 Windows 版节点编辑器的几何/参数节点）。</summary>
public enum NodeKind { Number, Point, Circle, Rectangle }

/// <summary>一个节点：位置 + 输入/输出端口数。</summary>
public sealed class Node
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public NodeKind Kind { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public int InputCount { get; init; }
    public int OutputCount { get; init; }
}

/// <summary>一条连线：源节点输出口 → 目标节点输入口。</summary>
public sealed class Connection
{
    public int FromNode { get; init; }
    public int FromPort { get; init; }
    public int ToNode { get; init; }
    public int ToPort { get; init; }
}

/// <summary>
/// 节点图模型 —— 节点编辑器的可验证内核（新增节点、连线校验）。
/// 视图（画布拖拽/连线）在 NodeEditorWindow；本类不依赖 UI，纯逻辑，可单测。
/// </summary>
public sealed class NodeGraph
{
    private int _nextId = 1;
    public List<Node> Nodes { get; } = new();
    public List<Connection> Connections { get; } = new();

    public Node AddNode(NodeKind kind, double x, double y)
    {
        var (title, ins, outs) = Spec(kind);
        var node = new Node { Id = _nextId++, Title = title, Kind = kind, X = x, Y = y, InputCount = ins, OutputCount = outs };
        Nodes.Add(node);
        return node;
    }

    /// <summary>连线（从输出口到输入口）。拒绝：自连、越界端口、不存在节点。一个输入口只接一条（重连替换）。</summary>
    public bool Connect(int fromNode, int fromPort, int toNode, int toPort)
    {
        if (fromNode == toNode) return false;
        var from = Nodes.Find(n => n.Id == fromNode);
        var to = Nodes.Find(n => n.Id == toNode);
        if (from == null || to == null) return false;
        if (fromPort < 0 || fromPort >= from.OutputCount) return false;
        if (toPort < 0 || toPort >= to.InputCount) return false;

        Connections.RemoveAll(c => c.ToNode == toNode && c.ToPort == toPort);  // 输入口独占
        Connections.Add(new Connection { FromNode = fromNode, FromPort = fromPort, ToNode = toNode, ToPort = toPort });
        return true;
    }

    private static (string title, int ins, int outs) Spec(NodeKind k) => k switch
    {
        NodeKind.Number => ("数字", 0, 1),
        NodeKind.Point => ("点", 2, 1),
        NodeKind.Circle => ("圆", 2, 1),
        NodeKind.Rectangle => ("矩形", 2, 1),
        _ => ("节点", 1, 1)
    };
}
