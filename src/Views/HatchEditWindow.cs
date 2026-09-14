using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「编辑填充」对话框（AutoCAD HATCHEDIT 的图案填充编辑）：
/// 类型和图案(图案 / 颜色) + 角度和比例(角度 / 比例或间距 / 十字交叉) + 右侧按这块填充**自己的边界**实时预览。
/// 确定只把值交回调用方(<see cref="PatternName"/> 等)，由主窗写进选中的填充并重算 —— 窗体本身不碰场景。
/// 原版这一项是「待引擎实现 HATCHEDIT」的空壳（MainWindow.ContextMenu.cs OnHatchEditClick），此窗为用户 2026-09-14 要求补的界面；
/// 双击填充与「填充 ▾ → 编辑填充」都到这里。
/// </summary>
public sealed class HatchEditWindow : Window
{
    private readonly HatchEntity _sample;      // 预览用的那块(多选时取第一块), 只读
    private readonly HatchEntity _work;        // 预览工作副本: 参数改在它身上算图案线
    private readonly ComboBox _patternBox = new() { MinWidth = 200, MinHeight = 24 };
    private readonly Controls.EntityColorPicker _colorPick = new() { MinWidth = 160 };
    private readonly TextBox _angleBox = new() { MinWidth = 120 };
    private readonly TextBox _scaleBox = new() { MinWidth = 120, Watermark = "自动" };
    private readonly TextBlock _scaleLabel = new() { Text = "比例", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _crossBox = new() { Content = "十字交叉（再加一组正交线, 仅用户定义图案）" };
    private readonly Canvas _preview = new() { Width = 320, Height = 240, ClipToBounds = true };
    private readonly TextBlock _info = new() { FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
    private readonly TextBlock _err = new() { Foreground = Brushes.Firebrick, FontSize = 11, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly (float r, float g, float b) _layerRgb;
    private (float r, float g, float b)? _rgb;   // null = 随层
    private bool _suppress;

    private const int PreviewMaxSegments = 6000;   // 预览只画前这么多段(图案密时够看形状, 不拖慢输入)

    /// <summary>确定后的结果。</summary>
    public string PatternName { get; private set; }
    public double Scale { get; private set; }
    public double Angle { get; private set; }
    public bool Cross { get; private set; }
    /// <summary>null = 随层(调用方按各自图层色写)。</summary>
    public (float r, float g, float b)? Rgb => _rgb;
    /// <summary>按了「确定」且表单合法(非模态打开时调用方据此决定要不要写回)。</summary>
    public bool Accepted { get; private set; }

    /// <param name="sample">要编辑的填充(多选时第一块, 预览按它的边界画)。</param>
    /// <param name="count">本次一并改写的填充数(标题/提示用)。</param>
    /// <param name="layerRgb">sample 所在图层的颜色(取色器「随层」显示成它)。</param>
    public HatchEditWindow(HatchEntity sample, int count, (float r, float g, float b) layerRgb)
    {
        _sample = sample;
        _layerRgb = layerRgb;
        _work = sample.Clone();
        PatternName = sample.PatternName; Scale = sample.Scale; Angle = sample.Angle; Cross = sample.Cross;
        // 实体只存具体 RGB, 与图层同色即当作「随层」显示(改层色时跟着走)
        _rgb = Near(sample.Cr, layerRgb.r) && Near(sample.Cg, layerRgb.g) && Near(sample.Cb, layerRgb.b) ? null : (sample.Cr, sample.Cg, sample.Cb);

        Title = count > 1 ? $"编辑填充（已选 {count} 个）" : "编辑填充";
        Width = 700; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Classes.Add("geodb");

        // ── 左：表单 ──
        var form = new StackPanel { Spacing = 6, Width = 300 };
        form.Children.Add(Group("类型和图案", first: true));
        _patternBox.ItemsSource = HatchPatternLibrary.All.Select(p => p.Display).ToList();
        _patternBox.SelectedIndex = Math.Max(0, Array.FindIndex(HatchPatternLibrary.All, p => p.Name == sample.PatternName));
        ToolTip.SetTip(_patternBox, "SOLID = 实心；用户定义 = 按下面的角度/间距画平行剖面线；其余为命名图案(比例 = 缩放倍数)");
        form.Children.Add(Row("图案", _patternBox));
        _colorPick.SetValue(_rgb, layerRgb);
        ToolTip.SetTip(_colorPick, "随层 / ACI 索引色板 / 更多颜色（自定义 RGB）");
        form.Children.Add(Row("颜色", _colorPick));

        form.Children.Add(Group("角度和比例"));
        _angleBox.Text = sample.Angle.ToString("0.###", CultureInfo.InvariantCulture);
        ToolTip.SetTip(_angleBox, "图案整体旋转角(度)，叠加在图案自带角度上；用户定义时就是剖面线角度");
        form.Children.Add(Row("角度", _angleBox));
        _scaleBox.Text = sample.Scale > 1e-12 ? sample.Scale.ToString("0.###", CultureInfo.InvariantCulture) : "";
        ToolTip.SetTip(_scaleBox, "命名图案: 缩放倍数(越大越疏)；用户定义: 剖面线间距(世界单位)。空 = 自动按边界大小取(约 24 条线)");
        form.Children.Add(Row(_scaleLabel, _scaleBox));
        _crossBox.IsChecked = sample.Cross;
        form.Children.Add(_crossBox);
        form.Children.Add(_err);
        if (count > 1)
            form.Children.Add(new TextBlock
            {
                Text = $"已选 {count} 个填充：预览按第一个的边界画，确定后 {count} 个一并改成这些参数。",
                FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            });

        // ── 右：预览 ──
        var previewPanel = new StackPanel { Spacing = 6 };
        previewPanel.Children.Add(new TextBlock { Text = "预览（按这块填充自己的边界）", FontWeight = FontWeight.Bold });
        previewPanel.Children.Add(new Border
        {
            Child = _preview, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x23, 0x38)),
        });
        previewPanel.Children.Add(_info);

        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(16, 12, 16, 4) };
        body.Children.Add(form); body.Children.Add(previewPanel);

        // ── 页脚（Dock 到底, 内容包 ScrollViewer: 缩放≠1 时不把「确定」顶出屏）──
        var reset = new Button { Content = "恢复原值", MinWidth = 88 };
        reset.Click += (_, _) => ResetToSample();
        var ok = new Button { Content = "确定", MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("primary");
        var cancel = new Button { Content = "取消", MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close(false);
        var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        DockPanel.SetDock(reset, Avalonia.Controls.Dock.Left);
        footer.Children.Add(reset); footer.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        WindowFit.ClampToScreen(this);

        // 任一项一改就重画预览
        _patternBox.SelectionChanged += (_, _) => OnFormChanged();
        _angleBox.TextChanged += (_, _) => OnFormChanged();
        _scaleBox.TextChanged += (_, _) => OnFormChanged();
        _crossBox.IsCheckedChanged += (_, _) => OnFormChanged();
        _colorPick.ColorCommitted += (_, rgb) => { _rgb = rgb; Redraw(); };
        Opened += (_, _) => { SyncPatternKind(); Redraw(); };
    }

    private void Accept()
    {
        if (!ReadForm(out _)) return;
        Accepted = true;
        Close(true);
    }

    /// <summary>自检：直设表单各项("-" = 不改)，可选按确定 —— 走的就是用户改完点确定那条路。</summary>
    public void SelftestSet(string pattern, string angle, string scale, bool? cross, bool accept)
    {
        if (pattern != "-" && HatchPatternLibrary.ByName(pattern) is { } p)
            _patternBox.SelectedIndex = Math.Max(0, Array.IndexOf(HatchPatternLibrary.All, p));
        if (angle != "-") _angleBox.Text = angle;
        if (scale != "-") _scaleBox.Text = scale == "自动" ? "" : scale;
        if (cross is { } c) _crossBox.IsChecked = c;
        if (accept) Accept();
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-3f;

    private static TextBlock Group(string text, bool first = false)
        => new() { Text = text, FontWeight = FontWeight.Bold, Margin = new Thickness(0, first ? 0 : 8, 0, 2) };

    private static Grid Row(string label, Control ctl) => Row(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, ctl);

    private static Grid Row(TextBlock label, Control ctl)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*") };
        Grid.SetColumn(label, 0); Grid.SetColumn(ctl, 1);
        row.Children.Add(label); row.Children.Add(ctl);
        return row;
    }

    private HatchPatternLibrary.Pattern CurrentPattern()
    {
        int i = _patternBox.SelectedIndex;
        return i >= 0 && i < HatchPatternLibrary.All.Length ? HatchPatternLibrary.All[i] : HatchPatternLibrary.ByName(_sample.PatternName) ?? HatchPatternLibrary.All[0];
    }

    /// <summary>图案种类变了: 比例栏改叫「间距」(用户定义) / 十字交叉只对用户定义可勾 / 实心图案角度比例都无意义。</summary>
    private void SyncPatternKind()
    {
        var pat = CurrentPattern();
        _scaleLabel.Text = pat.IsUserDefined ? "间距" : "比例";
        _crossBox.IsEnabled = pat.IsUserDefined;
        _angleBox.IsEnabled = !pat.IsSolid;
        _scaleBox.IsEnabled = !pat.IsSolid;
    }

    private void OnFormChanged()
    {
        if (_suppress) return;
        SyncPatternKind();
        if (ReadForm(out _)) Redraw();
    }

    private void ResetToSample()
    {
        _suppress = true;
        try
        {
            _patternBox.SelectedIndex = Math.Max(0, Array.FindIndex(HatchPatternLibrary.All, p => p.Name == _sample.PatternName));
            _angleBox.Text = _sample.Angle.ToString("0.###", CultureInfo.InvariantCulture);
            _scaleBox.Text = _sample.Scale > 1e-12 ? _sample.Scale.ToString("0.###", CultureInfo.InvariantCulture) : "";
            _crossBox.IsChecked = _sample.Cross;
            _rgb = Near(_sample.Cr, _layerRgb.r) && Near(_sample.Cg, _layerRgb.g) && Near(_sample.Cb, _layerRgb.b) ? null : (_sample.Cr, _sample.Cg, _sample.Cb);
            _colorPick.SetValue(_rgb, _layerRgb);
        }
        finally { _suppress = false; }
        SyncPatternKind();
        if (ReadForm(out _)) Redraw();
    }

    /// <summary>表单 → 结果属性; 非法值报错(留在错误栏)并返回 false。</summary>
    private bool ReadForm(out string error)
    {
        error = "";
        var pat = CurrentPattern();
        var inv = CultureInfo.InvariantCulture;
        string at = (_angleBox.Text ?? "").Trim();
        if (at.Length > 0 && !double.TryParse(at, NumberStyles.Float, inv, out _)) error = "角度须是数字（度）";
        double angle = at.Length > 0 && double.TryParse(at, NumberStyles.Float, inv, out double a) ? a : 0;
        string st = (_scaleBox.Text ?? "").Trim();
        double scale = 0;
        if (st.Length > 0)
        {
            if (!double.TryParse(st, NumberStyles.Float, inv, out scale) || scale <= 0)
                error = error.Length > 0 ? error : (pat.IsUserDefined ? "间距须是大于 0 的数（世界单位），空 = 自动" : "比例须是大于 0 的数，空 = 自动");
        }
        _err.Text = error; _err.IsVisible = error.Length > 0;
        if (error.Length > 0) return false;
        PatternName = pat.Name; Angle = angle; Scale = scale; Cross = _crossBox.IsChecked == true && pat.IsUserDefined;
        return true;
    }

    /// <summary>把当前参数套在样本边界上, 画到预览画布(等比缩放、Y 向上)。</summary>
    private void Redraw()
    {
        _preview.Children.Clear();
        var bnd = _sample.Boundary;
        if (bnd.Count < 3) { _info.Text = "边界不足 3 点，无法预览"; return; }
        double minX = bnd.Min(p => p.x), maxX = bnd.Max(p => p.x), minY = bnd.Min(p => p.y), maxY = bnd.Max(p => p.y);
        double W = _preview.Width, H = _preview.Height, m = 12;
        double span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-9);
        double k = Math.Min((W - 2 * m) / span, (H - 2 * m) / span);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        Point Map(double x, double y) => new(W / 2 + (x - cx) * k, H / 2 - (y - cy) * k);

        var rgb = _rgb ?? _layerRgb;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb.r * 255), (byte)(rgb.g * 255), (byte)(rgb.b * 255)));
        var pat = CurrentPattern();

        _work.PatternName = PatternName; _work.Scale = Scale; _work.Angle = Angle; _work.Cross = Cross;
        _work.Invalidate();

        var outline = new Polygon
        {
            Points = new Points(bnd.Select(p => Map(p.x, p.y))),
            Stroke = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)), StrokeThickness = 1,
            Fill = pat.IsSolid ? brush : null,
        };
        _preview.Children.Add(outline);

        if (pat.IsSolid) { _info.Text = $"{pat.Display} · 边界 {bnd.Count} 点 · 实心铺面"; return; }

        var lines = _work.Lines();
        int n = Math.Min(lines.Count, PreviewMaxSegments);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < n; i++)
            {
                var l = lines[i];
                g.BeginFigure(Map(l.x1, l.y1), false);
                g.LineTo(Map(l.x2, l.y2));
                g.EndFigure(false);
            }
        }
        _preview.Children.Add(new Path { Data = geo, Stroke = brush, StrokeThickness = 1 });
        string cap = lines.Count > n ? $"（预览只画前 {n} 段）" : "";
        string capped = lines.Count >= HatchPatternLibrary.MaxSegments ? " · 已达线段上限, 请把比例调大" : "";
        _info.Text = $"{_work.Describe()} · 边界 {bnd.Count} 点 · {lines.Count} 段{cap}{capped}";
    }
}
