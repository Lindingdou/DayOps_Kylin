using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.Road;

/// <summary>
/// 「路网存档」管理窗：列出各时期持久化路网（名称 / 采集时刻 / 节点·边·里程），载入为当前会话图、改名、删除。
/// 忠实原 RoadNetworkArchiveWindow（走 road_network 表）。
/// </summary>
internal sealed class RoadNetworkArchiveWindow : Window
{
    private sealed class Row
    {
        public RoadNetworkRecord Entity { get; init; } = null!;
        public string Summary => $"{Entity.Name}    [{Entity.CapturedAt}]    {Entity.NodeCount} 节点 / {Entity.EdgeCount} 边 / {Entity.LengthKm:F2} km";
    }

    private readonly Func<System.Data.Common.DbConnection?> _conn;
    private readonly Action<RoadNetworkRecord> _onLoad;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly ListBox _list;
    private readonly TextBox _nameBox;

    public RoadNetworkArchiveWindow(Func<System.Data.Common.DbConnection?> conn, Action<RoadNetworkRecord> onLoad)
    {
        _conn = conn; _onLoad = onLoad;
        Title = "路网存档管理";
        RoadUi.Place(this, 700, 480);
        _list = new ListBox { ItemsSource = _rows, DisplayMemberBinding = new Avalonia.Data.Binding(nameof(Row.Summary)), FontFamily = RoadUi.Mono, FontSize = 13, Margin = new Thickness(12, 12, 12, 6) };
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is Row r) _nameBox.Text = r.Entity.Name; };
        _list.DoubleTapped += (_, _) => _ = LoadSelected();
        _nameBox = RoadUi.Box("", 220);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
        tools.Children.Add(RoadUi.Lbl("名称："));
        tools.Children.Add(_nameBox);
        tools.Children.Add(RoadUi.Btn("载入为当前图", () => _ = LoadSelected(), 100, primary: true));
        tools.Children.Add(RoadUi.Btn("改名", () => _ = RenameSelected(), 72));
        tools.Children.Add(RoadUi.Btn("删除", () => _ = DeleteSelected(), 72));
        tools.Children.Add(RoadUi.Btn("刷新", Rebuild, 72));
        var hint = RoadUi.Hint("双击或选中后「载入为当前图」：设为会话路网并入快照（供寻径/运距/演化对比）。", 12);
        hint.Margin = new Thickness(12, 0, 12, 6);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 10) };
        foot.Children.Add(RoadUi.Btn("关闭", Close, 80));
        var g = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto") };
        Grid.SetRow(_list, 0); Grid.SetRow(tools, 1); Grid.SetRow(hint, 2); Grid.SetRow(foot, 3);
        g.Children.Add(_list); g.Children.Add(tools); g.Children.Add(hint); g.Children.Add(foot);
        Content = g;
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Clear();
        foreach (var e in RoadNetworkStore.List(_conn())) _rows.Add(new Row { Entity = e });
    }

    private Row? Selected() => _list.SelectedItem as Row;

    private async Task LoadSelected()
    {
        if (Selected() is not { } r) { await RoadUi.Info(this, "路网存档", "先选中一条存档。"); return; }
        try { _onLoad(r.Entity); }
        catch (Exception ex) { await RoadUi.Info(this, "路网存档", $"载入失败：{ex.Message}"); }
    }

    private async Task RenameSelected()
    {
        if (Selected() is not { } r) { await RoadUi.Info(this, "路网存档", "先选中一条存档。"); return; }
        var name = _nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { await RoadUi.Info(this, "路网存档", "名称不能为空。"); return; }
        var c = _conn();
        if (c == null) { await RoadUi.Info(this, "路网存档", "工程库未连接。"); return; }
        try { r.Entity.Name = name; RoadNetworkStore.Update(c, r.Entity); Rebuild(); }
        catch (Exception ex) { await RoadUi.Info(this, "路网存档", $"改名失败：{ex.Message}"); }
    }

    private async Task DeleteSelected()
    {
        if (Selected() is not { } r) { await RoadUi.Info(this, "路网存档", "先选中一条存档。"); return; }
        if (!await RoadUi.Confirm(this, "路网存档", $"删除路网存档「{r.Entity.Name}」？不可撤销。")) return;
        var c = _conn();
        if (c == null) { await RoadUi.Info(this, "路网存档", "工程库未连接。"); return; }
        try { RoadNetworkStore.Delete(c, r.Entity.Id); Rebuild(); }
        catch (Exception ex) { await RoadUi.Info(this, "路网存档", $"删除失败：{ex.Message}"); }
    }
}

/// <summary>
/// W7「时段快照管理」：列出各开采期路网快照，存当前、改名、转为当前路网、对比两期差异、删除。
/// 忠实原 SnapshotManagerWindow（直接操作会话的快照列表；diff 在窗内算：节点/边 Id 集合差 + 状态变化）。
/// </summary>
internal sealed class SnapshotManagerWindow : Window
{
    private sealed class Row
    {
        public int Index { get; init; }
        public RoadSnapshot Snap { get; init; } = null!;
        public RoadGraph Graph => Snap.Graph;
        public string Summary => $"#{Index}  {Snap.Name}    {Graph.NodeCount} 节点 / {Graph.EdgeCount} 边 / 总里程 {TotalKm(Graph):F2} km";
    }

    private readonly List<RoadSnapshot> _snapshots;
    private readonly Func<RoadSnapshot?> _snapshotCurrent;
    private readonly Action<RoadSnapshot> _loadAsCurrent;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly ListBox _list;
    private readonly TextBox _nameBox, _diff;

    public SnapshotManagerWindow(List<RoadSnapshot> snapshots, Func<RoadSnapshot?> snapshotCurrent, Action<RoadSnapshot> loadAsCurrent)
    {
        _snapshots = snapshots; _snapshotCurrent = snapshotCurrent; _loadAsCurrent = loadAsCurrent;
        Title = "时段快照管理";
        RoadUi.Place(this, 700, 520);
        _list = new ListBox { ItemsSource = _rows, DisplayMemberBinding = new Avalonia.Data.Binding(nameof(Row.Summary)), SelectionMode = SelectionMode.Multiple, FontFamily = RoadUi.Mono, FontSize = 13, Margin = new Thickness(12, 12, 12, 6) };
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItems?.Count == 1 && _list.SelectedItem is Row r) _nameBox.Text = r.Snap.Name; };
        _list.DoubleTapped += (_, _) => _ = LoadSelected();
        _nameBox = RoadUi.Box("", 200);
        var tools = new WrapPanel { Margin = new Thickness(12, 0, 12, 6) };
        tools.Children.Add(RoadUi.Lbl("名称："));
        tools.Children.Add(_nameBox);
        tools.Children.Add(RoadUi.Btn("改名", () => _ = RenameSelected(), 72));
        tools.Children.Add(RoadUi.Btn("存当前为快照", () => _ = SnapCurrent(), 110, primary: true));
        tools.Children.Add(RoadUi.Btn("转为当前路网", () => _ = LoadSelected(), 110));
        tools.Children.Add(RoadUi.Btn("对比选中两期", () => _ = CompareSelected(), 110));
        tools.Children.Add(RoadUi.Btn("删除选中", DeleteSelected, 90));
        _diff = RoadUi.Mono2("选中一期改名后点「改名」；选中一期点「转为当前路网」回灌为会话图；按住 Ctrl 选中两期点「对比选中两期」看差异。", 12);
        _diff.MinHeight = 110;
        _diff.Margin = new Thickness(12, 0, 12, 6);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 10) };
        foot.Children.Add(RoadUi.Btn("关闭", Close, 80));
        var g = new Grid { RowDefinitions = new RowDefinitions("1.4*,Auto,*,Auto") };
        var diffScroll = new ScrollViewer { Content = _diff, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(_list, 0); Grid.SetRow(tools, 1); Grid.SetRow(diffScroll, 2); Grid.SetRow(foot, 3);
        g.Children.Add(_list); g.Children.Add(tools); g.Children.Add(diffScroll); g.Children.Add(foot);
        Content = g;
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Clear();
        for (int i = 0; i < _snapshots.Count; i++) _rows.Add(new Row { Index = i + 1, Snap = _snapshots[i] });
    }

    private List<Row> SelectedRows() => _list.SelectedItems?.Cast<Row>().ToList() ?? new List<Row>();

    private async Task SnapCurrent()
    {
        var snap = _snapshotCurrent();
        if (snap == null) { await RoadUi.Info(this, "时段快照", "当前没有可保存的路网图（先「基础道路网络构建」或选中坑线中线）。"); return; }
        Rebuild();
        _list.SelectedItem = _rows.LastOrDefault();
    }

    private async Task RenameSelected()
    {
        var sel = SelectedRows();
        if (sel.Count != 1) { await RoadUi.Info(this, "时段快照", "请选中正好一期快照再改名。"); return; }
        var name = _nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { await RoadUi.Info(this, "时段快照", "名称不能为空。"); return; }
        sel[0].Snap.Name = name;
        Rebuild();
        _list.SelectedItem = _rows.FirstOrDefault(r => r.Snap == sel[0].Snap);
    }

    private async Task LoadSelected()
    {
        var sel = SelectedRows();
        if (sel.Count != 1) { await RoadUi.Info(this, "时段快照", "请选中正好一期快照再「转为当前路网」。"); return; }
        if (!await RoadUi.Confirm(this, "转为当前路网", $"把快照「{sel[0].Snap.Name}」转为当前路网？\n会替换当前会话路网图（快照本身不变）。")) return;
        _loadAsCurrent(sel[0].Snap);
        _diff.Text = $"已把快照「{sel[0].Snap.Name}」转为当前路网。";
    }

    private void DeleteSelected()
    {
        var sel = SelectedRows().Select(r => r.Snap).ToList();
        if (sel.Count == 0) return;
        foreach (var s in sel) _snapshots.Remove(s);
        Rebuild();
        _diff.Text = $"已删除 {sel.Count} 期快照。";
    }

    private async Task CompareSelected()
    {
        var sel = SelectedRows();
        if (sel.Count != 2) { await RoadUi.Info(this, "时段快照", "请按住 Ctrl 选中正好两期快照再对比。"); return; }
        var a = sel.OrderBy(r => r.Index).First();
        var b = sel.OrderBy(r => r.Index).Last();
        _diff.Text = Diff(a, b);
    }

    private static string Diff(Row a, Row b)
    {
        var aNodes = a.Graph.Nodes.Select(n => n.Id).ToHashSet();
        var bNodes = b.Graph.Nodes.Select(n => n.Id).ToHashSet();
        var aEdges = a.Graph.Edges.Select(e => e.Id).ToHashSet();
        var bEdges = b.Graph.Edges.Select(e => e.Id).ToHashSet();
        var sb = new StringBuilder();
        sb.AppendLine($"对比：#{a.Index}「{a.Snap.Name}」  →  #{b.Index}「{b.Snap.Name}」").AppendLine();
        sb.AppendLine($"  节点：{a.Graph.NodeCount} → {b.Graph.NodeCount}    边：{a.Graph.EdgeCount} → {b.Graph.EdgeCount}");
        sb.AppendLine($"  总里程：{TotalKm(a.Graph):F2} → {TotalKm(b.Graph):F2} km").AppendLine();
        Section(sb, "新增边", bEdges.Except(aEdges));
        Section(sb, "删除边", aEdges.Except(bEdges));
        Section(sb, "新增节点", bNodes.Except(aNodes));
        Section(sb, "删除节点", aNodes.Except(bNodes));
        var statusChanges = new List<string>();
        foreach (var id in aEdges.Intersect(bEdges))
        {
            var ea = a.Graph.GetEdge(id); var eb = b.Graph.GetEdge(id);
            if (ea != null && eb != null && ea.Status != eb.Status) statusChanges.Add($"{id}: {StatusText(ea.Status)} → {StatusText(eb.Status)}");
        }
        Section(sb, "状态变化", statusChanges);
        if (aEdges.SetEquals(bEdges) && aNodes.SetEquals(bNodes) && statusChanges.Count == 0) sb.AppendLine("两期拓扑/状态一致，无差异。");
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        sb.AppendLine($"{title}（{list.Count}）：{string.Join("、", list)}");
    }

    private static string StatusText(RoadEdgeStatus s) => s switch { RoadEdgeStatus.Open => "开放", RoadEdgeStatus.Maintenance => "检修", _ => "关闭" };
    internal static double TotalKm(RoadGraph g) { double m = 0; foreach (var e in g.Edges) m += e.LengthM; return m / 1000.0; }
}

/// <summary>
/// 「演化对比」窗：选两期路网快照(小序号=上期、大序号=本期) → 几何匹配按段判定 延拓/截短/废除/移位/保持 →
/// 段级对比清单 + 里程账 + 人工改判 + 导出 CSV。忠实原 EvolutionCompareWindow。
/// </summary>
internal sealed class EvolutionCompareWindow : Window
{
    private sealed class SnapRow
    {
        public int Index { get; init; }
        public RoadSnapshot Snap { get; init; } = null!;
        public RoadGraph Graph => Snap.Graph;
        public string Summary => $"#{Index} {Snap.Name}    {Graph.NodeCount} 节点 / {Graph.EdgeCount} 边 / 总里程 {SnapshotManagerWindow.TotalKm(Graph):F2} km";
    }

    public sealed class RouteRow
    {
        public RouteEvolution R { get; init; } = null!;
        public string ClassText => R.ClassText;
        public string WhereText => R.WhereText;
        public string SegText => $"{R.SegLenM:F0} m";
        public string FracText => R.IsWholeEdge ? "整条" : $"{R.SegFrac * 100:F0}%";
        public string PosText => R.IsWholeEdge ? "整条" : (R.AtEdgeEnd ? "端头" : "中段");
        public string PairText => R.Side == EvolutionSide.Curr ? (R.PrevEdgeId is null ? "-" : $"上 {R.PrevEdgeId}") : (R.CurrEdgeId is null ? "-" : $"本 {R.CurrEdgeId}");
        public string MatchText => double.IsNaN(R.MatchDistanceM) ? "-" : $"{R.MatchDistanceM:F1} m";
        public string ShiftText => R.Class is RoadEvolutionClass.Keep or RoadEvolutionClass.Shift ? $"{R.ShiftM:+0.0;-0.0;0} m" : "-";
        public string DirText => R.DirAgreesWithAdvance is null ? "-" : (R.DirAgreesWithAdvance.Value ? "✓合推进" : "✗逆推进");
    }

    private readonly List<RoadSnapshot> _snapshots;
    private readonly RoadEvolutionOptions _options;
    private readonly Action<RoadEvolutionResult> _onResult;
    private readonly ObservableCollection<SnapRow> _snapRows = new();
    private readonly ObservableCollection<RouteRow> _routeRows = new();
    private readonly ListBox _snapList;
    private readonly DataGrid _routeView;
    private readonly TextBlock _summary;
    private readonly TextBox _tbTol, _tbAngle, _tbGrow, _tbStep, _tbShift;
    private RoadEvolutionResult? _result;

    public EvolutionCompareWindow(List<RoadSnapshot> snapshots, RoadEvolutionOptions options, Action<RoadEvolutionResult> onResult)
    {
        _snapshots = snapshots; _options = options; _onResult = onResult;
        Title = "演化对比（延拓 / 截短 / 废除 / 移位 / 保持，按段判定）";
        RoadUi.Place(this, 900, 620);
        _snapList = new ListBox { ItemsSource = _snapRows, DisplayMemberBinding = new Avalonia.Data.Binding(nameof(SnapRow.Summary)), SelectionMode = SelectionMode.Multiple, FontFamily = RoadUi.Mono, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), MinHeight = 96, MaxHeight = 140 };
        var leftPanel = new StackPanel { Margin = new Thickness(12, 10, 6, 4) };
        leftPanel.Children.Add(RoadUi.Text("按住 Ctrl 选中正好两期快照（小序号=上期，大序号=本期）：", 12.5, bold: true));
        leftPanel.Children.Add(_snapList);
        _tbTol = RoadUi.NumBox(_options.MatchToleranceM);
        _tbAngle = RoadUi.NumBox(_options.MatchAngleDeg);
        _tbGrow = RoadUi.NumBox(_options.GrowMinLenM);
        _tbStep = RoadUi.NumBox(_options.SampleStepM);
        _tbShift = RoadUi.NumBox(_options.ShiftThresholdM);
        var paramGrid = new Grid { Margin = new Thickness(6, 10, 12, 4), ColumnDefinitions = new ColumnDefinitions("Auto,72"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
        AddParamRow(paramGrid, 0, "参数（真实数据可调）", null);
        AddParamRow(paramGrid, 1, "匹配容差 (m)", _tbTol);
        AddParamRow(paramGrid, 2, "走向夹角 (°)", _tbAngle);
        AddParamRow(paramGrid, 3, "最小段长 (m)", _tbGrow);
        AddParamRow(paramGrid, 4, "移位阈值 (m)", _tbShift);
        AddParamRow(paramGrid, 5, "采样步长 (m)", _tbStep);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("1.7*,Auto") };
        Grid.SetColumn(leftPanel, 0); Grid.SetColumn(paramGrid, 1);
        top.Children.Add(leftPanel); top.Children.Add(paramGrid);

        var tools = new WrapPanel { Margin = new Thickness(12, 2, 12, 4) };
        tools.Children.Add(RoadUi.Btn("识别对比", () => _ = RunAnalysis(), 84, bold: true, primary: true));
        tools.Children.Add(RoadUi.Sep());
        tools.Children.Add(RoadUi.Btn("标为延拓", () => _ = Reclassify(RoadEvolutionClass.Extend)));
        tools.Children.Add(RoadUi.Btn("标为截短", () => _ = Reclassify(RoadEvolutionClass.Shorten)));
        tools.Children.Add(RoadUi.Btn("标为废除", () => _ = Reclassify(RoadEvolutionClass.Abolish)));
        tools.Children.Add(RoadUi.Btn("标为移位", () => _ = Reclassify(RoadEvolutionClass.Shift)));
        tools.Children.Add(RoadUi.Btn("标为保持", () => _ = Reclassify(RoadEvolutionClass.Keep)));
        tools.Children.Add(RoadUi.Sep());
        tools.Children.Add(RoadUi.Btn("导出 CSV", () => _ = ExportCsv()));
        tools.Children.Add(RoadUi.Btn("关闭", Close));

        _routeView = RoadUi.Table(new (string, string, double)[]
        {
            ("类别", nameof(RouteRow.ClassText), 100), ("段位置(边+里程)", nameof(RouteRow.WhereText), 190), ("段长", nameof(RouteRow.SegText), 70),
            ("占边", nameof(RouteRow.FracText), 55), ("位置", nameof(RouteRow.PosText), 55), ("对期", nameof(RouteRow.PairText), 90),
            ("匹配距", nameof(RouteRow.MatchText), 70), ("横移", nameof(RouteRow.ShiftText), 70), ("方向", nameof(RouteRow.DirText), 85),
        });
        _routeView.ItemsSource = _routeRows;
        _routeView.Margin = new Thickness(12, 0, 12, 4);
        _summary = RoadUi.Text("选两期快照 → 点「识别对比」。出清单后用「分色显示」上图查看。", 12.5, bold: true);
        _summary.Margin = new Thickness(12, 0, 12, 10);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Grid.SetRow(top, 0); Grid.SetRow(tools, 1); Grid.SetRow(_routeView, 2); Grid.SetRow(_summary, 3);
        grid.Children.Add(top); grid.Children.Add(tools); grid.Children.Add(_routeView); grid.Children.Add(_summary);
        Content = grid;
        RebuildSnapshots();
    }

    private void RebuildSnapshots()
    {
        _snapRows.Clear();
        for (int i = 0; i < _snapshots.Count; i++) _snapRows.Add(new SnapRow { Index = i + 1, Snap = _snapshots[i] });
        if (_snapRows.Count >= 2 && _snapList.SelectedItems != null)
        {
            _snapList.SelectedItems.Add(_snapRows[_snapRows.Count - 2]);
            _snapList.SelectedItems.Add(_snapRows[_snapRows.Count - 1]);
        }
    }

    private async Task RunAnalysis()
    {
        var sel = (_snapList.SelectedItems?.Cast<SnapRow>() ?? Enumerable.Empty<SnapRow>()).OrderBy(r => r.Index).ToList();
        if (sel.Count != 2) { await RoadUi.Info(this, "演化对比", "请按住 Ctrl 选中正好两期快照再识别。"); return; }
        var prev = sel[0].Graph; var curr = sel[1].Graph;
        _options.MatchToleranceM = ParseOr(_tbTol, _options.MatchToleranceM);
        _options.MatchAngleDeg = ParseOr(_tbAngle, _options.MatchAngleDeg);
        _options.GrowMinLenM = ParseOr(_tbGrow, _options.GrowMinLenM);
        _options.SampleStepM = ParseOr(_tbStep, _options.SampleStepM);
        _options.ShiftThresholdM = ParseOr(_tbShift, _options.ShiftThresholdM);
        _result = RoadEvolutionAnalyzer.Analyze(prev, curr, _options);
        RefreshRows();
        _onResult(_result);
        _summary.Text = $"上期 #{sel[0].Index} → 本期 #{sel[1].Index}：{_result.Summary}" + Environment.NewLine + _result.Ledger.Text
                        + "　误判可选中行用「标为…」改判；再用「分色显示」上图/刷新。";
    }

    private void RefreshRows()
    {
        _routeRows.Clear();
        if (_result is null) return;
        foreach (var r in _result.Routes.OrderBy(r => (int)r.Class)) _routeRows.Add(new RouteRow { R = r });
    }

    private async Task Reclassify(RoadEvolutionClass cls)
    {
        var rows = _routeView.SelectedItems?.Cast<RouteRow>().ToList() ?? new List<RouteRow>();
        if (rows.Count == 0 || _result is null) { await RoadUi.Info(this, "人工改判", "先选中结果表里要改判的行。"); return; }
        foreach (var row in rows) row.R.Class = cls;
        RefreshRows();
        _summary.Text = $"已改判 {rows.Count} 条 → {ClassName(cls)}。{_result.Summary}（用「分色显示」刷新看图）";
    }

    private async Task ExportCsv()
    {
        if (_result is null || _result.Routes.Count == 0) { await RoadUi.Info(this, "导出 CSV", "尚无可导出的结果，先识别对比。"); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出演化对比清单", SuggestedFileName = "道路演化对比清单.csv", DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return;
        try
        {
            System.IO.File.WriteAllText(file.Path.LocalPath, _result.ToCsv(), new UTF8Encoding(true));
            _summary.Text = $"已导出：{file.Path.LocalPath}";
        }
        catch (Exception ex) { await RoadUi.Info(this, "导出 CSV", $"导出失败：{ex.Message}"); }
    }

    private static string ClassName(RoadEvolutionClass c) => c switch
    {
        RoadEvolutionClass.Extend => "延拓", RoadEvolutionClass.Shorten => "截短", RoadEvolutionClass.Abolish => "已废除", RoadEvolutionClass.Shift => "移位", _ => "保持",
    };

    private static double ParseOr(TextBox tb, double fallback)
        => double.TryParse(tb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : fallback;

    private static void AddParamRow(Grid g, int row, string label, TextBox? box)
    {
        var t = RoadUi.Text(label, 12.5, false, box is null);
        t.Margin = new Thickness(0, 4, 8, 0); t.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(t, row); Grid.SetColumn(t, 0); g.Children.Add(t);
        if (box is not null) { Grid.SetRow(box, row); Grid.SetColumn(box, 1); g.Children.Add(box); }
    }
}
