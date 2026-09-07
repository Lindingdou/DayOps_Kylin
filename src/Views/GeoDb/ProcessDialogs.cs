using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「工艺参数管理」组四个窗口共用的小对话框(原 WPF 代码内联的 PromptText 窗 + MessageBox 提示/确认)。
/// </summary>
internal static class ProcessDialogs
{
    /// <summary>单行文本输入(原 PromptText: 420×160, 确定/取消); 取消返回 null。</summary>
    public static async Task<string?> PromptTextAsync(Window owner, string title, string prompt)
    {
        var w = new Window
        {
            Title = title, Width = 420, Height = 160, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        w.Classes.Add("geodb");
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
        sp.Children.Add(tb);
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Spacing = 8 };
        var ok = new Button { Content = "确定", Padding = new Thickness(20, 4, 20, 4), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(20, 4, 20, 4), IsCancel = true };
        bp.Children.Add(ok); bp.Children.Add(cancel);
        sp.Children.Add(bp);
        w.Content = sp;
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.Close(true); };
        cancel.Click += (_, _) => w.Close(false);
        w.Opened += (_, _) => tb.Focus();
        await w.ShowDialog<bool>(owner);
        return result;
    }

    /// <summary>确认框(原 MessageBox OKCancel): 确定 → true。</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var w = new Window
        {
            Title = title, Width = 460, MinHeight = 150, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        w.Classes.Add("geodb");
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Spacing = 8 };
        var ok = new Button { Content = "确定", Padding = new Thickness(20, 4, 20, 4), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(20, 4, 20, 4), IsCancel = true };
        bp.Children.Add(ok); bp.Children.Add(cancel);
        sp.Children.Add(bp);
        w.Content = sp;
        ok.Click += (_, _) => w.Close(true);
        cancel.Click += (_, _) => w.Close(false);
        return await w.ShowDialog<bool>(owner);
    }

    /// <summary>信息框(原 MessageBox OK): 内容可滚动(历史列表等长文本)。</summary>
    public static async Task InfoAsync(Window owner, string title, string message)
    {
        var w = new Window
        {
            Title = title, Width = 560, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        w.Classes.Add("geodb");
        var dock = new DockPanel { Margin = new Thickness(16) };
        var ok = new Button { Content = "确定", Padding = new Thickness(24, 4, 24, 4), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(ok, Avalonia.Controls.Dock.Bottom);
        dock.Children.Add(ok);
        dock.Children.Add(new ScrollViewer
        {
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontFamily = new Avalonia.Media.FontFamily("Consolas, Microsoft YaHei, Noto Sans CJK SC, monospace") }
        });
        w.Content = dock;
        ok.Click += (_, _) => w.Close(true);
        await w.ShowDialog<bool>(owner);
    }
}
