using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Controls.Charts;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 变异函数编辑器（忠实原 Estimation/Controls/VariogramEditor + VariogramViewModel）：模型类型(球状/指数/高斯) +
/// 理论曲线预览(ChartView; 自动拟合后叠加实验变异函数散点) + 块金/基台/变程 + 校验提示 + 重置 + 从样品自动拟合。
/// 样本由宿主窗口经 <see cref="SampleProvider"/> 提供（原从视口选择集读）。
/// </summary>
public partial class VariogramEditor : UserControl
{
    private double _nugget = 0.0, _sill = 1.0, _range = 100.0;
    private bool _syncing;
    private List<EstimationAlgorithms.ExperimentalLag>? _lastExp;

    /// <summary>自动拟合用样本来源（值 = Z 高程）；未注入时提示先选点。</summary>
    public Func<List<SampleRecord>>? SampleProvider { get; set; }
    /// <summary>消息提示(标题, 内容)——替代原 MessageBox。</summary>
    public Func<string, string, System.Threading.Tasks.Task>? Notify { get; set; }
    /// <summary>参数变化通知。</summary>
    public event Action? Changed;

    public VariogramEditor()
    {
        InitializeComponent();
        VariogramChart.NumericX = true;
        VariogramChart.Legend = LegendPlacement.Hidden;
        VariogramChart.XTitle = "h";
        PushToBoxes();
        DrawCurve();
        UpdateValidation();
    }

    /// <summary>0=Spherical 1=Exponential 2=Gaussian。</summary>
    public int Type
    {
        get => Math.Max(0, TypeCombo.SelectedIndex);
        set { TypeCombo.SelectedIndex = Math.Clamp(value, 0, 2); }
    }
    public string TypeName => Type switch { 1 => "Exponential", 2 => "Gaussian", _ => "Spherical" };
    public double Nugget { get => _nugget; set { _nugget = value; PushToBoxes(); Refresh(); } }
    public double Sill { get => _sill; set { _sill = value; PushToBoxes(); Refresh(); } }
    public double Range { get => _range; set { _range = value; PushToBoxes(); Refresh(); } }
    public bool IsValid => _nugget <= _sill && _range > 0 && _sill > 0;

    /// <summary>成批设置参数（读选择集后的建议值），只刷新一次。</summary>
    public void Set(double? nugget, double? sill, double? range)
    {
        if (nugget.HasValue) _nugget = nugget.Value;
        if (sill.HasValue) _sill = sill.Value;
        if (range.HasValue) _range = range.Value;
        PushToBoxes(); Refresh();
    }

    public VariogramConfig ToConfig() => new() { Type = TypeName, Nugget = _nugget, Sill = _sill, Range = _range };

    private void PushToBoxes()
    {
        _syncing = true;
        NuggetBox.Text = _nugget.ToString("F2", CultureInfo.InvariantCulture);
        SillBox.Text = _sill.ToString("F2", CultureInfo.InvariantCulture);
        RangeBox.Text = _range.ToString("F1", CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void Refresh() { DrawCurve(); UpdateValidation(); Changed?.Invoke(); }

    private void OnTypeChanged(object? sender, SelectionChangedEventArgs e) { if (VariogramChart != null) Refresh(); }

    private void OnParamChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool ok = true;
        if (double.TryParse(NuggetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) _nugget = n; else ok = false;
        if (double.TryParse(SillBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) _sill = s; else ok = false;
        if (double.TryParse(RangeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) _range = r; else ok = false;
        if (!ok) PushToBoxes();
        Refresh();
    }

    private void DrawCurve()
    {
        VariogramChart.Series.Clear();
        var curve = new ChartSeries { Name = "γ(h)", Kind = SeriesKind.Line, Color = Color.Parse("#0D6EFD"), ShowMarkers = false, StrokeThickness = 2 };
        foreach (var (h, g) in PickedPointEstimation.VariogramCurve(Type, _nugget, _sill, _range)) curve.Points.Add((h, g));
        VariogramChart.Series.Add(curve);
        if (_lastExp != null)
        {
            var sc = new ChartSeries { Name = "实验变异函数", Kind = SeriesKind.Scatter, Color = Color.Parse("#DC3545"), MarkerSize = 4 };
            foreach (var lag in _lastExp) if (lag.Count > 0) sc.Points.Add((lag.H, lag.Gamma));
            if (sc.Points.Count > 0) VariogramChart.Series.Add(sc);
        }
        double maxH = _sill * 1.2; if (maxH <= 0) maxH = 1;
        VariogramChart.YMin = 0; VariogramChart.YMax = maxH;
        VariogramChart.XMin = 0;
        VariogramChart.Refresh();
    }

    private void UpdateValidation() => ValidationTip.Text = PickedPointEstimation.VariogramValidation(_nugget, _sill, _range);

    private async void OnAutoFitClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 选点插值模式：样本 = 视口当前选择集（点/多段线顶点），被拟合量 = Z 高程。
            var samples = SampleProvider?.Invoke() ?? new List<SampleRecord>();
            if (samples.Count < 3)
            {
                await Say("自动拟合", $"选择集样点不足（{samples.Count} 个），需至少 3 个；请先在视口选中点 / 多段线");
                return;
            }
            var (fit, exp, filledBins) = PickedPointEstimation.AutoFit(samples);
            if (fit == null)
            {
                await Say("自动拟合", $"有效 bin 数不足（{filledBins}/{exp.Count}），样本可能过于稀疏");
                return;
            }
            _lastExp = exp;
            Type = 0;
            _nugget = fit.Nugget; _sill = fit.Sill; _range = fit.Range;
            PushToBoxes(); Refresh();
            await Say("自动拟合完成",
                $"已拟合 Spherical 模型（基于 {samples.Count} 样本，{filledBins} 个有效 bin）：\n" +
                $"  Nugget = {fit.Nugget:0.###}\n" +
                $"  Sill   = {fit.Sill:0.###}\n" +
                $"  Range  = {fit.Range:0.###}");
        }
        catch (Exception ex)
        {
            await Say("自动拟合", $"拟合失败：{ex.Message}");
        }
    }

    private void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        _lastExp = null;
        Type = 0;
        _nugget = 0.0; _sill = 1.0; _range = 100.0;
        PushToBoxes(); Refresh();
    }

    private System.Threading.Tasks.Task Say(string title, string text)
        => Notify != null ? Notify(title, text) : System.Threading.Tasks.Task.CompletedTask;
}
