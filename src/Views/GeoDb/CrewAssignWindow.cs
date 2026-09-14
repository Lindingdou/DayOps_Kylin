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
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「班组派工」—— 把操作手 / 司机配到设备编组，并做<b>持证与出勤校核</b>。
///
/// <para>
/// 口径在 <see cref="CrewAssignModel"/>（纯函数、可脱 GUI 验收）。
/// 下半张表是**花名册**（<c>crew_roster.json</c>）的唯一入口：没有花名册就没法派工、也没法校持证，
/// 而「有表没入口 = 那张表永远没人填」这个亏班次日历吃过。
/// </para>
/// <para>
/// <b>不生成样例花名册</b>：编出来的是人名，会被当成真人派工、写进单据、发到班组。
/// </para>
/// </summary>
internal sealed class CrewAssignWindow : Window
{
    private static readonly IBrush OkBrush = Brush.Parse("#16A34A");
    private static readonly IBrush BadBrush = Brush.Parse("#DC2626");

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _shift = new() { MinWidth = 110 };
    private readonly TextBox _by = new() { Width = 120, Watermark = "派工人" };
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _check = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };
    private readonly TextBlock _rosterStatus = new() { FontSize = 11, Foreground = Brush.Parse("#555"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private readonly DataGrid _grid = NewGrid(readOnly: false);
    private readonly DataGrid _rosterGrid = NewGrid(readOnly: true);
    private readonly ObservableCollection<CrewRow> _rows = new();
    private readonly ObservableCollection<CrewMember> _roster = new();

    // 花名册录入表单
    private readonly TextBox _fId = new() { Width = 96, Watermark = "工号" };
    private readonly TextBox _fName = new() { Width = 110, Watermark = "姓名" };
    private readonly ComboBox _fJob = new() { Width = 96, ItemsSource = CrewAssignModel.Jobs, SelectedIndex = 0 };
    private readonly TextBox _fCert = new() { Width = 168, Watermark = "持证（电铲/钻机/矿卡…）" };
    private readonly TextBox _fPhone = new() { Width = 120, Watermark = "电话" };
    private readonly CheckBox _fDuty = new() { Content = "在岗", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };

    private PlanAssembly _asm = new();
    private ExploderResult _plan = new();

    private static DataGrid NewGrid(bool readOnly) => new()
    {
        AutoGenerateColumns = false, IsReadOnly = readOnly,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal CrewAssignWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "班组派工";
        Width = 1280; Height = 620;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        _by.Text = Environment.UserName;
        BuildColumns();
        _grid.ItemsSource = _rows;
        _rosterGrid.ItemsSource = _roster;
        Content = BuildLayout();

        _shift.ItemsSource = new[] { "全部" };
        _shift.SelectedIndex = 0;
        _shift.SelectionChanged += (_, _) => Rebind();
        _date.SelectedDateChanged += (_, _) => Reload();
        Reload();
    }

    private void BuildColumns()
    {
        DataGridTextColumn C(string h, string path, double w, bool ro = true) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path), IsReadOnly = ro };

        _grid.Columns.Add(C("班次", nameof(CrewRow.Shift), 84));
        _grid.Columns.Add(C("编组", nameof(CrewRow.Group), 170));
        _grid.Columns.Add(C("设备类别", nameof(CrewRow.Category), 110));
        // ★ 这两列可编辑：手工填名字与自动派工共用一份数据
        _grid.Columns.Add(C("操作手", nameof(CrewRow.Operator), 130, ro: false));
        _grid.Columns.Add(C("卡车司机（顿号分隔）", nameof(CrewRow.Drivers), 260, ro: false));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("持证校核"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(CrewRow.Cert)), IsReadOnly = true,
        });
        _grid.Columns.Add(C("出勤", nameof(CrewRow.Attend), 180));

        DataGridTextColumn R(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };
        _rosterGrid.Columns.Add(R("工号", nameof(CrewMember.PersonId), 110));
        _rosterGrid.Columns.Add(R("姓名", nameof(CrewMember.Name), 120));
        _rosterGrid.Columns.Add(R("工种", nameof(CrewMember.Job), 96));
        _rosterGrid.Columns.Add(R("持证类别", nameof(CrewMember.CertFor), 120));
        _rosterGrid.Columns.Add(R("电话", nameof(CrewMember.Phone), 140));
        _rosterGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("在岗"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(CrewMember.OnDuty)),
        });
    }

    private static Button B(string t, Action a, bool bold = false, string? tip = null)
    {
        var b = new Button { Content = t, Padding = new Thickness(11, 4), Margin = new Thickness(0, 0, 8, 0) };
        if (bold) b.FontWeight = FontWeight.SemiBold;
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => a();
        return b;
    }

    private Control BuildLayout()
    {
        var bar = new WrapPanel { Margin = new Thickness(12, 8, 12, 4) };
        void P(Control c) => bar.Children.Add(c);
        P(new TextBlock { Text = "作业日", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        P(_date);
        P(new TextBlock { Text = "班次", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        P(_shift);
        P(new TextBlock { Text = "派工人", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        P(_by);
        P(new Border { Width = 10 });
        P(B("自动派工", AutoAssign, bold: true, tip: "按【设备类别 ↔ 持证类别】匹配，避开休班与重复占用；爆破行不塞人"));
        P(B("重新校核", Recheck, tip: "手工改过名字之后按它重新比对花名册"));
        P(B("保存派工", Save));
        P(B("重新装配", Reload));

        var top = new StackPanel
        {
            Children =
            {
                bar,
                new Border
                {
                    Background = Brush.Parse("#F5F7FA"), Padding = new Thickness(12, 6),
                    BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 1),
                    Child = new StackPanel { Children = { _header, _check } },
                },
            },
        };

        var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void F(Control c) => form.Children.Add(c);
        F(new TextBlock { Text = "花名册", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        F(_fId); F(new Border { Width = 6 }); F(_fName); F(new Border { Width = 6 });
        F(_fJob); F(new Border { Width = 6 }); F(_fCert); F(new Border { Width = 6 }); F(_fPhone); F(_fDuty);
        F(B("加入花名册", AddMember, bold: true, tip: "工号是主键：姓名会重，重名的人一律不参与持证校核"));
        F(B("删除选中", DeleteMember));
        F(_rosterStatus);

        var rosterBox = new StackPanel
        {
            Children =
            {
                new Border
                {
                    Background = Brush.Parse("#FAFAFA"), Padding = new Thickness(12, 6),
                    BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
                    Child = form,
                },
                new Border { Height = 140, Margin = new Thickness(12, 4), Child = _rosterGrid },
            },
        };

        var footBar = new Border
        {
            Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(12, 6, 12, 9),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { B("关闭", Close) },
            },
        };
        var statusBar = new Border { Padding = new Thickness(12, 2), Child = _status };

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(rosterBox, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(rosterBox);
        root.Children.Add(new Border { Margin = new Thickness(12, 6), Child = _grid });
        return root;
    }

    private DateTime CurrentDate => _date.SelectedDate?.Date ?? DateTime.Today;
    private string PlanDate => CurrentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private string ShiftFilter => _shift.SelectedItem as string is { } s && s != "全部" ? s : "";

    internal void Reload()
    {
        _asm = ProductionPlanContext.Assemble(_conn(), CurrentDate, DateTime.Now.TimeOfDay.TotalHours);
        _plan = _asm.Usable ? TaskExploder.Explode(_asm.Config) : new ExploderResult();

        var shifts = new List<string> { "全部" };
        shifts.AddRange(_asm.Config.Shifts.Select(s => s.Name));
        string? keep = _shift.SelectedItem as string;
        _shift.ItemsSource = shifts;
        _shift.SelectedIndex = keep != null && shifts.Contains(keep) ? shifts.IndexOf(keep) : 0;

        LoadRoster();
        Rebind();
    }

    private void LoadRoster()
    {
        _roster.Clear();
        foreach (var m in CrewStore.LoadRoster()) _roster.Add(m);
        string err = CrewStore.LastError;
        _rosterStatus.Text = err.Length > 0
            ? "⚠ " + err
            : _roster.Count == 0
                ? "花名册是空的 —— 先在这一排把人录进来（这里不编样例人名：编出来的名字会被当成真人派工、写进单据）"
                : $"花名册 {_roster.Count} 人（在岗 {_roster.Count(m => m.OnDuty)}）";
    }

    private void Rebind()
    {
        var cats = ProductionPlanContext.CategoryOfEquipment(_conn());
        var rows = CrewAssignModel.BuildRows(_plan.Tasks, ShiftFilter, cats);
        CrewAssignModel.Apply(rows, CrewStore.LoadAssignments(), PlanDate, ShiftFilter);
        var chk = CrewAssignModel.Recheck(rows, _roster.ToList());

        _rows.Clear();
        foreach (var r in rows) _rows.Add(r);

        _header.Text = $"{PlanDate}　{_asm.SourceLabel}";
        _check.Foreground = _asm.Usable ? (chk.IssueCount == 0 ? OkBrush : BadBrush) : BadBrush;
        _check.Text = _asm.Usable ? chk.Summary : "排不出来 —— " + string.Join(" ", _asm.Notes);
        _status.Text = $"共 {_rows.Count(r => r.NeedsCrew)} 个编组待派工"
                     + (_rows.Any(r => r.Kind == CrewRowKind.Blast) ? "（另有爆破一行按口径不指人）" : "")
                     + "；持证校核按花名册比对，不是一律打勾。";
    }

    private void AutoAssign()
    {
        if (_roster.Count == 0) { _status.Text = "花名册是空的，自动派工无从谈起 —— 先在下半张表把人录进来。"; return; }
        var rows = _rows.ToList();
        var res = CrewAssignModel.AutoAssign(rows, _roster.ToList());
        _rows.Clear();
        foreach (var r in rows) _rows.Add(r);
        _check.Foreground = res.IssueCount == 0 ? OkBrush : BadBrush;
        _check.Text = res.Summary;
        _status.Text = $"自动派工：补入 {res.Filled} 人（按设备类别↔持证类别匹配，避开休班与重复占用）。"
                     + (res.Shortfall.Count > 0
                        ? " ⚠ 人手不足：" + string.Join("；", res.Shortfall) + " —— 请在花名册补录人员或跨班调剂。"
                        : "");
    }

    private void Recheck()
    {
        var rows = _rows.ToList();
        var chk = CrewAssignModel.Recheck(rows, _roster.ToList());
        _rows.Clear();
        foreach (var r in rows) _rows.Add(r);
        _check.Foreground = chk.IssueCount == 0 ? OkBrush : BadBrush;
        _check.Text = chk.Summary;
        _status.Text = "已按花名册重新校核持证与出勤。";
    }

    private void Save()
    {
        if (ShiftFilter.Length == 0) { _status.Text = "派工一班一份 —— 先在上面选一个具体班次再保存。"; return; }
        string by = string.IsNullOrWhiteSpace(_by.Text) ? Environment.UserName : _by.Text!.Trim();
        var list = _rows.Select(r => CrewAssignModel.ToAssignment(r, PlanDate, ShiftFilter, by))
                        .Where(a => a != null).Select(a => a!).ToList();
        if (list.Count == 0) { _status.Text = "没有可保存的派工行。"; return; }

        string err = CrewStore.SaveAssignments(list);
        if (err.Length > 0) { _status.Text = "没存下：" + err; return; }
        _status.Text = $"已保存 {list.Count} 条派工（{PlanDate} {ShiftFilter}）—— 生产任务书的「作业人员」一列即刻按它显示。";
        _echo(_status.Text);
    }

    private void AddMember()
    {
        string name = (_fName.Text ?? "").Trim();
        if (name.Length == 0) { _rosterStatus.Text = "先填姓名。"; return; }
        string id = (_fId.Text ?? "").Trim();
        if (id.Length > 0 && _roster.Any(m => string.Equals(m.PersonId, id, StringComparison.OrdinalIgnoreCase)))
        { _rosterStatus.Text = $"工号 {id} 已经在花名册里了。"; return; }

        _roster.Add(new CrewMember
        {
            PersonId = id, Name = name,
            Job = _fJob.SelectedItem as string ?? CrewAssignModel.JobOperator,
            CertFor = (_fCert.Text ?? "").Trim(),
            Phone = (_fPhone.Text ?? "").Trim(),
            OnDuty = _fDuty.IsChecked == true,
        });
        string err = CrewStore.SaveRoster(_roster);
        LoadRoster();
        if (err.Length > 0) { _rosterStatus.Text = err; return; }
        _fId.Text = _fName.Text = _fCert.Text = _fPhone.Text = "";
        Recheck();
        // 同名要当场说 —— 姓名不是主键，重名的人一律不参与持证校核
        int dup = _roster.Count(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (dup > 1) _rosterStatus.Text = $"⚠ 花名册里已有 {dup} 个「{name}」—— 重名的人按姓名对不准，请用工号区分。";
    }

    private void DeleteMember()
    {
        if (_rosterGrid.SelectedItem is not CrewMember m) { _rosterStatus.Text = "先选中要删的那一行。"; return; }
        _roster.Remove(m);
        CrewStore.SaveRoster(_roster);
        LoadRoster();
        Recheck();
        _rosterStatus.Text = $"已删除 {m.Name}。";
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal int RosterCount => _roster.Count;
    internal string HeaderText => _header.Text ?? "";
    internal string CheckText => _check.Text ?? "";
    internal string RosterStatusText => _rosterStatus.Text ?? "";
}
