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

namespace PitMine3D.Kylin.Views.Road;

/// <summary>
/// 「等效运距（一源多汇）」窗（非模态）。回答「这个采区排到哪个卸点最划算」：1 源 × N 汇，每个汇算一组完整往返对，
/// 按往返等效运距升序排。三条口径：不可达行标红、度量列一律 —；回程不可达 ≠ 整对可用；未设单价成本列 —（未设单价）。
/// 忠实原 RoadLib.Views.EquivHaulWindow。
/// </summary>
internal sealed class EquivHaulWindow : Window
{
    private readonly Action<Action<string>> _pickSource, _pickSink;
    private readonly Func<string, IReadOnlyList<string>, HaulCaliper, IReadOnlyList<HaulPairResult>> _solve;
    private readonly Action<HaulPairResult> _highlight;
    private readonly Action _clearHighlight;
    private readonly Action<string, string, HaulCaliper> _openInPointToPoint;
    private readonly Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>> _saveCandidates;
    private readonly StackPanel _sinkPanel;
    private readonly HashSet<string> _candidateIds = new(StringComparer.Ordinal);
    public const string CandidateTag = "（候选·未入账）";
    private HaulCaliper _caliper;
    private string? _srcId;
    private IReadOnlyList<HaulPairResult> _results = Array.Empty<HaulPairResult>();
    private readonly ComboBox _cbSource, _cbMode;
    private readonly TextBlock _srcText, _caliperText, _sinkTitle, _footer;
    private readonly Expander _caliperBox;
    private readonly List<CheckBox> _sinkChecks = new();
    private readonly DataGrid _grid;
    private static readonly (WeightMode Mode, string Text)[] Modes = { (WeightMode.Time, "时间最短（默认）"), (WeightMode.Distance, "里程最短"), (WeightMode.Cost, "成本最省") };

    public EquivHaulWindow(IReadOnlyList<string> sourceIds, IReadOnlyList<string> sinkIds, HaulCaliper caliper,
        Action<Action<string>> pickSource, Action<Action<string>> pickSink,
        Func<string, IReadOnlyList<string>, HaulCaliper, IReadOnlyList<HaulPairResult>> solve,
        Action<HaulPairResult> highlight, Action clearHighlight,
        Action<string, string, HaulCaliper> openInPointToPoint,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>> saveCandidates)
    {
        _caliper = caliper; _pickSource = pickSource; _pickSink = pickSink; _solve = solve; _highlight = highlight;
        _clearHighlight = clearHighlight; _openInPointToPoint = openInPointToPoint; _saveCandidates = saveCandidates;
        Title = "等效运距（一源多汇）";
        RoadUi.Place(this, 1320, 680);

        _cbMode = RoadUi.Combo(Modes.Select(m => m.Text));
        _cbMode.SelectedIndex = 0;
        _cbMode.SelectionChanged += (_, _) => ApplyMode();
        _caliperText = new TextBlock { Text = caliper.SummaryBlock().TrimEnd(), TextWrapping = TextWrapping.Wrap, FontFamily = RoadUi.Mono, FontSize = 11 };
        if (caliper.FromSharedSettings) RoadUi.Theme(_caliperText, TextBlock.ForegroundProperty, "Theme.Text.Body");
        else _caliperText.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00));
        var calPanel = new StackPanel();
        calPanel.Children.Add(RoadUi.Lbl("择路口径（本次生效，不回写共享约束）："));
        calPanel.Children.Add(_cbMode);
        calPanel.Children.Add(_caliperText);
        var calHint = RoadUi.Hint("修改全局口径请到「剥采工程辅助设计 → 运输系统 → 约束条件设置」。", 11); calHint.Margin = new Thickness(0, 4, 0, 0);
        calPanel.Children.Add(calHint);
        _caliperBox = new Expander { Header = "口径 ▸ " + caliper.ShortLine(), IsExpanded = false, Content = calPanel, FontSize = 12, Margin = new Thickness(12, 8, 12, 6) };

        _cbSource = RoadUi.Combo(sourceIds); _cbSource.IsEnabled = sourceIds.Count > 0; _cbSource.Margin = new Thickness(0);
        if (sourceIds.Count > 0) { _cbSource.SelectedIndex = 0; _srcId = sourceIds[0]; }
        _cbSource.SelectionChanged += (_, _) => { if (_cbSource.SelectedItem is string s) { _srcId = s; UpdateSourceText(); } };
        var btnPick = RoadUi.Btn("视口取点…", () => _pickSource(id => { _srcId = id; SelectSource(id); }), 96);
        btnPick.Margin = new Thickness(8, 0, 0, 0);
        _srcText = RoadUi.Text("", 12, false); _srcText.Margin = new Thickness(12, 0, 0, 0); _srcText.VerticalAlignment = VerticalAlignment.Center;
        UpdateSourceText();
        var srcRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 6) };
        var srcLbl = RoadUi.Text("源（采剥点）：", 13, false); srcLbl.VerticalAlignment = VerticalAlignment.Center; srcLbl.Margin = new Thickness(0, 0, 6, 0);
        srcRow.Children.Add(srcLbl); srcRow.Children.Add(_cbSource); srcRow.Children.Add(btnPick); srcRow.Children.Add(_srcText);

        _sinkPanel = new StackPanel();
        _sinkTitle = RoadUi.Text("", 13, false); _sinkTitle.VerticalAlignment = VerticalAlignment.Center; _sinkTitle.Margin = new Thickness(0, 0, 8, 0);
        foreach (var k in sinkIds) AddSinkRow(k, candidate: false);
        var sinkScroll = new ScrollViewer { Content = _sinkPanel, Height = 96, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        var sinkBorder = new Border { Child = sinkScroll, BorderThickness = new Thickness(1) };
        RoadUi.Theme(sinkBorder, Border.BorderBrushProperty, "Theme.Panel.Border");
        var sinkTools = new StackPanel { Orientation = Orientation.Horizontal };
        sinkTools.Children.Add(_sinkTitle);
        sinkTools.Children.Add(RoadUi.Small("全选", () => SetAllSinks(true)));
        sinkTools.Children.Add(RoadUi.Small("反选", ToggleSinks));
        sinkTools.Children.Add(RoadUi.Small("仅保留可达", () => _ = KeepReachableOnly()));
        sinkTools.Children.Add(RoadUi.Small("视口加候选卸点…", () => _pickSink(id => AddSinkRow(id, candidate: true))));
        sinkTools.Children.Add(RoadUi.Small("把候选存进台账…", () => _ = SaveCandidates()));
        UpdateSinkTitle();
        var sinkBlock = new StackPanel { Margin = new Thickness(12, 0, 12, 6) };
        sinkBlock.Children.Add(sinkTools); sinkBlock.Children.Add(sinkBorder);

        _grid = RoadUi.Table(new (string, string, double)[]
        {
            ("汇", nameof(Row.Sink), 150), ("来源", nameof(Row.Source), 86), ("状态", nameof(Row.Status), 110),
            ("去程实距 m", nameof(Row.OutLen), 84), ("去程等效 m", nameof(Row.OutEquiv), 84), ("去程 min", nameof(Row.OutTime), 70),
            ("回程实距 m", nameof(Row.RetLen), 84), ("回程等效 m", nameof(Row.RetEquiv), 84), ("回程 min", nameof(Row.RetTime), 70),
            ("往返等效 m", nameof(Row.RoundTripEquiv), 90), ("循环 min", nameof(Row.Cycle), 70), ("单趟成本 元", nameof(Row.Cost), 100), ("不可达主因", nameof(Row.Cause), 360),
        }, multi: false);
        _grid.FontFamily = RoadUi.Mono; _grid.FontSize = 12; _grid.Margin = new Thickness(12, 4, 12, 4);
        // 不可达行标红：一眼与可用行分开，避免把 — 当成"很小"（DataGrid 逐行上色走 LoadingRow）。
        _grid.LoadingRow += (_, e) => { if (e.Row.DataContext is Row r && !r.Feasible) e.Row.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)); else e.Row.ClearValue(ForegroundProperty); };
        _grid.SelectionChanged += (_, _) => { if (_grid.SelectedItem is Row r && r.Pair is not null) _highlight(r.Pair); };
        _footer = RoadUi.Hint("尚未计算。", 11); _footer.Margin = new Thickness(12, 4, 12, 0);

        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 12, 10) };
        foot.Children.Add(RoadUi.Btn("计算", () => _ = Run(), 96, primary: true));
        foot.Children.Add(RoadUi.Btn("导出CSV", () => _ = ExportCsv(), 96));
        foot.Children.Add(RoadUi.Btn("在点对点寻径里打开", () => _ = OpenSelectedInPointToPoint(), 150));
        foot.Children.Add(RoadUi.Btn("清除高亮", () => _clearHighlight(), 96));
        foot.Children.Add(RoadUi.Btn("关闭", Close, 96));

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        Grid.SetRow(_caliperBox, 0); Grid.SetRow(srcRow, 1); Grid.SetRow(sinkBlock, 2); Grid.SetRow(_grid, 3); Grid.SetRow(_footer, 4); Grid.SetRow(foot, 5);
        root.Children.Add(_caliperBox); root.Children.Add(srcRow); root.Children.Add(sinkBlock); root.Children.Add(_grid); root.Children.Add(_footer); root.Children.Add(foot);
        Content = root;
    }

    private void UpdateSourceText()
        => _srcText.Text = _srcId is null ? "（未选源：路网里没有采剥点，请用「视口取点…」指一个位置）" : $"当前源：{_srcId}";

    private void ApplyMode()
    {
        int i = _cbMode.SelectedIndex;
        var mode = i >= 0 && i < Modes.Length ? Modes[i].Mode : WeightMode.Time;
        if (mode == _caliper.Mode) return;
        _caliper = _caliper.WithMode(mode);
        _caliperBox.Header = "口径 ▸ " + _caliper.ShortLine();
        _caliperText.Text = _caliper.SummaryBlock().TrimEnd();
        _results = Array.Empty<HaulPairResult>();
        _grid.ItemsSource = null;
        _footer.Text = "口径已改，请重新计算。";
    }

    /// <summary>预选一个源（供「点对点寻径」结果窗带着源跳过来）。不在下拉里的（临时取点）也认。</summary>
    public void SelectSource(string id)
    {
        _srcId = id;
        if (!_cbSource.Items.Contains(id)) _cbSource.Items.Insert(0, id);
        _cbSource.SelectedItem = id;
        UpdateSourceText();
    }

    private void AddSinkRow(string id, bool candidate)
    {
        var exist = _sinkChecks.FirstOrDefault(c => string.Equals((string)c.Tag!, id, StringComparison.Ordinal));
        if (exist is not null) { exist.IsChecked = true; return; }
        if (candidate) _candidateIds.Add(id);
        var cb = new CheckBox { Tag = id, Content = candidate ? id + CandidateTag : id, IsChecked = true, Margin = new Thickness(0, 2, 12, 2), FontSize = 12 };
        if (candidate) cb.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00));
        _sinkChecks.Add(cb);
        _sinkPanel.Children.Add(cb);
        UpdateSinkTitle();
    }

    private void UpdateSinkTitle()
    {
        int cand = _candidateIds.Count;
        _sinkTitle.Text = _sinkChecks.Count == 0
            ? "汇（卸载点）：一个都没有 —— 用「视口加候选卸点…」在图上点几个试算，或去「破碎站位置设置」/「去向台账」录台账"
            : $"汇（卸载点，共 {_sinkChecks.Count} 个" + (cand > 0 ? $"，其中候选 {cand} 个未入账" : "") + "）：";
    }

    private async Task SaveCandidates()
    {
        var picked = _sinkChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).Where(_candidateIds.Contains).ToList();
        if (picked.Count == 0)
        {
            await RoadUi.Info(this, Title, _candidateIds.Count == 0 ? "还没有候选卸点。先用「视口加候选卸点…」在图上点几个位置。" : "勾选的里面没有候选（其余都已在台账里）。");
            return;
        }
        var saved = await _saveCandidates(picked);
        if (saved.Count == 0) return;
        foreach (var id in saved)
        {
            _candidateIds.Remove(id);
            var cb = _sinkChecks.FirstOrDefault(c => string.Equals((string)c.Tag!, id, StringComparison.Ordinal));
            if (cb is null) continue;
            cb.Content = id;
            cb.ClearValue(ForegroundProperty);
        }
        UpdateSinkTitle();
        _footer.Text = $"已把 {saved.Count} 个候选存进卸载点台账并接进路网。它们现在是真卸载点：运输指标报表、运量驱动布线都会统计到。"
                       + (_results.Count > 0 ? "（表里的数还是上一轮的，重点「计算」刷新。）" : "");
    }

    private async Task OpenSelectedInPointToPoint()
    {
        if (_grid.SelectedItem is not Row r || r.Pair is null) { await RoadUi.Info(this, Title, "先在表里选中一行（一个卸载点），再点这里看那一趟的逐段明细。"); return; }
        _openInPointToPoint(r.Pair.SrcId, r.Pair.DstId, _caliper);
    }

    private void SetAllSinks(bool on) { foreach (var cb in _sinkChecks) cb.IsChecked = on; }
    private void ToggleSinks() { foreach (var cb in _sinkChecks) cb.IsChecked = cb.IsChecked != true; }

    private async Task KeepReachableOnly()
    {
        if (_results.Count == 0) { await RoadUi.Info(this, Title, "先点「计算」，才知道哪些汇可达。"); return; }
        var ok = _results.Where(r => r.Feasible).Select(r => r.DstId).ToHashSet(StringComparer.Ordinal);
        foreach (var cb in _sinkChecks) cb.IsChecked = ok.Contains((string)cb.Tag!);
    }

    private List<string> CheckedSinks()
        => _sinkChecks.Where(cb => cb.IsChecked == true).Select(cb => (string)cb.Tag!).OrderBy(s => s, StringComparer.Ordinal).ToList();

    private async Task Run()
    {
        if (_srcId is null) { await RoadUi.Info(this, Title, "请先选一个源（下拉选采剥点，或「视口取点…」）。"); return; }
        var sinks = CheckedSinks();
        if (sinks.Count == 0) { await RoadUi.Info(this, Title, "请至少勾选一个汇。"); return; }
        _results = _solve(_srcId, sinks, _caliper);
        var rows = _results
            .OrderBy(r => r.Feasible ? 0 : 1)
            .ThenBy(r => r.RoundTripEquivM ?? double.MaxValue)
            .ThenBy(r => r.DstId, StringComparer.Ordinal)
            .Select(p => Row.From(p, _candidateIds.Contains(p.DstId)))
            .ToList();
        _grid.ItemsSource = rows;
        int ok = _results.Count(r => r.Feasible);
        var best = _results.Where(r => r.Feasible).OrderBy(r => r.RoundTripEquivM).FirstOrDefault();
        _footer.Text =
            $"源 {_srcId} → {sinks.Count} 个汇：可达 {ok} · 不可达 {sinks.Count - ok}"
            + (best is not null ? $" · 最近的是 {best.DstId}（往返等效 {best.RoundTripEquivM:F0} m）" : "")
            + $"｜{_caliper.SummaryLine()}"
            + $"\n口径说明：等效运距是沿「{_caliper.ModeText}」路测得的，不是「最小等效运距」；去程重车、回程空车两条腿独立求解，回程可能走另一条路（点某一行看视口高亮：玫红=去程、亮紫=仅回程）。";
    }

    private async Task ExportCsv()
    {
        if (_grid.ItemsSource is not List<Row> rows || rows.Count == 0) { await RoadUi.Info(this, Title, "没有可导出的结果，请先计算。"); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出等效运距 CSV", SuggestedFileName = $"等效运距_{_srcId}.csv", DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"# 等效运距（一源多汇）  源={_srcId}");
        sb.AppendLine($"# {_caliper.SummaryLine()}");
        foreach (var n in _caliper.Notes) sb.AppendLine($"# · {n}");
        sb.AppendLine($"# 等效运距沿「{_caliper.ModeText}」路测得；—=没有值（不可达/未设单价），不是 0");
        sb.AppendLine("# 汇来源：台账=load_unload_point 里的正式卸载点；候选·未入账=本次视口点的试算位置，不是台账事实");
        sb.AppendLine("汇,汇来源,状态,去程实距m,去程等效m,去程时间min,回程实距m,回程等效m,回程时间min,往返等效m,循环时间min,单趟成本元,不可达主因");
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', new[] { r.Sink, r.Source, r.Status, r.OutLen, r.OutEquiv, r.OutTime, r.RetLen, r.RetEquiv, r.RetTime, r.RoundTripEquiv, r.Cycle, r.Cost, Csv(r.Cause) }));
        try
        {
            File.WriteAllText(file.Path.LocalPath, sb.ToString(), new UTF8Encoding(true));
            await RoadUi.Info(this, Title, "已导出：" + file.Path.LocalPath);
        }
        catch (Exception ex) { await RoadUi.Info(this, Title, "导出失败：" + ex.Message); }
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    public sealed class Row
    {
        public string Sink { get; init; } = "";
        public string Status { get; init; } = "";
        public string OutLen { get; init; } = "";
        public string OutEquiv { get; init; } = "";
        public string OutTime { get; init; } = "";
        public string RetLen { get; init; } = "";
        public string RetEquiv { get; init; } = "";
        public string RetTime { get; init; } = "";
        public string RoundTripEquiv { get; init; } = "";
        public string Cycle { get; init; } = "";
        public string Cost { get; init; } = "";
        public string Cause { get; init; } = "";
        public bool Feasible { get; init; }
        public HaulPairResult? Pair { get; init; }
        public bool IsCandidate { get; init; }
        public string Source => IsCandidate ? "候选·未入账" : "台账";
        public static Row From(HaulPairResult p, bool isCandidate) => new()
        {
            IsCandidate = isCandidate,
            Sink = isCandidate ? p.DstId + CandidateTag : p.DstId,
            Status = p.StatusText,
            OutLen = HaulTextReport.Num(p.Outbound.LengthM, "F0"),
            OutEquiv = HaulTextReport.Num(p.Outbound.EquivM, "F0"),
            OutTime = HaulTextReport.Num(p.Outbound.TimeMin, "F1"),
            RetLen = HaulTextReport.Num(p.Return.LengthM, "F0"),
            RetEquiv = HaulTextReport.Num(p.Return.EquivM, "F0"),
            RetTime = HaulTextReport.Num(p.Return.TimeMin, "F1"),
            RoundTripEquiv = HaulTextReport.Num(p.RoundTripEquivM, "F0"),
            Cycle = HaulTextReport.Num(p.CycleTimeMin, "F1"),
            Cost = p.CostPerTripYuan is { } c ? c.ToString("F0") : !p.Feasible ? HaulTextReport.Dash : HaulTextReport.Dash + "（未设单价）",
            Cause = p.PrimaryDiagnosis?.Text ?? "",
            Feasible = p.Feasible,
            Pair = p,
        };
    }
}
