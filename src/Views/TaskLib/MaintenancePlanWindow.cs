// 忠实移植自原 PitMine3D Modules/TaskLib/Features/MaintenancePlanWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
// （PitMine3D.Kylin.Data 不整体引入：与 TaskLib.Engine 里 WorkCalendar/ShiftWindow/ProductionPlanContext 同名的旧切片会串）
using PitMine3D.Kylin.Data.Entities;     // MaintenanceWindowPlan
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;
using WorkCalendar = PitMine3D.Kylin.TaskLib.Engine.WorkCalendar;   // 与 Kylin 旧切片 Data.WorkCalendar 消歧
using ShiftWindow = PitMine3D.Kylin.TaskLib.Engine.ShiftWindow;
using ProductionPlanContext = PitMine3D.Kylin.TaskLib.Engine.ProductionPlanContext;
using EquipmentDataContext = PitMine3D.Kylin.Data.EquipmentDataContext;

/// <summary>
/// 检修档期：设备当日检修时窗的录入口。
///
/// <para>
/// <b>它补的是装箱那条减法里唯一没有数据源的一项</b>：
/// 有效时窗 = 班起 − <b>检修</b> − 爆破清场 − 交接班损失。V042 建了 <c>maintenance_window</c>，本窗口是它的录入口。
/// </para>
/// <para>
/// <b>与「设备状态·故障报修」的分工</b>：那边记的是<b>已经发生</b>的故障（FaultEvent），这边是<b>计划中</b>的检修档期。
/// </para>
/// <para>
/// <b>跨零点必须拆两条</b>（当日 22:00–24:00 + 次日 00:00–02:00）：装箱的时窗是同一天内的 [start,end)。界面在录入时就挡住。
/// </para>
/// </summary>
public sealed class MaintenancePlanWindow : Window
{
    public sealed class Row
    {
        public string Equip { get; set; } = "";
        public string Date { get; set; } = "";
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
        public string Hours { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Shifts { get; set; } = "";
        public string Note { get; set; } = "";
        public MaintenanceWindowPlan? Entity;
    }

    private DateTime _date;

    // 设备下拉：色块图标 + 类别 + 编号 + 型号（可直接输入编号 —— 原 WPF 可编辑 ComboBox，Avalonia 用 AutoCompleteBox）
    private readonly AutoCompleteBox equipCombo = new() { Width = 238, Margin = new Thickness(0, 0, 12, 0), FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 0, MinimumPopulateDelay = TimeSpan.Zero };
    private readonly TextBox startBox = new() { Width = 66, Margin = new Thickness(0, 0, 12, 0), Text = "08:00" };
    private readonly TextBox endBox = new() { Width = 66, Margin = new Thickness(0, 0, 12, 0), Text = "12:00" };
    private readonly ComboBox kindCombo = new() { Width = 80, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox noteBox = new() { MinWidth = 90 };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock hintLine = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly DataGrid grid = TaskUi.Grid(single: true);

    /// <summary>上一轮装载有没有真的读到库。false = 还在启动中/库不可用，窗口重新激活时会自己重试。</summary>
    private bool _loadedOk;

    public MaintenancePlanWindow()
    {
        Title = "检修档期 — 日常生产组织";
        TaskUi.Place(this, 1100, 600);

        var header = TaskUi.Header("检修档期", "设备当日检修时窗 —— 装箱的有效时窗要扣它（班起 − 检修 − 爆破清场 − 交接班）");

        // 第一行：按钮靠右钉死、备注吃剩余宽度（原版注释：全 Dock=Left 时最后那个按钮会被挤出窗外）
        var row1 = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        void L1(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); row1.Children.Add(c); }
        L1(Lbl("设备"));
        equipCombo.ItemTemplate = EquipPickLook.ItemTemplate();
        equipCombo.ItemSelector = (_, item) => (item as EquipPickItem)?.Id ?? item?.ToString() ?? "";
        equipCombo.ItemFilter = (text, item) => item is EquipPickItem e && (string.IsNullOrEmpty(text) || e.Id.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Detail.Contains(text, StringComparison.OrdinalIgnoreCase));
        ToolTip.SetTip(equipCombo, "下拉是设备台账里的在籍设备（按 穿孔→挖装→运输→排土→辅助 排序，\n不在用的排在最后并压暗）；台账没接通时可直接输入编号");
        L1(equipCombo);
        L1(Lbl("起(HH:mm)")); L1(startBox);
        L1(Lbl("止(HH:mm)")); L1(endBox);
        L1(Lbl("类别"));
        foreach (var k in new[] { "定修", "保养", "临修", "年检" }) kindCombo.Items.Add(new ComboBoxItem { Content = k });
        kindCombo.SelectedIndex = 0;
        L1(kindCombo);
        var add = TaskUi.Btn("加入档期", OnAdd, 88); add.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(add, Avalonia.Controls.Dock.Right); row1.Children.Add(add);
        L1(Lbl("备注"));
        row1.Children.Add(noteBox);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal };
        var del = TaskUi.Btn("删除选中", OnDelete, 88); del.Margin = new Thickness(0); row2.Children.Add(del);
        var reload = TaskUi.Btn("重新载入", OnReload, 88); reload.Margin = new Thickness(10, 0, 0, 0); row2.Children.Add(reload);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        row2.Children.Add(toolStatus);
        var toolPanel = new StackPanel(); toolPanel.Children.Add(row1); toolPanel.Children.Add(row2);

        // 跨零点必须拆两条：装箱的时窗是同一天内的 [start,end)，绕回 0 点会把次日的活算进今天
        TaskUi.Theme(hintLine, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var hintBox = new Border { Margin = new Thickness(10, 10, 10, 0), Padding = new Thickness(12, 9), BorderThickness = new Thickness(1), Child = hintLine };
        TaskUi.Theme(hintBox, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(hintBox, Border.BorderBrushProperty, "Theme.Surface.Border");

        grid.Columns.Add(TaskUi.TextCol("设备", nameof(Row.Equip), 110));
        grid.Columns.Add(TaskUi.TextCol("日期", nameof(Row.Date), 110));
        grid.Columns.Add(TaskUi.TextCol("起", nameof(Row.Start), 70));
        grid.Columns.Add(TaskUi.TextCol("止", nameof(Row.End), 70));
        grid.Columns.Add(TaskUi.TextCol("时长(h)", nameof(Row.Hours), 76));
        grid.Columns.Add(TaskUi.TextCol("类别", nameof(Row.Kind), 76));
        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("影响班次"), Binding = new Binding(nameof(Row.Shifts)), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("备注"), Binding = new Binding(nameof(Row.Note)), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        var bar = TaskUi.Bar(toolPanel, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(hintBox, 2); Grid.SetRow(grid, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(hintBox); g.Children.Add(grid);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        Opened += (_, _) => { FillEquipList(); Fill(); };
        // ★ 窗口可能在**应用还没启动完**的时候就被打开：每次窗口重新激活时，只要上一轮是失败的，就自己再试一次。
        Activated += (_, _) => { if (!_loadedOk) { FillEquipList(); Fill(); } };
    }

    private static TextBlock Lbl(string t)
    {
        var l = TaskUi.Lbl(t); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 0);
        return l;
    }

    /// <summary>
    /// 装设备下拉。<b>带类别图标</b>：光看编号分不出是电铲还是水车，而挑错一台的代价是整条链。
    /// <para><b>读不到不再静默</b>：把原因写在提示上（下拉照样能直接输编号）。</para>
    /// </summary>
    private void FillEquipList()
    {
        try
        {
            var items = EquipPickItem.Build(EquipmentDataContext.Equipment.All());
            equipCombo.ItemsSource = items;
            _loadedOk = true;

            // ★ 默认选中第一台**在用**的设备；全是报废时不许兜底选；人已经挑过/输过就不动它。
            if (string.IsNullOrWhiteSpace(equipCombo.Text))
            {
                var first = items.FirstOrDefault(x => x.InUse);
                if (first != null) { equipCombo.SelectedItem = first; equipCombo.Text = first.Id; }
            }
            ToolTip.SetTip(equipCombo, items.Count > 0
                ? $"设备台账 {items.Count} 台（按 穿孔→挖装→运输→排土→辅助 排序，"
                  + $"其中 {items.Count(x => !x.InUse)} 台不在用、排在最后并压暗）"
                : "◆ 设备台账里一台设备都没有 —— 可直接输入编号，但那样排出来的档期没有台账依据。");
        }
        catch (Exception ex)
        {
            equipCombo.ItemsSource = null;
            _loadedOk = false;
            bool notReady = ex is InvalidOperationException && ex.Message.Contains("未初始化");
            ToolTip.SetTip(equipCombo, notReady
                ? "◆ 应用还在启动中（数据库模块尚未就绪）—— 下拉是空的**不代表没有设备**。"
                  + "等状态栏不再显示「启动中…」，点一次「重新载入」即可；"
                  + "切走再切回本窗口也会自动重试。"
                : "◆ 设备台账读不到（" + Short(ex) + "）—— "
                  + "下拉是空的**不代表没有设备**。可直接输入编号，"
                  + "但那样挑不到类别，也核不了这台设备在不在用。");
        }
    }

    private void Fill()
    {
        _date = ProjectScope.WorkDate;

        List<MaintenanceWindowPlan> rows;
        string err = "";
        try { rows = EquipmentDataContext.MaintenanceWindows.ByDate(_date).Where(r => r != null).ToList(); }
        catch (Exception ex) { rows = new List<MaintenanceWindowPlan>(); err = Short(ex); _loadedOk = false; }

        // 本日班次（从盘子取，与引擎同一份）——用来算这条档期影响哪几个班
        List<ShiftWindow> shifts;
        try { shifts = ProductionPlanContext.Shifts.ToList(); }
        catch { shifts = new List<ShiftWindow>(); }

        var list = rows.Select(r =>
        {
            double? s = Hour(r.StartTime), e = Hour(r.EndTime);
            bool ok = s.HasValue && e.HasValue && e.Value > s.Value;
            return new Row
            {
                Equip = r.EquipmentId,
                Date = r.PlanDate,
                Start = r.StartTime,
                End = r.EndTime,
                // 起止解析不出来时如实标出来：引擎会丢弃这条，界面必须让人看得见为什么没生效
                Hours = ok ? $"{e!.Value - s!.Value:0.##}" : "⚠ 时刻非法",
                Kind = r.Kind,
                Shifts = ok ? Overlap(shifts, s!.Value, e!.Value) : "—",
                Note = r.Note ?? "",
                Entity = r,
            };
        }).ToList();

        grid.ItemsSource = list;

        toolStatus.Text = err.Length > 0
            ? (err.Contains("未初始化")
                ? "◆ 应用还在启动中（数据库模块尚未就绪）—— 档期读不到。"
                + "等状态栏不再显示「启动中…」，点一次「重新载入」；切走再切回本窗口也会自动重试。"
                : $"档期表读取失败（{err}）")
            : $"{_date:yyyy-MM-dd} · {list.Count} 条档期"
              + (list.Count == 0 ? " —— 本日无检修，装箱不扣检修时段" : "");

        int bad = list.Count(r => r.Hours.StartsWith("⚠"));
        hintLine.Text =
            "本表直接进装箱：某设备某班的有效时窗会扣掉与档期重叠的部分（检修排在班首时，作业从检修结束时刻起算）。\n"
          + "跨零点请拆两条（当日 22:00–24:00 + 次日 00:00–02:00）——装箱时窗是同一天内的 [起,止)，绕回 0 点会把次日的活算进今天。\n"
          + "计划检修 ≠ 故障：已经发生的停机请在「设备状态·故障报修」里记，那边算的是实际停机与完好率。"
          + (bad > 0 ? $"\n⚠ 有 {bad} 条起止时刻非法，引擎会丢弃它们（不按 0 点算），请修正或删除。" : "");
    }

    /// <summary>这条档期压到了哪几个班（让人一眼看出"这台今天早班干不了活"）。</summary>
    private static string Overlap(List<ShiftWindow> shifts, double s, double e)
    {
        var hit = shifts.Where(w => s < w.End && e > w.Start)
                        .Select(w => w.Name).ToList();
        return hit.Count > 0 ? string.Join(" / ", hit) : "不压任何班次";
    }

    private void OnAdd()
    {
        string id = ((equipCombo.SelectedItem as EquipPickItem)?.Id ?? equipCombo.Text ?? "").Trim();
        if (id.Length == 0) { toolStatus.Text = "请先选（或输入）设备编号。"; return; }

        double? s = Hour(startBox.Text), t = Hour(endBox.Text);
        if (s is null || t is null) { toolStatus.Text = "起止时刻要写成 HH:mm（如 08:00），或直接写小时数。"; return; }
        if (t.Value <= s.Value)
        {
            toolStatus.Text = "止必须晚于起。跨零点请拆两条：当日 22:00–24:00 + 次日 00:00–02:00 —— "
                            + "装箱时窗是同一天内的 [起,止)，绕回 0 点会把次日的活算进今天。";
            return;
        }

        var ent = new MaintenanceWindowPlan
        {
            EquipmentId = id,
            PlanDate = _date.ToString("yyyy-MM-dd"),
            StartTime = Text(s.Value),
            EndTime = Text(t.Value),
            Kind = ((kindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "定修").Trim(),
            Note = string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text!.Trim(),
        };

        try { EquipmentDataContext.MaintenanceWindows.Upsert(ent); }
        catch (Exception ex) { toolStatus.Text = $"保存失败：{Short(ex)}"; return; }

        ProductionPlanContext.Invalidate();   // 时窗变了，盘子作废重算
        Fill();
        toolStatus.Text = $"已加入：{id} {ent.StartTime}–{ent.EndTime} {ent.Kind}（盘子已作废，下次编制按新时窗算）";
    }

    private void OnDelete()
    {
        if (grid.SelectedItem is not Row r || r.Entity == null)
        { toolStatus.Text = "先选中一条档期。"; return; }

        try
        {
            EquipmentDataContext.MaintenanceWindows.Delete(r.Entity.EquipmentId, _date, r.Entity.StartTime);
        }
        catch (Exception ex) { toolStatus.Text = $"删除失败：{Short(ex)}"; return; }

        ProductionPlanContext.Invalidate();
        Fill();
        toolStatus.Text = $"已删除：{r.Equip} {r.Start}–{r.End}";
    }

    private void OnReload()
    {
        ProductionPlanContext.Invalidate();
        FillEquipList();     // ★ 应用启动完了点「重新载入」，设备下拉也要跟着重装
        Fill();
    }

    /// <summary>'HH:mm' 或纯小时数 → 0..24；认不出返回 null（绝不退化成 0）。</summary>
    private static double? Hour(string? s)
    {
        string v = (s ?? "").Trim();
        if (v.Length == 0) return null;
        if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var ts) && ts.TotalHours is >= 0 and <= 24)
            return Math.Round(ts.TotalHours, 3);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h is >= 0 and <= 24)
            return h;
        return null;
    }

    private static string Text(double hour)
    {
        int h = (int)Math.Floor(hour);
        int m = (int)Math.Round((hour - h) * 60);
        if (m >= 60) { h++; m -= 60; }
        return h >= 24 ? "24:00" : $"{h:00}:{m:00}";
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
