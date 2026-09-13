// 忠实移植自原 PitMine3D Modules/TaskLib/Features/DispatchBoardWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using SinkRegistryLoader = PitMine3D.Kylin.TaskLib.Engine.SinkRegistryLoader;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 调度态势看板：调度员的主界面 —— 要一眼看到异常。
///
/// <para>四块内容，按"先看异常、再看细节"排布：</para>
/// <list type="number">
/// <item><b>KPI 条</b>：Error 级校核数 / 未复机故障台数 / 本班车次执行进度 / 库容告警数。</item>
/// <item><b>去向预警卡</b>：各去向今日入方与剩余库容——哪个排土场快满了必须先看到。
///   排弃类按【占容方 V容 = V实×Kr】扣库容；破碎站/煤仓是通过型去向不占库容，改比【吨量 vs 通过能力】。</item>
/// <item><b>主设备卡</b>：此刻每台主设备在干什么、进度多少（原有内容保留并补上去向与车次）。</item>
/// <item><b>右侧告警栏</b>：未复机故障设备清单 + Error/Warn 校核明细。</item>
/// </list>
/// <para>只读实算：数据来自任务台账、去向登记簿与落盘的故障记录/派车单，本窗口不写任何东西。</para>
/// </summary>
public sealed class DispatchBoardWindow : Window
{
    private readonly string _date = SampleTaskBoard.DateLabel;

    private readonly TextBlock subTitle;
    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel kpi = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 0) };
    private readonly TextBlock sinkHead = new() { Text = "去向 · 本班入方与剩余库容", FontWeight = FontWeight.Bold, Margin = new Thickness(2, 0, 0, 8) };
    private readonly WrapPanel sinkCards = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock equipHead = new() { Text = "主设备 · 此刻在干什么", FontWeight = FontWeight.Bold, Margin = new Thickness(2, 4, 0, 8) };
    private readonly WrapPanel cards = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel alerts = new();
    private bool _loaded;

    public DispatchBoardWindow()
    {
        Title = "调度态势看板 — 日常生产组织";
        TaskUi.Place(this, 1320, 760);

        // 抬头带：看板用更深的蓝黑（#1E293B → #0F172A），与其它窗的灰蓝区分
        var headSp = new StackPanel { Orientation = Orientation.Horizontal };
        headSp.Children.Add(new TextBlock { Text = "调度态势看板", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        subTitle = new TextBlock { Foreground = new SolidColorBrush(C(0xCB, 0xD5, 0xE1)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 2, 0, 0) };
        headSp.Children.Add(subTitle);
        var header = new Border
        {
            Padding = new Thickness(16, 10), Child = headSp,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(C(0x1E, 0x29, 0x3B), 0), new GradientStop(C(0x0F, 0x17, 0x2A), 1) },
            },
        };

        var tool = new StackPanel { Orientation = Orientation.Horizontal };
        var lbl = TaskUi.Lbl("班次"); lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 6, 0);
        // 调度是按班盯的。取值域按当日班制、默认落当前班；「全部」留给日终复盘。
        // 选了别的班时，一切"进度/此刻"改按该班的锚点算（见 ShiftScope.AnchorHour）。
        var refresh = TaskUi.Btn("刷新", OnRefresh, 76); refresh.Margin = new Thickness(0, 0, 14, 0);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        tool.Children.Add(lbl); tool.Children.Add(shiftCombo); tool.Children.Add(refresh); tool.Children.Add(toolStatus);

        TaskUi.Theme(sinkHead, TextBlock.ForegroundProperty, "Theme.Text.Body");
        TaskUi.Theme(equipHead, TextBlock.ForegroundProperty, "Theme.Text.Body");
        var leftSp = new StackPanel();
        leftSp.Children.Add(sinkHead); leftSp.Children.Add(sinkCards); leftSp.Children.Add(equipHead); leftSp.Children.Add(cards);
        var leftScroll = new ScrollViewer { Content = leftSp, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(12, 0, 6, 12) };

        var alertDock = new DockPanel();
        var alertTitle = new TextBlock { Text = "异常与告警", FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(alertTitle, Avalonia.Controls.Dock.Top);
        alertDock.Children.Add(alertTitle);
        alertDock.Children.Add(new ScrollViewer { Content = alerts, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var alertBox = new Border
        {
            Margin = new Thickness(6, 0, 12, 12), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10),
            Background = new SolidColorBrush(C(0x16, 0x20, 0x2F)), BorderThickness = new Thickness(1), Child = alertDock,
        };
        TaskUi.Theme(alertBox, Border.BorderBrushProperty, "Theme.Surface.Border");

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(leftScroll, 0); Grid.SetColumn(alertBox, 1);
        body.Children.Add(leftScroll); body.Children.Add(alertBox);

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(kpi, 2); Grid.SetRow(body, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(kpi); g.Children.Add(body);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) Build(); };
        Opened += (_, _) =>
        {
            ShiftSelector.Bind(shiftCombo, includeAll: true);   // 默认当前班；「全部」留给日终复盘
            _loaded = true;
            Build();
        };
    }

    private void OnRefresh()
    {
        ProductionPlanContext.Invalidate();   // ★ 不重装配则「此刻」停在上次装配那一刻，刷新等于没刷
        SinkRegistryLoader.Invalidate();      // 台账可能刚被实绩回灌改过
        DispatchEngine.Invalidate();
        Build();
    }

    private double NowHour => SampleTaskBoard.NowHour;

    /// <summary>选中的班次（空 = 全天）。</summary>
    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);

    /// <summary>班次时窗的终点，跨零点摊平到 [Start, Start+24)。</summary>
    private static double EndOf(ShiftWindow w) => w.End <= w.Start ? w.End + 24 : w.End;

    private void Build()
    {
        string shift = ShiftFilter;
        var win = ShiftScope.Window(shift);
        // 锚点：当前班取真实此刻，过去的班取班末，未到的班取班初。
        // 直接用真实此刻的话，切到夜班时所有进度都按上午十点算，整班看着像没开工。
        double anchor = ShiftScope.AnchorHour(ShiftSelector.Selected(shiftCombo));
        string scope = shift.Length == 0 ? "全天" : ShiftScope.Caption(shift);

        // 已撤回的不上看板（口径见 DispatchStateLink.IsLive）—— 从前撤回的任务
        // 照样显示"这台设备在干它"，因为撤回状态根本没落到盘子上。
        var tasks = SampleTaskBoard.Day()
            .Where(DispatchStateLink.IsLive)
            .Where(t => shift.Length == 0 || t.Shift == shift).ToList();
        subTitle.Text = $"{_date} · {scope} · 锚点 {DispatchClock.Hm(anchor)}"
                      + (shift.Length > 0 && !string.Equals(shift, ShiftScope.ShiftOf(NowHour), StringComparison.Ordinal)
                          ? $"（此刻 {DispatchClock.Hm(NowHour)} 属{ShiftScope.ShiftOf(NowHour)}）" : "")
                      + $" · {SampleTaskBoard.SourceLabel}";

        // 故障按**与本班时窗的重叠**取，一次跨班停机分别落到各班头上
        var allFaults = SafeFaults();
        var faults = win == null
            ? allFaults
            : allFaults.Where(f => f.OverlapHours(win.Start, EndOf(win)) > 1e-6).ToList();

        // 校核：本班任务相关的 + 不带 TaskId 的全局项（口径同 DispatchEngine.ValidateForIssue）
        var ids = new HashSet<string>(tasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var violations = SafeViolations()
            .Where(v => string.IsNullOrWhiteSpace(v.TaskId) || ids.Contains(v.TaskId))
            .ToList();

        var (allOrders, orderSrc) = SafeOrders();
        var orders = shift.Length == 0 ? allOrders : allOrders.Where(o => o.Shift == shift).ToList();

        sinkHead.Text = $"去向 · {scope}入方与剩余库容";
        equipHead.Text = shift.Length == 0 ? "主设备 · 此刻在干什么" : $"主设备 · {shift}在干什么";

        BuildSinkCards(tasks, scope, out int sinkAlerts);
        BuildEquipCards(tasks, faults, anchor, shift);
        BuildAlerts(faults, violations, shift);
        BuildKpi(tasks, faults, violations, orders, orderSrc, sinkAlerts, anchor);

        int openNow = allFaults.Count(f => f.IsOpen);
        toolStatus.Text = $"{scope}：{cards.Children.Count} 台主设备 · 绿=运行 红=故障 灰=空闲 · "
                        + $"本班故障 {faults.Count(f => f.IsOpen || win != null)} 台次 · 当前未复机 {openNow} 台 · "
                        + $"校核 Error {violations.Count(v => v.Severity == ViolationSeverity.Error)} / Warn {violations.Count(v => v.Severity == ViolationSeverity.Warn)}";
    }

    // ── ① KPI 条 ─────────────────────────────────────────────────────────────

    private void BuildKpi(List<ProductionTask> tasks, List<FaultEvent> faults, List<PlanViolation> violations,
                          List<DispatchOrder> orders, string orderSrc, int sinkAlerts, double anchor)
    {
        kpi.Children.Clear();

        int err = violations.Count(v => v.Severity == ViolationSeverity.Error);
        int open = faults.Count(f => f.IsOpen);

        // 车次执行进度：已卸车次 / 按锚点应完成车次（派车单里 PlannedDumpHour ≤ 锚点的那些）
        int due = orders.Count(o => o.PlannedDumpHour <= anchor && o.Status != DispatchOrderStatus.Cancelled);
        int done = orders.Count(o => o.Status == DispatchOrderStatus.Dumped);
        string tripValue = orders.Count == 0 ? "—" : $"{done}/{due}";
        string tripUnit = orders.Count == 0 ? orderSrc : $"趟（本班共 {orders.Count}）";

        double planM3 = tasks.Where(t => t.Process is ProcessType.Load).Sum(t => t.TargetVolumeM3);
        double actM3 = tasks.Where(t => t.Process is ProcessType.Load).Sum(t => t.ActualVolumeM3);
        double att = planM3 > 1e-6 ? actM3 / planM3 * 100 : 0;

        kpi.Children.Add(Tile("Error 级校核", $"{err}", "条", err > 0 ? Red : Green));
        kpi.Children.Add(Tile("未复机故障", $"{open}", "台", open > 0 ? Red : Green));
        kpi.Children.Add(Tile("库容/卸点告警", $"{sinkAlerts}", "处", sinkAlerts > 0 ? Amber : Green));
        kpi.Children.Add(Tile("车次执行", tripValue, tripUnit, done < due ? Amber : Blue));
        kpi.Children.Add(Tile("本班采装达成", planM3 > 1e-6 ? $"{att:0.#}" : "—", "%", att < 90 ? Amber : Green));
    }

    // ── ② 去向预警卡 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 各去向今日入方与剩余库容。入方口径分家：
    /// 排弃类看【占容方】（库容按它扣），通过型看【吨量】（对卸点通过能力）。
    /// </summary>
    private void BuildSinkCards(List<ProductionTask> tasks, string scope, out int alertCount)
    {
        sinkCards.Children.Clear();
        alertCount = 0;

        SinkRegistry reg;
        try { reg = SinkRegistryLoader.Current; }
        catch { reg = SinkRegistry.Sample(); }

        // 今日投向各去向的量（按采装侧算：排土面的目标本就是入方推导来的，两侧都算会记两遍）
        var planDump = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var actDump = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var planT = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var actT = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tasks.Where(t => t.Process == ProcessType.Load && t.HasDestination))
        {
            string id = string.IsNullOrWhiteSpace(t.DestinationId) ? t.DestinationName : t.DestinationId;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var mix = t.ResolvedMix;
            planDump[id] = planDump.GetValueOrDefault(id) + mix.ToDumpM3(t.TargetVolumeM3);   // ★ 占容方
            actDump[id] = actDump.GetValueOrDefault(id) + mix.ToDumpM3(t.ActualVolumeM3);
            planT[id] = planT.GetValueOrDefault(id) + t.TargetTonnageT;
            actT[id] = actT.GetValueOrDefault(id) + t.ActualTonnageT;
        }

        var keys = planDump.Keys.Union(planT.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0)
        {
            sinkHead.Text = $"去向 · {scope}入方与剩余库容（本班无采装任务投向任何去向）";
            return;
        }

        foreach (var id in keys.OrderByDescending(k => planT.GetValueOrDefault(k)))
        {
            var sink = reg.Find(id) ?? reg.All.FirstOrDefault(s => string.Equals(s.Name, id, StringComparison.OrdinalIgnoreCase));
            string name = sink?.Name ?? id;
            string kind = sink?.Kind.Label() ?? "去向";

            bool dumping = sink?.IsDumping ?? false;
            double inDump = planDump.GetValueOrDefault(id), inT = planT.GetValueOrDefault(id);
            double doneDump = actDump.GetValueOrDefault(id), doneT = actT.GetValueOrDefault(id);

            string line1, line2, warn = "";
            double bar = 0;
            Color col = Blue;

            if (dumping && sink is { IsCapacityLimited: true })
            {
                double left = sink.RemainingM3;
                double after = Math.Max(0, left - Math.Max(0, inDump - doneDump));
                bar = sink.FillRate;
                line1 = $"今日入方 {inDump / 1e4:0.###} 万m³占容（已排 {doneDump / 1e4:0.###}）";
                line2 = $"剩余库容 {left / 1e4:0.##} 万m³ → 排后 {after / 1e4:0.##} 万m³ · 已填 {sink.FillRate * 100:0.#}%";

                // 预警：本日就要吃掉剩余库容的一大截，或整体充填率已高
                if (left <= 1e-6 || inDump > left) { warn = "⛔ 库容不足，今日排不下"; col = Red; alertCount++; }
                else if (inDump > left * 0.8) { warn = $"⚠ 今日将用掉剩余库容 {inDump / left * 100:0.#}%"; col = Red; alertCount++; }
                else if (sink.FillRate >= 0.9) { warn = "⚠ 充填率已超 90%，须准备接续排土场"; col = Amber; alertCount++; }
                else col = Green;
            }
            else
            {
                // 通过型去向（破碎站/煤仓/堆场）：不占库容，比的是通过能力
                double hours = sink != null && sink.OpenToHour - sink.OpenFromHour > 0 ? sink.OpenToHour - sink.OpenFromHour : 24;
                double cap = sink?.ThroughputCapT(hours) ?? double.PositiveInfinity;
                line1 = $"今日接收 {inT / 1e4:0.###} 万t（已卸 {doneT / 1e4:0.###} 万t）";
                if (double.IsInfinity(cap)) { line2 = "通过型去向 · 不占库容 · 未录通过能力"; col = Blue; }
                else
                {
                    bar = Math.Min(1, inT / Math.Max(1e-6, cap));
                    line2 = $"通过能力 {cap / 1e4:0.###} 万t/{hours:0.#}h（{sink!.AcceptTph:0} t/h）· 占用 {bar * 100:0.#}%";
                    if (inT > cap) { warn = "⛔ 超通过能力，卡车将在卸点排队"; col = Red; alertCount++; }
                    else if (bar > 0.85) { warn = "⚠ 接近通过能力上限"; col = Amber; alertCount++; }
                    else col = Green;
                }
            }

            if (sink is { IsActive: false }) { warn = $"⛔ 状态「{sink.Status}」不接收"; col = Red; alertCount++; }
            if (sink == null) { warn = "⚠ 该去向不在登记簿内，库容与能力无从校核"; col = Amber; alertCount++; }

            // 有告警才给「处置建议」：这是 DispatchEngine.OnSinkCongested 唯一的界面入口 ——
            // 那个触发器一直写在引擎里，却从来没有任何地方能点到它（只有 OnFault 有入口）。
            string sid = sink?.Id ?? id;
            Action? advise = warn.Length > 0 ? () => ShowSinkAdvice(sid, name) : null;
            sinkCards.Children.Add(SinkCard($"{name}（{kind}）", line1, line2, warn, bar, col, advise));
        }

        sinkHead.Text = $"去向 · {scope}入方与剩余库容（{keys.Count} 个去向 · 告警 {alertCount} 处）";
    }

    /// <summary>
    /// 卸点拥堵/库容告警的处置建议 —— 跑 <see cref="DispatchEngine.OnSinkCongested"/>：
    /// 判定是否触发分流重排，并把"哪几个面改投哪儿"摊开给调度员看。
    /// <para>看板本身仍是只读的：这里只算建议、不落任何盘，真要改计划去「生产任务动态调整」。</para>
    /// </summary>
    private async void ShowSinkAdvice(string sinkId, string sinkName)
    {
        string text;
        try
        {
            var cfg = SampleTaskBoard.Config();
            var adv = DispatchEngine.OnSinkCongested(sinkId, cfg, SampleTaskBoard.Day(),
                                                     ShiftScope.AnchorHour(ShiftSelector.Selected(shiftCombo)));
            text = adv.Summary;
            if (adv.Suggestions.Count > 0)
                text += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, adv.Suggestions);
        }
        catch (Exception ex)
        {
            text = $"重排引擎不可用（{ex.Message}）。请到「生产任务动态调整」手动处置。";
        }

        await TaskUi.Info(this, $"{sinkName} · 卸点处置建议",
            text + Environment.NewLine + Environment.NewLine
                 + "（看板只算建议、不改计划。要执行请到「生产任务动态调整」。）");
    }

    private static Border SinkCard(string title, string line1, string line2, string warn, double bar, Color col,
                                   Action? onAdvice = null)
    {
        var b = NewCard(268);
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 13.5 });
        sp.Children.Add(Sub(line1, 0, 6, 0, 2));
        sp.Children.Add(Sub(line2, 0, 0, 0, 6));

        var track = new Grid { Height = 8 };
        track.Children.Add(new Rectangle { RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(C(0x33, 0x41, 0x55)) });
        if (bar > 0)
            track.Children.Add(new Rectangle
            {
                Width = 240 * Math.Clamp(bar, 0, 1), HorizontalAlignment = HorizontalAlignment.Left,
                RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(col),
            });
        sp.Children.Add(track);

        if (!string.IsNullOrEmpty(warn))
            sp.Children.Add(new TextBlock
            {
                Text = warn, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(col),
            });

        if (onAdvice != null)
        {
            var btn = new Button
            {
                Content = "处置建议 →", FontSize = 11, Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(0, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            };
            ToolTip.SetTip(btn, "跑一次分流重排：哪几个面改投哪儿。只算建议，不改计划。");
            btn.Click += (_, _) => onAdvice();
            sp.Children.Add(btn);
        }

        b.Child = sp;
        return b;
    }

    // ── ③ 主设备卡 ───────────────────────────────────────────────────────────

    private void BuildEquipCards(List<ProductionTask> tasks, List<FaultEvent> faults, double anchor, string shift)
    {
        cards.Children.Clear();

        // 当班是谁在开这台设备 —— 趴窝了要找人，卡上没有名字调度就得再翻一个窗口
        var crew = CrewLookup.Of(_date, shift);

        foreach (var r in SampleTaskBoard.Roster().Where(x => !x.Sub))
        {
            var his = tasks.Where(t => t.Group.MainEquipment == r.EquipId).OrderBy(t => t.StartHour).ToList();
            // 本班内跨过锚点的那条；跨不到就取本班第一条 —— 看过去/未来的班时也得说得出"这台在干啥"
            var cur = his.FirstOrDefault(t => t.StartHour <= anchor && t.EndHour > anchor) ?? his.FirstOrDefault();
            var open = faults.FirstOrDefault(f => f.IsOpen && string.Equals(f.EquipId, r.EquipId, StringComparison.OrdinalIgnoreCase));

            string status; Color col; string detail;
            double prog = 0;        // 时间过了多少（钟走了多少）
            double timeProg = 0;
            if (open != null)
            {
                status = "故障"; col = Red;
                detail = open.Caption;
            }
            else if (cur == null) { status = "无任务"; col = C(0x94, 0xA3, 0xB8); detail = "—"; }
            else if (cur.Process == ProcessType.Idle) { status = cur.Material; col = C(0x88, 0x87, 0x80); detail = cur.WorkZone; }
            else
            {
                bool faultFlag = cur.Reasons.Contains(IncompleteReason.Fault);
                status = faultFlag ? "异常" : "运行";
                col = faultFlag ? Amber : Green;
                detail = cur.HasDestination ? $"{cur.Process.Label()}·{cur.RouteCaption}" : $"{cur.Process.Label()}·{cur.WorkZone}";
                prog = cur.TargetVolumeM3 > 0 ? Math.Clamp((anchor - cur.StartHour) / Math.Max(1e-6, cur.EndHour - cur.StartHour), 0, 1) : 0;
                timeProg = prog;
            }
            string who = crew.OperatorOf(r.EquipId);

            // 持证/出勤结论是派工时算好的，这里只转述、不重算（重算就会与派工窗打架）。
            // 只把**告警**摆上卡片：持证不符 / 有人休班是安全项，调度不该翻第二个窗口才看到。
            string cert = crew.CertNoteOf(r.EquipId), attend = crew.AttendNoteOf(r.EquipId);
            var warns = new List<string>();
            if (cert.StartsWith("⚠", StringComparison.Ordinal)) warns.Add(cert);
            if (attend.StartsWith("⚠", StringComparison.Ordinal)) warns.Add(attend);
            if (warns.Count > 0) detail += Environment.NewLine + string.Join("；", warns);

            cards.Children.Add(EquipCard(r.Display + (who.Length > 0 ? $"　{who}" : ""), status, col, detail, timeProg, cur));
        }
    }

    /// <param name="timeProg">
    /// <b>时间</b>过了多少（0..1）。★ 它不是"干了多少"。原先这一个数既画进度条又写成"进度 40%"，
    /// 旁边紧挨着"实采 1200/2450 m³" —— 两个都叫进度，一个是钟走了多少、一个是料出了多少，
    /// 读起来就是完成率。现在条画的是**量的达成率**，时间只作对照，落后了才点出来。
    /// </param>
    private static Border EquipCard(string name, string status, Color statusCol, string detail, double timeProg, ProductionTask? t)
    {
        var b = NewCard(268);
        var sp = new StackPanel();

        var head = new DockPanel { LastChildFill = false };
        var nm = new TextBlock { Text = name, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 14 };
        DockPanel.SetDock(nm, Avalonia.Controls.Dock.Left); head.Children.Add(nm);
        var pill = new Border
        {
            Background = new SolidColorBrush(statusCol), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 2),
            Child = new TextBlock { Text = status, Foreground = Brushes.White, FontSize = 11.5 },
        };
        DockPanel.SetDock(pill, Avalonia.Controls.Dock.Right); head.Children.Add(pill);
        sp.Children.Add(head);
        sp.Children.Add(Sub(detail, 0, 6, 0, 6));

        // 条 = 量的达成率；时间只画一根细刻度线作对照
        double volProg = t is { TargetVolumeM3: > 0 }
            ? Math.Clamp(t.ActualVolumeM3 / t.TargetVolumeM3, 0, 1) : 0;
        bool behind = volProg + 0.1 < timeProg;      // 量落后时间 10 个点以上才算落后，别为噪声报警

        var track = new Grid { Height = 8 };
        track.Children.Add(new Rectangle { RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(C(0x33, 0x41, 0x55)) });
        if (volProg > 0)
            track.Children.Add(new Rectangle
            {
                Width = 240 * volProg, HorizontalAlignment = HorizontalAlignment.Left,
                RadiusX = 4, RadiusY = 4,
                Fill = new SolidColorBrush(behind ? C(0xD9, 0x8B, 0x1F) : C(0x10, 0xB9, 0x81)),
            });
        if (timeProg > 0.001 && t is { TargetVolumeM3: > 0 })
            track.Children.Add(new Rectangle
            {
                Width = 2, Height = 8, Margin = new Thickness(240 * Math.Clamp(timeProg, 0, 1) - 1, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, Fill = new SolidColorBrush(C(0xCB, 0xD5, 0xE1)),
            });
        sp.Children.Add(track);

        if (t != null && t.Process != ProcessType.Idle && t.TargetVolumeM3 > 0)
            sp.Children.Add(new TextBlock
            {
                Text = $"实采 {t.ActualVolumeM3:0}/{t.TargetVolumeM3:0} m³ = {volProg * 100:0}%"
                     + $"（{t.ActualTonnageT / 1e4:0.###} 万t）· 时间过 {timeProg * 100:0}%"
                     + (behind ? " ⚠ 量落后于时间" : "")
                     + $" · 到位 {t.TrucksOnSite} 车",
                Foreground = new SolidColorBrush(behind ? C(0xD9, 0x8B, 0x1F) : C(0x94, 0xA3, 0xB8)), FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
            });

        b.Child = sp;
        return b;
    }

    // ── ④ 右侧告警栏 ─────────────────────────────────────────────────────────

    private void BuildAlerts(List<FaultEvent> faults, List<PlanViolation> violations, string shift)
    {
        alerts.Children.Clear();

        // 看某一个班时列**该班内发生过的**故障（已复机的也要留痕，交接班要交代）；
        // 看全天时只列当前未复机的。
        bool byShift = shift.Length > 0;
        var listed = (byShift ? faults : faults.Where(f => f.IsOpen)).OrderBy(f => f.StartHour).ToList();
        alerts.Children.Add(SectionTitle(byShift
            ? $"故障设备（{shift} {listed.Count} · 其中未复机 {listed.Count(f => f.IsOpen)}）"
            : $"故障设备（未复机 {listed.Count}）"));
        if (listed.Count == 0)
            alerts.Children.Add(Sub(byShift ? $"{shift}无故障记录。" : "无未复机故障。", 0, 0, 0, 8));
        else
            foreach (var f in listed)
                alerts.Children.Add(AlertLine($"{f.EquipId} · {f.Caption}", f.IsOpen ? Red : Amber));

        var errs = violations.Where(v => v.Severity == ViolationSeverity.Error).ToList();
        alerts.Children.Add(SectionTitle($"Error 级校核（{errs.Count}）"));
        if (errs.Count == 0)
            alerts.Children.Add(Sub("无 Error 级校核，计划可下达。", 0, 0, 0, 8));
        else
            foreach (var v in errs.Take(12))
                alerts.Children.Add(AlertLine($"[{v.Code}] {v.Message}", Red));

        var warns = violations.Where(v => v.Severity == ViolationSeverity.Warn).ToList();
        alerts.Children.Add(SectionTitle($"Warn 级校核（{warns.Count}）"));
        foreach (var v in warns.Take(12))
            alerts.Children.Add(AlertLine($"[{v.Code}] {v.Message}", Amber));
        if (warns.Count > 12)
            alerts.Children.Add(Sub($"…另有 {warns.Count - 12} 条，详见「生产任务编制」的计划校核。", 0, 2, 0, 8));
    }

    private static TextBlock SectionTitle(string s) => new()
    {
        Text = s, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 12.5,
        Margin = new Thickness(0, 8, 0, 5),
    };

    private static Border AlertLine(string text, Color col) => new()
    {
        Margin = new Thickness(0, 0, 0, 5),
        Padding = new Thickness(8, 5, 8, 6),
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(C(0x1E, 0x29, 0x3B)),
        BorderThickness = new Thickness(3, 0, 0, 0),
        BorderBrush = new SolidColorBrush(col),
        Child = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5,
            Foreground = new SolidColorBrush(C(0xCB, 0xD5, 0xE1)),
        },
    };

    // ── 数据装载（全部容错：任何一处不可用都不影响看板其余部分）──────────────

    private List<FaultEvent> SafeFaults()
    {
        try { return TaskPersistence.LoadFaults(_date); }
        catch { return new List<FaultEvent>(); }
    }

    private static List<PlanViolation> SafeViolations()
    {
        try { return SampleTaskBoard.Violations(); }
        catch { return new List<PlanViolation>(); }
    }

    /// <summary>
    /// 本日派车单：优先读落盘的（带实际状态），没有就现算一份只用于显示"应完成多少趟"。
    /// </summary>
    private (List<DispatchOrder> Orders, string Source) SafeOrders()
    {
        try
        {
            var saved = TaskPersistence.LoadOrdersOfDay(_date);
            if (saved.Count > 0) return (saved, "落盘派车单");
        }
        catch { /* 落盘不可用 → 现算 */ }

        try
        {
            var plan = DispatchEngine.Expand(SampleTaskBoard.Day(), _date);
            return (plan.Orders, plan.Orders.Count > 0 ? "现算（未保存派车单）" : "尚未生成派车单");
        }
        catch { return (new List<DispatchOrder>(), "派车单不可用"); }
    }

    // ── 画笔小工具 ───────────────────────────────────────────────────────────

    private static readonly Color Red = C(0xE2, 0x4B, 0x4A);
    private static readonly Color Amber = C(0xD9, 0x8B, 0x1F);
    private static readonly Color Green = C(0x1D, 0x9E, 0x75);
    private static readonly Color Blue = C(0x38, 0x7B, 0xD5);

    private static Border NewCard(double width) => new()
    {
        Width = width,
        Margin = new Thickness(0, 0, 12, 12),
        Padding = new Thickness(14, 12, 14, 12),
        CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(C(0x1E, 0x29, 0x3B)),
    };

    private static TextBlock Sub(string text, double l, double t, double r, double b) => new()
    {
        Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(C(0xCB, 0xD5, 0xE1)),
        Margin = new Thickness(l, t, r, b),
    };

    private static Border Tile(string title, string value, string unit, Color accent)
    {
        var box = new Border
        {
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(14, 8, 14, 9),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(C(0x1E, 0x29, 0x3B)),
            BorderThickness = new Thickness(0, 0, 0, 3),
            BorderBrush = new SolidColorBrush(accent),
            MinWidth = 150,
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 11.5, Foreground = new SolidColorBrush(C(0x94, 0xA3, 0xB8)) });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        line.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        line.Children.Add(new TextBlock { Text = unit, FontSize = 11, Margin = new Thickness(5, 6, 0, 0), Foreground = new SolidColorBrush(C(0xCB, 0xD5, 0xE1)) });
        sp.Children.Add(line);
        box.Child = sp;
        return box;
    }

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
