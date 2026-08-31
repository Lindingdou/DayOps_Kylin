using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Draw;
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
    private readonly Action<System.Collections.Generic.List<SceneEntity>>? _onBake;

    public NodeEditorWindow() : this(null) { }

    /// <summary>onBake：点"求值到场景"时，把所有 烘焙 节点产出的几何交给宿主(加入主绘图场景)。</summary>
    public NodeEditorWindow(Action<System.Collections.Generic.List<SceneEntity>>? onBake)
    {
        InitializeComponent();
        _onBake = onBake;

        Canvas.PointerMoved += OnCanvasMoved;
        Canvas.PointerReleased += OnCanvasReleased;

        // 预置演示：数字(50) → 圆半径 → 烘焙
        var num = _graph.AddNode(NodeKind.Number, 60, 90); num.Value = 50.0;
        var circle = _graph.AddNode(NodeKind.Circle, 300, 90);
        var bake = _graph.AddNode(NodeKind.Bake, 540, 110);
        _graph.Connect(num.Id, 0, circle.Id, 1);       // 数字 → 半径
        _graph.Connect(circle.Id, 0, bake.Id, 0);      // 圆 → 烘焙
        Rebuild();
    }

    // 求值所有 烘焙 节点 → 几何交宿主入场景
    private void OnBakeToScene(object? sender, RoutedEventArgs e)
    {
        var geoms = _graph.EvaluateBakes();
        if (geoms.Count == 0) { Hint.Text = "无产出：请添加 烘焙 节点并把几何连到其输入口"; return; }
        if (_onBake == null) { Hint.Text = $"求值出 {geoms.Count} 个几何（独立打开时无宿主场景可接收）"; return; }
        _onBake(geoms);
        Hint.Text = $"已烘焙 {geoms.Count} 个几何到主场景";
    }

    // 参数节点值编辑（双击）：Number/String/Bool/Point → 弹出 TextBox 改值
    private void EditParamValue(Node n)
    {
        if (n.Kind is not (NodeKind.Number or NodeKind.String or NodeKind.Bool or NodeKind.Point)) return;
        var tb = new TextBox
        {
            Width = NodeW, Text = ValueText(n),
            Watermark = n.Kind switch { NodeKind.Point => "x,y[,z]", NodeKind.Bool => "true/false", _ => "值" }
        };
        Canvas.SetLeft(tb, n.X); Canvas.SetTop(tb, n.Y + TitleH);
        void Commit()
        {
            ApplyValue(n, tb.Text ?? "");
            if (Canvas.Children.Contains(tb)) Canvas.Children.Remove(tb);
            Rebuild();
        }
        tb.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) Commit(); else if (ke.Key == Key.Escape) { Canvas.Children.Remove(tb); } };
        tb.LostFocus += (_, _) => Commit();
        Canvas.Children.Add(tb);
        tb.Focus();
    }

    private static string ValueText(Node n) => n.Kind switch
    {
        NodeKind.Number => n.Value is double d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0",
        NodeKind.String => n.Value as string ?? "",
        NodeKind.Bool => n.Value is bool b && b ? "true" : "false",
        NodeKind.Point => n.Value is Vec3 v ? $"{v.X},{v.Y},{v.Z}" : "0,0,0",
        _ => ""
    };

    private static void ApplyValue(Node n, string text)
    {
        text = text.Trim();
        switch (n.Kind)
        {
            case NodeKind.Number:
                if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) n.Value = d;
                break;
            case NodeKind.String: n.Value = text; break;
            case NodeKind.Bool: n.Value = text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1"; break;
            case NodeKind.Point:
                var parts = text.Split(',');
                double px = 0, py = 0, pz = 0;
                if (parts.Length >= 1) double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out px);
                if (parts.Length >= 2) double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out py);
                if (parts.Length >= 3) double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pz);
                n.Value = new Vec3(px, py, pz);
                break;
        }
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
            bool isParam = n.Kind is NodeKind.Number or NodeKind.String or NodeKind.Bool or NodeKind.Point;
            string label = isParam ? $"{n.Title} = {ValueText(n)}" : n.Title;
            var card = new Border
            {
                Width = NodeW,
                Height = NodeHeight(n),
                Background = n.Kind == NodeKind.Bake ? new SolidColorBrush(Color.Parse("#E8F5E9")) : Brushes.White,
                BorderBrush = Brushes.SlateGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Tag = n,
                Child = new TextBlock { Text = label, Margin = new Thickness(8, 5, 6, 0), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }
            };
            Canvas.SetLeft(card, n.X);
            Canvas.SetTop(card, n.Y);
            card.PointerPressed += OnNodePressed;
            if (isParam) card.DoubleTapped += (_, _) => EditParamValue(n);
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
