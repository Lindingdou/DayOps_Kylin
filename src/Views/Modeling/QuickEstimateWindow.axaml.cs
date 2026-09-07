using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 快速估值（选点插值）——忠实原 Estimation/Dialogs/QuickEstimateDialog + QuickEstimateViewModel：
/// 以视口选择集中的对象为样本（点 / 多段线顶点，被插值量 = Z 高程），NN / MA / IDW 水平插值到固定间距格网
/// → 在每个格网结点真实插入十字点（高程 = 插值结果）。未选中对象时插值不可用。非模态单例。
/// </summary>
public partial class QuickEstimateWindow : Window
{
    private readonly ModelingContext _ctx;
    private readonly EstimationEngine _engine = new();
    private CancellationTokenSource _cts = new();
    private List<SampleRecord> _pickedSamples = new();
    private (double minX, double minY, double maxX, double maxY)? _range;
    private bool _isRunning;

    private static readonly (string code, string desc)[] Algorithms =
    {
        ("NN", "取最近样点高程直接赋值"),
        ("MA", "邻域内样点高程等权平均"),
        ("IDW", "按水平距离幂次反比加权插值高程"),
    };

    /// <summary>仅供 XAML 设计器/编译器使用。</summary>
    public QuickEstimateWindow() { _ctx = null!; InitializeComponent(); }

    public QuickEstimateWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        RangeSummaryText.Text = PickedPointEstimation.RangeSummary(null);
        OnAlgorithmChanged(null, null);
        // 打开即读取当前选择集（"选中对象 → 点插值功能"）。
        ReadSelection();
        Closed += (_, _) => { if (_isRunning) _cts.Cancel(); };
    }

    private string SelectedAlgorithm => (AlgorithmCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "IDW";

    private void OnAlgorithmChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (IdwPanel == null) return;
        bool idw = SelectedAlgorithm == "IDW";
        IdwPanel.IsVisible = idw;
        AlgorithmDescText.IsVisible = !idw;
        AlgorithmDescText.Text = Algorithms.FirstOrDefault(a => a.code == SelectedAlgorithm).desc ?? "";
    }

    private void OnPowerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && PowerText != null) PowerText.Text = $"当前幂次: {PowerSlider.Value:F1}";
    }

    private void OnOctantChanged(object? sender, RoutedEventArgs e) => OctantGrid.IsEnabled = OctantBox.IsChecked == true;

    private void OnPickClick(object? sender, RoutedEventArgs e) { if (!_isRunning) ReadSelection(); }

    /// <summary>读取视口当前选择集里的点/多段线顶点为样本（被插值量 = Z）。</summary>
    private void ReadSelection()
    {
        var pts = _ctx.SelectedPoints().Select(p => (p.X, p.Y, p.Elevation));
        var polys = _ctx.SelectedPolylines().Select(pl =>
            (IReadOnlyList<(double, double, double)>)Enumerable.Range(0, pl.Points.Count).Select(i => (pl.Points[i].x, pl.Points[i].y, pl.ZAt(i))).ToList());
        _pickedSamples = PickedPointEstimation.ReadSelection(pts, polys);
        Analysis.Samples = _pickedSamples.Select(s => s.Z).ToArray();
        PickedSummaryText.Text = PickedPointEstimation.PickedSummary(_pickedSamples);
        UpdateButtons();

        if (_pickedSamples.Count == 0)
        {
            StatusText.Text = "选择集为空：请在视口选中点 / 多段线后再点「读取选择集」或「插值」";
            return;
        }
        // 格网间距默认 150m（不随样点自动改）；仅自动建议搜索半径，保证范围内格网填满
        var radius = PickedPointEstimation.SuggestSearchRadius(_pickedSamples);
        if (radius.HasValue) RadiusBox.Text = radius.Value.ToString("F1", CultureInfo.InvariantCulture);
        StatusText.Text = $"{PickedSummaryText.Text}，建议间距 {D(SpacingBox, 150):F1}m / 搜索半径 {D(RadiusBox, 150):F1}m";
    }

    private async void OnPickRangeClick(object? sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        StatusText.Text = "框选范围：在视口点两个对角点框定矩形范围（Esc 取消）";
        var a = await _ctx.PickPointAsync("框选范围：点第一个角点（Esc 取消）");
        if (a == null) { StatusText.Text = "已取消框选范围"; return; }
        var b = await _ctx.PickPointAsync($"框选范围：点对角点（第一角点 {a.Value.x:F1}, {a.Value.y:F1}；Esc 取消）");
        if (b == null) { StatusText.Text = "已取消框选范围"; return; }
        _range = (Math.Min(a.Value.x, b.Value.x), Math.Min(a.Value.y, b.Value.y), Math.Max(a.Value.x, b.Value.x), Math.Max(a.Value.y, b.Value.y));
        RangeSummaryText.Text = PickedPointEstimation.RangeSummary(_range);
        StatusText.Text = $"范围已框选：{RangeSummaryText.Text}";
        UpdateButtons();
        Activate();
    }

    private void OnClearRangeClick(object? sender, RoutedEventArgs e)
    {
        _range = null;
        RangeSummaryText.Text = PickedPointEstimation.RangeSummary(null);
        StatusText.Text = "已清除框选范围，回退样本包围盒";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        RunButton.IsEnabled = !_isRunning && _pickedSamples.Count > 0;
        CancelButton.IsEnabled = _isRunning;
        PickButton.IsEnabled = !_isRunning;
        PickRangeButton.IsEnabled = !_isRunning;
        ClearRangeButton.IsEnabled = _range != null;
        Progress.IsVisible = _isRunning;
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (_pickedSamples.Count == 0) { StatusText.Text = "选择集为空，无法插值"; return; }
        if (_isRunning) return;

        _cts = new CancellationTokenSource();
        _isRunning = true; Progress.Value = 0; UpdateButtons();
        var config = BuildConfig();
        var flat = PickedPointEstimation.Flatten(_pickedSamples);
        var progress = new Progress<EstimationProgress>(p => { Progress.Value = p.Percent; StatusText.Text = p.Status; });
        try
        {
            var result = await _engine.RunAsync(config, progress, _cts.Token, flat);
            if (!result.Success) { StatusText.Text = $"失败: {result.Message}"; return; }
            int inserted = InsertGridPoints(result);
            StatusText.Text = inserted > 0
                ? $"插值完成：{result.BlocksEstimated} 结点，已插入 {inserted} 个网格点，高程范围 [{result.MinEstimate:F2}, {result.MaxEstimate:F2}]"
                : "插值完成但无有效结点可插入（邻域为空？可增大搜索半径）";
            _ctx.Status($"快速估值({SelectedAlgorithm})：{StatusText.Text}");
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消"; }
        catch (Exception ex) { StatusText.Text = $"失败: {ex.Message}"; }
        finally { _isRunning = false; Progress.Value = 0; UpdateButtons(); }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) { if (_isRunning) { _cts.Cancel(); StatusText.Text = "正在取消..."; } }

    private EstimationTaskConfig BuildConfig()
    {
        var cfg = new EstimationTaskConfig
        {
            TargetProperty = PickedPointEstimation.ElevationKey,
            OutputProperty = "高程估值",
            AlgorithmCode = SelectedAlgorithm,
            IdwPower = PowerSlider.Value, IdwSmoothing = D(SmoothingBox, 0),
            SearchRadius = D(RadiusBox, 150), MaxSamples = I(MaxSamplesBox, 12), MinSamples = I(MinSamplesBox, 1),
            DefaultValue = D(DefaultValueBox, -999), UseOctant = OctantBox.IsChecked == true, MaxPerOctant = I(MaxPerOctantBox, 2),
        };
        // 插值范围：优先用框选矩形，否则回退样本点包围盒。范围内按固定间距均匀布网（正方形）。
        double spacing = PickedPointEstimation.ApplyGrid(cfg, _pickedSamples, D(SpacingBox, 150), _range);
        SpacingBox.Text = spacing.ToString("F1", CultureInfo.InvariantCulture);   // 点数上限保护后回填给 UI
        return cfg;
    }

    /// <summary>把估值格网的每个有效结点作为一个真实十字点插入场景：XY = 结点中心，Z = 插值高程。</summary>
    private int InsertGridPoints(EstimationResult r)
    {
        var nodes = PickedPointEstimation.GridNodes(r);
        if (nodes.Count == 0) return 0;
        double size = PickedPointEstimation.MarkerSize(r);   // 十字；大小取网格步长的一小份，保证可见又不糊成一片
        var ents = new List<SceneEntity>(nodes.Count);
        foreach (var (x, y, z) in nodes) ents.Add(new PointEntity { X = x, Y = y, Elevation = z, Style = 2, Size = size });
        _ctx.AddEntities(ents, null, null);
        return ents.Count;
    }

    private static double D(TextBox box, double fallback)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static int I(TextBox box, int fallback)
        => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
