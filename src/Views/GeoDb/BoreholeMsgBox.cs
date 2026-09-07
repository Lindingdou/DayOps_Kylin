using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>钻孔管理组的简易消息框(替代原 WPF MessageBox 的 是/否 与 确定): 代码构建, 无 XAML。</summary>
internal static class BoreholeMsgBox
{
    /// <summary>是/否 确认; 返回 true = 是。</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string yes = "是", string no = "否")
        => ShowAsync(owner, title, message, yes, no);

    /// <summary>仅「确定」的提示。</summary>
    public static async Task InfoAsync(Window owner, string title, string message)
        => await ShowAsync(owner, title, message, "确定", null);

    private static async Task<bool> ShowAsync(Window owner, string title, string message, string okText, string? cancelText)
    {
        var win = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
        };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 16, 16, 8) };
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
        var root = new StackPanel();
        root.Children.Add(text);
        root.Children.Add(buttons);
        win.Content = root;
        var r = await win.ShowDialog<bool?>(owner);
        return r == true;
    }
}
