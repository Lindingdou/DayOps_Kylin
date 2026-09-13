using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「标注样式」设置面板（AutoCAD DIMSTYLE 的文档级变量, 对应 <see cref="DimStyle"/>）：
/// 文字(字高/小数位/文字偏移) + 直线和箭头(箭头长宽/端刻度/界线偏移/界线延伸)，右侧样例标注随输入实时重画。
/// 确定写回传入的样式对象(影响之后新建的标注; 已有标注不动, 逐条覆盖走特性面板)。
/// 原版的 DimensionStyleWindow 是逐条标注的特性编辑器(已落到特性面板, §三二七)，这扇是样式级的, 用户 2026-09-13 要求补。
/// </summary>
public sealed class DimStyleWindow : Window
{
    private readonly DimStyle _target;
    private readonly DimStyle _work;
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly Canvas _preview = new() { Width = 380, Height = 150, Background = new SolidColorBrush(Color.FromRgb(0x18, 0x23, 0x38)) };
    private readonly TextBlock _err = new() { Foreground = Brushes.Firebrick, FontSize = 11, IsVisible = false };

    private static readonly (string key, string label, string hint, string group)[] Fields =
    {
        ("TextHeight", "文字高度", "0 = 自动（随视图比例, DIMTXT）; 其它为固定世界单位", "文字"),
        ("DecimalPlaces", "小数位数", "距离/半径/角度数字的小数位 0~8（DIMDEC）", "文字"),
        ("TextOffsetRatio", "文字偏移", "文字离尺寸线的距离 = 字高 × 此值（DIMGAP）", "文字"),
        ("ArrowRatio", "箭头长度", "箭头长 = 字高 × 此值（DIMASZ）", "直线和箭头"),
        ("ArrowWidthRatio", "箭头半宽", "箭头半宽 = 字高 × 此值", "直线和箭头"),
        ("TickRatio", "端刻度长", "两点标注的端刻度半长 = 字高 × 此值", "直线和箭头"),
        ("ExtLineOffsetRatio", "界线偏移", "测点到尺寸界线起点的间隙 = 字高 × 此值（DIMEXO）", "直线和箭头"),
        ("ExtLineExtensionRatio", "界线延伸", "尺寸界线越过尺寸线的长度 = 字高 × 此值（DIMEXE）", "直线和箭头"),
    };

    public DimStyleWindow(DimStyle target)
    {
        _target = target;
        _work = DimStyleStore.Clone(target);
        Title = "标注样式";
        Width = 720; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Classes.Add("geodb");

        var form = new StackPanel { Spacing = 6, Width = 280 };
        string? lastGroup = null;
        foreach (var f in Fields)
        {
            if (f.group != lastGroup)
            {
                form.Children.Add(new TextBlock { Text = f.group, FontWeight = FontWeight.Bold, Margin = new Thickness(0, lastGroup == null ? 0 : 8, 0, 2) });
                lastGroup = f.group;
            }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("88,*") };
            var lbl = new TextBlock { Text = f.label, VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { Text = Get(f.key), Tag = f.key };
            ToolTip.SetTip(box, f.hint);
            box.TextChanged += (_, _) => { if (Apply(box)) Redraw(); };
            Grid.SetColumn(lbl, 0); Grid.SetColumn(box, 1);
            row.Children.Add(lbl); row.Children.Add(box);
            form.Children.Add(row);
            _boxes[f.key] = box;
        }
        form.Children.Add(_err);

        var previewPanel = new StackPanel { Spacing = 6 };
        previewPanel.Children.Add(new TextBlock { Text = "预览（样例：对齐标注 100 单位）", FontWeight = FontWeight.Bold });
        previewPanel.Children.Add(new Border { Child = _preview, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, CornerRadius = new CornerRadius(3) });
        previewPanel.Children.Add(new TextBlock
        {
            Text = "比值项都以字高为基准；文字高度为 0 时按当前视图比例取字高, 缩放后新建的标注仍是同样大小。\n改动只影响之后新建的标注, 已有标注逐条修改请用特性面板。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, MaxWidth = 380,
        });

        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(16, 12, 16, 4) };
        body.Children.Add(form); body.Children.Add(previewPanel);

        var reset = new Button { Content = "恢复默认", MinWidth = 88 };
        reset.Click += (_, _) => { DimStyleStore.CopyTo(new DimStyle(), _work); foreach (var f in Fields) _boxes[f.key].Text = Get(f.key); Redraw(); };
        var ok = new Button { Content = "确定", MinWidth = 80, IsDefault = true };
        ok.Classes.Add("primary");
        var cancel = new Button { Content = "取消", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => { if (!ApplyAll()) return; DimStyleStore.CopyTo(_work, _target); Close(true); };
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
        Opened += (_, _) => Redraw();
    }

    private string Get(string key) => key switch
    {
        "TextHeight" => _work.TextHeight.ToString("0.###", CultureInfo.InvariantCulture),
        "DecimalPlaces" => _work.DecimalPlaces.ToString(CultureInfo.InvariantCulture),
        "TextOffsetRatio" => _work.TextOffsetRatio.ToString("0.###", CultureInfo.InvariantCulture),
        "ArrowRatio" => _work.ArrowRatio.ToString("0.###", CultureInfo.InvariantCulture),
        "ArrowWidthRatio" => _work.ArrowWidthRatio.ToString("0.###", CultureInfo.InvariantCulture),
        "TickRatio" => _work.TickRatio.ToString("0.###", CultureInfo.InvariantCulture),
        "ExtLineOffsetRatio" => _work.ExtLineOffsetRatio.ToString("0.###", CultureInfo.InvariantCulture),
        "ExtLineExtensionRatio" => _work.ExtLineExtensionRatio.ToString("0.###", CultureInfo.InvariantCulture),
        _ => "",
    };

    /// <summary>一格 → 工作样式；非法值报错不写(保留上次合法值)。</summary>
    private bool Apply(TextBox box)
    {
        string key = (string)box.Tag!;
        string? err = DimStyleStore.TrySet(_work, key, box.Text ?? "");
        _err.Text = err ?? ""; _err.IsVisible = err != null;
        return err == null;
    }

    private bool ApplyAll()
    {
        foreach (var b in _boxes.Values) if (!Apply(b)) { b.Focus(); return false; }
        return true;
    }

    /// <summary>样例：对齐标注 100 单位, 按工作样式画在预览画布上(像素直接当世界单位, 字高 0 时取 14px)。</summary>
    private void Redraw()
    {
        _preview.Children.Clear();
        double H = _work.TextHeight > 0 ? Math.Clamp(_work.TextHeight, 4, 40) : 14;
        var col = new SolidColorBrush(Color.FromRgb(0xF2, 0xD9, 0x4D));
        double W = _preview.Width, Hc = _preview.Height;
        double x1 = W * 0.18, x2 = W * 0.82, yPt = Hc * 0.80, yDim = Hc * 0.40;   // 测点在下, 尺寸线在上
        double gap = H * _work.ExtLineOffsetRatio, ext = H * _work.ExtLineExtensionRatio;
        double ah = H * _work.ArrowRatio, aw = H * _work.ArrowWidthRatio, tk = H * _work.TickRatio;
        void L(double ax, double ay, double bx, double by, double thick = 1.2)
            => _preview.Children.Add(new Line { StartPoint = new Point(ax, ay), EndPoint = new Point(bx, by), Stroke = col, StrokeThickness = thick });
        // 测点(小十字)
        foreach (double x in new[] { x1, x2 }) { L(x - 3, yPt, x + 3, yPt, 1); L(x, yPt - 3, x, yPt + 3, 1); }
        // 尺寸界线: 测点 - 间隙 → 尺寸线 + 超出
        L(x1, yPt - gap, x1, yDim - ext); L(x2, yPt - gap, x2, yDim - ext);
        // 尺寸线 + 两端箭头(指向外) + 端刻度
        L(x1, yDim, x2, yDim);
        L(x1, yDim, x1 + ah, yDim + aw); L(x1, yDim, x1 + ah, yDim - aw);
        L(x2, yDim, x2 - ah, yDim + aw); L(x2, yDim, x2 - ah, yDim - aw);
        if (tk > 0) { L(x1, yDim - tk, x1, yDim + tk, 1); L(x2, yDim - tk, x2, yDim + tk, 1); }
        // 数字: 100 按小数位格式化, 偏到尺寸线上方
        string s = 100.0.ToString(_work.NumberFormat, CultureInfo.InvariantCulture);
        var tb = new TextBlock { Text = s, Foreground = col, FontSize = H, FontFamily = new FontFamily("Consolas, Microsoft YaHei, sans-serif") };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, (x1 + x2) / 2 - tb.DesiredSize.Width / 2);
        Canvas.SetTop(tb, yDim - H * _work.TextOffsetRatio - tb.DesiredSize.Height);
        _preview.Children.Add(tb);
    }
}

/// <summary>标注样式的读写/校验/持久化（用户数据目录 dimstyle.json, 与 crash.log 同目录）。纯逻辑, 可单测。</summary>
public static class DimStyleStore
{
    public static DimStyle Clone(DimStyle s) { var d = new DimStyle(); CopyTo(s, d); return d; }

    public static void CopyTo(DimStyle src, DimStyle dst)
    {
        dst.TextHeight = src.TextHeight; dst.DecimalPlaces = src.DecimalPlaces;
        dst.TickRatio = src.TickRatio; dst.ArrowRatio = src.ArrowRatio; dst.ArrowWidthRatio = src.ArrowWidthRatio;
        dst.TextOffsetRatio = src.TextOffsetRatio; dst.ExtLineOffsetRatio = src.ExtLineOffsetRatio; dst.ExtLineExtensionRatio = src.ExtLineExtensionRatio;
    }

    /// <summary>按键名写一项；返回 null = 成功，否则为给用户看的错误说明。</summary>
    public static string? TrySet(DimStyle s, string key, string text)
    {
        text = text.Trim();
        if (key == "DecimalPlaces")
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > 8) return "小数位数须为 0~8 的整数";
            s.DecimalPlaces = n; return null;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) return $"「{Label(key)}」须为数值";
        switch (key)
        {
            case "TextHeight": if (v < 0 || v > 1e6) return "文字高度须 ≥ 0（0 = 自动）"; s.TextHeight = v; return null;
            case "TextOffsetRatio": if (v < 0 || v > 20) return "文字偏移须在 0~20 之间"; s.TextOffsetRatio = v; return null;
            case "ArrowRatio": if (v < 0 || v > 20) return "箭头长度须在 0~20 之间"; s.ArrowRatio = v; return null;
            case "ArrowWidthRatio": if (v < 0 || v > 20) return "箭头半宽须在 0~20 之间"; s.ArrowWidthRatio = v; return null;
            case "TickRatio": if (v < 0 || v > 20) return "端刻度长须在 0~20 之间"; s.TickRatio = v; return null;
            case "ExtLineOffsetRatio": if (v < 0 || v > 20) return "界线偏移须在 0~20 之间"; s.ExtLineOffsetRatio = v; return null;
            case "ExtLineExtensionRatio": if (v < 0 || v > 20) return "界线延伸须在 0~20 之间"; s.ExtLineExtensionRatio = v; return null;
            default: return $"未知项 {key}";
        }
    }

    public static string Label(string key) => key switch
    {
        "TextHeight" => "文字高度", "DecimalPlaces" => "小数位数", "TextOffsetRatio" => "文字偏移",
        "ArrowRatio" => "箭头长度", "ArrowWidthRatio" => "箭头半宽", "TickRatio" => "端刻度长",
        "ExtLineOffsetRatio" => "界线偏移", "ExtLineExtensionRatio" => "界线延伸", _ => key,
    };

    public static string Describe(DimStyle s)
        => $"文字高 {(s.TextHeight > 0 ? s.TextHeight.ToString("0.##", CultureInfo.InvariantCulture) : "自动")} · 小数位 {s.DecimalPlaces} · 箭头 {s.ArrowRatio:0.##}×{s.ArrowWidthRatio:0.##} · 界线偏移/延伸 {s.ExtLineOffsetRatio:0.##}/{s.ExtLineExtensionRatio:0.##}";

    /// <summary>持久化文件(与 crash.log 同目录; PITMINE_DATA_DIR 生效时跟着走)。</summary>
    public static string FilePath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(PitMine3D.Kylin.CrashLog.Path) ?? ".", "dimstyle.json");

    private static readonly JsonSerializerOptions JsonOpt = new() { IncludeFields = true, WriteIndented = true };

    public static string ToJson(DimStyle s) => JsonSerializer.Serialize(Clone(s), JsonOpt);

    /// <summary>解析失败/缺项时缺的项按默认；返回 null = 整份都不是合法 JSON。</summary>
    public static DimStyle? FromJson(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<DimStyle>(json, JsonOpt);
            if (s == null) return null;
            // 越界值(手改文件)按默认兜住
            var d = new DimStyle();
            if (s.TextHeight < 0 || !double.IsFinite(s.TextHeight)) s.TextHeight = d.TextHeight;
            if (s.DecimalPlaces < 0 || s.DecimalPlaces > 8) s.DecimalPlaces = d.DecimalPlaces;
            foreach (var k in new[] { "TextOffsetRatio", "ArrowRatio", "ArrowWidthRatio", "TickRatio", "ExtLineOffsetRatio", "ExtLineExtensionRatio" })
            {
                double v = GetRatio(s, k);
                if (v < 0 || v > 20 || !double.IsFinite(v)) TrySet(s, k, GetRatio(d, k).ToString(CultureInfo.InvariantCulture));
            }
            return s;
        }
        catch { return null; }
    }

    private static double GetRatio(DimStyle s, string key) => key switch
    {
        "TextOffsetRatio" => s.TextOffsetRatio, "ArrowRatio" => s.ArrowRatio, "ArrowWidthRatio" => s.ArrowWidthRatio,
        "TickRatio" => s.TickRatio, "ExtLineOffsetRatio" => s.ExtLineOffsetRatio, _ => s.ExtLineExtensionRatio,
    };

    public static void Save(DimStyle s)
    {
        try { File.WriteAllText(FilePath, ToJson(s)); }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("标注样式", $"保存失败: {ex.Message}"); }
    }

    /// <summary>启动时读上次保存的样式；没有/坏了就默认。</summary>
    public static DimStyle LoadOrDefault()
    {
        try
        {
            if (File.Exists(FilePath) && FromJson(File.ReadAllText(FilePath)) is { } s) return s;
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("标注样式", $"读取失败: {ex.Message}"); }
        return new DimStyle();
    }
}
