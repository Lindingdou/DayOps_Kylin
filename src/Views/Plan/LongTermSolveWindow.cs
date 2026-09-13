using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「规划计算 · 排产」窗口（中长远组第 2 大按钮；移植原 <c>LongTermSolveWindow</c>）。一键排产比选（按库里已指定的工作线真场排产 → 评分 → 推荐）/
/// 排产全部 / 排产选中 → 选中方案的逐年进度图 + 九项指标 + 逐年进度表（含均衡段 / 排土侧）→ 确定进度计划 / 导出 CSV。多套联合对比在「方案综合对比」。
/// </summary>
internal sealed class LongTermSolveWindow : Window
{
    private readonly ObservableCollection<LongTermPlan> _schemes;
    private readonly IReadOnlyList<MiningProgramPlan> _programs;
    private readonly IPlanEntityHost _host;
    private readonly Action<Window> _openBlockPicker;

    private readonly DataGrid _schemeList, _periodGrid;
    private readonly Canvas _chart = new() { Background = Brushes.White, ClipToBounds = true, MinHeight = 200 };
    private readonly TextBlock _toolStatus = RoadUi.Hint(""), _bottomStatus = RoadUi.Hint("排产后点「确定进度计划」→ 作为中长远主方案（喂短期计划 / 过程模拟 / 驱动推进）");
    private readonly TextBlock _detailTitle = RoadUi.Text("选中方案：—", 13, bold: true);
    private readonly TextBlock _pLife = B("—"), _pTtc = B("—"), _pPlateau = B("—"), _pPeak = B("—"), _pBasic = B("—"),
                               _pInner = B("—", new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF))), _pNpv = B("—", new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF))), _pPayback = B("—"), _pOk = B("—");
    private static TextBlock B(string t, IBrush? fg = null)
    {
        var tb = new TextBlock { Text = t, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap };
        if (fg != null) tb.Foreground = fg; else RoadUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Primary");
        return tb;
    }

    public LongTermSolveWindow(IPlanEntityHost host, IReadOnlyList<MiningProgramPlan>? programs, Action<Window> openBlockPicker)
    {
        _host = host; _openBlockPicker = openBlockPicker;
        _programs = programs ?? MiningProgramStore.Schemes;
        _schemes = LongTermSchemeStore.Schemes;
        Title = "规划计算 · 排产";
        PlanUi.Place(this, 1320, 860);

        _schemeList = new DataGrid { AutoGenerateColumns = false, IsReadOnly = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5 };
        _schemeList.Columns.Add(new DataGridCheckBoxColumn { Header = "排产", Binding = new Avalonia.Data.Binding("Participate") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(50) });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "方案", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Avalonia.Data.Binding("SolvedText"), Width = new DataGridLength(70), IsReadOnly = true });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "备注", Binding = new Avalonia.Data.Binding("Note"), Width = new DataGridLength(70), IsReadOnly = true });
        PlanUi.FitHeaders(_schemeList);
        _schemeList.ItemsSource = _schemes;
        _schemeList.SelectionChanged += (_, _) => { if (Current != null) RefreshDetail(Current); };

        _periodGrid = PlanUi.Table(new (string, string, double)[]
        {
            ("年", "Label", 72), ("采出(万t)", "CoalText", 84), ("剥离(万m³)", "StripText", 96), ("剥采比", "RatioText", 70), ("达产%", "CapText", 66), ("时相", "PhaseText", 56),
            ("均衡段", "StageNo", 66), ("段剥采比", "StageRatioText", 80), ("超前剥离(万m³)", "LeadText", 124), ("排弃", "DumpText", 52),
            ("内排(万m³)", "InnerText", 96), ("外排(万m³)", "OuterText", 96), ("排土推进(m)", "DumpAdvText", 104), ("排到哪儿", "DumpCellText", 190), ("排不下(万m³)", "OverflowText", 110), ("", "FlagText", 56),
        }, multi: false);
        _periodGrid.MaxHeight = 220;

        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        var root = (Grid)Content;
        var header = PlanUi.Header("规划计算 · 排产", "一键排产：划期 → 达产爬坡分配各期采剥量 → 剥采比削峰 → 逐年现金流/NPV → 逐年进度表 + 论证图表", Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x1E, 0x40, 0xAF));
        Grid.SetRow(header, 0); root.Children.Add(header);
        var tool = BuildTool(); Grid.SetRow(tool, 1); root.Children.Add(tool);
        var body = BuildBody(); Grid.SetRow(body, 2); root.Children.Add(body);
        var pg = PlanUi.Group("逐年进度表（采出 / 剥离 / 剥采比 / 达产率 / 时相 / 均衡 / 排弃 · 左右滚动看排土侧）", _periodGrid, new Thickness(12, 0, 12, 6), 6);
        Grid.SetRow(pg, 3); root.Children.Add(pg);
        var foot = BuildFooter(); Grid.SetRow(foot, 4); root.Children.Add(foot);

        _chart.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty && Current != null) LongTermCharts.DrawScheduleSheet(_chart, Current); };
        if (_schemes.Count > 0) _schemeList.SelectedIndex = 0;
        if (Current != null) RefreshDetail(Current);
    }

    private LongTermPlan? Current => _schemeList.SelectedItem as LongTermPlan;

    private Control BuildTool()
    {
        var tool = new DockPanel();
        var b1 = RoadUi.Btn("⚡ 一键排产比选", OneClickAuto, 160, bold: true);
        ToolTip.SetTip(b1, "自动续源 → 按【已在「派生计划方案」里指定的工作线】真场排产 → 评分 → 选出推荐（代码里没有默认工作线，一条都没指定就拦住）");
        var b2 = RoadUi.Btn("加载块体模型", () => { _openBlockPicker(this); _toolStatus.Text = LongTermBlockSource.HasActiveBlockModel(_host.ActiveBlockModel, out var note) ? $"量源就绪：{note} → 可「排产全部」" : $"仍无可用量源：{note}"; }, 110);
        ToolTip.SetTip(b2, "中长远的逐年采出/剥离量只能来自块体（BM1）。这里可以列出/激活/导入块体模型");
        var b3 = RoadUi.Btn("排产全部", OnScheduleAll, 90);
        var b4 = RoadUi.Btn("排产选中", OnScheduleSelected, 90);
        foreach (var b in new[] { b1, b2, b3, b4 }) { DockPanel.SetDock(b, Avalonia.Controls.Dock.Left); tool.Children.Add(b); }
        _toolStatus.VerticalAlignment = VerticalAlignment.Center; tool.Children.Add(_toolStatus);
        var border = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = tool };
        RoadUi.Theme(border, Border.BorderBrushProperty, "Theme.Panel.Border");
        RoadUi.Theme(border, Border.BackgroundProperty, "Theme.Panel.Background");
        return border;
    }

    private Control BuildBody()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*,330") };
        var left = PlanUi.Group("方案列表（勾选参与排产）", _schemeList, new Thickness(12, 12, 6, 6), 6);
        Grid.SetColumn(left, 0); g.Children.Add(left);
        var mid = PlanUi.Group("逐年采掘进度图（产量柱 + 达产线 + 生产剥采比折线 + 生产时相底色）", _chart, new Thickness(6, 12, 6, 6), 6);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        var detail = new StackPanel();
        _detailTitle.Margin = new Thickness(0, 0, 0, 8); _detailTitle.TextWrapping = TextWrapping.Wrap;
        detail.Children.Add(_detailTitle);
        var ug = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        void Pair(string k, TextBlock v) { ug.Children.Add(RoadUi.Hint(k, 12.5)); ug.Children.Add(v); }
        Pair("服务年限", _pLife); Pair("达产时间", _pTtc); Pair("稳产期", _pPlateau); Pair("峰值生产剥采比", _pPeak); Pair("基建剥离量", _pBasic);
        Pair("内排率", _pInner); Pair("累计NPV", _pNpv); Pair("投资回收期", _pPayback); Pair("校核", _pOk);
        detail.Children.Add(ug);
        var right = PlanUi.Group("进度计划指标", detail, new Thickness(6, 12, 12, 6), 8);
        Grid.SetColumn(right, 2); g.Children.Add(right);
        return g;
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var confirm = new Button
        {
            Content = "✔ 确定进度计划", MinWidth = 150, Height = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.FromRgb(0x3B, 0x82, 0xF6), 0), new GradientStop(Color.FromRgb(0x1E, 0x40, 0xAF), 1) } },
        };
        confirm.Click += (_, _) => OnConfirmPlan();
        DockPanel.SetDock(confirm, Avalonia.Controls.Dock.Right); dock.Children.Add(confirm);
        var export = RoadUi.Btn("导出报表", async () => await OnExportReportAsync(), 90); export.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(export, Avalonia.Controls.Dock.Right); dock.Children.Add(export);
        _bottomStatus.VerticalAlignment = VerticalAlignment.Center; dock.Children.Add(_bottomStatus);
        return PlanUi.Footer(dock);
    }

    private void RefreshList() { var sel = Current; _schemeList.ItemsSource = null; _schemeList.ItemsSource = _schemes; if (sel != null) _schemeList.SelectedItem = sel; }

    /// <summary>⚡一键排产比选：自动续源 → 按库里已指定的工作线真场排产 → 评分 → 选出推荐。一条工作线都没有就拦住（LT4）。</summary>
    public void OneClickAuto()
    {
        var wls = LongTermSchemeStore.SpecifiedWorkLines();
        if (wls.Count == 0) { _toolStatus.Text = LongTermSchemeStore.NeedWorkLineHint; return; }
        var model = _host.ActiveBlockModel;
        if (!LongTermBlockSource.HasActiveBlockModel(model, out var note))
        { _toolStatus.Text = $"排不了：{note} —— 中长远的量只能来自块体（BM1）。请先「加载块体模型」并激活一个带煤属性的块体"; return; }
        bool inherited = LongTermSchemeStore.AutoInheritIfNeeded(_programs);
        var (schemes, best) = LongTermScheduler.ComposeFrom(LongTermSchemeStore.Base, wls, model, LongTermDumpBridge.TryReadForm(_host.Db));
        _schemes.Clear();
        foreach (var s in schemes) _schemes.Add(s);
        if (best != null)
        {
            LongTermSchemeStore.Confirmed = best;
            foreach (var s in _schemes) s.Note = s == best ? "★推荐" : "";
        }
        RefreshList();
        _schemeList.SelectedItem = best ?? _schemes.FirstOrDefault();
        if (Current != null) RefreshDetail(Current);
        string inh = inherited ? $"自动续源「{LongTermSchemeStore.Base.SourceProgramName}」·" : "";
        _toolStatus.Text = $"⚡一键排产比选：{inh}按 {wls.Count} 条已指定工作线排出 {schemes.Count} 套【{note}】 → 推荐「{best?.Name}」(综合 {best?.Result?.CompositeScore:0}·服务年限 {best?.Result?.ServiceLifeYears:0}a·峰值剥采比 {best?.Result?.ProductionRatioPeak:0.0}) → 可直接「进度计划方案出图」";
        _host.Echo("规划计算：" + _toolStatus.Text, false);
    }

    private void RefreshDetail(LongTermPlan p)
    {
        _detailTitle.Text = $"选中方案：{p.Name}（{p.WorkLine.Caption}）";
        var r = p.Result;
        _pLife.Text = r != null ? $"{r.ServiceLifeYears:0} a" : "—";
        _pTtc.Text = r != null ? $"{r.TimeToCapacityYears:0} a（{r.DesignCalcYearLabel}）" : "—";
        _pPlateau.Text = r != null ? $"{r.StablePlateauYears:0} a" : "—";
        _pPeak.Text = r != null ? $"{r.ProductionRatioPeak:0.0} m³/t" : "—";
        _pBasic.Text = r != null ? $"{r.BasicStrippingYiM3:0.00} 亿m³" : "—";
        _pInner.Text = r != null ? $"{r.InnerDumpPct:0} %" : "—";
        _pNpv.Text = r != null ? $"{r.Npv:N0} 万元" : "—";
        _pPayback.Text = r != null ? $"{r.PaybackYears:0} a" : "—";
        _pOk.Text = r != null ? r.OkText : "—";
        _periodGrid.ItemsSource = null; _periodGrid.ItemsSource = p.Periods;
        LongTermCharts.DrawScheduleSheet(_chart, p);
    }

    private void OnScheduleAll()
    {
        var model = _host.ActiveBlockModel;
        if (!LongTermBlockSource.HasActiveBlockModel(model, out var note))
        { _toolStatus.Text = $"排不了：{note} —— 中长远的量只能来自块体（BM1）。请先「加载块体模型」并激活一个带煤属性的块体"; return; }
        var scope = _schemes.Where(z => z.Participate).ToList();
        var form = LongTermDumpBridge.TryReadForm(_host.Db);
        foreach (var s in scope) LongTermScheduler.Schedule(s, model, form);
        RefreshList();
        if (Current != null) RefreshDetail(Current);
        int solved = scope.Count(z => z.Result != null);
        _toolStatus.Text = $"已排产 {solved}/{scope.Count} 套【{note}】→ 选中查看逐年进度表；多套对比去「方案综合对比」" + (solved < scope.Count ? "；没排出来的看方案行的说明" : "");
    }

    private void OnScheduleSelected()
    {
        if (Current is not { } cur) { _toolStatus.Text = "请先选一个方案"; return; }
        LongTermScheduler.Schedule(cur, _host.ActiveBlockModel, LongTermDumpBridge.TryReadForm(_host.Db));
        RefreshList();
        RefreshDetail(cur);
        var r = cur.Result;
        _toolStatus.Text = r == null ? $"没排出来：{cur.ScheduleNote}" :
            $"已排产「{cur.Name}」：服务年限 {r.ServiceLifeYears:0}a · 达产 {r.TimeToCapacityYears:0}a({r.DesignCalcYearLabel}) · 峰值剥采比 {r.ProductionRatioPeak:0.0} · 内排率 {r.InnerDumpPct:0}%【{r.DumpSourceText}】"
          + (r.DumpFullYearLabel != "—" ? $" · ⚠{r.DumpFullYearLabel}年排土场排满(全期排不下 {r.DumpOverflowWanM3:N0} 万m³)" : "") + $" · NPV {r.Npv:N0}万 · {r.OkText}";
    }

    private void OnConfirmPlan()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先选一个方案"; return; }
        if (cur.Result == null) LongTermScheduler.Schedule(cur, _host.ActiveBlockModel, LongTermDumpBridge.TryReadForm(_host.Db));
        foreach (var s in _schemes) s.Note = s == cur ? "★已确定" : (s.Note == "★已确定" ? "" : s.Note);
        LongTermSchemeStore.Confirmed = cur;
        RefreshList();
        _bottomStatus.Text = $"✔ 已确定中长远主方案「{cur.Name}」（服务年限 {cur.Result?.ServiceLifeYears:0}a · 达产 {cur.Result?.DesignCalcYearLabel} · NPV {cur.Result?.Npv:N0}万）→ 可「进度计划方案出图」或喂短期/模拟";
        _host.Echo("规划计算：" + _bottomStatus.Text, false);
    }

    private async Task OnExportReportAsync()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先选一个方案再导出"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出进度计划报表", SuggestedFileName = $"中长远进度计划_{cur.Name}.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } }, new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } } },
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, BuildReport(cur), System.Text.Encoding.UTF8); _bottomStatus.Text = $"已导出报表：{file.Path.LocalPath}"; }
        catch (Exception ex) { _bottomStatus.Text = $"导出失败：{ex.Message}"; }
    }

    /// <summary>进度计划报表（CSV）：指标 + 逐年采掘进度表。供出图窗口复用。</summary>
    public static string BuildReport(LongTermPlan p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"中长远进度计划方案报表,{p.Name}");
        sb.AppendLine($"工作线·推进,{p.WorkLine.Caption}");
        sb.AppendLine($"来源,{p.SourceText}");
        sb.AppendLine();
        sb.AppendLine("=== 系统指标 ===");
        if (p.Result is { } r)
        {
            sb.AppendLine($"服务年限(a),{r.ServiceLifeYears:0}");
            sb.AppendLine($"达产时间(a),{r.TimeToCapacityYears:0}");
            sb.AppendLine($"设计计算年,{r.DesignCalcYearLabel}");
            sb.AppendLine($"稳产期(a),{r.StablePlateauYears:0}");
            sb.AppendLine($"峰值生产剥采比(m³/t),{r.ProductionRatioPeak:0.0}");
            sb.AppendLine($"基建剥离量(亿m³),{r.BasicStrippingYiM3:0.00}");
            sb.AppendLine($"量源,{r.QuantitySourceText}");
            sb.AppendLine($"均衡期数K,{r.BalanceStages}");
            sb.AppendLine($"峰值超前剥离(万m³),{r.PeakLeadStripWanM3:0}");
            sb.AppendLine($"内排率(%),{r.InnerDumpPct:0}");
            sb.AppendLine($"内排率来源,{r.DumpSourceText}");
            sb.AppendLine($"排土场排满年,{r.DumpFullYearLabel}");
            sb.AppendLine($"全期排不下(万m³),{r.DumpOverflowWanM3:0}");
            sb.AppendLine($"平均运距(km),{r.AvgHaulKm:0.0}");
            sb.AppendLine($"累计NPV(万元),{r.Npv:0}");
            sb.AppendLine($"投资回收期(a),{r.PaybackYears:0}");
            sb.AppendLine($"产量变异系数,{r.OutputCv:0.000}");
            sb.AppendLine($"剥采比变异系数,{r.RatioCv:0.000}");
            sb.AppendLine($"储量均衡系数,{r.ReserveBalanceCoef:0.00}");
            sb.AppendLine($"综合得分,{r.CompositeScore:0}");
            sb.AppendLine($"校核,{r.OkText}");
        }
        else sb.AppendLine("（未排产）");
        sb.AppendLine();
        sb.AppendLine("=== 逐年采掘进度表 ===");
        sb.AppendLine("年,采出(万t),剥离(万m³),生产剥采比,累计采出(万t),累计剥离(万m³),达产率%,推进度(m/a),时相,均衡段,段剥采比,超前剥离(万m³),排弃,内排(万m³),外排(万m³),排土推进(m),排到哪儿,排不下(万m³),现金流(万元),折现(万元),标记");
        foreach (var z in p.Periods)
            sb.AppendLine($"{z.Label},{z.CoalWanT:0},{z.StripWanM3:0},{z.Ratio:0.00},{z.CumCoal:0},{z.CumStrip:0},{z.CapacityPct:0},{z.AdvanceRateMpa:0},{z.PhaseText},{z.StageNo},{z.StageRatio:0.00},{z.LeadStripWanM3:0},{z.DumpText},{z.InnerDumpWanM3:0},{z.OuterDumpWanM3:0},{z.DumpAdvanceM:0.#},{z.DumpCellText},{z.DumpOverflowWanM3:0},{z.CashFlowWan:0},{z.NpvWan:0},{z.FlagText}");
        return sb.ToString();
    }
}
