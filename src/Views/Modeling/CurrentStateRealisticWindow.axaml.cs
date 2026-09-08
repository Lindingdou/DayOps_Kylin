using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Data.Common;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Views.GeoDb;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 现状写实(拾取见煤点; 原 MeshEditLib.ModelUpdate.CurrentStateRealisticWindow): 在视口拾取三角网/点上的点, 逐点弹窗确认「哪层煤 + 顶/底板」,
/// 记录为现状见煤点(x/y/z/煤层/顶底), 按批次(系统唯一时间标签)组织。煤层来自预置的「煤层结构」。
/// 原对屏幕点做曲面射线求交 → 此处对拾取 XY 在场景三角网上取高程(无网则取最近的带高程点); 图上标注 = 场景实体(图层「现状见煤点标注」)。
/// Kylin 追加: 采集选中点 / CSV 模板·导入·导出(原 CurrentStatePointExcelIo) / 建现状面(原 CurrentStateSurfaceBuilder)。
/// </summary>
public partial class CurrentStateRealisticWindow : Window
{
    private readonly ModelingContext _ctx;
    private readonly DbConnection? _conn;

    private const string MarkSettingsKey = "geo.realistic.currentstate.mark";
    public const string SurfaceLayer = "现状面";

    private SeamStructureConfig _config = new();
    private long? _currentBatchId;
    private bool _loadingBatches;
    private ObservableCollection<PointRow> _points = new();

    private bool _picking;
    private string? _lastSeamCode;
    private string _lastHorizon = "顶板";
    private readonly Dictionary<MeshEntity, SurfaceUpdateEngine.MeshSampler2D?> _samplers = new();

    /// <summary>XAML 编译器/设计器用。</summary>
    public CurrentStateRealisticWindow() { _ctx = null!; InitializeComponent(); }

    public CurrentStateRealisticWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        _conn = ctx.Conn();
        InitializeComponent();
        _config = SeamStructureConfig.LoadOrDefault(_conn);
        LoadMarkSettings();
        Opened += (_, _) => LoadBatches(null);
        Closed += (_, _) => { _picking = false; SaveMarkSettings(); };
    }

    // ─────────────────────────── 图上标注 ───────────────────────────
    /// <summary>标注开关与字高(全局记住, 同「煤层结构」的做法)。</summary>
    public sealed class MarkSettings
    {
        public bool Show { get; set; } = true;
        public double Size { get; set; } = 10.0;
    }

    private void LoadMarkSettings()
    {
        var m = MuSettingsGet<MarkSettings>(MarkSettingsKey) ?? new MarkSettings();
        markCheck.IsChecked = m.Show;
        markSizeBox.Text = m.Size.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void SaveMarkSettings()
    {
        try { MuSettingsSet(MarkSettingsKey, new MarkSettings { Show = markCheck.IsChecked == true, Size = MarkSize() }); }
        catch { }
    }

    private double MarkSize()
    {
        var v = BoreholeParseNum(markSizeBox.Text);
        return v is > 0 ? v.Value : 10.0;
    }

    private List<CurrentStatePointMarker.Item> MarkItems()
    {
        var list = new List<CurrentStatePointMarker.Item>(_points.Count);
        foreach (var r in _points)
            list.Add(new CurrentStatePointMarker.Item { X = r.X, Y = r.Y, Z = r.Z, SeamName = r.SeamName, Horizon = r.Horizon });
        return list;
    }

    /// <summary>重绘本批次全部标注(先清旧)。拾取中每点只追加一条, 见 <see cref="MarkOne"/>。</summary>
    private void RedrawMarks(bool quiet = false)
    {
        if (markCheck.IsChecked != true) return;
        var (ok, msg) = DrawMarks(MarkItems(), MarkSize(), clearFirst: true);
        if (!quiet || !ok) statusText.Text = (ok ? "✓ " : "✗ ") + msg;
    }

    /// <summary>拾取到一个点就画一个(不重画整批, 拾取过程不卡)。</summary>
    private void MarkOne(PointRow r)
    {
        if (markCheck.IsChecked != true) return;
        DrawMarks(new List<CurrentStatePointMarker.Item>
        {
            new CurrentStatePointMarker.Item { X = r.X, Y = r.Y, Z = r.Z, SeamName = r.SeamName, Horizon = r.Horizon },
        }, MarkSize(), clearFirst: false);
    }

    /// <summary>原 CurrentStatePointMarker.Draw: 先清掉本图层旧标记, 再把 items 整批入图。</summary>
    private (bool ok, string message) DrawMarks(IReadOnlyList<CurrentStatePointMarker.Item> items, double size, bool clearFirst)
    {
        if (clearFirst) _ctx.RemoveLayerEntities(CurrentStatePointMarker.Layer);
        if (items.Count == 0) { _ctx.RefreshScene(); return (true, "无见煤点可标注"); }
        try
        {
            _ctx.AddEntities(CurrentStatePointMarker.Build(items, size), CurrentStatePointMarker.Layer, null);
            return (true, $"已标注 {items.Count} 个见煤点（图层「{CurrentStatePointMarker.Layer}」）");
        }
        catch (Exception ex) { return (false, "标注异常：" + ex.Message); }
    }

    private int ClearMarks()
    {
        int n = _ctx.RemoveLayerEntities(CurrentStatePointMarker.Layer);
        _ctx.RefreshScene();
        return n;
    }

    private void OnMarkToggled(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (markCheck.IsChecked == true) RedrawMarks();
        else
        {
            int n = ClearMarks();
            statusText.Text = n > 0 ? $"已清除 {n} 个标注实体" : "图上没有标注";
        }
        SaveMarkSettings();
    }

    private void OnRedrawMarks(object? sender, RoutedEventArgs e)
    {
        markCheck.IsChecked = true;
        RedrawMarks();
        SaveMarkSettings();
    }

    private void OnClearMarks(object? sender, RoutedEventArgs e)
    {
        int n = ClearMarks();
        statusText.Text = n > 0 ? $"已清除 {n} 个标注实体（数据不受影响）" : "图上没有标注";
    }

    // ─────────────────────────── 批次 ───────────────────────────
    private void LoadBatches(long? selectId)
    {
        _loadingBatches = true;
        List<CsBatchRow> batches;
        try { batches = _conn == null ? new() : CsAllBatches(_conn); }
        catch (Exception ex) { _loadingBatches = false; statusText.Text = $"批次加载失败：{ex.Message}"; return; }

        var items = new List<ComboBoxItem>();
        ComboBoxItem? sel = null;
        foreach (var b in batches)
        {
            var item = new ComboBoxItem { Content = BatchLabel(b), Tag = b };
            items.Add(item);
            if (selectId != null && b.Id == selectId) sel = item;
        }
        batchCombo.ItemsSource = items;
        _loadingBatches = false;

        if (sel != null) batchCombo.SelectedItem = sel;
        else if (items.Count > 0) batchCombo.SelectedIndex = 0;
        else { _currentBatchId = null; _points = new(); pointGrid.ItemsSource = _points; }
    }

    private static string BatchLabel(CsBatchRow b) => $"{b.Name}  ({b.PointCount}点)";

    private void OnBatchChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingBatches) return;
        _currentBatchId = (batchCombo.SelectedItem as ComboBoxItem)?.Tag is CsBatchRow b ? b.Id : (long?)null;
        LoadPoints();
    }

    private void OnNewBatch(object? sender, RoutedEventArgs e)
    {
        if (_conn == null) { statusText.Text = "数据库不可用"; return; }
        try
        {
            long id = CsCreateBatch(_conn, "拾取录入");
            LoadBatches(id);
            statusText.Text = "已新建现状写实批次；点「开始拾取」在视口拾取见煤点";
        }
        catch (Exception ex) { statusText.Text = $"新建批次失败：{ex.Message}"; }
    }

    private long EnsureBatch()
    {
        if (_currentBatchId is { } id) return id;
        if (_conn == null) throw new InvalidOperationException("数据库不可用");
        long nid = CsCreateBatch(_conn, "拾取录入");
        LoadBatches(nid);
        return nid;
    }

    private void UpdateCurrentBatchCountDisplay()
    {
        if (batchCombo.SelectedItem is ComboBoxItem it && it.Tag is CsBatchRow b)
        {
            b.PointCount = _points.Count;
            it.Content = BatchLabel(b);
        }
    }

    // ─────────────────────────── 点网格 ───────────────────────────
    private void LoadPoints()
    {
        _points = new ObservableCollection<PointRow>();
        if (_currentBatchId is { } b && _conn != null)
        {
            try
            {
                foreach (var p in CsPointsByBatch(_conn, b))
                    _points.Add(new PointRow
                    {
                        X = p.X, Y = p.Y, Z = p.Z,
                        SeamCode = p.SeamCode, SeamName = _config.NameFor(p.SeamCode),
                        Horizon = p.Horizon, Remark = p.Remark,
                    });
            }
            catch (Exception ex) { statusText.Text = $"加载失败：{ex.Message}"; }
        }
        pointGrid.ItemsSource = _points;
        statusText.Text = $"当前批次 {_points.Count} 个见煤点";
        RedrawMarks(quiet: true);                 // 标注跟着当前批次走
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (pointGrid.SelectedItem is PointRow r)
        { _points.Remove(r); UpdateCurrentBatchCountDisplay(); RedrawMarks(quiet: true); }
        else statusText.Text = "请先选中要删除的见煤点";
    }

    // ─────────────────────────── 煤层结构预置 ───────────────────────────
    private async void OnEditStructure(object? sender, RoutedEventArgs e)
    {
        var dlg = new SeamStructureDialog(_conn, _config);
        if (await dlg.ShowDialog<bool>(this) && dlg.Result != null)
        {
            _config = dlg.Result;
            _config.Save();
            statusText.Text = $"✓ 煤层结构已更新并记住（{_config.Seams.Count} 层）";
        }
    }

    // ─────────────────────────── 视口拾取 ───────────────────────────
    private async void OnStartPick(object? sender, RoutedEventArgs e)
    {
        if (_picking) return;
        if (_config.Seams.Count == 0)
        {
            statusText.Text = "请先「煤层结构…」预置煤层，再拾取";
            await BoreholeMsgBox.InfoAsync(this, "现状写实", "尚未预置煤层。请先点「煤层结构…」指定煤层后再拾取见煤点。");
            return;
        }
        try { EnsureBatch(); }
        catch (Exception ex) { statusText.Text = $"新建批次失败：{ex.Message}"; return; }
        _picking = true;
        pickButton.IsEnabled = false;
        _samplers.Clear();
        statusText.Text = "拾取中：在视口点击见煤点(三角网/点)，逐点弹窗定煤层+顶/底；Esc 结束";
        try
        {
            while (_picking)
            {
                var p = await _ctx.PickPointAsync("拾取见煤点（Esc 结束）");
                if (p == null || !_picking) break;
                await HandlePick(p.Value.x, p.Value.y);
            }
        }
        finally { StopPicking(save: true); }
    }

    /// <summary>弹窗定煤层+层位 → 点「确定」后才解算曲面高程(拾取 XY 在场景三角网上取 Z; 无网面则取最近带高程点)。</summary>
    private async System.Threading.Tasks.Task HandlePick(double x, double y)
    {
        var dlg = new PickHorizonDialog(_config, double.NaN, double.NaN, double.NaN, _lastSeamCode, _lastHorizon);
        if (!await dlg.ShowDialog<bool>(this)) return;

        double? z = SampleSceneZ(x, y);
        if (z == null)
        {
            statusText.Text = "✗ 该处没打到三角网/点云面（可能点在空白处或面被隐藏），本点未记录，请重新点";
            return;
        }
        var row = new PointRow
        {
            X = x, Y = y, Z = z.Value,
            SeamCode = dlg.SeamCode, SeamName = dlg.SeamName,
            Horizon = dlg.Horizon, Remark = dlg.Remark,
        };
        _points.Add(row);
        MarkOne(row);                      // 当场在图上画出落点+引线+高程
        _lastSeamCode = dlg.SeamCode; _lastHorizon = dlg.Horizon;
        UpdateCurrentBatchCountDisplay();
        statusText.Text = $"已记录见煤点：{dlg.SeamName} · {dlg.Horizon} · Z={z.Value:F2}（共 {_points.Count} 点）；继续点击或 Esc 结束";
    }

    /// <summary>拾取 XY 处的曲面高程: 逐场景三角网重心插值(先建采样器缓存); 都不含则取视口跨度 1% 内最近的点实体高程。</summary>
    private double? SampleSceneZ(double x, double y)
    {
        foreach (var m in _ctx.Meshes())
        {
            if (!m.Visible) continue;
            var b = m.Bounds;
            if (x < b.minX || x > b.maxX || y < b.minY || y > b.maxY) continue;
            if (!_samplers.TryGetValue(m, out var s)) { s = SurfaceUpdateEngine.MeshSampler2D.Build(m); _samplers[m] = s; }
            if (s != null && s.TrySample(x, y, out double z)) return z + m.Elevation;
        }
        var vb = _ctx.ViewBounds();
        double tol = vb != null && vb.Length == 4 ? Math.Max(vb[2] - vb[0], vb[3] - vb[1]) * 0.01 : 1.0;
        PointEntity? best = null; double bestD = tol;
        foreach (var p in _ctx.Points())
        {
            if (!p.Visible) continue;
            double d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
            if (d <= bestD) { bestD = d; best = p; }
        }
        return best?.Elevation;
    }

    private void OnStopPick(object? sender, RoutedEventArgs e) => StopPicking(save: true);

    private void StopPicking(bool save)
    {
        bool was = _picking;
        _picking = false;
        pickButton.IsEnabled = true;
        if (was && save)
        {
            if (Save()) statusText.Text = $"拾取结束，已保存 {_points.Count} 个见煤点";
        }
    }

    /// <summary>把视口当前选中的点(带高程)整批记为见煤点(弹一次窗定煤层+顶/底)。</summary>
    private async void OnCollectSelected(object? sender, RoutedEventArgs e)
    {
        var sel = _ctx.SelectedPoints();
        if (sel.Count == 0) { statusText.Text = "请先在视口选中要采集的点"; return; }
        if (_config.Seams.Count == 0) { statusText.Text = "请先「煤层结构…」预置煤层，再采集"; return; }
        try { EnsureBatch(); }
        catch (Exception ex) { statusText.Text = $"新建批次失败：{ex.Message}"; return; }
        var dlg = new PickHorizonDialog(_config, sel[0].X, sel[0].Y, sel[0].Elevation, _lastSeamCode, _lastHorizon);
        if (!await dlg.ShowDialog<bool>(this)) return;
        foreach (var p in sel)
            _points.Add(new PointRow { X = p.X, Y = p.Y, Z = p.Elevation, SeamCode = dlg.SeamCode, SeamName = dlg.SeamName, Horizon = dlg.Horizon, Remark = dlg.Remark });
        _lastSeamCode = dlg.SeamCode; _lastHorizon = dlg.Horizon;
        UpdateCurrentBatchCountDisplay();
        RedrawMarks(quiet: true);
        statusText.Text = $"已采集 {sel.Count} 个选中点为见煤点（{dlg.SeamName} · {dlg.Horizon}，共 {_points.Count} 点）；点「保存」入库";
    }

    // ─────────────────────────── 数据管理 / 保存 ───────────────────────────
    private async void OnManageData(object? sender, RoutedEventArgs e)
    {
        var dlg = new CurrentStateBatchManagerWindow(_conn);
        await dlg.ShowDialog(this);
        LoadBatches(_currentBatchId);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Save()) statusText.Text = $"✓ 已保存 {_points.Count} 个见煤点";
    }

    private bool Save()
    {
        if (_currentBatchId is not { } batch)
        {
            statusText.Text = "请先「新建批次」或「开始拾取」";
            return false;
        }
        if (_conn == null) { statusText.Text = "✗ 数据库不可用"; return false; }
        var pts = _points.Select(r => new CsPointRow
        {
            X = r.X, Y = r.Y, Z = r.Z,
            SeamCode = r.SeamCode, Horizon = r.Horizon,
            Remark = string.IsNullOrWhiteSpace(r.Remark) ? null : r.Remark,
        }).ToList();
        try { CsReplacePoints(_conn, batch, pts); }
        catch (Exception ex) { statusText.Text = $"✗ 入库失败：{ex.Message}"; return false; }
        UpdateCurrentBatchCountDisplay();
        return true;
    }

    // ─────────────────────────── CSV 模板 / 导入 / 导出 ───────────────────────────
    private async void OnDownloadTemplate(object? sender, RoutedEventArgs e)
    {
        var path = await _ctx.SaveTextAsync("保存现状点导入模板", "现状点模板.csv", CsCsvTemplate());
        if (path != null) statusText.Text = $"模板已生成：{path}";
    }

    private async void OnImportCsv(object? sender, RoutedEventArgs e)
    {
        var path = await _ctx.OpenFileAsync("选择现状点数据文件 (CSV)", new[] { "*.csv", "*.txt" });
        if (path == null) return;
        List<CsParsedPoint> parsed; string status;
        try { parsed = CsParseCsv(File.ReadAllText(path), out status); }
        catch (Exception ex) { statusText.Text = $"解析失败：{ex.Message}"; return; }
        if (parsed.Count == 0) { statusText.Text = status; return; }
        try { EnsureBatch(); }
        catch (Exception ex) { statusText.Text = $"新建批次失败：{ex.Message}"; return; }
        foreach (var p in parsed)
            _points.Add(new PointRow { X = p.X, Y = p.Y, Z = p.Z, Remark = p.Remark, SeamCode = p.SeamCode, SeamName = _config.NameFor(p.SeamCode), Horizon = p.Horizon });
        UpdateCurrentBatchCountDisplay();
        RedrawMarks(quiet: true);
        statusText.Text = $"导入 {parsed.Count} 点到当前批次（{status}）；点「保存」入库";
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_points.Count == 0) { statusText.Text = "当前批次没有现状点可导出"; return; }
        var rows = _points.Select(r => new CsPointRow { X = r.X, Y = r.Y, Z = r.Z, Remark = r.Remark, SeamCode = r.SeamCode, Horizon = r.Horizon }).ToList();
        var path = await _ctx.SaveTextAsync("导出现状点", "现状点.csv", CsExportCsv(rows));
        if (path != null) statusText.Text = $"已导出 {rows.Count} 点：{path}";
    }

    // ─────────────────────────── 建现状面 ───────────────────────────
    private void OnBuildSurface(object? sender, RoutedEventArgs e)
    {
        var pts = _points.Select(r => (r.X, r.Y, r.Z)).ToList();
        var (ok, msg, mesh) = CurrentStateSurfaceBuilder.Build(pts, SurfaceLayer);
        if (!ok || mesh == null) { statusText.Text = "✗ " + msg; return; }
        _ctx.AddMesh(mesh, true);
        statusText.Text = "✓ " + msg;
        _ctx.Status(statusText.Text);
    }

    /// <summary>见煤点行(X/Y/Z 由拾取写入, 只读显示; 备注可编辑)。</summary>
    public sealed class PointRow
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? SeamCode { get; set; }
        public string? SeamName { get; set; }
        public string? Horizon { get; set; }
        public string? Remark { get; set; }

        public string XDisp => X.ToString("F2", CultureInfo.InvariantCulture);
        public string YDisp => Y.ToString("F2", CultureInfo.InvariantCulture);
        public string ZDisp => Z.ToString("F2", CultureInfo.InvariantCulture);
    }
}
