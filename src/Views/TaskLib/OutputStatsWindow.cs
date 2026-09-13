// 忠实移植自原 PitMine3D Modules/TaskLib/Features/OutputStatsWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>产量统计：按设备/工序 多维聚合引擎任务 + 剥采比 + 工序占比图。样例。</summary>
public sealed class OutputStatsWindow : Window
{
    public sealed class Row
    {
        public string Equip { get; set; } = "";
        public string Process { get; set; } = "";
        public string Zone { get; set; } = "";
        public string Plan { get; set; } = "";
        public string Actual { get; set; } = "";
        /// <summary>单据状态（已下达 / 未下达）。计划侧不再被下达闸挡掉之后，这一列是必须的。</summary>
        public string Issue { get; set; } = "";
    }

    private readonly TextBlock subTitle;
    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock vLoad, vDump, vActual, vRatio;
    private readonly TextBlock lRatio = new();
    private readonly DataGrid grid = TaskUi.Grid();
    private readonly Canvas chart = new() { ClipToBounds = true };
    private bool _loaded;

    public OutputStatsWindow()
    {
        Title = "产量统计 — 日常生产组织";
        TaskUi.Place(this, 1100, 620);

        // 抬头带的副题由 Build 填（日期 · 班次 · 口径）
        var header = TaskUi.Header("产量统计", "");
        subTitle = (TextBlock)((StackPanel)header.Child!).Children[1];

        // LastChildFill=True：让状态栏吃掉剩余宽度，超长时按字符省略而不是被裁掉。全文进 ToolTip。
        var tool = new DockPanel { LastChildFill = true };
        var lbl = TaskUi.Lbl("班次"); lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(shiftCombo, Avalonia.Controls.Dock.Left);
        var refresh = TaskUi.Btn("刷新", Build, 66); refresh.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Left);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        toolStatus.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = toolStatus });
        tool.Children.Add(lbl); tool.Children.Add(shiftCombo); tool.Children.Add(refresh); tool.Children.Add(toolStatus);

        var cards = TaskUi.CardRow(
            TaskUi.Card("采装合计", out vLoad),
            TaskUi.Card("排土合计", out vDump),
            TaskUi.Card("实绩合计", out vActual, TaskUi.Hex("#FF0F6E56")),
            // 剥采比 = 剥离实方 ÷ 采出实方，按物料构成拆（不是排土量÷采装量）
            TaskUi.Card("生产剥采比", out vRatio, labelBlock: lRatio));

        grid.Margin = new Thickness(0);
        grid.Columns.Add(TaskUi.TextCol("设备", nameof(Row.Equip), 90));
        grid.Columns.Add(TaskUi.TextCol("工序", nameof(Row.Process), 64));
        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("作业面"), Binding = new Binding(nameof(Row.Zone)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 160 });
        grid.Columns.Add(TaskUi.TextCol("计划m³", nameof(Row.Plan), 84));
        grid.Columns.Add(TaskUi.TextCol("实绩m³", nameof(Row.Actual), 84));
        // 单据列（EV9）：计划侧不再被下达闸挡掉，那就必须逐行看得出这条下没下达 —
        // 否则一屏"计划量都在、实绩全是—"，读的人只会以为是没干活。
        grid.Columns.Add(TaskUi.TextCol("单据", nameof(Row.Issue), 76));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,400"), Margin = new Thickness(10, 4, 10, 10) };
        var left = TaskUi.GroupBox("按设备 产量（计划 / 实绩 m³）", grid, new Thickness(0, 0, 6, 0));
        var right = TaskUi.GroupBox("按工序 产量占比（计划 m³）", chart);
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 1);
        body.Children.Add(left); body.Children.Add(right);
        chart.SizeChanged += (_, _) => DrawChart();

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(cards, 2); Grid.SetRow(body, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(cards); g.Children.Add(body);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) Build(); };
        Opened += (_, _) =>
        {
            ShiftSelector.Bind(shiftCombo, includeAll: true);
            _loaded = true;
            Build();
        };
    }

    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);

    /// <summary>
    /// 清空图表区。<b>空态要把上一轮的图也擦掉</b> —— 留着旧图配"—"的指标卡，
    /// 人会以为图是这一轮的。
    /// </summary>
    private void ClearCharts()
    {
        chart?.Children.Clear();
        grid.ItemsSource = null;
        vRatio.Text = "—";
    }

    /// <summary>
    /// 本窗口径下的采装/排土任务 —— <b>计划侧</b>（EV9）：未下达的算，已撤回的不算。
    ///
    /// <para>★ 2026-08-20 前这里套的是分母口径（只算下达过的），于是一条都没签发时
    /// 整窗归零：225 条任务、16 万方计划量一起消失，界面上写着「本日没有任务」。
    /// 而产量统计的左半边（计划量 / 计划剥采比 / 按设备清单）**与下达与否无关** ——
    /// 它们是排出来的，就在盘子上。实绩侧仍按 <see cref="DispatchStateLink.CountsForEvaluation"/>。</para>
    /// </summary>
    private List<ProductionTask> Scope()
        => SampleTaskBoard.Day()
            .Where(DispatchStateLink.CountsForPlan)
            .Where(t => t.Process is ProcessType.Load or ProcessType.Dump)
            .Where(t => ShiftFilter.Length == 0 || t.Shift == ShiftFilter)
            .ToList();

    private void Build()
    {
        string shift = ShiftFilter;
        string scope = shift.Length == 0 ? "全天" : ShiftScope.Caption(shift);

        var tasks = Scope();

        // ★ 空态（2026-08-18 补）：样例不再兜底之后，没有任务是**常态**而不是异常。
        //   此前这个窗永远有数（样例兜着），所以从没写过空态 ——
        //   现在不写就会显示"0.00 万m³ / 剥采比 0.00 / 空图表"，一个数都不错，
        //   而它说的是"这个矿今天没产量"，不是"还没排计划"。两件事在界面上必须分得开。
        if (tasks.Count == 0)
        {
            vLoad.Text = vDump.Text = vActual.Text = "—";
            // 计划侧都空了才是真的没排 —— 这里已经不会再把"没下达"说成"没排计划"了（EV9）。
            toolStatus.Text = $"{scope}：**本日没有采装/排土任务** —— 不是产量为 0，是计划还没排出来。"
                            + ProductionPlanContext.SourceLabel;
            ClearCharts();
            return;
        }

        var loads = tasks.Where(t => t.Process == ProcessType.Load).ToList();
        double load = loads.Sum(t => t.TargetVolumeM3);
        double dump = tasks.Where(t => t.Process == ProcessType.Dump).Sum(t => t.TargetVolumeM3);

        // ★ 实绩侧走分母口径：**只算下达过的**（用户 2026-08-20 定）。
        //   计划侧（上面两格）不受它限制 —— 两侧口径不同，故必须在文案里写明白，
        //   否则"计划 16 万方 / 实绩 0"读起来像是活没干，而实际是一条都没签发。
        var issued = tasks.Where(DispatchStateLink.CountsForEvaluation).ToList();
        int notIssued = tasks.Count - issued.Count;
        double actual = issued.Sum(t => t.ActualVolumeM3);

        vLoad.Text = $"{load / 1e4:0.00} 万m³";
        vDump.Text = $"{dump / 1e4:0.00} 万m³";
        vActual.Text = issued.Count == 0 ? "—" : $"{actual / 1e4:0.00} 万m³";

        // ── 剥采比 ────────────────────────────────────────────────────────────
        //  ★ 原先是 排土量 ÷ 采装量，注释自认"粗略"。它错在两处：
        //    ① 采装量里既有煤也有岩（混采面就是同一台铲一起挖的），分母本该只算**采出的煤**；
        //    ② 排土量是排土工序的量，不等于剥离量（还有直接外运/内排不过排土面的）。
        //  正解走物料构成：剥离 = Σ WasteVolumeM3、采出 = Σ OreVolumeM3，两个都在采装侧算。
        //  ★ 这是**计划**剥采比：Ore/WasteVolumeM3 都由 TargetVolumeM3 × 物料份额算出。
        //    原先这一格标着「生产剥采比」，读起来像是实绩口径 —— 两个数在欠产的班里差得很远。
        double ore = loads.Sum(t => t.OreVolumeM3);
        double waste = loads.Sum(t => t.WasteVolumeM3);
        vRatio.Text = ore > 1e-6 ? $"{waste / ore:0.00}" : "—";
        lRatio.Text = ore > 1e-6 ? "计划剥采比（剥离÷采出实方）" : "计划剥采比（本班无采出煤量）";

        subTitle.Text = $"{SampleTaskBoard.DateLabel} · {scope} · 按设备 / 工序 多维统计";
        toolStatus.Text = $"{scope}：{tasks.Count} 条任务（采装 {loads.Count} · 排土 {tasks.Count - loads.Count}）"
                        + $" · 计划剥离 {waste / 1e4:0.00} 万m³ / 计划采出 {ore / 1e4:0.00} 万m³"
                        // 两侧口径不同，必须在同一行里说清：上面的量是计划侧（含未下达），
                        // 「实绩合计」那一格是实绩侧（只算下达过的）。不写就成了两个口径混列。
                        + (notIssued > 0
                            ? $"　|　◆ **计划侧含 {notIssued} 条未下达**（计划量照算）；"
                              + $"「实绩合计」只算已下达的 {issued.Count} 条"
                              + (issued.Count == 0 ? " —— 一条都没签发，故为「—」" : "")
                            : "")
                        + "　|　" + SampleTaskBoard.DispatchSourceLabel;

        grid.ItemsSource = tasks
            .OrderBy(t => t.Group.MainEquipment).ThenBy(t => t.StartHour)
            .Select(t => new Row
            {
                Equip = t.Group.MainEquipment, Process = t.Process.Label(), Zone = t.WorkZone,
                Plan = $"{t.TargetVolumeM3:0}",
                // 实绩一律按实绩侧口径：未下达的任务即便回灌过量也不在这里显示，
                // 与「实绩合计」那一格同源，免得表与卡片对不上。
                Actual = DispatchStateLink.CountsForEvaluation(t) && t.ActualVolumeM3 > 0
                    ? $"{t.ActualVolumeM3:0}" : "—",
                Issue = t.IsWithdrawn ? "已撤回" : t.IsIssued ? "已下达" : "未下达",
            }).ToList();

        DrawChart();
    }

    private void DrawChart()
    {
        var c = chart; c.Children.Clear();
        if (!_loaded) return;
        double w = c.Bounds.Width > 40 ? c.Bounds.Width : 360, h = c.Bounds.Height > 40 ? c.Bounds.Height : 220;
        var tasks = Scope();          // 图与表同一个班次口径，别一个按班、一个按全天
        var data = new (string Name, double Val, string Col)[]
        {
            ("采装", tasks.Where(t => t.Process == ProcessType.Load).Sum(t => t.TargetVolumeM3) / 1e4, "#FF378ADD"),
            ("排土", tasks.Where(t => t.Process == ProcessType.Dump).Sum(t => t.TargetVolumeM3) / 1e4, "#FFEF9F27"),
        };
        double x0 = 8, y0 = h - 18, x1 = w - 8, y1 = 14;
        double max = Math.Max(0.1, data.Max(d => d.Val)) * 1.2;
        TaskUi.Line(c, x0, y0, x1, y0, new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88)));
        double slot = (x1 - x0) / data.Length, bw = Math.Min(70, slot * 0.5);
        for (int i = 0; i < data.Length; i++)
        {
            double cx = x0 + (i + 0.5) * slot, bh = data[i].Val / max * (y0 - y1);
            TaskUi.Rect(c, cx - bw / 2, y0 - bh, bw, bh, TaskUi.Hex(data[i].Col));
            TaskUi.CanvasText(c, cx - bw / 2, y0 - bh - 15, $"{data[i].Val:0.00}万", 10);
            TaskUi.CanvasText(c, cx - 12, y0 + 2, data[i].Name, 11);
        }
    }
}
