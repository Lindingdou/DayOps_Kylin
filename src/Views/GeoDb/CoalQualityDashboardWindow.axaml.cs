using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>煤质数据看板(忠实原 CoalQualityDashboardWindow): 5 KPI 卡 + 综合结论横幅 + 煤类饼 + 煤层对比 + 数据健康度 + 化验段分布/分级构成/洗选提质。</summary>
public partial class CoalQualityDashboardWindow : Window
{
    private readonly GeoDbContext _ctx;
    private List<CoalSeamDefRow> _seams = new();
    private List<CoalGradeRuleFull> _ashRules = new(), _sulfurRules = new();

    private static readonly Color[] CoalTypePalette =
    {
        Color.FromRgb(0x42, 0x8B, 0xCA), Color.FromRgb(0xF5, 0xA6, 0x23), Color.FromRgb(0x6F, 0xC0, 0x84),
        Color.FromRgb(0xC6, 0x52, 0x47), Color.FromRgb(0x9C, 0x6A, 0xCB), Color.FromRgb(0x88, 0x88, 0x88),
    };

    /// <summary>仅供 XAML 设计器/编译器使用。</summary>
    public CoalQualityDashboardWindow() { _ctx = null!; InitializeComponent(); }

    public CoalQualityDashboardWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _seams = CoalSeamDefs(_ctx.Conn);
        _ashRules = CoalGradeRules(_ctx.Conn, "ash");
        _sulfurRules = CoalGradeRules(_ctx.Conn, "sulfur");
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            var samples = CoalLoadSamples(_ctx.Conn);
            if (samples.Count == 0) { subtitleText.Text = "  数据库无煤质数据"; return; }
            var analytics = samples.Select(s => s.ToAnalytics()).ToList();

            UpdateKpis(samples);
            DrawCoalTypePie(samples);
            DrawSeamCompare(samples);
            DrawSeamCount(samples);
            DrawGradeDistribution(samples, _ashRules, s => s.AdRaw, adGradeChart);
            DrawGradeDistribution(samples, _sulfurRules, s => s.StdRaw, sGradeChart);
            UpdateHealthCards(samples, analytics);
            washGrid.ItemsSource = CoalAnalytics.WashingBySeam(analytics);
            BuildConclusionBanner(analytics);

            subtitleText.Text = $"  截止 {DateTime.Now:yyyy-MM-dd}  样品总数 {samples.Count}";
            statusText.Text = "已刷新";
        }
        catch (Exception ex) { _ = CoalMsgBox.ShowAsync(this, "错误", $"加载看板失败：{ex.Message}"); }
    }

    // ─────────────────────────────────────────────────────────
    private void UpdateKpis(IReadOnlyList<CoalSampleRow> samples)
    {
        double? avgAd = Avg(samples.Select(s => s.AdRaw));
        double? avgS = Avg(samples.Select(s => s.StdRaw));
        double? avgV = Avg(samples.Select(s => s.VdafRaw));
        double? avgQ = Avg(samples.Select(s => s.QgrD));    // 弹筒发热量 Qgr,d(覆盖高; Qnet 样本太少不宜当头条)
        int nQ = samples.Count(s => s.QgrD.HasValue);
        double? avgG = Avg(samples.Select(s => s.CakingG));

        kpiAd.Text = avgAd is null ? "—" : $"{avgAd:F2}%";
        kpiAdLevel.Text = avgAd is null ? "—" : CoalFindLevel(_ashRules, avgAd)?.LevelName ?? "—";
        kpiS.Text = avgS is null ? "—" : $"{avgS:F2}%";
        kpiSLevel.Text = avgS is null ? "—" : CoalFindLevel(_sulfurRules, avgS)?.LevelName ?? "—";
        kpiV.Text = avgV is null ? "—" : $"{avgV:F1}%";
        kpiVLevel.Text = avgV is null ? "—" : "(干燥无灰基)";
        kpiQ.Text = avgQ is null ? "—" : $"{avgQ:F1}";
        kpiQLevel.Text = avgQ is null ? "—" : $"MJ/kg · 弹筒干基 n={nQ}";
        kpiG.Text = avgG is null ? "—" : avgG.Value.ToString("F1");
        kpiGSub.Text = avgG is null ? "—" : CoalCakingWord(avgG.Value);

        ColorizeLevel(kpiAdLevel, _ashRules, avgAd);
        ColorizeLevel(kpiSLevel, _sulfurRules, avgS);
        // Qgr,d 不适用 qnet 分级规则，不做分级着色
    }

    private static void ColorizeLevel(TextBlock tb, IReadOnlyList<CoalGradeRuleFull> rules, double? val)
    {
        var rule = CoalFindLevel(rules, val);
        if (rule is null) return;
        var (r, g, b) = CoalHexToRgb(rule.ColorHex, (0x90, 0x9A, 0xA8));
        tb.Foreground = new SolidColorBrush(Color.FromRgb((byte)(r * 0.75), (byte)(g * 0.75), (byte)(b * 0.75)));   // 浅色主题压深 25%
        tb.FontWeight = FontWeight.SemiBold;
    }

    // ─────────────────────────────────────────────────────────
    private void DrawCoalTypePie(IReadOnlyList<CoalSampleRow> samples)
    {
        var counter = CoalTypeCounts(samples);
        pieCoalType.Slices = counter.Select((c, i) => new PieSlice
        {
            Label = $"{c.code} ({c.count})", Value = c.count, Color = CoalTypePalette[i % CoalTypePalette.Length],
        }).ToList();
        pieCoalType.ShowLegend = true;
        pieCoalType.Refresh();
    }

    /// <summary>各煤层 Ad / Vdaf / S / Qnet 对比(柱状): Ad/Vdaf 走左轴(%), 硫分与 Qnet 走右轴(原三轴, 托管图表合并为一条副轴)。</summary>
    private void DrawSeamCompare(IReadOnlyList<CoalSampleRow> samples)
    {
        var seams = samples.Select(s => s.SeamCode).Distinct().OrderBy(s => s).ToList();
        double Mean(IEnumerable<double?> src) { var a = src.Where(v => v.HasValue).Select(v => v!.Value).ToList(); return a.Count == 0 ? 0 : a.Average(); }
        List<double?> Vals(Func<CoalSampleRow, double?> g) => seams.Select(seam => (double?)Mean(samples.Where(s => s.SeamCode == seam).Select(g))).ToList();

        seamCompareChart.Categories = seams;
        seamCompareChart.Series = new List<ChartSeries>
        {
            new() { Name = "Ad(%)", Kind = SeriesKind.Bar, Color = Color.FromRgb(0xC6, 0x52, 0x47), Values = Vals(s => s.AdRaw) },
            new() { Name = "Vdaf(%)", Kind = SeriesKind.Bar, Color = Color.FromRgb(0xF5, 0xA6, 0x23), Values = Vals(s => s.VdafRaw) },
            new() { Name = "S(%)", Kind = SeriesKind.Bar, Color = Color.FromRgb(0x88, 0x88, 0x88), Values = Vals(s => s.StdRaw), SecondaryAxis = true },
            new() { Name = "Qnet(MJ/kg)", Kind = SeriesKind.Bar, Color = Color.FromRgb(0x4A, 0x7C, 0x2E), Values = Vals(s => s.QnetAd), SecondaryAxis = true },
        };
        seamCompareChart.YTitle = "灰分/挥发分 (%)";
        seamCompareChart.Y2Title = "硫分 St,d (%) · Qnet (MJ/kg)";
        seamCompareChart.Legend = LegendPlacement.Right;
        seamCompareChart.Refresh();
    }

    /// <summary>按煤层化验段数量(每煤层一色, 取自 coal_seam_def.color_hex)。</summary>
    private void DrawSeamCount(IReadOnlyList<CoalSampleRow> samples)
    {
        var counts = samples.GroupBy(s => s.SeamCode).Select(g => (seam: g.Key, n: g.Count())).OrderBy(x => x.seam).ToList();
        seamCountChart.Categories = counts.Select(c => c.seam).ToList();
        seamCountChart.Series = new List<ChartSeries>
        {
            new()
            {
                Name = "化验段数", Kind = SeriesKind.Bar, Values = counts.Select(c => (double?)c.n).ToList(), ShowValueLabels = true, ValueFormat = "0",
                PointColors = counts.Select(c => { var (r, g, b) = CoalSeamColor(_seams, c.seam); return (Color?)Color.FromRgb(r, g, b); }).ToList(),
            },
        };
        seamCountChart.YTitle = "化验段数";
        seamCountChart.Legend = LegendPlacement.Hidden;
        seamCountChart.Refresh();
    }

    /// <summary>各煤层的 GB 分级构成(堆叠柱, 段色取自 coal_grade_rule.color_hex)。</summary>
    private void DrawGradeDistribution(IReadOnlyList<CoalSampleRow> samples, IReadOnlyList<CoalGradeRuleFull> rules, Func<CoalSampleRow, double?> get, ChartView chart)
    {
        var seams = samples.Select(s => s.SeamCode).Distinct().OrderBy(s => s).ToList();
        var dist = CoalGradeDistribution(samples, seams, rules, get);
        chart.Categories = seams;
        chart.Series = dist.Select(d =>
        {
            var (r, g, b) = CoalHexToRgb(d.colorHex, (0x80, 0x80, 0x80));
            return new ChartSeries { Name = d.level, Kind = SeriesKind.StackedBar, Color = Color.FromRgb(r, g, b), Values = d.counts.Select(c => (double?)c).ToList() };
        }).ToList();
        chart.YTitle = "化验段数";
        chart.Legend = LegendPlacement.Bottom;
        chart.Refresh();
    }

    // ─────────────────────────────────────────────────────────
    private void UpdateHealthCards(IReadOnlyList<CoalSampleRow> samples, IReadOnlyList<CoalSample> analytics)
    {
        var h = GeoDataQueries.GetCoalDataHealth(_ctx.Conn);
        healthSampleCount.Text = $"样品总数: {h.TotalSamples}";
        int withCoalType = samples.Count(s => !string.IsNullOrEmpty(s.CoalType));
        healthCoalTypeCoverage.Text = $"煤类标注率: {h.CoalTypeCoveragePct:F1}% ({withCoalType}/{h.TotalSamples})";
        healthDensityCheck.Text = $"化验孔覆盖: {h.HolesWithSamples}/{h.TotalHoles} = {h.HoleCoveragePct:F1}%";
        healthSelfConsistency.Text = h.SelfEvaluableCount == 0
            ? "工分自洽: (字段不全)"
            : $"工分自洽: {h.SelfConsistencyPct:F1}% ({h.SelfConsistentCount}/{h.SelfEvaluableCount})";

        // 煤类反推一致率(GB/T 5751 三维区间 vs 原表标注)
        var ranges = GeoDataQueries.GetCoalClassificationRanges(_ctx.Conn);
        var cons = CoalTypeInference.InferConsistency(analytics, ranges);
        healthClassConsistency.Text = cons.Total == 0
            ? "煤类反推: (数据不足)"
            : $"煤类反推一致率: {cons.RatePct:F1}% ({cons.Consistent}/{cons.Total})";
    }

    private static double? Avg(IEnumerable<double?> source)
    {
        var arr = source.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return arr.Count == 0 ? null : arr.Average();
    }

    /// <summary>综合分析结论横幅: 表征/洗选/用途三条最关键结论(完整七类见「统计分析」窗)。</summary>
    private void BuildConclusionBanner(IReadOnlyList<CoalSample> analytics)
    {
        conclusionBanner.Children.Clear();
        var all = CoalAnalytics.OverallConclusions(analytics);
        foreach (var cat in new[] { "煤质表征", "洗选提质", "用途建议" })
        {
            var c = all.FirstOrDefault(x => x.Category == cat);
            if (c == null) continue;
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(new TextBlock { Text = $"● {c.Category}：", FontWeight = FontWeight.Bold, FontSize = 12.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top });
            row.Children.Add(new TextBlock { Text = c.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MaxWidth = 1050 });
            conclusionBanner.Children.Add(row);
        }
    }
}
