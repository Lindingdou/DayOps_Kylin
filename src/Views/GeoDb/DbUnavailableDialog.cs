using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「数据库不可用」提示窗口。代码构建、无 XAML（与本目录其它窗口一致）。
///
/// 为什么不复用简易消息框: 数据库连不上会让整组功能(103 个命令)全部失效, 这不是一句
/// "操作失败"能打发的。用户需要一眼看清三件事 —— 出了什么问题、连的是哪台服务器、下一步做什么。
///
/// 版面上的取舍:
///   · 正文只讲中文。底层报错(多是英文错误码)收进折叠的「详细信息」, 排查时才展开;
///     摆在正文只会让现场人员困惑, 但丢掉它远程支持又无从下手。
///   · 服务器地址单独一行显示 —— 最常见的故障就是配错了地址却没意识到。
///   · 不可自行解决的情况(库没初始化)不给"去配置"按钮: 让用户反复改设置比不提示更糟。
/// </summary>
internal static class DbUnavailableDialog
{
    /// <summary>用户在提示窗口里选了什么。</summary>
    public enum Choice
    {
        /// <summary>关掉了窗口, 不做处理。</summary>
        Dismiss,
        /// <summary>再连一次 —— 服务器可能刚起来, 或网络刚恢复, 不必先去改设置。</summary>
        Retry,
        /// <summary>去改连接设置。</summary>
        Configure,
    }

    /// <summary>显示提示, 返回用户的选择。</summary>
    public static async Task<Choice> ShowAsync(Window owner, DbConnectionDiagnosis.Result d)
    {
        var cfg = DbConnectionSettings.LoadEffective();

        var win = new Window
        {
            Title = "数据库不可用",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
        };

        // ── 顶部：警示图标 + 标题 + 说明 ──────────────────────────────────────
        var icon = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.Parse("#FEF3C7")),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "!", FontSize = 22, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#B45309")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var title = new TextBlock
        {
            Text = d.Title, FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1F2328")),
            TextWrapping = TextWrapping.Wrap,
        };
        var detail = new TextBlock
        {
            Text = d.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#57606A")), LineHeight = 20,
        };

        var textCol = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        textCol.Children.Add(title);
        textCol.Children.Add(detail);

        // ── 当前连的是哪台：配错地址是最常见的故障, 直接摆出来 ────────────────
        var target = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EEF2F6")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(cfg.Host)
                       ? "当前未设置服务器地址"
                       : $"当前连接：{cfg.Host}:{cfg.Port}   库名：{cfg.Database}   用户：{cfg.Username}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#424A53")),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        textCol.Children.Add(target);

        // ── 详细信息：默认折叠, 里面才是英文原文 ──────────────────────────────
        if (!string.IsNullOrWhiteSpace(d.Raw))
        {
            var raw = new TextBox
            {
                Text = d.Raw, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 110, FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            };
            textCol.Children.Add(new Expander
            {
                Header = "详细信息（提供给技术支持）",
                Margin = new Thickness(0, 10, 0, 0),
                FontSize = 12,
                Content = raw,
            });
        }

        var head = new Grid { Margin = new Thickness(18, 18, 18, 4) };
        head.ColumnDefinitions = new ColumnDefinitions("Auto,*");
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textCol, 1);
        head.Children.Add(icon);
        head.Children.Add(textCol);

        // ── 按钮 ─────────────────────────────────────────────────────────────
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 14, 18, 16),
        };

        if (d.Actionable)
        {
            // 「重新尝试连接」放在最前: 最常见的情形是服务器刚启动、网线刚插好、VPN 刚连上,
            // 这些都不需要改任何设置, 再连一次就好。让用户先去翻设置反而绕远路。
            var retry = new Button { Content = "重新尝试连接", MinWidth = 110, IsDefault = true };
            retry.Classes.Add("primary");
            retry.Click += (_, _) => win.Close(Choice.Retry);

            var go = new Button { Content = "配置数据库连接", MinWidth = 120 };
            go.Click += (_, _) => win.Close(Choice.Configure);

            var close = new Button { Content = "关闭", MinWidth = 72, IsCancel = true };
            close.Click += (_, _) => win.Close(Choice.Dismiss);

            buttons.Children.Add(retry);
            buttons.Children.Add(go);
            buttons.Children.Add(close);
        }
        else
        {
            // 改连接解决不了的(库没初始化), 只给"知道了" —— 别把用户往错误的方向引。
            // 也不给"重试": 库没建起来之前, 连一百次结果都一样。
            var ok = new Button { Content = "知道了", MinWidth = 88, IsDefault = true, IsCancel = true };
            ok.Classes.Add("primary");
            ok.Click += (_, _) => win.Close(Choice.Dismiss);
            buttons.Children.Add(ok);
        }

        var root = new StackPanel();
        root.Children.Add(head);
        root.Children.Add(buttons);
        win.Content = root;

        return await win.ShowDialog<Choice?>(owner) ?? Choice.Dismiss;
    }
}
