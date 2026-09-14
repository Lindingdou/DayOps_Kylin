using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 导入钻孔数据(CSV): 下载模板 → 选文件 → 按表头映射 → 校验 + 冲突检测 → 入库。
/// 解析/模板逻辑走 <see cref="GeoDbViews"/> 的 BoreholeImport* 系列(与「开孔坐标管理」共用)。原 Excel 分支改为 CSV。
/// </summary>
public partial class BoreholeImportWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<GeoDbViews.BoreholeImportRow> _rows = new();

    /// <summary>XAML 编译器/设计器用(运行时一律走带 ctx 的构造)。</summary>
    public BoreholeImportWindow() { _ctx = null!; InitializeComponent(); }

    public BoreholeImportWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        previewGrid.ItemsSource = _rows;
    }


    private async void OnPickFileClick(object? sender, RoutedEventArgs e)
    {
        var path = await _ctx.OpenFileAsync("选择钻孔数据文件", new[] { "*.csv" });
        if (path == null) return;
        LoadFile(path);
    }

    /// <summary>读一份 CSV 进预览表(选文件与自检 @钻孔导入 共用这一条路)。</summary>
    public void LoadFile(string path)
    {
        try { LoadRecords(File.ReadAllText(path)); }
        catch (Exception ex) { statusText.Text = $"解析失败：{ex.Message}"; }
    }

    /// <summary>表头映射 + 逐行校验 + 冲突检测。</summary>
    private void LoadRecords(string text)
    {
        _rows.Clear();
        var (rows, msg) = GeoDbViews.BoreholePreviewImport(_ctx.Conn, GeoDbViews.BoreholeReadCsvRecords(text));
        foreach (var r in rows) _rows.Add(r);
        statusText.Text = msg;
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) { statusText.Text = "请先选择并解析文件"; return; }
        var o = GeoDbViews.BoreholeApplyImport(_ctx.Conn, _rows, overwriteBox.IsChecked == true);
        var msg = $"导入完成：新增 {o.Inserted}，更新 {o.Updated}，跳过 {o.Skipped}（无效或未勾选覆盖）";
        if (o.Failed > 0) msg += $"；失败 {o.Failed}，首条错误：{o.FirstError}";
        statusText.Text = msg;
        _ctx.Status(msg);
    }

    /// <summary>生成带表头与示例行的 CSV 导入模板, 并提示用户填好后再导入。</summary>
    private async void OnDownloadTemplateClick(object? sender, RoutedEventArgs e)
    {
        string? path;
        try { path = await _ctx.SaveTextAsync("保存钻孔导入模板", "钻孔导入模板.csv", GeoDbViews.BoreholeCsvTemplate()); }
        catch (Exception ex) { statusText.Text = $"模板生成失败：{ex.Message}"; return; }
        if (path == null) return;

        statusText.Text = $"模板已生成：{path}（按列填好数据后，点「选择文件」导入）";
        bool open = await BoreholeMsgBox.ConfirmAsync(this, "模板已生成",
            $"导入模板已生成：\n{path}\n\n请按表头各列填入钻孔数据（必填：孔号、经距X、纬距Y），保存后回到本窗口点「选择文件 (CSV)…」导入。\n\n是否现在打开该模板？");
        if (open)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { statusText.Text = $"已生成模板，但打开失败：{ex.Message}"; }
        }
    }
}
