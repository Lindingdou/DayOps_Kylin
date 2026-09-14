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
/// 班次日历（移植原 <c>TaskLib.Features.ShiftCalendarWindow</c>）：
/// 按日看三班（开班时刻 / 班长 / 是否爆破班 / 天气 / 备注），可改可存；
/// 「生成整月」一次铺满当月，**已排过的日子原样保留**。底部显示本月作业日口径。
///
/// 这张表（<c>shift_calendar</c>）在 V001 就建好了，但一直**没有任何读写** ——
/// 本窗与 <see cref="WorkCalendar"/> 一起把它接通。接通之后，
/// §一九四c 记的「`WorkWindowCalc` 依赖受阻的班次日历配置」那条依据也就不成立了。
/// </summary>
internal sealed class ShiftCalendarWindow : Window
{
    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    /// <summary>表格一行（编辑态；写回时转成 <see cref="ShiftCalendarRow"/>）。</summary>
    internal sealed class Row
    {
        public string Shift { get; set; } = "";        // 显示用中文名
        public string StartTime { get; set; } = "";
        public string LeaderName { get; set; } = "";
        public bool IsBlast { get; set; }
        public string Weather { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    private readonly DatePicker _date = new();
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };
    private readonly ObservableCollection<Row> _rows = new();
    private readonly TextBlock _status = new() { Text = "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _monthInfo = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#2B579A") };

    public ShiftCalendarWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "班次日历";
        Width = 820; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _date.SelectedDate = DateTime.Today;
        _date.SelectedDateChanged += (_, _) => Reload();

        static TextBlock Head(string t) => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };
        DataGridTextColumn Text(string h, string path, double w, bool ro = false) => new()
        {
            Header = Head(h), Width = new DataGridLength(w), IsReadOnly = ro,
            Binding = new Binding(path) { Mode = ro ? BindingMode.OneWay : BindingMode.TwoWay },
        };
        _grid.Columns.Add(Text("班次", nameof(Row.Shift), 90, ro: true));
        _grid.Columns.Add(Text("开班时刻", nameof(Row.StartTime), 110));
        _grid.Columns.Add(Text("班长", nameof(Row.LeaderName), 120));
        _grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = Head("爆破班"), Width = new DataGridLength(84),
            Binding = new Binding(nameof(Row.IsBlast)) { Mode = BindingMode.TwoWay },
        });
        _grid.Columns.Add(Text("天气", nameof(Row.Weather), 120));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("备注"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(Row.Notes)) { Mode = BindingMode.TwoWay },
        });
        _grid.ItemsSource = _rows;

        var btnSave = new Button { Content = "保存本日", Padding = new Thickness(12, 3), Margin = new Thickness(10, 0, 0, 0) };
        var btnMonth = new Button { Content = "生成整月", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        var btnReload = new Button { Content = "重新载入", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        btnSave.Click += (_, _) => SaveDay();
        btnMonth.Click += (_, _) => BuildMonth();
        btnReload.Click += (_, _) => Reload();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new TextBlock { Text = "工作日期", VerticalAlignment = VerticalAlignment.Center },
                new Border { Width = 8 },
                _date, btnSave, btnMonth, btnReload,
            },
        };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
        var bottom = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children
                = { _monthInfo, new Border { Height = 26, Background = Brush.Parse("#F3F3F3"), Margin = new Thickness(0, 6, 0, 0), Child = _status } },
        };
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        Reload();
    }

    private DateTime CurrentDate => _date.SelectedDate?.Date ?? DateTime.Today;

    internal void Reload()
    {
        var conn = _conn();
        _rows.Clear();
        if (conn == null) { _status.Text = "没有数据库连接 —— 先用「数据库连接」连上。"; _monthInfo.Text = ""; return; }

        var have = WorkCalendar.Day(conn, CurrentDate, out string err);
        // 三班恒列出来: 没排的那班也要能当场填, 不然得先去别处建一条
        foreach (var code in new[] { "A", "B", "C" })
        {
            var r = have.FirstOrDefault(x => string.Equals(x.Shift, code, StringComparison.OrdinalIgnoreCase));
            _rows.Add(new Row
            {
                Shift = WorkCalendar.ShiftName(code),
                StartTime = r?.StartTime ?? "",
                LeaderName = r?.LeaderName ?? "",
                IsBlast = r?.IsBlastShift ?? false,
                Weather = r?.Weather ?? "",
                Notes = r?.Notes ?? "",
            });
        }
        // 台账里若有 A/B/C 以外的班制, 一并列出(不猜、不丢)
        foreach (var extra in have.Where(x => !"ABC".Contains(x.Shift.ToUpperInvariant())))
            _rows.Add(new Row
            {
                Shift = WorkCalendar.ShiftName(extra.Shift), StartTime = extra.StartTime ?? "",
                LeaderName = extra.LeaderName ?? "", IsBlast = extra.IsBlastShift,
                Weather = extra.Weather ?? "", Notes = extra.Notes ?? "",
            });

        _status.Text = err.Length > 0
            ? "读取失败：" + err
            : $"{CurrentDate:yyyy-MM-dd} · 台账已有 {have.Count} 条班次记录";
        _monthInfo.Text = WorkCalendar.MonthWorkdays(conn, CurrentDate).Label;
    }

    internal void SaveDay()
    {
        var conn = _conn();
        if (conn == null) { _status.Text = "没有数据库连接"; return; }

        int ok = 0, skipped = 0;
        var errs = new List<string>();
        foreach (var r in _rows)
        {
            // 整行空白就当"这班没排", 不写一条空记录进台账
            if (string.IsNullOrWhiteSpace(r.StartTime) && string.IsNullOrWhiteSpace(r.LeaderName)
                && string.IsNullOrWhiteSpace(r.Weather) && string.IsNullOrWhiteSpace(r.Notes) && !r.IsBlast)
            { skipped++; continue; }

            string err = WorkCalendar.Upsert(conn, new ShiftCalendarRow
            {
                Date = CurrentDate,
                Shift = WorkCalendar.ShiftCode(r.Shift),
                StartTime = Null(r.StartTime), LeaderName = Null(r.LeaderName),
                IsBlastShift = r.IsBlast, Weather = Null(r.Weather), Notes = Null(r.Notes),
            });
            if (err.Length == 0) ok++;
            else if (errs.Count < 3) errs.Add($"{r.Shift}：{err}");
        }
        _status.Text = $"已保存 {ok} 条"
                     + (skipped > 0 ? $"　· {skipped} 班整行空白，未写入台账" : "")
                     + (errs.Count > 0 ? "　◆ " + string.Join("；", errs) : "");
        _echo($"班次日历 {CurrentDate:yyyy-MM-dd}：已保存 {ok} 条");
        _monthInfo.Text = WorkCalendar.MonthWorkdays(conn, CurrentDate).Label;
    }

    internal void BuildMonth()
    {
        var conn = _conn();
        if (conn == null) { _status.Text = "没有数据库连接"; return; }

        var first = new DateTime(CurrentDate.Year, CurrentDate.Month, 1);
        var existing = WorkCalendar.InRange(conn, first, first.AddMonths(1).AddDays(-1), out _);
        var (rows, kept) = WorkCalendar.BuildMonth(CurrentDate, existing);

        int ok = 0;
        var errs = new List<string>();
        foreach (var r in rows)
        {
            string err = WorkCalendar.Upsert(conn, r);
            if (err.Length == 0) ok++;
            else if (errs.Count < 3) errs.Add(err);
        }
        _status.Text = $"已写入 {ok}/{rows.Count} 条"
                     + (kept.Count > 0 ? $"　· {kept.Count} 天已排过，保留未动" : "")
                     + (errs.Count > 0 ? "　◆ " + string.Join("；", errs) : "");
        _echo($"班次日历：{first:yyyy-MM} 生成 {ok} 条，保留已排 {kept.Count} 天");
        Reload();     // 留在刚生成的那一月, 不跳回今天
    }

    private static string? Null(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── 自检钩子用 ──
    internal void SetDate(DateTime d) { _date.SelectedDate = d; Reload(); }
    internal void SetRow(int i, string? start, string? leader, bool blast)
    {
        if (i < 0 || i >= _rows.Count) return;
        if (start != null) _rows[i].StartTime = start;
        if (leader != null) _rows[i].LeaderName = leader;
        _rows[i].IsBlast = blast;
    }
    internal string StatusText => _status.Text ?? "";
    internal string MonthText => _monthInfo.Text ?? "";
    internal int RowCount => _rows.Count;
}
