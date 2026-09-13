using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「生产计划编制」各窗共用的控件工厂：原 WPF 窗的 teal 标题栏 / GroupBox 分组 / 标签+输入 行 / 信息框，
/// 都收在这里（原各 XAML 里各写一份）。配色走主题资源；teal 与原 PlanLib 一致（#14B8A6 → #0F766E）。
/// </summary>
internal static class PlanUi
{
    public static readonly Color TealDark = Color.FromRgb(0x0F, 0x76, 0x6E);
    public static readonly Color Teal = Color.FromRgb(0x14, 0xB8, 0xA6);
    public static readonly IBrush TealBrush = new SolidColorBrush(TealDark);
    public static readonly IBrush OrangeBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));

    /// <summary>原 PlanLib 窗口统一的 teal 渐变标题栏：标题 + 副题。</summary>
    public static Border Header(string title, string subtitle, Color? from = null, Color? to = null)
    {
        var sp = new DockPanel();   // 标题靠左定宽，副标题占余下宽度可换行（横排 StackPanel 会把长副标题裁掉）
        var t = new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(t, Avalonia.Controls.Dock.Left); sp.Children.Add(t);
        sp.Children.Add(new TextBlock
        {
            Text = subtitle, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xFB, 0xF1)), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        return new Border
        {
            Padding = new Thickness(16, 10),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(from ?? Teal, 0), new GradientStop(to ?? TealDark, 1) },
            },
            Child = sp,
        };
    }

    /// <summary>GroupBox 等价：标题 + 边框 + 内容。</summary>
    public static Border Group(string header, Control content, Thickness? margin = null, double padding = 10)
    {
        var head = new TextBlock { Text = header, FontWeight = FontWeight.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) };
        RoadUi.Theme(head, TextBlock.ForegroundProperty, "Theme.Text.Primary");
        var dock = new DockPanel();
        DockPanel.SetDock(head, Avalonia.Controls.Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(content);
        var b = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(padding),
            Margin = margin ?? new Thickness(0, 0, 0, 10), Child = dock,
        };
        RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        RoadUi.Theme(b, Border.BackgroundProperty, "Theme.Panel.Background");
        return b;
    }

    /// <summary>浅色信息框（原 Theme.InfoBox）。</summary>
    public static Border InfoBox(Control content, Thickness? margin = null)
    {
        var b = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 6),
            Margin = margin ?? new Thickness(0, 0, 0, 8), Child = content,
        };
        RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        RoadUi.Theme(b, Border.BackgroundProperty, "Theme.Panel.Background2");
        return b;
    }

    /// <summary>「标签(定宽) + 控件 [+ 尾控件]」一行（原 Grid 120/220/Auto 三列）。</summary>
    public static Grid LabeledRow(string label, Control ctrl, Control? tail = null, double labelWidth = 120, double ctrlWidth = double.NaN, Thickness? margin = null, string? tip = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},{(double.IsNaN(ctrlWidth) ? "*" : ctrlWidth.ToString(CultureInfo.InvariantCulture))},Auto"), Margin = margin ?? new Thickness(0, 0, 0, 8) };
        var l = RoadUi.Lbl(label); l.VerticalAlignment = VerticalAlignment.Center;
        if (tip != null) ToolTip.SetTip(l, tip);
        Grid.SetColumn(l, 0); g.Children.Add(l);
        ctrl.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(ctrl, 1); g.Children.Add(ctrl);
        if (tail != null) { tail.Margin = new Thickness(10, 0, 0, 0); tail.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(tail, 2); g.Children.Add(tail); }
        return g;
    }

    /// <summary>teal 粗体读数（原 Foreground=#0F766E FontWeight=Bold）。</summary>
    public static TextBlock Value(string text, double fontSize = 13)
        => new() { Text = text, FontWeight = FontWeight.Bold, Foreground = TealBrush, FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center };

    public static TextBox Box(string text, double width = 80)
        => new() { Text = text, Width = width, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };

    public static ComboBox Combo(IEnumerable<string> items, int selected = 0, double width = 220)
    {
        var cb = RoadUi.Combo(items, width);
        cb.Margin = new Thickness(0);
        cb.SelectedIndex = selected;
        return cb;
    }

    public static string Num(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);
    public static string NumOpt(double v) => v == 0 ? "" : v.ToString("0.###", CultureInfo.CurrentCulture);
    public static double D(TextBox tb)
        => double.TryParse(tb.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var v) ? v
         : double.TryParse(tb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;

    /// <summary>底部操作条（原 Theme.Surface 背景 + 上边线）。</summary>
    public static Border Footer(Control content)
    {
        var b = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 10), Child = content };
        RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        RoadUi.Theme(b, Border.BackgroundProperty, "Theme.Panel.Background");
        return b;
    }

    /// <summary>三行骨架：标题栏 / 主体(拉伸) / 页脚。</summary>
    public static Grid Shell(Control header, Control body, Control footer)
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(header, 0); Grid.SetRow(body, 1); Grid.SetRow(footer, 2);
        g.Children.Add(header); g.Children.Add(body); g.Children.Add(footer);
        return g;
    }

    /// <summary>可编辑表格（原 DataGrid CanUserAddRows）。列 = (表头, 绑定属性, 宽, 只读)。</summary>
    public static DataGrid EditableTable(IReadOnlyList<(string Header, string Path, double Width, bool ReadOnly)> cols, double height = 160)
    {
        var dg = new DataGrid
        {
            IsReadOnly = false, AutoGenerateColumns = false, CanUserReorderColumns = false, CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column, FontSize = 12.5, Height = height,
        };
        foreach (var (h, p, w, ro) in cols)
            dg.Columns.Add(new DataGridTextColumn
            {
                Header = h, Binding = new Avalonia.Data.Binding(p) { Mode = ro ? Avalonia.Data.BindingMode.OneWay : Avalonia.Data.BindingMode.TwoWay },
                Width = w <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w), IsReadOnly = ro,
            });
        return dg;
    }

    /// <summary>
    /// 表头按 Kylin 正文字号(表头实为 14px + 列头内边距 + 排序指示位)估最小列宽，把 XAML 里搬来的定宽撑到不截字（原版 WPF 13px 的定宽在这儿差一截）。
    /// 星号列不动；估值：汉字 14.5 · 字母数字 8.5 · 标点/上标 7 + 表头附加 48（实测 40 仍截「时相」）。
    /// </summary>
    public static DataGrid FitHeaders(DataGrid dg)
    {
        foreach (var c in dg.Columns)
        {
            if (c.Header is not string h || c.Width.IsStar || c.Width.IsAuto) continue;
            double w = 48;
            foreach (char ch in h) w += ch > 0x2E80 ? 14.5 : char.IsLetterOrDigit(ch) ? 8.5 : 7;
            if (c.Width.Value < w) c.Width = new DataGridLength(Math.Ceiling(w));
        }
        return dg;
    }

    /// <summary>只读文本列（支持嵌套路径如 "Result.CompletionRatePct" 与格式串如 "{0:F1}"；原 XAML 的 StringFormat 绑定）。</summary>
    public static DataGridTextColumn FmtCol(string header, string path, double width, string? fmt = null)
    {
        var b = new Avalonia.Data.Binding(path) { Mode = Avalonia.Data.BindingMode.OneWay };
        if (fmt != null) b.StringFormat = fmt;
        return new DataGridTextColumn { Header = header, Binding = b, IsReadOnly = true, Width = width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) };
    }

    /// <summary>只读表格（同 <see cref="RoadUi.Table"/>）+ 表头撑宽。</summary>
    public static DataGrid Table(IReadOnlyList<(string Header, string Path, double Width)> cols, bool multi = true) => FitHeaders(RoadUi.Table(cols, multi));

    public static void Place(Window w, double width, double height)
    {
        w.Width = width; w.Height = height;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowFit.ClampToScreen(w);
    }
}
