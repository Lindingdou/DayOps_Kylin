using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 设备效能预测 — What-if 提升路径模拟器(忠实原 EquipmentForecastWindow):
/// 净提升 = (故障杠杆 + 出动杠杆 + 装载杠杆 + 运距杠杆×0.6) × 协同衰减 0.85(模型见 EfficiencyWhatIf)。
/// 基线 = 最近 12 月产能均值 + 最新 KPI; 标杆 = 同型号 2023 年 P90/均值。
/// </summary>
public partial class EquipmentForecastWindow : Window
{
    private readonly GeoDbContext _ctx;
    private List<EqCapacityRow> _capacityHistory = new();
    private List<EqKpiRow> _kpiAll = new();
    private EqForecastBaseline? _base;
    private bool _ready;

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentForecastWindow() { _ctx = null!; InitializeComponent(); }

    public EquipmentForecastWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        LoadData();
        _ready = true;
        if (equipmentList.ItemCount > 0) equipmentList.SelectedIndex = 0;
    }

    private void LoadData()
    {
        _capacityHistory = EqLoadCapacity(_ctx.Conn);
        _kpiAll = EqLoadKpi(_ctx.Conn);
        var summaries = EqEquipmentSummaries(_capacityHistory);
        equipmentList.ItemsSource = summaries;
        statusText.Text = $"加载 {summaries.Count} 台设备,共 {_capacityHistory.Count} 条产能记录";
    }

    private void OnEquipmentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (equipmentList.SelectedItem is not EqEquipmentSummary sel) return;
        _base = EqForecastBaselineOf(_capacityHistory, _kpiAll, sel);
        baselineOutput.Text = $"{_base.OutputWan:F1} 万 m³";
        baselineAvail.Text = $"{_base.Availability * 100:F1}%";
        baselineRun.Text = $"{_base.RunRate * 100:F1}%";
        baselineFault.Text = $"{_base.FaultHours:F0} h";
        Recalculate();
    }

    private void OnLeverChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        lever1Value.Text = $"{lever1Slider.Value:F0}";
        lever2Value.Text = $"{lever2Slider.Value:F0}";
        lever3Value.Text = $"{lever3Slider.Value:F0}";
        lever4Value.Text = $"{lever4Slider.Value:F0}";
        Recalculate();
    }

    private void OnPresetAggressive(object? sender, RoutedEventArgs e) { lever1Slider.Value = 40; lever2Slider.Value = 15; lever3Slider.Value = 10; lever4Slider.Value = 15; }
    private void OnPresetConservative(object? sender, RoutedEventArgs e) { lever1Slider.Value = 15; lever2Slider.Value = 5; lever3Slider.Value = 3; lever4Slider.Value = 5; }
    private void OnReset(object? sender, RoutedEventArgs e) { lever1Slider.Value = 0; lever2Slider.Value = 0; lever3Slider.Value = 0; lever4Slider.Value = 0; }

    // ─── 核心:重算所有视图 ───
    private void Recalculate()
    {
        if (!_ready || _base == null || _base.OutputWan <= 0) return;
        double l1 = lever1Slider.Value / 100.0, l2 = lever2Slider.Value / 100.0, l3 = lever3Slider.Value / 100.0, l4 = lever4Slider.Value / 100.0;
        double faultShare = _base.PlanHours > 0 ? _base.FaultHours / _base.PlanHours : 0.1;
        var r = EfficiencyWhatIf.Simulate(_base.OutputWan, faultShare, l1, l2, l3, l4);

        resultBaseline.Text = $"{_base.OutputWan:F1}";
        resultSimulated.Text = $"{r.Simulated:F1}";
        resultGain.Text = $"+{r.Delta:F1} 万 m³";
        resultGainPct.Text = $"+{r.ActualGain * 100:F1}%";
        var (bench, hint) = EqPeerBenchmark(_capacityHistory, _base.Model, r.Simulated);
        resultBenchmark.Text = bench; resultBenchmarkHint.Text = hint;

        RenderStackChart(r.C1, r.C2, r.C3, r.C4);
        RenderContributionChart(r.C1, r.C2, r.C3, r.C4);
        conclusionText.Text = EqForecastConclusion(_base.OutputWan, r.C1, r.C2, r.C3, r.C4, lever1Slider.Value, lever2Slider.Value, lever3Slider.Value, lever4Slider.Value, r.ActualGain, r.Delta, r.Simulated);
        conclusionText.Foreground = conclusionText.Text.StartsWith("尚未") ? Brushes.Gray : new SolidColorBrush(Color.Parse("#333333"));
    }

    /// <summary>6 个台阶:基线 / +① / +①② / +①②③ / +①②③④ / 综合(协同衰减)。</summary>
    private void RenderStackChart(double c1, double c2, double c3, double c4)
    {
        double b = _base!.OutputWan;
        var values = new[] { b, b * (1 + c1), b * (1 + c1 + c2), b * (1 + c1 + c2 + c3), b * (1 + c1 + c2 + c3 + c4), b * (1 + (c1 + c2 + c3 + c4) * EfficiencyWhatIf.SynergyDecay) };
        var fills = new[] { "#9E9E9E", "#FFB74D", "#64B5F6", "#81C784", "#F06292", "#388E3C" };
        stackChart.Categories = new() { "基线", "+①故障", "+②出动", "+③装载", "+④运距", "综合(协同衰减)" };
        stackChart.Series = new List<ChartSeries>
        {
            new() { Name = "月产能", Kind = SeriesKind.Bar, Values = values.Select(v => (double?)v).ToList(), PointColors = fills.Select(f => (Color?)Color.Parse(f)).ToList(), ShowValueLabels = true, ValueFormat = "F1" },
        };
        stackChart.Refresh();
    }

    private void RenderContributionChart(double c1, double c2, double c3, double c4)
    {
        var items = new[] { ("① 故障降低", c1 * 100, "#FFB74D"), ("② 出动提升", c2 * 100, "#64B5F6"), ("③ 装载优化", c3 * 100, "#81C784"), ("④ 运距优化", c4 * 100, "#F06292") }
            .OrderByDescending(x => x.Item2).ToArray();
        contributionChart.Categories = items.Select(x => x.Item1).ToList();
        contributionChart.Series = new List<ChartSeries>
        {
            new() { Name = "贡献", Kind = SeriesKind.Bar, Values = items.Select(x => (double?)x.Item2).ToList(), PointColors = items.Select(x => (Color?)Color.Parse(x.Item3)).ToList(), ShowValueLabels = true, ValueFormat = "+0.0'%'" },
        };
        contributionChart.Refresh();
    }
}
