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
using CalendarScenario = PitMine3D.Kylin.Cad.Plan.CalendarScenario;
using DispatchStrategy = PitMine3D.Kylin.Cad.Plan.DispatchStrategy;
using ShortTermComparer = PitMine3D.Kylin.Cad.Plan.ShortTermComparer;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using ShortTermScheduler = PitMine3D.Kylin.Cad.Plan.ShortTermScheduler;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「派生计划方案」窗口（短期组；移植原 <c>ShortTermDeriveWindow</c>）。在「短期生产计划编制」定的**单一基础约束**之上，
/// 按 作业组织 × 工作历方案 笛卡尔积派生候选方案，替换本窗口上一次派生的那批、各自编制并联合打分排名（对比矩阵 + 图表）。
/// </summary>
internal sealed class ShortTermDeriveWindow : Window
{
    private readonly ObservableCollection<ShortTermPlan> _schemes;
    /// <summary>本窗口上一次派生出来的那批。重新派生时只清这些 —— 方案库是共用的，「量驱动采剥接续」确定的方案也在里面。</summary>
    private readonly HashSet<ShortTermPlan> _derived = new();

    private readonly DataGrid _resultGrid;
    private readonly Canvas _outputCanvas = C(), _radarCanvas = C();
    private static Canvas C() => new() { Background = Brushes.White, ClipToBounds = true, MinHeight = 120 };
    private readonly CheckBox _chkBalanced = Chk("均衡型", true), _chkMultiFace = Chk("多面展开型", true), _chkConcentrated = Chk("集中强采型", true);
    private readonly CheckBox _chkCalStd = Chk("标准", true), _chkCalPush = Chk("抢产（增作业日）", false), _chkCalCons = Chk("保守（留天气余量）", false);
    private static CheckBox Chk(string t, bool on) => new() { Content = t, IsChecked = on, Margin = new Thickness(0, 3) };
    private readonly TextBlock _baseInfo = RoadUi.Hint("", 12), _genStatus = RoadUi.Hint("", 12), _bottomStatus = RoadUi.Hint("生成后系统自动按比选权重打分排名；选中一套点「确定」作为短期主方案。");

    public ShortTermDeriveWindow(ObservableCollection<ShortTermPlan>? schemes = null)
    {
        _schemes = schemes ?? ShortTermSchemeStore.Schemes;
        Title = "派生计划方案 — 作业组织 × 工作历 联合比选";
        PlanUi.Place(this, 1280, 800);

        _resultGrid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5 };
        _resultGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "参与", Binding = new Avalonia.Data.Binding("Participate") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(50) });
        _resultGrid.Columns.Add(new DataGridTextColumn { Header = "方案", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(170), IsReadOnly = true });   // 定宽：定宽列一多，星号列会被挤没
        _resultGrid.Columns.Add(PlanUi.FmtCol("完成率%", "Result.CompletionRatePct", 74, "{0:F1}"));
        _resultGrid.Columns.Add(PlanUi.FmtCol("月产CV", "Result.OutputCv", 68, "{0:F3}"));
        _resultGrid.Columns.Add(PlanUi.FmtCol("设备%", "Result.AvgEquipUtilPct", 62, "{0:F0}"));
        _resultGrid.Columns.Add(PlanUi.FmtCol("峰值月", "Result.PeakMonthLabel", 78));
        _resultGrid.Columns.Add(PlanUi.FmtCol("备采月", "Result.PreparedMonths", 66, "{0:F1}"));
        _resultGrid.Columns.Add(PlanUi.FmtCol("综合分", "Result.CompositeScore", 66, "{0:F0}"));
        _resultGrid.Columns.Add(PlanUi.FmtCol("校核", "Result.OkText", 66));
        _resultGrid.Columns.Add(PlanUi.FmtCol("标记", "Note", 70));
        PlanUi.FitHeaders(_resultGrid);
        _resultGrid.ItemsSource = _schemes;
        _resultGrid.SelectionChanged += (_, _) => Redraw();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = PlanUi.Header("派生计划方案 · 联合比选", "在基础约束上按「作业组织 × 工作历方案」正交派生多套，各自编制 → 对比矩阵 + 雷达 + 逐月产量对比 → 方向感知加权评分 → 推荐/确定", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xC2, 0x41, 0x0C));
        Grid.SetRow(header, 0); root.Children.Add(header);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("290,*,380") };
        var left = new StackPanel { Margin = new Thickness(12, 12, 6, 12) };
        _baseInfo.Margin = new Thickness(0, 0, 0, 10); left.Children.Add(_baseInfo);
        var g1 = new StackPanel(); g1.Children.Add(_chkBalanced); g1.Children.Add(_chkMultiFace); g1.Children.Add(_chkConcentrated);
        left.Children.Add(PlanUi.Group("作业组织轴", g1, new Thickness(0, 0, 0, 10), 8));
        var g2 = new StackPanel(); g2.Children.Add(_chkCalStd); g2.Children.Add(_chkCalPush); g2.Children.Add(_chkCalCons);
        left.Children.Add(PlanUi.Group("工作历方案轴", g2, new Thickness(0, 0, 0, 10), 8));
        var gen = RoadUi.Btn("⚡ 生成并编制", OnGenerate, 0, bold: true); gen.Height = 36; gen.HorizontalAlignment = HorizontalAlignment.Stretch; gen.HorizontalContentAlignment = HorizontalAlignment.Center;
        left.Children.Add(gen);
        _genStatus.Margin = new Thickness(0, 8, 0, 0); left.Children.Add(_genStatus);
        Grid.SetColumn(left, 0); body.Children.Add(left);

        var mid = PlanUi.Group("对比矩阵（方向感知归一加权评分；★=推荐）", _resultGrid, new Thickness(6, 12, 6, 12), 6);
        Grid.SetColumn(mid, 1); body.Children.Add(mid);

        var right = new Grid { RowDefinitions = new RowDefinitions("*,*"), Margin = new Thickness(6, 12, 12, 12) };
        var r0 = PlanUi.Group("逐月产量对比（各方案 + 目标月均线）", _outputCanvas, new Thickness(0, 0, 0, 6), 6); Grid.SetRow(r0, 0); right.Children.Add(r0);
        var r1 = PlanUi.Group("多指标综合雷达（完成/均衡/设备/削峰/推进）", _radarCanvas, new Thickness(0), 6); Grid.SetRow(r1, 1); right.Children.Add(r1);
        Grid.SetColumn(right, 2); body.Children.Add(right);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var foot = new DockPanel();
        var confirm = new Button
        {
            Content = "✔ 确定选中为主方案", MinWidth = 160, Height = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.FromRgb(0xF9, 0x73, 0x16), 0), new GradientStop(Color.FromRgb(0xC2, 0x41, 0x0C), 1) } },
        };
        confirm.Click += (_, _) => OnConfirm();
        DockPanel.SetDock(confirm, Avalonia.Controls.Dock.Right); foot.Children.Add(confirm);
        var export = RoadUi.Btn("导出选中报表", async () => await OnExportAsync(), 110); export.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(export, Avalonia.Controls.Dock.Right); foot.Children.Add(export);
        _bottomStatus.VerticalAlignment = VerticalAlignment.Center; foot.Children.Add(_bottomStatus);
        var footB = PlanUi.Footer(foot); Grid.SetRow(footB, 2); root.Children.Add(footB);
        Content = root;

        foreach (var c in new[] { _outputCanvas, _radarCanvas }) c.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Redraw(); };
        ShowBaseInfo();
        Redraw();
    }

    private void ShowBaseInfo()
    {
        var b = ShortTermSchemeStore.Base;
        _baseInfo.Text = $"基础约束：{b.PlanYear}年 · 年采出 {b.AnnualCoalTargetWanT:N0}万t · 年剥离 {b.AnnualStripTargetWanM3:N0}万m³ · 月上限 {b.MonthlyCoalCeilingWanT:0}万t · {b.SourceText}（在「短期生产计划编制」里改）";
    }

    /// <summary>⚡ 生成并编制（自检直通亦走此）。</summary>
    public void OnGenerate()
    {
        var b = ShortTermSchemeStore.Base;
        var dis = new List<ShortTermScheduler.DispatchSpec>();
        if (_chkBalanced.IsChecked == true) dis.Add(new("均衡型", DispatchStrategy.Balanced));
        if (_chkMultiFace.IsChecked == true) dis.Add(new("多面展开", DispatchStrategy.MultiFace));
        if (_chkConcentrated.IsChecked == true) dis.Add(new("集中强采", DispatchStrategy.Concentrated));
        var cals = new List<ShortTermScheduler.CalendarSpec>();
        if (_chkCalStd.IsChecked == true) cals.Add(new("标准", CalendarScenario.Standard));
        if (_chkCalPush.IsChecked == true) cals.Add(new("抢产", CalendarScenario.Push));
        if (_chkCalCons.IsChecked == true) cals.Add(new("保守", CalendarScenario.Conservative));
        if (dis.Count == 0 || cals.Count == 0) { _genStatus.Text = "请至少各勾选一个作业组织与工作历方案"; return; }

        var variants = ShortTermScheduler.Generate(b, dis, cals, schedule: true);

        // ★ 只清【本窗口上一次派生的那批】，不 Clear 整个方案库（「量驱动采剥接续」确定的几何计划也在库里）。
        for (int i = _schemes.Count - 1; i >= 0; i--) if (_derived.Contains(_schemes[i])) _schemes.RemoveAt(i);
        _derived.Clear();
        foreach (var v in variants) { _schemes.Add(v); _derived.Add(v); }
        ShortTermConfirmService.ClearIfDropped();   // 选定项只在它自己被清掉时才清空

        string best = ShortTermComparer.Score(_schemes.Where(s => s.Participate && s.Result != null).ToList());
        foreach (var s in _schemes) s.Note = s.Name == best ? "★推荐" : "";
        _resultGrid.ItemsSource = null; _resultGrid.ItemsSource = _schemes;
        Redraw();
        _genStatus.Text = $"{dis.Count}×{cals.Count} = {variants.Count} 套已生成并编制";
        _bottomStatus.Text = $"已生成 {variants.Count} 套候选（替换方案库），按比选权重打分排名 → 推荐「{best}」；选中一套点「确定」作为短期主方案";
    }

    private void Redraw()
    {
        var list = _schemes.Where(s => s.Participate && s.Months.Count > 0).ToList();
        if (list.Count == 0) list = _schemes.ToList();
        ShortTermCharts.DrawMonthlyOutput(_outputCanvas, list);
        ShortTermCharts.DrawRadar(_radarCanvas, list);
    }

    private ShortTermPlan? Current => _resultGrid.SelectedItem as ShortTermPlan;

    private void OnConfirm()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先在矩阵里选一套方案"; return; }
        if (cur.Result == null) ShortTermScheduler.Schedule(cur);
        var o = ShortTermConfirmService.Confirm(cur);   // 与「月度计划编制」走同一个实现（单套 vs 多套的差别，不是"一个能确定一个不能"）
        var sel = Current; _resultGrid.ItemsSource = null; _resultGrid.ItemsSource = _schemes; _resultGrid.SelectedItem = sel;
        _bottomStatus.Text = o.Ok ? o.Message + "\n→ 喂作业计划 / 进度模拟" : "✗ " + o.Err;
    }

    private async Task OnExportAsync()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先在矩阵里选一套方案再导出"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出月度计划报表", SuggestedFileName = $"短期月度计划_{cur.Name}.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } }, new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } } },
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, ShortTermSolveWindow.BuildReport(cur), System.Text.Encoding.UTF8); _bottomStatus.Text = $"已导出报表：{file.Path.LocalPath}"; }
        catch (Exception ex) { _bottomStatus.Text = $"导出失败：{ex.Message}"; }
    }

    internal string SelftestStatus => _bottomStatus.Text ?? "";
    internal int SelftestCount => _schemes.Count;
}
