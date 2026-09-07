using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 设备管理组窗口用的简易消息框(原 WPF MessageBox 的 OK / YesNo / YesNoCancel 三种形态)。
/// Avalonia 无内建 MessageBox, 以小窗口 + ShowDialog 等价实现。
/// </summary>
internal static class EquipmentMessageBox
{
    public static Task Info(Window owner, string text, string title = "提示") => Show(owner, text, title, new[] { ("确定", (bool?)true) });

    public static async Task<bool> Confirm(Window owner, string text, string title = "确认")
        => await Show(owner, text, title, new[] { ("是", (bool?)true), ("否", (bool?)false) }) == true;

    /// <summary>是/否/取消: true / false / null(取消或关闭)。</summary>
    public static Task<bool?> YesNoCancel(Window owner, string text, string title = "确认")
        => Show(owner, text, title, new[] { ("是", (bool?)true), ("否", (bool?)false), ("取消", (bool?)null) });

    private static async Task<bool?> Show(Window owner, string text, string title, (string label, bool? result)[] buttons)
    {
        var win = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
        };
        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 14), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        bool? result = null; bool closedByButton = false;
        foreach (var (label, r) in buttons)
        {
            var b = new Button { Content = label, Padding = new Thickness(18, 5), MinWidth = 72 };
            if (r == true) b.Classes.Add("primary");
            b.Click += (_, _) => { result = r; closedByButton = true; win.Close(); };
            row.Children.Add(b);
        }
        root.Children.Add(row);
        win.Content = root;
        await win.ShowDialog(owner);
        return closedByButton ? result : null;
    }
}
