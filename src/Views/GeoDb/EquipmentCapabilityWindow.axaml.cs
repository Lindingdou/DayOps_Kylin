using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 设备能力分析(忠实原 EquipmentCapabilityWindow): 决策结论横幅 + 4 KPI(峰值/当前年化/CAGR/同型号排名) +
/// Tab(衰减趋势: 年累计+滚动均线+异常带 · 环比 · 月×年热力 / 同类对标: 台年产能·可用率 / 损失归因: OEE 三率乘性瀑布) + 自动诊断。
/// </summary>
public partial class EquipmentCapabilityWindow : Window
{
    private readonly GeoDbContext _ctx;
    private List<EqCapacityRow> _allRecords = new();
    private List<EqKpiRow> _kpiAll = new();
    private Dictionary<string, double> _availByEq = new();
    private bool _ready;

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentCapabilityWindow() { _ctx = null!; InitializeComponent(); }

    public EquipmentCapabilityWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        LoadData();
        _ready = true;
        if (equipmentList.ItemCount > 0) equipmentList.SelectedIndex = 0;
    }

    private void LoadData()
    {
        _allRecords = EqLoadCapacity(_ctx.Conn);
        try { _kpiAll = EqLoadKpi(_ctx.Conn); _availByEq = EqLatestAvailByEq(_kpiAll); } catch { _kpiAll = new(); _availByEq = new(); }
        var summaries = EqEquipmentSummaries(_allRecords);
        equipmentList.ItemsSource = summaries;
        statusText.Text = $"加载 {_allRecords.Count} 条记录,{summaries.Count} 台设备";
    }

    /// <summary>按编号选中(供外部下钻)。</summary>
    public void SelectEquipment(string equipmentId)
    {
        if (equipmentList.ItemsSource is List<EqEquipmentSummary> l)
        {
            var hit = l.FirstOrDefault(s => s.EquipmentId == equipmentId);
            if (hit != null) equipmentList.SelectedItem = hit;
        }
    }

    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    private void OnEquipmentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || equipmentList.SelectedItem is not EqEquipmentSummary sel) return;
        var records = _allRecords.Where(r => r.EquipmentId == sel.EquipmentId).OrderBy(r => r.Year).ThenBy(r => r.Month).ToList();
        if (records.Count == 0) return;
        var kpi = _kpiAll.Where(k => k.EquipmentId == sel.EquipmentId).OrderByDescending(k => k.Year).ThenByDescending(k => k.Month).FirstOrDefault();

        RenderKpis(sel, records);
        RenderAnnualTrend(records);
        RenderDeclineWaterfall(records);
        RenderHeatmap(records);
        RenderPeerRanking(sel);
        RenderPeerAging(sel);
        RenderDiagnosis(sel, records);
        RenderLossWaterfall(records, kpi);
        var (icon, color, headline, action) = EqCapabilityVerdict(sel, records, kpi);
        verdictBanner.Background = B(color);
        verdictIcon.Text = icon; verdictHeadline.Text = headline; verdictAction.Text = action;
    }

    private void RenderKpis(EqEquipmentSummary sel, List<EqCapacityRow> records)
    {
        var k = EqCapabilityKpisOf(_allRecords, sel, records);
        kpiPeakYearOutput.Text = $"{k.PeakWan:F0}";
        kpiPeakYearLabel.Text = $"{k.PeakYear} 年";
        kpiCurrentYearTitle.Text = $"{k.LatestYear} 台年能力" + (k.LatestPartial ? "(年化)" : "");
        kpiCurrentYearOutput.Text = $"{k.LatestWan:F0}";
        kpiCurrentYearRatio.Text = k.PeakWan > 0 ? $"为峰值 {k.Ratio * 100:F0}%" + (k.LatestPartial ? " · 1-N月年化" : "") : "—";
        kpiAnnualDecline.Text = k.HasCagr ? (k.Cagr >= 0 ? $"+{k.Cagr * 100:F1}%" : $"{k.Cagr * 100:F1}%") : "—";
        kpiDeclineHint.Text = k.DeclineHint;
        kpiRank.Text = k.Rank > 0 ? $"#{k.Rank} / {k.PeerCount}" : "—";
        kpiRankHint.Text = $"{sel.Model} 同型号";
    }

    private void RenderAnnualTrend(List<EqCapacityRow> records)
    {
        var yearly = EqYearlyAnnualized(records).Select(y => (y.Year, Total: y.Total / 10000.0)).ToList();
        if (yearly.Count == 0) { annualTrendChart.Series = new(); annualTrendChart.Refresh(); return; }
        var values = yearly.Select(y => y.Total).ToArray();
        var rolling = new List<double?>();
        for (int i = 0; i < values.Length; i++) { int lo = Math.Max(0, i - 2); rolling.Add(values.Skip(lo).Take(i - lo + 1).Average()); }
        double mean = values.Average(), std = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, values.Length - 1));
        annualTrendChart.Categories = yearly.Select(y => y.Year.ToString()).ToList();
        annualTrendChart.Series = new List<ChartSeries>
        {
            new() { Name = "年累计(万 m³)", Kind = SeriesKind.Bar, Color = Color.Parse("#42A5F5"), Values = values.Select(v => (double?)v).ToList() },
            new() { Name = "3 年滚动均线", Kind = SeriesKind.Line, Color = Color.Parse("#FF8F00"), StrokeThickness = 3, Values = rolling },
            new() { Name = "+1σ", Kind = SeriesKind.Line, Color = Color.Parse("#BDBDBD"), StrokeThickness = 1, Dashed = true, ShowMarkers = false, Values = Enumerable.Repeat((double?)(mean + std), values.Length).ToList() },
            new() { Name = "-1σ", Kind = SeriesKind.Line, Color = Color.Parse("#BDBDBD"), StrokeThickness = 1, Dashed = true, ShowMarkers = false, Values = Enumerable.Repeat((double?)Math.Max(0, mean - std), values.Length).ToList() },
        };
        annualTrendChart.Refresh();
    }

    private void RenderDeclineWaterfall(List<EqCapacityRow> records)
    {
        var rates = EqYoyRates(EqYearlyAnnualized(records));
        if (rates.Count == 0) { declineChart.Series = new(); declineChart.Refresh(); return; }
        declineChart.Categories = rates.Select(r => r.Year.ToString()).ToList();
        declineChart.Series = new List<ChartSeries>
        {
            new() { Name = "环比 %", Kind = SeriesKind.Bar, Values = rates.Select(r => (double?)r.RatePct).ToList(),
                    PointColors = rates.Select(r => (Color?)Color.Parse(r.RatePct >= 0 ? "#66BB6A" : "#EF5350")).ToList(), ShowValueLabels = true, ValueFormat = "0'%'" },
        };
        declineChart.Refresh();
    }

    private void RenderHeatmap(List<EqCapacityRow> records)
    {
        var years = records.Select(r => r.Year).Distinct().OrderBy(y => y).ToList();
        if (years.Count == 0) { heatmapChart.Series = new(); heatmapChart.Refresh(); return; }
        var m = new double[years.Count, 12];
        for (int yi = 0; yi < years.Count; yi++)
            for (int mi = 0; mi < 12; mi++)
                m[yi, mi] = (records.FirstOrDefault(r => r.Year == years[yi] && r.Month == mi + 1)?.OutputM3 ?? 0) / 10000.0;
        heatmapChart.Categories = Enumerable.Range(1, 12).Select(i => $"{i}月").ToList();
        heatmapChart.YCategories = years.Select(y => y.ToString()).ToList();
        heatmapChart.Series = new List<ChartSeries> { new() { Name = "万 m³", Kind = SeriesKind.Heatmap, Matrix = m, ValueFormat = "0" } };
        heatmapChart.Refresh();
    }

    /// <summary>本机型号高亮(绿)的柱: 其他型号灰。</summary>
    private static List<ChartSeries> HighlightBars(List<(string Model, double OutWan, double AvailPct, int Units)> stats, string selfModel, Func<(string Model, double OutWan, double AvailPct, int Units), double> val, string suffix)
        => new()
        {
            new() { Name = "其他型号 / 本机型号(绿)", Kind = SeriesKind.Bar, Values = stats.Select(s => (double?)Math.Round(val(s), 0)).ToList(),
                    PointColors = stats.Select(s => (Color?)Color.Parse(s.Model == selfModel ? "#2E7D32" : "#90A4AE")).ToList(), ShowValueLabels = true, ValueFormat = "0" + (suffix.Length > 0 ? "'" + suffix + "'" : "") },
        };

    private void RenderPeerRanking(EqEquipmentSummary sel)
    {
        var stats = EqCategoryModelStats(_allRecords, sel.Category, _availByEq);
        if (stats.Count == 0) { boxplotChart.Series = new(); boxplotChart.Refresh(); peerRankCaption.Text = "暂无同类设备数据。"; return; }
        boxplotChart.Categories = stats.Select(s => s.Model).ToList();
        boxplotChart.Series = HighlightBars(stats, sel.Model, s => s.OutWan, "");
        boxplotChart.Refresh();
        int rank = stats.FindIndex(s => s.Model == sel.Model) + 1;
        var self = stats.FirstOrDefault(s => s.Model == sel.Model);
        string tag = rank == 1 ? "同类产能最高" : rank <= (stats.Count + 1) / 2 ? "处于同类上半区" : "处于同类下半区";
        peerRankCaption.Text = $"本机型号 {sel.Model}（{EqCatCn(sel.Category)}）:台年产能 {self.OutWan:F0} 万m³/台 · 同类 {stats.Count} 个型号中第 {rank} 位 · {tag}";
    }

    private void RenderPeerAging(EqEquipmentSummary sel)
    {
        var stats = EqCategoryModelStats(_allRecords, sel.Category, _availByEq);
        if (stats.Count == 0) { bubbleChart.Series = new(); bubbleChart.Refresh(); peerAgingCaption.Text = "暂无同类设备数据。"; return; }
        bubbleChart.Categories = stats.Select(s => s.Model).ToList();
        bubbleChart.Series = HighlightBars(stats, sel.Model, s => s.AvailPct, "%");
        bubbleChart.Refresh();
        var self = stats.FirstOrDefault(s => s.Model == sel.Model);
        int availRank = stats.OrderByDescending(s => s.AvailPct).ToList().FindIndex(s => s.Model == sel.Model) + 1;
        string tag = self.AvailPct >= 88 ? "可靠性优" : self.AvailPct >= 84 ? "可靠性中等" : "可靠性偏低,需加强检修";
        peerAgingCaption.Text = $"本机型号 {sel.Model}:可用率 {self.AvailPct:F0}% · 同类第 {availRank} 位 · {tag}";
    }

    private void RenderLossWaterfall(List<EqCapacityRow> records, EqKpiRow? kpi)
    {
        var w = EqLossWaterfall(records, kpi, out var note);
        if (w == null) { lossWaterfallChart.Series = new(); lossWaterfallChart.Refresh(); lossSummary.Text = note; return; }
        var (theo, lossA, lossR, lossU, actual, _, summary) = w.Value;
        lossWaterfallChart.Categories = new() { "理论上限", "可用率损失", "作业率损失", "利用率损失", "实际产能" };
        // 瀑布: 理论 → 逐级扣减(浮动柱) → 实际
        double a1 = theo - lossA, a2 = a1 - lossR, a3 = a2 - lossU;
        lossWaterfallChart.Series = new List<ChartSeries>
        {
            new() { Name = "万 m³", Kind = SeriesKind.FloatingBar, Ranges = new() { (0, theo), (a1, theo), (a2, a1), (a3, a2), (0, actual) },
                    PointColors = new() { Color.Parse("#2694A6"), Color.Parse("#EF6C6C"), Color.Parse("#EF6C6C"), Color.Parse("#EF6C6C"), Color.Parse("#43A047") }, ShowValueLabels = true, ValueFormat = "0" },
        };
        lossWaterfallChart.Refresh();
        lossSummary.Text = summary;
    }

    private void RenderDiagnosis(EqEquipmentSummary sel, List<EqCapacityRow> records)
    {
        diagnosisPanel.Children.Clear();
        foreach (var c in EqCapabilityDiagnosis(_allRecords, sel, records))
        {
            var border = new Border { Background = B(c.Bg), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 12, 6), Margin = new Thickness(0, 0, 8, 4), BorderBrush = B(c.Fg), BorderThickness = new Thickness(0, 0, 0, 2) };
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock { Text = c.Icon, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var right = new StackPanel();
            right.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeight.Bold, FontSize = 12, Foreground = B(c.Fg) });
            right.Children.Add(new TextBlock { Text = c.Detail, FontSize = 10, Foreground = B("#666666") });
            stack.Children.Add(right);
            border.Child = stack;
            diagnosisPanel.Children.Add(border);
        }
    }
}
