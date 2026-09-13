// 忠实移植自原 PitMine3D Modules/TaskLib/Features/AttainmentWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Reporting;   // FactSource：月度累计改吃真实实绩，不再按系数推算

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>达成度评价：计划vs实绩 量+质双维 + 原因归因 + 各设备达成图。响应条款(15)。样例数据。</summary>
public sealed class AttainmentWindow : Window
{
    public sealed class Row
    {
        public string Equip { get; set; } = "";
        public string Process { get; set; } = "";
        public string Shift { get; set; } = "";
        public string Plan { get; set; } = "";
        public string Actual { get; set; } = "";
        public string Qty { get; set; } = "";
        public string Qual { get; set; } = "";
        /// <summary>单据状态。表按计划侧铺（EV9）之后，这一列是必须的。</summary>
        public string Issue { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    private List<ProductionTask> _tasks = SampleTaskBoard.Day();

    /// <summary>
    /// 「当时排的是什么」—— 达成度的<b>分母</b>。
    /// <para>不能拿现算的计划当分母：`Config()` 每次调用都重新装配，于是计划量是个**活的数** ——
    /// 早上排 3000、中午有人把单元量改成 2500，下午再看分母就成了 2500，
    /// <b>达成率凭空涨 20%</b>，而没有任何东西报错。</para>
    /// </summary>
    private PlanBaselineResult _baseline = new();
    private double _monthTarget, _cum, _dailyDone;   // 月对账中间量（供再平衡）
    private int _remain;
    private bool _behind;

    private readonly TextBlock subTitle;
    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock vQty, vQual, vShort, vOver;
    private readonly DataGrid grid = TaskUi.Grid();
    private readonly StackPanel monthPanel = new();
    private readonly Canvas chart = new() { ClipToBounds = true };
    private readonly TextBlock gapNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private bool _loaded;

    public AttainmentWindow()
    {
        Title = "达成度评价 — 日常生产组织";
        TaskUi.Place(this, 1140, 680);

        var header = TaskUi.Header("达成度评价", "");
        subTitle = (TextBlock)((StackPanel)header.Child!).Children[1];

        // 达成度是按班考核的：一个班欠、一个班超，全天平均下来看不出任何问题。
        // 上方四张卡与下方明细都跟着这个筛；月对账那一块是整月口径，不受它影响。
        var tool = new DockPanel { LastChildFill = true };
        var lbl = TaskUi.Lbl("班次"); lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(shiftCombo, Avalonia.Controls.Dock.Left);
        var refresh = TaskUi.Btn("刷新", OnRefresh, 66); refresh.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Left);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        toolStatus.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = toolStatus });
        tool.Children.Add(lbl); tool.Children.Add(shiftCombo); tool.Children.Add(refresh); tool.Children.Add(toolStatus);

        var cards = TaskUi.CardRow(
            TaskUi.Card("量达成度(至今)", out vQty, TaskUi.Hex("#FF0F6E56")),
            TaskUi.Card("质达标率", out vQual, TaskUi.Hex("#FF0C447C")),
            TaskUi.Card("欠产任务", out vShort, TaskUi.Hex("#FF993C1D")),
            TaskUi.Card("计划过满项", out vOver));

        grid.Margin = new Thickness(0);
        grid.Columns.Add(TaskUi.TextCol("设备", nameof(Row.Equip), 72));
        grid.Columns.Add(TaskUi.TextCol("工序", nameof(Row.Process), 52));
        grid.Columns.Add(TaskUi.TextCol("班次", nameof(Row.Shift), 54));
        grid.Columns.Add(TaskUi.TextCol("计划m³", nameof(Row.Plan), 68));
        grid.Columns.Add(TaskUi.TextCol("实绩m³", nameof(Row.Actual), 68));
        grid.Columns.Add(TaskUi.TextCol("量达成%", nameof(Row.Qty), 70));
        grid.Columns.Add(TaskUi.TextCol("质量", nameof(Row.Qual), 64));
        // 单据列（EV9）：表按计划侧铺，逐行看得出这条签没签发。未下达的行不进达成率的分子分母，原因归因也算不了。
        grid.Columns.Add(TaskUi.TextCol("单据", nameof(Row.Issue), 66));
        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("原因归因"), Binding = new Binding(nameof(Row.Reason)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 200 });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,420"), Margin = new Thickness(10, 4, 10, 10) };
        var left = TaskUi.GroupBox("任务达成（量 / 质 / 原因归因）", grid, new Thickness(0, 0, 6, 0));
        var rightGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var monthBox = TaskUi.GroupBox("月计划对账（日常实绩 → 反验上层）", monthPanel, new Thickness(0, 0, 0, 4), 10);
        var chartBox = TaskUi.GroupBox("各设备 量达成度（% · 100 为达标线）", chart);
        Grid.SetRow(monthBox, 0); Grid.SetRow(chartBox, 1);
        rightGrid.Children.Add(monthBox); rightGrid.Children.Add(chartBox);
        Grid.SetColumn(left, 0); Grid.SetColumn(rightGrid, 1);
        body.Children.Add(left); body.Children.Add(rightGrid);
        chart.SizeChanged += (_, _) => DrawChart();

        // 缺口归因：把欠量按原因摊开（突发停机 / 设备布置·运力不足 / 等待 / 执行效率），并把归不上的单列成「未解释」。
        TaskUi.Theme(gapNote, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = TaskUi.Bar(gapNote, top: false, padY: 8);

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(cards, 2); Grid.SetRow(body, 3); Grid.SetRow(foot, 4);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(cards); g.Children.Add(body); g.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) Rebuild(); };
        Opened += (_, _) =>
        {
            ShiftSelector.Bind(shiftCombo, includeAll: true);   // 默认当前班
            _loaded = true;
            Rebuild();
        };
    }

    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);

    /// <summary>重读计划（本窗是 modeless 单例，编制参数改过之后不重读就是旧盘子）并全量重算。</summary>
    private void OnRefresh()
    {
        _tasks = SampleTaskBoard.Day();
        // 基准取当日快照；取不到就明说取不到（不退现算 —— 顶替出来的达成度看着完全正常）
        _baseline = PlanBaseline.For(SampleTaskBoard.DateLabel);
        // 快照里的任务没有单据状态（JSON 反序列化，Dispatch 默认未下达）——
        // 分母要按「只算下达过的」筛它，就得先照同一批单据回灌一次。
        DispatchStateLink.ApplyPersistedInstances(_baseline.Tasks, SampleTaskBoard.DateLabel);
        Rebuild();
    }

    private void Rebuild() { Build(); DrawChart(); BuildMonthReconcile(); }

    /// <summary>当前班次口径下的采装/排土任务，**已按分母口径筛过**（只算下达过的）。</summary>
    private List<ProductionTask> Scope()
        => PlanScope().Where(DispatchStateLink.CountsForEvaluation).ToList();

    /// <summary>
    /// 计划侧口径（EV9）：未下达的算，已撤回的不算 —— 与 <see cref="PlanScope"/> 同一批。
    /// <para><b>只用来铺表</b>。达成<b>率</b>的分子分母仍走 <see cref="Scope"/>（只算下达过的，
    /// 用户 2026-08-20 定，不动）—— 一条都没下达时给「—」+ 原因，而不是给一个数。
    /// 但表要照铺：225 条排好的活在界面上整片消失，读起来像"今天没排计划"。</para>
    /// </summary>
    private List<ProductionTask> PlanRows() => PlanScope();

    /// <summary>
    /// 同一班次口径下的采装/排土任务，但**不按单据筛** —— 即"计划里还有哪些活"。
    ///
    /// <para>专供 <see cref="PlanBaseline.Vanished"/> 用。那条问的是
    /// 「基准里排过、现在计划里没有了」，如果拿筛过的集合去比，
    /// 一条**只是还没下达**的任务就会被报成"排过的活消失了" ——
    /// 一句吓人且完全错误的话，而且它指的方向（去查台账改没改）也是错的。</para>
    /// </summary>
    private List<ProductionTask> PlanScope()
        => _tasks.Where(t => t.Process is ProcessType.Load or ProcessType.Dump)
                 .Where(t => ShiftFilter.Length == 0 || t.Shift == ShiftFilter)
                 .ToList();

    private void Build()
    {
        string shift = ShiftFilter;
        var mat = Scope();
        // 锚点而不是真实此刻：看已经过去的班时，"应完成量"要按那个班的班末算，
        // 拿上午十点去评夜班，应完成量是 0、达成度恒无穷大。
        double now = ShiftScope.AnchorHour(ShiftSelector.Selected(shiftCombo));
        double actual = mat.Sum(t => t.ActualVolumeM3);

        // ★ 分母走**当日基准**（保存过的计划快照），不是现算的计划。
        //   基准里的任务同样按当前班次筛，两边口径才对得上。
        //
        //   ★★ 单据口径也要对得上（2026-08-20）：快照是 JSON 反序列化出来的，
        //   Dispatch 一律是默认的"未下达" —— 不给它回灌一次，就会出现
        //   「有基准时分母含未下达的活、没基准时不含」这种随快照存不存在而变的分母。
        //   同一个窗、同一天，分母口径不该取决于有没有人点过「保存本日计划」。
        var baseScope = _baseline.Tasks
            .Where(t => t.Process is ProcessType.Load or ProcessType.Dump)
            .Where(t => shift.Length == 0 || t.Shift == shift)
            .Where(DispatchStateLink.CountsForEvaluation)
            .ToList();
        var denomSrc = _baseline.IsBaseline ? baseScope : mat;

        double planToNow = denomSrc.Sum(t =>
        {
            double dur = Math.Max(1e-6, t.EndHour - t.StartHour);
            return t.TargetVolumeM3 * Math.Clamp((Math.Min(t.EndHour, now) - t.StartHour) / dur, 0, 1);
        });

        // 「计划了却消失了」—— 基准里有、现在没有的那些。
        // 这些最容易被漏掉：现算的计划里它不存在，于是任何「遍历当前任务」的统计都碰不到它，
        // 而它明明是当天排过的活。
        // ★ 比的是 PlanScope()（未按单据筛）而不是 mat —— 见 PlanScope 的注释：
        //   拿分母口径的子集来比，未下达的任务会被误报成"消失了"。
        var vanished = _baseline.IsBaseline
            ? PlanBaseline.Vanished(baseScope, PlanScope())
            : new List<ProductionTask>();

        // 质达标率：有实测且有目标的任务里，达标占比
        var qual = mat.Where(t => t.QualityActual != null && t.QualityTarget != null).ToList();
        int qualOk = qual.Count(t => t.QualityActual!.MeetsTarget(t.QualityTarget!));
        int shortCnt = mat.Count(t => t.ActualVolumeM3 > 0 && t.AttainmentPct < 100);
        // 「计划过满」也按当前班次筛 —— 原先这一格数的是全天，与上面三格不是一个口径
        int overPlan = mat.Count(t => t.Reasons.Contains(IncompleteReason.OverPlanned));

        // 没有基准时**不给达成率**：给一个随台账漂移的百分数，比不给更糟
        vQty.Text = !_baseline.IsBaseline ? "—"
                  : planToNow > 1e-6 ? $"{actual / planToNow * 100:0}%" : "—";
        vQual.Text = qual.Count > 0 ? $"{100.0 * qualOk / qual.Count:0}%" : "—";
        vShort.Text = $"{shortCnt} 项";
        vOver.Text = $"{overPlan} 项";

        // ★ 表按**计划侧**铺（EV9）：排过的活每条都在，未下达的行只有计划量。
        //   原先只铺 mat（已下达的），一条都没签发时整张表是空的 ——
        //   而"排了多少活、其中多少还没下达"正是这时候最该看到的东西。
        //   达成率那四个指标格仍走 mat（只算下达过的），两侧口径在状态栏里写明。
        var rows = PlanRows().Select(t =>
        {
            bool counted = DispatchStateLink.CountsForEvaluation(t);
            return new Row
            {
                Equip = t.Group.MainEquipment,
                Process = t.Process.Label(),
                Shift = t.Shift,
                Plan = $"{t.TargetVolumeM3:0}",
                Actual = counted && t.ActualVolumeM3 > 0 ? $"{t.ActualVolumeM3:0}" : "—",
                Qty = counted && t.ActualVolumeM3 > 0 ? $"{t.AttainmentPct:0}%" : "—",
                Qual = counted && t.QualityActual != null && t.QualityTarget != null
                    ? (t.QualityActual.MeetsTarget(t.QualityTarget) ? "达标" : "超标") : "—",
                Issue = t.IsWithdrawn ? "已撤回" : t.IsIssued ? "已下达" : "未下达",
                // 归因不再只是"人填了哪几个原因码"，而是**各占多少方** —— 见 AttainmentAnalyzer。
                // 原先这一列写「设备故障、运力不足」，两个各占多少没人答得上来，
                // 而"该补车还是该修设备"这个决定恰恰只差这一个数。
                // 未下达的活没有"欠了多少"可归因 —— 归因分解的分母是实绩，硬算会把
                // 整条计划量记成"未解释缺口"，而它只是还没签发。
                Reason = counted ? AttainmentAnalyzer.Caption(BreakdownOf(t)) : "（未下达，不归因）",
            };
        }).ToList();
        grid.ItemsSource = rows;

        BuildGapPanel(mat);

        string scope = shift.Length == 0 ? "全天" : ShiftScope.Caption(shift);
        subTitle.Text = $"{SampleTaskBoard.DateLabel} · {scope} · 截至 {Hm(now)} · 量+质双维评价 + 原因归因";
        // ★ 基准是什么必须摆在最前：达成率是不是可信，全看分母是不是"当时那份"
        int planned = rows.Count, notIssued = planned - mat.Count;
        toolStatus.Text = _baseline.Label
                        + $"　|　{scope}：表列 {planned} 条（计划侧）"
                        + (notIssued > 0
                            ? $"，其中 **{notIssued} 条未下达**（只出计划量，不进达成率）"
                            : "")
                        + $" · 计入达成率 {mat.Count} 条 · 有实绩 {mat.Count(t => t.ActualVolumeM3 > 0)} 条"
                        + $" · 质量回灌 {qual.Count} 条"
                        + (vanished.Count > 0
                            ? $"　◆ **{vanished.Count} 条当天排过的活现在不在计划里了**"
                            + $"（{string.Join("、", vanished.Take(3).Select(t => t.WorkZone))}"
                            + (vanished.Count > 3 ? " 等" : "") + "）—— "
                            + "台账改过之后现算的计划里没有它们，任何「遍历当前任务」的统计都碰不到，"
                            + "而它们明明是当天排过的。"
                            : "")
                        // 分母口径与未下达条数：达成率高不高，先看分母是按什么算的。
                        + "　|　" + SampleTaskBoard.DispatchSourceLabel
                        + "（下方「月计划对账」是整月口径，不随班次变）";
    }

    /// <summary>本任务时段内的停机（非计划故障 / 计划检修分开取），喂给归因分解。</summary>
    private AttainmentBreakdown BreakdownOf(ProductionTask t)
    {
        double fault = 0, maint = 0;
        try
        {
            var faults = TaskPersistence.LoadFaults(SampleTaskBoard.DateLabel)
                .Where(f => string.Equals(f.EquipId, t.Group.MainEquipment, StringComparison.OrdinalIgnoreCase))
                .ToList();
            fault = faults.Where(f => !f.IsPlanned).Sum(f => f.OverlapHours(t.StartHour, t.EndHour));
            maint = faults.Where(f => f.IsPlanned).Sum(f => f.OverlapHours(t.StartHour, t.EndHour));
        }
        catch { /* 故障记录读不到 ⇒ 这两项算 0，残差会如实进「未解释」，不编 */ }

        return AttainmentAnalyzer.Of(t, fault, maint);
    }

    /// <summary>
    /// 缺口归因面板：把本班（或全天）的欠量按原因摊开，一眼看出"该补车还是该修设备"。
    /// <para>未解释的那部分**单列**并标红提示 —— 解释率低说明原因码没录全，不是分析算错。</para>
    /// </summary>
    private void BuildGapPanel(List<ProductionTask> mat)
    {
        var all = AttainmentAnalyzer.Combine(mat.Select(BreakdownOf).ToList());

        if (all.GapM3 <= 1)
        {
            gapNote.Text = all.IsOver
                ? $"本范围超额 {-all.GapM3:N0} m³实方 —— 无缺口可归因。"
                : "本范围无欠量。";
            return;
        }

        var parts = all.Items.OrderByDescending(x => x.VolumeM3)
                             .Select(x => $"{x.Cause} {x.VolumeM3:N0}（{x.VolumeM3 / all.GapM3 * 100:0}%）");

        gapNote.Text =
            $"缺口 {all.GapM3:N0} m³实方 = 工时缺口 {all.HourGapM3:N0} + 效率缺口 {all.RateGapM3:N0}（恒等式，两项之和即缺口）"
          + Environment.NewLine + "归因：" + string.Join(" · ", parts)
          + (all.UnexplainedM3 > 1
              ? $" · ⚠ 未解释 {all.UnexplainedM3:N0}（解释率 {all.ExplainedPct:0}%——多半是「未完成原因」没录全，去实绩录入补）"
              : $"（解释率 {all.ExplainedPct:0}%）");
    }

    /// <summary>
    /// 月计划对账（向上反验）：**真实实绩累计** vs 月计划目标 → 完成率/进度偏差/预测/反馈。
    ///
    /// <para>
    /// ★ 累计口径（本轮改掉的那处编造）：原实现是
    /// <c>日计划量 × 0.96 × 已过工作日</c> 推出来的——一个自洽但完全虚构的数。
    /// 合同十五条要的是「矿山**实际**任务量达成评价」，分子必须是真实录入的实绩。
    /// 现在改为按月初到作业日逐日取数（<see cref="FactSource.ForRange"/>），
    /// 没录实绩的日子就是没有，如实标出"已录 N 天"，绝不拿系数补齐。
    /// </para>
    /// </summary>
    private void BuildMonthReconcile()
    {
        monthPanel.Children.Clear();
        var mi = ShortTermLink.GetMonthInfo(SampleTaskBoard.DateLabel);

        var asOf = ProjectScope.WorkDate;
        var monthStart = new DateTime(asOf.Year, asOf.Month, 1);

        // ── 分子：真实实绩累计（月初 → 作业日）──
        // 月对账是**月**口径：显式传 PeriodKind.Month，槽③（采掘单元台账）才供数。
        var facts = FactSource.ForRange(monthStart, asOf, PeriodKind.Month);
        double cum = facts.Where(f => f.CountsAsMined || f.IsDumpReceipt).Sum(f => f.ActualVolumeM3) / 1e4;  // 万m³
        double coalCum = facts.Where(f => f.CountsAsMined && f.IsOre).Sum(f => f.ActualTonnage) / 1e4;        // 万t
        int daysWithData = FactSource.LastRangeDaysWithVolume;
        int daysElapsed = FactSource.LastRangeDays;

        // ── 分母：月计划目标（会话方案 → 月计划台账 → 没有）──
        double monthWd = mi.HasPlan ? mi.Workdays : DateTime.DaysInMonth(asOf.Year, asOf.Month);
        double monthTarget = mi.HasPlan ? mi.MaterialWanM3 : 0;

        AddMonthHead(mi.Source switch
        {
            ShortTermLink.MonthPlanSource.ConfirmedScheme
                => $"目标来源：短期计划「{mi.PlanName}」· {mi.MonthLabel}月 · 月作业日 {monthWd:0}",
            ShortTermLink.MonthPlanSource.Ledger
                => $"目标来源：月度计划台账 monthly_plan · {asOf:yyyy年M月} · 月作业日 {monthWd:0}（未确定会话方案时以台账为准）",
            _ => "目标来源：**无**——既未确定月度方案，月计划台账里也没有本月。完成率无从计算（不推算）。",
        });

        if (!mi.HasPlan)
        {
            AddKV("本月实绩累计", daysWithData > 0
                ? $"{cum:0.0} 万m³（已录 {daysWithData}/{daysElapsed} 天）"
                : $"— （本月尚无实绩录入，共 {daysElapsed} 天）");
            monthPanel.Children.Add(new TextBlock
            {
                Text = "▸ 请先在「生产计划编制 · 月度计划编制」确定本月方案，或在月度计划台账录入本月目标，达成评价即自动接真值。",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(MHex("#FF993C1D"))
            });
            _behind = false;
            return;
        }

        double complete = monthTarget > 1e-6 ? cum / monthTarget * 100 : 0;
        double timeProg = 100.0 * daysElapsed / Math.Max(1, DateTime.DaysInMonth(asOf.Year, asOf.Month));
        double dev = complete - timeProg;
        // 预测按【已录天数的日均】外推，而不是拿计划量当日均——外推的是实绩，不是愿望
        double dailyDone = daysWithData > 0 ? cum / daysWithData : 0;
        double forecast = monthTarget > 1e-6 && daysWithData > 0 ? dailyDone * monthWd / monthTarget * 100 : 0;
        int remain = Math.Max(1, (int)monthWd - daysWithData);
        double needDaily = (monthTarget - cum) / remain;

        AddKV("月计划目标", $"{monthTarget:0.0} 万m³（采出 {mi.CoalWanT:0}万t / 剥离 {mi.StripWanM3:0}万m³）");
        AddKV($"至今累计（已录 {daysWithData}/{daysElapsed} 天）",
              daysWithData > 0
                ? $"{cum:0.0} 万m³" + (coalCum > 1e-6 ? $"（其中采出 {coalCum:0.00} 万t）" : "")
                : "— （本月尚无实绩录入）");

        if (daysWithData == 0)
        {
            monthPanel.Children.Add(new TextBlock
            {
                Text = "▸ 本月还没有任何实绩录入，完成率与预测无从计算。请在「实绩录入」按班保存实绩后再看本页。",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(MHex("#FF993C1D"))
            });
            _behind = false;
            return;
        }

        AddKV("月度完成率", $"{complete:0}%   ·   时间进度 {timeProg:0}%");

        string devTxt = dev >= 0.5 ? $"超前 {dev:0.#} 个点" : dev <= -0.5 ? $"滞后 {Math.Abs(dev):0.#} 个点" : "基本持平";
        AddKV("进度偏差", devTxt, dev >= 0.5 ? MHex("#FF0F6E56") : dev <= -0.5 ? MHex("#FF993C1D") : MHex("#FF5F5E5A"));
        AddKV("预测月末完成率", $"{forecast:0}%（{(forecast >= 99 ? "可达产" : "欠产")}）", forecast >= 99 ? MHex("#FF0F6E56") : MHex("#FF993C1D"));

        string advice = dev <= -0.5
            ? $"滞后 → 剩余 {remain} 个工作日日均需提到 {needDaily:0.0} 万m³（当前日均 {dailyDone:0.0}）；否则反馈月计划削峰/补设备/调采区。"
            : forecast >= 99 ? "进度正常，按当前日均可达月目标。"
            : "进度持平但偏紧，关注剩余天数日均。";
        monthPanel.Children.Add(new TextBlock
        {
            Text = "▸ " + advice, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(dev <= -0.5 ? MHex("#FF993C1D") : MHex("#FF0F6E56"))
        });

        // 缓存供再平衡 + 滞后则给一键再平衡
        _monthTarget = monthTarget; _cum = cum; _dailyDone = dailyDone; _remain = remain;
        _behind = dev <= -0.5 || forecast < 99;
        if (_behind)
        {
            var btn = new Button
            {
                Content = "⚖ 一键再平衡（缺口摊到剩余工作日，追平月目标）",
                Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4), FontWeight = FontWeight.Bold
            };
            btn.Click += (s, _) => OnRebalance(s as Button);
            monthPanel.Children.Add(btn);
        }
    }

    /// <summary>滚动校正：把月度缺口摊到剩余工作日 → 新日目标；校核设备日产能是否追得平。</summary>
    private void OnRebalance(Button? sender)
    {
        double gap = Math.Max(0, _monthTarget - _cum);
        double newDaily = _remain > 0 ? gap / _remain : 0;
        double pitCap = PitDailyCapacityWanM3();
        bool feasible = newDaily <= pitCap + 1e-6;

        var sp = new StackPanel();
        if (feasible)
        {
            double pct = _dailyDone > 1e-6 ? (newDaily / _dailyDone - 1) * 100 : 0;
            sp.Children.Add(new TextBlock { Text = "✓ 再平衡可行", FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(MHex("#FF0F6E56")) });
            sp.Children.Add(Body($"剩余 {_remain} 工作日：日均 {_dailyDone:0.00} → {newDaily:0.00} 万m³（+{pct:0}%），≤ 设备日产能 {pitCap:0.00} 万m³ → 可追平月目标。", 3));

            // 真重排：按新日目标(等比放大各面)重跑引擎 → 代表性新日计划 + 校核
            var cfg = SampleTaskBoard.Config();
            var mFaces = cfg.Faces.Where(f => f.Process is ProcessType.Load or ProcessType.Dump).ToList();
            double curTotal = mFaces.Sum(f => f.DayTargetM3);
            double scale = curTotal > 1e-6 ? newDaily * 1e4 / curTotal : 1;
            foreach (var f in mFaces) f.DayTargetM3 = Math.Round(f.DayTargetM3 * scale);
            var res = TaskExploder.Explode(cfg);
            double newTotal = res.Tasks.Where(t => t.Process is ProcessType.Load or ProcessType.Dump).Sum(t => t.TargetVolumeM3) / 1e4;

            var t1 = Body($"▸ 引擎已按新日目标重排：新日计划总 {newTotal:0.00} 万m³（守恒）。", 6); t1.FontSize = 12; t1.FontWeight = FontWeight.Medium;
            sp.Children.Add(t1);
            foreach (var f in mFaces)
                sp.Children.Add(new TextBlock { Text = $"  · {f.Zone} 日目标 → {f.DayTargetM3:0} m³", FontSize = 11.5, Foreground = Hint });
            sp.Children.Add(new TextBlock
            {
                Text = res.Violations.Count == 0 ? "  · 校核：无异常，可执行。" : $"  · 校核：{res.Violations.Count} 项（{string.Join("；", res.Violations.Take(2).Select(v => v.Code))}）",
                FontSize = 11.5, Foreground = Hint, TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            double maxRemain = pitCap * _remain;
            double stillShort = gap - maxRemain;
            double daysNeeded = pitCap > 1e-6 ? stillShort / pitCap : 0;
            sp.Children.Add(new TextBlock { Text = "⚠ 提日产仍追不平 → 须反馈月计划", FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(MHex("#FF993C1D")) });
            sp.Children.Add(Body($"剩余 {_remain} 天即便满产（{pitCap:0.00} 万m³/日）仍差 {stillShort:0.0} 万m³。", 3));
            var t2 = Body($"→ 反馈短期月计划：削月目标 {stillShort:0.0} 万m³ / 补设备 / 顺延 ~{daysNeeded:0.#} 天。", 3);
            t2.Foreground = new SolidColorBrush(MHex("#FF993C1D"));
            sp.Children.Add(t2);
        }
        monthPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(feasible ? MHex("#FFE1F5EE") : MHex("#FFFCEBEB")),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 0), Child = sp
        });
        if (sender != null) sender.IsEnabled = false;
    }

    private static TextBlock Body(string text, double top)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        TaskUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Body");
        return t;
    }

    /// <summary>采场日产能上限（万m³）= Σ 各面编组班产 × 日有效工时(~18h)。</summary>
    private static double PitDailyCapacityWanM3()
    {
        const double effHours = 18;
        return SampleTaskBoard.Config().Faces
            .Where(f => f.Process is ProcessType.Load or ProcessType.Dump)
            .Sum(f => f.Group.GroupCapacityM3PerH * effHours) / 1e4;
    }

    private void AddMonthHead(string t)
        => monthPanel.Children.Add(new TextBlock { Text = t, FontSize = 12, Foreground = Hint, Margin = new Thickness(0, 0, 0, 7), TextWrapping = TextWrapping.Wrap });

    private void AddKV(string k, string v, Color? c = null)
    {
        // ★ 用 Grid 不用横向 StackPanel：横向 StackPanel 给子元素**无限宽**，
        //   于是 TextWrapping 永远不生效，值超出面板就被 GroupBox 直接裁掉
        //   （实测「月计划目标 469.9 万m³（采出 120万t / 剥离 381万m³）」尾巴没了）。
        //   两列 = 定宽标签 + 星号值列，值才会换行。
        var sp = new Grid { Margin = new Thickness(0, 0, 0, 3), ColumnDefinitions = new ColumnDefinitions("158,*") };
        sp.Children.Add(new TextBlock { Text = k + "：", FontSize = 12.5, Foreground = Hint });

        // ★ 没给颜色时**一个字都不许写 Foreground**（原版注释：显式置 null 会盖掉主题继承值，那一行就看不见）。
        var val = new TextBlock { Text = v, FontSize = 12.5, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap };
        if (c.HasValue) val.Foreground = new SolidColorBrush(c.Value);
        else TaskUi.Theme(val, TextBlock.ForegroundProperty, "Theme.Text.Body");
        Grid.SetColumn(val, 1);
        sp.Children.Add(val);
        monthPanel.Children.Add(sp);
    }

    private static Color MHex(string s) => Color.Parse(s);
    // DayOfMonth 已随「按已过工作日推算累计」一并删除——现在的口径是逐日真实取数。

    private void DrawChart()
    {
        var c = chart;
        c.Children.Clear();
        if (!_loaded) return;
        double w = c.Bounds.Width > 40 ? c.Bounds.Width : 400, h = c.Bounds.Height > 40 ? c.Bounds.Height : 200;
        double now = SampleTaskBoard.NowHour;
        var groups = _tasks.Where(t => t.Process is ProcessType.Load or ProcessType.Dump)
            .GroupBy(t => t.Group.MainEquipment)
            .Select(g =>
            {
                double act = g.Sum(t => t.ActualVolumeM3);
                double ptn = g.Sum(t => { double d = Math.Max(1e-6, t.EndHour - t.StartHour); return t.TargetVolumeM3 * Math.Clamp((Math.Min(t.EndHour, now) - t.StartHour) / d, 0, 1); });
                return (Equip: g.Key, Pct: ptn > 1e-6 ? act / ptn * 100 : 0);
            }).Where(x => x.Pct > 0).ToList();
        if (groups.Count == 0) return;

        double x0 = 8, y0 = h - 16, x1 = w - 6, y1 = 12;
        double max = Math.Max(110, groups.Max(g => g.Pct) * 1.1);
        TaskUi.Line(c, x0, y0, x1, y0, Axis);
        double y100 = y0 - 100 / max * (y0 - y1);
        AddLine(c, x0, y100, x1, y100, new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)), 1, new AvaloniaList<double> { 3, 2 });
        TaskUi.CanvasText(c, x1 - 30, y100 - 12, "100%", 9, new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)));
        double slot = (x1 - x0) / groups.Count, bw = Math.Min(34, slot * 0.5);
        foreach (var (g, i) in groups.Select((g, i) => (g, i)))
        {
            double cx = x0 + (i + 0.5) * slot;
            double bh = g.Pct / max * (y0 - y1);
            var col = g.Pct >= 100 ? Color.FromRgb(0x1D, 0x9E, 0x75) : Color.FromRgb(0xBA, 0x75, 0x17);
            if (bw > 0 && bh > 0) TaskUi.Rect(c, cx - bw / 2, y0 - bh, bw, bh, new SolidColorBrush(col));
            TaskUi.CanvasText(c, cx - bw / 2, y0 - bh - 13, $"{g.Pct:0}%", 9, Hint);
            TaskUi.CanvasText(c, cx - slot / 2 + 4, y0 + 1, g.Equip, 9, Hint);
        }
    }

    private static string Hm(double hh) { int h = (int)hh; int m = (int)Math.Round((hh - h) * 60); return $"{h:00}:{m:00}"; }
    private static IBrush Axis => new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));
    private static IBrush Hint => new SolidColorBrush(Color.FromArgb(0xAA, 0x88, 0x88, 0x88));
    private static void AddLine(Canvas c, double x1, double y1, double x2, double y2, IBrush b, double th = 1, AvaloniaList<double>? dash = null)
    {
        var l = new Avalonia.Controls.Shapes.Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = th };
        if (dash != null) l.StrokeDashArray = dash;
        c.Children.Add(l);
    }
}
