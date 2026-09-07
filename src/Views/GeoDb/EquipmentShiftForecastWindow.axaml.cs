using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 班次生产效能预测(忠实原 EquipmentShiftForecastWindow): 面向单台设备, 基于历史班次记录预测下班产能 +
/// 能力分布 + 五维评分 + 编组建议。预测=ForecastModels(趋势+EWMA), 置信=越远越宽半宽, 风险=CV+故障, 评分五维归一化, §2.5 熵权综合分。
/// </summary>
public partial class EquipmentShiftForecastWindow : Window
{
    private readonly GeoDbContext _ctx;
    private List<EqProductionRecord> _allRecords = new();
    private List<EqKpiRow> _allKpis = new();
    private List<EqFaultRow> _allFaults = new();
    private string? _selectedEquipmentId;
    private double[] _wEntropy = { 0.25, 0.25, 0.20, 0.15, 0.15 };
    private const int BaseYear = 2023, BaseMonth = 12;   // 原窗口以 2023-12 KPI 建设备清单

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentShiftForecastWindow() { _ctx = null!; InitializeComponent(); }

    public EquipmentShiftForecastWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        LoadData();
        BuildEquipmentList();
    }

    private void LoadData()
    {
        _allRecords = EqLoadProduction(_ctx.Conn);
        _allKpis = EqLoadKpi(_ctx.Conn);
        _allFaults = EqLoadFaults(_ctx.Conn);
        _wEntropy = EqFleetEntropyWeights(_allRecords, _allKpis, _allFaults, BaseYear, BaseMonth);
        statusText.Text = $"已加载:{_allRecords.Count} 条班次 / {_allKpis.Count} 条 KPI / {_allFaults.Count} 条故障";
    }

    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    // ─── 设备列表(按分类分组,带颜色) ───
    private void BuildEquipmentList()
    {
        equipmentListPanel.Children.Clear();
        var groups = _allKpis.Where(k => k.Year == BaseYear && k.Month == BaseMonth).GroupBy(k => EqCategoryOfModel(k.Model)).OrderBy(g => EqCategoryOrder(g.Key));
        foreach (var grp in groups)
        {
            var (catName, catColor, catIcon) = EqCategoryStyle(grp.Key);
            equipmentListPanel.Children.Add(new Border
            {
                Background = B(catColor), Padding = new Thickness(10, 6), Margin = new Thickness(0, 8, 0, 0),
                Child = new TextBlock { Text = $"{catIcon} {catName} ({grp.Count()})", FontWeight = FontWeight.Bold, FontSize = 12, Foreground = Brushes.White },
            });
            foreach (var k in grp.OrderBy(x => x.EquipmentId))
            {
                var item = new Border { Background = Brushes.White, BorderBrush = B("#E0E0E0"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 6, 8, 6), Cursor = new Cursor(StandardCursorType.Hand), Tag = k.EquipmentId };
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = k.EquipmentId, FontWeight = FontWeight.Bold, FontSize = 13 });
                stack.Children.Add(new TextBlock { Text = k.Model, Foreground = B("#666666"), FontSize = 11 });
                item.Child = stack;
                item.PointerEntered += (s, _) => ((Border)s!).Background = B("#E3F2FD");
                item.PointerExited += (s, _) => ((Border)s!).Background = Brushes.White;
                item.PointerReleased += (s, _) => SelectEquipment((string)((Border)s!).Tag!);
                equipmentListPanel.Children.Add(item);
            }
        }
        var first = _allKpis.FirstOrDefault(k => k.Year == BaseYear && k.Month == BaseMonth);
        if (first != null) SelectEquipment(first.EquipmentId);
        else statusText.Text += $" · 无 {BaseYear}-{BaseMonth:D2} 基准 KPI, 设备清单为空";
    }

    // ─── 选中设备 → 渲染所有视图 ───
    private void SelectEquipment(string equipmentId)
    {
        _selectedEquipmentId = equipmentId;
        var records = _allRecords.Where(r => r.EquipmentId == equipmentId).OrderBy(r => r.Date).ThenBy(r => r.Shift).ToList();
        var kpi = _allKpis.FirstOrDefault(k => k.EquipmentId == equipmentId && k.Year == BaseYear && k.Month == BaseMonth);
        var faults = _allFaults.Where(f => f.EquipmentId == equipmentId).ToList();
        if (records.Count == 0 || kpi == null) { statusText.Text = $"{equipmentId} 暂无班次数据,无法预测"; return; }
        var validShifts = records.Where(r => r.WorkHours > 0).ToList();
        if (validShifts.Count == 0) { statusText.Text = $"{equipmentId} 全部班次为故障停机"; return; }

        var stats = EqComputeStats(validShifts);
        var fr = ForecastModels.Forecast(validShifts.Select(r => r.OutputM3).ToList(), 6);

        RenderTopKpis(kpi, stats, fr, faults);
        RenderTrendChart(records, fr);
        RenderHistogram(validShifts, stats);
        RenderScatter(validShifts);
        RenderScores(stats, kpi, faults);
        RenderAnalysisDetails(stats, faults);
        RenderProbabilistic(validShifts, fr);
        RenderDispatchSuggestion(kpi, stats);
        statusText.Text = $"{equipmentId} · {kpi.Model} · 共 {records.Count} 条班次 · {validShifts.Count} 条有效";
    }

    private void RenderTopKpis(EqKpiRow kpi, EqShiftStats stats, ForecastResult fr, List<EqFaultRow> faults)
    {
        double forecast = fr.Next;
        var unit = EqUnitFor(kpi.Model);
        kpiNextOutput.Text = $"{forecast:F0}";
        kpiNextOutputUnit.Text = unit;
        kpiNextOutputHint.Text = $"趋势 {fr.TrendLabel} · {fr.Method}";
        kpiConfidence.Text = $"±{fr.HalfWidthAt(0):F0} {unit}";
        kpiVariance.Text = $"变异系数 {stats.Cv * 100:F1}%" + (fr.AnomalyCount > 0 ? $" · 异常 {fr.AnomalyCount} 班" : "");
        int faultPerMonth = faults.Count(f => f.Date >= DateTime.Now.AddMonths(-1));
        var (riskText, riskHint, _) = EqRiskLevel(stats.Cv, faultPerMonth);
        kpiRiskLevel.Text = riskText; kpiRiskHint.Text = riskHint;
        var (score, hint) = EqPotentialScore(kpi, stats, forecast, faultPerMonth);
        kpiOverallScore.Text = $"{score}"; kpiOverallHint.Text = hint;
    }

    private void RenderTrendChart(List<EqProductionRecord> records, ForecastResult fr)
    {
        var sorted = records.OrderBy(r => r.Date).ThenBy(r => r.Shift).ToList();
        var labels = sorted.Select(r => $"{r.Date:MM-dd} {r.Shift}").ToList();
        var values = sorted.Select(r => (double?)r.OutputM3).ToList();
        for (int i = 0; i < 6; i++) { values.Add(null); labels.Add($"+{i + 1}班"); }
        int n = sorted.Count, total = values.Count;
        var forecastValues = new List<double?>(new double?[total]);
        var upper = new List<double?>(new double?[total]);
        var lower = new List<double?>(new double?[total]);
        if (n > 0) { forecastValues[n - 1] = sorted[^1].OutputM3; upper[n - 1] = sorted[^1].OutputM3; lower[n - 1] = sorted[^1].OutputM3; }
        for (int i = 0; i < 6; i++)
        {
            double p = i < fr.Path.Length ? fr.Path[i] : fr.Next, hw = fr.HalfWidthAt(i);
            forecastValues[n + i] = p; upper[n + i] = p + hw; lower[n + i] = Math.Max(0, p - hw);
        }
        trendChart.Categories = labels;
        trendChart.Series = new List<ChartSeries>
        {
            new() { Name = "历史班次产量", Kind = SeriesKind.Line, Color = Color.Parse("#1976D2"), StrokeThickness = 3, MarkerSize = 7, Values = values },
            new() { Name = "未来 6 班预测", Kind = SeriesKind.Line, Color = Color.Parse("#FF8F00"), StrokeThickness = 3, Dashed = true, MarkerSize = 8, Values = forecastValues },
            new() { Name = "95% 上界", Kind = SeriesKind.Line, Color = Color.Parse("#FFCC80"), StrokeThickness = 1, Dashed = true, ShowMarkers = false, Values = upper },
            new() { Name = "95% 下界", Kind = SeriesKind.Line, Color = Color.Parse("#FFCC80"), StrokeThickness = 1, Dashed = true, ShowMarkers = false, Values = lower },
        };
        trendChart.Refresh();
    }

    private void RenderHistogram(List<EqProductionRecord> validShifts, EqShiftStats stats)
    {
        var (labels, counts) = EqHistogram(validShifts.Select(r => r.OutputM3).ToList(), stats.Min, stats.Max);
        histChart.Categories = labels.ToList();
        histChart.Series = new List<ChartSeries> { new() { Name = "频次", Kind = SeriesKind.Bar, Color = Color.Parse("#42A5F5"), Values = counts.Select(c => (double?)c).ToList() } };
        histChart.Refresh();
    }

    private void RenderScatter(List<EqProductionRecord> validShifts)
    {
        scatterChart.Series = new List<ChartSeries> { new() { Name = "班次", Kind = SeriesKind.Scatter, Color = Color.Parse("#9C27B0"), MarkerSize = 14, Points = validShifts.Select(r => (r.WorkHours, r.OutputM3)).ToList() } };
        scatterChart.Refresh();
    }

    private void RenderScores(EqShiftStats stats, EqKpiRow kpi, List<EqFaultRow> faults)
    {
        var (output, stability, avail, eff, reliability) = EqFiveScores(stats, kpi, faults.Count);
        scoreOutput.Value = output; scoreOutputValue.Text = $"{output}";
        scoreStability.Value = stability; scoreStabilityValue.Text = $"{stability}";
        scoreAvail.Value = avail; scoreAvailValue.Text = $"{avail}";
        scoreEfficiency.Value = eff; scoreEfficiencyValue.Text = $"{eff}";
        scoreReliability.Value = reliability; scoreReliabilityValue.Text = $"{reliability}";
    }

    private void RenderProbabilistic(List<EqProductionRecord> validShifts, ForecastResult fr)
    {
        if (validShifts.Count < 3) return;
        var (p10, p50, p90, meetProb) = EqProbabilistic(validShifts.Select(r => r.OutputM3).ToList(), fr.Next);
        AddDetailLine(analysisDetails, "🎲 下班产能 P10 / P50 / P90", $"{p10:F0} / {p50:F0} / {p90:F0}", isBold: true, color: "#1976D2");
        AddDetailLine(analysisDetails, "🎯 达产概率(≥历史均值)", $"{meetProb:F0}%", color: meetProb >= 60 ? "#388E3C" : "#E65100");
        var dims = _selectedEquipmentId != null ? EqFiveDims(_selectedEquipmentId, _allRecords, _allKpis, _allFaults, BaseYear, BaseMonth) : null;
        if (dims != null)
        {
            double comp = dims.Zip(_wEntropy, (x, w) => x * w).Sum() * 100;
            AddDetailLine(analysisDetails, "⚖️ 熵权客观综合分", $"{comp:F0} 分 · 权重 {string.Join("/", _wEntropy.Select(w => (w * 100).ToString("F0")))}%", color: "#6A1B9A");
        }
    }

    private void RenderAnalysisDetails(EqShiftStats stats, List<EqFaultRow> faults)
    {
        analysisDetails.Children.Clear();
        AddDetailLine(analysisDetails, "🎯 历史班次产量峰值", $"{stats.Max:F0}");
        AddDetailLine(analysisDetails, "📉 历史班次产量谷底", $"{stats.Min:F0}");
        AddDetailLine(analysisDetails, "📊 班次产量均值", $"{stats.Mean:F0}", isBold: true);
        AddDetailLine(analysisDetails, "📐 标准差 σ", $"{stats.Std:F0}");
        AddDetailLine(analysisDetails, "🎚️ P5 - P95 区间", $"{stats.P5:F0} ~ {stats.P95:F0}");
        AddDetailLine(analysisDetails, "🔄 变异系数 CV", $"{stats.Cv * 100:F1}%", color: stats.Cv > 0.3 ? "#C62828" : "#388E3C");
        AddDetailLine(analysisDetails, "🛠️ 月度故障次数", $"{faults.Count}", color: faults.Count > 5 ? "#C62828" : "#388E3C");
        AddDetailLine(analysisDetails, "⏱️ 月度故障总时长", $"{faults.Sum(f => f.DurationHours):F1} h");
        var topFault = faults.GroupBy(f => f.FaultType).OrderByDescending(g => g.Sum(f => f.DurationHours)).FirstOrDefault();
        if (topFault != null) AddDetailLine(analysisDetails, "⚠️ 主要故障类型", $"{topFault.Key} ({topFault.Sum(f => f.DurationHours):F1} h)", color: "#C62828");
    }

    private static void AddDetailLine(Panel parent, string label, string value, bool isBold = false, string? color = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 3), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = B("#555555") });
        var v = new TextBlock { Text = value, FontSize = 13, FontWeight = isBold ? FontWeight.Bold : FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        if (color != null) v.Foreground = B(color);
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        parent.Children.Add(grid);
    }

    private void RenderDispatchSuggestion(EqKpiRow kpi, EqShiftStats stats)
    {
        dispatchSuggestion.Children.Clear();
        var cat = EqCategoryOfModel(kpi.Model);
        var (verdict, role, hint) = EqDispatchVerdict(stats.Cv, kpi.Availability);
        dispatchSuggestion.Children.Add(new TextBlock { Text = verdict, FontWeight = FontWeight.Bold, FontSize = 14, Foreground = B("#2E7D32"), Margin = new Thickness(0, 0, 0, 10) });
        AddDetailLine(dispatchSuggestion, "🎭 推荐角色", role);
        AddDetailLine(dispatchSuggestion, "💡 调度提示", hint);
        if (cat == "Shovel")
        {
            AddDetailLine(dispatchSuggestion, "🚛 推荐配车数", stats.Mean > 8000 ? "3 台 930E + 4 台 730E" : "2 台 930E + 3 台 730E");
            AddDetailLine(dispatchSuggestion, "⏱️ 单循环", $"约 {(stats.Mean > 8000 ? 28 : 32)} 分钟");
        }
        else if (cat == "Truck")
        {
            AddDetailLine(dispatchSuggestion, "⛏️ 推荐配铲", stats.Mean > 5000 ? "4100XPC 优先" : "PH2800 / WK-35 均可");
            AddDetailLine(dispatchSuggestion, "🛣️ 推荐运距", "≤ 3 km");
        }
        else if (cat == "Drill")
        {
            AddDetailLine(dispatchSuggestion, "💥 推荐爆破区", stats.Mean > 65 ? "主炮区(优先)" : "辅助炮区");
            AddDetailLine(dispatchSuggestion, "🔨 每班期望孔深", $"{stats.Mean:F0} m,搭配中等炸药系数 0.35 kg/m³");
        }
        AddDetailLine(dispatchSuggestion, "📈 下一步分析建议", "结合『设备智能编组』查看完整匹配方案");
    }
}
