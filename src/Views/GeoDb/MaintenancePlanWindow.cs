using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 检修档期（移植原 <c>TaskLib.Features.MaintenancePlanWindow</c>）：按日排设备的计划检修时窗，
/// 逐条显示时长与**压到了哪几个班**。班次时窗由 §三三二 接通的班次日历推出来。
///
/// 底部三句提示逐条照搬原窗 —— 它们不是客套话，是这张表最容易被用错的三处：
///   ① 本表直接进装箱（有效时窗会扣掉与档期重叠的部分）；
///   ② 跨零点要拆两条；
///   ③ **计划检修 ≠ 故障**（已发生的停机在「设备状态·故障报修」记，那边算实际停机与完好率）。
/// </summary>
internal sealed class MaintenancePlanWindow : Window
{
    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    internal sealed class Row
    {
        public string Equip { get; set; } = "";
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
        public string Hours { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Shifts { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private readonly DatePicker _date = new();
    private readonly TextBox _equip = new() { Width = 130, Watermark = "设备编号" };
    private readonly TextBox _start = new() { Width = 90, Text = "08:00" };
    private readonly TextBox _end = new() { Width = 90, Text = "12:00" };
    private readonly ComboBox _kind = new() { Width = 100, ItemsSource = new[] { "定修", "保养", "临修", "年检" }, SelectedIndex = 0 };
    private readonly TextBox _note = new() { Width = 180, Watermark = "备注（检修内容/承修班组）" };

    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };
    private readonly ObservableCollection<Row> _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#666") };

    public MaintenancePlanWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "检修档期";
        Width = 940; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _date.SelectedDate = DateTime.Today;
        _date.SelectedDateChanged += (_, _) => Reload();

        static TextBlock Head(string t) => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };
        _grid.Columns.Add(C("设备", nameof(Row.Equip), 120));
        _grid.Columns.Add(C("起", nameof(Row.Start), 80));
        _grid.Columns.Add(C("止", nameof(Row.End), 80));
        _grid.Columns.Add(C("时长(h)", nameof(Row.Hours), 100));
        _grid.Columns.Add(C("类别", nameof(Row.Kind), 90));
        _grid.Columns.Add(C("影响班次", nameof(Row.Shifts), 170));
        _grid.Columns.Add(new DataGridTextColumn
        { Header = Head("备注"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding(nameof(Row.Note)) });
        _grid.ItemsSource = _rows;

        var btnAdd = new Button { Content = "加入档期", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        var btnDel = new Button { Content = "删除选中", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        var btnReload = new Button { Content = "重新载入", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        btnAdd.Click += (_, _) => Add();
        btnDel.Click += (_, _) => DeleteSelected();
        btnReload.Click += (_, _) => Reload();

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void P(Control c) => bar.Children.Add(c);
        P(new TextBlock { Text = "日期", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        P(_date);
        P(new TextBlock { Text = "设备", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        P(_equip);
        P(new TextBlock { Text = "起", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        P(_start);
        P(new TextBlock { Text = "止", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        P(_end);
        P(new TextBlock { Text = "类别", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        P(_kind);
        P(_note);
        P(btnAdd); P(btnDel); P(btnReload);

        var bottom = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _hint, new Border { Height = 26, Background = Brush.Parse("#F3F3F3"), Margin = new Thickness(0, 6, 0, 0), Child = _status } },
        };
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        Reload();
    }

    private DateTime CurrentDate => _date.SelectedDate?.Date ?? DateTime.Today;
    private string DateText => CurrentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal void Reload()
    {
        var conn = _conn();
        _rows.Clear();
        if (conn == null) { _status.Text = "没有数据库连接 —— 先用「数据库连接」连上。"; SetHint(0); return; }

        var shifts = MaintenanceWindows.ShiftWindowsOf(WorkCalendar.Day(conn, CurrentDate, out _));
        var rows = MaintenanceWindows.ByDate(conn, CurrentDate, out string err);
        int bad = 0;
        foreach (var r in rows)
        {
            double? s = MaintenanceWindows.Hour(r.StartTime), e = MaintenanceWindows.Hour(r.EndTime);
            bool ok = s.HasValue && e.HasValue && e.Value > s.Value;
            if (!ok) bad++;
            _rows.Add(new Row
            {
                Equip = r.EquipmentId, Start = r.StartTime, End = r.EndTime,
                // 起止解析不出来时如实标出来：引擎会丢弃这条，界面必须让人看得见为什么没生效
                Hours = ok ? $"{e!.Value - s!.Value:0.##}" : "⚠ 时刻非法",
                Kind = r.Kind,
                Shifts = ok ? MaintenanceWindows.Overlap(shifts, s!.Value, e!.Value) : "—",
                Note = r.Note ?? "",
            });
        }
        _status.Text = err.Length > 0
            ? $"档期表读取失败（{err}）"
            : $"{DateText} · {_rows.Count} 条档期" + (_rows.Count == 0 ? " —— 本日无检修，装箱不扣检修时段" : "");
        SetHint(bad);
    }

    private void SetHint(int bad) => _hint.Text =
        "本表直接进装箱：某设备某班的有效时窗会扣掉与档期重叠的部分（检修排在班首时，作业从检修结束时刻起算）。\n"
      + "跨零点请拆两条（当日 22:00–24:00 + 次日 00:00–02:00）—— 装箱时窗是同一天内的 [起,止)，绕回 0 点会把次日的活算进今天。\n"
      + "计划检修 ≠ 故障：已经发生的停机请在「设备状态·故障报修」里记，那边算的是实际停机与完好率。"
      + (bad > 0 ? $"\n⚠ 有 {bad} 条起止时刻非法，引擎会丢弃它们（不按 0 点算），请修正或删除。" : "");

    internal void Add()
    {
        var conn = _conn();
        if (conn == null) { _status.Text = "没有数据库连接"; return; }
        string err = MaintenanceWindows.Upsert(conn, new MaintenanceWindowRow
        {
            EquipmentId = (_equip.Text ?? "").Trim(),
            PlanDate = DateText,
            StartTime = (_start.Text ?? "").Trim(),
            EndTime = (_end.Text ?? "").Trim(),
            Kind = (_kind.SelectedItem as string) ?? "定修",
            Note = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text!.Trim(),
        });
        if (err.Length > 0) { _status.Text = err; return; }
        _echo($"检修档期：{DateText} {_equip.Text} {_start.Text}–{_end.Text} 已加入");
        Reload();
    }

    internal void DeleteSelected()
    {
        var conn = _conn();
        if (conn == null) { _status.Text = "没有数据库连接"; return; }
        if (_grid.SelectedItem is not Row r) { _status.Text = "请先在表里选中一条。"; return; }
        string err = MaintenanceWindows.Delete(conn, r.Equip, DateText, r.Start);
        _status.Text = err.Length > 0 ? "删除失败：" + err : $"已删除 {r.Equip} {r.Start}–{r.End}";
        Reload();
    }

    // ── 自检钩子用 ──
    internal void SetDate(DateTime d) { _date.SelectedDate = d; Reload(); }
    internal void Fill(string equip, string start, string end) { _equip.Text = equip; _start.Text = start; _end.Text = end; }
    internal string StatusText => _status.Text ?? "";
    internal int RowCount => _rows.Count;
    internal string ShiftsOf(int i) => i >= 0 && i < _rows.Count ? _rows[i].Shifts : "";
}
