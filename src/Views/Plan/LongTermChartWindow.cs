using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「进度计划方案出图」窗口（中长远组第 5 按钮；移植原 <c>LongTermChartWindow</c>）。画已确定/最优方案的逐年采掘进度图
/// （产量柱 + 达产线 + 生产剥采比折线 + 生产时相底色）→ 导出 PNG / CSV。
///
/// 【LT4】原先打开时若库里没有推荐方案，会自动全套编制再画 —— 用户第一次点开看到的是按写死工作线画出来的进度图。
/// 现在不自动编：没有人为指定的工作线就拦住并说清楚去哪儿指定。
/// </summary>
internal sealed class LongTermChartWindow : Window
{
    private readonly ObservableCollection<LongTermPlan> _schemes;
    private readonly IReadOnlyList<MiningProgramPlan>? _programs;   // 上游开采程序（自动续源用）
    private readonly IPlanEntityHost _host;
    private readonly ComboBox _schemeCombo = new() { Width = 240, DisplayMemberBinding = new Avalonia.Data.Binding("Name") };
    private readonly Canvas _sheet = new() { Background = Brushes.White, ClipToBounds = true, MinHeight = 240 };
    private readonly TextBlock _info = RoadUi.Hint("", 12.5), _bottomStatus = RoadUi.Hint("", 12.5);

    public LongTermChartWindow(IPlanEntityHost host, ObservableCollection<LongTermPlan>? schemes = null, IReadOnlyList<MiningProgramPlan>? programs = null)
    {
        _host = host;
        _schemes = schemes is { Count: > 0 } ? schemes : LongTermSchemeStore.Schemes;
        _programs = programs ?? MiningProgramStore.Schemes;
        Title = "进度计划方案出图";
        PlanUi.Place(this, 980, 640);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var header = PlanUi.Header("进度计划方案出图", "逐年采掘进度图（产量柱 + 达产线 + 生产剥采比折线 + 生产时相底色）·  可导出 PNG / CSV", Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x1E, 0x40, 0xAF));
        Grid.SetRow(header, 0); root.Children.Add(header);

        var bar = new DockPanel();
        var auto = new Button
        {
            Content = "⚡ 一键排产并出图", MinWidth = 150, Margin = new Thickness(0, 0, 12, 0), FontWeight = FontWeight.Bold, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.FromRgb(0x25, 0x63, 0xEB), 0), new GradientStop(Color.FromRgb(0x1E, 0x40, 0xAF), 1) } },
        };
        ToolTip.SetTip(auto, "自动续源 → 按【已在「派生计划方案」里指定的工作线】真场排产 → 评分 → 选推荐 → 画图。没指定工作线时会拦住");
        auto.Click += (_, _) => OnAutoComposeAndPlot();
        DockPanel.SetDock(auto, Avalonia.Controls.Dock.Left); bar.Children.Add(auto);
        var lbl = RoadUi.Lbl("方案"); lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left); bar.Children.Add(lbl);
        _schemeCombo.ItemsSource = _schemes;
        DockPanel.SetDock(_schemeCombo, Avalonia.Controls.Dock.Left); bar.Children.Add(_schemeCombo);
        _info.VerticalAlignment = VerticalAlignment.Center; _info.Margin = new Thickness(16, 0, 0, 0); bar.Children.Add(_info);
        var barB = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        RoadUi.Theme(barB, Border.BorderBrushProperty, "Theme.Panel.Border"); RoadUi.Theme(barB, Border.BackgroundProperty, "Theme.Panel.Background");
        Grid.SetRow(barB, 1); root.Children.Add(barB);

        var sheetB = new Border { Margin = new Thickness(14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(6), Child = _sheet };
        RoadUi.Theme(sheetB, Border.BorderBrushProperty, "Theme.Panel.Border");
        Grid.SetRow(sheetB, 2); root.Children.Add(sheetB);

        var foot = new DockPanel();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var bPng = RoadUi.Btn("导出 PNG", async () => await OnExportPngAsync(), 90); bPng.Margin = new Thickness(0, 0, 8, 0);
        var bCsv = RoadUi.Btn("导出 CSV", async () => await OnExportCsvAsync(), 90); bCsv.Margin = new Thickness(0, 0, 8, 0);
        var bClose = RoadUi.Btn("关闭", Close, 70);
        btns.Children.Add(bPng); btns.Children.Add(bCsv); btns.Children.Add(bClose);
        DockPanel.SetDock(btns, Avalonia.Controls.Dock.Right); foot.Children.Add(btns);
        _bottomStatus.VerticalAlignment = VerticalAlignment.Center; foot.Children.Add(_bottomStatus);
        var footB = PlanUi.Footer(foot);
        Grid.SetRow(footB, 3); root.Children.Add(footB);
        Content = root;

        EnsureRecommended(force: false);             // 【LT4】只按已指定工作线补排，库空时拦住
        SelectRecommended();
        _schemeCombo.SelectionChanged += (_, _) => Redraw();
        _sheet.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Redraw(); };
        Opened += (_, _) => Redraw();
    }

    /// <summary>外部（如「规划计算」确定方案后）请求重画：重选推荐并重绘。</summary>
    public void Reload() { SelectRecommended(); Redraw(); }

    /// <summary>确保库里有可出图的推荐方案。【LT4】只按**已指定的工作线**重排，绝不自己造工作线；一条都没有就拦住（返回 false）。</summary>
    private bool EnsureRecommended(bool force)
    {
        bool hasScheduled = _schemes.Any(s => s.Result != null);
        if (!force && hasScheduled) return true;

        var wls = LongTermSchemeStore.SpecifiedWorkLines();
        if (wls.Count == 0) { _bottomStatus.Text = LongTermSchemeStore.NeedWorkLineHint; return false; }
        var model = _host.ActiveBlockModel;
        if (!LongTermBlockSource.HasActiveBlockModel(model, out var note))
        { _bottomStatus.Text = $"排不了：{note} —— 中长远的量只能来自块体（BM1）。请先「加载块体模型」并激活一个带煤属性的块体"; return false; }
        bool inherited = LongTermSchemeStore.AutoInheritIfNeeded(_programs);
        var (schemes, best) = LongTermScheduler.ComposeFrom(LongTermSchemeStore.Base, wls, model, LongTermDumpBridge.TryReadForm(_host.Db));
        _schemes.Clear();
        foreach (var s in schemes) _schemes.Add(s);
        if (best != null)
        {
            LongTermSchemeStore.Confirmed = best;
            foreach (var s in _schemes) s.Note = s == best ? "★推荐" : "";
        }
        string inh = inherited ? $"自动续源「{LongTermSchemeStore.Base.SourceProgramName}」·" : "";
        _bottomStatus.Text = $"⚡{inh}按 {wls.Count} 条已指定工作线排出 {schemes.Count} 套【{note}】 → 推荐「{best?.Name}」并出图";
        return true;
    }

    private void SelectRecommended()
    {
        var def = LongTermSchemeStore.Confirmed != null && _schemes.Contains(LongTermSchemeStore.Confirmed)
            ? LongTermSchemeStore.Confirmed
            : _schemes.Where(s => s.Result != null).OrderByDescending(s => s.Result!.CompositeScore).FirstOrDefault() ?? _schemes.FirstOrDefault();
        _schemeCombo.SelectedItem = def;
    }

    /// <summary>⚡一键排产并出图：按已指定的工作线重排全部 → 选推荐 → 画图。库里没指定工作线时只提示、不画。</summary>
    private void OnAutoComposeAndPlot()
    {
        if (!EnsureRecommended(force: true)) return;
        SelectRecommended();
        Redraw();
        _host.Echo("进度计划方案出图：" + _bottomStatus.Text, false);
    }

    private LongTermPlan? Current => _schemeCombo.SelectedItem as LongTermPlan;

    private void Redraw()
    {
        var p = Current;
        if (p != null && p.Result == null) LongTermScheduler.Schedule(p, _host.ActiveBlockModel, LongTermDumpBridge.TryReadForm(_host.Db));
        LongTermCharts.DrawScheduleSheet(_sheet, p);
        var r = p?.Result;
        _info.Text = p == null ? "（无方案 —— 请先在「派生计划方案」指定工作线）"
            : r == null ? $"{p.Name}（未排产）"
            : $"{p.Name} · {p.WorkLine.Caption} · 服务年限 {r.ServiceLifeYears:0}a · 达产 {r.DesignCalcYearLabel} · 峰值剥采比 {r.ProductionRatioPeak:0.0} · NPV {r.Npv:N0}万";
    }

    private async Task OnExportPngAsync()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先选一个方案"; return; }
        if (_sheet.Bounds.Width < 2 || _sheet.Bounds.Height < 2) { _bottomStatus.Text = "画布未就绪"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出进度图 PNG", SuggestedFileName = $"进度计划图_{cur.Name}.png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG 图片") { Patterns = new[] { "*.png" } } },
        });
        if (file == null) return;
        try
        {
            int w = (int)Math.Ceiling(_sheet.Bounds.Width), h = (int)Math.Ceiling(_sheet.Bounds.Height);
            using var bmp = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            bmp.Render(_sheet);          // 画布本身白底，导出不透明
            bmp.Save(file.Path.LocalPath);
            _bottomStatus.Text = $"已导出 PNG：{file.Path.LocalPath}";
        }
        catch (Exception ex) { _bottomStatus.Text = $"导出失败：{ex.Message}"; }
    }

    private async Task OnExportCsvAsync()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先选一个方案"; return; }
        if (cur.Result == null) LongTermScheduler.Schedule(cur, _host.ActiveBlockModel, LongTermDumpBridge.TryReadForm(_host.Db));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出进度计划 CSV", SuggestedFileName = $"中长远进度计划_{cur.Name}.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, LongTermSolveWindow.BuildReport(cur), System.Text.Encoding.UTF8); _bottomStatus.Text = $"已导出 CSV：{file.Path.LocalPath}"; }
        catch (Exception ex) { _bottomStatus.Text = $"导出失败：{ex.Message}"; }
    }
}
