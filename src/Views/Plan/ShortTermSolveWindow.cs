using System;
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
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using ShortTermScheduler = PitMine3D.Kylin.Cad.Plan.ShortTermScheduler;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「月度计划编制 · 排产」窗口（短期组第 2 大按钮；移植原 <c>ShortTermSolveWindow</c>）。一键编制 → 选中方案的逐月计划图 +
/// 指标 + 逐月配置表(输入) / 逐月计划表(结果) → 确定入库 / 导出。多套联合对比在「派生计划方案」窗口。
/// </summary>
internal sealed class ShortTermSolveWindow : Window
{
    private readonly ObservableCollection<ShortTermPlan> _schemes;
    private readonly DataGrid _schemeList, _monthGrid;
    private readonly MonthlyTargetGrid _targetGrid = new();
    private readonly Canvas _chart = new() { Background = Brushes.White, ClipToBounds = true, MinHeight = 200 };
    private readonly TextBlock _toolStatus = RoadUi.Hint(""), _bottomStatus = RoadUi.Hint("编制后点「确定月度计划」→ 作为短期主方案（喂作业计划 / 进度模拟）；多套对比去「派生计划方案」");
    private readonly TextBlock _detailTitle = RoadUi.Text("选中方案：—", 13, bold: true);
    private readonly TextBlock _pCoal = B("—"), _pStrip = B("—"), _pComp = B("—", PlanUi.OrangeBrush), _pRatio = B("—"), _pPeak = B("—"), _pUtil = B("—", PlanUi.OrangeBrush), _pCv = B("—"), _pPrep = B("—"), _pOk = B("—");
    private static TextBlock B(string t, IBrush? fg = null)
    {
        var tb = new TextBlock { Text = t, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap };
        if (fg != null) tb.Foreground = fg; else RoadUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Primary");
        return tb;
    }

    public ShortTermSolveWindow(ObservableCollection<ShortTermPlan>? schemes = null)
    {
        _schemes = schemes is { Count: > 0 } ? schemes : ShortTermSchemeStore.Schemes;
        Title = "月度计划编制 — 排产";
        PlanUi.Place(this, 1280, 820);

        _schemeList = new DataGrid { AutoGenerateColumns = false, IsReadOnly = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5 };
        _schemeList.Columns.Add(new DataGridCheckBoxColumn { Header = "编制", Binding = new Avalonia.Data.Binding("Participate") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(50) });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "方案", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Avalonia.Data.Binding("SolvedText"), Width = new DataGridLength(62), IsReadOnly = true });
        PlanUi.FitHeaders(_schemeList);
        _schemeList.ItemsSource = _schemes;
        _schemeList.SelectionChanged += (_, _) => { if (Current != null) RefreshDetail(Current); };

        _monthGrid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5, MaxHeight = 220 };
        _monthGrid.Columns.Add(PlanUi.FmtCol("月", "Label", 88));
        _monthGrid.Columns.Add(PlanUi.FmtCol("采出(万t)", "CoalWanT", 84, "{0:F1}"));
        _monthGrid.Columns.Add(PlanUi.FmtCol("剥离(万m³)", "StripWanM3", 92, "{0:N0}"));
        _monthGrid.Columns.Add(PlanUi.FmtCol("剥采比", "Ratio", 66, "{0:F2}"));
        _monthGrid.Columns.Add(PlanUi.FmtCol("作业日", "Workdays", 64, "{0:F1}"));
        _monthGrid.Columns.Add(PlanUi.FmtCol("推进(m)", "AdvanceM", 72, "{0:F1}"));
        _monthGrid.Columns.Add(PlanUi.FmtCol("设备%", "EquipUtilPct", 64, "{0:F0}"));
        _monthGrid.Columns.Add(PlanUi.FmtCol("完成%", "CompletionPct", 64, "{0:F0}"));
        _monthGrid.Columns.Add(PlanUi.FmtCol("排弃", "DumpText", 60));                 // 三维模拟按它画内排/外排
        _monthGrid.Columns.Add(PlanUi.FmtCol("量来源", "FlowSourceText", 76));         // 流派生 / 份额摊分：两者数看着一样，含义完全不同
        _monthGrid.Columns.Add(PlanUi.FmtCol("主作业面", "ActiveFace", 0));
        _monthGrid.Columns.Add(PlanUi.FmtCol("", "FlagText", 72));
        PlanUi.FitHeaders(_monthGrid);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        var header = PlanUi.Header("月度计划编制 · 排产", "一键编制：划月 → 工作历×设备×组织 摊年目标 → 月度均衡削峰 → 推进/设备利用 → 拆物料流配去向扣库容 → 逐月计划表 + 指标", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xC2, 0x41, 0x0C));
        Grid.SetRow(header, 0); root.Children.Add(header);
        var tool = BuildTool(); Grid.SetRow(tool, 1); root.Children.Add(tool);
        var body = BuildBody(); Grid.SetRow(body, 2); root.Children.Add(body);
        var tabs = new TabControl { Margin = new Thickness(12, 0, 12, 6) };
        tabs.Items.Add(new TabItem { Header = "① 逐月配置表（设定 · 编制的输入）", FontSize = 13, Content = new Border { Margin = new Thickness(6), Child = _targetGrid } });
        tabs.Items.Add(new TabItem { Header = "② 逐月计划表（结果 · 采出/剥离/剥采比/作业日/推进/设备利用/完成率/排弃/主作业面）", FontSize = 13, Content = new Border { Margin = new Thickness(6), Child = _monthGrid } });
        tabs.SelectedIndex = 1;
        Grid.SetRow(tabs, 3); root.Children.Add(tabs);
        var foot = BuildFooter(); Grid.SetRow(foot, 4); root.Children.Add(foot);
        Content = root;

        _chart.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty && Current != null) ShortTermCharts.DrawScheduleSheet(_chart, Current); };
        _targetGrid.Edited += OnTargetEdited;      // 配置表改动 → 只提示要重编，不自动重排
        if (_schemes.Count > 0) _schemeList.SelectedIndex = 0;
        if (Current != null) RefreshDetail(Current);
    }

    private ShortTermPlan? Current => _schemeList.SelectedItem as ShortTermPlan;

    private Control BuildTool()
    {
        var tool = new DockPanel();
        var b1 = RoadUi.Btn("⚡ 一键编制(全部参与)", OneClickSchedule, 160, bold: true);
        var b2 = RoadUi.Btn("编制选中", OnScheduleSelected, 90);
        foreach (var b in new[] { b1, b2 }) { DockPanel.SetDock(b, Avalonia.Controls.Dock.Left); tool.Children.Add(b); }
        _toolStatus.VerticalAlignment = VerticalAlignment.Center; tool.Children.Add(_toolStatus);
        var border = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = tool };
        RoadUi.Theme(border, Border.BorderBrushProperty, "Theme.Panel.Border"); RoadUi.Theme(border, Border.BackgroundProperty, "Theme.Panel.Background");
        return border;
    }

    private Control BuildBody()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*,420") };
        var left = PlanUi.Group("方案列表（勾选参与编制）", _schemeList, new Thickness(12, 12, 6, 6), 6);
        Grid.SetColumn(left, 0); g.Children.Add(left);
        var mid = PlanUi.Group("逐月生产计划图（产量柱 + 目标月均线 + 生产剥采比折线 · 灰=检修月 深橙=峰值月）", _chart, new Thickness(6, 12, 6, 6), 6);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        var detail = new StackPanel();
        _detailTitle.Margin = new Thickness(0, 0, 0, 8); _detailTitle.TextWrapping = TextWrapping.Wrap; detail.Children.Add(_detailTitle);
        var ug = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        void Pair(string k, TextBlock v) { ug.Children.Add(RoadUi.Hint(k, 12.5)); ug.Children.Add(v); }
        Pair("全期采出", _pCoal); Pair("全期剥离", _pStrip); Pair("年目标完成率", _pComp); Pair("平均生产剥采比", _pRatio); Pair("峰值月", _pPeak);
        Pair("平均设备利用率", _pUtil); Pair("月产变异系数", _pCv); Pair("备采保有", _pPrep); Pair("校核", _pOk);
        detail.Children.Add(ug);
        var right = PlanUi.Group("月度计划指标", detail, new Thickness(6, 12, 12, 6), 8);
        Grid.SetColumn(right, 2); g.Children.Add(right);
        return g;
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var confirm = new Button
        {
            Content = "✔ 确定月度计划", MinWidth = 150, Height = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.FromRgb(0xF9, 0x73, 0x16), 0), new GradientStop(Color.FromRgb(0xC2, 0x41, 0x0C), 1) } },
        };
        confirm.Click += (_, _) => OnConfirmPlan();
        DockPanel.SetDock(confirm, Avalonia.Controls.Dock.Right); dock.Children.Add(confirm);
        var export = RoadUi.Btn("导出报表", async () => await OnExportReportAsync(), 90); export.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(export, Avalonia.Controls.Dock.Right); dock.Children.Add(export);
        _bottomStatus.VerticalAlignment = VerticalAlignment.Center; dock.Children.Add(_bottomStatus);
        return PlanUi.Footer(dock);
    }

    private void RefreshList() { var sel = Current; _schemeList.ItemsSource = null; _schemeList.ItemsSource = _schemes; if (sel != null) _schemeList.SelectedItem = sel; }

    private void RefreshDetail(ShortTermPlan p)
    {
        _detailTitle.Text = $"选中方案：{p.Name}（{p.Caption}）";
        var r = p.Result;
        _pCoal.Text = r != null ? $"{r.TotalCoalWanT:N0} 万t" : "—";
        _pStrip.Text = r != null ? $"{r.TotalStripWanM3:N0} 万m³" : "—";
        _pComp.Text = r != null ? $"{r.CompletionRatePct:0.0} %" : "—";
        _pRatio.Text = r != null ? $"{r.AvgRatio:0.00} m³/t" : "—";
        _pPeak.Text = r != null ? $"{r.PeakMonthLabel}（{r.PeakMonthCoalWanT:0.0}万t）" : "—";
        _pUtil.Text = r != null ? $"{r.AvgEquipUtilPct:0} %" : "—";
        _pCv.Text = r != null ? $"{r.OutputCv:0.000}" : "—";
        _pPrep.Text = r != null ? $"{r.PreparedMonths:0.0} 月" : "—";
        _pOk.Text = r != null ? r.OkText : "—";
        _monthGrid.ItemsSource = null; _monthGrid.ItemsSource = p.Months;
        ShortTermCharts.DrawScheduleSheet(_chart, p);
    }

    /// <summary>⚡ 一键编制（全部参与）。</summary>
    public void OneClickSchedule()
    {
        var scope = _schemes.Where(z => z.Participate).ToList();
        foreach (var s in scope) ShortTermScheduler.Schedule(s);
        RefreshList();
        if (Current != null) RefreshDetail(Current);
        _toolStatus.Text = $"已编制 {scope.Count} 套（参与编制的方案）→ 选中查看逐月计划表；多套对比去「派生计划方案」";
    }

    private void OnScheduleSelected()
    {
        if (Current is not { } cur) { _toolStatus.Text = "请先选一个方案"; return; }
        ShortTermScheduler.Schedule(cur);
        RefreshList();
        RefreshDetail(cur);
        var r = cur.Result;
        _toolStatus.Text = r == null ? "编制失败" :
            $"已编制「{cur.Name}」：完成率 {r.CompletionRatePct:0.0}% · 平均剥采比 {r.AvgRatio:0.00} · 峰值月 {r.PeakMonthLabel} · 设备利用 {r.AvgEquipUtilPct:0}% · {r.OkText}"
            + $" · 内排率 {r.InternalDumpPct:0.#}% · 运输功 {r.TransportWorkWanTKm:N0}万t·km · {r.DumpOkText}" + (r.DumpOk ? "" : "（详见「采排配对」面板）");
    }

    /// <summary>配置表被改过 —— 只提示，不自动重编（它是编制的输入，什么时候重算由人决定）。</summary>
    private void OnTargetEdited()
        => _bottomStatus.Text = "逐月配置表已改动 —— 点「⚡ 一键编制」才会按新配置重排。（它是编制的输入，不是结果；已确定的方案在重新确定之前不变）\n" + _targetGrid.ReconcileText();

    private void OnConfirmPlan()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先选一个方案"; return; }
        if (cur.Result == null) ShortTermScheduler.Schedule(cur);   // 编制是本窗口的动作：没算过就先算一遍
        var o = ShortTermConfirmService.Confirm(cur);               // 确定入库走唯一实现：写确定簿 + 写 monthly_plan 台账 + 下游就绪自检
        RefreshList();
        _bottomStatus.Text = o.Ok ? o.Message + "\n→ 可派生比选或喂作业计划/进度模拟" : "✗ " + o.Err;
    }

    private async Task OnExportReportAsync()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先选一个方案再导出"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出月度计划报表", SuggestedFileName = $"短期月度计划_{cur.Name}.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } }, new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } } },
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, BuildReport(cur), System.Text.Encoding.UTF8); _bottomStatus.Text = $"已导出报表：{file.Path.LocalPath}"; }
        catch (Exception ex) { _bottomStatus.Text = $"导出失败：{ex.Message}"; }
    }

    /// <summary>月度计划报表（CSV）：指标 + 逐月计划表 + 采排配对明细 + 警示。供出图/派生窗口复用。</summary>
    internal static string BuildReport(ShortTermPlan p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"短期(月度)生产计划方案报表,{p.Name}");
        sb.AppendLine($"作业组织·工作历,{p.DispatchText}·{p.CalendarText}");
        sb.AppendLine($"来源,{p.SourceText}");
        sb.AppendLine($"年采出目标(万t),{p.AnnualCoalTargetWanT:0}");
        sb.AppendLine($"年剥离目标(万m³),{p.AnnualStripTargetWanM3:0}");
        sb.AppendLine();
        sb.AppendLine("=== 系统指标 ===");
        if (p.Result is { } r)
        {
            sb.AppendLine($"全期采出(万t),{r.TotalCoalWanT:0}");
            sb.AppendLine($"全期剥离(万m³),{r.TotalStripWanM3:0}");
            sb.AppendLine($"年目标完成率(%),{r.CompletionRatePct:0.0}");
            sb.AppendLine($"平均生产剥采比(m³/t),{r.AvgRatio:0.00}");
            sb.AppendLine($"峰值月,{r.PeakMonthLabel}");
            sb.AppendLine($"峰值月采出(万t),{r.PeakMonthCoalWanT:0.0}");
            sb.AppendLine($"月产变异系数,{r.OutputCv:0.000}");
            sb.AppendLine($"剥采比变异系数,{r.RatioCv:0.000}");
            sb.AppendLine($"平均设备利用率(%),{r.AvgEquipUtilPct:0}");
            sb.AppendLine($"全期推进(m),{r.AdvanceTotalM:0}");
            sb.AppendLine($"备采保有(月),{r.PreparedMonths:0.0}");
            sb.AppendLine($"月产均衡系数,{r.BalanceCoef:0.00}");
            sb.AppendLine($"综合得分,{r.CompositeScore:0}");
            sb.AppendLine($"校核,{r.OkText}");
            sb.AppendLine($"全期排弃占容(万m³),{r.TotalDumpedWanM3:0}");
            sb.AppendLine($"内排率(%),{r.InternalDumpPct:0.0}");
            sb.AppendLine($"全期运输功(万t·km),{r.TransportWorkWanTKm:0}");
            sb.AppendLine($"吨量加权平均运距(km),{r.WeightedAvgHaulKm:0.00}");
            sb.AppendLine($"去向来源,{r.DumpSourceText}");
            sb.AppendLine($"库容校核,{r.DumpOkText}");
        }
        else sb.AppendLine("（未编制）");
        sb.AppendLine();
        sb.AppendLine("=== 逐月计划表 ===");
        sb.AppendLine("月,采出(万t),剥离(万m³实方),生产剥采比,有效作业日,推进(m),设备利用%,累计完成%,累计采出(万t),排弃占容(万m³),内排率%,运输功(万t·km),加权运距(km),内/外排,主作业面,标记");
        foreach (var z in p.Months)
            sb.AppendLine($"{z.Label},{z.CoalWanT:0.0},{z.StripWanM3:0},{z.Ratio:0.00},{z.Workdays:0.0},{z.AdvanceM:0.0},{z.EquipUtilPct:0},{z.CompletionPct:0.0},{z.CumCoal:0.0}," +
                          $"{z.DumpedWanM3:0},{z.InternalDumpPct:0.0},{z.TransportWorkWanTKm:0},{z.WeightedAvgHaulKm:0.00},{z.DumpText},{z.ActiveFace},{z.FlagText}");
        if (p.Months.Any(z => z.HasFlows))
        {
            sb.AppendLine();
            sb.AppendLine("=== 采排配对明细（源—汇物料流）===");
            sb.AppendLine("月,作业面,物料,原位实方(万m³),吨量(万t),占容方(万m³),去向,去向类别,运距(km),等效运距(km),运输功(万t·km)");
            foreach (var z in p.Months)
                foreach (var f in z.Flows)
                    sb.AppendLine($"{z.Label},{f.SourceName},{f.MaterialName},{f.InSituWanM3:0.##},{f.TonnageWanT:0.##},{(f.IsDumping ? f.DumpWanM3 : 0):0.##}," +
                                  $"{f.DestinationName},{f.DestinationKindText},{f.HaulKm:0.##},{f.EquivHaulKm:0.##},{f.TransportWorkWanTKm:0.#}");
        }
        if (p.Result is { Warnings.Count: > 0 } rw)
        {
            sb.AppendLine();
            sb.AppendLine("=== 库容/去向警示 ===");
            foreach (var w in rw.Warnings) sb.AppendLine(w.Replace(",", "，"));
        }
        return sb.ToString();
    }
}
