using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Nodes;

/// <summary>节点端口用的三维坐标值（对应原 NodeEditor 的 Vector3）。</summary>
public readonly record struct Vec3(double X, double Y, double Z);

/// <summary>节点类型（忠实原 NodeEditor 调色板：参数节点 + 几何节点 + 烘焙）。</summary>
public enum NodeKind { Number, String, Bool, Point, Line, Circle, Arc, Rectangle, Polygon, Polyline, Bake }

/// <summary>一个节点：画布位置 + 输入/输出端口 + 参数节点自身值 + 各输入口默认值。</summary>
public sealed class Node
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public NodeKind Kind { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public int InputCount { get; init; }
    public int OutputCount { get; init; }
    /// <summary>参数节点(Number/String/Bool/Point)自身承载的值。</summary>
    public object? Value { get; set; }
    /// <summary>各输入端口未连线时的默认值(忠实原节点 Inputs[i].DefaultValue)。</summary>
    public object?[] InputDefaults { get; init; } = Array.Empty<object?>();
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
/// 节点图模型 —— 节点编辑器的可验证内核。除结构(节点/连线/端口校验)外，实现
/// <b>求值</b>：参数节点(Number/Point/…)输出值，几何节点(Circle/Line/…)按输入产 <see cref="SceneEntity"/>，
/// Bake 汇总几何。忠实原 NodeEditor 的 pull-based Evaluate（原产 native 句柄，此产托管实体=几何等价）。
/// 视图在 NodeEditorWindow；本类不依赖 UI，纯逻辑，可单测。
/// </summary>
public sealed class NodeGraph
{
    private int _nextId = 1;
    public List<Node> Nodes { get; } = new();
    public List<Connection> Connections { get; } = new();

    public Node AddNode(NodeKind kind, double x, double y)
    {
        var (title, ins, outs, defaults, value) = Spec(kind);
        var node = new Node { Id = _nextId++, Title = title, Kind = kind, X = x, Y = y, InputCount = ins, OutputCount = outs, InputDefaults = defaults, Value = value };
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

    public Node? Find(int id) => Nodes.Find(n => n.Id == id);

    // ─────────────────────────── 求值 ───────────────────────────

    /// <summary>求节点某输出口的值（参数节点→标量/Vec3，几何节点→SceneEntity，Bake→透传几何）。带环保护。</summary>
    public object? Evaluate(int nodeId, int outputPort = 0) => Evaluate(nodeId, outputPort, new HashSet<int>());

    private object? Evaluate(int nodeId, int outputPort, HashSet<int> visiting)
    {
        var n = Find(nodeId);
        if (n == null || !visiting.Add(nodeId)) return null;   // 不存在 / 环
        try { return Compute(n, outputPort, visiting); }
        finally { visiting.Remove(nodeId); }
    }

    /// <summary>求某输入口的值：有连线取上游输出，否则取该口默认值。</summary>
    private object? EvalInput(Node n, int port, HashSet<int> visiting)
    {
        var c = Connections.Find(cn => cn.ToNode == n.Id && cn.ToPort == port);
        if (c != null) return Evaluate(c.FromNode, c.FromPort, visiting);
        return port >= 0 && port < n.InputDefaults.Length ? n.InputDefaults[port] : null;
    }

    private object? Compute(Node n, int outputPort, HashSet<int> visiting)
    {
        switch (n.Kind)
        {
            case NodeKind.Number: return AsD(n.Value, 0);
            case NodeKind.String: return n.Value as string ?? "";
            case NodeKind.Bool: return AsB(n.Value, false);
            case NodeKind.Point: return n.Value is Vec3 v ? v : new Vec3(0, 0, 0);

            case NodeKind.Line:
            {
                var s = AsV3(EvalInput(n, 0, visiting)); var e = AsV3(EvalInput(n, 1, visiting));
                return new LineEntity { X0 = s.X, Y0 = s.Y, X1 = e.X, Y1 = e.Y };
            }
            case NodeKind.Circle:
            {
                var c = AsV3(EvalInput(n, 0, visiting)); double r = AsD(EvalInput(n, 1, visiting), 10);
                return new CircleEntity { Cx = c.X, Cy = c.Y, Radius = Math.Abs(r) };
            }
            case NodeKind.Arc:
            {
                var c = AsV3(EvalInput(n, 0, visiting)); double r = AsD(EvalInput(n, 1, visiting), 10);
                double sa = AsD(EvalInput(n, 2, visiting), 0) * Math.PI / 180.0;
                double ea = AsD(EvalInput(n, 3, visiting), 90) * Math.PI / 180.0;
                double ma = (sa + ea) / 2;
                return new ArcEntity
                {
                    X1 = c.X + r * Math.Cos(sa), Y1 = c.Y + r * Math.Sin(sa),
                    X2 = c.X + r * Math.Cos(ma), Y2 = c.Y + r * Math.Sin(ma),
                    X3 = c.X + r * Math.Cos(ea), Y3 = c.Y + r * Math.Sin(ea),
                };
            }
            case NodeKind.Rectangle:
            {
                var a = AsV3(EvalInput(n, 0, visiting)); var b = AsV3(EvalInput(n, 1, visiting));
                return new RectEntity { X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y };
            }
            case NodeKind.Polygon:
            {
                var c = AsV3(EvalInput(n, 0, visiting));
                int sides = Math.Max(3, AsI(EvalInput(n, 1, visiting), 6));
                double r = Math.Abs(AsD(EvalInput(n, 2, visiting), 10));
                bool inscribed = EvalInput(n, 3, visiting) is bool ib ? ib : true;   // true=内接(顶点在半径圆上), false=外切(边中点在半径圆上)
                if (!inscribed && sides >= 3) r /= Math.Cos(Math.PI / sides);         // 外切: 顶点半径放大到边中点=输入半径
                return new PolygonEntity { Cx = c.X, Cy = c.Y, Radius = r, Sides = sides, Rotation = 0 };
            }
            case NodeKind.Polyline:
            {
                var pl = new PolylineEntity();
                if (EvalInput(n, 0, visiting) is List<Vec3> verts) foreach (var p in verts) pl.Points.Add((p.X, p.Y));
                pl.Closed = AsB(EvalInput(n, 1, visiting), false);
                return pl;
            }
            case NodeKind.Bake:
                return EvalInput(n, 0, visiting);   // 透传上游几何(供 EvaluateBakes 收集)
        }
        return null;
    }

    /// <summary>求所有 Bake 节点汇总的几何实体（节点图的"输出"）。忠实原 Bake→场景。</summary>
    public List<SceneEntity> EvaluateBakes()
    {
        var outp = new List<SceneEntity>();
        foreach (var n in Nodes)
            if (n.Kind == NodeKind.Bake && Evaluate(n.Id) is SceneEntity se) outp.Add(se);
        return outp;
    }

    private static double AsD(object? o, double def) => o switch { double d => d, int i => i, float f => f, _ => def };
    private static int AsI(object? o, int def) => o switch { int i => i, double d => (int)Math.Round(d), _ => def };
    private static bool AsB(object? o, bool def) => o is bool b ? b : def;
    private static Vec3 AsV3(object? o) => o is Vec3 v ? v : new Vec3(0, 0, 0);

    private static (string title, int ins, int outs, object?[] defaults, object? value) Spec(NodeKind k) => k switch
    {
        NodeKind.Number => ("数字", 0, 1, Array.Empty<object?>(), 0.0),
        NodeKind.String => ("文本", 0, 1, Array.Empty<object?>(), ""),
        NodeKind.Bool => ("布尔", 0, 1, Array.Empty<object?>(), false),
        NodeKind.Point => ("点", 0, 1, Array.Empty<object?>(), new Vec3(0, 0, 0)),
        NodeKind.Line => ("直线", 2, 1, new object?[] { new Vec3(0, 0, 0), new Vec3(10, 10, 0) }, null),
        NodeKind.Circle => ("圆", 2, 1, new object?[] { new Vec3(0, 0, 0), 10.0 }, null),
        NodeKind.Arc => ("圆弧", 4, 1, new object?[] { new Vec3(0, 0, 0), 10.0, 0.0, 90.0 }, null),
        NodeKind.Rectangle => ("矩形", 2, 1, new object?[] { new Vec3(0, 0, 0), new Vec3(10, 10, 0) }, null),
        NodeKind.Polygon => ("多边形", 4, 1, new object?[] { new Vec3(0, 0, 0), 6, 10.0, true }, null),   // 内接开关(忠实原 PolygonNode: Center/Sides/Radius/Inscribed)
        NodeKind.Polyline => ("多段线", 2, 1, new object?[] { new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0) }, false }, null),
        NodeKind.Bake => ("烘焙", 1, 1, new object?[] { null }, null),
        _ => ("节点", 1, 1, new object?[] { null }, null)
    };
}
