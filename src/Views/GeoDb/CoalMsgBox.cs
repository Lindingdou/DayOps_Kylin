using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>煤质管理组用的简易消息框(替代原 WPF MessageBox): 提示 / 确认 / 多按钮询问。</summary>
internal static class CoalMsgBox
{
    /// <summary>信息提示(单「确定」)。</summary>
    public static Task ShowAsync(Window owner, string title, string text) => AskAsync(owner, title, text, "确定");

    /// <summary>确认(确定/取消), 确定返回 true。</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string text, string ok = "确定", string cancel = "取消")
        => await AskAsync(owner, title, text, ok, cancel) == ok;

    /// <summary>多按钮询问, 返回所按按钮文本; 关闭窗口返回 null。</summary>
    public static async Task<string?> AskAsync(Window owner, string title, string text, params string[] buttons)
    {
        if (!owner.IsVisible)
        {
            // 构造期(窗口尚未显示)触发的提示: 等 owner 打开后再弹, 免 ShowDialog 无可见 owner 抛异常
            var tcs = new TaskCompletionSource<string?>();
            owner.Opened += async (_, _) => { try { tcs.TrySetResult(await AskAsync(owner, title, text, buttons)); } catch { tcs.TrySetResult(null); } };
            return await tcs.Task;
        }
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
        };
        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = 420 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var b in buttons)
        {
            var btn = new Button { Content = b, MinWidth = 76, Padding = new Thickness(14, 5) };
            if (b == buttons[0]) btn.Classes.Add("primary");
            var cap = b;
            btn.Click += (_, _) => dlg.Close(cap);
            row.Children.Add(btn);
        }
        root.Children.Add(row);
        dlg.Content = root;
        return await dlg.ShowDialog<string?>(owner);
    }
}
