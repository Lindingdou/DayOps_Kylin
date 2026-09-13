using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「派生计划方案」窗口（中长远组第 2 按钮；移植原 <c>LongTermDeriveWindow</c>）。在「中长远进度计划编制」定的单一基础约束之上，
/// 按【人为指定的工作线】[× 能力档 × 达产节奏] 派生候选方案，替换共享方案库并各自排产。工作线只能从图上拾取（LT1）；一条工作线 = 一套方案（LT2）。
/// </summary>
internal sealed class LongTermDeriveWindow : Window
{
    private readonly ObservableCollection<LongTermPlan> _schemes;
    private readonly IPlanEntityHost _host;
    private readonly Action<Window> _openBlockPicker;
    private readonly ObservableCollection<WorkLineAdvanceVariant> _workLines = new();

    private readonly TextBlock _baseInfo = RoadUi.Hint("基础约束：—");
    private readonly ListBox _wlList = new() { Height = 118, SelectionMode = SelectionMode.Multiple };
    private readonly CheckBox _capBase = new() { Content = "基准 A_p", IsChecked = true, Margin = new Thickness(0, 2, 14, 2) }, _capHi = new() { Content = "+20%", Margin = new Thickness(0, 2, 14, 2) }, _capLo = new() { Content = "−20%", Margin = new Thickness(0, 2, 14, 2) };
    private readonly CheckBox _rampLinear = new() { Content = "线性", IsChecked = true, Margin = new Thickness(0, 2, 14, 2) }, _rampStep = new() { Content = "阶梯", Margin = new Thickness(0, 2, 14, 2) }, _rampFast = new() { Content = "尽快达产", Margin = new Thickness(0, 2, 14, 2) };
    private readonly TextBlock _genStatus = RoadUi.Hint("尚未指定工作线"), _bottomStatus = RoadUi.Hint("生成会替换当前方案库为本批候选；不勾任何能力/节奏即不在该轴上变。工作线只能从图上拾取。");
    private readonly DataGrid _resultGrid;

    public LongTermDeriveWindow(IPlanEntityHost host, Action<Window> openBlockPicker)
    {
        _host = host; _openBlockPicker = openBlockPicker;
        _schemes = LongTermSchemeStore.Schemes;
        Title = "派生计划方案";
        PlanUi.Place(this, 1000, 720);

        _resultGrid = PlanUi.Table(new (string, string, double)[]
        {
            ("方案", "Name", 150), ("工作线·推进", "WorkLineCaption", 0), ("A_p(万t/a)", "CapText", 96), ("服务年限(a)", "RLife", 96), ("峰值剥采比", "RPeak", 96),
            ("内排率%", "RInner", 80), ("排满年", "RFullYear", 72), ("NPV(万)", "RNpv", 106), ("综合得分", "RScore", 84),
        }, multi: false);
        _resultGrid.Columns[1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);   // 工作线·推进 吃余宽（方案名定宽，免得被定宽列挤没）
        _resultGrid.ItemsSource = _schemes;
        _wlList.ItemsSource = _workLines;
        _wlList.DisplayMemberBinding = new Avalonia.Data.Binding("Display");

        foreach (var wl in _schemes.Select(s => s.WorkLine).Where(w => w.FromEntity).GroupBy(w => w.SourceHandle).Select(g => g.First().Copy()))
            _workLines.Add(wl);
        foreach (var cb in new[] { _capBase, _capHi, _capLo, _rampLinear, _rampStep, _rampFast }) cb.IsCheckedChanged += (_, _) => UpdateCount();

        Content = PlanUi.Shell(
            PlanUi.Header("派生计划方案", "在「中长远进度计划编制」定的基础约束之上，按【人为指定的工作线】[× 能力档 × 达产节奏] 笛卡尔积派生多套候选，各自排产 → 联合对比", Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x1E, 0x40, 0xAF)),
            BuildBody(), BuildFooter());
        ShowBaseInfo();
        UpdateCount();
        Activated += (_, _) => ShowBaseInfo();
    }

    private Control BuildBody()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(12, 8, 12, 8) };
        var info = PlanUi.InfoBox(_baseInfo, new Thickness(0, 0, 0, 8));
        Grid.SetRow(info, 0); root.Children.Add(info);

        var axes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var wlPanel = new StackPanel();
        wlPanel.Children.Add(RoadUi.Text("工作线（人为指定 · 一条 = 一套方案）", 13, bold: true));
        wlPanel.Children.Add(RoadUi.Hint("在图上选中 1 条工作线 → 点「拾取选中工作线」。L / 推进方位 / 平行·扇形 全部从实体量取；方向长在实体的箭头上。"));
        wlPanel.Children.Add(_wlList);
        var wlBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        wlBtns.Children.Add(RoadUi.Btn("拾取选中工作线", OnPickWorkLine, 120));
        var bRef = RoadUi.Btn("按实体刷新", OnRefreshWorkLines, 86); ToolTip.SetTip(bRef, "按 handle 重读图上实体的最新推进方向与长度（在图上拖过箭头之后用）");
        wlBtns.Children.Add(bRef);
        wlBtns.Children.Add(RoadUi.Btn("移除", () => { foreach (var wl in _wlList.SelectedItems!.Cast<WorkLineAdvanceVariant>().ToList()) _workLines.Remove(wl); UpdateCount(); }, 60));
        wlBtns.Children.Add(RoadUi.Btn("清空", () => { _workLines.Clear(); UpdateCount(); }, 60));
        wlPanel.Children.Add(wlBtns);
        var wlGroup = PlanUi.Group("", wlPanel, new Thickness(0, 0, 8, 0), 8);
        Grid.SetColumn(wlGroup, 0); axes.Children.Add(wlGroup);

        var capPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        capPanel.Children.Add(RoadUi.Text("能力档（相对基准 A_p；可选）", 13, bold: true));
        capPanel.Children.Add(_capBase); capPanel.Children.Add(_capHi); capPanel.Children.Add(_capLo);
        var capGroup = PlanUi.Group("", capPanel, new Thickness(0, 0, 8, 0), 8);
        Grid.SetColumn(capGroup, 1); axes.Children.Add(capGroup);

        var rampPanel = new StackPanel();
        rampPanel.Children.Add(RoadUi.Text("达产节奏（可选）", 13, bold: true));
        rampPanel.Children.Add(_rampLinear); rampPanel.Children.Add(_rampStep); rampPanel.Children.Add(_rampFast);
        var rampGroup = PlanUi.Group("", rampPanel, new Thickness(0), 8);
        Grid.SetColumn(rampGroup, 2); axes.Children.Add(rampGroup);

        var top = new StackPanel();
        top.Children.Add(axes);
        var gen = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        gen.Children.Add(RoadUi.Btn("生成多套方案", OnGenerate, 130, bold: true));
        var bLoad = RoadUi.Btn("加载块体模型", () => { _openBlockPicker(this); _genStatus.Text = LongTermBlockSource.HasActiveBlockModel(_host.ActiveBlockModel, out var note) ? $"量源就绪：{note}" : $"仍无可用量源：{note}"; }, 110);
        ToolTip.SetTip(bLoad, "中长远的逐年采出/剥离量只能来自块体（BM1）。这里可以列出/激活/导入块体模型");
        gen.Children.Add(bLoad);
        _genStatus.VerticalAlignment = VerticalAlignment.Center; _genStatus.Margin = new Thickness(12, 0, 0, 0);
        gen.Children.Add(_genStatus);
        top.Children.Add(gen);
        Grid.SetRow(top, 1); root.Children.Add(top);

        var res = PlanUi.Group("候选进度计划方案（= 当前方案库；已各自排产 → 去「方案综合对比」联合比选 / 「规划计算」看逐年表）", _resultGrid, new Thickness(0), 6);
        Grid.SetRow(res, 2); root.Children.Add(res);
        return root;
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var close = RoadUi.Btn("关闭", Close, 70); close.Margin = new Thickness(0);
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Right); dock.Children.Add(close);
        _bottomStatus.VerticalAlignment = VerticalAlignment.Center; dock.Children.Add(_bottomStatus);
        return PlanUi.Footer(dock);
    }

    private void ShowBaseInfo()
    {
        var b = LongTermSchemeStore.Base;
        _baseInfo.Text = $"基础约束：A_p {b.DesignCapacityWanTa:0} 万t/a · 可采储量 {b.CoalReserveWanT:N0} 万t · 基准剥采比 {b.BaseStripRatio:0.0} · n经 {b.EconomicStripRatioMax:0} · {b.RampText} · {b.SourceText}（在「中长远进度计划编制」里改）";
    }

    /// <summary>自检直通：拾取当前选中工作线 → 生成多套方案（不模拟鼠标）。</summary>
    internal string SelftestPickAndGenerate() { OnPickWorkLine(); OnGenerate(); return _genStatus.Text; }

    private void OnPickWorkLine()
    {
        var r = WorkLinePicker.PickSelected(_host);
        if (!r.Success) { _genStatus.Text = r.Message; return; }
        var wl = r.Variant!;
        var dup = wl.SourceHandle != 0 ? _workLines.FirstOrDefault(x => x.SourceHandle == wl.SourceHandle) : null;
        if (dup != null)
        {
            wl.Label = dup.Label;
            _workLines[_workLines.IndexOf(dup)] = wl;
            _genStatus.Text = $"已更新同一条工作线：{wl.Caption}";
        }
        else
        {
            wl.Label = $"工作线{_workLines.Count + 1}";
            _workLines.Add(wl);
            _genStatus.Text = r.Message;
        }
        UpdateCount();
    }

    private void OnRefreshWorkLines()
    {
        int ok = 0, lost = 0;
        var refreshed = _workLines.ToList();
        foreach (var wl in refreshed) { if (WorkLinePicker.Refresh(_host, wl)) ok++; else lost++; }
        _workLines.Clear();
        foreach (var wl in refreshed) _workLines.Add(wl);
        _genStatus.Text = lost == 0 ? $"已按图上实体刷新 {ok} 条工作线" : $"已刷新 {ok} 条；{lost} 条读不到实体（已删除或非工作线）——它们仍按上次量到的数参与派生";
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (_workLines.Count == 0) { _genStatus.Text = "尚未指定工作线 —— 在图上选中 1 条工作线后点「拾取选中工作线」"; return; }
        int caps = Math.Max(1, new[] { _capBase, _capHi, _capLo }.Count(b => b.IsChecked == true));
        int ramps = Math.Max(1, new[] { _rampLinear, _rampStep, _rampFast }.Count(b => b.IsChecked == true));
        _genStatus.Text = $"{_workLines.Count} 条工作线 × {caps} 能力档 × {ramps} 节奏 → {_workLines.Count * caps * ramps} 套";
    }

    private void OnGenerate()
    {
        var b = LongTermSchemeStore.Base;
        if (_workLines.Count == 0) { _genStatus.Text = "请先指定工作线：图上选中 1 条工作线 → 「拾取选中工作线」（代码里没有默认工作线）"; return; }

        var caps = new List<LongTermScheduler.CapacitySpec>();
        if (_capBase.IsChecked == true) caps.Add(new("基准", 1.0));
        if (_capHi.IsChecked == true) caps.Add(new("+20%", 1.2));
        if (_capLo.IsChecked == true) caps.Add(new("−20%", 0.8));
        if (caps.Count == 0) caps.Add(new("基准", 1.0));
        var ramps = new List<LongTermScheduler.RampSpec>();
        if (_rampLinear.IsChecked == true) ramps.Add(new("线性", RampProfileKind.Linear));
        if (_rampStep.IsChecked == true) ramps.Add(new("阶梯", RampProfileKind.Stepped));
        if (_rampFast.IsChecked == true) ramps.Add(new("尽快", RampProfileKind.Aggressive));
        if (ramps.Count == 0) ramps.Add(new("基准", b.RampProfile));

        foreach (var wl in _workLines) WorkLinePicker.Refresh(_host, wl);
        var model = _host.ActiveBlockModel;
        if (!LongTermBlockSource.HasActiveBlockModel(model, out var qnote))
        { _genStatus.Text = $"排不了：{qnote} —— 中长远的量只能来自块体（BM1）。请先「加载块体模型」并激活一个带煤属性的块体"; return; }

        var variants = LongTermScheduler.Generate(b, _workLines.ToList(), model, LongTermDumpBridge.TryReadForm(_host.Db), caps, ramps, schedule: true);
        _schemes.Clear();
        foreach (var v in variants) _schemes.Add(v);
        LongTermSchemeStore.Confirmed = null;
        LongTermComparer.Score(_schemes.Where(s => s.Participate && s.Result != null).ToList());
        _resultGrid.ItemsSource = null; _resultGrid.ItemsSource = _schemes;

        int solved = variants.Count(v => v.Result != null);
        _genStatus.Text = $"{_workLines.Count}×{caps.Count}×{ramps.Count} = {variants.Count} 套，排出 {solved} 套";
        var failed = variants.Where(v => v.Result == null).Select(v => $"{v.Name}：{v.ScheduleNote}").ToList();
        _bottomStatus.Text = failed.Count == 0
            ? $"已生成 {variants.Count} 套候选（替换方案库），全部按块体沿工作线排产【{qnote}】→ 去「方案综合对比」联合比选、或「规划计算」看逐年表"
            : $"已生成 {variants.Count} 套，{failed.Count} 套没排出来 —— " + string.Join("；", failed.Take(3));
        _host.Echo("派生计划方案：" + _bottomStatus.Text, failed.Count > 0);
    }
}
