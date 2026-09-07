using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 设备数据分析(忠实原 EquipmentAnalysisWindow): OEE 三率卡片 + 班次效率时间轴 +
/// Tab(三率趋势·预测 / 故障 Pareto / 主控因素分析 三图 / 优化建议 / 可靠性 Weibull)。
/// </summary>
public partial class EquipmentAnalysisWindow : Window
{
    private readonly GeoDbContext _ctx;
    private List<EqKpiRow> _kpiAll = new();
    private List<EqFaultRow> _faultAll = new();
    private List<EqProductionRecord> _prodAll = new();
    private List<EqCapacityRow> _capAll = new();
    private List<EqProductionRecord> _currentShifts = new();
    private List<(string Factor, double[] Values)> _factorValues = new();
    private double[] _outputValues = Array.Empty<double>();
    private bool _ready;

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentAnalysisWindow() { _ctx = null!; InitializeComponent(); }

    public EquipmentAnalysisWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        LoadData();
    }

    private void LoadData()
    {
        _kpiAll = EqLoadKpi(_ctx.Conn);
        _faultAll = EqLoadFaults(_ctx.Conn);
        _prodAll = EqLoadProduction(_ctx.Conn);
        _capAll = EqLoadCapacity(_ctx.Conn);

        periodCombo.ItemsSource = EqPeriodOptions(_kpiAll);
        periodCombo.SelectedIndex = 1;   // 近 12 个月
        var devices = _kpiAll.Select(r => r.EquipmentId).Distinct().OrderBy(x => x).ToList();
        deviceCombo.ItemsSource = devices.Select(id => new ComboBoxItem { Content = $"{id} · {_kpiAll.First(r => r.EquipmentId == id).Model}", Tag = id }).ToList();
        _ready = true;
        if (devices.Count > 0) deviceCombo.SelectedIndex = 0;
        statusText.Text = $"加载 KPI {_kpiAll.Count} 条 / 故障记录 {_faultAll.Count} 条";
    }

    private string? SelectedDevice => (deviceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private void OnDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var deviceId = SelectedDevice;
        if (string.IsNullOrEmpty(deviceId)) return;
        var kpisDev = _kpiAll.Where(r => r.EquipmentId == deviceId).OrderBy(r => r.Year).ThenBy(r => r.Month).ToList();
        var kpis = EqApplyPeriod(kpisDev, periodCombo.SelectedItem as string ?? "近 12 个月");
        if (kpis.Count == 0) return;

        int loOrd = kpis.Min(k => EqOrd(k.Year, k.Month)), hiOrd = kpis.Max(k => EqOrd(k.Year, k.Month));
        bool InWindow(DateTime d) => EqOrd(d.Year, d.Month) >= loOrd && EqOrd(d.Year, d.Month) <= hiOrd;
        var faults = _faultAll.Where(r => r.EquipmentId == deviceId && InWindow(r.Date)).ToList();
        var model = kpis.First().Model;
        deviceMeta.Text = $"型号:{model}  ·  {kpis.First().Year}-{kpis.First().Month:D2} ~ {kpis.Last().Year}-{kpis.Last().Month:D2}  ·  {kpis.Count} 月 KPI / {faults.Count} 条故障";

        RenderKpiCards(kpis, faults);
        RenderKpiTrend(kpis, deviceId);
        RenderParetoChart(faults);
        RefreshShiftDateOptions(deviceId);
        var prod = _prodAll.Where(r => r.EquipmentId == deviceId && InWindow(r.Date)).ToList();
        RenderFactorAnalysis(kpis, faults, prod);
        RenderReliability(kpis, faults, model);
    }

    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    private void RenderKpiCards(List<EqKpiRow> kpis, List<EqFaultRow> faults)
    {
        var latest = kpis.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).First();
        kpiAvailability.Text = $"{latest.Availability * 100:F1}%";
        kpiAvailability.Foreground = latest.Availability >= 0.90 ? Brushes.SeaGreen : B("#D32F2F");
        kpiActualRun.Text = $"{latest.ActualRunRate * 100:F1}%";
        kpiActualRun.Foreground = latest.ActualRunRate >= 0.70 ? Brushes.SeaGreen : B("#D32F2F");
        kpiUtilization.Text = $"{latest.UtilizationRate * 100:F1}%";
        kpiOee.Text = $"{latest.Oee * 100:F1}%";
        kpiFaultHours.Text = $"{kpis.Sum(r => r.FaultHours):F0} h";
        kpiFaultCount.Text = $"{faults.Count} 次故障";
        var totalRun = kpis.Sum(r => r.WorkHours);
        kpiMtbf.Text = $"{(faults.Count > 0 ? totalRun / faults.Count : 0):F0}";
    }

    private void RenderKpiTrend(List<EqKpiRow> kpis, string deviceId)
    {
        var labels = kpis.Select(r => r.YearMonth).ToList();
        var capByMonth = _capAll.Where(r => r.EquipmentId == deviceId).GroupBy(r => (r.Year, r.Month)).ToDictionary(g => g.Key, g => g.Sum(x => x.OutputM3) / 1e4);
        var outWan = kpis.Select(k => (double?)(capByMonth.TryGetValue((k.Year, k.Month), out var v) ? Math.Round(v, 0) : 0)).ToList();
        // 可用率 趋势+EWMA 预测未来 3 月(虚线段)
        var availPct = kpis.Select(r => r.Availability * 100).ToList();
        var fr = ForecastModels.Forecast(availPct, 3);
        int n = availPct.Count;
        var fcast = new List<double?>(new double?[n + 3]);
        if (n > 0) fcast[n - 1] = Math.Round(availPct[n - 1], 1);
        for (int i = 0; i < 3 && i < fr.Path.Length; i++) fcast[n + i] = Math.Round(fr.Path[i], 1);
        for (int i = 0; i < 3; i++) labels.Add($"预测+{i + 1}");

        kpiTrendChart.Categories = labels;
        kpiTrendChart.Series = new List<ChartSeries>
        {
            new() { Name = "可用率(%)", Kind = SeriesKind.Line, Color = Color.Parse("#1976D2"), StrokeThickness = 3, Values = kpis.Select(r => (double?)(r.Availability * 100)).ToList() },
            new() { Name = "实动率(%)", Kind = SeriesKind.Line, Color = Color.Parse("#388E3C"), StrokeThickness = 3, Values = kpis.Select(r => (double?)(r.ActualRunRate * 100)).ToList() },
            new() { Name = "利用率(%)", Kind = SeriesKind.Line, Color = Color.Parse("#FF8F00"), StrokeThickness = 3, Values = kpis.Select(r => (double?)(r.UtilizationRate * 100)).ToList() },
            new() { Name = $"可用率·预测({fr.TrendLabel})", Kind = SeriesKind.Line, Color = Color.Parse("#E53935"), Dashed = true, Values = fcast, MarkerSize = 7 },
            new() { Name = "月度产量(万m³)", Kind = SeriesKind.Line, Color = Color.Parse("#8E24AA"), StrokeThickness = 3, Values = outWan, SecondaryAxis = true, MarkerSize = 8 },
        };
        kpiTrendChart.Refresh();
    }

    private void RenderParetoChart(List<EqFaultRow> faults)
    {
        if (faults.Count == 0) { paretoChart.Categories = new(); paretoChart.Series = new(); paretoChart.Refresh(); return; }
        var grouped = faults.GroupBy(f => f.FaultType).Select(g => new { Type = g.Key, Hours = g.Sum(f => f.DurationHours) }).OrderByDescending(x => x.Hours).ToList();
        double total = grouped.Sum(x => x.Hours), running = 0;
        var cum = new List<double?>();
        foreach (var g in grouped) { running += g.Hours; cum.Add(total > 0 ? running / total * 100 : 0); }
        paretoChart.Categories = grouped.Select(x => x.Type).ToList();
        paretoChart.Series = new List<ChartSeries>
        {
            new() { Name = "故障时长(h)", Kind = SeriesKind.Bar, Color = Color.Parse("#C62828"), Values = grouped.Select(x => (double?)x.Hours).ToList() },
            new() { Name = "累计 %", Kind = SeriesKind.Line, Color = Color.Parse("#FF8F00"), Values = cum, SecondaryAxis = true },
        };
        paretoChart.Refresh();
    }

    // ─── 班次效率时间轴 ───
    private void RefreshShiftDateOptions(string deviceId)
    {
        var dates = _prodAll.Where(r => r.EquipmentId == deviceId).Select(r => r.Date.Date).Distinct().OrderBy(d => d).ToList();
        shiftDateCombo.ItemsSource = dates.Select(d => new ComboBoxItem { Content = d.ToString("yyyy-MM-dd"), Tag = d }).ToList();
        if (dates.Count > 0) shiftDateCombo.SelectedIndex = 0;
        else { _currentShifts = new(); DrawStatusBlocks(); }
    }

    private void OnShiftDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || (shiftDateCombo.SelectedItem as ComboBoxItem)?.Tag is not DateTime date) return;
        var deviceId = SelectedDevice;
        if (string.IsNullOrEmpty(deviceId)) return;
        var shifts = _prodAll.Where(r => r.EquipmentId == deviceId && r.Date.Date == date).OrderBy(r => r.Shift).ToList();
        RenderShiftView(deviceId, date, shifts);
    }

    private void RenderShiftView(string deviceId, DateTime date, List<EqProductionRecord> shifts)
    {
        shiftDeviceLabel.Text = deviceId;
        var model = _kpiAll.FirstOrDefault(k => k.EquipmentId == deviceId)?.Model ?? "";
        var s = EqShiftDay(shifts, model);
        shiftDeviceIcon.Text = s.Icon;
        shiftDriverLabel.Text = date.ToString("MM-dd");
        shiftStatusLabel.Text = s.AnyFault ? "有故障" : "运行";

        shiftKpiTotalLoads.Text = $"{s.LoadCount}";
        shiftKpiWorkHours.Text = $"{s.TotalWork:F1}";
        shiftKpiAvgLoadTime.Text = s.LoadCount > 0 ? $"{s.AvgLoadTimeMin:F1}" : "—";
        shiftKpiAvgEff.Text = $"{s.AvgEff:F0}";
        shiftKpiPeakEff.Text = $"{s.PeakEff:F0}";
        shiftKpiWaitTime.Text = $"{s.WaitMin:F0}";

        shiftBottomIneffective.Text = $"{s.Ineffective:F1} H";
        shiftBottomWork.Text = $"{s.TotalWork:F1} H";
        shiftBottomIdle.Text = $"{s.Ineffective:F1} H";
        shiftBottomFault.Text = $"{s.TotalFault:F1} H";
        shiftBottomTotal.Text = "24.0 H";
        shiftBottomDispatch.Text = $"{s.DispatchPct:F0} %";
        shiftBottomActual.Text = $"{s.ActualPct:F0} %";
        shiftBottomUtil.Text = $"{s.UtilPct:F0} %";
        shiftBottomAvgPerLoad.Text = s.LoadCount > 0 ? $"{s.AvgPerLoad:F0} m³" : "— m³";

        shiftEffChart.Categories = shifts.Select(x => $"{x.Shift} 班").ToList();
        shiftEffChart.Series = new List<ChartSeries>
        {
            new() { Name = "瞬时效率 m³/h", Kind = SeriesKind.Line, Color = Color.Parse("#1976D2"), StrokeThickness = 3, MarkerSize = 12, Values = shifts.Select(x => (double?)(x.WorkHours > 0 ? x.OutputM3 / x.WorkHours : 0)).ToList() },
        };
        shiftEffChart.Refresh();

        _currentShifts = shifts;
        DrawStatusBlocks();
    }

    private void OnStatusBarSizeChanged(object? sender, SizeChangedEventArgs e) => DrawStatusBlocks();

    /// <summary>状态色条: A/B/C 三班横向 [作业绿|故障红|待命灰], 各占 1/3(8h)。</summary>
    private void DrawStatusBlocks()
    {
        statusCanvas.Children.Clear();
        if (_currentShifts.Count == 0) return;
        double totalWidth = statusCanvas.Bounds.Width, height = statusCanvas.Bounds.Height;
        if (totalWidth < 10 || height < 10) return;
        double sliceWidth = totalWidth / 3.0, x = 0;
        foreach (var shiftName in new[] { "A", "B", "C" })
        {
            var rec = _currentShifts.FirstOrDefault(r => r.Shift == shiftName);
            if (rec == null) { AddBlock(x, sliceWidth, height, Brushes.LightGray); x += sliceWidth; continue; }
            double workW = rec.WorkHours / 8.0 * sliceWidth, faultW = rec.FaultHours / 8.0 * sliceWidth, idleW = Math.Max(0, sliceWidth - workW - faultW);
            if (workW > 0) AddBlock(x, workW, height, B("#66BB6A"));
            if (faultW > 0) AddBlock(x + workW, faultW, height, B("#EF5350"));
            if (idleW > 0) AddBlock(x + workW + faultW, idleW, height, B("#E0E0E0"));
            x += sliceWidth;
        }
        for (int i = 1; i < 3; i++)
            statusCanvas.Children.Add(new Line { StartPoint = new Point(i * sliceWidth, 0), EndPoint = new Point(i * sliceWidth, height), Stroke = Brushes.Black, StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 2, 2 } });
    }

    private void AddBlock(double x, double w, double h, IBrush brush)
    {
        var rect = new Rectangle { Width = w, Height = h, Fill = brush };
        Canvas.SetLeft(rect, x); Canvas.SetTop(rect, 0);
        statusCanvas.Children.Add(rect);
    }

    // ═══ 因素分析(主控因素 + 相关性 + 回归 + 建议) ═══
    private void RenderFactorAnalysis(List<EqKpiRow> kpis, List<EqFaultRow> faults, List<EqProductionRecord> prodAll)
    {
        if (kpis.Count < 3 && prodAll.Count < 5)
        {
            ClearPanel(suggestionPanel);
            AddCard(suggestionPanel, new EqCard("⚠️", "样本不足", $"该设备 KPI 记录仅 {kpis.Count} 条,生产记录 {prodAll.Count} 条 — 因素分析需要至少 5+ 条样本", "#FFF8E1", "#E65100"));
            return;
        }
        (_outputValues, _factorValues) = EqFactorObservations(kpis, faults);

        regressionFactorCombo.SelectionChanged -= OnRegressionFactorChanged;
        regressionFactorCombo.ItemsSource = _factorValues.Select(f => f.Factor).ToList();
        regressionFactorCombo.SelectedIndex = 0;
        regressionFactorCombo.SelectionChanged += OnRegressionFactorChanged;

        RenderWaterfall();
        RenderCorrelationHeatmap();
        RenderRegressionScatter(0);
        RenderSuggestions(kpis, faults, kpis.Count);
    }

    private void RenderWaterfall()
    {
        if (_outputValues.Length < 2) { waterfallChart.Series = new(); waterfallChart.Refresh(); return; }
        var c = EqFactorContributions(_outputValues, _factorValues);
        waterfallChart.Categories = c.Select(x => x.Name).ToList();
        waterfallChart.Series = new List<ChartSeries>
        {
            new() { Name = "贡献度", Kind = SeriesKind.Bar, Values = c.Select(x => (double?)x.Contribution).ToList(),
                    PointColors = c.Select(x => (Color?)Color.Parse(x.Contribution >= 0 ? "#66BB6A" : "#EF5350")).ToList(), ShowValueLabels = true, ValueFormat = "0" },
        };
        waterfallChart.Refresh();
    }

    private void RenderCorrelationHeatmap()
    {
        if (_factorValues.Count == 0 || _outputValues.Length < 2) { correlationChart.Series = new(); correlationChart.Refresh(); return; }
        var (labels, m) = EqCorrelationMatrix(_outputValues, _factorValues);
        correlationChart.Categories = labels; correlationChart.YCategories = labels;
        correlationChart.Series = new List<ChartSeries> { new() { Name = "相关系数", Kind = SeriesKind.Heatmap, Matrix = m, ValueFormat = "0.00" } };
        correlationChart.Refresh();
    }

    private void OnRegressionFactorChanged(object? sender, SelectionChangedEventArgs e) => RenderRegressionScatter(regressionFactorCombo.SelectedIndex);

    private void RenderRegressionScatter(int factorIdx)
    {
        if (factorIdx < 0 || factorIdx >= _factorValues.Count || _outputValues.Length < 2) { regressionChart.Series = new(); regressionChart.Refresh(); return; }
        var (name, xs) = _factorValues[factorIdx];
        var ys = _outputValues;
        var (a, b) = EqLinearRegression(xs, ys);
        var r = EqCorrelate(xs, ys);
        double xMin = xs.Min(), xMax = xs.Max();
        regressionChart.XTitle = name;
        regressionChart.Series = new List<ChartSeries>
        {
            new() { Name = $"{name} 观测点", Kind = SeriesKind.Scatter, Color = Color.Parse("#1976D2"), MarkerSize = 12, Points = xs.Zip(ys, (x, y) => (x, y)).ToList() },
            new() { Name = $"回归线 (r={r:F2})", Kind = SeriesKind.Line, Color = Color.Parse("#FF8F00"), StrokeThickness = 3, ShowMarkers = false, Points = new() { (xMin, a * xMin + b), (xMax, a * xMax + b) } },
        };
        regressionChart.Refresh();
        regressionHint.Text = $"当前:{name}  ·  r={r:F2}  ·  斜率={a:F1}";
    }

    private void RenderSuggestions(List<EqKpiRow> kpis, List<EqFaultRow> faults, int sampleCount)
    {
        ClearPanel(suggestionPanel);
        suggestionPanel.Children.Add(new TextBlock { Text = $"基于 {sampleCount} 条月度 KPI + {faults.Count} 条故障记录,生成以下建议:", FontStyle = FontStyle.Italic, Foreground = B("#666666"), Margin = new Thickness(0, 0, 0, 12) });
        foreach (var c in EqSuggestionCards(kpis, faults, _outputValues, _factorValues)) AddCard(suggestionPanel, c);
    }

    private void RenderReliability(List<EqKpiRow> kpis, List<EqFaultRow> faults, string model)
    {
        ClearPanel(reliabilityPanel);
        foreach (var c in EqReliabilityCards(kpis, faults, model))
        {
            var card = new Border { Background = B(c.Bg), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 8) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeight.Bold, FontSize = 13, Foreground = B(c.Fg) });
            sp.Children.Add(new TextBlock { Text = c.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = B("#333333") });
            card.Child = sp;
            reliabilityPanel.Children.Add(card);
        }
    }

    /// <summary>清空面板但保留首条说明 TextBlock。</summary>
    private static void ClearPanel(StackPanel panel)
    {
        while (panel.Children.Count > 1) panel.Children.RemoveAt(panel.Children.Count - 1);
    }

    private static void AddCard(StackPanel panel, EqCard c)
    {
        var card = new Border { Background = B(c.Bg), CornerRadius = new CornerRadius(4), Padding = new Thickness(14, 12), Margin = new Thickness(0, 0, 0, 10), BorderBrush = B(c.Fg), BorderThickness = new Thickness(0, 0, 0, 3) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*") };
        grid.Children.Add(new TextBlock { Text = c.Icon, FontSize = 22, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeight.Bold, FontSize = 14, Foreground = B(c.Fg) });
        stack.Children.Add(new TextBlock { Text = c.Detail, FontSize = 12, Foreground = B("#444444"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 20 });
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        card.Child = grid;
        panel.Children.Add(card);
    }
}
