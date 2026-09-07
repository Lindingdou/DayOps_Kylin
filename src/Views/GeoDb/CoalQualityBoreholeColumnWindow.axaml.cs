using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 钻孔煤质柱状图 3D(忠实原 CoalQualityBoreholeColumnWindow): 化验孔按矿区 (X,Y) 真实坐标摆放, 每孔一根立柱
/// (孔口高程 → 孔口−孔深), 柱身按煤层段分色, 化验段加粗并可按 Ad/S 分级着色; 选中孔高亮(其余淡出)。
/// </summary>
public partial class CoalQualityBoreholeColumnWindow : Window
{
    private readonly GeoDbContext _ctx;

    // 渲染参数(忠实原: 柱半径 25 m / 化验段 32 m)
    private const double HoleRadius = 25;
    private const double SampleRadius = 32;

    private double _originX, _originY;
    private List<CoalBoreholeRow> _holes = new();
    private Dictionary<long, List<CoalSeamResultRow>> _seamResultsByHole = new();
    private Dictionary<long, List<CoalSampleRow>> _samplesByHole = new();
    private List<CoalSeamDefRow> _seams = new();
    private List<CoalGradeRuleFull> _ashRules = new(), _sulfurRules = new();
    private readonly Dictionary<long, List<Box3D>> _holeBoxes = new();
    private string _colorMode = "seam";
    private bool _loaded, _suppressSelect;

    /// <summary>仅供 XAML 设计器/编译器使用。</summary>
    public CoalQualityBoreholeColumnWindow() { _ctx = null!; InitializeComponent(); }

    public CoalQualityBoreholeColumnWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        viewport3D.ItemClicked += OnSceneItemClicked;
        Opened += (_, _) => { if (!_loaded) { _loaded = true; LoadAndRender(); } };
    }

    // ─────────────────────────────────────────────────────────
    private void LoadAndRender()
    {
        try
        {
            statusText.Text = "加载中...";
            _seams = CoalSeamDefs(_ctx.Conn);
            _ashRules = CoalGradeRules(_ctx.Conn, "ash");
            _sulfurRules = CoalGradeRules(_ctx.Conn, "sulfur");

            _holes = CoalBoreholesWithSamples(_ctx.Conn);   // 只显示有化验的孔
            if (_holes.Count == 0) { statusText.Text = "数据库中无化验孔"; holeCountText.Text = "0"; return; }

            holeCountText.Text = $"{_holes.Count}";
            subtitleText.Text = $"  化验孔 {_holes.Count} 个  ·  矿区坐标已归零";

            var allSr = CoalSeamResults(_ctx.Conn).GroupBy(s => s.BoreholeId).ToDictionary(g => g.Key, g => g.ToList());
            var allSamples = CoalLoadSamples(_ctx.Conn).GroupBy(s => s.BoreholeId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.DepthFrom).ToList());
            _seamResultsByHole.Clear();
            _samplesByHole.Clear();
            foreach (var h in _holes)
            {
                _seamResultsByHole[h.Id] = allSr.TryGetValue(h.Id, out var sr) ? sr : new List<CoalSeamResultRow>();
                _samplesByHole[h.Id] = allSamples.TryGetValue(h.Id, out var ss) ? ss : new List<CoalSampleRow>();
            }

            _originX = _holes.Average(h => h.X);
            _originY = _holes.Average(h => h.Y);

            holeList.Items.Clear();
            foreach (var h in _holes.OrderBy(h => h.HoleId))
                holeList.Items.Add(new ListBoxItem { Content = $"{h.HoleId}    ({_samplesByHole[h.Id].Count} 段化验)", Tag = h });

            BuildScene();
            statusText.Text = $"已加载 {_holes.Count} 孔, {_samplesByHole.Sum(kv => kv.Value.Count)} 化验段, {_seamResultsByHole.Sum(kv => kv.Value.Count)} 煤层段";
        }
        catch (Exception ex) { _ = CoalMsgBox.ShowAsync(this, "错误", $"加载失败：{ex.Message}"); }
    }

    // ─────────────────────────────────────────────────────────
    private void BuildScene()
    {
        viewport3D.Boxes.Clear();
        viewport3D.Points.Clear();
        viewport3D.Lines.Clear();
        _holeBoxes.Clear();
        viewport3D.ShowGrid = true;   // 地表网格(原 GridLinesVisual3D)
        foreach (var h in _holes)
        {
            var boxes = BuildOneHole(h);
            _holeBoxes[h.Id] = boxes;
            viewport3D.Boxes.AddRange(boxes);
        }
        HighlightSelectedHoles();
        viewport3D.Refresh();
        viewport3D.ZoomExtents();
    }

    private List<Box3D> BuildOneHole(CoalBoreholeRow h)
    {
        var list = new List<Box3D>();
        double cx = h.X - _originX, cy = h.Y - _originY;
        double topZ = h.ZCollar ?? 1300;
        double depth = h.DepthTotal ?? 200;
        double botZ = topZ - depth;

        // 1) 整孔轮廓柱（淡灰）+ 顶部孔号标签
        list.Add(new Box3D
        {
            X = cx, Y = cy, Z0 = botZ, Z1 = topZ, W = HoleRadius * 2, D = HoleRadius * 2,
            Color = Color.FromRgb(0xCB, 0xD4, 0xDB), Opacity = 0x90 / 255.0, Label = h.HoleId,
            Tip = $"{h.HoleId}  孔深 {depth:F1}m  高程 {topZ:F1}m", Tag = h,
        });

        // 2) 煤层段彩色(仅 正常 状态; 段底优先权威底板标高, 缺则 孔口−测井止煤深度)
        foreach (var sr in _seamResultsByHole[h.Id])
        {
            if (sr.OverallThickness is null || sr.Status != "正常") continue;
            double? floorZ = CoalSeamSegmentFloor(sr, topZ);
            if (floorZ is null) continue;
            var (r, g, b) = CoalSeamColor(_seams, sr.SeamCode);
            list.Add(new Box3D
            {
                X = cx, Y = cy, Z0 = floorZ.Value, Z1 = floorZ.Value + sr.OverallThickness.Value, W = (HoleRadius + 1) * 2, D = (HoleRadius + 1) * 2,
                Color = Color.FromRgb(r, g, b), Tip = $"{h.HoleId} · {sr.SeamCode} 煤层  厚 {sr.OverallThickness:F2}m  底板 {floorZ:F1}m", Tag = h,
            });
        }

        // 3) 化验段加粗 + 着色
        foreach (var s in _samplesByHole[h.Id])
        {
            if (s.ZSample is null || s.SampleThickness is null) continue;
            double mid = s.ZSample.Value, th = s.SampleThickness.Value;
            list.Add(new Box3D
            {
                X = cx, Y = cy, Z0 = mid - th / 2, Z1 = mid + th / 2, W = SampleRadius * 2, D = SampleRadius * 2,
                Color = ResolveColor(s),
                Tip = $"{h.HoleId} · {s.SeamCode} {s.DepthFrom:F1}~{s.DepthTo:F1}m  Ad={s.AdRaw?.ToString("F1") ?? "—"} S={s.StdRaw?.ToString("F2") ?? "—"}", Tag = h,
            });
        }
        return list;
    }

    private Color ResolveColor(CoalSampleRow s)
    {
        if (_colorMode == "ad")
        {
            var rule = CoalFindLevel(_ashRules, s.AdRaw);
            var (r, g, b) = CoalHexToRgb(rule?.ColorHex, (0xA9, 0xA9, 0xA9));
            return rule is not null ? Color.FromRgb(r, g, b) : Colors.DarkGray;
        }
        if (_colorMode == "s")
        {
            var rule = CoalFindLevel(_sulfurRules, s.StdRaw);
            var (r, g, b) = CoalHexToRgb(rule?.ColorHex, (0xA9, 0xA9, 0xA9));
            return rule is not null ? Color.FromRgb(r, g, b) : Colors.DarkGray;
        }
        var (sr, sg, sb) = CoalSeamColor(_seams, s.SeamCode);
        return Color.FromRgb(sr, sg, sb);
    }

    // ─────────────────────────────────────────────────────────
    private void OnHoleSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect) return;
        if (holeList.SelectedItem is not ListBoxItem item || item.Tag is not CoalBoreholeRow h)
        {
            ClearDetail();
            HighlightSelectedHoles();
            viewport3D.Refresh();
            return;
        }
        ShowDetail(h);
        HighlightSelectedHoles();
        viewport3D.Refresh();
    }

    private void ShowDetail(CoalBoreholeRow h)
    {
        detailHole.Text = $"{h.HoleId}  (孔深 {h.DepthTotal?.ToString("F1") ?? "—"}m)";
        detailLocation.Text = $"X={h.X:F1}, Y={h.Y:F1}, 高程 Z={h.ZCollar?.ToString("F1") ?? "—"}m  "
                            + (h.CoordFilled != "原始" ? "  [" + h.CoordFilled + "]" : "");

        holeSummary.Text = CoalHoleSummary(_samplesByHole[h.Id], _ashRules, _sulfurRules);
        holeSummaryBox.IsVisible = true;

        var seamRows = _seamResultsByHole[h.Id].Where(s => s.Status == "正常").OrderBy(s => s.LogEndDepth ?? 0)
            .Select(s =>
            {
                var (r, g, b) = CoalSeamColor(_seams, s.SeamCode);
                return new CoalLegendItem { Label = $"{s.SeamCode}  止深 {s.LogEndDepth:F1}m  厚 {s.OverallThickness:F2}m", ColorBrush = new SolidColorBrush(Color.FromRgb(r, g, b)) };
            }).ToList();
        seamList.ItemsSource = seamRows;

        var samples = _samplesByHole[h.Id].OrderBy(s => s.DepthFrom ?? 0)
            .Select(s => new CoalLegendItem
            {
                Label = $"{s.SeamCode} / {s.DepthFrom:F1}~{s.DepthTo:F1}m  "
                      + $"Ad={s.AdRaw?.ToString("F1") ?? "—"}  S={s.StdRaw?.ToString("F2") ?? "—"}  "
                      + $"Vdaf={s.VdafRaw?.ToString("F1") ?? "—"}  Qgr={s.QgrD?.ToString("F1") ?? "—"}  煤类={s.CoalType ?? "—"}",
                ColorBrush = new SolidColorBrush(ResolveColor(s)),
            }).ToList();
        sampleList.ItemsSource = samples;
        detailFooter.Text = $"化验 {samples.Count} 段 / 煤层 {seamRows.Count} 层";
    }

    /// <summary>选中孔保持不透明, 其余淡出 0.3(忠实原 SetOpacity); 无选中时全部正常。</summary>
    private void HighlightSelectedHoles()
    {
        var selectedIds = holeList.SelectedItems?.OfType<ListBoxItem>().Select(i => i.Tag as CoalBoreholeRow).Where(b => b != null).Select(b => b!.Id).ToHashSet()
                          ?? new HashSet<long>();
        foreach (var (id, boxes) in _holeBoxes)
        {
            bool on = selectedIds.Count == 0 || selectedIds.Contains(id);
            foreach (var b in boxes)
            {
                double baseOp = b.Label != null ? 0x90 / 255.0 : 1.0;   // 轮廓柱本身半透明
                b.Opacity = on ? baseOp : baseOp * 0.3;
            }
        }
    }

    private void ClearDetail()
    {
        detailHole.Text = "(未选)";
        detailLocation.Text = "";
        holeSummaryBox.IsVisible = false;
        seamList.ItemsSource = null;
        sampleList.ItemsSource = null;
        detailFooter.Text = "";
    }

    /// <summary>3D 视图点击某孔柱段 → 在左列表选中该孔。</summary>
    private void OnSceneItemClicked(object? tag)
    {
        if (tag is not CoalBoreholeRow h) return;
        foreach (var it in holeList.Items.OfType<ListBoxItem>())
            if (it.Tag is CoalBoreholeRow b && b.Id == h.Id) { holeList.SelectedItem = it; return; }
    }

    // ─────────────────────────────────────────────────────────
    private void OnColorChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _colorMode = (colorScheme.SelectedItem as ComboBoxItem)?.Tag as string ?? "seam";
        BuildScene();
        if (holeList.SelectedItem is ListBoxItem item && item.Tag is CoalBoreholeRow h) ShowDetail(h);
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        _suppressSelect = true;
        try { holeList.SelectAll(); }
        finally { _suppressSelect = false; }
        OnHoleSelected(holeList, null!);
    }

    private void OnZoomExtentsClick(object? sender, RoutedEventArgs e) => viewport3D.ZoomExtents();
}
