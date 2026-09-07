using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>块体模型组的简易消息框（替代原 ErrorHandler.ShowInfo / ShowWarning / MessageBox 是否）。代码构建, 无 XAML。</summary>
internal static class BlockMsgBox
{
    public static Task InfoAsync(Window owner, string title, string message) => ShowAsync(owner, title, message, "确定", null, false);
    public static Task WarnAsync(Window owner, string title, string message) => ShowAsync(owner, title, message, "确定", null, true);
    /// <summary>是/否 确认; 返回 true = 是。</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string yes = "是", string no = "否") => ShowAsync(owner, title, message, yes, no, true);

    private static async Task<bool> ShowAsync(Window owner, string title, string message, string okText, string? cancelText, bool warn)
    {
        var win = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
        };
        var icon = new TextBlock { Text = warn ? "⚠" : "ⓘ", FontSize = 22, Foreground = new SolidColorBrush(Color.Parse(warn ? "#B45309" : "#0D6EFD")), Margin = new Thickness(16, 14, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 16, 16, 8), MaxWidth = 400 };
        var ok = new Button { Content = okText, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        ok.Classes.Add("primary");
        ok.Click += (_, _) => win.Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 4, 16, 14) };
        buttons.Children.Add(ok);
        if (cancelText != null)
        {
            var cancel = new Button { Content = cancelText, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
            cancel.Click += (_, _) => win.Close(false);
            buttons.Children.Add(cancel);
        }
        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(icon); body.Children.Add(text);
        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(buttons);
        win.Content = root;
        var r = await win.ShowDialog<bool?>(owner);
        return r == true;
    }
}
