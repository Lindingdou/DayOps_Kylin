// 忠实移植自原 PitMine3D Modules/TaskLib/Features/ProcessProgressWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 消歧 System.Threading.Tasks.TaskStatus

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 工序进度跟踪：各作业面 工序链(穿孔→爆破→采装→运输→排土) 进度，按 区×工序 聚合引擎任务。
///
/// <para>
/// <b>按班组织</b>：默认只看当前班——工序进度是交接班时要交代的东西，一上来把三个班的量
/// 加在一起看不出"我这个班干到哪了"。「全部」保留给交接班/日终复盘。
/// </para>
/// </summary>
public sealed class ProcessProgressWindow : Window
{
    /// <summary>工序链的排列次序。★五道工序一个都不能少：漏掉的那道 <c>Array.IndexOf</c> 返 −1，
    /// 会被排到所有工序**最前面**（原先漏了爆破与运输，而窗口标题写的正是这五道）。</summary>
    private static readonly ProcessType[] ChainOrder =
    {
        ProcessType.Drill, ProcessType.Blast, ProcessType.Load, ProcessType.Haul, ProcessType.Dump,
    };

    public sealed class Row
    {
        public string Zone { get; set; } = "";
        public string Process { get; set; } = "";
        public string Plan { get; set; } = "";
        public string Done { get; set; } = "";
        public string Progress { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid grid = TaskUi.Grid();
    private bool _loaded;

    public ProcessProgressWindow()
    {
        Title = "工序进度跟踪 — 日常生产组织";
        TaskUi.Place(this, 980, 560);

        var tool = new DockPanel { LastChildFill = true };
        var lbl = TaskUi.Lbl("班次"); lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(shiftCombo, Avalonia.Controls.Dock.Left);
        var refresh = TaskUi.Btn("刷新", Build, 66); refresh.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Left);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        tool.Children.Add(lbl); tool.Children.Add(shiftCombo); tool.Children.Add(refresh); tool.Children.Add(toolStatus);

        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("作业面/区"), Binding = new Avalonia.Data.Binding(nameof(Row.Zone)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(TaskUi.TextCol("工序", nameof(Row.Process), 70));
        // 单位随工序变：采装/排土是 m³ 实方，穿孔是延米 m（值里带单位，别在表头写死）
        grid.Columns.Add(TaskUi.TextCol("计划量", nameof(Row.Plan), 100));
        grid.Columns.Add(TaskUi.TextCol("已完成", nameof(Row.Done), 100));
        grid.Columns.Add(TaskUi.TextCol("进度", nameof(Row.Progress), 80));
        grid.Columns.Add(TaskUi.TextCol("状态", nameof(Row.Status), 90));

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        var header = TaskUi.Header("工序进度跟踪", "各作业面 穿孔→爆破→采装→运输→排土 工序链进度");
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(grid, 2);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(grid);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) Build(); };
        Opened += (_, _) =>
        {
            ShiftSelector.Bind(shiftCombo, includeAll: true);   // 默认当前班
            _loaded = true;
            Build();
        };
    }

    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);

    private void Build()
    {
        string shift = ShiftFilter;

        var rows = SampleTaskBoard.Day()
            // 已撤回的不是"要干的活"：不上看板、不进工序进度、不该有人给它填实绩。
            // 未下达的**仍然留着** —— 调度要看得见计划排了什么，那与评价分母是两个口径。
            .Where(DispatchStateLink.IsLive)
            .Where(t => t.Process != ProcessType.Idle)
            .Where(t => shift.Length == 0 || t.Shift == shift)
            .GroupBy(t => (t.WorkZone, t.Process))
            .OrderBy(g => g.Key.WorkZone).ThenBy(g => Array.IndexOf(ChainOrder, g.Key.Process))
            .Select(g =>
            {
                // 穿孔的量是**延米**（来自 drill_plan 台账，经装箱下沉到任务）。
                // 台账没录孔数/延米时才退回"完成/未完成"二值 —— 那是最后的兜底，不是常态：
                // 在延米下沉之前，这里对穿孔恒判 0%（Status==Done 没有任何入口能置位）。
                bool drill = g.Key.Process == ProcessType.Drill;
                bool hasMeters = drill && g.Any(t => t.Drill?.PlanMeters is > 0);

                double plan, done, pct;
                string unit;
                if (drill && hasMeters)
                {
                    plan = g.Sum(t => t.Drill?.PlanMeters ?? 0);
                    done = g.Sum(t => t.Drill?.ActualMeters ?? 0);
                    pct = plan > 1e-6 ? done / plan * 100 : 0;
                    unit = "m";
                }
                else if (drill || g.Key.Process == ProcessType.Blast)
                {
                    plan = done = 0;
                    pct = g.All(t => t.Status == TaskStatus.Done) ? 100 : 0;
                    unit = "";
                }
                else
                {
                    plan = g.Sum(t => t.TargetVolumeM3);
                    done = g.Sum(t => t.ActualVolumeM3);
                    pct = plan > 1e-6 ? done / plan * 100 : 0;
                    unit = "m³";
                }

                return new Row
                {
                    Zone = g.Key.WorkZone,
                    Process = g.Key.Process.Label(),
                    Plan = unit.Length == 0 ? "—" : $"{plan:0.#} {unit}",
                    Done = unit.Length == 0 ? "—" : (done > 1e-6 ? $"{done:0.#} {unit}" : "—"),
                    Progress = $"{pct:0}%",
                    Status = pct >= 100 ? "完成" : pct > 0 ? "进行中" : "待开始",
                };
            }).ToList();

        grid.ItemsSource = rows;

        // 抬头写清"看的是哪个班的哪段时间"，别让人以为是全天
        double anchor = ShiftScope.AnchorHour(ShiftSelector.Selected(shiftCombo));
        toolStatus.Text = $"{(shift.Length == 0 ? "全天" : ShiftScope.Caption(shift))}："
                        + $"{rows.Select(r => r.Zone).Distinct().Count()} 个作业区 · {rows.Count} 条工序进度"
                        + $"（截至 {DispatchClock.Hm(anchor)}）"
                        + (rows.Count == 0 ? " —— 本班没有排任何工序任务" : "");
    }
}
