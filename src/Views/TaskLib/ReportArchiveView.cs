// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportArchiveView.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：SaveFileDialog → StorageProvider；MessageBox → CoalMsgBox；打印 → 出 PDF 交系统查看器。
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.TaskLib.Reporting;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 报表存档 —— 回溯历史报表、按当前数据重生成、选两期对比（环比/趋势）页（<see cref="ReportHubWindow"/> 的第四页）。
/// 渲染复用 <see cref="ReportViewBuilder"/>。
/// </summary>
public sealed class ReportArchiveView : UserControl
{
    private readonly ReportEngine _engine = new(IndicatorRegistry.Load());
    private ReportDocument? _doc;

    /// <summary>页签切换会重放 Loaded，首次显示才自动读一次存档。</summary>
    private bool _initialized;

    private readonly ListBox arcList = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock statusText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border paper = new() { Background = Brushes.White, Margin = new Thickness(16), Padding = new Thickness(28, 24), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 780 };
    private readonly StackPanel previewHost = new();

    public ReportArchiveView()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };

        var lib = new DockPanel();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var cmp = TaskUi.Btn("两期对比", OnCompare, 76); cmp.Margin = new Thickness(0, 0, 6, 0); btns.Children.Add(cmp);
        var del = TaskUi.Btn("删除", OnDelete, 60); del.Margin = new Thickness(0, 0, 6, 0); btns.Children.Add(del);
        var rf = TaskUi.Btn("刷新", Reload, 60); rf.Margin = new Thickness(0); btns.Children.Add(rf);
        DockPanel.SetDock(btns, Avalonia.Controls.Dock.Bottom); lib.Children.Add(btns);
        arcList.ItemTemplate = new FuncDataTemplate<ReportArchiveEntry>((_, _) =>
        {
            var sp = new StackPanel { Margin = new Thickness(0, 3) };
            var t = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            t.Bind(TextBlock.TextProperty, new Binding(nameof(ReportArchiveEntry.Title))); sp.Children.Add(t);
            var s = new TextBlock { FontSize = 11, Foreground = ReportViewBuilder.Hex("#9AA5B1") };
            s.Bind(TextBlock.TextProperty, new Binding(nameof(ReportArchiveEntry.SubLine))); sp.Children.Add(s);
            return sp;
        });
        ReportTemplateView.CompactList(arcList);
        arcList.SelectionChanged += (_, _) => OnSelect();
        lib.Children.Add(arcList);
        var libBox = TaskUi.GroupBox("存档（最新在前 · 可多选对比）", lib, new Thickness(0, 0, 8, 0), 6);
        Grid.SetColumn(libBox, 0); root.Children.Add(libBox);

        var pv = new DockPanel();
        // 与报表中心页同一条口径：Grid(Auto | * | Auto)，两端定宽先拿够，状态文字才是那个该让位的
        var tg = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") }; tg.ColumnDefinitions[1].MinWidth = 12;
        var regen = TaskUi.Btn("重生成(按当前数据)", OnRegen, 140); regen.Margin = new Thickness(0, 0, 16, 0); regen.Padding = new Thickness(12, 4);
        ToolTip.SetTip(regen, "用存档当时的模板与统计区间，对现在的数据重算一遍（不改存档本身）");
        Grid.SetColumn(regen, 0); tg.Children.Add(regen);
        TaskUi.Theme(statusText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        Grid.SetColumn(statusText, 1); tg.Children.Add(statusText);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var pdf = TaskUi.Btn("导出 PDF", OnExportPdf, 92); pdf.Padding = new Thickness(12, 4); pdf.Margin = new Thickness(0); right.Children.Add(pdf);
        var word = TaskUi.Btn("导出 Word", OnExportWord, 92); word.Padding = new Thickness(12, 4); word.Margin = new Thickness(6, 0, 0, 0); right.Children.Add(word);
        var print = TaskUi.Btn("打印", OnPrint, 64); print.Padding = new Thickness(12, 4); print.Margin = new Thickness(6, 0, 0, 0); right.Children.Add(print);
        Grid.SetColumn(right, 2); tg.Children.Add(right);
        var tool = TaskUi.Bar(tg, top: true, padX: 8, padY: 6); tool.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(tool, Avalonia.Controls.Dock.Top); pv.Children.Add(tool);
        paper.Child = previewHost;
        pv.Children.Add(new ScrollViewer { Content = paper, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = ReportViewBuilder.Hex("#3A3F45") });   // 同报表中心页：按视口宽量纸张，宽表各自横向滚
        var pvBox = TaskUi.GroupBox("预览", pv, new Thickness(0), 6);
        Grid.SetColumn(pvBox, 1); root.Children.Add(pvBox);

        Content = root;
        AttachedToVisualTree += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            Reload();
        };
    }

    /// <summary>重读存档列表并清空预览。宿主在「报表中心」页归档成功后也会调它。</summary>
    public void Reload()
    {
        var all = ReportArchiveStore.LoadAll();
        arcList.ItemsSource = all;
        previewHost.Children.Clear();
        _doc = null;
        SetStatus(all.Count == 0
            ? "暂无存档。到「报表中心」页生成后点「归档」。"
            : $"共 {all.Count} 份存档。");
    }

    private void OnSelect()
    {
        if (arcList.SelectedItems?.Count == 1 && arcList.SelectedItem is ReportArchiveEntry entry)
            View(entry.Doc, $"回溯：{entry.Title}");
    }

    private void View(ReportDocument doc, string status)
    {
        _doc = doc;
        ReportViewBuilder.Render(previewHost, doc);
        SetStatus(status);
    }

    /// <summary>状态文字会被压缩成省略号，全文一律同时进 ToolTip。</summary>
    private void SetStatus(string text)
    {
        statusText.Text = text;
        ToolTip.SetTip(statusText, string.IsNullOrEmpty(text) ? null : text);
    }

    private void OnRegen()
    {
        if (arcList.SelectedItem is not ReportArchiveEntry entry) { SetStatus("请先选一份存档。"); return; }
        try
        {
            entry.Ctx.AsOf = DateTime.Now;
            // 重生成 = 用存档当时的【模板 + 上下文（含统计区间）】对**现在的数据**重算。取数按存档自己的区间与口径取。
            var facts = entry.Ctx.HasRange
                ? FactSource.ForRange(entry.Ctx.From, entry.Ctx.To, entry.Ctx.Period)
                : FactSource.FromCurrentBoard();
            var doc = _engine.Generate(entry.Def, entry.Ctx, facts);
            View(doc, $"已按当前数据重生成（未改存档）：{entry.TemplateName}");
        }
        catch (Exception ex) { SetStatus("重生成失败：" + ex.Message); }
    }

    private void OnCompare()
    {
        var sel = (arcList.SelectedItems ?? Array.Empty<object>()).OfType<ReportArchiveEntry>().ToList();
        if (sel.Count != 2) { SetStatus("请在左侧按住 Ctrl 选中两份存档再对比。"); return; }
        var a = sel[0]; var b = sel[1];
        if (a.TemplateId != b.TemplateId) { SetStatus("两期对比需选同一模板的存档。"); return; }
        if (a.Doc.TotalRow == null || b.Doc.TotalRow == null) { SetStatus("该模板无合计行，暂不支持对比。"); return; }
        View(BuildCompareDoc(a, b), $"两期对比：{a.PeriodLabel} ⟷ {b.PeriodLabel}");
    }

    /// <summary>把两份同模板存档的合计行逐指标列做差，拼成一份"对比报表"，仍走共享渲染器。</summary>
    private static ReportDocument BuildCompareDoc(ReportArchiveEntry a, ReportArchiveEntry b)
    {
        var doc = new ReportDocument { Title = "两期对比", Subtitle = $"{a.TemplateName}" };
        doc.MetaLines.Add($"A：{a.ScopeLabel} · {a.PeriodLabel} · {a.ArchivedAt:yyyy-MM-dd HH:mm}");
        doc.MetaLines.Add($"B：{b.ScopeLabel} · {b.PeriodLabel} · {b.ArchivedAt:yyyy-MM-dd HH:mm}");
        doc.Columns = new()
        {
            new ColumnView { Header = "项目", Width = 150, Align = CellAlign.Left },
            new ColumnView { Header = $"A·{a.PeriodName}", Width = 120, Align = CellAlign.Right },
            new ColumnView { Header = $"B·{b.PeriodName}", Width = 120, Align = CellAlign.Right },
            new ColumnView { Header = "变化", Width = 110, Align = CellAlign.Right },
            new ColumnView { Header = "变化%", Width = 84, Align = CellAlign.Right },
        };

        var cols = a.Def.Columns;
        var aT = a.Doc.TotalRow!; var bT = b.Doc.TotalRow!;
        for (int i = 0; i < cols.Count; i++)
        {
            if (cols[i].Bind != ColBind.Indicator) continue;
            string header = cols[i].Header;
            string aTxt = i < aT.Cells.Count ? aT.Cells[i].Text : "—";
            string bTxt = i < bT.Cells.Count ? bT.Cells[i].Text : "—";
            double av = ParseNum(aTxt), bv = ParseNum(bTxt);

            string deltaTxt = "—", pctTxt = "—";
            var status = ReportStatus.None;
            if (!double.IsNaN(av) && !double.IsNaN(bv))
            {
                double d = bv - av;
                deltaTxt = (d >= 0 ? "+" : "") + d.ToString("N2");
                pctTxt = Math.Abs(av) > 1e-9 ? (d >= 0 ? "+" : "") + (d / av * 100).ToString("0.0") + "%" : "—";
                status = d > 0 ? ReportStatus.Ok : d < 0 ? ReportStatus.Bad : ReportStatus.None;
            }

            doc.Rows.Add(new RowView
            {
                Cells =
                {
                    new CellView { Text = header, Align = CellAlign.Left },
                    new CellView { Text = aTxt, Align = CellAlign.Right },
                    new CellView { Text = bTxt, Align = CellAlign.Right },
                    new CellView { Text = deltaTxt, Align = CellAlign.Right, Status = status },
                    new CellView { Text = pctTxt, Align = CellAlign.Right, Status = status },
                }
            });
        }
        doc.Conclusion = "变化 = B − A（绿=增、红=减）。同模板同口径下比较，供环比/趋势分析。";
        return doc;
    }

    private static double ParseNum(string s)
    {
        if (string.IsNullOrEmpty(s)) return double.NaN;
        var t = s.Replace(",", "").Replace("%", "").Trim();
        return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    private async void OnDelete()
    {
        var sel = (arcList.SelectedItems ?? Array.Empty<object>()).OfType<ReportArchiveEntry>().ToList();
        if (sel.Count == 0) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null && !await TaskUi.Confirm(owner, "确认", $"删除选中的 {sel.Count} 份存档？")) return;
        foreach (var s in sel) ReportArchiveStore.Delete(s.Id);
        Reload();
    }

    private async void OnExportPdf()
    {
        if (_doc == null) { SetStatus("请先选一份存档。"); return; }
        var path = await SavePath("PDF 文件", "*.pdf", FileBase() + ".pdf");
        if (path == null) return;
        try { ReportPdfRenderer.Write(_doc, path); SetStatus("已导出 PDF：" + Path.GetFileName(path)); OpenFile(path); }
        catch (Exception ex) { SetStatus("导出 PDF 失败：" + ex.Message); }
    }

    private async void OnExportWord()
    {
        if (_doc == null) { SetStatus("请先选一份存档。"); return; }
        var path = await SavePath("Word 文档", "*.docx", FileBase() + ".docx");
        if (path == null) return;
        try { WordReportRenderer.Write(_doc, path); SetStatus("已导出 Word：" + Path.GetFileName(path)); OpenFile(path); }
        catch (Exception ex) { SetStatus("导出 Word 失败：" + ex.Message); }
    }

    private void OnPrint()
    {
        if (_doc == null) { SetStatus("请先选一份存档。"); return; }
        try
        {
            string path = Path.Combine(Path.GetTempPath(), FileBase() + "_print.pdf");
            ReportPdfRenderer.Write(_doc, path);
            OpenFile(path);
            SetStatus("已生成打印稿 PDF 并交给系统查看器，请在查看器里打印：" + path);
        }
        catch (Exception ex) { SetStatus("打印失败：" + ex.Message); }
    }

    private async System.Threading.Tasks.Task<string?> SavePath(string typeName, string pattern, string suggested)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggested,
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { pattern } } },
        });
        return file?.Path.LocalPath;
    }

    private string FileBase()
    {
        string name = _doc?.Title ?? "报表存档";
        return string.Join("_", (name + "_" + DateTime.Now.ToString("yyyyMMdd")).Split(Path.GetInvalidFileNameChars()));
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}
