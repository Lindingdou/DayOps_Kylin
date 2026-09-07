using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 原始钻孔柱状图(2D 出图): 左选孔, 中 Canvas 画单孔逐分层地层柱(煤层段 + 段间围岩段, 填满全孔深), 右侧详情面板。
/// 每个岩段/煤层均可点击, 点击后在右侧显示该分层的具体层位信息(段顶/底深、标高、厚度、顶底板岩性、结构、级别、状态…)。
/// </summary>
public partial class OriginalBoreholeColumnWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly List<GeoDbViews.SeamDefRow> _defs;
    private List<GeoDbViews.BoreholeRow> _holes = new();

    // 布局参数
    private const double PxPerM = 3.0;      // 垂向比例 (像素/米)
    private const double TopMargin = 24, BarX = 92, BarW = 96, TickLen = 6;

    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x36));
    private static readonly IBrush SelectStroke = new SolidColorBrush(Color.FromRgb(0x00, 0xBF, 0xFE));   // 中煤蓝
    private Rectangle? _selectedRect;
    private IBrush? _selectedOrigStroke;
    private double _selectedOrigStrokeW;

    /// <summary>XAML 编译器/设计器用(运行时一律走带 ctx 的构造)。</summary>
    public OriginalBoreholeColumnWindow() { _ctx = null!; _defs = new List<GeoDbViews.SeamDefRow>(); InitializeComponent(); }

    public OriginalBoreholeColumnWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        try { _defs = GeoDbViews.LoadSeamDefs(ctx.Conn); } catch { _defs = new List<GeoDbViews.SeamDefRow>(); }
        filterBox.TextChanged += (_, _) => RefreshList();
        Opened += (_, _) => LoadHoles();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ─────────────────────────── 孔列表 ───────────────────────────
    private void LoadHoles()
    {
        try { _holes = GeoDbViews.LoadBoreholes(_ctx.Conn); }
        catch (Exception ex) { statusText.Text = $"加载失败：{ex.Message}"; return; }
        RefreshList();
    }

    private void RefreshList()
    {
        string f = filterBox.Text?.Trim() ?? "";
        var items = _holes.Where(h => f.Length == 0 || (h.HoleId?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        holeList.ItemsSource = items;
        statusText.Text = $"共 {_holes.Count} 孔" + (f.Length > 0 ? $"，筛选 {items.Count}" : "");
    }

    private void OnHoleSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (holeList.SelectedItem is GeoDbViews.BoreholeRow h) DrawColumn(h);
    }

    // ─────────────────────────── 绘制 ───────────────────────────
    private void DrawColumn(GeoDbViews.BoreholeRow h)
    {
        columnCanvas.Children.Clear();
        ClearSelection();
        ShowDetailHint();

        if (h.ZCollar is null || h.DepthTotal is null || h.DepthTotal.Value <= 0)
        {
            headerText.Text = $"{h.HoleId}  (缺孔口高程或孔深，无法绘制)";
            return;
        }
        double collar = h.ZCollar.Value, depth = h.DepthTotal.Value;
        headerText.Text = $"{h.HoleId}    孔口 {collar:F1}m    孔深 {depth:F1}m"
                        + (string.IsNullOrEmpty(h.TerminateHorizon) ? "" : $"    终孔 {h.TerminateHorizon}");

        double barTop = TopMargin, barBot = TopMargin + depth * PxPerM;
        columnCanvas.Width = BarX + BarW + 220;
        columnCanvas.Height = barBot + TopMargin;

        List<GeoDbViews.OriginalColumnSeg> segs;
        int skipped;
        try { segs = GeoDbViews.BuildOriginalSegments(collar, depth, GeoDbViews.LoadSeamResultsByBorehole(_ctx.Conn, h.Id), _defs, out skipped); }
        catch (Exception ex) { statusText.Text = $"读取煤层成果失败：{ex.Message}"; return; }

        // 深度刻度 (每 50m) + 标高
        for (int d = 0; d <= (int)depth; d += 50)
        {
            double y = barTop + d * PxPerM;
            AddLine(BarX - TickLen, y, BarX, y, Brushes.Gray);
            AddText(BarX - TickLen - 46, y - 8, $"{d}", 10, Brushes.Gray);
        }
        AddLine(BarX - TickLen, barBot, BarX, barBot, Brushes.Gray);
        AddText(BarX - TickLen - 46, barBot - 8, $"{depth:F0}", 10, Brushes.Gray);
        AddText(BarX - 74, barTop - 18, "深度(m)", 10, Brushes.Gray);

        // 逐段绘制 (可点击)
        int coalN = 0;
        foreach (var s in segs)
        {
            double yTop = barTop + s.DepthTop * PxPerM;
            double hPx = Math.Max((s.DepthBot - s.DepthTop) * PxPerM, 1.5);
            var rect = new Rectangle
            {
                Width = BarW, Height = hPx,
                Fill = new SolidColorBrush(Color.FromRgb(s.Fill.r, s.Fill.g, s.Fill.b)),
                Stroke = s.IsCoal ? Brushes.Black : Brushes.DimGray,
                StrokeThickness = s.IsCoal ? 0.8 : 0.5,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = s,
            };
            ToolTip.SetTip(rect, SegTooltip(s));
            rect.PointerPressed += OnSegmentClick;
            Canvas.SetLeft(rect, BarX); Canvas.SetTop(rect, yTop);
            columnCanvas.Children.Add(rect);

            if (s.IsCoal) { coalN++; AddSegLabel(s, yTop + hPx / 2); }
            else if (hPx >= 14) AddSegLabel(s, yTop + hPx / 2);
        }

        statusText.Text = $"{h.HoleId}：{segs.Count} 个分层（{coalN} 煤层）" + (skipped > 0 ? $"，{skipped} 层缺深度/厚度未展绘" : "");
    }

    private void AddSegLabel(GeoDbViews.OriginalColumnSeg s, double yMid)
    {
        AddLine(BarX + BarW, yMid, BarX + BarW + 12, yMid, Brushes.Gray);
        string lab = s.IsCoal ? $"{s.LayerName}  厚{s.Thick:F2}m" : $"{s.LayerName}  厚{s.Thick:F1}m";
        AddText(BarX + BarW + 14, yMid - 9, lab, 11, TextBrush);
    }

    private void OnSegmentClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Rectangle rect || rect.Tag is not GeoDbViews.OriginalColumnSeg s) return;
        if (!e.GetCurrentPoint(rect).Properties.IsLeftButtonPressed) return;
        SelectRect(rect);
        ShowDetail(s);
        e.Handled = true;
    }

    // ─────────────────────────── 选中高亮 ───────────────────────────
    private void SelectRect(Rectangle rect)
    {
        ClearSelection();
        _selectedRect = rect;
        _selectedOrigStroke = rect.Stroke;
        _selectedOrigStrokeW = rect.StrokeThickness;
        rect.Stroke = SelectStroke;
        rect.StrokeThickness = 2.4;
    }

    private void ClearSelection()
    {
        if (_selectedRect != null) { _selectedRect.Stroke = _selectedOrigStroke; _selectedRect.StrokeThickness = _selectedOrigStrokeW; }
        _selectedRect = null;
    }

    // ─────────────────────────── 详情面板 ───────────────────────────
    private void ShowDetailHint()
    {
        detailTitle.Text = "层位详情";
        detailHint.IsVisible = true;
        detailPanel.Children.Clear();
    }

    private void ShowDetail(GeoDbViews.OriginalColumnSeg s)
    {
        detailHint.IsVisible = false;
        detailPanel.Children.Clear();
        detailTitle.Text = s.IsCoal ? $"煤层 · {s.LayerName}" : $"岩层 · {s.LayerName}";

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(s.Fill.r, s.Fill.g, s.Fill.b)), Width = 26, Height = 14,
            CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(0.6), VerticalAlignment = VerticalAlignment.Center,
        };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        chipRow.Children.Add(chip);
        chipRow.Children.Add(new TextBlock { Text = s.IsCoal ? "煤层段" : "围岩段", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        detailPanel.Children.Add(chipRow);

        AddRow("段顶深度", $"{s.DepthTop:F2} m");
        AddRow("段底深度", $"{s.DepthBot:F2} m");
        AddRow("段厚", $"{s.Thick:F2} m");
        AddRow("段顶标高", $"{s.ZTop:F2} m");
        AddRow("段底标高", $"{s.ZBot:F2} m");

        if (s.IsCoal && s.Seam is { } r)
        {
            AddSep();
            AddRow("煤层编号", r.SeamCode);
            AddRow("采用厚度", Fmt(r.AdoptedThickness, "m"));
            AddRow("综合厚度", Fmt(r.OverallThickness, "m"));
            AddRow("夹石厚度", Fmt(r.PartingThickness, "m"));
            AddRow("风氧化煤厚", Fmt(r.WeatheredCoalThickness, "m"));
            AddRow("煤层结构", r.LogStructure ?? r.DrillStructure);
            AddRow("测井止煤深", Fmt(r.LogEndDepth, "m"));
            AddRow("底板标高", Fmt(r.FloorElevation, "m"));
            AddRow("采取率", Fmt(r.DrillRecoveryRate, "%"));
            AddSep();
            AddRow("顶板岩性", r.RoofLithology);
            AddRow("底板岩性", r.FloorLithology);
            AddRow("综合级别", r.OverallRating);
            AddRow("状态", r.Status);
            AddRow("备注", r.Remark);
        }
        else
        {
            AddSep();
            AddRow("分层类型", s.LayerName);
            AddRow("推断岩性", s.RockLith ?? "—（原始资料未分层，按相邻煤层顶底板推断）");
        }
    }

    private void AddRow(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lab = new TextBlock { Text = label, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
        var val = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(val, 1);
        g.Children.Add(lab); g.Children.Add(val);
        detailPanel.Children.Add(g);
    }

    private void AddSep()
        => detailPanel.Children.Add(new Border { Height = 1, Background = Brushes.Gray, Opacity = 0.5, Margin = new Thickness(0, 6, 0, 6) });

    private static string? Fmt(double? v, string unit) => v is { } d ? $"{d.ToString("0.##", CultureInfo.InvariantCulture)} {unit}" : null;

    private static string SegTooltip(GeoDbViews.OriginalColumnSeg s)
        => s.IsCoal
            ? $"{s.LayerName}\n{s.DepthTop:F2}~{s.DepthBot:F2} m  厚 {s.Thick:F2} m\n点击查看层位详情"
            : $"{s.LayerName}\n{s.DepthTop:F2}~{s.DepthBot:F2} m  厚 {s.Thick:F2} m\n点击查看详情";

    // ─────────────────────────── Canvas helpers ───────────────────────────
    private void AddLine(double x1, double y1, double x2, double y2, IBrush stroke)
        => columnCanvas.Children.Add(new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = stroke, StrokeThickness = 0.8 });

    private void AddText(double x, double y, string text, double size, IBrush brush)
    {
        var t = new TextBlock { Text = text, FontSize = size, Foreground = brush };
        Canvas.SetLeft(t, x); Canvas.SetTop(t, y); columnCanvas.Children.Add(t);
    }
}
