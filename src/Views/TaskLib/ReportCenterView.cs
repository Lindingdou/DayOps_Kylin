// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportCenterView.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：SaveFileDialog → StorageProvider；WPF PrintDialog.PrintVisual → 导出 PDF 后交系统查看器打印（Avalonia 无打印对话框）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;   // ProjectScope（当前作业日 = 统计锚点的缺省值）
using PitMine3D.Kylin.TaskLib.Reporting;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 报表中心 —— 「一键生成实际报表」页（<see cref="ReportHubWindow"/> 的第一页）。
/// 选模板 × 周期 × 范围 → 引擎取数渲染 → 纸张预览（所见即所得）→ 导出 PDF/Word / 打印 / 归档。
/// 首次显示即按默认上下文自动生成一次（真·一键）。
/// </summary>
public sealed class ReportCenterView : UserControl
{
    private readonly ReportEngine _engine = new(IndicatorRegistry.Load());   // 含用户自定义指标

    /// <summary>本次统计区间内的事实。<b>随「截至日 × 周期」重取</b>——日报周报月报各出各的数。</summary>
    private List<ProductionFact> _facts = new();
    private List<ReportDefinition> _templates = new();
    private PeriodKind _period = PeriodKind.Day;

    /// <summary>统计锚点日（区间的右端）。缺省 = 当前作业日。</summary>
    private DateTime _anchor = ProjectScope.WorkDate;
    private ReportDocument? _doc;
    private GenerationContext? _lastCtx;
    private bool _ready;

    /// <summary>模板设计器压过来的（未保存的）待预览模板；非空时恒排在下拉第一项。</summary>
    private ReportDefinition? _previewDef;

    /// <summary>页签切换会重放 Loaded，初始化只许跑一次（否则每次切回来都把范围/周期选择重置）。</summary>
    private bool _initialized;

    /// <summary>成功归档后触发，供宿主刷新「存档」页。</summary>
    public event Action? Archived;

    /// <summary>范围下拉里区分"源侧/汇侧/物料"的前缀（同一个下拉框承载三种范围）。</summary>
    private const string DestPrefix = "去向：";
    private const string MatPrefix = "物料：";

    /// <summary>周期三选一的选中底色（与「一键生成」的绿岔开：那是动作，这是状态）。</summary>
    private static readonly IBrush PeriodOnBrush = ReportViewBuilder.Hex("#475569");

    private readonly ComboBox templateCombo = new() { MinWidth = 200, MaxWidth = 280, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox scopeCombo = new() { MinWidth = 130, MaxWidth = 220, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DatePicker anchorPicker = new() { Width = 270, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button btnDay, btnWeek, btnMonth;
    private readonly TextBlock statusText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    // ★ 横向滚动 Disabled（原 WPF 是 Auto）：Avalonia 的 ScrollContentPresenter 在 Auto 下按无穷宽量内容，
    //   纸张里的居中抬头 / KPI 卡行会按无穷宽排、再被 StackPanel 以「max(可用宽, 期望宽)」摆放 —— 整段内容溢出纸张右侧。
    //   按视口宽量之后纸张永远不比视口宽，宽表仍由 ReportViewBuilder.Scrollable 各自横向滚（与原版意图一致）。
    private readonly ScrollViewer paperScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = ReportViewBuilder.Hex("#3A3F45"), Padding = new Thickness(0) };
    private readonly Border paper = new() { Background = Brushes.White, Margin = new Thickness(20), Padding = new Thickness(30, 26), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 820 };   // 原 WPF 有 DropShadowEffect；Avalonia 的 Border.BoxShadow 会把子树按 DPI 再放大一次渲染（实测内容溢出纸张），故不带阴影
    private readonly StackPanel previewHost = new();

    public ReportCenterView()
    {
        // ① 取数口径 → 生成。四个口径 + 生成钮连排
        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        row1.Children.Add(Lbl("模板"));
        templateCombo.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(ReportDefinition.Name));
        templateCombo.SelectionChanged += (_, _) => { if (_ready) Generate(); };
        row1.Children.Add(templateCombo);
        row1.Children.Add(Lbl("范围"));
        scopeCombo.SelectionChanged += (_, _) => { if (_ready) Generate(); };
        row1.Children.Add(scopeCombo);
        row1.Children.Add(Lbl("截至"));
        anchorPicker.SelectedDateChanged += (_, _) => OnAnchorChanged();
        row1.Children.Add(anchorPicker);
        // 周期三选一：带前置标签；选中态由 SyncPeriodButtons 上底色
        row1.Children.Add(Lbl("周期"));
        btnDay = PBtn("日报", PeriodKind.Day, 4); btnWeek = PBtn("周报", PeriodKind.Week, 4); btnMonth = PBtn("月报", PeriodKind.Month, 16);
        row1.Children.Add(btnDay); row1.Children.Add(btnWeek); row1.Children.Add(btnMonth);
        var gen = TaskUi.Btn("⚡ 一键生成", () => { ReloadFacts(); Generate(); }, 98, bold: true);
        gen.Padding = new Thickness(12, 4); gen.Margin = new Thickness(0); gen.Background = ReportViewBuilder.Hex("#1D9E75"); gen.Foreground = Brushes.White; gen.BorderThickness = new Thickness(0);
        ToolTip.SetTip(gen, "按上面这组口径重新取数并生成（改口径本来就会自动重出，这里是手动再拉一次）");
        row1.Children.Add(gen);

        // ② 输出：归档 | 取数说明(弹性) | 导出·打印
        var row2 = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row2.ColumnDefinitions[1].MinWidth = 12;
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var arc = TaskUi.Btn("归档", OnArchive, 56); arc.Padding = new Thickness(10, 4); arc.Margin = new Thickness(0, 0, 16, 0);
        ToolTip.SetTip(arc, "把当前这份报表连同模板与统计区间存起来，可到「存档」页回溯或与另一期对比");
        left.Children.Add(arc);
        Grid.SetColumn(left, 0); row2.Children.Add(left);
        TaskUi.Theme(statusText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        Grid.SetColumn(statusText, 1); row2.Children.Add(statusText);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var pdf = TaskUi.Btn("导出 PDF", OnExportPdf, 92); pdf.Padding = new Thickness(12, 4); pdf.Margin = new Thickness(0); right.Children.Add(pdf);
        var word = TaskUi.Btn("导出 Word", OnExportWord, 92); word.Padding = new Thickness(12, 4); word.Margin = new Thickness(6, 0, 0, 0); right.Children.Add(word);
        var print = TaskUi.Btn("打印", OnPrint, 64); print.Padding = new Thickness(12, 4); print.Margin = new Thickness(6, 0, 0, 0); right.Children.Add(print);
        Grid.SetColumn(right, 2); row2.Children.Add(right);

        var toolPanel = new StackPanel(); toolPanel.Children.Add(row1); toolPanel.Children.Add(row2);
        var tool = TaskUi.Bar(toolPanel, top: true, padY: 8);

        // 报表纸张预览：横向滚动条留 Auto、纸张宽度绑到视口（纸张永远不比视口宽）
        paper.Child = previewHost;
        paperScroll.Content = paper;
        paperScroll.ScrollChanged += (_, _) => OnPaperViewportChanged();
        paperScroll.SizeChanged += (_, _) => OnPaperViewportChanged();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(tool, 0); Grid.SetRow(paperScroll, 1);
        root.Children.Add(tool); root.Children.Add(paperScroll);
        Content = root;

        AttachedToVisualTree += (_, _) => OnLoaded();
    }

    private static TextBlock Lbl(string t)
    {
        var l = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        TaskUi.Theme(l, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        return l;
    }

    private Button PBtn(string text, PeriodKind kind, double right)
    {
        var b = TaskUi.Btn(text, () => OnPeriod(kind), 54);
        b.Padding = new Thickness(10, 4); b.Margin = new Thickness(0, 0, right, 0); b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    private void OnLoaded()
    {
        if (_initialized) return;
        _initialized = true;

        _templates = ReportLibrary.LoadAll();
        if (_previewDef != null) _templates.Insert(0, _previewDef);
        templateCombo.ItemsSource = _templates;
        templateCombo.SelectedIndex = 0;

        anchorPicker.SelectedDate = new DateTimeOffset(_anchor);
        PickFirstPeriodWithData();   // EV11：开在**有数**的最小口径上
        SyncPeriodButtons();
        BuildScopes();

        _ready = true;
        Generate();   // 一键：打开即生成
    }

    /// <summary>范围三选一：源侧（作业面）/ 汇侧（去向）/ 物料。带前缀区分，免得再加两个下拉框。</summary>
    private void BuildScopes()
    {
        var scopes = new List<string> { "全矿" };
        scopes.AddRange(_facts.Select(f => f.Panel).Where(p => !string.IsNullOrEmpty(p)).Distinct().OrderBy(p => p, StringComparer.Ordinal));
        scopes.AddRange(_facts.Where(f => f.HasDestination).Select(f => DestPrefix + f.DestinationKey).Distinct().OrderBy(p => p, StringComparer.Ordinal));
        scopes.AddRange(_facts.Where(f => f.HasMaterial).Select(f => MatPrefix + f.MaterialKey).Distinct().OrderBy(p => p, StringComparer.Ordinal));
        scopeCombo.ItemsSource = scopes;
        scopeCombo.SelectedIndex = 0;
    }

    // ── 宿主转接（页签之间的联动，原来是各开各窗、互不知情）──

    /// <summary>模板设计器的「预览」：把一份未保存的模板顶到下拉第一项并当场生成。</summary>
    public void ShowPreview(ReportDefinition def)
    {
        _previewDef = def;
        bool wasReady = _ready;
        _ready = false;                       // 换 ItemsSource 会连发几次 SelectionChanged，压住不让它各生成一次
        _templates = ReportLibrary.LoadAll();
        _templates.Insert(0, def);
        templateCombo.ItemsSource = _templates;
        templateCombo.SelectedIndex = 0;
        _ready = wasReady;
        if (_ready) Generate();
    }

    /// <summary>模板库有增删改后刷新下拉，尽量停在原来那份模板上（Id 相同即算同一份）。</summary>
    public void ReloadTemplates()
    {
        if (!_initialized) return;            // 还没显示过，OnLoaded 会自己加载
        string? prevId = (templateCombo.SelectedItem as ReportDefinition)?.Id;
        bool wasReady = _ready;
        _ready = false;
        _templates = ReportLibrary.LoadAll();
        if (_previewDef != null) _templates.Insert(0, _previewDef);
        templateCombo.ItemsSource = _templates;
        templateCombo.SelectedItem = _templates.FirstOrDefault(t => t.Id == prevId) ?? _templates.FirstOrDefault();
        _ready = wasReady;
        if (_ready) Generate();               // 改完模板当场重出，不必关窗重开
    }

    // ── 事件 ──

    /// <summary>纸张宽度跟着视口走 —— <b>纸张永远不比视口宽</b>（挂 ScrollChanged：竖向滚动条出现会吃掉视口宽却不触发 SizeChanged）。</summary>
    private void OnPaperViewportChanged()
    {
        double avail = paperScroll.Viewport.Width;
        if (avail <= 0) return;
        // 减去纸张自己的 Margin（左右各 20），否则纸张贴着视口两边、外层照样冒出滚动条
        double want = Math.Max(820, avail - 40);
        if (Math.Abs(paper.MaxWidth - want) < 0.5) return;
        paper.MaxWidth = want;
    }

    private void OnAnchorChanged()
    {
        if (anchorPicker.SelectedDate is not { } d) return;
        if (d.Date == _anchor.Date) return;
        _anchor = d.Date;
        if (!_ready) return;
        ReloadFacts();
        Generate();
    }

    /// <summary>EV11：首次打开时，从**日 → 周 → 月**依次试，停在第一个取得到量的口径上（细口径确实取不到量时才退一档，并把理由写进状态栏）。</summary>
    private void PickFirstPeriodWithData()
    {
        var tried = new List<string>();
        foreach (var kind in new[] { PeriodKind.Day, PeriodKind.Week, PeriodKind.Month })
        {
            _period = kind;
            ReloadFacts();
            // 「有事实」还不够 —— 只有工时没有量的口径照样出不了产量报表。
            if (_facts.Count > 0 && _facts.Any(f => f.PlanVolumeM3 > 1e-6 || f.ActualVolumeM3 > 1e-6))
            {
                if (tried.Count > 0)
                    SetStatus($"{string.Join("、", tried)}取不到量，已自动开在【{PeriodName(kind)}】口径。"
                            + FactSource.LastRangeLabel);
                return;
            }
            tried.Add(PeriodName(kind));
        }

        // 三个口径都没量：停在日口径（最细的那个），把最后一次取数说明留给用户。
        _period = PeriodKind.Day;
        ReloadFacts();
    }

    private static string PeriodName(PeriodKind k) => k switch
    {
        PeriodKind.Week => "周报",
        PeriodKind.Month => "月报",
        _ => "日报",
    };

    private void OnPeriod(PeriodKind kind)
    {
        _period = kind;
        SyncPeriodButtons();
        ReloadFacts();     // 周期变 ⇒ 区间变 ⇒ 必须重新取数，否则周报还是那一天的数
        Generate();
    }

    /// <summary>三选一的选中态。原来只加粗字重——隔着间距根本比不出来选的是哪一个。</summary>
    private void SyncPeriodButtons()
    {
        Paint(btnDay, _period == PeriodKind.Day);
        Paint(btnWeek, _period == PeriodKind.Week);
        Paint(btnMonth, _period == PeriodKind.Month);

        static void Paint(Button b, bool on)
        {
            b.FontWeight = on ? FontWeight.Bold : FontWeight.Normal;
            if (on) { b.Background = PeriodOnBrush; b.Foreground = Brushes.White; }
            else { b.ClearValue(BackgroundProperty); b.ClearValue(ForegroundProperty); }
        }
    }

    /// <summary>按「周期 × 截至日」重取事实。取数说明一并留下，进报表脚注。</summary>
    private void ReloadFacts()
    {
        var (from, to) = FactSource.RangeOf(_period, _anchor);
        // 口径要一起传：槽③（采掘单元台账）是期级实测，只在【月】口径下供数。
        try { _facts = FactSource.ForRange(from, to, _period); }
        catch (Exception ex)
        {
            _facts = new List<ProductionFact>();
            SetStatus("取数失败：" + ex.Message);
        }
    }

    // ── 生成 ──
    private void Generate()
    {
        if (!_ready) return;
        if (templateCombo.SelectedItem is not ReportDefinition def) return;

        string scope = scopeCombo.SelectedItem as string ?? "全矿";
        var (from, to) = FactSource.RangeOf(_period, _anchor);
        var ctx = new GenerationContext
        {
            Mine = _facts.FirstOrDefault()?.Mine ?? SampleTaskBoard.MineName,
            Period = _period,
            PeriodLabel = PeriodLabel(),
            From = from,
            To = to,
            SourceNote = FactSource.LastRangeLabel,
            ScopeLabel = scope,
            PanelFilter = scope == "全矿" || scope.StartsWith(DestPrefix) || scope.StartsWith(MatPrefix) ? null : scope,
            DestinationFilter = scope.StartsWith(DestPrefix) ? scope[DestPrefix.Length..] : null,
            MaterialFilter = scope.StartsWith(MatPrefix) ? scope[MatPrefix.Length..] : null,
            AsOf = DateTime.Now,
        };
        _lastCtx = ctx;

        try
        {
            _doc = _engine.Generate(def, ctx, _facts);
            RenderDocument(_doc);
            // 状态栏直说这份报表吃到了几天数据——空报表和"确实没干活"必须一眼能分开
            SetStatus($"已生成：{def.Name}　·　{FactSource.LastRangeDaysWithVolume}/{FactSource.LastRangeDays} 天有量　·　{FactSource.LastRangeLabel}");
        }
        catch (Exception ex)
        {
            SetStatus("生成失败：" + ex.Message);
        }
    }

    /// <summary>状态文字会被压缩成省略号，全文一律同时进 ToolTip。</summary>
    private void SetStatus(string text)
    {
        statusText.Text = text;
        ToolTip.SetTip(statusText, string.IsNullOrEmpty(text) ? null : text);
    }

    /// <summary>期间文案 = 真实统计区间。日报给带星期的日期，周/月给起止与天数。</summary>
    private string PeriodLabel()
    {
        var (from, to) = FactSource.RangeOf(_period, _anchor);
        return FactSource.RangeLabel(from, to);
    }

    // ── 把渲染树画成"实际报表"（纸张预览）→ 共享渲染器 ──
    private void RenderDocument(ReportDocument doc) => ReportViewBuilder.Render(previewHost, doc);

    // ── 导出 / 打印 ──
    private async void OnExportPdf()
    {
        if (_doc == null) { SetStatus("请先生成报表再导出。"); return; }
        var path = await SavePath("PDF 文件", "*.pdf", FileBase() + ".pdf");
        if (path == null) return;
        try
        {
            ReportPdfRenderer.Write(_doc, path);
            SetStatus("已导出 PDF：" + Path.GetFileName(path));
            OpenFile(path);
        }
        catch (Exception ex) { SetStatus("导出 PDF 失败：" + ex.Message); }
    }

    private async void OnExportWord()
    {
        if (_doc == null) { SetStatus("请先生成报表再导出。"); return; }
        var path = await SavePath("Word 文档", "*.docx", FileBase() + ".docx");
        if (path == null) return;
        try
        {
            WordReportRenderer.Write(_doc, path);
            SetStatus("已导出 Word：" + Path.GetFileName(path));
            OpenFile(path);
        }
        catch (Exception ex) { SetStatus("导出 Word 失败：" + ex.Message); }
    }

    /// <summary>打印：Avalonia 无打印对话框 —— 出一份 PDF 交给系统查看器打印（版式与导出一致）。</summary>
    private void OnPrint()
    {
        if (_doc == null) { SetStatus("请先生成报表再打印。"); return; }
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

    // ── 归档 ──
    private void OnArchive()
    {
        if (_doc == null || _lastCtx == null || templateCombo.SelectedItem is not ReportDefinition def)
        { SetStatus("请先生成报表再归档。"); return; }
        var entry = new ReportArchiveEntry
        {
            Title = $"{_doc.Title} · {_lastCtx.ScopeLabel} · {_lastCtx.PeriodLabel}",
            TemplateId = def.Id,
            TemplateName = def.Name,
            ScopeLabel = _lastCtx.ScopeLabel,
            PeriodName = GenerationContext.PeriodName(_period),
            PeriodLabel = _lastCtx.PeriodLabel,
            ArchivedAt = DateTime.Now,
            Def = def,
            Ctx = _lastCtx,
            Doc = _doc,
        };
        try
        {
            ReportArchiveStore.Save(entry);
            SetStatus("已归档（可到「存档」页回溯/对比）：" + entry.Title);
            Archived?.Invoke();
        }
        catch (Exception ex) { SetStatus("归档失败：" + ex.Message); }
    }

    private string FileBase()
    {
        string name = (_doc?.Title ?? "生产报表");
        return string.Join("_", (name + "_" + DateTime.Now.ToString("yyyyMMdd")).Split(Path.GetInvalidFileNameChars()));
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* 打开失败不影响导出 */ }
    }
}
