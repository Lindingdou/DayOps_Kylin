using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Controls.Charts;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 煤质空间分布窗(忠实原 CoalQualitySpatialWindow): 化验区内做插值显示。
/// 实测点=Point3D 按指标着色; 插值场=Box3D 体素半透明; 化验区边界=Line3D 外接矩形(原 AABB); 底图网格=Scene3DView.ShowGrid。
/// 插值 OK/IDW/NN/MA 走 GeoDbViews.CoalInterpolate(OK 核复用 OrdinaryKriging)。
/// </summary>
public partial class CoalQualitySpatialWindow : Window
{
    private readonly GeoDbContext _ctx;
    private double _originX, _originY;
    private List<CoalSampleRow> _allPoints = new();
    private readonly Dictionary<string, List<CoalGradeRuleFull>> _rules = new();

    // 连续着色 / 置信度：缓存上次插值场，切换着色模式即时重绘（无需重算）
    private List<CoalVoxel>? _lastField;
    private List<OrdinaryKriging.ControlPoint>? _lastControl;
    private double _lastResolution = 100;
    private string _lastIndicator = "ad_raw";
    private double _curMin, _curMax;
    private double _fieldVarMax;
    private bool _loaded;

    /// <summary>仅供 XAML 设计器/编译器使用。</summary>
    public CoalQualitySpatialWindow() { _ctx = null!; InitializeComponent(); }

    public CoalQualitySpatialWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        InitSelectors();
        Opened += (_, _) => { if (!_loaded) { _loaded = true; LoadInitial(); } };
    }

    private void InitSelectors()
    {
        seamSelect.Items.Clear();
        seamSelect.Items.Add("全部");
        foreach (var s in CoalSeamDefs(_ctx.Conn)) seamSelect.Items.Add(s.Code);
        seamSelect.SelectedIndex = 0;

        methodSelect.Items.Clear();
        foreach (var m in CoalInterpMethods) methodSelect.Items.Add(m);
        methodSelect.SelectedIndex = 0;

        foreach (var t in new[] { "ash", "sulfur", "qnet" }) _rules[t] = CoalGradeRules(_ctx.Conn, t);
    }

    private void LoadInitial()
    {
        try
        {
            _allPoints = CoalLoadSamples(_ctx.Conn).Where(p => p.ZSample.HasValue).ToList();
            if (_allPoints.Count == 0) { statusText.Text = "数据库无 3D 化验数据"; return; }
            _originX = _allPoints.Average(p => p.X);
            _originY = _allPoints.Average(p => p.Y);
            UpdateCoverageInfo();
            RebuildScene();
            subtitleText.Text = $"  化验区 {ComputeArea():F2} km²  ·  控制点 {_allPoints.Count}";
        }
        catch (Exception ex) { _ = CoalMsgBox.ShowAsync(this, "错误", $"加载失败：{ex.Message}"); }
    }

    private double ComputeArea()
    {
        if (_allPoints.Count < 2) return 0;
        return (_allPoints.Max(p => p.X) - _allPoints.Min(p => p.X)) * (_allPoints.Max(p => p.Y) - _allPoints.Min(p => p.Y)) / 1e6;
    }

    private List<CoalSampleRow> SelectedPoints()
    {
        var seamSel = seamSelect.SelectedItem?.ToString();
        return seamSel == "全部" || seamSel == null ? _allPoints : _allPoints.Where(p => p.SeamCode == seamSel).ToList();
    }

    private void UpdateCoverageInfo()
    {
        var cov = CoalCoverageOf(SelectedPoints());
        coverageInfo.Text = cov?.Text ?? "(该煤层无数据)";
    }

    // ─────────────────────────────────────────────────────────
    private void RebuildScene(bool zoom = true)
    {
        viewport3D.Points.Clear();
        viewport3D.Lines.Clear();
        viewport3D.Boxes.Clear();
        viewport3D.ShowGrid = showGrid.IsChecked == true;
        if (showPoints.IsChecked == true) DrawPoints();
        if (showCoverage.IsChecked == true) DrawCoverageBoundary();
        RenderField();
        viewport3D.Refresh();
        if (zoom) viewport3D.ZoomExtents();
    }

    private string Indicator() => (indicatorSelect.SelectedItem as ComboBoxItem)?.Tag as string ?? "ad_raw";
    private string ColorMode() => (colorModeSelect.SelectedItem as ComboBoxItem)?.Tag as string ?? "grade";

    private void DrawPoints()
    {
        var indicator = Indicator();
        var pts = SelectedPoints();
        var get = CoalGetter(indicator);
        ComputeRange(indicator, pts);
        foreach (var p in pts)
        {
            var v = get(p);
            if (v is null) continue;
            viewport3D.Points.Add(new Point3D
            {
                X = p.X - _originX, Y = p.Y - _originY, Z = p.ZSample ?? 0, Size = 7,
                Color = ColorForValue(indicator, v.Value),
                Tip = $"{p.HoleId} · {p.SeamCode}  {CoalIndicatorName(indicator)}={v.Value:F2}{CoalIndicatorUnit(indicator)}  Z={p.ZSample:F1}",
                Tag = p,
            });
        }
        BuildLegend(indicator);
    }

    /// <summary>化验区边界(忠实原: 简化用包围盒 AABB, 四条边)。</summary>
    private void DrawCoverageBoundary()
    {
        if (_allPoints.Count < 3) return;
        double minX = _allPoints.Min(p => p.X) - _originX, maxX = _allPoints.Max(p => p.X) - _originX;
        double minY = _allPoints.Min(p => p.Y) - _originY, maxY = _allPoints.Max(p => p.Y) - _originY;
        double z = _allPoints.Average(p => p.ZSample ?? 1200) - 30;
        var c = Color.FromRgb(0x66, 0x88, 0xAA);
        void L(double x0, double y0, double x1, double y1) => viewport3D.Lines.Add(new Line3D { X0 = x0, Y0 = y0, Z0 = z, X1 = x1, Y1 = y1, Z1 = z, Color = c, Thickness = 2 });
        L(minX, minY, maxX, minY); L(maxX, minY, maxX, maxY); L(maxX, maxY, minX, maxY); L(minX, maxY, minX, minY);
    }

    private Color ColorForValue(string indicator, double value)
    {
        if (ColorMode() == "grade")
        {
            var lvl = CoalFindLevel(_rules[CoalRuleForSpatial(indicator)], value);
            var (r, g, b) = CoalHexToRgb(lvl?.ColorHex, (0xA9, 0xA9, 0xA9));
            return Color.FromRgb(r, g, b);
        }
        double t = _curMax > _curMin ? (value - _curMin) / (_curMax - _curMin) : 0.5;
        var (cr, cg, cb) = CoalColormap(ColorMode(), t);
        return Color.FromRgb(cr, cg, cb);
    }

    private void BuildLegend(string indicator)
    {
        var items = new ObservableCollection<CoalLegendItem>();
        if (ColorMode() == "grade")
        {
            foreach (var r in _rules[CoalRuleForSpatial(indicator)])
            {
                var (cr, cg, cb) = CoalHexToRgb(r.ColorHex, (0xA9, 0xA9, 0xA9));
                items.Add(new CoalLegendItem
                {
                    Label = $"{r.LevelName} [{(r.ValueMin?.ToString() ?? "−∞")} ~ {(r.ValueMax?.ToString() ?? "+∞")}]",
                    ColorBrush = new SolidColorBrush(Color.FromRgb(cr, cg, cb)),
                });
            }
        }
        else
        {
            const int n = 6;   // 连续色带按 6 档采样 + 标数值
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)(n - 1);
                double val = _curMin + t * (_curMax - _curMin);
                var (cr, cg, cb) = CoalColormap(ColorMode(), t);
                items.Add(new CoalLegendItem { Label = val.ToString("F1"), ColorBrush = new SolidColorBrush(Color.FromRgb(cr, cg, cb)) });
            }
        }
        legendItems.ItemsSource = items;
    }

    private void ComputeRange(string indicator, IEnumerable<CoalSampleRow> pts)
    {
        var get = CoalGetter(indicator);
        var vals = pts.Select(p => get(p)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (vals.Count > 0) { _curMin = vals.Min(); _curMax = vals.Max(); }
        else { _curMin = 0; _curMax = 1; }
    }

    /// <summary>用缓存的插值场重绘(着色模式/置信度切换即时生效, 不重算)。</summary>
    private void RenderField()
    {
        if (_lastField is null || showField.IsChecked != true) return;
        bool conf = showConfidence.IsChecked == true;
        double res = _lastResolution;
        foreach (var v in _lastField)
        {
            var color = ColorForValue(_lastIndicator, v.Value);
            byte alpha = 0x80;
            if (conf)
            {
                double nearest = 0;
                if (_lastControl is { Count: > 0 })
                {
                    double best = double.MaxValue;
                    foreach (var c in _lastControl) { double dx = c.X - v.X, dy = c.Y - v.Y, d2 = dx * dx + dy * dy; if (d2 < best) best = d2; }
                    nearest = Math.Sqrt(best);
                }
                double keep = CoalConfidenceKeep(v.Variance, _fieldVarMax, nearest, res);
                alpha = (byte)(0x28 + 0xB4 * keep);
            }
            viewport3D.Boxes.Add(new Box3D
            {
                X = v.X, Y = v.Y, Z0 = v.Z - res * 0.2, Z1 = v.Z + res * 0.2, W = res * 0.9, D = res * 0.9,
                Color = color, Opacity = alpha / 255.0,
                Tip = $"{CoalIndicatorName(_lastIndicator)} ≈ {v.Value:F2}" + (v.Variance.HasValue ? $"  σ²={v.Variance:F2}" : ""),
            });
        }
    }

    private void RefreshRender()
    {
        if (!_loaded) return;
        RebuildScene(zoom: false);
    }

    private void OnColorModeChanged(object? sender, SelectionChangedEventArgs e) => RefreshRender();
    private void OnRenderToggle(object? sender, RoutedEventArgs e) => RefreshRender();

    // ─────────────────────────────────────────────────────────
    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            statusText.Text = "运行中...";
            var indicator = Indicator();
            var method = methodSelect.SelectedItem?.ToString() ?? "IDW";
            if (!double.TryParse(resolutionBox.Text, out var resolution) || resolution < 10) resolution = 100;
            var get = CoalGetter(indicator);
            var control = SelectedPoints()
                .Where(p => get(p).HasValue && p.ZSample.HasValue)
                .Select(p => new OrdinaryKriging.ControlPoint(p.X - _originX, p.Y - _originY, p.ZSample!.Value, get(p)!.Value))
                .ToList();
            if (control.Count < 3) { await CoalMsgBox.ShowAsync(this, "提示", "控制点不足 3 个，无法插值。"); statusText.Text = "就绪"; return; }

            double pp = double.TryParse(paramP.Text, out var p1) ? p1 : 2;
            int kk = double.TryParse(paramK.Text, out var k1) ? Math.Max(1, (int)k1) : 5;
            var result = CoalInterpolate(control, method, resolution, pp, kk);

            _lastField = result;
            _fieldVarMax = result.Where(f => f.Variance.HasValue).Select(f => f.Variance!.Value).DefaultIfEmpty(0).Max();
            _lastControl = control;
            _lastResolution = resolution;
            _lastIndicator = indicator;
            _curMin = control.Min(c => c.V);
            _curMax = control.Max(c => c.V);
            spatialConclusion.Text = CoalSpatialConclusion(control, indicator);

            RebuildScene();
            statusText.Text = $"插值完成 — {method} / 控制点 {control.Count} / 体素 {result.Count}";
            _ctx.Status($"煤质空间分布：{method} 插值完成，体素 {result.Count}");
        }
        catch (Exception ex)
        {
            statusText.Text = "运行失败";
            await CoalMsgBox.ShowAsync(this, "错误", $"运行失败：{ex.Message}");
        }
    }

    private void OnRefreshPointsClick(object? sender, RoutedEventArgs e)
    {
        UpdateCoverageInfo();
        RebuildScene();
        statusText.Text = "点云已刷新";
    }

    private void OnZoomExtentsClick(object? sender, RoutedEventArgs e) => viewport3D.ZoomExtents();
}

/// <summary>图例条目(色块 + 文本)。</summary>
public sealed class CoalLegendItem
{
    public string Label { get; set; } = "";
    public IBrush ColorBrush { get; set; } = Brushes.Gray;
}
