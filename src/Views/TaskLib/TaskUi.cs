using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using PitMine3D.Kylin.Views.GeoDb;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 「日常生产组织」各窗共用的控件工厂 —— 对应原 TaskLib 各 XAML 里反复出现的那几块：
/// 深色渐变抬头带（标题 + 一句副题）、工具条 Border、表格、底部汇总带、消息框。配色走主题资源。
/// </summary>
internal static class TaskUi
{
    public static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    public static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
    public static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    public static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

    public static void Theme(StyledElement c, AvaloniaProperty prop, string key) => RoadUi.Theme(c, prop, key);

    /// <summary>正文字色（主题资源 Theme.Text.Body 的即时值；给单元格模板里"条件不成立时回到正常色"用）。</summary>
    public static IBrush BodyBrush
        => Application.Current is { } app && app.TryGetResource("Theme.Text.Body", app.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Black;

    /// <summary>原各窗顶部那条深蓝灰渐变抬头带：大标题 + 灰白副题。</summary>
    public static Border Header(string title, string subtitle)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = subtitle, Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xDE, 0xEA)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Padding = new Thickness(16, 10),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0x47, 0x55, 0x69), 0), new GradientStop(Color.FromRgb(0x33, 0x41, 0x55), 1) },
            },
            Child = sp,
        };
    }

    /// <summary>工具条 / 底栏那种 Surface 底 + 单边框线的条。</summary>
    public static Border Bar(Control content, bool top, double padX = 14, double padY = 7)
    {
        var b = new Border { Padding = new Thickness(padX, padY), BorderThickness = top ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0), Child = content };
        Theme(b, Border.BackgroundProperty, "Theme.Surface.Background");
        Theme(b, Border.BorderBrushProperty, "Theme.Surface.Border");
        return b;
    }

    public static TextBlock Text(string text, double fontSize = 13, bool wrap = true, bool bold = false) => RoadUi.Text(text, fontSize, wrap, bold);
    public static TextBlock Hint(string text, double fontSize = 11.5) => RoadUi.Hint(text, fontSize);
    public static TextBlock Lbl(string text) => RoadUi.Lbl(text);
    public static Button Btn(string text, Action onClick, double minWidth = 84, bool bold = false, bool primary = false) => RoadUi.Btn(text, onClick, minWidth, bold, primary);
    public static Button Small(string text, Action onClick) => RoadUi.Small(text, onClick);
    public static TextBox Box(string text, double width, double height = 26) => RoadUi.Box(text, width, height);
    public static ComboBox Combo(IEnumerable<string> items, double width = 220) => RoadUi.Combo(items, width);
    public static StackPanel Row(params Control[] children) => RoadUi.Row(children);

    public static CheckBox Check(string text, bool isChecked, Action onToggle, string? tip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (tip != null) ToolTip.SetTip(c, tip);
        c.IsCheckedChanged += (_, _) => onToggle();
        return c;
    }

    /// <summary>表格（列自己加）。原 WPF 表格默认横滚、表头可见、单选。</summary>
    public static DataGrid Grid(bool readOnly = true, bool single = true, int frozen = 0)
    {
        var dg = new DataGrid
        {
            IsReadOnly = readOnly, AutoGenerateColumns = false, CanUserReorderColumns = false, CanUserResizeColumns = true,
            SelectionMode = single ? DataGridSelectionMode.Single : DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
            FontSize = 12.5, Margin = new Thickness(10, 10, 10, 4), FrozenColumnCount = frozen,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        // Fluent 的表头自带 12px 左内边距 + 14px 字：原 WPF 表里 46~64 DIP 的窄列（工序/荐车/配车/台阶）装不下两个汉字。
        // 表头内边距收到 4、字号 12，列宽才能照原值抄。正文单元格同理收字号（Fluent 正文默认 14，见 datagrid-star-column-fit 记录）。
        // Fluent 表头模板给排序箭头预留 DataGridSortIconMinWidth=32 DIP —— 48 宽的列减掉它和 12 的内边距只剩 4 px 给字。
        dg.Resources["DataGridSortIconMinWidth"] = 6.0;
        var hs = new Style(x => x.OfType<DataGrid>().Descendant().OfType<DataGridColumnHeader>());
        hs.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(4, 0)));
        hs.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 12.0));
        dg.Styles.Add(hs);
        var cs = new Style(x => x.OfType<DataGrid>().Descendant().OfType<DataGridCell>());
        cs.Setters.Add(new Setter(DataGridCell.FontSizeProperty, 12.5));
        dg.Styles.Add(cs);
        return dg;
    }

    /// <summary>表头（带提示）。DataGridTextColumn 的 Header 是 object，放 TextBlock 才能挂 ToolTip。</summary>
    public static TextBlock Head(string text, string? tip = null)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };
        if (tip != null) ToolTip.SetTip(t, tip);
        return t;
    }

    /// <summary>只读文本列。</summary>
    public static DataGridTextColumn TextCol(string header, string path, double width, string? tip = null, bool readOnly = true)
        => new() { Header = Head(header, tip), Binding = new Binding(path), Width = new DataGridLength(width), IsReadOnly = readOnly };

    /// <summary>
    /// 带条件观感的文本列：显示态 TextBlock 的前景/斜体/粗体绑到行上的属性（如运力不足标红、未录坐标琥珀斜体）；
    /// 编辑态是普通 TextBox 双向绑 <paramref name="path"/>（LostFocus 提交）。
    /// </summary>
    public static DataGridTemplateColumn StyledCol<TRow>(string header, string path, double width,
        string? tipPath = null, string? brushPath = null, string? italicPath = null, string? boldPath = null,
        string? headerTip = null, bool editable = true, bool rightAlign = false) where TRow : class
    {
        var col = new DataGridTemplateColumn { Header = Head(header, headerTip), Width = new DataGridLength(width), IsReadOnly = !editable };
        col.CellTemplate = new FuncDataTemplate<TRow>((_, _) =>
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };   // 不设 CharacterEllipsis：Avalonia 按首次布局宽度算省略号（见 datagrid-star-column-fit 记录）
            if (rightAlign) tb.TextAlignment = TextAlignment.Right;
            tb.Bind(TextBlock.TextProperty, new Binding(path));
            if (brushPath != null) tb.Bind(TextBlock.ForegroundProperty, new Binding(brushPath));
            else Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Body");
            if (italicPath != null) tb.Bind(TextBlock.FontStyleProperty, new Binding(italicPath));
            if (boldPath != null) tb.Bind(TextBlock.FontWeightProperty, new Binding(boldPath));
            if (tipPath != null) tb.Bind(ToolTip.TipProperty, new Binding(tipPath));
            return tb;
        });
        if (editable)
        {
            col.CellEditingTemplate = new FuncDataTemplate<TRow>((_, _) =>
            {
                var tx = new TextBox { VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(6, 2) };
                if (rightAlign) tx.TextAlignment = TextAlignment.Right;
                tx.Bind(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                return tx;
            });
        }
        return col;
    }

    public static Task Info(Window owner, string title, string text) => CoalMsgBox.ShowAsync(owner, title, text);
    public static Task<bool> Confirm(Window owner, string title, string text, string ok = "确定", string cancel = "取消") => CoalMsgBox.ConfirmAsync(owner, title, text, ok, cancel);

    public static void Place(Window w, double width, double height)
    {
        w.Width = width; w.Height = height;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowFit.ClampToScreen(w);
    }
}
