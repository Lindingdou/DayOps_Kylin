using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「正在连接数据库」进度窗口。代码构建、无 XAML（与本目录其它窗口一致）。
///
/// 为什么需要它: 连接是同步发起的, 服务器没开或地址填错时要等到超时才返回。
/// 在 UI 线程上直接连, 界面就整个僵住 —— 用户看到的是程序卡死, 而不是"正在连接"。
///
/// 这里把连接放到后台线程, 窗口只负责显示进度和允许放弃:
///   · 进度条是不确定态(圈圈转)—— 连接没有可报告的百分比, 假装有反而误导;
///   · 显示正在连哪台服务器, 顺带让用户自己发现"哦地址填错了";
///   · 「放弃」只是不再等待, 后台那次连接尝试仍会自然超时结束 ——
///     Npgsql 的同步 Open() 打断不了, 谎称"已取消"不如说清楚是"不等了"。
/// </summary>
internal static class DbConnectingDialog
{
    /// <summary>
    /// 显示进度窗口并在后台执行 <paramref name="connect"/>。
    /// 返回 (成功?, 异常)。用户中途放弃时返回 (false, null)。
    /// </summary>
    public static async Task<(bool ok, Exception? error)> RunAsync(Window owner, Func<GeoDatabase> connect)
    {
        var cfg = DbConnectionSettings.LoadEffective();

        var win = new Window
        {
            Title = "连接数据库",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            SystemDecorations = SystemDecorations.BorderOnly,   // 不给关闭按钮: 用「放弃」退出, 语义更准
        };

        var caption = new TextBlock
        {
            Text = "正在连接数据库…",
            FontSize = 14, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1F2328")),
        };

        var targetText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(cfg.Host)
                   ? "未设置服务器地址"
                   : $"{cfg.Host}:{cfg.Port}   ·   {cfg.Database}",
            FontSize = 12, Margin = new Avalonia.Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#57606A")),
            TextWrapping = TextWrapping.Wrap,
        };

        var bar = new ProgressBar
        {
            IsIndeterminate = true,   // 连接没有百分比可报, 假装有反而误导
            Height = 4, Margin = new Avalonia.Thickness(0, 14, 0, 0),
        };

        var hint = new TextBlock
        {
            Text = "若服务器未启动或地址不可达，最多等待 8 秒。",
            FontSize = 11, Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#8C959F")),
            TextWrapping = TextWrapping.Wrap,
        };

        var giveUp = new Button { Content = "放弃", MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right };
        giveUp.Margin = new Avalonia.Thickness(0, 12, 0, 0);

        var panel = new StackPanel { Margin = new Avalonia.Thickness(20, 18, 20, 16) };
        panel.Children.Add(caption);
        panel.Children.Add(targetText);
        panel.Children.Add(bar);
        panel.Children.Add(hint);
        panel.Children.Add(giveUp);
        win.Content = panel;

        var abandoned = new CancellationTokenSource();
        giveUp.Click += (_, _) => { abandoned.Cancel(); win.Close(); };

        (bool ok, Exception? error) result = (false, null);

        // 后台连, 前台转 —— 连接本身打断不了, 但至少界面不僵、用户可以不等。
        var work = Task.Run(() =>
        {
            try { connect(); return (true, (Exception?)null); }
            catch (Exception ex) { return (false, ex); }
        });

        _ = work.ContinueWith(t =>
        {
            if (abandoned.IsCancellationRequested) return;   // 已经放弃了就别再弹回来
            result = t.Result;
            Dispatcher.UIThread.Post(() => win.Close());
        }, TaskScheduler.Default);

        await win.ShowDialog(owner);
        return abandoned.IsCancellationRequested ? (false, null) : result;
    }
}
