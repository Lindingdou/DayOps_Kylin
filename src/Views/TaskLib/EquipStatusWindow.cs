// 忠实移植自原 PitMine3D Modules/TaskLib/Features/EquipStatusWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 设备状态·故障报修：执行期设备状态 + 报修/维修/复机。
///
/// <para>
/// 原先"液压故障 0.8h（已报修 R-2）"是写死在代码里的一句话，报修/复机两个按钮什么也不做。
/// 现在这两个按钮真写 <see cref="FaultEvent"/> 并落盘 %LOCALAPPDATA%/PitMine/fault_events/，
/// 下游（实绩录入的故障工时、调度看板的故障设备列表、重排引擎的故障原因码）读的都是这份记录。
/// </para>
/// <para>
/// 报修之后还要回答调度员真正关心的问题——"这台趴窝了，我这个班怎么办"。
/// 故点报修即调 <see cref="DispatchEngine.OnFault"/>：判定是否越过重排门槛，
/// 越过就跑 <see cref="TaskRescheduler"/> 给出备机顶替 / 补车 / 欠产回摊的具体建议。
/// </para>
/// </summary>
public sealed class EquipStatusWindow : Window
{
    public sealed class Row
    {
        public string Equip { get; set; } = "";
        public string Category { get; set; } = "";
        public string Status { get; set; } = "";
        public string Current { get; set; } = "";
        public string Hours { get; set; } = "";
        public string FaultHours { get; set; } = "";
        public string Fault { get; set; } = "";
        /// <summary>挂钟口径的报修/复机时刻（格子里放不下，走 ToolTip）。</summary>
        public string FaultAudit { get; set; } = "";

        public string EquipId { get; init; } = "";
    }

    private readonly string _date = SampleTaskBoard.DateLabel;
    private readonly List<Row> _rows = new();
    private List<FaultEvent> _faults = new();

    private readonly TextBlock subTitle;
    private readonly CheckBox plannedBox = new() { Content = "计划检修", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox catCombo = new() { Width = 86, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox hoursBox = new() { Width = 60, Margin = new Thickness(0, 0, 12, 0), Text = "1.0" };
    private readonly TextBox actualBox = new() { Width = 60, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox reporterBox = new() { Width = 86, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox descBox = new() { Width = 200 };
    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid grid = TaskUi.Grid(single: true);
    private readonly TextBlock adviceText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Text = "报修后此处给出滚动重排建议（备机顶替 / 欠产回摊 / 补车）。" };
    private bool _loaded;

    public EquipStatusWindow()
    {
        Title = "设备状态·故障报修 — 日常生产组织";
        TaskUi.Place(this, 1240, 620);

        var header = TaskUi.Header("设备状态·故障报修", "报修/复机真写 FaultEvent 并落盘 · 报修后给出滚动重排建议");
        subTitle = (TextBlock)((StackPanel)header.Child!).Children[1];

        // 工具条第一行：计划检修 / 故障类别 / 预估停机 / 实际停机 / 报修人 / 描述
        // 计划检修 ≠ 非计划故障：前者是排好的保养，不该计入设备可用率的扣分项。勾上之后停机记到「检修 h」，不进「故障 h」
        ToolTip.SetTip(plannedBox, "勾选＝按计划的保养/检修；不勾＝非计划故障。两者的停机分开记账，别混。");
        foreach (var s in new[] { "机械", "电气", "液压", "轮胎", "其它" }) catCombo.Items.Add(new ComboBoxItem { Content = s });
        ToolTip.SetTip(hoursBox, "报修时填。未复机期间的停机时长按它算，也是 0.5h 滚动重排门槛的判据——填错会真的动全盘计划，故不接受非数字。");
        ToolTip.SetTip(actualBox, "复机时填，留空则按报修至复机的真实经过时长计（跨重启仍然对）。补录历史停机时在此直接写实际时长。");
        reporterBox.Text = Environment.UserName;
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row1.Children.Add(plannedBox);
        row1.Children.Add(Lbl("故障类别")); row1.Children.Add(catCombo);
        row1.Children.Add(Lbl("预估停机 h")); row1.Children.Add(hoursBox);
        // 复机口径：默认按「报修 → 复机」的真实经过时长算，登记晚了/补录昨天的可在此改写
        row1.Children.Add(Lbl("实际停机 h")); row1.Children.Add(actualBox);
        row1.Children.Add(Lbl("报修人")); row1.Children.Add(reporterBox);
        row1.Children.Add(Lbl("故障描述")); row1.Children.Add(descBox);

        // 第二行：班次（只管显示口径；报修/复机落在此刻所在的班）+ 四钮 + 状态
        var row2 = new StackPanel { Orientation = Orientation.Horizontal };
        row2.Children.Add(Lbl("班次")); row2.Children.Add(shiftCombo);
        row2.Children.Add(TaskUi.Btn("报修选中", OnRepair, 94, bold: true));
        row2.Children.Add(TaskUi.Btn("开始维修", OnRepairing, 86));
        row2.Children.Add(TaskUi.Btn("复机", OnResume, 72));
        var refresh = TaskUi.Btn("刷新", OnRefresh, 66); refresh.Margin = new Thickness(0, 0, 14, 0);
        row2.Children.Add(refresh);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        row2.Children.Add(toolStatus);
        var toolPanel = new StackPanel(); toolPanel.Children.Add(row1); toolPanel.Children.Add(row2);

        grid.Columns.Add(TaskUi.TextCol("设备", nameof(Row.Equip), 110));
        grid.Columns.Add(TaskUi.TextCol("类别", nameof(Row.Category), 80));
        grid.Columns.Add(TaskUi.TextCol("状态", nameof(Row.Status), 80));
        // 三列都按选中的班裁（抬头写明是哪个班的哪段时间），不再是"今日"合计
        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("当班作业"), Binding = new Binding(nameof(Row.Current)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(TaskUi.TextCol("工时 h", nameof(Row.Hours), 76));
        grid.Columns.Add(TaskUi.TextCol("故障 h", nameof(Row.FaultHours), 80));
        // 格子里写计划轴口径的时长，挂钟口径（几号几点报的、几点修好的）进 ToolTip：两套时刻都要能查到，但塞进一格会互相干扰
        var faultCol = new DataGridTemplateColumn { Header = TaskUi.Head("故障/停机记录"), Width = new DataGridLength(1.9, DataGridLengthUnitType.Star) };
        faultCol.CellTemplate = new FuncDataTemplate<Row>((_, _) =>
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            TaskUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Body");
            tb.Bind(TextBlock.TextProperty, new Binding(nameof(Row.Fault)));
            tb.Bind(ToolTip.TipProperty, new Binding(nameof(Row.FaultAudit)));
            return tb;
        });
        grid.Columns.Add(faultCol);

        TaskUi.Theme(adviceText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var adviceBar = TaskUi.Bar(new ScrollViewer { Content = adviceText, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, top: false, padY: 6);
        adviceBar.MaxHeight = 170;

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var bar = TaskUi.Bar(toolPanel, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(grid, 2); Grid.SetRow(adviceBar, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(grid); g.Children.Add(adviceBar);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) Build(); };
        Opened += (_, _) =>
        {
            catCombo.SelectedIndex = 0;
            ShiftSelector.Bind(shiftCombo, includeAll: true);   // 默认当前班；「全部」留给日终复盘
            _loaded = true;
            Build();
        };
    }

    private static TextBlock Lbl(string t)
    {
        var l = TaskUi.Lbl(t); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 0);
        return l;
    }

    private double NowHour => SampleTaskBoard.NowHour;

    /// <summary>显示口径的班次（空 = 全天）。只裁显示，不影响报修/复机落在哪个班。</summary>
    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);

    private FaultCategory Category => (catCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
    {
        "机械" => FaultCategory.Mechanical,
        "电气" => FaultCategory.Electrical,
        "液压" => FaultCategory.Hydraulic,
        "轮胎" => FaultCategory.Tyre,
        _ => FaultCategory.Other,
    };

    private string Reporter => string.IsNullOrWhiteSpace(reporterBox.Text) ? Environment.UserName : reporterBox.Text!.Trim();

    /// <summary>
    /// 解析停机小时数。三态分得开：<b>空</b>=没填（true, null）、<b>填了个数</b>=（true, 值）、
    /// <b>填了但不是正数</b>=（false, null）。
    /// <para>
    /// 原先「预估停机 h」解析失败会静默取 1.0——手滑输个全角数字，就凭空落一条 1.0h 的故障记录，
    /// 并且真的越过 0.5h 的重排门槛去动全盘计划。停机时长是重排判据，不能猜。
    /// </para>
    /// </summary>
    private static bool TryHours(string? s, out double? hours)
    {
        hours = null;
        string v = (s ?? "").Trim();
        if (v.Length == 0) return true;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
         && !double.TryParse(v, NumberStyles.Float, CultureInfo.CurrentCulture, out x)) return false;
        if (x <= 0 || double.IsNaN(x) || double.IsInfinity(x)) return false;
        hours = x;
        return true;
    }

    // ── 装载 ─────────────────────────────────────────────────────────────────

    private void Build()
    {
        _rows.Clear();
        _faults = TaskPersistence.LoadFaults(_date);

        string shift = ShiftFilter;
        var win = ShiftScope.Window(shift);                     // null = 全天
        // 锚点：当前班取真实此刻，已过去的班取班末，还没到的班取班初——
        // 否则切到夜班时会拿上午十点去问"此刻在干啥"，整班显示成无任务。
        double anchor = ShiftScope.AnchorHour(ShiftSelector.Selected(shiftCombo));
        var tasks = SampleTaskBoard.Day().Where(t => shift.Length == 0 || t.Shift == shift).ToList();

        foreach (var r in SampleTaskBoard.Roster().Where(x => !x.Sub))
        {
            var his = tasks.Where(t => t.Group.MainEquipment == r.EquipId).OrderBy(t => t.StartHour).ToList();
            // 该班内包含锚点的那条；跨不到就取该班第一条（过去/未来班的"当班作业"仍要写得出来）
            var cur = his.FirstOrDefault(t => t.StartHour <= anchor && t.EndHour > anchor) ?? his.FirstOrDefault();
            double hrs = his.Sum(t => t.ActualHours);

            var mine = _faults.Where(f => string.Equals(f.EquipId, r.EquipId, StringComparison.OrdinalIgnoreCase)).ToList();
            var open = mine.FirstOrDefault(f => f.IsOpen);

            // 停机 h 按**与该班时窗的重叠**算，不是整天合计：一次跨班停机要摊到各班头上。
            // 计划检修与非计划故障分开记：混在一起会把"按计划保养"记成"设备不行"。
            double Span(FaultEvent f) => win == null
                ? f.DurationHours
                : f.OverlapHours(win.Start, win.End <= win.Start ? win.End + 24 : win.End);

            double faultH = mine.Where(f => !f.IsPlanned).Sum(Span);
            double maintH = mine.Where(f => f.IsPlanned).Sum(Span);

            // 状态是**该班时窗内**的状态：过去的班里修好了的故障，在那个班上仍应显示为故障
            bool faultyInShift = win == null ? open is { IsPlanned: false } : faultH > 1e-6;
            bool maintInShift = win == null ? open is { IsPlanned: true } : maintH > 1e-6;
            // 检修判定认**原因码**，不认文案。装箱时检修任务就带着 IncompleteReason.Maintenance
            //（TaskExploder 那儿写的），而 Material 只是个显示串——台账里把"检修"写成"计划检修"
            // 这一格就再也判不出检修了，且不会有任何报错。
            bool maint = maintInShift
                      || (cur != null && cur.Process == ProcessType.Idle
                          && cur.Reasons.Contains(IncompleteReason.Maintenance));
            string status = faultyInShift ? "故障"
                          : maint ? "检修"
                          : cur == null || cur.Process == ProcessType.Idle ? "空闲"
                          : "运行";

            _rows.Add(new Row
            {
                EquipId = r.EquipId,
                Equip = r.Display,
                Category = r.Category,
                Status = status,
                Current = cur == null ? "—" : cur.Process == ProcessType.Idle ? cur.Material : $"{cur.Process.Label()}·{cur.WorkZone}",
                Hours = hrs > 1e-6 ? $"{hrs:0.#}" : "",
                FaultHours = faultH > 1e-6 || maintH > 1e-6
                    ? (faultH > 1e-6 ? $"{faultH:0.#}" : "") + (maintH > 1e-6 ? $"（检修 {maintH:0.#}）" : "")
                    : "",
                Fault = mine.Count == 0 ? "" : string.Join("；", mine.OrderByDescending(f => f.StartHour).Take(2).Select(f => f.Caption)),
                FaultAudit = mine.Count == 0 ? "" : string.Join(Environment.NewLine,
                    mine.OrderByDescending(f => f.StartHour).Select(f => f.AuditCaption)),
            });
        }

        grid.ItemsSource = null;
        grid.ItemsSource = _rows.ToList();

        int faulty = _rows.Count(x => x.Status == "故障");
        int openNow = _faults.Count(f => f.IsOpen);
        toolStatus.Text = $"{(shift.Length == 0 ? "全天" : ShiftScope.Caption(shift))}："
                        + $"{_rows.Count} 台主设备 · 本班故障 {faulty} 台 · 当前未复机 {openNow} 台 · 本日故障记录 {_faults.Count} 条";
        subTitle.Text = $"{_date} · 此刻 {DispatchClock.Hm(NowHour)}（属{ShiftScope.ShiftOf(NowHour)}）"
                      + $" · 显示口径 {(shift.Length == 0 ? "全天" : ShiftScope.Caption(shift))}"
                      + $" · 故障记录落盘 {TaskPersistence.FaultDir()}";
    }

    private void OnRefresh()
    {
        Build();
        adviceText.Text = "已重新读取设备状态与故障记录。";
    }

    private Row? Selected => grid.SelectedItem as Row;

    // ── 报修 / 维修 / 复机 ───────────────────────────────────────────────────

    private void OnRepair()
    {
        if (Selected is not { } r) { adviceText.Text = "请先选一台设备再报修。"; return; }

        var exist = _faults.FirstOrDefault(f => string.Equals(f.EquipId, r.EquipId, StringComparison.OrdinalIgnoreCase) && f.IsOpen);
        if (exist != null)
        {
            adviceText.Text = $"{r.EquipId} 已有未复机的故障记录：{exist.Caption}。请先「复机」再重新报修。";
            return;
        }

        if (!TryHours(hoursBox.Text, out double? estOpt) || estOpt == null)
        {
            adviceText.Text = $"「预估停机 h」填的是「{hoursBox.Text}」——请填一个大于 0 的数字。"
                            + Environment.NewLine
                            + "它是未复机期间的停机时长口径，也是 0.5h 滚动重排门槛的判据，不接受空值与非数字（原先会静默按 1.0h 记）。";
            return;
        }

        double now = NowHour;
        double est = estOpt.Value;
        var ev = new FaultEvent
        {
            EquipId = r.EquipId,
            PlanDate = _date,
            // 报修永远落在**此刻所在的班**（按当日班制判），与上方的显示筛选无关：
            // 在夜班视图里点报修，故障也还是发生在此刻这个班。
            Shift = ShiftScope.ShiftOf(now),
            StartHour = Math.Round(now, 2),
            EndHour = Math.Round(now, 2),
            EstimatedHours = est,
            Category = Category,
            IsPlanned = plannedBox.IsChecked == true,
            Description = (descBox.Text ?? "").Trim(),
            Reporter = Reporter,
            Status = FaultStatus.Reported,
        };

        bool ok = TaskPersistence.AppendFault(_date, ev);
        Build();

        var lines = new List<string>
        {
            (ok ? "✓ 已登记故障记录" : "⚠ 故障记录写盘失败：" + TaskPersistence.LastIoLabel)
                + $"：{r.EquipId} {ev.Caption}",
        };
        lines.Add(Advice(r.EquipId, est));
        adviceText.Text = string.Join(Environment.NewLine, lines);
        toolStatus.Text = $"{r.EquipId} 已报修（{Category.Label()}·预估 {est:0.#}h）";
    }

    private void OnRepairing()
    {
        if (Selected is not { } r) { adviceText.Text = "请先选一台设备。"; return; }
        var open = _faults.FirstOrDefault(f => string.Equals(f.EquipId, r.EquipId, StringComparison.OrdinalIgnoreCase) && f.IsOpen);
        if (open == null) { adviceText.Text = $"{r.EquipId} 无未复机的故障记录，无法转「维修中」。"; return; }

        // 两条轴各记一个时刻：计划轴用于与任务时段对齐，挂钟用于算响应时长（报修→开修）
        open.BeginRepair(NowHour, DateTime.Now);
        bool ok = TaskPersistence.AppendFault(_date, open);
        Build();
        adviceText.Text = (ok ? "✓ " : "⚠ 写盘失败 ") + $"{r.EquipId} 转「维修中」：{open.Caption}"
                        + (open.ResponseHours is { } rh ? $"　响应 {rh:0.##} h（报修→开修）" : "");
    }

    private void OnResume()
    {
        if (Selected is not { } r) { adviceText.Text = "请先选一台设备。"; return; }
        var open = _faults.FirstOrDefault(f => string.Equals(f.EquipId, r.EquipId, StringComparison.OrdinalIgnoreCase) && f.IsOpen);
        if (open == null) { adviceText.Text = $"{r.EquipId} 无未复机的故障记录。"; return; }

        if (!TryHours(actualBox.Text, out double? typed))
        {
            adviceText.Text = $"「实际停机 h」填的是「{actualBox.Text}」——请填一个大于 0 的数字，或留空按实际经过时长计。";
            return;
        }

        // 停机时长的口径全在 FaultEvent.Resume 里（那儿讲了为什么不能用"计划时钟此刻 − StartHour"）
        double dur = open.Resume(DateTime.Now, typed);
        string basis = typed.HasValue ? "按填写的实际停机" : "按报修至复机的经过时长";

        bool ok = TaskPersistence.AppendFault(_date, open);
        Build();
        actualBox.Text = "";   // 一次性输入，别让它粘到下一台设备的复机上

        adviceText.Text = (ok ? "✓ " : "⚠ 写盘失败 ")
            + $"{r.EquipId} 已复机，实际停机 {open.DurationHours:0.##} h"
            + $"（{DispatchClock.Hm(open.StartHour)}–{DispatchClock.Hm(open.EndHour)} · {basis}）。"
            + (dur <= 1e-6 ? Environment.NewLine + "⚠ 停机时长为 0：报修后随即复机。若是补录历史停机，请在「实际停机 h」里填真实时长后重报。" : "")
            + Environment.NewLine + "实绩录入窗点「重算故障工时」即可把这段停机计入本班故障 h。";
        toolStatus.Text = $"{r.EquipId} 已复机 · 停机 {open.DurationHours:0.##} h";
    }

    // ── 重排建议 ─────────────────────────────────────────────────────────────

    /// <summary>报修后调 DispatchEngine.OnFault：判定是否触发重排，并把建议摊开给调度员看。</summary>
    private string Advice(string equipId, double hours)
    {
        try
        {
            var cfg = SampleTaskBoard.Config();
            var tasks = SampleTaskBoard.Day();
            var adv = DispatchEngine.OnFault(equipId, hours, cfg, tasks, NowHour);
            return "调度建议 · " + adv.Summary;
        }
        catch (Exception ex)
        {
            return $"调度建议：重排引擎不可用（{ex.Message}），请在「生产任务动态调整」手动重排。";
        }
    }

    // 「此刻是第几班」已收归 ShiftScope.ShiftOf（按当日班制判）——
    // 原先这里私藏一个把 8 / 16 写死的版本，班次日历一改交接时刻就与任务的 Shift 对不上号。
}
