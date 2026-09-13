// 忠实移植自原 PitMine3D Modules/TaskLib/Features/TaskDispatchWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 任务下达：把任务书数字派单至设备，并留下**可追溯的单据**。
///
/// <para>本窗口是"计划 → 执行"的闸门，三件事必须为真：</para>
/// <list type="number">
/// <item><b>下达前校验</b>：走 <see cref="DispatchEngine.ValidateForIssue"/>——有 Error 级校核、缺去向、缺主设备一律不得下达。</item>
/// <item><b>落盘</b>：下达即生成 <see cref="TaskInstance"/>（带稳定键与版本）与 <see cref="DispatchReceipt"/> 回执，写 JSON。</item>
/// <item><b>可撤回</b>：撤回也是一条回执，不是把记录抹掉——单据流水只增不改。</item>
/// </list>
/// <para>稳定键（<see cref="TaskKey"/>）取「日期+班次+主设备+工序+作业面」，编制参数漂移后仍对得上号。</para>
/// </summary>
public sealed class TaskDispatchWindow : Window
{
    public sealed class Row
    {
        public string Shift { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string Group { get; set; } = "";
        public string Place { get; set; } = "";
        public string Destination { get; set; } = "";
        public string Process { get; set; } = "";
        /// <summary>计划量（<b>按工序取自己那本账</b>，见 <see cref="TaskQuantity"/>）。</summary>
        public string Plan { get; set; } = "";
        /// <summary>量口径名（控制方量 / 原位实方 / 承运量 / 排弃占容；爆破为"—"）。</summary>
        public string Basis { get; set; } = "";
        public string Span { get; set; } = "";
        public string State { get; set; } = "待下达";

        /// <summary>对应的计划任务（同一份内存对象，改状态即改台账）。</summary>
        public ProductionTask Task { get; init; } = new();
        /// <summary>稳定键 —— 与已落盘实例对号的真主键。</summary>
        public string Key { get; init; } = "";
        public bool Issued { get; set; }

        /// <summary>能不能**单独签发**。假 = 运输笔（随源采装笔连带）。</summary>
        public bool SelfIssuable { get; init; } = true;
    }

    private readonly string _date = SampleTaskBoard.DateLabel;
    private readonly List<Row> _all = new();
    /// <summary>稳定键 → 已落盘的任务实例。</summary>
    private readonly Dictionary<string, TaskInstance> _inst = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBlock subTitle;
    private readonly TextBlock storeHint = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private readonly TextBox issuerBox = new() { Width = 110, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox stateCombo = new() { Width = 100, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid grid = TaskUi.Grid(single: false);
    private readonly TextBlock receiptTitle = new() { Text = "单据流水", FontWeight = FontWeight.Bold };
    private readonly TextBlock receiptHint = new() { FontSize = 11, Margin = new Thickness(10, 4, 10, 8), TextWrapping = TextWrapping.Wrap, Text = "选中左侧任务只看它的流水；不选看本班全部。流水只增不改。" };
    private readonly ListBox receiptList = new() { Margin = new Thickness(6), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock checkText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private bool _loaded;

    public TaskDispatchWindow()
    {
        Title = "任务下达 — 日常生产组织";
        TaskUi.Place(this, 1320, 720);

        var header = TaskUi.Header("任务下达", "下达前校验（Error 校核 / 缺去向 / 缺主设备）→ 生成任务实例 + 回执并落盘 · 可撤回　｜　五道工序全在表里：运输随采装连带、爆破不派设备");
        subTitle = (TextBlock)((StackPanel)header.Child!).Children[1];

        var row1 = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        void L1(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); row1.Children.Add(c); }
        L1(TaskUi.Btn("下达全部（当前筛选）", OnDispatchAll, 150, bold: true));
        L1(TaskUi.Btn("下达选中", OnDispatchSel, 86));
        L1(TaskUi.Btn("确认回执", OnAck, 86));
        L1(TaskUi.Btn("撤回下达", OnWithdraw, 86));
        L1(TaskUi.Btn("校验", OnValidate, 66));
        L1(TaskUi.Btn("刷新", OnRefresh, 66));
        TaskUi.Theme(storeHint, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        DockPanel.SetDock(storeHint, Avalonia.Controls.Dock.Right); row1.Children.Add(storeHint);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal };
        row2.Children.Add(Lbl("下达人")); issuerBox.Text = Environment.UserName; row2.Children.Add(issuerBox);
        // 取值域按当日班制、默认落当前班；「全部」保留：班前会要一次下达全天
        row2.Children.Add(Lbl("班次")); row2.Children.Add(shiftCombo);
        row2.Children.Add(Lbl("下达状态"));
        foreach (var s in new[] { "全部", "未下达", "已下达" }) stateCombo.Items.Add(new ComboBoxItem { Content = s });
        row2.Children.Add(stateCombo);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        row2.Children.Add(toolStatus);
        var toolPanel = new StackPanel(); toolPanel.Children.Add(row1); toolPanel.Children.Add(row2);

        grid.Columns.Add(TaskUi.TextCol("班次", nameof(Row.Shift), 56));
        grid.Columns.Add(TaskUi.TextCol("任务号", nameof(Row.TaskId), 110));
        grid.Columns.Add(Star("设备编组", nameof(Row.Group), 1.8));
        grid.Columns.Add(Star("作业地点", nameof(Row.Place), 1));
        // 下达时调度员最需要确认的就是「这车拉到哪」：去向与运距必须在下达界面上可见
        grid.Columns.Add(Star("去向 · 运距", nameof(Row.Destination), 1.4));
        grid.Columns.Add(TaskUi.TextCol("工序", nameof(Row.Process), 50));
        // 计划量按工序取自己那本账；量口径单列
        grid.Columns.Add(TaskUi.TextCol("计划量", nameof(Row.Plan), 86));
        grid.Columns.Add(TaskUi.TextCol("量口径", nameof(Row.Basis), 66));
        grid.Columns.Add(TaskUi.TextCol("时段", nameof(Row.Span), 92));
        grid.Columns.Add(Star("下达状态", nameof(Row.State), 1.5));
        grid.SelectionChanged += (_, _) => { if (_loaded) RefreshReceipts(); };

        // 单据流水（回执台账）：回执是纯追加流水，正是"这件事到底发生过没有"的唯一根据
        var rDock = new DockPanel();
        var rHead = new DockPanel { LastChildFill = false, Margin = new Thickness(10, 8, 10, 4) };
        TaskUi.Theme(receiptTitle, TextBlock.ForegroundProperty, "Theme.Text.Body");
        DockPanel.SetDock(receiptTitle, Avalonia.Controls.Dock.Left); rHead.Children.Add(receiptTitle);
        var exp = TaskUi.Small("导出", OnExportReceipts); exp.Margin = new Thickness(0);
        ToolTip.SetTip(exp, "把本日回执流水导出成文本，交接班/追溯时贴给别人看");
        DockPanel.SetDock(exp, Avalonia.Controls.Dock.Right); rHead.Children.Add(exp);
        DockPanel.SetDock(rHead, Avalonia.Controls.Dock.Top); rDock.Children.Add(rHead);
        TaskUi.Theme(receiptHint, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        DockPanel.SetDock(receiptHint, Avalonia.Controls.Dock.Bottom); rDock.Children.Add(receiptHint);
        receiptList.ItemTemplate = new FuncDataTemplate<ReceiptRow>((_, _) =>
        {
            var sp = new StackPanel { Margin = new Thickness(2, 3, 2, 4) };
            var h = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            h.Bind(TextBlock.TextProperty, new Binding(nameof(ReceiptRow.Head)));
            TaskUi.Theme(h, TextBlock.ForegroundProperty, "Theme.Text.Body");
            var b = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
            b.Bind(TextBlock.TextProperty, new Binding(nameof(ReceiptRow.Body)));
            TaskUi.Theme(b, TextBlock.ForegroundProperty, "Theme.Text.Muted");
            sp.Children.Add(h); sp.Children.Add(b);
            return sp;
        });
        rDock.Children.Add(receiptList);
        var rBox = new Border { Margin = new Thickness(0, 10, 10, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = rDock };
        TaskUi.Theme(rBox, Border.BorderBrushProperty, "Theme.Surface.Border");

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        Grid.SetColumn(grid, 0); Grid.SetColumn(rBox, 1);
        body.Children.Add(grid); body.Children.Add(rBox);

        TaskUi.Theme(checkText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = TaskUi.Bar(checkText, top: false, padY: 6);

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var bar = TaskUi.Bar(toolPanel, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(body, 2); Grid.SetRow(foot, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(body); g.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        subTitle.Text = $"{_date} · 校验 → 下达 → 回执落盘 · {SampleTaskBoard.SourceLabel}";
        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) ApplyFilter(); };
        stateCombo.SelectionChanged += (_, _) => { if (_loaded) ApplyFilter(); };
        Opened += (_, _) =>
        {
            ShiftSelector.Bind(shiftCombo, includeAll: true);
            stateCombo.SelectedIndex = 0;
            _loaded = true;
            BuildRows();
            ApplyFilter();
        };
    }

    private static TextBlock Lbl(string t)
    {
        var l = TaskUi.Lbl(t); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 0);
        return l;
    }

    private static DataGridTextColumn Star(string header, string path, double star)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path), Width = new DataGridLength(star, DataGridLengthUnitType.Star) };

    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);
    private string StateFilter => Text(stateCombo);
    private string Issuer => string.IsNullOrWhiteSpace(issuerBox.Text) ? Environment.UserName : issuerBox.Text!.Trim();

    private static string Text(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    // ── 装载 ─────────────────────────────────────────────────────────────────

    /// <summary>重建行：计划任务 × 已落盘实例。</summary>
    private void BuildRows()
    {
        _all.Clear();
        _inst.Clear();

        // 已落盘实例：按日期读全部班次，键是稳定键，故重排后仍能对上号
        foreach (var it in TaskPersistence.LoadInstancesOfDay(_date))
            if (!string.IsNullOrWhiteSpace(it.StableKey)) _inst[it.StableKey] = it;

        var day = SampleTaskBoard.Day();
        foreach (var t in day.Where(t => t.Process != ProcessType.Idle).OrderBy(t => t.StartHour))
        {
            string key = TaskKey.Of(t, _date);
            _inst.TryGetValue(key, out var inst);
            var q = TaskQuantity.Of(t);

            // 单据状态**不在这里打** —— 由 DispatchStateLink 在盘子装配时统一回灌，本窗只读。
            _all.Add(new Row
            {
                Task = t,
                Key = key,
                Shift = t.Shift,
                TaskId = t.Id,
                Group = GroupText(t),
                Place = t.LocationCaption,
                Destination = DestText(t),
                Process = t.Process.Label(),
                Plan = q.HasValue ? $"{q.Value:N0} {q.Unit}" : "—",
                Basis = q.Basis.Length > 0 ? q.Basis : "—",
                Span = $"{DispatchClock.Hm(t.StartHour)}–{DispatchClock.Hm(t.EndHour)}",
                Issued = t.IsIssued,
                SelfIssuable = DispatchStateLink.IsSelfIssuable(t),
                // 运输笔的状态由源采装笔说了算（DL9）
                State = t.Process == ProcessType.Haul
                    ? DispatchStateLink.FollowerCaption(t, day)
                    : StateText(inst),
            });
        }

        storeHint.Text = $"单据落盘：{TaskPersistence.InstanceDir()}";
    }

    private static string GroupText(ProductionTask t)
        => t.Process == ProcessType.Blast && string.IsNullOrWhiteSpace(t.Group.MainEquipment)
            ? "不派设备（清场）"
            : t.Group.Caption;

    /// <summary>去向文案：缺去向的行明着写"⚠ 未指定卸点"，因为它会阻止下达。</summary>
    private static string DestText(ProductionTask t)
    {
        if (t.HasDestination) return t.DestinationCaption;
        return PitMine3D.Kylin.TaskLib.Gantt.DailyGanttModel.NeedsDestination(t) ? "⚠ 未指定卸点" : "—";
    }

    private static string StateText(TaskInstance? inst)
        => inst == null ? "待下达"
         : !inst.IsIssued ? (inst.WasIssued
             ? $"待下达（v{inst.Version} {inst.IssuedBy} {inst.IssuedAt:HH:mm} 下达 → {inst.WithdrawnBy} {inst.WithdrawnAt:HH:mm} 撤回）"
             : "待下达")
         : inst.AckedAt.HasValue
             ? $"✓ 已下达 v{inst.Version}（{inst.IssuedBy} {inst.IssuedAt:HH:mm}）· 已确认（{inst.AckedBy} {inst.AckedAt:HH:mm}）"
             : $"✓ 已下达 v{inst.Version}（{inst.IssuedBy} {inst.IssuedAt:HH:mm}）· 待确认";

    private void ApplyFilter()
    {
        var view = _all
            .Where(r => ShiftFilter.Length == 0 || r.Shift == ShiftFilter)
            .Where(r => StateFilter switch { "未下达" => !r.Issued, "已下达" => r.Issued, _ => true })
            .ToList();
        grid.ItemsSource = null;
        grid.ItemsSource = view;

        int issued = _all.Count(r => r.Issued);
        int follow = _all.Count(r => !r.SelfIssuable);
        toolStatus.Text = $"共 {_all.Count} 项 · 已下达 {issued} · 未下达 {_all.Count - issued}"
                        + (follow > 0 ? $"（其中 {follow} 项运输随采装连带，不单独签发）" : "")
                        + (view.Count != _all.Count ? $"　当前筛选 {view.Count} 项" : "");

        RefreshReceipts();   // 筛选/下达/确认/撤回之后，右侧流水跟着走
    }

    private void OnRefresh()
    {
        BuildRows();
        ApplyFilter();
        checkText.Text = "已重新读取计划与落盘单据。";
    }

    // ── 校验 ─────────────────────────────────────────────────────────────────

    private void OnValidate()
    {
        var chk = Validate(null);
        checkText.Text = chk.CanIssue
            ? chk.Summary + (chk.Warnings.Count > 0 ? Environment.NewLine + chk.WarnText : "")
            : chk.Summary + Environment.NewLine + chk.BlockText;
    }

    private IssueCheck Validate(IReadOnlyList<Row>? rows)
    {
        ExploderResult res;
        try { res = SampleTaskBoard.Result(); }
        catch (Exception ex)
        {
            var bad = new IssueCheck();
            bad.Blocks.Add(new PlanViolation { Severity = ViolationSeverity.Error, Code = ViolationCodes.DispatchIssued, Message = "计划引擎不可用：" + ex.Message });
            return bad;
        }
        return DispatchEngine.ValidateForIssue(res, ShiftFilter.Length == 0 ? null : ShiftFilter,
                                               rows?.Select(r => r.TaskId));
    }

    // ── 下达 ─────────────────────────────────────────────────────────────────

    private void OnDispatchAll()
        => Issue(VisibleRows().Where(r => !r.Issued && r.SelfIssuable).ToList(), "下达全部（当前筛选）");

    private void OnDispatchSel()
    {
        var all = grid.SelectedItems.OfType<Row>().ToList();
        var sel = all.Where(r => !r.Issued && r.SelfIssuable).ToList();
        if (sel.Count == 0)
        {
            int follow = all.Count(r => !r.SelfIssuable);
            checkText.Text = follow > 0 && follow == all.Count
                ? $"选中的 {follow} 项都是**运输笔**：运输随源采装笔连带下达（口径 2026-08-20 定），"
                + "不单独签发。请改选对应的采装任务 —— 签发它就等于签发了这几趟运输。"
                : "未选中「未下达」的任务。";
            return;
        }
        Issue(sel, "下达选中");
    }

    private List<Row> VisibleRows() => (grid.ItemsSource as IEnumerable<Row>)?.ToList() ?? _all;

    /// <summary>下达：校验 → 生成实例（版本 +1）→ 写回执 → 落盘 → 回写任务状态。</summary>
    private async void Issue(List<Row> rows, string action)
    {
        if (rows.Count == 0) { checkText.Text = $"{action}：没有待下达的任务。"; return; }

        var chk = Validate(rows);
        if (!chk.CanIssue)
        {
            checkText.Text = chk.Summary + Environment.NewLine + chk.BlockText;
            await TaskUi.Info(this, "下达前校验未通过",
                $"以下 {chk.Blocks.Count} 条阻止项未消除，任务不得下达：{Environment.NewLine}{Environment.NewLine}{chk.BlockText}"
                + $"{Environment.NewLine}{Environment.NewLine}（缺去向的任务尤其不能下达——任务书上写不出「这车拉到哪」。）");
            return;
        }

        string by = Issuer;
        var now = DateTime.Now;
        var byShift = new Dictionary<string, List<TaskInstance>>(StringComparer.Ordinal);
        var receipts = new List<DispatchReceipt>();

        foreach (var r in rows)
        {
            int ver = _inst.TryGetValue(r.Key, out var old) ? old.Version + 1 : 1;
            var inst = TaskInstance.From(r.Task, _date, ver);
            inst.IssuedBy = by;
            inst.IssuedAt = now;
            inst.Status = TaskStatus.Dispatched;
            inst.Notes = r.Task.HasDestination ? $"去向 {r.Task.DestinationCaption}" : "无去向";
            if (old != null) { inst.AckedBy = ""; inst.AckedAt = null; }   // 重新下达 ⇒ 确认作废

            _inst[r.Key] = inst;
            r.State = StateText(inst);

            Bucket(byShift, inst.Shift).Add(inst);
            receipts.Add(DispatchReceipt.For(inst, ReceiptKind.Issue, by,
                $"{r.Task.Process.Label()}·{r.Task.WorkZone} {r.Task.TargetVolumeM3:0}m³ → {(r.Task.HasDestination ? r.Task.DestinationCaption : "无去向")}"));
        }

        bool ok = Persist(byShift, receipts);

        // ★ 落盘之后立刻重跑单据回灌：不刷新的话刚签发的这几条要等下一次整盘重排才进得去
        ProductionPlanContext.RefreshDispatch();
        RefreshRowStates();
        ApplyFilter();

        // 连带下达了几趟运输 —— 单据上必须写出来
        var issuedIds = rows.Select(r => r.Task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int carried = _all.Count(r => !r.SelfIssuable && r.Task.IsIssued
                                   && issuedIds.Contains(r.Task.SourceTaskId ?? ""));
        var sum = TaskQuantity.Sum(rows.Select(r => r.Task));

        checkText.Text = $"{action}：已下达 {rows.Count} 项 · 下达人 {by} · 回执 {receipts.Count} 条"
                       + (carried > 0 ? $" · **连带 {carried} 笔运输**（随采装，不另建单据）" : "")
                       + Environment.NewLine + "本批量：" + sum.Caption
                       + Environment.NewLine
                       + (ok ? $"已落盘 {TaskPersistence.InstanceDir()}" : $"⚠ 落盘失败：{TaskPersistence.LastIoLabel}")
                       + (chk.Warnings.Count > 0 ? Environment.NewLine + "提醒：" + chk.WarnText : "");
    }

    // ── 确认 / 撤回 ──────────────────────────────────────────────────────────

    /// <summary>回灌之后把行状态重新读一遍。**运输笔必须重算**：它的状态跟着源采装笔走。</summary>
    private void RefreshRowStates()
    {
        var day = _all.Select(r => r.Task).ToList();
        foreach (var r in _all)
        {
            r.Issued = r.Task.IsIssued;
            if (r.Task.Process == ProcessType.Haul)
                r.State = DispatchStateLink.FollowerCaption(r.Task, day);
            else if (_inst.TryGetValue(r.Key, out var inst)) r.State = StateText(inst);
        }
    }

    private void OnAck()
    {
        var sel = grid.SelectedItems.OfType<Row>().Where(r => r.Issued && r.SelfIssuable).ToList();
        if (sel.Count == 0) { checkText.Text = "请先选中已下达的任务再确认回执（运输笔随采装走，没有自己的回执）。"; return; }

        string by = Issuer;
        var byShift = new Dictionary<string, List<TaskInstance>>(StringComparer.Ordinal);
        var receipts = new List<DispatchReceipt>();

        foreach (var r in sel)
        {
            if (!_inst.TryGetValue(r.Key, out var inst)) continue;
            inst.AckedBy = by;
            inst.AckedAt = DateTime.Now;
            r.State = StateText(inst);
            Bucket(byShift, inst.Shift).Add(inst);
            receipts.Add(DispatchReceipt.For(inst, ReceiptKind.Ack, by, "班组已接收任务"));
        }

        bool ok = Persist(byShift, receipts);
        ApplyFilter();
        checkText.Text = $"已确认 {receipts.Count} 项（确认人 {by}）· " + (ok ? "回执已落盘" : $"⚠ 落盘失败：{TaskPersistence.LastIoLabel}");
    }

    private async void OnWithdraw()
    {
        var sel = grid.SelectedItems.OfType<Row>().Where(r => r.Issued && r.SelfIssuable).ToList();
        if (sel.Count == 0) { checkText.Text = "请先选中已下达的任务再撤回（运输笔随采装走，撤采装即撤运输）。"; return; }

        int willCarry = _all.Count(r => !r.SelfIssuable && r.Task.IsIssued
                                     && sel.Any(s => string.Equals(s.Task.Id, r.Task.SourceTaskId, StringComparison.OrdinalIgnoreCase)));

        if (!await TaskUi.Confirm(this, "撤回下达", $"撤回选中的 {sel.Count} 项任务下达？"
                            + (willCarry > 0 ? $"（连带撤回 {willCarry} 笔运输）" : "")
                            + $"{Environment.NewLine}撤回会写一条撤回回执，历史下达记录不会被抹掉。")) return;

        string by = Issuer;
        var byShift = new Dictionary<string, List<TaskInstance>>(StringComparer.Ordinal);
        var receipts = new List<DispatchReceipt>();

        foreach (var r in sel)
        {
            if (!_inst.TryGetValue(r.Key, out var inst)) continue;
            receipts.Add(DispatchReceipt.For(inst, ReceiptKind.Withdraw, by,
                $"撤回 v{inst.Version} 下达（原 {inst.IssuedBy} {inst.IssuedAt:MM-dd HH:mm} 下达）"));

            // 撤回留痕不抹痕：下达时刻原样保留，见 TaskInstance.Withdraw
            inst.Withdraw(by, DateTime.Now);
            r.State = StateText(inst);
            Bucket(byShift, inst.Shift).Add(inst);
        }

        bool ok = Persist(byShift, receipts);
        ProductionPlanContext.RefreshDispatch();
        RefreshRowStates();
        ApplyFilter();
        checkText.Text = $"已撤回 {receipts.Count} 项（操作人 {by}）"
                       + (willCarry > 0 ? $" · 连带撤回 {willCarry} 笔运输" : "")
                       + " · " + (ok ? "撤回回执已落盘" : $"⚠ 落盘失败：{TaskPersistence.LastIoLabel}");
    }

    // ── 单据流水（回执台账）────────────────────────────────────────────────────

    /// <summary>流水条目：抬头一行、正文一行。</summary>
    public sealed class ReceiptRow
    {
        public string Head { get; set; } = "";
        public string Body { get; set; } = "";
    }

    /// <summary>重刷右侧流水。选中任务 ⇒ 只看它的；不选 ⇒ 看当前班次筛选下的全部。读不出来时明说。</summary>
    private void RefreshReceipts()
    {
        List<DispatchReceipt> all;
        try { all = TaskPersistence.LoadReceipts(_date); }
        catch (Exception ex)
        {
            receiptList.ItemsSource = null;
            receiptTitle.Text = "单据流水（读不出来）";
            receiptHint.Text = $"⚠ {ex.Message}";
            return;
        }

        var keys = grid.SelectedItems.OfType<Row>().Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string shift = ShiftFilter;

        var view = all
            .Where(r => keys.Count > 0
                        ? keys.Contains(r.StableKey)
                        : shift.Length == 0 || string.Equals(r.Shift, shift, StringComparison.Ordinal))
            .OrderByDescending(r => r.At)
            .ToList();

        receiptList.ItemsSource = view.Select(r => new ReceiptRow
        {
            Head = $"{r.At:MM-dd HH:mm}　{r.Kind.Label()}　{r.By}",
            Body = (string.IsNullOrWhiteSpace(r.TaskId) ? "" : r.TaskId + "　") + r.Message,
        }).ToList();

        string scope = keys.Count > 0 ? $"选中 {keys.Count} 项任务"
                     : shift.Length == 0 ? "全天" : shift;
        receiptTitle.Text = $"单据流水 · {scope}（{view.Count}）";
        receiptHint.Text = view.Count == 0
            ? (all.Count == 0
                ? $"本日尚无回执。下达 / 确认 / 撤回都会在这里留痕 → {TaskPersistence.ReceiptDir()}"
                : $"该范围内无回执（本日共 {all.Count} 条，换个班次或取消选中看全部）。")
            : "流水只增不改：撤回也是新增一条，不会把下达记录抹掉。";
    }

    /// <summary>把当前流水导出成一份文本（交接班/追溯时贴给别人看）。</summary>
    private void OnExportReceipts()
    {
        var rows = (receiptList.ItemsSource as IEnumerable<ReceiptRow>)?.ToList() ?? new List<ReceiptRow>();
        if (rows.Count == 0) { receiptHint.Text = "当前范围没有可导出的回执。"; return; }

        string path = System.IO.Path.Combine(TaskPersistence.ReceiptDir(),
                                             $"流水_{TaskPersistence.DateKey(_date)}.txt");
        try
        {
            System.IO.File.WriteAllText(path,
                $"{SampleTaskBoard.MineName}　{_date}　单据流水（{rows.Count} 条）" + Environment.NewLine
                + new string('-', 60) + Environment.NewLine
                + string.Join(Environment.NewLine, rows.Select(r => r.Head + Environment.NewLine + "    " + r.Body)));
            receiptHint.Text = $"✓ 已导出 {rows.Count} 条 → {path}";
        }
        catch (Exception ex) { receiptHint.Text = $"⚠ 导出失败：{ex.Message}"; }
    }

    // ── 落盘 ─────────────────────────────────────────────────────────────────

    private static List<TaskInstance> Bucket(Dictionary<string, List<TaskInstance>> map, string shift)
    {
        if (!map.TryGetValue(shift, out var list)) map[shift] = list = new List<TaskInstance>();
        return list;
    }

    private bool Persist(Dictionary<string, List<TaskInstance>> byShift, List<DispatchReceipt> receipts)
    {
        bool ok = true;
        foreach (var kv in byShift) ok &= TaskPersistence.AppendInstances(_date, kv.Key, kv.Value);
        if (receipts.Count > 0) ok &= TaskPersistence.AppendReceipts(_date, receipts);
        return ok;
    }
}
