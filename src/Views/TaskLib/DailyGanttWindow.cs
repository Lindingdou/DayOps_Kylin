// 忠实移植自原 PitMine3D Modules/TaskLib/Gantt/DailyGanttWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Gantt;
using SinkRegistryLoader = PitMine3D.Kylin.TaskLib.Engine.SinkRegistryLoader;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 消歧 System.Threading.Tasks.TaskStatus

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 设备作业甘特窗口（按日·三班）。行=设备、条=任务（工序配色），含现在线 / 爆破窗口 /
/// 检修空闲 / 当日达成度。四个视图维度：按设备类型 / 按设备 / 按作业区 / 按去向。
/// 卡车行画的是按编组循环时间展开的【车次条】，不是影子条。
/// 点条出七问明细卡（设备怎么组合 / 在什么地方 / 排到哪 / 干什么活 / 要干多久 / 实际作业 / 是否完成）。
/// </summary>
public sealed class DailyGanttWindow : Window
{
    private DailyGanttModel _model = null!;
    private List<ProductionTask> _tasks = SampleTaskBoard.Day();
    private List<PlanViolation> _violations = SampleTaskBoard.Violations();
    private List<RosterEntry> _roster = SampleTaskBoard.Roster();

    // 当前显示的这一天（按日翻页时整体换掉；样例日以外要有快照才有数据）
    private string _dateLabel = SampleTaskBoard.DateLabel;
    private double _nowHour = SampleTaskBoard.NowHour;
    private double _blastStart = SampleTaskBoard.BlastStart;
    private double _blastEnd = SampleTaskBoard.BlastEnd;
    /// <summary>本日全部停产时窗（逐炮画带子）。翻到快照日时清空——快照里没有爆破信息。</summary>
    private List<BlastWindow> _blasts = new();
    private string _daySource = "";

    /// <summary>
    /// 班次口径的筛选结果。<b>甘特与表头统计都取这一份</b> ——
    /// 两边各取各的话，图上是一个班、上面的数字是全天，而它们并排摆着。
    /// </summary>
    private GanttShiftFilter.Result _shiftScope = new();

    private enum View { Equip, Zone, Type, Dest, Chain }
    private View _view = View.Type;   // 默认：排班按设备类型成组处理

    // ── 控件（对应原 XAML 的 x:Name）──
    private readonly TextBlock srcText = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xDE, 0xEA)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 2, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock dateText = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold, MinWidth = 150, TextAlignment = TextAlignment.Center };
    private readonly ComboBox cbShift = new() { Width = 88, Height = 26, Margin = new Thickness(0, 0, 16, 0) };
    private readonly Button btnByType, btnByEquip, btnByZone, btnByChain, btnByDest, btnSaveDay, btnToday;
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Canvas ganttCanvas = new() { ClipToBounds = true, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top };   // 定高画布在 ScrollViewer 里要贴顶（Avalonia 默认居中）
    private readonly StackPanel detailPanel = new();
    private readonly Canvas progressCanvas = new() { Height = 14, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
    private readonly TextBlock progressText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly WrapPanel legendPanel = new() { Orientation = Orientation.Horizontal };
    private bool _loaded;

    public DailyGanttWindow()
    {
        Title = "设备作业甘特 — 日常生产组织";
        TaskUi.Place(this, 1480, 820);
        _blasts = SafeBlasts();
        BuildModel();

        // 标题条（深岩灰，区别于编制类窗的橙）。DockPanel 而非 StackPanel：横向 StackPanel 给子元素无限宽，TextTrimming 不会生效
        var headDock = new DockPanel { LastChildFill = true };
        var title = new TextBlock { Text = "设备作业甘特", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(title, Avalonia.Controls.Dock.Left);
        // 来源文案是「月计划·去向·运距·编组」四段拼接，很长：裁到窗宽，全文挂 ToolTip
        srcText.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = srcText });
        headDock.Children.Add(title); headDock.Children.Add(srcText);
        var header = new Border
        {
            Padding = new Thickness(16, 10), Child = headDock,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0x47, 0x55, 0x69), 0), new GradientStop(Color.FromRgb(0x33, 0x41, 0x55), 1) },
            },
        };

        // 工具条：日期 + 视图钮 + 保存/回当日
        var tool = new DockPanel { LastChildFill = true };
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); tool.Children.Add(c); }
        void R(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Right); tool.Children.Add(c); }
        var prev = TaskUi.Btn("‹", () => ShiftDay(-1), 30); prev.Margin = new Thickness(0, 0, 4, 0); L(prev);
        TaskUi.Theme(dateText, TextBlock.ForegroundProperty, "Theme.Text.Body"); L(dateText);
        var next = TaskUi.Btn("›", () => ShiftDay(+1), 30); next.Margin = new Thickness(4, 0, 16, 0); L(next);
        var shiftLbl = TaskUi.Lbl("班次"); shiftLbl.VerticalAlignment = VerticalAlignment.Center; shiftLbl.Margin = new Thickness(0, 0, 6, 0); L(shiftLbl);
        ToolTip.SetTip(cbShift, "日甘特是全天视图，所以这里**默认「全部」**（别的窗口默认落当前班）。\n切到某一班时，表头的计划/实绩/达成度会按同一把尺子重算 —— 条形是一个班、数字是全天的话，\n读的人只会以为这一班干了全天的量。\n⚠ 没有班次归属的任务哪个班都进不去，只有「全部」看得见 —— 切班时会点名报数。");
        L(cbShift);
        btnByType = TaskUi.Btn("按设备类型", () => SetView(View.Type), 84); btnByType.Margin = new Thickness(0, 0, 6, 0); L(btnByType);
        btnByEquip = TaskUi.Btn("按设备", () => SetView(View.Equip), 62); btnByEquip.Margin = new Thickness(0, 0, 6, 0); L(btnByEquip);
        btnByZone = TaskUi.Btn("按作业区", () => SetView(View.Zone), 74); btnByZone.Margin = new Thickness(0, 0, 6, 0); L(btnByZone);
        btnByChain = TaskUi.Btn("按工序链", () => SetView(View.Chain), 76); btnByChain.Margin = new Thickness(0, 0, 6, 0);
        ToolTip.SetTip(btnByChain, "一个面一组，组里一道工序一行 —— 穿孔→爆破→采装→运输→排土，衔接与等待一眼可见"); L(btnByChain);
        btnByDest = TaskUi.Btn("按去向", () => SetView(View.Dest), 62); btnByDest.Margin = new Thickness(0, 0, 16, 0);
        ToolTip.SetTip(btnByDest, "汇侧视角：各排土场/破碎站/煤仓 今日入方占容与剩余库容；同一去向的采装条与排土条同组同色"); L(btnByDest);
        // 「周览」「日精细」两个钮已去掉（原版注释）：换上来的是本窗口真正缺的那件事——把这一天的计划**存下来**
        btnSaveDay = TaskUi.Btn("保存本日计划", OnSaveDay, 96, bold: true); btnSaveDay.Margin = new Thickness(0);
        ToolTip.SetTip(btnSaveDay, "把当前这一天的任务与校核落成计划快照（%LOCALAPPDATA%/PitMine/task_snapshots）。\n存过之后按日翻页就能回看这一天，达成度/报表也能拿它对账。");
        R(btnSaveDay);
        btnToday = TaskUi.Btn("回当日", OnToday, 62); btnToday.Margin = new Thickness(0, 0, 8, 0);
        ToolTip.SetTip(btnToday, "翻页看过历史快照后，回到当前作业日的实时盘子"); R(btnToday);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        toolStatus.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = toolStatus });
        tool.Children.Add(toolStatus);

        // 甘特画布 + 任务明细
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300"), Margin = new Thickness(10, 10, 10, 6) };
        var ganttScroll = new ScrollViewer { Content = ganttCanvas, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(2) };
        var ganttBox = Panel(ganttScroll, new Thickness(0));
        var detailScroll = new ScrollViewer { Content = detailPanel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(12, 10) };
        var detailBox = Panel(detailScroll, new Thickness(6, 0, 0, 0));
        Grid.SetColumn(ganttBox, 0); Grid.SetColumn(detailBox, 1);
        body.Children.Add(ganttBox); body.Children.Add(detailBox);
        ganttCanvas.SizeChanged += (_, _) => GanttRenderer.Render(ganttCanvas, _model);
        ganttCanvas.PointerPressed += OnGanttClick;

        // 底部：当日工作量进度
        var prog = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var progLbl = TaskUi.Hint("当日工作量", 12.5); progLbl.VerticalAlignment = VerticalAlignment.Center; progLbl.Margin = new Thickness(0, 0, 12, 0);
        TaskUi.Theme(progressText, TextBlock.ForegroundProperty, "Theme.Text.Body");
        Grid.SetColumn(progLbl, 0); Grid.SetColumn(progressCanvas, 1); Grid.SetColumn(progressText, 2);
        prog.Children.Add(progLbl); prog.Children.Add(progressCanvas); prog.Children.Add(progressText);
        var progBox = Panel(prog, new Thickness(10, 0, 10, 6)); progBox.Padding = new Thickness(12, 8);
        progressCanvas.SizeChanged += (_, _) => GanttRenderer.RenderProgress(progressCanvas, _model);

        // 图例
        var legend = new Border { Padding = new Thickness(14, 7), BorderThickness = new Thickness(0, 1, 0, 0), Child = legendPanel };
        TaskUi.Theme(legend, Border.BorderBrushProperty, "Theme.Surface.Border");

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(body, 2); Grid.SetRow(progBox, 3); Grid.SetRow(legend, 4);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(body); g.Children.Add(progBox); g.Children.Add(legend);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        cbShift.SelectionChanged += (_, _) => OnShiftChanged();
        Opened += (_, _) => OnLoaded();
    }

    private static Border Panel(Control child, Thickness margin)
    {
        var b = new Border { Margin = margin, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = child };
        TaskUi.Theme(b, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(b, Border.BorderBrushProperty, "Theme.Surface.Border");
        return b;
    }

    /// <summary>
    /// 当前选中的班次（空 = 全天）。
    /// <para>构造期 <c>cbShift</c> 还没装配，此时一律**全天** ——
    /// 返回一个班次名会让开窗第一帧只画三分之一的天，然后 Loaded 之后又变回全天。</para>
    /// </summary>
    private string ShiftFilterText()
        => cbShift == null || cbShift.Items.Count == 0 ? "" : ShiftSelector.Filter(cbShift);

    /// <summary>本日全部停产时窗；盘子装不出来时退回空表（由那对标量兜底画一段）。</summary>
    private static List<BlastWindow> SafeBlasts()
    {
        try { return new List<BlastWindow>(ProductionPlanContext.BlastWindowsOfDay); }
        catch { return new List<BlastWindow>(); }
    }

    private void BuildModel()
    {
        // ★ 先按班次筛，再造模型 —— 四个工厂方法算表头统计时吃的就是这一份，
        //   于是「条形与统计同源」是结构上成立的，不靠调用方记得两处都筛。
        _shiftScope = GanttShiftFilter.Apply(_tasks, ShiftFilterText());
        var _tasksInScope = _shiftScope.Tasks;

        _model = _view switch
        {
            View.Zone => DailyGanttModel.FromTasksByZone(_tasksInScope, _roster, _dateLabel, _nowHour, _blastStart, _blastEnd),
            View.Type => DailyGanttModel.FromTasksByEquipType(_tasksInScope, _roster, _dateLabel, _nowHour, _blastStart, _blastEnd),
            View.Dest => DailyGanttModel.FromTasksByDestination(_tasksInScope, _roster, _dateLabel, _nowHour, _blastStart, _blastEnd),
            // 按面看工序链：一个面一组，组里一道工序一行，从上往下就是穿孔→爆破→采装→运输→排土。
            View.Chain => DailyGanttModel.FromTasksByFaceChain(_tasksInScope, _roster, _dateLabel, _nowHour, _blastStart, _blastEnd),
            _ => DailyGanttModel.FromTasks(_tasksInScope, _roster, _dateLabel, _nowHour, _blastStart, _blastEnd),
        };
        // 逐炮的停产带（四个工厂方法只收一对标量，故在这儿补上；空 = 回落那一对）
        _model.Blasts = new List<BlastWindow>(_blasts);
    }

    /// <summary>外部（排产引擎）喂入真实一天的甘特数据。</summary>
    public void SetModel(DailyGanttModel model)
    {
        _model = model ?? DailyGanttModel.Sample();
        if (_loaded) { RefreshTexts(); RenderAll(); }
    }

    /// <summary>外部喂入一整份计划（任务 + 校核），四个视图共用。</summary>
    public void SetPlan(ExploderResult result, string dateLabel, double nowHour, double blastStart, double blastEnd)
        => SetPlan(result, dateLabel, nowHour, blastStart, blastEnd, null);

    /// <summary>同上，另给一份设备行清单（喂入的计划不是当日盘子时，花名册也得跟着换，否则一行都画不出）。</summary>
    public void SetPlan(ExploderResult result, string dateLabel, double nowHour, double blastStart, double blastEnd, List<RosterEntry>? roster)
    {
        if (result == null) return;
        if (roster != null) _roster = roster;
        TruckTripPlanner.Invalidate();
        _tasks = result.Tasks;
        _violations = result.Violations;
        _dateLabel = dateLabel;
        _nowHour = nowHour; _blastStart = blastStart; _blastEnd = blastEnd;
        _blasts.Clear();   // 外部喂入的是一对标量，逐炮带由那对兜底
        BuildModel();
        if (_loaded) { RefreshTexts(); RenderAll(); ShowDetailHint(); }
    }

    private void OnLoaded()
    {
        BuildLegend();
        UpdateViewButtons();
        BindShiftCombo();
        _loaded = true;
        srcText.Text = "来源：" + SampleTaskBoard.SourceLabel;
        toolStatus.Text = ViewHint(_view);
        RefreshTexts();
        RenderAll();
        ShowDetailHint();
    }

    /// <summary>
    /// 装配班次下拉框。<b>这个窗默认「全部」</b>，是一条写明的例外：
    /// <see cref="ShiftSelector"/> 的规矩是默认落当前班（任务书/派车单本身一班一份），
    /// 而日甘特的整张图就是 0–24 点那条轴，默认只显示一个班等于开窗就藏掉三分之二的天。
    /// </summary>
    private void BindShiftCombo()
    {
        ShiftSelector.Bind(cbShift, includeAll: true);
        for (int i = 0; i < cbShift.Items.Count; i++)
            if ((cbShift.Items[i] as ComboBoxItem)?.Content?.ToString() == ShiftScope.All)
            { cbShift.SelectedIndex = i; break; }
    }

    private void OnShiftChanged()
    {
        if (!_loaded) return;
        BuildModel();
        RefreshTexts();
        RenderAll();
        ShowDetailHint();
    }

    private void RefreshTexts()
    {
        dateText.Text = _model.DateLabel;
        string work = _model.TransportWorkWanTKm > 0
            ? $" · 运输功 {_model.TransportWorkWanTKm:0.##}万t·km（均运距 {_model.WeightedAvgHaulKm:0.##}km）"
            : "";
        progressText.Text =
            $"计划 {_model.DayPlanWanM3:0.#}万m³ · 实绩(至{_model.NowText}) {_model.DayActualWanM3:0.#} · 达成度 {_model.AttainmentPct:0}%{work}";
        // 筛了班次就把口径挂出来 —— 「计划 3.2 万m³」是全天还是一个班，光看这个数分不出来
        if (_shiftScope.Filtered) toolStatus.Text = _shiftScope.Caption;
    }

    private void RenderAll()
    {
        GanttRenderer.Render(ganttCanvas, _model);
        GanttRenderer.RenderProgress(progressCanvas, _model);
    }

    // ── 图例：工序色块 + 工序接续 ──
    private void BuildLegend()
    {
        legendPanel.Children.Clear();
        (string label, Color fill)[] items =
        {
            ("穿孔", Color.FromRgb(0xCE, 0xCB, 0xF6)),
            ("爆破", Color.FromRgb(0xF7, 0xC1, 0xC1)),
            ("采装", Color.FromRgb(0xB5, 0xD4, 0xF4)),
            ("运输(车次)", Color.FromRgb(0x9F, 0xE1, 0xCB)),
            ("排土", Color.FromRgb(0xFA, 0xC7, 0x75)),
        };
        foreach (var (label, fill) in items)
            legendPanel.Children.Add(LegendChip(label, fill, hatch: false));
        legendPanel.Children.Add(LegendChip("检修/空闲", Color.FromRgb(0xE6, 0xE4, 0xDC), hatch: true));

        var note = new TextBlock
        {
            Text = "｜ 工序接续：穿孔→爆破→采装→运输→排土　｜ 卡车行每小条 = 一个车次（T_c 循环时间展开，条左端色帽 = 去向）",
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0)
        };
        TaskUi.Theme(note, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        legendPanel.Children.Add(note);
    }

    private static Control LegendChip(string label, Color fill, bool hatch)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var box = new Rectangle
        {
            Width = 12, Height = 12, RadiusX = 2, RadiusY = 2,
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x55, 0x55, 0x55)), StrokeThickness = 0.6,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
        };
        if (hatch) box.Stroke = new SolidColorBrush(Color.FromRgb(0x5F, 0x5E, 0x5A));
        sp.Children.Add(box);
        var t = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        TaskUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Body");
        sp.Children.Add(t);
        return sp;
    }

    // ── 日期翻页：按快照取真实那一天（样例台账只有一天，没快照就明确说清楚，不造假数据）──
    private void ShiftDay(int delta)
    {
        if (!TryParseDay(_dateLabel, out var cur))
        {
            toolStatus.Text = $"当前数据日期「{_dateLabel}」无法解析，按日翻页不可用";
            return;
        }

        var target = cur.AddDays(delta);
        var snap = LoadSnapshot(target, out string label);
        if (snap == null)
        {
            toolStatus.Text = $"{target:yyyy-MM-dd} 无任务快照 —— 样例台账仅含 {SampleTaskBoard.DateLabel}；"
                            + $"在「生产任务编制」保存该日计划后（{TaskPersistence.SnapshotDir()}）即可翻页查看";
            return;
        }

        TruckTripPlanner.Invalidate();
        _tasks = snap.Tasks;
        _violations = snap.Violations;
        _dateLabel = label;
        // 快照不含爆破窗口与"现在"：过去的日子把现在线推到班末，未来的日子推到班首。
        _blastStart = _blastEnd = 0;
        _blasts.Clear();
        _nowHour = target.Date < DateTime.Today ? 24
                 : target.Date > DateTime.Today ? 0
                 : DateTime.Now.TimeOfDay.TotalHours;
        _daySource = $"任务快照 {label}（{snap.Tasks.Count} 项任务）";

        BuildModel();
        RefreshTexts();
        RenderAll();
        ShowDetailHint();
        toolStatus.Text = $"{(delta < 0 ? "← 上一日" : "下一日 →")}：{label} · 载入{_daySource}";
    }

    /// <summary>"2026-06-17 周二" → 日期。取首个空白分隔的词按 yyyy-MM-dd 解析。</summary>
    private static bool TryParseDay(string label, out DateTime day)
    {
        day = default;
        string head = (label ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return DateTime.TryParseExact(head, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
            || DateTime.TryParse(head, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>在快照目录里找该日的计划（文件名形如「任务计划_2026-06-18 周三.json」）。</summary>
    private static ExploderResult? LoadSnapshot(DateTime day, out string label)
    {
        label = $"{day:yyyy-MM-dd} {WeekCn(day)}";
        try
        {
            string dir = TaskPersistence.SnapshotDir();
            if (!Directory.Exists(dir)) return null;

            var file = Directory.GetFiles(dir, $"*{day:yyyy-MM-dd}*.json")
                                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault();
            if (file == null) return null;

            var r = TaskPersistence.Load(file);
            if (r == null || r.Tasks.Count == 0) return null;

            string name = System.IO.Path.GetFileNameWithoutExtension(file);   // 消歧：Shapes.Path 同名
            int us = name.IndexOf('_');
            if (us >= 0 && us + 1 < name.Length) label = name[(us + 1)..];
            return r;
        }
        catch
        {
            return null;   // 快照目录不可读 → 当作没有，不打断看图
        }
    }

    private static string WeekCn(DateTime d) => "周" + d.DayOfWeek switch
    {
        DayOfWeek.Monday => "一", DayOfWeek.Tuesday => "二", DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四", DayOfWeek.Friday => "五", DayOfWeek.Saturday => "六", _ => "日"
    };

    // ── 落盘：本日计划快照 ────────────────────────────────────────────────────
    //  按日翻页读的是 task_snapshots/；计划落盘还有两个下游：达成度评价要拿"当时排的是什么"当分母，
    //  动态调整重排前要有一份基线。存的是**当前显示的这一天**。
    private void OnSaveDay()
    {
        try
        {
            string path = TaskPersistence.DefaultPath(_dateLabel);
            TaskPersistence.Save(path, new ExploderResult
            {
                Tasks = _tasks,
                Violations = _violations ?? new List<PlanViolation>(),
            });
            int err = (_violations ?? new List<PlanViolation>()).Count(v => v.Severity == ViolationSeverity.Error);
            toolStatus.Text = $"已保存 {_dateLabel} 的计划快照：{_tasks.Count} 项任务"
                            + (err > 0 ? $" · 含 {err} 条冲突（快照如实存，签发闸门在「任务下达」）" : "")
                            + $" → {path}";
        }
        catch (Exception ex)
        {
            toolStatus.Text = $"保存失败（{ex.Message}）—— 快照目录：{TaskPersistence.SnapshotDir()}";
        }
    }

    /// <summary>回到当前作业日的实时盘子（翻页看过历史快照之后）。</summary>
    private void OnToday()
    {
        TruckTripPlanner.Invalidate();
        _tasks = SampleTaskBoard.Day();
        _violations = SampleTaskBoard.Violations();
        _dateLabel = SampleTaskBoard.DateLabel;
        _nowHour = SampleTaskBoard.NowHour;
        _blastStart = SampleTaskBoard.BlastStart;
        _blastEnd = SampleTaskBoard.BlastEnd;
        _blasts = SafeBlasts();
        _daySource = "";

        BuildModel();
        RefreshTexts();
        RenderAll();
        ShowDetailHint();
        toolStatus.Text = $"已回到当前作业日 {_dateLabel}（实时盘子）";
    }

    // ── 视图切换：按设备类型 / 按设备 / 按作业区 / 按去向 ──
    private void SetView(View v)
    {
        _view = v;
        UpdateViewButtons();
        BuildModel();
        RefreshTexts();
        RenderAll();
        ShowDetailHint();
        toolStatus.Text = ViewHint(v);
    }

    private static string ViewHint(View v) => v switch
    {
        View.Zone => "按作业区：穿孔/采装/排土 等过程归到各作业区域下",
        View.Type => "按设备类型：电铲/卡车/钻机/推土机 各类设备成组排班",
        View.Dest => "按去向：各排土场/破碎站/煤仓今日入方与剩余库容；同一去向的采装条与排土条同组同色",
        _ => "按设备：逐台设备一行",
    };

    private void UpdateViewButtons()
    {
        btnByType.FontWeight = _view == View.Type ? FontWeight.Bold : FontWeight.Normal;
        btnByEquip.FontWeight = _view == View.Equip ? FontWeight.Bold : FontWeight.Normal;
        btnByZone.FontWeight = _view == View.Zone ? FontWeight.Bold : FontWeight.Normal;
        btnByDest.FontWeight = _view == View.Dest ? FontWeight.Bold : FontWeight.Normal;
        btnByChain.FontWeight = _view == View.Chain ? FontWeight.Bold : FontWeight.Normal;
    }

    // ── 点甘特条 → 右侧任务明细卡（七问） ──
    private void OnGanttClick(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ganttCanvas).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Control fe) return;
        if (fe.Tag is GanttBar bar && bar.Task != null) RenderTaskCard(bar.Task, bar);
        else if (fe.Tag is ProductionTask t) RenderTaskCard(t, null);
    }

    private IBrush B(string key, string fallback)
        => this.TryFindResource(key, out var v) && v is IBrush b ? b : new SolidColorBrush(ParseHex(fallback));

    private static Color ParseHex(string hex) => Color.Parse(hex);

    private void ShowDetailHint()
    {
        detailPanel.Children.Clear();

        // 引擎计划校核结果（去向未定 / 物料不兼容 / 排土库容不足 / 卸点能力不足 / 采排不守恒 / 去向不可用 / 运距缺失 …）
        var vios = new List<PlanViolation>(_violations ?? new List<PlanViolation>());

        // 本窗自检：引擎没报过「去向未定」时，把缺卸点的采装/运输任务顶上来——
        // 这是任务书不得签发的硬条件，不能因为盘子里没走流向分配就在图上看不见。
        // 判据是「逐物料都定了」：混采任务只定了煤、岩没人管，同样不得签发。
        var noDestTasks = _tasks.Where(t => DailyGanttModel.NeedsDestination(t) && !DailyGanttModel.AllMaterialsRouted(t)).ToList();
        int noDest = noDestTasks.Count;
        if (noDest > 0 && !vios.Any(v => v.Code == ViolationCodes.NoDestination))
        {
            var miss = noDestTasks.SelectMany(DailyGanttModel.UnroutedMaterials).Distinct().ToList();
            vios.Insert(0, new PlanViolation
            {
                Severity = ViolationSeverity.Error, Code = ViolationCodes.NoDestination,
                Message = $"{noDest} 项采装/运输任务未指定卸点"
                        + (miss.Count > 0 ? $"（缺去向的物料：{string.Join("、", miss)}）" : "")
                        + "，运距与配车无从算起，任务书不得签发。",
            });
        }

        int nErr = vios.Count(v => v.Severity == ViolationSeverity.Error);
        int nWarn = vios.Count(v => v.Severity == ViolationSeverity.Warn);
        int nInfo = vios.Count - nErr - nWarn;

        var head = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) };
        TaskUi.Theme(head, TextBlock.ForegroundProperty, "Theme.Text.Body");
        head.Text = vios.Count == 0 ? "计划校核（引擎）" : $"计划校核（引擎）　冲突 {nErr} · 告警 {nWarn} · 提示 {nInfo}";
        detailPanel.Children.Add(head);

        if (vios.Count == 0)
        {
            detailPanel.Children.Add(new TextBlock { Text = "✓ 无异常", Foreground = new SolidColorBrush(ParseHex("#FF0F6E56")), FontSize = 13 });
        }
        else
        {
            // 冲突 → 告警 → 提示：先看拦得住签发的
            foreach (var v in vios.OrderByDescending(v => (int)v.Severity))
            {
                var (fill, fg) = v.Severity switch
                {
                    ViolationSeverity.Error => ("#FFFCEBEB", "#FF791F1F"),
                    ViolationSeverity.Warn => ("#FFFAEEDA", "#FF854F0B"),
                    _ => ("#FFF1EFE8", "#FF5F5E5A"),
                };
                var card = new Border { Background = new SolidColorBrush(ParseHex(fill)), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 5) };
                var sp = new StackPanel();
                string title = $"[{SevLabel(v.Severity)}] {v.Code}" + (string.IsNullOrWhiteSpace(v.TaskId) ? "" : $"　{v.TaskId}");
                sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 12, Foreground = new SolidColorBrush(ParseHex(fg)) });
                sp.Children.Add(new TextBlock { Text = v.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(ParseHex(fg)) });
                card.Child = sp;
                detailPanel.Children.Add(card);
            }
        }

        detailPanel.Children.Add(new Border { Height = 1, Background = B("Theme.Surface.Border", "#22888888"), Margin = new Thickness(0, 8, 0, 8) });
        detailPanel.Children.Add(new TextBlock
        {
            Text = "点甘特条看任务明细（卡车行每小条为一个车次）",
            Foreground = B("Theme.Text.Muted", "#FF9AA0A6"),
            FontSize = 13, TextWrapping = TextWrapping.Wrap
        });

        // 数据来源（去向台账 / 快照）：让人知道图上这盘数是哪来的
        string src = SinkSourceLabel();
        if (!string.IsNullOrEmpty(_daySource)) src = _daySource + "　｜　" + src;
        detailPanel.Children.Add(new TextBlock
        {
            Text = src, Foreground = B("Theme.Text.Muted", "#FF9AA0A6"),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        });
    }

    private static string SinkSourceLabel()
    {
        try
        {
            _ = SinkRegistryLoader.Current;   // 触发一次装载，拿来源文案
            return "去向：" + SinkRegistryLoader.LastSourceLabel;
        }
        catch { return "去向台账未装载"; }
    }

    private static string SevLabel(ViolationSeverity s) => s switch
    {
        ViolationSeverity.Error => "冲突", ViolationSeverity.Warn => "告警", _ => "提示"
    };

    private void RenderTaskCard(ProductionTask t, GanttBar? bar)
    {
        detailPanel.Children.Clear();

        // 头：编号 + 工序 + 状态
        var head = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var id = new TextBlock { Text = t.Id, FontFamily = new FontFamily("Consolas, Courier New, monospace"), FontSize = 13, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        TaskUi.Theme(id, TextBlock.ForegroundProperty, "Theme.Text.Body");
        head.Children.Add(id);
        head.Children.Add(Chip(t.Process.Label(), ProcFill(t.Process), ProcText(t.Process)));
        head.Children.Add(Chip(StatusLabel(t), StatusFill(t.Status), StatusText(t.Status)));
        detailPanel.Children.Add(head);
        AddSep();

        var match = TruckTripPlanner.MatchOf(t);

        // 点的是车次条 → 先把这一趟说清楚（混采任务：这一趟走的是哪条线）
        if (bar is { TripIndex: > 0 })
        {
            string trip = $"第 {bar.TripIndex} 趟 / 共 {bar.TripCount} 趟 · {bar.TruckId}\n{Hm(bar.StartHour)}–{Hm(bar.EndHour)}";

            if (!string.IsNullOrWhiteSpace(bar.TripMaterial))
                trip += $"\n拉 {bar.TripMaterial}"
                      + (string.IsNullOrWhiteSpace(bar.TripSink) ? " · ⚠ 去向未定" : $" → {bar.TripSink}")
                      + (bar.TripHaulKm > 1e-6 ? $" · 运距 {bar.TripHaulKm:0.##} km" : "");

            if (bar.TripCycleMin > 1e-6) trip += $"\n本趟循环 T_c {bar.TripCycleMin:0.#}min";
            if (match != null)
                trip += match.IsMultiLeg
                    ? $"（车队加权 T_c {match.CycleTimeMin:0.#}min · 装车节拍 τ_L {match.LoadTaktMin:0.#}min）"
                    : $"\n循环 T_c {match.CycleTimeMin:0.#}min · 装车节拍 τ_L {match.LoadTaktMin:0.#}min";

            AddRow("这一趟", trip);
        }
        else if (bar is { IsShadow: true })
        {
            AddRow("这条运输", $"{bar.TruckId} 整段随铲作业（编组未解出，未能展开车次）", warn: true);
        }

        AddRow("设备怎么组合", t.Group.Caption
            + (t.Group.GroupCapacityM3PerH > 0 ? $"\n编组班产 {t.Group.GroupCapacityM3PerH:0} m³/h" : "")
            + (match != null ? $"\n匹配系数 MF {match.MatchFactor:0.00}（{match.Bottleneck}）· 荐 {match.OptimalTrucks} 车" : ""));

        AddRow("在什么地方", t.LocationCaption);

        // ★ 第三问「排到哪」——源与汇对称，任务书上的卸载地点即此
        if (t.Process == ProcessType.Dump)
        {
            // 排土任务就在卸点上作业：写本场排弃与占容方，不计运距/运输功（推土机不外运）
            string at = t.HasDestination
                ? (string.IsNullOrWhiteSpace(t.DestinationName) ? t.DestinationId : t.DestinationName)
                : t.WorkZone;
            AddRow("排到哪", $"本场排弃 · {at}\n占容 {t.TargetDumpM3:N0} m³（Kr 折算后的库容消耗）");
        }
        else if (DailyGanttModel.RoutesOf(t).Count > 1)
        {
            // ── 混采任务：一条任务多个去向，逐分项列全 ──
            var routes = DailyGanttModel.RoutesOf(t);
            var lines = new List<string>();
            foreach (var d in routes)
            {
                double m3 = t.TargetVolumeM3 * d.Fraction;

                // 车次层的细账：这条线该占多少趟、它自己的循环时间是多少
                string trip = "";
                if (match != null)
                    foreach (var l in match.Legs)
                        if (string.Equals(l.MaterialCode, d.MaterialCode, StringComparison.OrdinalIgnoreCase))
                        {
                            trip = $"　｜ 车次 {l.Fraction * 100:0.#}% · T_c {l.CycleMin:0.#}min · τ_L {l.TaktMin:0.#}min";
                            break;
                        }

                lines.Add(d.HasDestination
                    ? $"· {d.Spec.Name} {d.Fraction * 100:0.#}% → "
                      + $"{(string.IsNullOrWhiteSpace(d.DestinationName) ? d.DestinationId : d.DestinationName)}"
                      + $"（{d.DestinationKind.Label()}）　{m3:N0} m³实方 · {d.EffectiveHaulKm:0.##} km" + trip
                    : $"· {d.Spec.Name} {d.Fraction * 100:0.#}% → ⚠ 未指定卸点（{m3:N0} m³实方无处可去）");
            }

            string sum = $"加权运距 {DailyGanttModel.WeightedHaulKm(t):0.##} km";
            if (t.TransportWorkBySplitTKm > 1e-6)
                sum += $" · 运输功 {t.TransportWorkBySplitTKm / 1e4:0.###} 万t·km（按各物料各自的运距算）";
            if (match is { IsMultiLeg: true })
                sum += $"\n车队按加权 T_c {match.CycleTimeMin:0.#}min / τ_L {match.LoadTaktMin:0.#}min 解配车 → 荐 {match.OptimalTrucks} 车"
                     + "（份额取吨量口径＝车次口径：每趟都拉满一车，趟数只随吨量走）";

            AddRow("排到哪", string.Join("\n", lines) + "\n" + sum,
                   warn: !DailyGanttModel.AllMaterialsRouted(t));
        }
        else if (t.HasDestination)
        {
            string dest = t.DestinationCaption;
            string haul = $"运距 {t.HaulDistanceKm:0.##} km";
            if (t.EquivHaulKm > 1e-6 && Math.Abs(t.EquivHaulKm - t.HaulDistanceKm) > 0.01)
                haul += $"（等效 {t.EquivHaulKm:0.##} km）";
            if (t.TransportWorkTKm > 1e-6) haul += $" · 运输功 {t.TransportWorkTKm / 1e4:0.###} 万t·km";
            AddRow("排到哪", dest + "\n" + haul);
        }
        else if (DailyGanttModel.NeedsDestination(t))
        {
            AddRow("排到哪", "未指定卸点 —— 运距与配车无从算起，任务书不得签发", warn: true);
        }

        if (t.Process == ProcessType.Idle)
        {
            AddRow("状态", t.Material);
        }
        else
        {
            string mix = t.ResolvedMix.Caption;
            string what = t.Process.Label() + (string.IsNullOrEmpty(mix) ? "" : $" · {mix}");
            if (t.TargetVolumeM3 > 0)
                what += $"\n目标 {t.TargetVolumeM3:0} m³实方 · {t.TargetTonnageT:0} t · 松方 {t.TargetLooseM3:0} m³";
            if (t.QualityTarget != null) what += $"\n质量目标 {t.QualityTarget.Caption}";
            AddRow("干什么活", what);

            AddRow("要干多久", $"{t.Shift} {Hm(t.StartHour)}–{Hm(t.EndHour)} · 计划 {t.PlannedHours:0.#} 工时");

            string act = t.ActualVolumeM3 > 0 || t.ActualHours > 0
                ? $"实采 {t.ActualVolumeM3:0} m³（{t.ActualTonnageT:0} t）· {t.ActualHours:0.#} 工时" + (t.TrucksOnSite > 0 ? $" · 到位 {t.TrucksOnSite} 车" : "")
                : "（未开始）";
            if (t.QualityActual != null) act += $"\n实测 {t.QualityActual.Caption}";
            AddRow("实际作业", act);

            string verdict = t.TargetVolumeM3 > 0 ? $"达成度 {t.AttainmentPct:0}%" + (t.ShortfallM3 > 0 ? $" · 欠 {t.ShortfallM3:0} m³" : "") : StatusLabel(t);
            AddRow("是否完成·为啥", verdict);
        }

        if (t.Reasons.Count > 0)
        {
            var rc = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            foreach (var r in t.Reasons)
                rc.Children.Add(Chip(r.Label(), ParseHex("#FFFCEBEB"), ParseHex("#FF791F1F")));
            detailPanel.Children.Add(rc);
        }

        // 与本任务相关的校核条目
        var mine = (_violations ?? new List<PlanViolation>()).Where(v => v.TaskId == t.Id).ToList();
        foreach (var v in mine)
        {
            var fg = v.Severity switch
            {
                ViolationSeverity.Error => "#FF791F1F",
                ViolationSeverity.Warn => "#FF854F0B",
                _ => "#FF5F5E5A",
            };
            detailPanel.Children.Add(new TextBlock
            {
                Text = $"[{SevLabel(v.Severity)}] {v.Code}：{v.Message}",
                Foreground = new SolidColorBrush(ParseHex(fg)), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });
        }
    }

    private void AddSep()
        => detailPanel.Children.Add(new Border { Height = 1, Background = B("Theme.Surface.Border", "#22888888"), Margin = new Thickness(0, 0, 0, 8) });

    private void AddRow(string label, string value, bool warn = false)
    {
        detailPanel.Children.Add(new TextBlock { Text = label, Foreground = B("Theme.Text.Muted", "#FF9AA0A6"), FontSize = 11.5, Margin = new Thickness(0, 6, 0, 1) });
        detailPanel.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = warn ? new SolidColorBrush(ParseHex("#FF791F1F")) : B("Theme.Text.Body", "#FF202020"),
            FontWeight = warn ? FontWeight.Bold : FontWeight.Normal,
            FontSize = 13, TextWrapping = TextWrapping.Wrap
        });
    }

    private static Border Chip(string text, Color fill, Color fg) => new()
    {
        Background = new SolidColorBrush(fill), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 0, 5, 0),
        Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(fg), FontSize = 11.5 }
    };

    private static string Hm(double h)
    {
        int hh = (int)h; int mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh:00}:{mm:00}";
    }

    private static Color ProcFill(ProcessType p) => p switch
    {
        ProcessType.Drill => ParseHex("#FFCECBF6"), ProcessType.Load => ParseHex("#FFB5D4F4"),
        ProcessType.Haul => ParseHex("#FF9FE1CB"), ProcessType.Dump => ParseHex("#FFFAC775"),
        ProcessType.Blast => ParseHex("#FFF7C1C1"), _ => ParseHex("#FFE6E4DC")
    };
    private static Color ProcText(ProcessType p) => p switch
    {
        ProcessType.Drill => ParseHex("#FF26215C"), ProcessType.Load => ParseHex("#FF042C53"),
        ProcessType.Haul => ParseHex("#FF04342C"), ProcessType.Dump => ParseHex("#FF412402"),
        ProcessType.Blast => ParseHex("#FF501313"), _ => ParseHex("#FF2C2C2A")
    };
    /// <summary>
    /// 条上的状态文案。**先看单据轴**：撤回的任务执行轴仍是 Planned，
    /// 只按 Status 显示就会写成"计划" —— 而它已经不算数了。
    /// </summary>
    private static string StatusLabel(ProductionTask t)
        => t.IsWithdrawn ? "已撤回" : StatusLabel(t.Status);

    private static string StatusLabel(TaskStatus s) => s switch
    {
        TaskStatus.Planned => "计划", TaskStatus.Dispatched => "已下达", TaskStatus.Running => "执行中",
        TaskStatus.Done => "完成", TaskStatus.Partial => "部分完成", TaskStatus.Failed => "未完成", _ => "—"
    };
    private static Color StatusFill(TaskStatus s) => s switch
    {
        TaskStatus.Done => ParseHex("#FFE1F5EE"), TaskStatus.Running => ParseHex("#FFE6F1FB"),
        TaskStatus.Partial => ParseHex("#FFFAEEDA"), TaskStatus.Failed => ParseHex("#FFFCEBEB"), _ => ParseHex("#FFF1EFE8")
    };
    private static Color StatusText(TaskStatus s) => s switch
    {
        TaskStatus.Done => ParseHex("#FF0F6E56"), TaskStatus.Running => ParseHex("#FF0C447C"),
        TaskStatus.Partial => ParseHex("#FF854F0B"), TaskStatus.Failed => ParseHex("#FF791F1F"), _ => ParseHex("#FF5F5E5A")
    };
}
