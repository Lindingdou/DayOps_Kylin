using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views.GeoDb;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 克里金估值（选点插值）——忠实原 Estimation/Dialogs/KrigingDialog + KrigingViewModel：
/// 以视口选择集中的对象为样本（点 / 多段线顶点，被插值量 = Z 高程），OK / SK / UK 水平插值到固定间距格网
/// → 在每个结点真实插入十字点（高程 = 插值结果）。变异函数经 <see cref="VariogramEditor"/> 编辑/自动拟合。非模态单例。
/// </summary>
public partial class KrigingWindow : Window
{
    private readonly ModelingContext _ctx;
    private readonly EstimationEngine _engine = new();
    private CancellationTokenSource _cts = new();
    private List<SampleRecord> _pickedSamples = new();
    private (double minX, double minY, double maxX, double maxY)? _range;
    private bool _isRunning;

    /// <summary>仅供 XAML 设计器/编译器使用。</summary>
    public KrigingWindow() { _ctx = null!; InitializeComponent(); }

    public KrigingWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        Variogram.SampleProvider = () => _pickedSamples;
        Variogram.Notify = (t, m) => CoalMsgBox.ShowAsync(this, t, m);
        RangeSummaryText.Text = PickedPointEstimation.RangeSummary(null);
        OnAlgorithmChanged(null, null);
        ReadSelection();
        Closed += (_, _) => { if (_isRunning) _cts.Cancel(); };
    }

    private string SelectedAlgorithm => (AlgorithmCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "OK";
    private int TrendOrder => TrendCombo.SelectedIndex == 1 ? 2 : 1;

    private void OnAlgorithmChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (TrendGrid == null) return;
        TrendGrid.IsVisible = SelectedAlgorithm == "UK";
        AlgorithmDescText.Text = SelectedAlgorithm switch
        {
            "OK" => "普通克里金：均值未知但恒定，Σλ=1 无偏约束；通用稳健，默认首选。",
            "SK" => "简单克里金：已知全局均值（取样点均值），平稳场下方差更小。",
            "UK" => "泛克里金：显式拟合坐标趋势（漂移），适合有系统性倾斜/走向的高程面。",
            _ => ""
        };
    }

    private void OnOctantChanged(object? sender, RoutedEventArgs e) => OctantGrid.IsEnabled = OctantBox.IsChecked == true;

    private void OnPickClick(object? sender, RoutedEventArgs e) { if (!_isRunning) ReadSelection(); }

    /// <summary>读取视口当前选择集里的点/多段线顶点为样本（被插值量 = Z），并按 Z 建议变异函数参数。</summary>
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
            StatusText.Text = "选择集为空：请在视口选中点 / 多段线后再点「读取选择集」或「运行」";
            return;
        }
        var radius = PickedPointEstimation.SuggestSearchRadius(_pickedSamples);
        if (radius.HasValue) RadiusBox.Text = radius.Value.ToString("F1", CultureInfo.InvariantCulture);
        // 变异函数按 Z 的样本统计给建议值（基台≈方差，块金≈15%，变程≈平均点距×4）
        var vg = PickedPointEstimation.SuggestVariogram(_pickedSamples);
        if (vg is { } v) Variogram.Set(v.nugget, v.sill, v.range);
        StatusText.Text = $"{PickedSummaryText.Text}，建议间距 {D(SpacingBox, 150):F1}m / 搜索半径 {D(RadiusBox, 200):F1}m";
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
        // 水平插值：样本测距 Z 拍平为常量（真实高程仅作被插值量）。
        var flat = PickedPointEstimation.Flatten(_pickedSamples);
        var progress = new Progress<EstimationProgress>(p => { Progress.Value = p.Percent; StatusText.Text = p.Status; });
        try
        {
            var result = await _engine.RunAsync(config, progress, _cts.Token, flat);
            if (!result.Success) { StatusText.Text = $"失败: {result.Message}"; return; }
            int inserted = InsertGridPoints(result);
            StatusText.Text = inserted > 0
                ? $"{SelectedAlgorithm} 插值完成：{result.BlocksEstimated} 结点，已插入 {inserted} 个网格点，高程范围 [{result.MinEstimate:F2}, {result.MaxEstimate:F2}]"
                : "插值完成但无有效结点可插入（邻域为空？可增大搜索半径）";
            _ctx.Status($"克里金估值：{StatusText.Text}");
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
            TrendOrder = TrendOrder,
            Variogram = Variogram.ToConfig(),
            SearchRadius = D(RadiusBox, 200), MaxSamples = I(MaxSamplesBox, 16), MinSamples = I(MinSamplesBox, 1),
            DefaultValue = D(DefaultValueBox, -999), UseOctant = OctantBox.IsChecked == true, MaxPerOctant = I(MaxPerOctantBox, 2),
        };
        double spacing = PickedPointEstimation.ApplyGrid(cfg, _pickedSamples, D(SpacingBox, 150), _range);
        SpacingBox.Text = spacing.ToString("F1", CultureInfo.InvariantCulture);
        return cfg;
    }

    /// <summary>每个有效格网结点作为一个真实十字点插入：XY = 结点中心，Z = 插值高程。</summary>
    private int InsertGridPoints(EstimationResult r)
    {
        var nodes = PickedPointEstimation.GridNodes(r);
        if (nodes.Count == 0) return 0;
        double size = PickedPointEstimation.MarkerSize(r);
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
