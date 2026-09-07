using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 输出报告对话框（忠实原 ReportDialog）：来源块体 / 模板（v1 固定）/ 范围（全部·当前筛选）→ 右侧文本预览；
/// 底部选 HTML/CSV 导出并打开（PDF 依赖 QuestPDF，Kylin 不提供）。统计走 <see cref="BlockModelReport.Compute"/>。
/// </summary>
public partial class ReportWindow : Window
{
    private readonly ModelingContext _ctx;
    private BlockModelReport.Report? _lastReport;
    private BlockModelMeta? _lastModel;
    private BlockFilterSet? _lastScope;
    private bool _busy;

    public ReportWindow() { _ctx = null!; InitializeComponent(); }

    public ReportWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        sourceCombo.ItemsSource = BlockModelStore.Models;
        scopeAll.IsCheckedChanged += OnScopeChanged;
        scopeFilter.IsCheckedChanged += OnScopeChanged;
        var def = BlockModelStore.PickDefault();
        if (def != null) sourceCombo.SelectedItem = def;
    }

    private BlockModelMeta? Source => sourceCombo.SelectedItem as BlockModelMeta;

    private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Source is { } m) { UpdateScopeUi(m); _ = RefreshPreviewAsync(m); }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (Source is { } m) await RefreshPreviewAsync(m);
        else await BlockMsgBox.WarnAsync(this, "刷新预览", "请先选择来源块体。");
    }

    private void OnScopeChanged(object? sender, RoutedEventArgs e) { if (Source is { } m) _ = RefreshPreviewAsync(m); }

    private BlockFilterSet? CurrentScopeFilter(BlockModelMeta m) => scopeFilter.IsChecked == true && m.Filter is { Conditions.Count: > 0 } ? m.Filter : null;

    private void UpdateScopeUi(BlockModelMeta? m)
    {
        bool hasFilter = m?.Filter is { Conditions.Count: > 0 };
        scopeFilter.IsEnabled = hasFilter;
        if (hasFilter) filterInfoText.Text = "筛选：" + m!.Filter;
        else
        {
            if (scopeFilter.IsChecked == true) scopeAll.IsChecked = true;
            filterInfoText.Text = "（未设置筛选；用『筛选块体』设条件后此项可用）";
        }
    }

    private async Task RefreshPreviewAsync(BlockModelMeta m)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var scope = CurrentScopeFilter(m);
            _ctx.Status("输出报告 — 统计中…");
            var report = await Task.Run(() => BlockModelReport.Compute(m, scope));
            _lastReport = report; _lastModel = m; _lastScope = scope;
            previewText.Text = BlockModelReport.RenderPlainText(report);
            _ctx.Status($"输出报告 {m.Name}：可见 {report.VisibleCells:N0} 块 · 体积 {report.VisibleVolume:N0} m³");
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "计算报告", ex.Message); }
        finally { _busy = false; }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (Source is not { } m) { await BlockMsgBox.WarnAsync(this, "导出报告", "请先选择来源块体。"); return; }
            string fmt = (formatCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "html";
            var scope = CurrentScopeFilter(m);
            BlockModelReport.Report report;
            if (_lastReport != null && ReferenceEquals(_lastModel, m) && ReferenceEquals(_lastScope, scope)) report = _lastReport;
            else
            {
                _busy = true;
                try { report = await Task.Run(() => BlockModelReport.Compute(m, scope)); _lastReport = report; _lastModel = m; _lastScope = scope; }
                finally { _busy = false; }
            }
            string ext = fmt == "csv" ? ".csv" : ".html";
            string name = $"{m.Name}_报告_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            string content = fmt == "csv" ? BlockModelReport.RenderCsv(report) : BlockModelReport.RenderHtml(report);
            var path = await _ctx.SaveTextAsync("导出报告", name, content);
            if (path == null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex) { await BlockMsgBox.InfoAsync(this, "导出成功", $"报告已保存到：\n{path}\n\n(自动打开失败：{ex.Message})"); }
            _ctx.Status($"输出报告：{Path.GetFileName(path)}");
            Close(true);
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "导出报告", ex.Message); }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
