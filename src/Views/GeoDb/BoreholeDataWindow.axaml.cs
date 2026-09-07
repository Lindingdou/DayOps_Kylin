using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 开孔坐标管理窗口: 直接接 borehole 真库。CSV 批量导入(含下载模板)、DataGrid 增删改即时存库、按孔号/类别筛选、导出 CSV。
/// 导入/模板解析走 <see cref="GeoDbViews"/> BoreholeImport* 系列(与「导入钻孔数据」共用)。
/// 错误走状态栏; 仅删除前确认一声。
/// </summary>
public partial class BoreholeDataWindow : Window
{
    private readonly GeoDbContext _ctx;
    public ObservableCollection<GeoDbViews.BoreholeRow> Records { get; } = new();
    private string _filter = "";

    /// <summary>XAML 编译器/设计器用(运行时一律走带 ctx 的构造)。</summary>
    public BoreholeDataWindow() { _ctx = null!; InitializeComponent(); }

    public BoreholeDataWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        filterBox.TextChanged += OnFilterChanged;
        Opened += (_, _) => LoadData();
    }


    private void LoadData()
    {
        try
        {
            Records.Clear();
            foreach (var b in GeoDbViews.LoadBoreholes(_ctx.Conn)) Records.Add(b);
            ApplyFilter();
            UpdateStatus();
        }
        catch (Exception ex) { statusText.Text = $"加载失败：{ex.Message}"; }
    }

    private bool FilterPredicate(GeoDbViews.BoreholeRow b)
        => string.IsNullOrWhiteSpace(_filter)
           || (b.HoleId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
           || (b.Category?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private int _shown;
    private void ApplyFilter()
    {
        var list = Records.Where(FilterPredicate).ToList();
        _shown = list.Count;
        boreholeGrid.ItemsSource = list;
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        _filter = filterBox.Text?.Trim() ?? "";
        ApplyFilter();
        UpdateStatus();
    }

    private void UpdateStatus()
        => statusText.Text = _shown != Records.Count ? $"共 {Records.Count} 个钻孔，筛选显示 {_shown}" : $"共 {Records.Count} 个钻孔";

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        // 先只加内存行(Id=0), 待用户编辑提交时再 Insert; 避免"新增后未编辑即关窗"在库里残留 (0,0) 废孔。
        var b = new GeoDbViews.BoreholeRow { HoleId = "新孔", CoordFilled = "原始" };
        Records.Add(b);
        ApplyFilter();
        boreholeGrid.SelectedItem = b;
        boreholeGrid.ScrollIntoView(b, null);
        UpdateStatus();
        statusText.Text = "已新增一行（尚未入库）：请编辑孔号与坐标，提交该行即存库";
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (boreholeGrid.SelectedItem is not GeoDbViews.BoreholeRow b) { statusText.Text = "请先选择要删除的行"; return; }
        if (b.Id <= 0)   // 未入库的新行: 仅从内存移除
        {
            Records.Remove(b); ApplyFilter(); UpdateStatus();
            statusText.Text = "已移除未入库的新行";
            return;
        }
        bool yes = await BoreholeMsgBox.ConfirmAsync(this, "确认删除", $"确定删除钻孔「{b.HoleId}」吗？此操作不可撤销。");
        if (!yes) return;
        try
        {
            GeoDbViews.DeleteBorehole(_ctx.Conn, b.Id);
            Records.Remove(b); ApplyFilter(); UpdateStatus();
            statusText.Text = $"已删除「{b.HoleId}」";
        }
        catch (Exception ex) { statusText.Text = $"删除失败：{ex.Message}"; }
    }

    // 经距/纬距/孔口高程/总孔深为数值列; 输入非数字时撤销提交并提示。
    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        var header = e.Column.Header as string;
        bool required = header is "经距 X" or "纬距 Y";
        bool optional = header is "孔口高程" or "总孔深";
        if (!required && !optional) return;
        var text = (e.EditingElement as TextBox)?.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            if (required) { e.Cancel = true; statusText.Text = $"「{header}」必须填写数字，已撤销本次编辑（按 Esc 还原）"; }
            return;
        }
        if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
            && !double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out _))
        {
            e.Cancel = true;
            statusText.Text = $"「{header}」必须为数字（“{text}”无效），已撤销本次编辑（按 Esc 还原）";
        }
    }

    private void OnRowEditEnded(object? sender, DataGridRowEditEndedEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.DataContext is not GeoDbViews.BoreholeRow b) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (b.Id <= 0) { GeoDbViews.InsertBorehole(_ctx.Conn, b); statusText.Text = $"已入库新孔「{b.HoleId}」"; }
                else { GeoDbViews.UpdateBorehole(_ctx.Conn, b); statusText.Text = $"已保存「{b.HoleId}」"; }
            }
            catch (Exception ex) { statusText.Text = $"保存失败：{ex.Message}"; }
        }, DispatcherPriority.Background);
    }

    // ─────────────────────────── 导入 / 模板 ───────────────────────────
    private async void OnDownloadTemplateClick(object? sender, RoutedEventArgs e)
    {
        string? path;
        try { path = await _ctx.SaveTextAsync("保存开孔坐标导入模板", "开孔坐标导入模板.csv", GeoDbViews.BoreholeCsvTemplate()); }
        catch (Exception ex) { statusText.Text = $"模板生成失败：{ex.Message}"; return; }
        if (path == null) return;
        statusText.Text = $"模板已生成：{path}（按列填好后点「导入 CSV」导入）";
        bool open = await BoreholeMsgBox.ConfirmAsync(this, "模板已生成",
            $"导入模板已生成：\n{path}\n\n请按表头各列填入开孔坐标（必填：孔号、经距X、纬距Y），保存后回到本窗口点「导入 CSV…」。\n\n是否现在打开该模板？");
        if (open)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { statusText.Text = $"已生成模板，但打开失败：{ex.Message}"; }
        }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var path = await _ctx.OpenFileAsync("选择开孔坐标数据文件", new[] { "*.csv" });
        if (path == null) return;
        System.Collections.Generic.List<System.Collections.Generic.List<string>> records;
        try { records = GeoDbViews.BoreholeReadCsvRecords(File.ReadAllText(path)); }
        catch (Exception ex) { statusText.Text = $"解析失败：{ex.Message}"; return; }

        var o = GeoDbViews.BoreholeImportDirect(_ctx.Conn, records, overwriteBox.IsChecked == true, out string message);
        if (o == null) { statusText.Text = message; return; }
        LoadData();
        var msg = $"导入完成：新增 {o.Inserted}，更新 {o.Updated}，跳过 {o.Skipped}（缺必填/重复/未勾选覆盖）";
        if (o.Failed > 0) msg += $"；失败 {o.Failed}，首条错误：{o.FirstError}";
        statusText.Text = msg;
    }

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await _ctx.SaveTextAsync("导出钻孔数据", "boreholes.csv", GeoDbViews.BoreholeExportCsv(Records));
            if (path == null) return;
            statusText.Text = $"已导出 {Records.Count} 行到 {Path.GetFileName(path)}";
        }
        catch (Exception ex) { statusText.Text = $"导出失败：{ex.Message}"; }
    }
}
