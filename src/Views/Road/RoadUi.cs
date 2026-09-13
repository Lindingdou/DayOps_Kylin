using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Views.GeoDb;

namespace PitMine3D.Kylin.Views.Road;

/// <summary>「道路运输系统」各窗共用的小控件工厂（原各 WPF 窗里各自一份 MakeBtn/Lbl…，这里合成一处）。配色走主题资源。</summary>
internal static class RoadUi
{
    public static readonly FontFamily Mono = new("Consolas, Courier New, monospace");

    public static void Theme(StyledElement c, AvaloniaProperty prop, string key) => c.Bind(prop, c.GetResourceObservable(key));

    public static TextBlock Text(string text, double fontSize = 13, bool wrap = true, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = fontSize, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (bold) t.FontWeight = FontWeight.SemiBold;
        Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Body");
        return t;
    }

    /// <summary>灰色说明字（随主题）。</summary>
    public static TextBlock Hint(string text, double fontSize = 11.5)
    {
        var t = new TextBlock { Text = text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap };
        Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        return t;
    }

    /// <summary>橙色警示字（未读到共享约束 / 校验告警）。</summary>
    public static TextBlock Warn(string text, double fontSize = 12)
        => new() { Text = text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00)) };

    public static TextBlock Lbl(string text) => Text(text, 12, false);

    public static Button Btn(string text, Action onClick, double minWidth = 84, bool bold = false, bool primary = false)
    {
        var b = new Button { Content = text, MinWidth = minWidth, Height = 28, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 0), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (bold) b.FontWeight = FontWeight.Bold;
        if (primary) b.Classes.Add("primary");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Small(string text, Action onClick)
    {
        var b = new Button { Content = text, Height = 24, Padding = new Thickness(8, 0), Margin = new Thickness(0, 0, 6, 0), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static TextBox Box(string text, double width, double height = 26)
        => new() { Text = text, Width = width, Height = height, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2) };

    public static TextBox NumBox(double value, double width = 64)
        => new() { Text = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Width = width, Height = 24, HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };

    /// <summary>等宽只读多行文本区（矩阵 / 报表 / 清单）。</summary>
    public static TextBox Mono2(string text, double fontSize = 12.5)
        => new()
        {
            Text = text, IsReadOnly = true, FontFamily = Mono, FontSize = fontSize, TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true, BorderThickness = new Thickness(0), Padding = new Thickness(12),
        };

    public static ComboBox Combo(IEnumerable<string> items, double width = 220)
    {
        var cb = new ComboBox { Height = 28, Width = width, FontSize = 13, Margin = new Thickness(0, 2, 0, 6) };
        foreach (var s in items) cb.Items.Add(s);
        return cb;
    }

    public static StackPanel Row(params Control[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static StackPanel Foot(params Control[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static Border Sep() => new() { Width = 1, Margin = new Thickness(4, 2, 8, 2), Background = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) };

    /// <summary>四行布局：工具栏 / 主体(拉伸) / 底栏 / 页脚。</summary>
    public static Grid Rows(Control top, Control middle, Control bottom, Control foot)
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        Avalonia.Controls.Grid.SetRow(top, 0); Avalonia.Controls.Grid.SetRow(middle, 1); Avalonia.Controls.Grid.SetRow(bottom, 2); Avalonia.Controls.Grid.SetRow(foot, 3);
        g.Children.Add(top); g.Children.Add(middle); g.Children.Add(bottom); g.Children.Add(foot);
        return g;
    }

    /// <summary>表格（DataGrid，只读、可多选）。列 = (表头, 绑定属性, 宽)。</summary>
    public static DataGrid Table(IReadOnlyList<(string Header, string Path, double Width)> cols, bool multi = true)
    {
        var dg = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false, CanUserReorderColumns = false, CanUserResizeColumns = true,
            SelectionMode = multi ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
            FontSize = 12.5, Margin = new Thickness(4),
        };
        foreach (var (h, p, w) in cols)
            dg.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Avalonia.Data.Binding(p), Width = new DataGridLength(w) });
        return dg;
    }

    public static Task Info(Window owner, string title, string text) => CoalMsgBox.ShowAsync(owner, title, text);
    public static Task<bool> Confirm(Window owner, string title, string text, string ok = "确定", string cancel = "取消") => CoalMsgBox.ConfirmAsync(owner, title, text, ok, cancel);

    public static void Place(Window w, double width, double height = double.NaN)
    {
        w.Width = width;
        if (double.IsNaN(height)) w.SizeToContent = SizeToContent.Height; else w.Height = height;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.ShowInTaskbar = false;
        WindowFit.ClampToScreen(w);
    }
}
