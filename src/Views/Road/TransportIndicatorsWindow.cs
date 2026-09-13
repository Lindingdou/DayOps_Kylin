using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Controls.Charts;

namespace PitMine3D.Kylin.Views.Road;

/// <summary>
/// W6「运输指标报表」窗（非模态）：四页 Tab（OD矩阵 / 运距指标 / 运能与瓶颈 / 分期趋势）。
/// 分期趋势用 ChartView 双 Y 轴折线（原 OxyPlot）；底部导出 CSV/TXT + 刷新。
/// 凡需吨量/真网络流的指标显式标「待采矿模型/求解器」，不假装算出。忠实原 TransportIndicatorsWindow。
/// </summary>
internal sealed class TransportIndicatorsWindow : Window
{
    private readonly Func<TransportIndicators?> _refresh;
    private TransportIndicators _ind;
    private enum OdMode { Dist, Equiv, Time }
    private OdMode _odMode = OdMode.Dist;
    private readonly RadioButton _rbDist, _rbEquiv, _rbTime;
    private readonly TextBox _odText;
    private readonly TextBlock _avgEquiv, _maxEquiv, _weightedEquiv, _tphWeightedEquiv, _distFooter, _capacityText, _plotHint, _summary, _caliperBanner, _fallbackWarn;
    private readonly DataGrid _bottleneckList;
    private readonly ChartView _plot;

    public TransportIndicatorsWindow(TransportIndicators ind, Func<TransportIndicators?> refresh)
    {
        _ind = ind ?? throw new ArgumentNullException(nameof(ind));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        Title = "运输指标报表";
        RoadUi.Place(this, 900, 640);

        _summary = new TextBlock { FontFamily = RoadUi.Mono, FontSize = 13, Margin = new Thickness(12, 10, 12, 2), TextWrapping = TextWrapping.Wrap };
        RoadUi.Theme(_summary, TextBlock.ForegroundProperty, "Theme.Text.Body");
        _caliperBanner = new TextBlock { FontFamily = RoadUi.Mono, FontSize = 11, Margin = new Thickness(12, 0, 12, 2), TextWrapping = TextWrapping.Wrap };
        RoadUi.Theme(_caliperBanner, TextBlock.ForegroundProperty, "Theme.Text.Body");
        _fallbackWarn = RoadUi.Warn(""); _fallbackWarn.Margin = new Thickness(12, 0, 12, 6); _fallbackWarn.IsVisible = false;

        // ── ①OD 矩阵 ──
        _rbDist = MakeRadio("里程", true, () => SwitchOdMode(OdMode.Dist));
        _rbEquiv = MakeRadio("等效运距", false, () => SwitchOdMode(OdMode.Equiv));
        _rbTime = MakeRadio("时间", false, () => SwitchOdMode(OdMode.Time));
        var odTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 4) };
        var odLbl = RoadUi.Lbl("口径："); odLbl.VerticalAlignment = VerticalAlignment.Center; odLbl.Margin = new Thickness(0, 0, 6, 0);
        odTools.Children.Add(odLbl); odTools.Children.Add(_rbDist); odTools.Children.Add(_rbEquiv); odTools.Children.Add(_rbTime);
        _odText = RoadUi.Mono2("", 12); _odText.Margin = new Thickness(8, 0, 8, 8);
        var odGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var odScroll = new ScrollViewer { Content = _odText, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(odTools, 0); Grid.SetRow(odScroll, 1);
        odGrid.Children.Add(odTools); odGrid.Children.Add(odScroll);

        // ── ②运距指标 ──
        _avgEquiv = MakeMetricLine(); _maxEquiv = MakeMetricLine(); _weightedEquiv = MakeMetricLine(); _tphWeightedEquiv = MakeMetricLine();
        _distFooter = RoadUi.Hint("", 11); _distFooter.Margin = new Thickness(0, 16, 0, 0);
        var distPanel = new StackPanel { Margin = new Thickness(16) };
        distPanel.Children.Add(RoadUi.Text("运距指标（基于 OD 等效运距的可达对）", 13, bold: true));
        distPanel.Children[0].Margin = new Thickness(0, 0, 0, 12);
        distPanel.Children.Add(MakeLabeled("平均等效运距：", _avgEquiv));
        distPanel.Children.Add(MakeLabeled("最大等效运距：", _maxEquiv));
        distPanel.Children.Add(MakeLabeled("按吞吐加权：", _tphWeightedEquiv));
        distPanel.Children.Add(MakeLabeled("运量加权平均：", _weightedEquiv));
        distPanel.Children.Add(_distFooter);

        // ── ③运能与瓶颈 ──
        _capacityText = RoadUi.Text("", 13); _capacityText.Margin = new Thickness(16, 16, 16, 8);
        _bottleneckList = RoadUi.Table(new (string, string, double)[]
        {
            ("边Id", nameof(BottleneckRow.EdgeId), 100), ("里程 m", nameof(BottleneckRow.LengthM), 76), ("平均坡 %", nameof(BottleneckRow.GradePct), 76),
            ("最陡段 %", nameof(BottleneckRow.MaxSegGradePct), 76), ("车道", nameof(BottleneckRow.LaneCount), 56), ("经过次数", nameof(BottleneckRow.Betweenness), 78),
            ("状态", nameof(BottleneckRow.Status), 66), ("瓶颈分", nameof(BottleneckRow.Score), 70), ("主因", nameof(BottleneckRow.Reason), 120),
        }, multi: false);
        _bottleneckList.FontFamily = RoadUi.Mono; _bottleneckList.FontSize = 12; _bottleneckList.Margin = new Thickness(12, 0, 12, 12);
        var capGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(_capacityText, 0); Grid.SetRow(_bottleneckList, 1);
        capGrid.Children.Add(_capacityText); capGrid.Children.Add(_bottleneckList);

        // ── ④分期趋势 ──
        _plot = new ChartView { Margin = new Thickness(8), XTitle = "开采期", YTitle = "理论运能 (t/h)" };
        _plotHint = RoadUi.Hint("需 ≥2 期快照（用『时段快照』累积）。", 13);
        _plotHint.HorizontalAlignment = HorizontalAlignment.Center; _plotHint.VerticalAlignment = VerticalAlignment.Center; _plotHint.IsVisible = false;
        var trendGrid = new Grid();
        trendGrid.Children.Add(_plot); trendGrid.Children.Add(_plotHint);

        var tabs = new TabControl { Margin = new Thickness(8, 4, 8, 4) };
        tabs.Items.Add(new TabItem { Header = "OD矩阵", Content = odGrid });
        tabs.Items.Add(new TabItem { Header = "运距指标", Content = distPanel });
        tabs.Items.Add(new TabItem { Header = "运能与瓶颈", Content = capGrid });
        tabs.Items.Add(new TabItem { Header = "分期趋势", Content = trendGrid });

        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 12, 10) };
        foot.Children.Add(RoadUi.Btn("导出CSV", () => _ = ExportCsv(), 96));
        foot.Children.Add(RoadUi.Btn("导出报表TXT", () => _ = ExportTxt(), 110));
        foot.Children.Add(RoadUi.Btn("刷新", () => _ = Reload(), 96));
        foot.Children.Add(RoadUi.Btn("关闭", Close, 96));

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Grid.SetRow(_summary, 0); Grid.SetRow(_caliperBanner, 1); Grid.SetRow(_fallbackWarn, 2); Grid.SetRow(tabs, 3); Grid.SetRow(foot, 4);
        grid.Children.Add(_summary); grid.Children.Add(_caliperBanner); grid.Children.Add(_fallbackWarn); grid.Children.Add(tabs); grid.Children.Add(foot);
        Content = grid;
        Render();
    }

    private void Render()
    {
        string srcSink = $"{_ind.Sources.Count} 源 / {_ind.Sinks.Count} 汇";
        string conn = _ind.IsFullyConnected ? "全连通 ✓" : $"{_ind.ComponentCount} 个连通分量 ✗";
        _summary.Text = $"概要：{_ind.NodeCount} 节点（{srcSink}） · {_ind.EdgeCount} 边 · 总里程 {_ind.TotalKm:F2} km · {conn}";
        _caliperBanner.Text = _ind.CaliperSummary + (_ind.CaliperNotes.Count > 0 ? "\n  · " + string.Join("\n  · ", _ind.CaliperNotes) : "");
        var warn = new List<string>();
        if (_ind.UsedAllNodesFallback) warn.Add("⚠ 未设装卸点，按全节点估算（非真实源汇，仅供体检）");
        if (_ind.TruncationNote is { } tn) warn.Add("⚠ " + tn);
        _fallbackWarn.Text = string.Join("\n", warn);
        _fallbackWarn.IsVisible = warn.Count > 0;
        RenderOdMatrix();
        _avgEquiv.Text = $"{_ind.AvgEquivM:F0} m";
        _maxEquiv.Text = $"{_ind.MaxEquivM:F0} m";
        _tphWeightedEquiv.Text = _ind.ThroughputWeightedAvgEquivM is { } tw ? $"{tw:F0} m" : "—（源吞吐能力全为 0，未录）";
        _weightedEquiv.Text = _ind.WeightedAvgEquivM is { } w ? $"{w:F0} m" : "—（待采矿模型：吨量权未接入）";
        _distFooter.Text =
            $"口径说明：平均等效运距 = 沿「{_ind.ModeText}」路测得的等效运距，不是「最小等效运距」；等效运距 = 实距按纵坡折算（重车上坡加权）。\n"
            + "「按吞吐加权」的权是源节点的 ThroughputTph（真数据）；「运量加权」要的是各源的吨量，来自采矿模型，未接入前一律显示 —，不用 0 顶替。";
        _capacityText.Text =
            $"理论运能上界：{_ind.TheoreticalCapacityTph:F0} t/h  =  min( Σ源 {_ind.SourceThroughputSumTph:F0} , Σ汇 {_ind.SinkCapacitySumTph:F0} ) t/h\n"
            + "（理论上界·非网络流解，真运能待求解器 IRoadLayoutSolver）";
        _bottleneckList.ItemsSource = _ind.Bottlenecks.Select(BottleneckRow.From).ToList();
        RenderTrend();
    }

    private void RenderOdMatrix()
    {
        var od = _ind.Od;
        if (od is null || od.Sources.Count == 0 || od.Sinks.Count == 0) { _odText.Text = "无源汇可达对（路网无装卸点且无可达节点对）。"; return; }
        double[,] m = _odMode switch { OdMode.Equiv => od.Equiv, OdMode.Time => od.Time, _ => od.Dist };
        string unit = _odMode switch { OdMode.Time => "时间 min", OdMode.Equiv => "等效运距 m", _ => "里程 m" };
        string fmt = _odMode == OdMode.Time ? "F1" : "F0";
        var sb = new StringBuilder();
        sb.AppendLine($"OD 矩阵（{unit}）  ·=自身 · ∞=不可达").AppendLine();
        const int cw = 9;
        sb.Append("源\\汇".PadRight(cw));
        foreach (var id in od.Sinks) sb.Append(id.PadLeft(cw));
        sb.AppendLine();
        for (int i = 0; i < od.Sources.Count; i++)
        {
            sb.Append(od.Sources[i].PadRight(cw));
            for (int j = 0; j < od.Sinks.Count; j++)
            {
                double v = m[i, j];
                string cell = od.Sources[i] == od.Sinks[j] ? "·" : double.IsInfinity(v) ? "∞" : v.ToString(fmt);
                sb.Append(cell.PadLeft(cw));
            }
            sb.AppendLine();
        }
        _odText.Text = sb.ToString();
    }

    private void RenderTrend()
    {
        var series = _ind.PerPeriodSeries;
        if (series.Count < 2) { _plot.IsVisible = false; _plotHint.IsVisible = true; return; }
        _plot.IsVisible = true; _plotHint.IsVisible = false;
        _plot.Categories = series.Select(p => p.Period.ToString()).ToList();
        var cap = new ChartSeries { Name = "理论运能 (t/h)", Kind = SeriesKind.Line, Color = Color.Parse("#1976D2") };
        var haul = new ChartSeries { Name = "平均运距 (m)", Kind = SeriesKind.Line, Color = Color.Parse("#E65100"), SecondaryAxis = true };
        foreach (var p in series) { cap.Values.Add(p.TheoreticalCapacityTph); haul.Values.Add(p.AvgEquivM); }
        _plot.Series = new List<ChartSeries> { cap, haul };
        _plot.InvalidateVisual();
    }

    private void SwitchOdMode(OdMode mode) { if (_odMode == mode) return; _odMode = mode; RenderOdMatrix(); }

    private async Task Reload()
    {
        try
        {
            var fresh = _refresh();
            if (fresh is null) return;
            _ind = fresh;
            _odMode = OdMode.Dist;
            _rbDist.IsChecked = true;
            Render();
        }
        catch (Exception ex) { await RoadUi.Info(this, "运输指标报表", "刷新失败：" + ex.Message); }
    }

    private async Task<string?> PickSave(string title, string name, string ext, string filter)
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = name, DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(filter) { Patterns = new[] { "*." + ext } } },
        });
        return f?.Path.LocalPath;
    }

    private async Task ExportCsv()
    {
        var path = await PickSave("导出运输指标 CSV", "运输指标报表.csv", "csv", "CSV 文件");
        if (path == null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"# {_ind.CaliperSummary}");
        foreach (var n in _ind.CaliperNotes) sb.AppendLine($"# · {n}");
        sb.AppendLine($"# 平均等效运距 = 沿「{_ind.ModeText}」路测得的等效运距，不是最小等效运距");
        if (_ind.TruncationNote is { } tn0) sb.AppendLine($"# ⚠ {tn0}");
        if (_ind.UsedAllNodesFallback) sb.AppendLine("# ⚠ 未设装卸点，按全节点估算（非真实源汇）");
        sb.AppendLine();
        sb.AppendLine("# 概要");
        sb.AppendLine("节点数,边数,总里程km,全连通,连通分量,源数,汇数,降级全节点");
        sb.AppendLine($"{_ind.NodeCount},{_ind.EdgeCount},{_ind.TotalKm:F2},{(_ind.IsFullyConnected ? "是" : "否")},{_ind.ComponentCount},{_ind.Sources.Count},{_ind.Sinks.Count},{(_ind.UsedAllNodesFallback ? "是" : "否")}");
        sb.AppendLine();
        var od = _ind.Od;
        if (od is not null && od.Sources.Count > 0 && od.Sinks.Count > 0)
        {
            string unit = _odMode switch { OdMode.Time => "时间min", OdMode.Equiv => "等效运距m", _ => "里程m" };
            double[,] m = _odMode switch { OdMode.Equiv => od.Equiv, OdMode.Time => od.Time, _ => od.Dist };
            string fmt = _odMode == OdMode.Time ? "F1" : "F0";
            sb.AppendLine($"# OD 矩阵（{unit}）·=自身 ∞=不可达");
            sb.Append("源\\汇");
            foreach (var id in od.Sinks) sb.Append(',').Append(id);
            sb.AppendLine();
            for (int i = 0; i < od.Sources.Count; i++)
            {
                sb.Append(od.Sources[i]);
                for (int j = 0; j < od.Sinks.Count; j++)
                {
                    double v = m[i, j];
                    sb.Append(',').Append(od.Sources[i] == od.Sinks[j] ? "·" : double.IsInfinity(v) ? "∞" : v.ToString(fmt));
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        sb.AppendLine("# 指标");
        sb.AppendLine("项,值");
        sb.AppendLine($"平均等效运距m,{_ind.AvgEquivM:F0}");
        sb.AppendLine($"最大等效运距m,{_ind.MaxEquivM:F0}");
        sb.AppendLine($"按吞吐加权等效运距m,{(_ind.ThroughputWeightedAvgEquivM is { } tw ? tw.ToString("F0") : "—（源吞吐全为0）")}");
        sb.AppendLine($"运量加权等效运距m,{(_ind.WeightedAvgEquivM is { } w ? w.ToString("F0") : "—（待采矿模型）")}");
        sb.AppendLine($"Σ源吞吐t/h,{_ind.SourceThroughputSumTph:F0}");
        sb.AppendLine($"Σ汇接收t/h,{_ind.SinkCapacitySumTph:F0}");
        sb.AppendLine($"理论运能上界t/h,{_ind.TheoreticalCapacityTph:F0}（非网络流·待求解器）");
        sb.AppendLine();
        sb.AppendLine("# 瓶颈段");
        sb.AppendLine("边Id,里程m,平均坡%,最陡段%,车道,经过次数,状态,瓶颈分,主因");
        foreach (var b in _ind.Bottlenecks)
            sb.AppendLine($"{b.EdgeId},{b.LengthM:F0},{b.GradePct:F1},{b.MaxAbsSegGradePct:F1},{b.LaneCount},{b.Betweenness},{b.Status},{b.Score:F2},{b.Reason}");
        sb.AppendLine();
        sb.AppendLine("# 分期序列");
        sb.AppendLine("期,总里程km,边数,平均等效运距m,理论运能t/h");
        foreach (var p in _ind.PerPeriodSeries) sb.AppendLine($"{p.Period},{p.TotalKm:F2},{p.EdgeCount},{p.AvgEquivM:F0},{p.TheoreticalCapacityTph:F0}");
        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); await RoadUi.Info(this, "运输指标报表", "已导出：" + path); }
        catch (Exception ex) { await RoadUi.Info(this, "运输指标报表", "导出失败：" + ex.Message); }
    }

    private async Task ExportTxt()
    {
        var path = await PickSave("导出运输指标报表 TXT", "运输指标报表.txt", "txt", "文本文件");
        if (path == null) return;
        var sb = new StringBuilder();
        sb.AppendLine("运输指标报表").AppendLine(new string('=', 40)).AppendLine();
        sb.AppendLine(_summary.Text);
        sb.AppendLine(_caliperBanner.Text);
        if (_fallbackWarn.IsVisible) sb.AppendLine(_fallbackWarn.Text);
        sb.AppendLine();
        sb.AppendLine("【OD 矩阵】").AppendLine(_odText.Text).AppendLine();
        sb.AppendLine("【运距指标】");
        sb.AppendLine($"  平均等效运距：{_ind.AvgEquivM:F0} m");
        sb.AppendLine($"  最大等效运距：{_ind.MaxEquivM:F0} m");
        sb.AppendLine($"  按吞吐加权：  {(_ind.ThroughputWeightedAvgEquivM is { } tw ? tw.ToString("F0") + " m" : "—（源吞吐能力全为 0）")}");
        sb.AppendLine($"  运量加权平均：{(_ind.WeightedAvgEquivM is { } w ? w.ToString("F0") + " m" : "—（待采矿模型）")}");
        sb.AppendLine("  " + (_distFooter.Text ?? "").Replace("\n", "\n  "));
        sb.AppendLine();
        sb.AppendLine("【运能与瓶颈】");
        sb.AppendLine("  " + (_capacityText.Text ?? "").Replace("\n", "\n  ")).AppendLine();
        sb.AppendLine("  瓶颈段（边Id / 里程m / 平均坡% / 最陡段% / 车道 / 经过次数 / 状态 / 瓶颈分 / 主因）：");
        foreach (var b in _ind.Bottlenecks)
            sb.AppendLine($"    {b.EdgeId}  {b.LengthM:F0}  {b.GradePct:F1}  {b.MaxAbsSegGradePct:F1}  {b.LaneCount}  {b.Betweenness}  {b.Status}  {b.Score:F2}  {b.Reason}");
        sb.AppendLine();
        sb.AppendLine("【分期序列】（期 / 总里程km / 边数 / 平均等效运距m / 理论运能t/h）：");
        foreach (var p in _ind.PerPeriodSeries) sb.AppendLine($"    {p.Period}  {p.TotalKm:F2}  {p.EdgeCount}  {p.AvgEquivM:F0}  {p.TheoreticalCapacityTph:F0}");
        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); await RoadUi.Info(this, "运输指标报表", "已导出：" + path); }
        catch (Exception ex) { await RoadUi.Info(this, "运输指标报表", "导出失败：" + ex.Message); }
    }

    public sealed class BottleneckRow
    {
        public string EdgeId { get; init; } = "";
        public string LengthM { get; init; } = "";
        public string GradePct { get; init; } = "";
        public string MaxSegGradePct { get; init; } = "";
        public int LaneCount { get; init; }
        public int Betweenness { get; init; }
        public string Status { get; init; } = "";
        public string Score { get; init; } = "";
        public string Reason { get; init; } = "";
        public static BottleneckRow From(BottleneckEdge b) => new()
        {
            EdgeId = b.EdgeId, LengthM = b.LengthM.ToString("F0"), GradePct = b.GradePct.ToString("F1"), MaxSegGradePct = b.MaxAbsSegGradePct.ToString("F1"),
            LaneCount = b.LaneCount, Betweenness = b.Betweenness, Status = b.Status, Score = b.Score.ToString("F2"), Reason = b.Reason,
        };
    }

    private static RadioButton MakeRadio(string text, bool isChecked, Action onChecked)
    {
        var rb = new RadioButton { Content = text, IsChecked = isChecked, GroupName = "odMode", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) onChecked(); };
        return rb;
    }

    private static TextBlock MakeMetricLine()
    {
        var t = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        RoadUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Body");
        return t;
    }

    private static Control MakeLabeled(string label, TextBlock value)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var l = RoadUi.Text(label, 14, false); l.Width = 130; l.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(l); sp.Children.Add(value);
        return sp;
    }
}
