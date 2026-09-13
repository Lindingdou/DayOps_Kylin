using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「平盘标高清单」窗口（短期组；移植原 <c>BenchLevelWindow</c>）。回答"这套设计一共有多少个平盘标高"：
/// 取线（视口选中 / 指定图层 / 全图多段线）→ 可选按可采区域裁 → 按标高归并成级 → 出结论 + 逐级明细表。
/// 归并算法在 <see cref="BenchLevelInventory"/>，本窗只管取线与展示。
/// </summary>
internal sealed class BenchLevelWindow : Window
{
    private readonly IPlanEntityHost _host;
    private readonly ObservableCollection<LayerVm> _layerVms = new();
    private readonly ObservableCollection<RegionVm> _regionVms = new();
    private readonly ObservableCollection<LevelVm> _levelVms = new();
    private BenchLevelInventory.Result? _last;
    private string _lastSourceNote = "";
    private static readonly string[] ExcludedLayers = { BenchElevationAnnotator.Layer, BenchElevationAnnotator.QueryLayer };

    private readonly RadioButton _srcSel = new() { Content = "视口选中的线", GroupName = "src", Margin = new Thickness(0, 2, 0, 4) };
    private readonly RadioButton _srcLayer = new() { Content = "指定图层", GroupName = "src", IsChecked = true, Margin = new Thickness(0, 2, 0, 4) };
    private readonly RadioButton _srcAll = new() { Content = "全图所有多段线", GroupName = "src", Margin = new Thickness(0, 2, 0, 2) };
    private readonly ListBox _layerList = new() { Height = 132 }, _regionList = new() { Height = 96, IsEnabled = false };
    private readonly CheckBox _regionFilter = new() { Content = "限定可采区域", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _flatOnly = new() { Content = "只统计水平线，起伏 ≤", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _tol = PlanUi.Box("0.5", 48), _flat = PlanUi.Box("0.5", 48), _minLen = PlanUi.Box("0", 48);
    private readonly TextBlock _regionHint = RoadUi.Hint("来自「采场/排土场圈定」识别出的范围；线的 XY 质心落在勾选区域内才计入。", 11);
    private readonly TextBlock _count = new() { Text = "—", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = PlanUi.OrangeBrush, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _summary = RoadUi.Hint("设好左边的取线范围，点右下「统计」。", 12), _warn = RoadUi.Warn(""), _status = RoadUi.Hint("", 12);
    private readonly DataGrid _levelGrid;

    public BenchLevelWindow(IPlanEntityHost host)
    {
        _host = host;
        Title = "平盘标高清单";
        PlanUi.Place(this, 1000, 660);
        _layerList.ItemTemplate = CheckTemplate<LayerVm>(v => v.Display, v => v.IsSelected, (v, b) => v.IsSelected = b);
        _regionList.ItemTemplate = CheckTemplate<RegionVm>(v => v.Display, v => v.IsSelected, (v, b) => v.IsSelected = b);
        _layerList.ItemsSource = _layerVms; _regionList.ItemsSource = _regionVms;
        _regionFilter.IsCheckedChanged += (_, _) => _regionList.IsEnabled = _regionFilter.IsChecked == true;
        _srcLayer.IsCheckedChanged += (_, _) => _layerList.IsEnabled = _srcLayer.IsChecked == true;
        _levelGrid = PlanUi.Table(new (string, string, double)[] { ("序号", "Index", 60), ("标高(m)", "ElevText", 96), ("线条数", "LineCount", 72), ("平面长度(m)", "LengthText", 110), ("本级起伏(m)", "SpanText", 104), ("到下一级(m)", "DropText", 108), ("图层", "LayerText", 0) }, multi: false);
        _levelGrid.ItemsSource = _levelVms;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = PlanUi.Header("平盘标高清单", "把台阶线按标高归并成级 → 数出一共设计了多少个平盘标高 + 逐级明细", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xC2, 0x41, 0x0C));
        Grid.SetRow(header, 0); root.Children.Add(header);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), Margin = new Thickness(12, 10, 12, 4) };
        var left = new StackPanel();
        var g1 = new StackPanel();
        g1.Children.Add(_srcSel); g1.Children.Add(_srcLayer);
        var lt = new DockPanel(); var rl = RoadUi.Btn("刷新图层", () => { LoadLayerList(); _status.Text = $"图层已刷新：{_layerVms.Count} 个含线图层。"; }, 72); DockPanel.SetDock(rl, Avalonia.Controls.Dock.Right); lt.Children.Add(rl);
        var lh = RoadUi.Hint("（括号里是该层多段线条数）", 11); lh.VerticalAlignment = VerticalAlignment.Center; lt.Children.Add(lh); g1.Children.Add(lt);
        g1.Children.Add(_layerList); g1.Children.Add(_srcAll);
        left.Children.Add(PlanUi.Group("取哪些线", g1, new Thickness(0, 0, 0, 8), 10));
        var g2 = new StackPanel();
        var rt = new DockPanel(); var rr = RoadUi.Btn("刷新区域", LoadRegionList, 72); DockPanel.SetDock(rr, Avalonia.Controls.Dock.Right); rt.Children.Add(rr); rt.Children.Add(_regionFilter); g2.Children.Add(rt);
        _regionList.Margin = new Thickness(0, 6, 0, 0); g2.Children.Add(_regionList); _regionHint.Margin = new Thickness(0, 5, 0, 0); g2.Children.Add(_regionHint);
        left.Children.Add(PlanUi.Group("只统计某区域内（可选）", g2, new Thickness(0, 0, 0, 8), 10));
        var g3 = new StackPanel();
        g3.Children.Add(RoadUi.Row(RoadUi.Lbl("标高差 ≤"), _tol, RoadUi.Lbl("m 视为同一级")));
        var r3 = RoadUi.Row(_flatOnly, _flat, RoadUi.Lbl("m")); r3.Margin = new Thickness(0, 4, 0, 0); g3.Children.Add(r3);
        var r4 = RoadUi.Row(RoadUi.Lbl("忽略平面长度 <"), _minLen, RoadUi.Lbl("m 的碎线")); r4.Margin = new Thickness(0, 4, 0, 0); g3.Children.Add(r4);
        left.Children.Add(PlanUi.Group("归并规则", g3, new Thickness(0), 10));
        var lsv = new ScrollViewer { Content = left, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(lsv, 0); body.Children.Add(lsv);

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(10, 0, 0, 0) };
        var head = new StackPanel();
        var cnt = new StackPanel { Orientation = Orientation.Horizontal };
        var l1 = RoadUi.Lbl("共"); l1.VerticalAlignment = VerticalAlignment.Bottom; l1.Margin = new Thickness(0, 0, 6, 2); cnt.Children.Add(l1); cnt.Children.Add(_count);
        var l2 = RoadUi.Lbl("个平盘标高"); l2.VerticalAlignment = VerticalAlignment.Bottom; l2.Margin = new Thickness(6, 0, 0, 4); cnt.Children.Add(l2);
        head.Children.Add(cnt); _summary.Margin = new Thickness(0, 2, 0, 6); head.Children.Add(_summary);
        Grid.SetRow(head, 0); right.Children.Add(head);
        Grid.SetRow(_levelGrid, 1); right.Children.Add(_levelGrid);
        _warn.Margin = new Thickness(2, 6, 0, 0); _warn.IsVisible = false; Grid.SetRow(_warn, 2); right.Children.Add(_warn);
        Grid.SetColumn(right, 1); body.Children.Add(right);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var foot = new DockPanel();
        var btns = RoadUi.Foot(RoadUi.Btn("统计", OnCompute, 88, bold: true), RoadUi.Btn("选中该级的线", OnSelectLevelLines, 104), RoadUi.Btn("复制清单", () => _ = OnCopyAsync(), 84), RoadUi.Btn("导出 CSV", () => _ = OnExportAsync(), 84), RoadUi.Btn("关闭", Close, 70));
        btns.Margin = new Thickness(0); DockPanel.SetDock(btns, Avalonia.Controls.Dock.Right); foot.Children.Add(btns);
        _status.VerticalAlignment = VerticalAlignment.Center; foot.Children.Add(_status);
        var footB = PlanUi.Footer(foot); Grid.SetRow(footB, 2); root.Children.Add(footB);
        Content = root;
        LoadLayerList(); LoadRegionList();
    }

    private static Avalonia.Controls.Templates.FuncDataTemplate<T> CheckTemplate<T>(Func<T, string> text, Func<T, bool> get, Action<T, bool> set) where T : class
        => new((v, _) =>
        {
            var cb = new CheckBox { Content = new TextBlock { Text = v == null ? "" : text(v) }, IsChecked = v != null && get(v), Margin = new Thickness(2, 1) };   // Content 用 TextBlock：字符串 Content 里的 "_" 会被当作助记键吃掉
            cb.IsCheckedChanged += (_, _) => { if (v != null) set(v, cb.IsChecked == true); };
            return cb;
        });

    /// <summary>列图层（带该层多段线条数），条数多的排前面；标注图层不列；没线的层不占位。</summary>
    private void LoadLayerList()
    {
        var keep = _layerVms.Where(v => v.IsSelected).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        _layerVms.Clear();
        var rows = new List<LayerVm>();
        foreach (var name in _host.LayerNames())
        {
            if (string.IsNullOrEmpty(name) || ExcludedLayers.Contains(name, StringComparer.Ordinal)) continue;
            int n = _host.GetHandlesByLayer(name).Count(h => _host.TryGetPolylineWorldVertices(h, out _, out _));
            if (n == 0) continue;
            rows.Add(new LayerVm(name, n) { IsSelected = keep.Contains(name) });
        }
        foreach (var r in rows.OrderByDescending(r => r.LineCount).ThenBy(r => r.Name, StringComparer.Ordinal)) _layerVms.Add(r);
    }

    private void LoadRegionList()
    {
        var keep = _regionVms.Where(v => v.IsSelected).Select(v => v.Id).ToHashSet();
        _regionVms.Clear();
        if (_host.Db == null) { _regionHint.Text = "GeoDataBase 未就绪，无法按区域过滤；不勾「限定可采区域」即整图统计。"; return; }
        int n = 0;
        foreach (var x in MineableRegionRepo.All(_host.Db))
        {
            var poly = BenchElevationWindow.ParsePoints(x.PointsJson);
            if (poly.Length < 9) continue;
            _regionVms.Add(new RegionVm(x.Id, x.Name, x.Category, poly) { IsSelected = keep.Contains(x.Id) }); n++;
        }
        if (n == 0) _regionHint.Text = "可采区域库里还没有识别出的区域 —— 先去「采场/排土场圈定」识别，再回来「刷新区域」。";
    }

    public void OnCompute()
    {
        var opt = new BenchLevelInventory.Options
        {
            MergeTolM = ParseOr(_tol.Text, 0.5), FlatTolM = ParseOr(_flat.Text, 0.5), SkipTilted = _flatOnly.IsChecked == true, MinLengthM = Math.Max(0, ParseOr(_minLen.Text, 0)),
        };
        var lines = CollectLines(out string note);
        _lastSourceNote = note;
        _levelVms.Clear();
        if (lines.Count == 0) { _count.Text = "—"; _summary.Text = note; _warn.IsVisible = false; _last = null; _status.Text = "没取到线。"; return; }
        var r = BenchLevelInventory.Build(lines, opt);
        _last = r;
        if (!r.Ok) { _count.Text = "—"; _summary.Text = r.Message; _warn.IsVisible = false; _status.Text = note; return; }
        foreach (var lv in r.Levels) _levelVms.Add(new LevelVm(lv));
        _levelGrid.ItemsSource = null; _levelGrid.ItemsSource = _levelVms;
        _count.Text = r.Levels.Count.ToString();
        _summary.Text = $"{r.Message} {note}";
        if (r.Warnings.Count > 0) { _warn.Text = "⚠ " + string.Join("\n⚠ ", r.Warnings); _warn.IsVisible = true; } else _warn.IsVisible = false;
        _status.Text = $"归并容差 {opt.MergeTolM:0.##}m" + (opt.SkipTilted ? $"，起伏 > {opt.FlatTolM:0.##}m 视为斜线剔除" : "，不剔斜线");
        _host.Echo($"平盘标高清单：共 {r.Levels.Count} 个平盘标高 · {r.Message}", false);
    }

    private List<BenchLevelInventory.SourceLine> CollectLines(out string note)
    {
        note = "";
        var result = new List<BenchLevelInventory.SourceLine>();
        long[] handles;
        if (_srcSel.IsChecked == true)
        {
            handles = _host.SelectedHandles();
            if (handles.Length == 0) { note = "选了「视口选中的线」，但视口里没有选中任何实体。"; return result; }
            note = $"（视口选中 {handles.Length} 个实体）";
        }
        else if (_srcLayer.IsChecked == true)
        {
            var picked = _layerVms.Where(v => v.IsSelected).Select(v => v.Name).ToList();
            if (picked.Count == 0)
            {
                note = _layerVms.Count == 0 ? "图层列表为空（图里没有多段线）—— 改选「视口选中的线」或「全图所有多段线」。" : "请在左边勾选设计台阶线所在的图层（可多选），或改选别的取线方式。";
                return result;
            }
            handles = picked.SelectMany(lay => _host.GetHandlesByLayer(lay)).Distinct().ToArray();
            note = $"（图层 {string.Join("、", picked)}）";
        }
        else
        {
            var excluded = new HashSet<long>(ExcludedLayers.SelectMany(l => _host.GetHandlesByLayer(l)));
            handles = _host.ListEntities(PlanEntityType.Polyline).Select(e => e.handle).Where(h => !excluded.Contains(h)).ToArray();
            note = "（全图多段线）";
        }
        List<double[]>? rings = null;
        if (_regionFilter.IsChecked == true)
        {
            var picked = _regionVms.Where(v => v.IsSelected).ToList();
            if (picked.Count == 0) { note = "勾了「限定可采区域」，但没勾具体区域。"; return result; }
            rings = picked.Select(v => v.Poly).ToList();
            note += $"（限 {string.Join("、", picked.Select(v => v.Name))}）";
        }
        var layerOf = _host.ListEntities(PlanEntityType.Polyline).ToDictionary(e => e.handle, e => e.layer);
        int outside = 0;
        foreach (var h in handles)
        {
            if (!_host.TryGetPolylineWorldVertices(h, out var xyz, out _) || xyz is not { Length: >= 6 }) continue;
            if (rings != null && !rings.Any(rg => BenchElevationWindow.CategoryOf(xyz, new List<(double[], string)> { (rg, "x") }).Length > 0)) { outside++; continue; }
            result.Add(new BenchLevelInventory.SourceLine { Handle = unchecked((ulong)h), Layer = layerOf.TryGetValue(h, out var lay) ? lay : "", Xyz = xyz });
        }
        if (result.Count == 0) note = rings != null ? "勾选区域内没有取到线（换个区域，或先确认台阶线确实落在该范围内）。" : note + " 里没有可读的多段线。";
        else if (outside > 0) note += $"（区域外略过 {outside} 条）";
        return result;
    }

    private void OnSelectLevelLines()
    {
        if (_levelGrid.SelectedItem is not LevelVm vm) { _status.Text = "先在明细表里点一行，再点「选中该级的线」。"; return; }
        _host.ClearSelection();
        foreach (var h in vm.Handles) _host.SelectByHandle(unchecked((long)h), addToSelection: true);
        _status.Text = $"已选中 {vm.ElevText}m 这一级的 {vm.Handles.Count} 条线。";
    }

    private async Task OnCopyAsync()
    {
        if (_last is not { Ok: true }) { _status.Text = "先点「统计」。"; return; }
        try { var cb = TopLevel.GetTopLevel(this)?.Clipboard; if (cb != null) await cb.SetTextAsync(BenchLevelInventory.BuildReport(_last, _lastSourceNote)); _status.Text = "清单已复制到剪贴板。"; }
        catch (Exception ex) { _status.Text = "复制失败：" + ex.Message; }
    }

    private async Task OnExportAsync()
    {
        if (_last is not { Ok: true }) { _status.Text = "先点「统计」。"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出平盘标高清单", SuggestedFileName = $"平盘标高清单_{_last.Levels.Count}级.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } }, new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } } },
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, BenchLevelInventory.BuildReport(_last, _lastSourceNote), System.Text.Encoding.UTF8); _status.Text = "已导出：" + file.Path.LocalPath; }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }

    private static double ParseOr(string? s, double fallback) => double.TryParse((s ?? "").Trim(), out double v) ? v : fallback;

    internal void SelftestSource(bool all) { if (all) _srcAll.IsChecked = true; }
    internal string SelftestSummary => _summary.Text ?? "";
    internal int SelftestLevelCount => _levelVms.Count;

    public sealed class LayerVm
    {
        public string Name { get; } public int LineCount { get; } public bool IsSelected { get; set; }
        public string Display => $"{Name}（{LineCount} 条线）";
        public LayerVm(string name, int lineCount) { Name = name; LineCount = lineCount; }
    }
    public sealed class RegionVm
    {
        public long Id { get; } public string Name { get; } public string Category { get; } public double[] Poly { get; } public bool IsSelected { get; set; }
        public string Display => $"{Name}（{MineableRegion.DisplayName(Category)}）";
        public RegionVm(long id, string name, string category, double[] poly) { Id = id; Name = name ?? ""; Category = category ?? ""; Poly = poly; }
    }
    public sealed class LevelVm
    {
        public int Index { get; } public string ElevText { get; } public int LineCount { get; } public string LengthText { get; } public string SpanText { get; } public string DropText { get; } public string LayerText { get; }
        public IReadOnlyList<ulong> Handles { get; }
        public LevelVm(BenchLevelInventory.Level lv)
        {
            Index = lv.Index; ElevText = lv.Elevation.ToString("0.##"); LineCount = lv.LineCount; LengthText = lv.TotalLengthM.ToString("0.#");
            SpanText = lv.SpanM.ToString("0.##"); DropText = lv.DropToNextM > 0 ? lv.DropToNextM.ToString("0.##") : "—"; LayerText = string.Join(" / ", lv.Layers); Handles = lv.Handles;
        }
    }
}
