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

        // 视口内移动时回报像素坐标（真实世界坐标需反投影，最小版先给屏幕系）
        Viewport.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(Viewport);
            CoordText.Text = $"视口 px  X {p.X:0}  Y {p.Y:0}";
        };
    }

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
