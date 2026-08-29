using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PitMine3D.Kylin.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // OpenGL 上下文就绪后，把真实后端版本显示到视口与状态栏
        Viewport.GlReady += backend =>
        {
            GlInfo.Text = $"渲染后端: {backend}";
            StatusMsg.Text = $"OpenGL 就绪 · {backend}";
        };

        // 视口交互：在宿主 Panel（可命中）上收指针事件，转发到相机。
        // OpenGlControlBase 自身无背景时命中测试不可靠，直接在其上收事件在部分后端收不到，
        // 故统一在 ViewportHost（Background=Transparent → 全区可命中）上处理。
        ViewportHost.PointerPressed += (_, e) =>
        {
            _dragging = true;
            _lastPointer = e.GetPosition(ViewportHost);
            e.Pointer.Capture(ViewportHost);
        };
        ViewportHost.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(ViewportHost);
            CoordText.Text = $"视口 px  X {p.X:0}  Y {p.Y:0}";
            if (_dragging)
            {
                Viewport.Orbit((p.X - _lastPointer.X) * 0.01, (p.Y - _lastPointer.Y) * 0.01);
                _lastPointer = p;
            }
        };
        ViewportHost.PointerReleased += (_, e) =>
        {
            _dragging = false;
            e.Pointer.Capture(null);
        };
        ViewportHost.PointerWheelChanged += (_, e) =>
            Viewport.Zoom(e.Delta.Y > 0 ? 0.9 : 1.1);
    }

    private bool _dragging;
    private Avalonia.Point _lastPointer;

    // Ribbon 按钮 → 回显命令（证明整条 UI 已接线）
    private void OnRibbonCommand(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is string cmd)
        {
            StatusMsg.Text = $"命令: {cmd}";
            CommandInput.Text = cmd;
            CommandInput.CaretIndex = cmd.Length;
        }
    }

    // 命令行回车 → 执行回显
    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            StatusMsg.Text = $"执行: {tb.Text.Trim()}";
            tb.Text = string.Empty;
        }
    }
}
