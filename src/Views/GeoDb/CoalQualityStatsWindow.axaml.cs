using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>煤质统计分析窗(忠实原 CoalQualityStatsWindow): 分组汇总 + 箱线 + 散点 + 直方 + 相关矩阵 + 高程剖面 + 深度分析标签 + 结论。</summary>
public partial class CoalQualityStatsWindow : Window
{
    private readonly GeoDbContext _ctx;
    private List<CoalSampleRow> _all = new();
    private List<CoalSample> _analytics = new();
    private List<CoalSeamDefRow> _seams = new();
    private readonly Dictionary<string, List<CoalGradeRuleFull>> _rules = new();
    private bool _ready;

    public ObservableCollection<CoalSeamStat> StatsRows { get; } = new();

    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush Primary = new SolidColorBrush(Color.Parse("#1F2937"));
    private static readonly IBrush PanelBg = new SolidColorBrush(Color.Parse("#F7F8FA"));
    private static readonly IBrush BorderBr = new SolidColorBrush(Color.Parse("#DCDFE4"));

    /// <summary>仅供 XAML 设计器/编译器使用。</summary>
    public CoalQualityStatsWindow() { _ctx = null!; InitializeComponent(); }

    public CoalQualityStatsWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        statsGrid.ItemsSource = StatsRows;
        _seams = CoalSeamDefs(_ctx.Conn);
        foreach (var t in new[] { "ash", "sulfur", "qnet" }) _rules[t] = CoalGradeRules(_ctx.Conn, t);
        InitFilters();
        _ready = true;
        Recompute();
    }

    private void InitFilters()
    {
        seamFilter.Items.Clear();
        seamFilter.Items.Add("全部");
        foreach (var s in _seams) seamFilter.Items.Add(s.Code);
        seamFilter.SelectedIndex = 0;
    }

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) { if (_ready) Recompute(); }
    private void OnRecomputeClick(object? sender, RoutedEventArgs e) => Recompute();

    private string Indicator() => (indicatorFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "ad_raw";
    private string? SeamSel() => seamFilter.SelectedItem?.ToString();
    private bool UseClean() => rawOrCleanFilter.SelectedIndex == 1;

    private void Recompute()
    {
        try
        {
            _all = CoalLoadSamples(_ctx.Conn);
            _analytics = _all.Select(s => s.ToAnalytics()).ToList();
            var indicator = Indicator();
            var seamSel = SeamSel();
            bool useClean = UseClean();

            var stats = CoalStatsBySeam(_all, CoalMapColumn(indicator, useClean));
            var filtered = (seamSel == "全部" ? stats : stats.Where(s => s.SeamCode == seamSel)).OrderBy(s => s.SeamCode).ToList();
            StatsRows.Clear();
            foreach (var s in filtered) StatsRows.Add(s);

            string sampleLabel = useClean ? "浮煤" : "原煤";
            boxTitle.Text = $"箱线对照 ({CoalIndicatorName(indicator)}, {sampleLabel})";
            histTab.Header = $"分布直方 · {CoalIndicatorName(indicator)}";

            BuildKpiStrip(indicator, useClean, seamSel);
            DrawBoxChart(filtered);
            DrawScatterChart(seamSel, useClean);
            DrawHistogram(indicator, useClean, seamSel);
            BuildCorrelation(useClean, seamSel);
            DrawProfile(indicator, useClean, seamSel);
            BuildDepthTabs(indicator, useClean, seamSel);
            BuildConclusions();

            subtitleText.Text = $"  共 {filtered.Sum(s => s.Count)} 个样本";
            statusText.Text = $"已刷新 {StatsRows.Count} 组";
        }
        catch (Exception ex) { _ = CoalMsgBox.ShowAsync(this, "错误", $"统计失败：{ex.Message}"); }
    }

    private Color SeamColor(string seam) { var (r, g, b) = CoalSeamColor(_seams, seam); return Color.FromRgb(r, g, b); }

    // ─────────────────────────────────────────────────────────
    /// <summary>真实箱线：每个煤层一个箱体（min / P25 / 中位 / P75 / max）。</summary>
    private void DrawBoxChart(IReadOnlyList<CoalSeamStat> stats)
    {
        boxChart.Categories = stats.Select(s => s.SeamCode).ToList();
        boxChart.Series = new List<ChartSeries>
        {
            new()
            {
                Name = "分布", Kind = SeriesKind.Box, Color = Color.FromRgb(0x55, 0x8B, 0x2F),
                Boxes = stats.Select(s => (BoxStats?)new BoxStats { Min = s.Min, Q1 = s.P25, Median = s.P50, Q3 = s.P75, Max = s.Max, Mean = s.Mean }).ToList(),
            },
        };
        boxChart.YTitle = "数值";
        boxChart.Legend = LegendPlacement.Bottom;
        boxChart.Refresh();
    }

    /// <summary>散点：X=Ad，Y=可选(Vdaf/St/Qgr/G)，按煤层分系列。半透明看密度，坐标用分位数放大主簇。</summary>
    private void DrawScatterChart(string? seamSel, bool useClean)
    {
        string yCode = (scatterYSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "vdaf";
        Func<CoalSampleRow, double?> gy = yCode switch
        {
            "std" => s => useClean ? s.StdClean : s.StdRaw,
            "qgr" => s => s.QgrD,
            "caking" => s => s.CakingG,
            _ => s => useClean ? s.VdafClean : s.VdafRaw,
        };
        string yName = yCode switch { "std" => "St 全硫 (%)", "qgr" => "Qgr 发热量 (MJ/kg)", "caking" => "G 粘结指数", _ => "Vdaf 挥发分 (%)" };

        var series = new List<ChartSeries>();
        var xs = new List<double>(); var ys = new List<double>();
        foreach (var grp in _all.GroupBy(s => s.SeamCode).OrderBy(g => g.Key))
        {
            if (seamSel != "全部" && grp.Key != seamSel) continue;
            var pts = grp.Select(s => (X: useClean ? s.AdClean : s.AdRaw, Y: gy(s))).Where(t => t.X.HasValue && t.Y.HasValue)
                         .Select(t => (t.X!.Value, t.Y!.Value)).ToList();
            if (pts.Count == 0) continue;
            xs.AddRange(pts.Select(p => p.Item1)); ys.AddRange(pts.Select(p => p.Item2));
            series.Add(new ChartSeries
            {
                Name = $"{grp.Key} ({pts.Count})", Kind = SeriesKind.Scatter, Color = SeamColor(grp.Key), Opacity = 0x88 / 255.0, MarkerSize = 5,
                Points = pts,
            });
        }
        scatterChart.NumericX = true;
        scatterChart.Series = series;
        scatterChart.XTitle = useClean ? "浮煤 Ad (%)" : "原煤 Ad (%)";
        scatterChart.YTitle = yName;
        scatterChart.Legend = LegendPlacement.Right;
        scatterChart.XMin = scatterChart.XMax = scatterChart.YMin = scatterChart.YMax = null;
        if (xs.Count >= 10)
        {
            xs.Sort(); ys.Sort();
            scatterChart.XMin = 0;
            scatterChart.XMax = CoalPctl(xs, 97) * 1.05;
            double y0 = CoalPctl(ys, 2), y1 = CoalPctl(ys, 98), pad = Math.Max(0.5, (y1 - y0) * 0.15);
            scatterChart.YMin = y0 - pad;
            scatterChart.YMax = y1 + pad;
        }
        scatterChart.Refresh();
    }

    private void OnScatterYChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready) DrawScatterChart(SeamSel(), UseClean());
    }

    /// <summary>当前指标的直方图(横轴 bin, 纵轴 count)。</summary>
    private void DrawHistogram(string indicator, bool useClean, string? seamSel)
    {
        var values = GetValues(indicator, useClean, seamSel);
        var (labels, bins) = CoalHistogram(values);
        histChart.Categories = labels.ToList();
        histChart.Series = bins.Length == 0 ? new List<ChartSeries>() : new List<ChartSeries>
        {
            new() { Name = "样品数", Kind = SeriesKind.Bar, Color = Color.FromRgb(0x42, 0x8B, 0xCA), Opacity = 0xCC / 255.0, Values = bins.Select(b => (double?)b).ToList() },
        };
        histChart.YTitle = "样品数";
        histChart.Legend = LegendPlacement.Hidden;
        histChart.Refresh();
    }

    private List<double> GetValues(string indicator, bool useClean, string? seamSel)
    {
        var get = CoalGetter(CoalMapColumn(indicator, useClean));
        return _all.Where(s => seamSel == "全部" || s.SeamCode == seamSel).Select(get).Where(v => v.HasValue).Select(v => v!.Value).ToList();
    }

    // ═════════════ KPI 概览条 / 指标相关性 / 高程剖面 ═════════════
    private void BuildKpiStrip(string indicator, bool useClean, string? seamSel)
    {
        kpiStrip.Children.Clear();
        var vals = GetValues(indicator, useClean, seamSel);
        var kpi = CoalKpiOf(vals);
        if (kpi is null) { kpiStrip.Children.Add(new TextBlock { Text = "当前筛选无数据", Foreground = Muted }); return; }
        string unit = CoalIndicatorUnit(indicator);
        kpiStrip.Children.Add(Tile("样本数", kpi.N.ToString(), ""));
        kpiStrip.Children.Add(Tile("均值", kpi.Mean.ToString("F2"), unit));
        kpiStrip.Children.Add(Tile("标准差 σ", kpi.Std.ToString("F2"), unit));
        kpiStrip.Children.Add(Tile("变异系数", kpi.CvPct.ToString("F1"), "%"));
        kpiStrip.Children.Add(Tile("极差", $"{kpi.Min:F2} – {kpi.Max:F2}", unit));
        var ruleType = CoalRuleForStats(indicator);
        if (ruleType != null) kpiStrip.Children.Add(GradeBar(_rules[ruleType], vals));
    }

    private static Border Tile(string label, string value, string unit)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Muted });
        var vp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        vp.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Primary });
        if (unit.Length > 0)
            vp.Children.Add(new TextBlock { Text = " " + unit, FontSize = 11, Margin = new Thickness(3, 0, 0, 3), VerticalAlignment = VerticalAlignment.Bottom, Foreground = Muted });
        sp.Children.Add(vp);
        return new Border
        {
            Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 5, 14, 5), MinWidth = 78,
            Background = PanelBg, CornerRadius = new CornerRadius(6), BorderBrush = BorderBr, BorderThickness = new Thickness(1), Child = sp,
        };
    }

    /// <summary>GB 分级分布条（分段比例 + 图例），色标取自 coal_grade_rule.color_hex。</summary>
    private static Border GradeBar(IReadOnlyList<CoalGradeRuleFull> rules, List<double> vals)
    {
        var counts = CoalGradeCounts(rules, vals);
        int total = counts.Sum(c => c.count);
        var bar = new Grid { Height = 16 };
        var legend = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (level, hex, c) in counts)
        {
            var (r, g, b) = CoalHexToRgb(hex, (0x90, 0x9A, 0xA8));
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            bar.ColumnDefinitions.Add(new ColumnDefinition(c, GridUnitType.Star));
            var seg = new Border { Background = brush };
            ToolTip.SetTip(seg, $"{level}  {c} 段  {(total > 0 ? c * 100.0 / total : 0):F0}%");
            Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(seg);

            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
            chip.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            chip.Children.Add(new TextBlock { Text = $"{level} {c}", FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(chip);
        }
        var sp = new StackPanel { Width = 300 };
        sp.Children.Add(new TextBlock { Text = "GB 分级分布", FontSize = 11, Foreground = Muted });
        sp.Children.Add(new Border { Margin = new Thickness(0, 3, 0, 0), BorderBrush = BorderBr, BorderThickness = new Thickness(1), Child = bar });
        sp.Children.Add(legend);
        return new Border
        {
            Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5), Child = sp,
            Background = PanelBg, CornerRadius = new CornerRadius(6), BorderBrush = BorderBr, BorderThickness = new Thickness(1),
        };
    }

    /// <summary>指标两两 Pearson 相关矩阵（发散色：正相关蓝、负相关红）。</summary>
    private void BuildCorrelation(bool useClean, string? seamSel)
    {
        var samples = _all.Where(s => seamSel == "全部" || s.SeamCode == seamSel).ToList();
        var m = CoalCorrelationMatrix(samples, useClean);
        var names = CoalCorrNames;
        int n = names.Length;

        corrHost.Children.Clear();
        corrHost.RowDefinitions.Clear();
        corrHost.ColumnDefinitions.Clear();
        for (int k = 0; k <= n; k++)
        {
            corrHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            corrHost.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        void Put(Control el, int r, int c) { Grid.SetRow(el, r); Grid.SetColumn(el, c); corrHost.Children.Add(el); }
        TextBlock Head(string t) => new() { Text = t, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 4, 6, 4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };

        for (int j = 0; j < n; j++) Put(Head(names[j]), 0, j + 1);
        for (int i = 0; i < n; i++)
        {
            Put(Head(names[i]), i + 1, 0);
            for (int j = 0; j < n; j++)
            {
                double? r = m[i, j];
                var cell = new Border
                {
                    Width = 52, Height = 34, Margin = new Thickness(1),
                    Background = r.HasValue ? new SolidColorBrush(CorrColor(r.Value)) : PanelBg,
                    Child = new TextBlock
                    {
                        Text = r.HasValue ? r.Value.ToString("F2") : "—", FontSize = 11.5,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = r.HasValue && Math.Abs(r.Value) >= 0.6 ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Primary,
                    },
                };
                Put(cell, i + 1, j + 1);
            }
        }
    }

    private static Color CorrColor(double r)
    {
        byte a = (byte)(30 + 170 * Math.Min(1, Math.Abs(r)));
        return r >= 0 ? Color.FromArgb(a, 0x18, 0x5F, 0xA5) : Color.FromArgb(a, 0xC6, 0x28, 0x28);
    }

    /// <summary>高程剖面：X=指标值, Y=采样高程 Z，按煤层着色（看质量随深度的垂向变化）。</summary>
    private void DrawProfile(string indicator, bool useClean, string? seamSel)
    {
        var get = CoalGetter(CoalMapColumn(indicator, useClean));
        var series = new List<ChartSeries>();
        foreach (var grp in _all.GroupBy(s => s.SeamCode).OrderBy(g => g.Key))
        {
            if (seamSel != "全部" && grp.Key != seamSel) continue;
            var pts = grp.Select(s => (V: get(s), Z: s.ZSample)).Where(p => p.V.HasValue && p.Z.HasValue).Select(p => (p.V!.Value, p.Z!.Value)).ToList();
            if (pts.Count == 0) continue;
            series.Add(new ChartSeries { Name = $"{grp.Key} ({pts.Count})", Kind = SeriesKind.Scatter, Color = SeamColor(grp.Key), MarkerSize = 8, Points = pts });
        }
        profileChart.NumericX = true;
        profileChart.Series = series;
        profileChart.XTitle = $"{CoalIndicatorName(indicator)} ({(useClean ? "浮" : "原")})";
        profileChart.YTitle = "采样高程 Z (m)";
        profileChart.Legend = LegendPlacement.Right;
        profileChart.Refresh();
    }

    // ═════════════ 深度分析标签：洗选提质 / 商品煤符合性 / 用途适宜性 / 离群质检 ═════════════
    private void BuildDepthTabs(string indicator, bool useClean, string? seamSel)
    {
        washGrid.ItemsSource = CoalAnalytics.WashingBySeam(_analytics);
        utilGrid.ItemsSource = CoalAnalytics.UtilizationBySeam(_analytics);

        var od = CoalDetectOutliers(_all, CoalMapColumn(indicator, useClean), seamSel == "全部" ? null : seamSel);
        string sample = useClean ? "浮煤" : "原煤";
        string name = CoalIndicatorName(indicator);
        outlierSummary.Text = od.N < 5
            ? $"{name}：样本不足（n={od.N}），不做离群检测"
            : od.Outliers.Count == 0
                ? $"{name}（{sample}）：n={od.N}，IQR 栅栏 [{od.Lower:F2}, {od.Upper:F2}]，中位 {od.Median:F2} —— 无离群段"
                : $"{name}（{sample}）：n={od.N}，IQR 栅栏 [{od.Lower:F2}, {od.Upper:F2}]，中位 {od.Median:F2} —— {od.Outliers.Count} 个可疑段（按超出程度排序，建议复核化验）";
        outlierGrid.ItemsSource = od.Outliers;
    }

    /// <summary>分析结论：七类可读结论卡（表征/煤层/变异/相关/洗选/用途/数据）。</summary>
    private void BuildConclusions()
    {
        conclusionHost.Children.Clear();
        foreach (var c in CoalAnalytics.OverallConclusions(_analytics))
            conclusionHost.Children.Add(ConclusionCard(c.Category, c.Text, c.Level));
    }

    private static Border ConclusionCard(string category, string text, CoalAnalytics.Verdict level)
    {
        var accent = level switch
        {
            CoalAnalytics.Verdict.Good => Color.FromRgb(0x2E, 0x7D, 0x32),
            CoalAnalytics.Verdict.Warn => Color.FromRgb(0xC6, 0x28, 0x28),
            _ => Color.FromRgb(0x35, 0x5A, 0x8D),
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = category, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(accent) });
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Foreground = Primary });
        return new Border
        {
            Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 7, 10, 7), Background = PanelBg, CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(3, 0, 0, 0), Child = sp,
        };
    }

    private async void OnComplianceClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            static double D(TextBox t, double def) => double.TryParse(t.Text, out var v) ? v : def;
            bool useClean = UseClean();
            var lim = new ComplianceLimits(useClean,
                cmpAdOn.IsChecked == true, D(cmpAdMax, 16),
                cmpStOn.IsChecked == true, D(cmpStMax, 1.0),
                cmpQOn.IsChecked == true, D(cmpQMin, 24), CalorificKind.Qgr,
                false, 0, 100);
            var res = CoalAnalytics.Evaluate(_analytics, lim);
            complianceGrid.ItemsSource = res.BySeam;
            complianceSummary.Text = res.Evaluated == 0
                ? "无足够数据判定（勾选的限值项是否都有对应化验？Qgr 覆盖约 69%、Qnet 仅 6%，发热量默认用 Qgr）"
                : $"{(useClean ? "浮煤" : "原煤")}：总评定 {res.Evaluated} 段，达标 {res.Pass} 段 = {res.PassPct:F1}%，数据不足 {res.Insufficient} 段已跳过";
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"符合性计算失败：{ex.Message}"); }
    }

    // ─────────────────────────────────────────────────────────
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await _ctx.SaveTextAsync("导出煤质统计 (CSV, UTF-8 BOM)", $"煤质统计_{DateTime.Now:yyyyMMdd_HHmm}.csv", CoalStatsCsv(StatsRows.ToList()));
            if (path == null) return;
            statusText.Text = $"已导出 {StatsRows.Count} 行 → {path}";
            await CoalMsgBox.ShowAsync(this, "完成", $"已导出 {StatsRows.Count} 行。");
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"导出失败：{ex.Message}"); }
    }
}
