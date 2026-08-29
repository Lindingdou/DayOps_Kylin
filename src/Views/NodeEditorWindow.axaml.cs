using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Nodes;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 节点编辑器 —— 画布式可视化脚本（对应 Windows 版节点编辑器 MVP）。
/// 添加节点、拖拽移动、点输出口再点输入口连线；模型在 <see cref="NodeGraph"/>（可单测）。
/// </summary>
public partial class NodeEditorWindow : Window
{
    private sealed record PortRef(int NodeId, int Port, bool IsInput);

    private readonly NodeGraph _graph = new();
    private const double NodeW = 120, TitleH = 26, RowH = 18, PortR = 5;

    private Node? _dragNode;
    private Point _dragOffset;
    private (int node, int port)? _pendingOut;

    public NodeEditorWindow()
    {
        InitializeComponent();

        Canvas.PointerMoved += OnCanvasMoved;
        Canvas.PointerReleased += OnCanvasReleased;

        // 预置演示：数字 → 圆
        var num = _graph.AddNode(NodeKind.Number, 60, 90);
        var circle = _graph.AddNode(NodeKind.Circle, 340, 120);
        _graph.Connect(num.Id, 0, circle.Id, 0);
        Rebuild();
    }

    private void OnAddNode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s && Enum.TryParse<NodeKind>(s, out var kind))
        {
            double x = 40 + (_graph.Nodes.Count * 26) % 320;
            double y = 60 + (_graph.Nodes.Count * 22) % 320;
            _graph.AddNode(kind, x, y);
            Rebuild();
        }
    }

    private double NodeHeight(Node n) => TitleH + Math.Max(1, Math.Max(n.InputCount, n.OutputCount)) * RowH + 8;
    private Point InPort(Node n, int i) => new(n.X, n.Y + TitleH + i * RowH + RowH * 0.5);
    private Point OutPort(Node n, int i) => new(n.X + NodeW, n.Y + TitleH + i * RowH + RowH * 0.5);

    private void Rebuild()
    {
        Canvas.Children.Clear();

        // 连线（画在节点下层）
        foreach (var c in _graph.Connections)
        {
            var from = _graph.Nodes.Find(n => n.Id == c.FromNode);
            var to = _graph.Nodes.Find(n => n.Id == c.ToNode);
            if (from == null || to == null) continue;
            var a = OutPort(from, c.FromPort);
            var z = InPort(to, c.ToPort);
            Canvas.Children.Add(new Line { StartPoint = a, EndPoint = z, Stroke = Brushes.SteelBlue, StrokeThickness = 2 });
        }

        // 节点卡片 + 端口
        foreach (var n in _graph.Nodes)
        {
            var card = new Border
            {
                Width = NodeW,
                Height = NodeHeight(n),
                Background = Brushes.White,
                BorderBrush = Brushes.SlateGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Tag = n,
                Child = new TextBlock { Text = n.Title, Margin = new Thickness(8, 5, 0, 0), FontWeight = FontWeight.SemiBold }
            };
            Canvas.SetLeft(card, n.X);
            Canvas.SetTop(card, n.Y);
            card.PointerPressed += OnNodePressed;
            Canvas.Children.Add(card);

            for (int i = 0; i < n.InputCount; i++) AddPort(InPort(n, i), new PortRef(n.Id, i, true));
            for (int i = 0; i < n.OutputCount; i++) AddPort(OutPort(n, i), new PortRef(n.Id, i, false));
        }
    }

    private void AddPort(Point p, PortRef pr)
    {
        var dot = new Ellipse
        {
            Width = PortR * 2,
            Height = PortR * 2,
            Fill = pr.IsInput ? Brushes.DarkOrange : Brushes.SeaGreen,
            Tag = pr
        };
        Canvas.SetLeft(dot, p.X - PortR);
        Canvas.SetTop(dot, p.Y - PortR);
        dot.PointerPressed += OnPortPressed;
        Canvas.Children.Add(dot);
    }

    private void OnPortPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Ellipse dot || dot.Tag is not PortRef pr) return;
        e.Handled = true;
        if (!pr.IsInput)
        {
            _pendingOut = (pr.NodeId, pr.Port);
            Hint.Text = "已选输出口 → 点一个输入口连线";
        }
        else if (_pendingOut is { } o)
        {
            if (_graph.Connect(o.node, o.port, pr.NodeId, pr.Port)) { Hint.Text = "已连线"; Rebuild(); }
            _pendingOut = null;
        }
    }

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card || card.Tag is not Node n) return;
        e.Handled = true;
        _dragNode = n;
        var pos = e.GetPosition(Canvas);
        _dragOffset = new Point(pos.X - n.X, pos.Y - n.Y);
        e.Pointer.Capture(Canvas);
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode == null) return;
        var pos = e.GetPosition(Canvas);
        _dragNode.X = pos.X - _dragOffset.X;
        _dragNode.Y = pos.Y - _dragOffset.Y;
        Rebuild();
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragNode = null;
        e.Pointer.Capture(null);
    }
}
