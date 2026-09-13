using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「标注台阶标高」窗口（短期组；移植原 <c>BenchElevationWindow</c>）。一键给工作帮的各台阶线标上「倒三角形标高符号 ▽ + 整数高程」：
/// 可一次性全标注，或按采场 / 排土场过滤（依赖「采场/排土场圈定」识别出的区域）。标注落在独立图层 <see cref="BenchElevationAnnotator.Layer"/>，
/// 每次标注前先整层清空 → 可反复重刷。几何在 <see cref="BenchElevationAnnotator"/>；本窗只负责取线、按区域过滤、删旧 + 导入。
/// </summary>
internal sealed class BenchElevationWindow : Window
{
    private readonly IPlanEntityHost _host;
    private readonly ObservableCollection<RegionVm> _regionVms = new();
    private static readonly string[] BenchLayers = { "点云_坡顶线", "点云_坡底线", "坡顶线", "坡底线", "台阶线", "点云_台阶线" };

    private readonly CheckBox _allArea = new() { Content = "全部工作帮（整图，不按区域）", IsChecked = true };
    private readonly CheckBox _selectionOnly = new() { Content = "仅标注视口选中的线（不勾则用台阶线图层，无则用全部多段线）" };
    private readonly ListBox _regionList = new() { Height = 116, IsEnabled = false };
    private readonly TextBlock _regionHint = RoadUi.Hint("勾「全部工作帮」=整图标注，不依赖区域；取消它后在上方列表里勾选要标注的区域（台阶线代表点落在区域内才标）。", 12);
    private readonly TextBlock _status = RoadUi.Hint("点「一键标注」：每个平盘立一个标高符号——▽ 尖朝下落在平盘点，顶边引线 + 整数高程；同平盘只标一处。样式在下拉「标注设置…」里改。", 12);

    public BenchElevationWindow(IPlanEntityHost host)
    {
        _host = host;
        Title = "标注台阶标高";
        PlanUi.Place(this, 600, 560);
        _regionList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RegionVm>((v, _) =>
        {
            var cb = new CheckBox { Content = new TextBlock { Text = v?.Display ?? "" }, IsChecked = v?.IsSelected ?? false, Margin = new Thickness(2, 1) };
            cb.IsCheckedChanged += (_, _) => { if (v != null) v.IsSelected = cb.IsChecked == true; };
            return cb;
        });
        _regionList.ItemsSource = _regionVms;
        _allArea.IsCheckedChanged += (_, _) => _regionList.IsEnabled = _allArea.IsChecked != true;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = PlanUi.Header("标注台阶标高", "一键给工作帮各台阶线标「▽ 标高符号 + 高程」(精确到整数)", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xC2, 0x41, 0x0C));
        Grid.SetRow(header, 0); root.Children.Add(header);
        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 4) };
        var g1 = new StackPanel();
        var top = new DockPanel();
        var refresh = RoadUi.Btn("刷新区域", LoadRegionList, 72); DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Right); top.Children.Add(refresh);
        _allArea.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(_allArea);
        g1.Children.Add(top);
        _regionList.Margin = new Thickness(0, 6, 0, 0); g1.Children.Add(_regionList);
        _regionHint.Margin = new Thickness(0, 5, 0, 0); g1.Children.Add(_regionHint);
        stack.Children.Add(PlanUi.Group("标注范围（可采区域识别出来的范围）", g1, new Thickness(0, 0, 0, 8), 10));
        var g2 = new StackPanel(); g2.Children.Add(_selectionOnly);
        stack.Children.Add(PlanUi.Group("台阶线来源", g2, new Thickness(0), 10));
        _status.Margin = new Thickness(2, 10, 0, 0); stack.Children.Add(_status);
        var sv = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(sv, 1); root.Children.Add(sv);
        var foot = RoadUi.Foot(RoadUi.Btn("一键标注", OnAnnotate, 96, bold: true), RoadUi.Btn("清除标注", OnClear, 84), RoadUi.Btn("关闭", Close, 70));
        var footB = PlanUi.Footer(foot); Grid.SetRow(footB, 2); root.Children.Add(footB);
        Content = root;
        LoadRegionList();
    }

    /// <summary>从可采区域库读已识别区域填进列表（采场/排土场等）。</summary>
    private void LoadRegionList()
    {
        var keep = _regionVms.Where(v => v.IsSelected).Select(v => v.Id).ToHashSet();
        _regionVms.Clear();
        if (_host.Db == null) { _regionHint.Text = "GeoDataBase 未就绪，无法按区域过滤；勾「全部工作帮」整图标注。"; return; }
        int n = 0;
        foreach (var x in MineableRegionRepo.All(_host.Db))
        {
            var poly = ParsePoints(x.PointsJson);
            if (poly.Length < 9) continue;
            _regionVms.Add(new RegionVm(x.Id, x.Name, x.Category, poly) { IsSelected = keep.Contains(x.Id) });
            n++;
        }
        if (n == 0) _regionHint.Text = "区域库里还没有区域——先去「采场/排土场圈定」自动识别或手工圈画，再回来「刷新区域」。或直接勾「全部工作帮」。";
    }

    private void OnClear()
    {
        int n = ClearOldAnnotations();
        _status.Text = n > 0 ? $"已清除 {n} 个旧标注实体（图层「{BenchElevationAnnotator.Layer}」）。" : "没有可清除的标注。";
    }

    public void OnAnnotate()
    {
        List<(double[] poly, string cat)>? wanted = null;
        if (_allArea.IsChecked != true)
        {
            wanted = _regionVms.Where(v => v.IsSelected).Select(v => (v.Poly, v.Category)).ToList();
            if (wanted.Count == 0) { _status.Text = "请在区域列表里勾选至少一个范围，或勾「全部工作帮」整图标注。"; return; }
        }
        var lines = CollectBenchLines(wanted, out string sourceNote);
        if (lines.Count == 0) { _status.Text = sourceNote; return; }

        var cfg = BenchElevationConfig.Load();
        var r = BenchElevationAnnotator.Build(lines, cfg.ToOptions());
        if (!r.Ok) { _status.Text = r.Message; return; }

        ClearOldAnnotations();
        int placed = PlaceMarkers(_host, r, cfg);
        _status.Text = $"{r.Message} {sourceNote} 图层「{BenchElevationAnnotator.Layer}」（{placed} 处），可「清除标注」重刷。";
        _host.Echo("标注台阶标高：" + _status.Text, false);
    }

    /// <summary>把放置点落成实体：▽ 符号(闭合三角) + 顶边引线 + 高程文字，整批一步导入（原 PMBI 载荷的托管等价）。</summary>
    internal static int PlaceMarkers(IPlanEntityHost host, BenchElevationAnnotator.Result r, BenchElevationConfig cfg)
    {
        var batch = new PlanEntityBatch { Layer = BenchElevationAnnotator.Layer, LayerColor = (0xFF, 0xE0, 0x00) };
        double rot = cfg.TiltAxis == 3 ? cfg.TiltDeg * Math.PI / 180.0 : 0;
        foreach (var m in r.Markers)
        {
            var tri = BenchElevationAnnotator.TriangleXY(m.X, m.Y, r.SymbolSize);
            batch.Rings.Add((tri.Select(t => t.x).ToArray(), tri.Select(t => t.y).ToArray(), m.Z, m.R, m.G, m.B));
            var (lx0, ly0, lx1, ly1) = BenchElevationAnnotator.LeaderXY(m.X, m.Y, r.SymbolSize, m.Label.Length);
            batch.Lines.Add((lx0, ly0, m.Z, lx1, ly1, m.Z, m.R, m.G, m.B));
            var (tx, ty) = BenchElevationAnnotator.TextAnchorXY(m.X, m.Y, r.SymbolSize);
            batch.Texts.Add((tx, ty, m.Z, r.SymbolSize, m.Label, 0, 0, m.R, m.G, m.B));
        }
        _ = rot;   // Kylin 平面文字：绕 Z 的旋转由文本实体自身角度表达时在此套用；当前批量文字按水平落图
        host.Import(batch);
        return r.Markers.Count;
    }

    private List<BenchElevationAnnotator.BenchLine> CollectBenchLines(List<(double[] poly, string cat)>? wanted, out string sourceNote)
    {
        sourceNote = "";
        bool filtering = wanted != null;
        var regs = wanted ?? _regionVms.Select(v => (v.Poly, v.Category)).ToList();
        long[] handles;
        long[] sel = _host.SelectedHandles();
        if (_selectionOnly.IsChecked == true)
        {
            if (sel.Length == 0) { sourceNote = "勾选了「仅标注选中的线」，但视口没有选中任何线。"; return new(); }
            handles = sel; sourceNote = "（用选中的线）";
        }
        else if (sel.Length > 0) { handles = sel; sourceNote = "（用选中的线）"; }
        else
        {
            handles = HandlesFromBenchLayers();
            if (handles.Length > 0) sourceNote = "（用台阶线图层）";
            else
            {
                var ann = new HashSet<long>(_host.GetHandlesByLayer(BenchElevationAnnotator.Layer));
                handles = _host.ListEntities(PlanEntityType.Polyline).Select(e => e.handle).Where(h => !ann.Contains(h)).ToArray();
                sourceNote = "（图中无台阶线图层，用全部多段线）";
            }
        }
        var outList = new List<BenchElevationAnnotator.BenchLine>(handles.Length);
        int dropped = 0;
        foreach (var h in handles)
        {
            if (!_host.TryGetPolylineWorldVertices(h, out var xyz, out _) || xyz == null || xyz.Length < 6) continue;
            string cat = CategoryOf(xyz, regs);
            if (filtering && cat.Length == 0) { dropped++; continue; }
            outList.Add(new BenchElevationAnnotator.BenchLine { Xyz = xyz, Category = cat });
        }
        if (outList.Count == 0)
            sourceNote = filtering ? "勾选区域内没有台阶线（换勾别的区域，或勾「全部工作帮」；也确认台阶线确实落在区域范围内）。"
                                   : "没有取到有效台阶线（请确认图中存在台阶线，或在视口选中要标注的线）。";
        else if (filtering) sourceNote += $"（{_regionVms.Count(v => v.IsSelected)} 个区域内取 {outList.Count} 条线" + (dropped > 0 ? $"，区域外略过 {dropped} 条" : "") + "）";
        return outList;
    }

    private long[] HandlesFromBenchLayers()
    {
        var set = new HashSet<long>();
        foreach (var layer in BenchLayers) foreach (var h in _host.GetHandlesByLayer(layer)) set.Add(h);
        return set.ToArray();
    }

    private int ClearOldAnnotations()
    {
        var old = _host.GetHandlesByLayer(BenchElevationAnnotator.Layer);
        if (old.Length > 0) _host.DeleteEntities(old);
        return old.Length;
    }

    /// <summary>台阶线代表点（XY 质心）落在哪个区域 → 该区域类别；都不在则 ""。</summary>
    internal static string CategoryOf(double[] xyz, List<(double[] poly, string cat)> regs)
    {
        if (regs.Count == 0) return "";
        int n = xyz.Length / 3; double sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { sx += xyz[i * 3]; sy += xyz[i * 3 + 1]; }
        double cx = sx / n, cy = sy / n;
        foreach (var (poly, cat) in regs) if (PointInPolygon(cx, cy, poly)) return cat;
        return "";
    }

    internal static bool PointInPolygon(double px, double py, double[] poly)
    {
        int n = poly.Length / 3; bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = poly[i * 3], yi = poly[i * 3 + 1], xj = poly[j * 3], yj = poly[j * 3 + 1];
            if (((yi > py) != (yj > py)) && (px < (xj - xi) * (py - yi) / (yj - yi + (yj == yi ? 1e-12 : 0)) + xi)) inside = !inside;
        }
        return inside;
    }

    internal static double[] ParsePoints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<double>();
        try { return System.Text.Json.JsonSerializer.Deserialize<double[]>(json) ?? Array.Empty<double>(); }
        catch { return Array.Empty<double>(); }
    }

    internal string SelftestStatus => _status.Text ?? "";

    public sealed class RegionVm
    {
        public long Id { get; }
        public string Name { get; }
        public string Category { get; }
        public double[] Poly { get; }
        public bool IsSelected { get; set; }
        public string Display => $"{Name}（{MineableRegion.DisplayName(Category)}）";
        public RegionVm(long id, string name, string category, double[] poly) { Id = id; Name = name ?? ""; Category = category ?? ""; Poly = poly; }
    }
}
