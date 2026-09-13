using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.Road;

/// <summary>
/// 「中心线管理」窗口与宿主之间的<b>唯一</b>接口（忠实原 ICenterlineManagerHost；实体 handle 在 Kylin 就是 PolylineEntity 引用）。
/// 窗口只认这一组回调，不碰场景/数据库 —— 「读中线 / 删中线 / 建图」全留在主窗，与「手动标定线路」「基础道路网络构建」共用同一套口径。
/// </summary>
internal interface ICenterlineManagerHost
{
    IReadOnlyList<(PolylineEntity Handle, double[] Xyz)> ReadCenterlines();
    void SelectInViewport(IReadOnlyList<PolylineEntity> handles);
    void Highlight(IReadOnlyList<double[]> lines);
    int DeleteCenterlines(IReadOnlyList<PolylineEntity> handles, double totalLengthM);
    void PickInViewport(IReadOnlyList<PolylineEntity> preSelected, Action<IReadOnlyList<PolylineEntity>> onPicked);
    void SetCenterlineOnlySelection(bool enabled, IReadOnlyList<PolylineEntity> centerlineHandles, Action<IReadOnlyList<PolylineEntity>> onSelectionChanged);
    int WriteCenterlines(IReadOnlyList<double[]> lines, bool append);
    bool ArchiveAvailable { get; }
    IReadOnlyList<CenterlineSetRecord> ListArchives();
    long SaveArchive(string name, string? note);
    IReadOnlyList<double[]>? LoadArchiveGeometry(CenterlineSetRecord entity);
    void RenameArchive(CenterlineSetRecord entity, string name);
    void DeleteArchive(long id);
    void Echo(string message, bool warn = false);
}

/// <summary>「批量选择：只选中心线」的过滤器状态（挂在主窗的选集变更钩子上）。</summary>
internal sealed class CenterlineOnlySelectionFilter
{
    public HashSet<PolylineEntity> Allow = new();
    public Action<IReadOnlyList<PolylineEntity>>? OnChanged;
    public bool Busy;
}

/// <summary>
/// 「中心线管理」窗口 —— 原「删除道路中心线」升级而来：清单化（长度/顶点/纵坡/高程/起讫点/连通片/悬空），
/// 点表头排序、一键挑出碎线·悬空端·孤立线、多选批量删（删完当场重建路网）；存档页把整理干净的中线随工程持久化（V045）。
/// 忠实原 RoadLib.Views.CenterlineManagerWindow。
/// </summary>
internal sealed class CenterlineManagerWindow : Window
{
    public sealed class Row
    {
        public int No { get; init; }
        public PolylineEntity Handle { get; init; } = null!;
        public double[] Xyz { get; init; } = Array.Empty<double>();
        public double LengthM { get; init; }
        public int VertexCount { get; init; }
        public double MaxGradePct { get; init; }
        public double MinZ { get; init; }
        public double MaxZ { get; init; }
        public int ComponentId { get; init; }
        public int ComponentLineCount { get; init; }
        public int LooseEnds { get; init; }
        public double StartX { get; init; }
        public double StartY { get; init; }
        public double EndX { get; init; }
        public double EndY { get; init; }
        public string LengthText => LengthM.ToString("F1", CultureInfo.InvariantCulture);
        public string GradeText => MaxGradePct >= 999 ? "竖直" : MaxGradePct.ToString("F1", CultureInfo.InvariantCulture);
        public string ZText => $"{MinZ:F1} ~ {MaxZ:F1}";
        public string StartText => $"{StartX:F0}, {StartY:F0}";
        public string EndText => $"{EndX:F0}, {EndY:F0}";
        public string CompText => ComponentLineCount <= 1 ? $"#{ComponentId} 孤" : $"#{ComponentId}";
        public string LooseText => LooseEnds switch { 0 => "", 1 => "一端", _ => "两端" };
    }

    public sealed class ArchiveRow
    {
        public CenterlineSetRecord Entity { get; init; } = null!;
        public string Name => Entity.Name;
        public string CapturedAt => Entity.CapturedAt;
        public string LineCount => Entity.LineCount.ToString(CultureInfo.InvariantCulture);
        public string LengthKm => Entity.LengthKm.ToString("F2", CultureInfo.InvariantCulture);
        public string Source => Entity.Source;
        public string Note => Entity.Note ?? "";
    }

    private const string Caption = "中心线管理";
    private readonly ICenterlineManagerHost _host;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly ObservableCollection<ArchiveRow> _archives = new();
    private readonly DataGrid _list, _archiveList;
    private readonly TextBlock _footer, _archiveFooter;
    private readonly TextBox _shortBox, _nameBox, _noteBox;
    private readonly ToggleButton _batchToggle;
    private CenterlineInventoryResult _inv = new();
    private bool _busy;
    private bool _syncingSelection;

    public CenterlineManagerWindow(ICenterlineManagerHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Title = Caption;
        RoadUi.Place(this, 980, 660);
        MinWidth = 720; MinHeight = 420;
        _list = BuildList();
        _footer = RoadUi.Hint("", 11.5); _footer.Margin = new Thickness(6, 0, 6, 8);
        _archiveList = BuildArchiveList();
        _archiveFooter = RoadUi.Hint("", 11.5); _archiveFooter.Margin = new Thickness(6, 0, 6, 8);
        _shortBox = RoadUi.Box("50", 52, 24);
        _nameBox = RoadUi.Box("", 200, 24);
        _noteBox = RoadUi.Box("", 240, 24);
        _batchToggle = new ToggleButton { Content = "批量选择：只选中心线", MinWidth = 150, Height = 26, Margin = new Thickness(0, 2, 6, 2), Padding = new Thickness(8, 0) };
        _batchToggle.IsCheckedChanged += (_, _) => ToggleBatchSelect();
        var tabs = new TabControl { Margin = new Thickness(8) };
        tabs.Items.Add(new TabItem { Header = "当前中心线", Content = BuildCurrentTab() });
        tabs.Items.Add(new TabItem { Header = "存档", Content = BuildArchiveTab() });
        Content = tabs;
        Closed += (_, _) =>
        {
            try { _host.Highlight(Array.Empty<double[]>()); } catch { }
            try { _host.SetCenterlineOnlySelection(false, Array.Empty<PolylineEntity>(), _ => { }); } catch { }
        };
        Reload();
        ReloadArchives();
    }

    // ══════════════════ 当前中心线页 ══════════════════
    private Control BuildCurrentTab()
    {
        var top = new WrapPanel { Margin = new Thickness(4, 6, 4, 4) };
        top.Children.Add(RoadUi.Small("刷新", () => Reload()));
        top.Children.Add(RoadUi.Small("全选", () => { foreach (var r in _rows) if (!_list.SelectedItems.Contains(r)) _list.SelectedItems.Add(r); }));
        top.Children.Add(RoadUi.Small("反选", InvertSelection));
        top.Children.Add(RoadUi.Small("清空选择", () => _list.SelectedItems.Clear()));
        top.Children.Add(new Border { Width = 12 });
        top.Children.Add(RoadUi.Lbl("挑出："));
        top.Children.Add(RoadUi.Small("碎线 <", () => _ = SelectWhere(r => r.LengthM < ShortThreshold())));
        top.Children.Add(_shortBox);
        top.Children.Add(RoadUi.Lbl("m"));
        top.Children.Add(RoadUi.Small("悬空端点", () => _ = SelectWhere(r => r.LooseEnds > 0)));
        top.Children.Add(RoadUi.Small("孤立线", () => _ = SelectWhere(r => r.ComponentLineCount <= 1)));
        var bottom = new WrapPanel { Margin = new Thickness(4) };
        bottom.Children.Add(_batchToggle);
        bottom.Children.Add(RoadUi.Small("同步到视口选集", () => _ = SyncSelectionToViewport()));
        bottom.Children.Add(RoadUi.Small("视口点选追加…", PickMore));
        bottom.Children.Add(new Border { Width = 12 });
        bottom.Children.Add(RoadUi.Small("删除选中", () => _ = DeleteSelected()));
        return RoadUi.Rows(top, _list, bottom, _footer);
    }

    private DataGrid BuildList()
    {
        var dg = RoadUi.Table(new (string, string, double)[]
        {
            ("序", nameof(Row.No), 46), ("长度 m", nameof(Row.LengthText), 80), ("顶点", nameof(Row.VertexCount), 56), ("最大纵坡 %", nameof(Row.GradeText), 88),
            ("高程范围 m", nameof(Row.ZText), 132), ("起点 X,Y", nameof(Row.StartText), 132), ("终点 X,Y", nameof(Row.EndText), 132),
            ("连通片", nameof(Row.CompText), 68), ("悬空", nameof(Row.LooseText), 60),
        });
        // 点表头排序：显示列是格式化过的字符串，按它排会得到字典序 → 排序键指到原始数值。
        string[] sortKeys = { nameof(Row.No), nameof(Row.LengthM), nameof(Row.VertexCount), nameof(Row.MaxGradePct), nameof(Row.MinZ), nameof(Row.StartX), nameof(Row.EndX), nameof(Row.ComponentId), nameof(Row.LooseEnds) };
        for (int i = 0; i < dg.Columns.Count; i++) { dg.Columns[i].SortMemberPath = sortKeys[i]; dg.Columns[i].CanUserSort = true; }
        dg.CanUserSortColumns = true;
        dg.ItemsSource = _rows;
        dg.SelectionChanged += (_, _) => { if (!_syncingSelection) HighlightSelection(); };
        return dg;
    }

    private double ShortThreshold()
        => double.TryParse(_shortBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 50.0;

    private List<Row> SelectedRows() => _list.SelectedItems.OfType<Row>().ToList();

    private void SetSelected(IEnumerable<Row> rows)
    {
        _syncingSelection = true;
        try
        {
            _list.SelectedItems.Clear();
            foreach (var r in rows) _list.SelectedItems.Add(r);
        }
        finally { _syncingSelection = false; }
        HighlightSelection();
        if (_list.SelectedItems.Count > 0) _list.ScrollIntoView(_list.SelectedItems[^1], null);
    }

    /// <summary>重读图上中线 + 重算清单量（长度/纵坡/连通片/悬空端点），保持已选中的那几条仍选中。</summary>
    public void Reload()
    {
        var keepSelected = new HashSet<PolylineEntity>(SelectedRows().Select(r => r.Handle));
        _rows.Clear();
        IReadOnlyList<(PolylineEntity Handle, double[] Xyz)> src;
        try { src = _host.ReadCenterlines(); }
        catch (Exception ex) { _ = Warn($"读取中心线失败：{ex.Message}"); return; }
        var lines = new List<double[]>(src.Count);
        foreach (var s in src) lines.Add(s.Xyz);
        var inv = _inv = CenterlineInventory.Build(lines);
        int no = 1;
        foreach (var r in inv.Rows)
            _rows.Add(new Row
            {
                No = no++, Handle = src[r.Index].Handle, Xyz = src[r.Index].Xyz, LengthM = r.LengthM, VertexCount = r.VertexCount,
                MaxGradePct = r.MaxGradePct, MinZ = r.MinZ, MaxZ = r.MaxZ, ComponentId = r.ComponentId, ComponentLineCount = r.ComponentLineCount,
                LooseEnds = r.LooseEnds, StartX = r.StartX, StartY = r.StartY, EndX = r.EndX, EndY = r.EndY,
            });
        if (keepSelected.Count > 0) SetSelected(_rows.Where(r => keepSelected.Contains(r.Handle)));
        if (_batchToggle.IsChecked == true) ToggleBatchSelect();
        else UpdateFooter();
    }

    private void UpdateFooter()
    {
        string batch = _batchToggle.IsChecked == true ? "　|　批量选择已开：视口里框选/点选只会留下中心线，其余实体自动移出选集并同步勾进清单。" : "";
        if (_rows.Count == 0)
        {
            _footer.Text = "图层「点云_道路中心线」上没有中线。先「提取道路中心线」/「手动标定线路」，或在「存档」页载入一版。" + batch;
            return;
        }
        double km = _inv.TotalLengthM / 1000.0;
        int shorties = _rows.Count(r => r.LengthM < ShortThreshold());
        _footer.Text = $"共 {_rows.Count} 条 · 总长 {km:F2} km · 连通片 {_inv.ComponentCount} 个 · 悬空端点 {_inv.LooseEndCount} 个 · 短于 {ShortThreshold():F0}m 的 {shorties} 条　|　"
                       + "连通片按建网同口径（吸附 5m·立交 4m）算，不含建网那步 25m 缺口桥接 —— 故这里的片数 ≥「基础道路网络构建」回显的片数，差的那几片是靠桥接才连上的。" + batch;
    }

    private void InvertSelection()
    {
        var sel = new HashSet<Row>(SelectedRows());
        SetSelected(_rows.Where(r => !sel.Contains(r)));
    }

    private async Task SelectWhere(Func<Row, bool> pred)
    {
        var picked = _rows.Where(pred).ToList();
        SetSelected(picked);
        if (picked.Count == 0) await Info("没有符合条件的中线。");
    }

    private void HighlightSelection()
    {
        var lines = SelectedRows().Select(r => r.Xyz).ToList();
        try { _host.Highlight(lines); } catch { }
    }

    private async Task SyncSelectionToViewport()
    {
        var handles = SelectedRows().Select(r => r.Handle).ToList();
        if (handles.Count == 0) { await Info("先在清单里选中要同步的中线。"); return; }
        try
        {
            _host.SelectInViewport(handles);
            _host.Echo($"中心线管理：已把 {handles.Count} 条中线设为视口选集（其它命令即可直接吃这个选集）。");
        }
        catch (Exception ex) { await Warn($"同步选集失败：{ex.Message}"); }
    }

    private void PickMore()
    {
        if (_busy) return;
        _busy = true;
        var pre = SelectedRows().Select(r => r.Handle).ToList();
        try
        {
            _host.PickInViewport(pre, picked =>
            {
                _busy = false;
                var want = new HashSet<PolylineEntity>(picked ?? Array.Empty<PolylineEntity>());
                SetSelected(_rows.Where(r => want.Contains(r.Handle)));
                Activate();
            });
        }
        catch (Exception ex) { _busy = false; _ = Warn($"视口点选不可用：{ex.Message}"); }
    }

    private void ToggleBatchSelect()
    {
        bool on = _batchToggle.IsChecked == true;
        try
        {
            _host.SetCenterlineOnlySelection(on, on ? _rows.Select(r => r.Handle).ToList() : Array.Empty<PolylineEntity>(),
                picked =>
                {
                    var want = new HashSet<PolylineEntity>(picked ?? Array.Empty<PolylineEntity>());
                    SetSelected(_rows.Where(r => want.Contains(r.Handle)));
                });
            UpdateFooter();
        }
        catch (Exception ex)
        {
            _batchToggle.IsChecked = false;
            _ = Warn($"批量选择开关打不开：{ex.Message}");
        }
    }

    private async Task DeleteSelected()
    {
        var picked = SelectedRows();
        if (picked.Count == 0) { await Info("先在清单里选中要删的中线（可按住 Ctrl/Shift 多选）。"); return; }
        double total = picked.Sum(r => r.LengthM);
        string msg = $"将从「点云_道路中心线」删除 {picked.Count} 条中心线（总长 {total:F0} m）。\n\n"
                     + "· 删完按剩下的中线重建路网图；\n· 接在被删线上的联络段会变成悬空端点（清单「悬空」一列会亮出来），必要时一并删掉；\n"
                     + "· 误删可 Ctrl+Z 撤回，撤回后重点一次「基础道路网络构建」让路网跟上。\n\n继续？";
        if (!await Ask(msg)) { _host.Echo("中心线管理：已取消，未删任何线。"); return; }
        try
        {
            int n = _host.DeleteCenterlines(picked.Select(r => r.Handle).ToList(), total);
            _host.Highlight(Array.Empty<double[]>());
            Reload();
            if (n == 0) await Warn("一条都没删掉（实体已不在图上？）。");
        }
        catch (Exception ex) { await Warn($"删除失败：{ex.Message}"); }
    }

    // ══════════════════ 存档页 ══════════════════
    private Control BuildArchiveTab()
    {
        var top = new WrapPanel { Margin = new Thickness(4, 6, 4, 4) };
        top.Children.Add(RoadUi.Lbl("名称："));
        top.Children.Add(_nameBox);
        top.Children.Add(RoadUi.Lbl("备注："));
        top.Children.Add(_noteBox);
        top.Children.Add(RoadUi.Small("存为存档", () => _ = SaveArchive()));
        var bottom = new WrapPanel { Margin = new Thickness(4) };
        bottom.Children.Add(RoadUi.Small("载入（覆盖图层）", () => _ = LoadArchive(append: false)));
        bottom.Children.Add(RoadUi.Small("追加载入", () => _ = LoadArchive(append: true)));
        bottom.Children.Add(RoadUi.Small("改名", () => _ = RenameArchive()));
        bottom.Children.Add(RoadUi.Small("删除存档", () => _ = DeleteArchive()));
        bottom.Children.Add(RoadUi.Small("刷新", ReloadArchives));
        return RoadUi.Rows(top, _archiveList, bottom, _archiveFooter);
    }

    private DataGrid BuildArchiveList()
    {
        var dg = RoadUi.Table(new (string, string, double)[]
        {
            ("存档名", nameof(ArchiveRow.Name), 210), ("存档时刻", nameof(ArchiveRow.CapturedAt), 150), ("条数", nameof(ArchiveRow.LineCount), 66),
            ("总长 km", nameof(ArchiveRow.LengthKm), 84), ("来路", nameof(ArchiveRow.Source), 110), ("备注", nameof(ArchiveRow.Note), 300),
        }, multi: false);
        dg.ItemsSource = _archives;
        dg.SelectionChanged += (_, _) => { if (dg.SelectedItem is ArchiveRow a) { _nameBox.Text = a.Name; _noteBox.Text = a.Note; } };
        return dg;
    }

    private void ReloadArchives()
    {
        _archives.Clear();
        if (!_host.ArchiveAvailable)
        {
            _archiveFooter.Text = "工程库未连接 —— 存档读写不可用。中心线仍可在「当前中心线」页管理，只是存不下来。";
            return;
        }
        try
        {
            foreach (var e in _host.ListArchives()) _archives.Add(new ArchiveRow { Entity = e });
            _archiveFooter.Text = _archives.Count == 0
                ? "还没有存档。整理干净之后按「存为存档」留一版 —— 存的是中线几何本身（建网的进料），与「路网存档」的成品图各存各的：改口径重建网要回到这里。"
                : $"共 {_archives.Count} 份存档。载入（覆盖图层）会先清空「点云_道路中心线」再写入，可 Ctrl+Z 撤回；载入后重点一次「基础道路网络构建」让路网跟上。";
        }
        catch (Exception ex) { _archiveFooter.Text = $"读取存档失败：{ex.Message}"; }
    }

    private async Task SaveArchive()
    {
        if (!await RequireArchive()) return;
        string name = (_nameBox.Text ?? "").Trim();
        if (name.Length == 0) { await Info("先填存档名称（建议带时期，如「2026-06 提取+人工整理」）。"); return; }
        if (_rows.Count == 0) { await Info("图上没有中线可存。"); return; }
        try
        {
            long id = _host.SaveArchive(name, (_noteBox.Text ?? "").Trim());
            if (id <= 0) { await Warn("没有存下任何中线（图上无中线？）。"); return; }
            ReloadArchives();
        }
        catch (Exception ex) { await Warn($"存档失败：{ex.Message}"); }
    }

    private async Task LoadArchive(bool append)
    {
        if (!await RequireArchive()) return;
        if (_archiveList.SelectedItem is not ArchiveRow a) { await Info("先选中一份存档。"); return; }
        string msg = append
            ? $"把存档「{a.Name}」的 {a.LineCount} 条中线【追加】写进「点云_道路中心线」。\n\n· 追加不动图上现有中线，可能与之重叠（重叠会在建网时被去重）；\n· 可 Ctrl+Z 撤回。\n\n继续？"
            : $"用存档「{a.Name}」的 {a.LineCount} 条中线【覆盖】「点云_道路中心线」。\n\n· 图上现有中线会先被删掉；\n· 可 Ctrl+Z 撤回；\n· 载入后重点一次「基础道路网络构建」让路网跟上。\n\n继续？";
        if (!await Ask(msg)) return;
        try
        {
            var lines = _host.LoadArchiveGeometry(a.Entity);
            if (lines is null || lines.Count == 0) { await Warn("存档几何解不出来（数据损坏？），图上中线未动。"); return; }
            _host.WriteCenterlines(lines, append);
            Reload();
        }
        catch (Exception ex) { await Warn($"载入失败：{ex.Message}"); }
    }

    private async Task RenameArchive()
    {
        if (!await RequireArchive()) return;
        if (_archiveList.SelectedItem is not ArchiveRow a) { await Info("先选中一份存档。"); return; }
        string name = (_nameBox.Text ?? "").Trim();
        if (name.Length == 0) { await Info("名称不能为空。"); return; }
        try { _host.RenameArchive(a.Entity, name); ReloadArchives(); }
        catch (Exception ex) { await Warn($"改名失败：{ex.Message}"); }
    }

    private async Task DeleteArchive()
    {
        if (!await RequireArchive()) return;
        if (_archiveList.SelectedItem is not ArchiveRow a) { await Info("先选中一份存档。"); return; }
        if (!await Ask($"删除存档「{a.Name}」？不可撤销（图上的中线不受影响）。")) return;
        try { _host.DeleteArchive(a.Entity.Id); ReloadArchives(); }
        catch (Exception ex) { await Warn($"删除失败：{ex.Message}"); }
    }

    private async Task<bool> RequireArchive()
    {
        if (_host.ArchiveAvailable) return true;
        await Info("工程库未连接，存档读写不可用。");
        return false;
    }

    private Task Info(string msg) => RoadUi.Info(this, Caption, msg);
    private Task Warn(string msg) { _host.Echo($"中心线管理：{msg}", warn: true); return RoadUi.Info(this, Caption, msg); }
    private Task<bool> Ask(string msg) => RoadUi.Confirm(this, Caption, msg);
}
