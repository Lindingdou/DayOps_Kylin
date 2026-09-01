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
    private readonly NodeGraphUndoRedo _undoRedo;
    private const double NodeW = 120, TitleH = 26, RowH = 18, PortR = 5;

    private Node? _dragNode;
    private Node? _selected;
    private bool _dragSaved;
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
        KeyDown += OnKeyDown;

        // 预置演示：数字(50) → 圆半径 → 烘焙
        var num = _graph.AddNode(NodeKind.Number, 60, 90); num.Value = 50.0;
        var circle = _graph.AddNode(NodeKind.Circle, 300, 90);
        var bake = _graph.AddNode(NodeKind.Bake, 540, 110);
        _graph.Connect(num.Id, 0, circle.Id, 1);       // 数字 → 半径
        _graph.Connect(circle.Id, 0, bake.Id, 0);      // 圆 → 烘焙
        _undoRedo = new NodeGraphUndoRedo(_graph);      // 演示图作基线，之后的改动方可撤销
        Rebuild();
    }

    // 键盘：Delete 删除选中，Ctrl+Z 撤销，Ctrl+Y 重做
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { OnDeleteSelected(null, null!); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z) { OnUndo(null, null!); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Y) { OnRedo(null, null!); e.Handled = true; }
    }

    private void OnDeleteSelected(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) { Hint.Text = "先单击选中一个节点，再删除"; return; }
        _undoRedo.SaveState();                 // 改动前记录
        _graph.RemoveNode(_selected.Id);
        _selected = null; _pendingOut = null;
        Hint.Text = "已删除节点";
        Rebuild();
    }

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (!_undoRedo.CanUndo) { Hint.Text = "无可撤销"; return; }
        _undoRedo.Undo();
        _selected = null; _pendingOut = null; _dragNode = null;
        Hint.Text = "已撤销";
        Rebuild();
    }

    private void OnRedo(object? sender, RoutedEventArgs e)
    {
        if (!_undoRedo.CanRedo) { Hint.Text = "无可重做"; return; }
        _undoRedo.Redo();
        _selected = null; _pendingOut = null; _dragNode = null;
        Hint.Text = "已重做";
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

    // 保存节点图 → JSON 文件（忠实原 保存节点图）
    private async void OnSaveGraph(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "保存节点图",
            DefaultExtension = "json",
            SuggestedFileName = "graph.json",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("节点图 JSON") { Patterns = new[] { "*.json" } } }
        });
        if (file == null) return;
        try
        {
            System.IO.File.WriteAllText(file.Path.LocalPath, _graph.ToJson());
            Hint.Text = $"已保存 {System.IO.Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex) { Hint.Text = $"保存失败：{ex.Message}"; }
    }

    // 加载节点图 ← JSON 文件（忠实原 加载节点图：读盘前存撤销点，失败不动现图）
    private async void OnLoadGraph(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "加载节点图",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("节点图 JSON") { Patterns = new[] { "*.json" } } }
        });
        if (files.Count == 0) return;
        try
        {
            string json = System.IO.File.ReadAllText(files[0].Path.LocalPath);
            _undoRedo.SaveState();             // 载入可撤销
            _graph.LoadJson(json);
            _selected = null; _pendingOut = null; _dragNode = null;
            Hint.Text = $"已加载 {System.IO.Path.GetFileName(files[0].Path.LocalPath)}（{_graph.Nodes.Count} 节点）";
            Rebuild();
        }
        catch (Exception ex) { Hint.Text = $"加载失败：{ex.Message}"; }
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
        bool committed = false;
        void Commit()
        {
            if (committed) return;             // Enter+失焦双触发保护，避免重复入撤销栈
            committed = true;
            _undoRedo.SaveState();             // 改动前记录
            ApplyValue(n, tb.Text ?? "");
            if (Canvas.Children.Contains(tb)) Canvas.Children.Remove(tb);
            Rebuild();
        }
        tb.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) Commit(); else if (ke.Key == Key.Escape) { Canvas.Children.Remove(tb); } };
        tb.LostFocus += (_, _) => Commit();
        Canvas.Children.Add(tb);
        tb.Focus();
    }

    // 值编解码统一走 NodeGraph 规范实现（UI 卡片显示/内联编辑 与 JSON 存盘 同一套）
    private static string ValueText(Node n) => NodeGraph.EncodeValue(n);
    private static void ApplyValue(Node n, string text) => NodeGraph.DecodeValueInto(n, text);

    private void OnAddNode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s && Enum.TryParse<NodeKind>(s, out var kind))
        {
            double x = 40 + (_graph.Nodes.Count * 26) % 320;
            double y = 60 + (_graph.Nodes.Count * 22) % 320;
            _undoRedo.SaveState();             // 改动前记录
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
                BorderBrush = ReferenceEquals(n, _selected) ? Brushes.DodgerBlue : Brushes.SlateGray,
                BorderThickness = new Thickness(ReferenceEquals(n, _selected) ? 2 : 1),
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
            _undoRedo.SaveState();             // 改动前记录
            if (_graph.Connect(o.node, o.port, pr.NodeId, pr.Port)) { Hint.Text = "已连线"; Rebuild(); }
            _pendingOut = null;
        }
    }

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card || card.Tag is not Node n) return;
        e.Handled = true;
        _dragNode = n;
        _selected = n;                         // 单击=选中(供删除高亮)
        _dragSaved = false;
        var pos = e.GetPosition(Canvas);
        _dragOffset = new Point(pos.X - n.X, pos.Y - n.Y);
        e.Pointer.Capture(Canvas);
        Rebuild();                             // 刷新选中高亮
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode == null) return;
        if (!_dragSaved) { _undoRedo.SaveState(); _dragSaved = true; }   // 真正移动才入撤销栈(一次拖拽一步)
        var pos = e.GetPosition(Canvas);
        _dragNode.X = pos.X - _dragOffset.X;
        _dragNode.Y = pos.Y - _dragOffset.Y;
        Rebuild();
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragNode = null;
        _dragSaved = false;
        e.Pointer.Capture(null);
    }
}
