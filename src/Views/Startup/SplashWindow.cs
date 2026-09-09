using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.Startup;

/// <summary>
/// 启动欢迎界面（忠实原 PitMineApp.Startup.SplashWindow）：白卡 + 中煤官方蓝 #0086D1 点缀，
/// 中煤标志 + 「中国中煤 / CHINA COAL」→ 品牌短横条 → 产品名 DayOps → 中文全名 →
/// 进度区（状态 + 进度条 + 当前模块 + 百分比）→ 版权栏。
///
/// 自包含品牌配色，不引用应用主题画刷：它在主窗口建起来之前就要显示，那时主题字典还没并进来。
/// 代码构建，无 XAML。
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly TextBlock _status = new() { Text = "正在启动…", Foreground = Brush.Parse("#44505E"), FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _module = new() { Text = "", Foreground = Brush.Parse("#9AA6B4"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _percent = new() { Text = "", Foreground = Brush.Parse("#9AA6B4"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _fill;
    private readonly Border _track;
    private double _fraction;


    public SplashWindow()
    {
        Title = "DayOps";
        // 560×376 是原版 WPF 的尺寸；Avalonia 的字形度量更高一档，同样的版式在 376 高里会把
        // 进度区与版权栏挤出窗外，故加高到 424（宽度不动，观感与原版一致）。
        Width = 560; Height = 424;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SystemDecorations = SystemDecorations.None;
        // 不用透明窗：原版靠 AllowsTransparency 做圆角+投影，但透明层在麒麟/信创的合成器上时灵时不灵
        // (窗口在、内容不上屏，看着就是"闪一下什么都没有")。这里改成实心白卡 + 细边框，观感一致、各平台都稳。
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── 企业标识：中煤标志 + 字标（英文字标置于标志下方）──
        var logo = new Canvas { Width = 56, Height = 56, ClipToBounds = true };
        try
        {
            var geom = Geometry.Parse(BrandLogo.Path);
            logo.Children.Add(new Path
            {
                Data = geom, Fill = Brush.Parse(BrandLogo.Blue),
                RenderTransform = new ScaleTransform(56.0 / 1024, 56.0 / 1024),
                RenderTransformOrigin = RelativePoint.TopLeft,
            });
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("启动页", "标志解析失败(不影响启动): " + ex.Message); }

        var brand = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetRow(logo, 0); Grid.SetColumn(logo, 0); brand.Children.Add(logo);
        var name = new TextBlock
        {
            Text = "中国中煤", VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush.Parse("#1A2430"), FontSize = 30, FontWeight = FontWeight.Bold,
            Margin = new Thickness(14, 0, 0, 0),
        };
        Grid.SetRow(name, 0); Grid.SetColumn(name, 1); brand.Children.Add(name);
        var en = new TextBlock { Text = "CHINA COAL", Foreground = Brush.Parse("#6B7684"), FontSize = 14, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(en, 1); Grid.SetColumn(en, 0); Grid.SetColumnSpan(en, 2); brand.Children.Add(en);

        _track = new Border { CornerRadius = new CornerRadius(3), Background = Brush.Parse("#E9EDF2"), Height = 6 };
        _fill = new Border
        {
            CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Height = 6,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#0072B5"), 0), new GradientStop(Color.Parse("#0086D1"), 1) },
            },
        };
        _track.Child = _fill;
        _track.SizeChanged += (_, _) => ApplyFill();

        var progress = new StackPanel();   // 透明: 直接坐在白卡片上(别塞底色, 会在卡片上糊出一整条色块)
        progress.Children.Add(_status);
        progress.Children.Add(_track);
        var footRow = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_module, 0); Grid.SetColumn(_percent, 1);
        footRow.Children.Add(_module); footRow.Children.Add(_percent);
        progress.Children.Add(footRow);

        var content = new Grid
        {
            Margin = new Thickness(36, 26, 36, 14),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
        };
        Grid.SetRow(brand, 0); content.Children.Add(brand);
        var bar = new Border
        {
            Width = 54, Height = 5, CornerRadius = new CornerRadius(2.5),
            Background = Brush.Parse(BrandLogo.Blue), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 0),
        };
        Grid.SetRow(bar, 1); content.Children.Add(bar);
        var product = new TextBlock { Text = "DayOps", Foreground = Brush.Parse("#1A2430"), FontSize = 28, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(product, 2); content.Children.Add(product);
        var full = new TextBlock { Text = "中煤平朔露天煤矿生产计划决策支撑系统", Foreground = Brush.Parse("#6B7684"), FontSize = 14, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(full, 3); content.Children.Add(full);
        Grid.SetRow(progress, 5); content.Children.Add(progress);

        var copyright = new Border
        {
            // 版权条：极浅灰底 + 一条发丝分隔线, 与白卡片拉开层次但不抢眼
            Background = Brush.Parse("#F5F7FA"), Padding = new Thickness(0, 10),
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brush.Parse("#E9EDF3"),
            Child = new TextBlock
            {
                Text = "© 中煤平朔集团有限公司 · 版权所有",
                Foreground = Brush.Parse("#9AA6B4"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        var card = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(content, 0); card.Children.Add(content);
        Grid.SetRow(copyright, 1); card.Children.Add(copyright);

        Content = new Border
        {
            Background = Brushes.White,
            BorderThickness = new Thickness(1), BorderBrush = Brush.Parse("#E5E9F0"),
            Child = card,
        };
    }

    /// <summary>更新进度（0~1）、状态文案与当前模块名。</summary>
    public void SetProgress(double frac, string? status = null, string? module = null)
    {
        _fraction = Math.Clamp(frac, 0, 1);
        ApplyFill();
        if (status != null) _status.Text = status;
        if (module != null) _module.Text = module;
        _percent.Text = $"{(int)Math.Round(_fraction * 100)}%";
    }

    private void ApplyFill()
    {
        double track = _track.Bounds.Width;
        _fill.Width = track > 0 ? track * _fraction : 0;
    }
}
