using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System.Data.Common;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Views.GeoDb;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 补勘钻孔写实(原 MeshEditLib.ModelUpdate.SupplementaryBoreholeWindow): 左选/增补勘孔, 中交互柱状图(按煤层顶/底板画层带),
/// 右逐煤层录入顶底板高程。支持手工录入 + CSV 批量导入(原 Excel)/导出; 保存到独立写实表(supplementary_*); 按批次(系统唯一时间标签)组织。
/// Kylin 追加: 「拾取孔位」从视口取 X/Y、「展绘」把当前批次孔位/层位/孔柱入场景。
/// </summary>
public partial class SupplementaryBoreholeWindow : Window
{
    private readonly ModelingContext _ctx;
    private readonly DbConnection? _conn;

    private SeamStructureConfig _config = new();
    private List<SeamDefRow> _defs = new();
    private List<SupHoleRow> _holes = new();
    private ObservableCollection<SeamRow> _seamRows = new();
    private SupHoleRow? _currentHole;
    private string? _selectedSeamCode;
    private long? _currentBatchId;      // 当前写实批次(新增/导入归入)
    private bool _loadingBatches;       // 批次下拉重填时抑制 SelectionChanged
    private bool _loadingList;

    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#222222"));

    // 柱状图布局
    private const double TopMargin = 28, AxisX = 66, BarX = 98, BarW = 128;

    /// <summary>XAML 编译器/设计器用。</summary>
    public SupplementaryBoreholeWindow() { _ctx = null!; InitializeComponent(); }

    public SupplementaryBoreholeWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        _conn = ctx.Conn();
        InitializeComponent();
        _config = SeamStructureConfig.LoadOrDefault(_conn);
        _defs = SafeDefs();
        filterBox.TextChanged += OnFilterChanged;
        Opened += (_, _) => LoadBatches(null);   // 选中最新批次并加载其孔
    }

    // ─────────────────────────── 批次 ───────────────────────────
    private void LoadBatches(long? selectId)
    {
        _loadingBatches = true;
        List<SupBatchRow> batches;
        try { batches = _conn == null ? new() : SupAllBatches(_conn); }
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
        else if (items.Count > 0) batchCombo.SelectedIndex = 0;   // 最新批次
        else { _currentBatchId = null; _holes = new(); RefreshList(); }   // 无批次: 清空
    }

    private static string BatchLabel(SupBatchRow b) => $"{b.Name}  ({b.HoleCount}孔)";

    private void OnBatchChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingBatches) return;
        _currentBatchId = (batchCombo.SelectedItem as ComboBoxItem)?.Tag is SupBatchRow b ? b.Id : (long?)null;
        _currentHole = null;
        ClearEditor();
        LoadHoles();
    }

    private void OnNewBatch(object? sender, RoutedEventArgs e)
    {
        if (_conn == null) { statusText.Text = "数据库不可用"; return; }
        try
        {
            long id = SupCreateBatch(_conn, "手工录入");
            LoadBatches(id);   // 选中新批次(空孔)
            statusText.Text = "已新建写实批次；点「新增孔」开始录入";
        }
        catch (Exception ex) { statusText.Text = $"新建批次失败：{ex.Message}"; }
    }

    private async void OnManageData(object? sender, RoutedEventArgs e)
    {
        var dlg = new SupplementaryBatchManagerWindow(_conn);
        await dlg.ShowDialog(this);
        // 管理窗可能删/改批次, 回来刷新下拉+当前批次孔
        LoadBatches(_currentBatchId);
    }

    /// <summary>确保有当前批次: 无则新建「手工录入」批次并选中, 返回其 id。</summary>
    private long EnsureBatch()
    {
        if (_currentBatchId is { } id) return id;
        if (_conn == null) throw new InvalidOperationException("数据库不可用");
        long nid = SupCreateBatch(_conn, "手工录入");
        LoadBatches(nid);
        return nid;
    }

    /// <summary>把下拉里当前批次项的孔数显示刷新为当前列表孔数(不重载)。</summary>
    private void UpdateCurrentBatchCountDisplay()
    {
        if (batchCombo.SelectedItem is ComboBoxItem it && it.Tag is SupBatchRow b)
        {
            b.HoleCount = _holes.Count;
            it.Content = BatchLabel(b);
        }
    }

    // ─────────────────────────── 孔列表 ───────────────────────────
    private void LoadHoles()
    {
        try { _holes = _conn == null ? new() : SupAllHoles(_conn, _currentBatchId); }
        catch (Exception ex) { statusText.Text = $"加载失败：{ex.Message}"; return; }
        RefreshList();
    }

    private void RefreshList(SupHoleRow? select = null)
    {
        string f = filterBox.Text?.Trim() ?? "";
        _loadingList = true;
        var items = new List<ListBoxItem>();
        ListBoxItem? toSelect = null;
        foreach (var h in _holes)
        {
            if (f.Length > 0 && !(h.HoleId?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            var item = new ListBoxItem { Content = string.IsNullOrEmpty(h.HoleId) ? "(未命名)" : h.HoleId, Tag = h };
            items.Add(item);
            if (select != null && ReferenceEquals(h, select)) toSelect = item;
        }
        holeList.ItemsSource = items;
        _loadingList = false;
        if (toSelect != null) holeList.SelectedItem = toSelect;
        statusText.Text = $"共 {_holes.Count} 个补勘孔" + (f.Length > 0 ? $"，筛选 {items.Count}" : "");
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => RefreshList(_currentHole);

    private void OnHoleSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingList) return;
        if (holeList.SelectedItem is ListBoxItem item && item.Tag is SupHoleRow h)
            LoadHoleIntoEditor(h);
    }

    private void OnAddHole(object? sender, RoutedEventArgs e)
    {
        long batchId;
        try { batchId = EnsureBatch(); }   // 确保有当前批次(无则新建并选中)
        catch (Exception ex) { statusText.Text = $"新建批次失败：{ex.Message}"; return; }
        var hole = new SupHoleRow { HoleId = UniqueHoleId(), BatchId = batchId };
        _holes.Add(hole);
        RefreshList(hole);   // 选中即触发 LoadHoleIntoEditor
        holeIdBox.Focus(); holeIdBox.SelectAll();
        statusText.Text = "已新增补勘孔（填好信息后点「保存」入库）";
    }

    private async void OnDeleteHole(object? sender, RoutedEventArgs e)
    {
        if (_currentHole is null) { statusText.Text = "请先在左侧选择要删除的补勘孔"; return; }
        var h = _currentHole;
        if (h.Id > 0)
        {
            if (!await BoreholeMsgBox.ConfirmAsync(this, "删除确认", $"确认删除补勘孔「{h.HoleId}」及其全部煤层顶底板记录？")) return;
            try { if (_conn != null) SupDeleteHole(_conn, h.Id); }
            catch (Exception ex) { statusText.Text = $"删除失败：{ex.Message}"; return; }
        }
        _holes.Remove(h);
        _currentHole = null;
        ClearEditor();
        RefreshList();
        UpdateCurrentBatchCountDisplay();
        statusText.Text = $"已删除补勘孔「{h.HoleId}」";
    }

    private string UniqueHoleId()
    {
        // hole_id 全局唯一(跨批次), 既查库也查当前内存里未保存的孔。
        for (int i = 1; ; i++)
        {
            string name = $"补勘孔{i}";
            bool inMem = _holes.Any(h => string.Equals(h.HoleId, name, StringComparison.OrdinalIgnoreCase));
            if (!inMem && (_conn == null || SupGetHoleByHoleId(_conn, name) == null)) return name;
        }
    }

    // ─────────────────────────── 录入面板 ───────────────────────────
    private void LoadHoleIntoEditor(SupHoleRow h)
    {
        _currentHole = h;
        holeIdBox.Text = h.HoleId;
        xBox.Text = h.X.ToString(CultureInfo.InvariantCulture);
        yBox.Text = h.Y.ToString(CultureInfo.InvariantCulture);
        zBox.Text = h.ZCollar?.ToString(CultureInfo.InvariantCulture) ?? "";

        var horizons = h.Id > 0 && _conn != null
            ? SupHorizonsByHole(_conn, h.Id).ToDictionary(z => z.SeamCode, z => z)
            : new Dictionary<string, SupHorizonRow>();

        _seamRows = new ObservableCollection<SeamRow>();
        foreach (var seam in _config.Seams)
        {
            var row = new SeamRow { SeamCode = seam.Code, SeamName = seam.Name };
            if (horizons.TryGetValue(seam.Code, out var z))
            {
                row.RoofText = Fmt(z.RoofElevation);
                row.FloorText = Fmt(z.FloorElevation);
            }
            _seamRows.Add(row);
        }
        seamGrid.ItemsSource = _seamRows;
        _selectedSeamCode = null;
        DrawColumn();
    }

    private void ClearEditor()
    {
        holeIdBox.Text = xBox.Text = yBox.Text = zBox.Text = "";
        _seamRows = new ObservableCollection<SeamRow>();
        seamGrid.ItemsSource = _seamRows;
        DrawColumn();
    }

    private void OnSeamCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        // 绑定在本事件返回后才提交, 故延迟到提交完成再刷新只读列(厚度)与柱状图。
        Dispatcher.UIThread.Post(DrawColumn, DispatcherPriority.Background);
    }

    private void OnSeamRowSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selectedSeamCode = (seamGrid.SelectedItem as SeamRow)?.SeamCode;
        DrawColumn();
    }

    private void OnRefreshPreview(object? sender, RoutedEventArgs e)
    {
        try { seamGrid.CommitEdit(); } catch { }
        DrawColumn();
    }

    private async void OnPickPosition(object? sender, RoutedEventArgs e)
    {
        if (_currentHole is null) { statusText.Text = "请先选择或新增补勘孔"; return; }
        statusText.Text = "在视口点一下取孔位（Esc 取消）";
        var p = await _ctx.PickPointAsync("拾取补勘孔孔位");
        if (p == null) { statusText.Text = "已取消拾取"; return; }
        xBox.Text = p.Value.x.ToString("F2", CultureInfo.InvariantCulture);
        yBox.Text = p.Value.y.ToString("F2", CultureInfo.InvariantCulture);
        Activate();
        DrawColumn();
        statusText.Text = $"孔位已取：X {xBox.Text}  Y {yBox.Text}（点「保存」入库）";
    }

    // ─────────────────────────── 保存 ───────────────────────────
    private void OnSave(object? sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (_currentHole is null) { statusText.Text = "请先选择或新增补勘孔"; return; }
        if (_conn == null) { statusText.Text = "✗ 数据库不可用"; return; }
        try { seamGrid.CommitEdit(); } catch { }

        string holeId = holeIdBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(holeId)) { statusText.Text = "✗ 缺孔号"; return; }
        double? x = TryNum(xBox.Text), y = TryNum(yBox.Text), z = TryNum(zBox.Text);
        if (x is null) { statusText.Text = "✗ 经距X 非数字"; return; }
        if (y is null) { statusText.Text = "✗ 纬距Y 非数字"; return; }

        // 孔号唯一性(与其它孔冲突则拒绝)
        try
        {
            var other = SupGetHoleByHoleId(_conn, holeId);
            if (other != null && other.Id != _currentHole.Id) { statusText.Text = $"✗ 孔号「{holeId}」已存在"; return; }
        }
        catch { /* 查询失败不阻断, 交下方入库报错 */ }

        _currentHole.HoleId = holeId;
        _currentHole.X = x.Value; _currentHole.Y = y.Value; _currentHole.ZCollar = z;
        if (_currentHole.BatchId is null) _currentHole.BatchId = _currentBatchId;

        try
        {
            if (_currentHole.Id <= 0) SupInsertHole(_conn, _currentHole);
            else SupUpdateHole(_conn, _currentHole);

            var horizons = new List<SupHorizonRow>();
            int order = 0;
            foreach (var row in _seamRows)
            {
                double? rf = row.Roof, fl = row.Floor;
                if (rf != null || fl != null)
                    horizons.Add(new SupHorizonRow { SeamCode = row.SeamCode, RoofElevation = rf, FloorElevation = fl, SortOrder = order });
                order++;
            }
            SupReplaceHorizons(_conn, _currentHole.Id, horizons);
        }
        catch (Exception ex) { statusText.Text = $"✗ 入库失败：{ex.Message}"; return; }

        RefreshList(_currentHole);
        UpdateCurrentBatchCountDisplay();
        statusText.Text = $"✓ 已保存补勘孔「{holeId}」（{_seamRows.Count(r => r.Roof != null || r.Floor != null)} 层有高程）"
                        + "；三维面重建请到功能区「更新地质模型」组";
    }

    // ─────────────────────────── 煤层结构 ───────────────────────────
    private async void OnEditStructure(object? sender, RoutedEventArgs e)
    {
        var dlg = new SeamStructureDialog(_conn, _config);
        if (await dlg.ShowDialog<bool>(this) && dlg.Result != null)
        {
            _config = dlg.Result;
            _config.Save();
            if (_currentHole != null) LoadHoleIntoEditor(_currentHole);
            else ClearEditor();
            statusText.Text = $"✓ 煤层结构已更新并记住（{_config.Seams.Count} 层）";
        }
    }

    // ─────────────────────────── CSV 模板 / 导入 / 导出 ───────────────────────────
    private async void OnDownloadTemplate(object? sender, RoutedEventArgs e)
    {
        if (_config.Seams.Count == 0) { statusText.Text = "请先在「煤层结构…」配置煤层，再下载模板"; return; }
        string? path;
        try { path = await _ctx.SaveTextAsync("保存补勘钻孔写实导入模板", "补勘钻孔写实模板.csv", SupCsvTemplate(_config.AsPairs())); }
        catch (Exception ex) { statusText.Text = $"模板生成失败：{ex.Message}"; return; }
        if (path == null) return;
        statusText.Text = $"模板已生成：{path}";
        await BoreholeMsgBox.InfoAsync(this, "模板已生成", $"模板已生成：\n{path}\n\n列随当前煤层结构生成（CSV）。");
    }

    private async void OnImportCsv(object? sender, RoutedEventArgs e)
    {
        if (_config.Seams.Count == 0) { statusText.Text = "请先在「煤层结构…」配置煤层，再导入"; return; }
        if (_conn == null) { statusText.Text = "✗ 数据库不可用"; return; }
        var path = await _ctx.OpenFileAsync("选择补勘钻孔写实数据文件 (CSV)", new[] { "*.csv", "*.txt" });
        if (path == null) return;

        List<SupParsedHole> parsed;
        string status;
        try { parsed = SupParseCsv(File.ReadAllText(path), _config.AsPairs(), out status); }
        catch (Exception ex) { statusText.Text = $"解析失败：{ex.Message}"; return; }
        if (parsed.Count == 0) { statusText.Text = status; return; }

        var (batchId, ins, upd, fail) = SupImportParsed(_conn, parsed, _config);
        LoadBatches(batchId ?? _currentBatchId);   // 选中导入批次并显示其孔
        statusText.Text = $"导入完成：新增 {ins}，更新 {upd}" + (fail > 0 ? $"，失败 {fail}" : "") + $"（{status}）";
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_conn == null) { statusText.Text = "✗ 数据库不可用"; return; }
        if (_holes.Count == 0) { statusText.Text = "当前批次没有补勘孔可导出"; return; }
        var path = await _ctx.SaveTextAsync("导出补勘钻孔写实", "补勘钻孔写实.csv", SupExportCsv(_conn, _currentBatchId, _config.AsPairs()));
        if (path != null) statusText.Text = $"已导出 {_holes.Count} 孔：{path}";
    }

    // ─────────────────────────── 展绘 ───────────────────────────
    private void OnDraw(object? sender, RoutedEventArgs e)
    {
        if (_conn == null) { statusText.Text = "✗ 数据库不可用"; return; }
        if (_holes.Count == 0) { statusText.Text = "当前批次没有补勘孔可展绘"; return; }
        var byHole = new Dictionary<long, List<SupHorizonRow>>();
        foreach (var h in _holes) if (h.Id > 0) byHole[h.Id] = SupHorizonsByHole(_conn, h.Id);
        double minX = _holes.Min(h => h.X), maxX = _holes.Max(h => h.X), minY = _holes.Min(h => h.Y), maxY = _holes.Max(h => h.Y);
        double span = Math.Max(maxX - minX, maxY - minY);
        double size = Math.Clamp(span * 0.01, 1.0, 20.0);
        var ents = SupBuildHoleEntities(_holes, byHole, _defs, size);
        _ctx.RemoveLayerEntities(SupDrawLayer);
        _ctx.AddEntities(ents, SupDrawLayer, new[] { minX - size * 5, minY - size * 5, maxX + size * 5, maxY + size * 5 });
        statusText.Text = $"已展绘 {_holes.Count} 个补勘孔（{ents.Count} 实体，图层「{SupDrawLayer}」）";
        _ctx.Status(statusText.Text);
    }

    // ─────────────────────────── 柱状图绘制 ───────────────────────────
    private void DrawColumn()
    {
        columnCanvas.Children.Clear();
        if (_currentHole is null) { headerText.Text = "（选择或新增左侧补勘钻孔）"; return; }

        double? collar = TryNum(zBox.Text);
        headerText.Text = $"{holeIdBox.Text}    X {xBox.Text}  Y {yBox.Text}"
                        + (collar is { } c ? $"    孔口 {c:F1}m" : "");

        // 收集有高程的层带
        var bands = new List<(SeamRow row, double? roof, double? floor)>();
        var zs = new List<double>();
        foreach (var r in _seamRows)
        {
            if (r.Roof is null && r.Floor is null) continue;
            bands.Add((r, r.Roof, r.Floor));
            if (r.Roof is { } rr) zs.Add(rr);
            if (r.Floor is { } ff) zs.Add(ff);
        }
        if (collar is { } cc) zs.Add(cc);

        if (zs.Count == 0)
        {
            AddText(BarX, TopMargin, "（该孔尚无顶/底板高程，右侧逐层填入后自动成图）", 12, TextBrush);
            columnCanvas.Width = 420; columnCanvas.Height = 80;
            return;
        }

        double zHi = zs.Max(), zLo = zs.Min();
        double range = Math.Max(zHi - zLo, 1e-3);
        double pxPerM = Math.Min(Math.Max(560.0 / range, 0.8), 12.0);
        double barTop = TopMargin, barBot = TopMargin + range * pxPerM;

        columnCanvas.Width = BarX + BarW + 230;
        columnCanvas.Height = barBot + TopMargin;

        double Y(double z) => barTop + (zHi - z) * pxPerM;

        // 标高刻度轴
        double step = NiceTick(range);
        double first = Math.Ceiling(zLo / step) * step;
        for (double zt = first; zt <= zHi + 1e-6; zt += step)
        {
            double y = Y(zt);
            AddLine(AxisX - 5, y, AxisX, y, Brushes.Gray);
            AddText(2, y - 8, zt.ToString("0.#"), 10, Brushes.Gray);
        }
        AddLine(AxisX, barTop, AxisX, barBot, Brushes.Gray);
        AddText(2, barTop - 18, "标高(m)", 10, Brushes.Gray);

        // 孔口 ▽
        if (collar is { } cz)
        {
            double y = Y(cz);
            AddText(BarX + BarW / 2 - 6, y - 16, "▽", 12, TextBrush);
            AddLine(BarX, y, BarX + BarW, y, Brushes.DimGray);
        }

        // 逐层
        foreach (var (row, roof, floor) in bands)
        {
            var (r, g, b) = SeamPaletteColor(_defs, row.SeamCode);
            var fill = Color.FromRgb(r, g, b);
            bool selected = row.SeamCode == _selectedSeamCode;

            if (roof is { } rz && floor is { } fz && rz > fz)
            {
                double yT = Y(rz), yB = Y(fz);
                var rect = new Rectangle
                {
                    Width = BarW, Height = Math.Max(yB - yT, 2),
                    Fill = new SolidColorBrush(fill),
                    Stroke = selected ? new SolidColorBrush(Color.FromRgb(0x00, 0xBF, 0xFE)) : Brushes.Black,
                    StrokeThickness = selected ? 2.4 : 0.8,
                    Cursor = new Cursor(StandardCursorType.Hand), Tag = row,
                };
                ToolTip.SetTip(rect, $"{row.SeamName}\n顶板 {rz:F2} / 底板 {fz:F2}  厚 {rz - fz:F2} m");
                rect.PointerPressed += OnBandClick;
                Canvas.SetLeft(rect, BarX); Canvas.SetTop(rect, yT);
                columnCanvas.Children.Add(rect);
                AddLine(BarX + BarW, (yT + yB) / 2, BarX + BarW + 10, (yT + yB) / 2, Brushes.Gray);
                AddText(BarX + BarW + 12, (yT + yB) / 2 - 9,
                    $"{row.SeamName}  顶{rz:F1} 底{fz:F1} 厚{rz - fz:F2}", 11, TextBrush);
            }
            else
            {
                // 只有一个高程: 画虚线标记
                double z = roof ?? floor!.Value;
                double y = Y(z);
                var line = new Line
                {
                    StartPoint = new Point(BarX, y), EndPoint = new Point(BarX + BarW, y),
                    Stroke = new SolidColorBrush(fill), StrokeThickness = 2,
                    StrokeDashArray = new AvaloniaList<double> { 3, 2 }, Cursor = new Cursor(StandardCursorType.Hand), Tag = row,
                };
                line.PointerPressed += OnBandClick;
                columnCanvas.Children.Add(line);
                AddText(BarX + BarW + 12, y - 9,
                    $"{row.SeamName}  {(roof != null ? "顶" : "底")}{z:F1}（缺另一面）", 11, TextBrush);
            }
        }
    }

    private void OnBandClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control fe && fe.Tag is SeamRow row)
        {
            seamGrid.SelectedItem = row;
            seamGrid.ScrollIntoView(row, null);
        }
        e.Handled = true;
    }

    // ─────────────────────────── 工具 ───────────────────────────
    private static double NiceTick(double range)
    {
        double raw = range / 6.0;
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return step * mag;
    }

    private static double? TryNum(string? s) => BoreholeParseNum(s);

    private static string Fmt(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    private List<SeamDefRow> SafeDefs()
    {
        try { return _conn == null ? new() : LoadSeamDefs(_conn); }
        catch { return new(); }
    }

    private void AddLine(double x1, double y1, double x2, double y2, IBrush stroke)
        => columnCanvas.Children.Add(new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = stroke, StrokeThickness = 0.8 });

    private void AddText(double x, double y, string text, double size, IBrush brush)
    {
        var t = new TextBlock { Text = text, FontSize = size, Foreground = brush };
        Canvas.SetLeft(t, x); Canvas.SetTop(t, y); columnCanvas.Children.Add(t);
    }

    /// <summary>右侧煤层录入行(顶/底板文本可编辑, 厚度只读)。</summary>
    public sealed class SeamRow : INotifyPropertyChanged
    {
        private string _roofText = "", _floorText = "";
        public string SeamCode { get; set; } = "";
        public string SeamName { get; set; } = "";
        public string RoofText { get => _roofText; set { _roofText = value ?? ""; OnChanged(nameof(RoofText)); OnChanged(nameof(ThicknessText)); } }
        public string FloorText { get => _floorText; set { _floorText = value ?? ""; OnChanged(nameof(FloorText)); OnChanged(nameof(ThicknessText)); } }

        public double? Roof => TryNum(RoofText);
        public double? Floor => TryNum(FloorText);

        public string ThicknessText =>
            Roof is { } r && Floor is { } f ? (r - f).ToString("0.##", CultureInfo.InvariantCulture) : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
