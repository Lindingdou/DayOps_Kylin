using System;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 周计划编制（移植原 <c>TaskLib.Features.WeekPlanWindow</c>）：一周七行，
/// 逐日「有效班 / 计划采装 / 计划排土 / 实绩 / 达成度 / 出处 / 状态」，可上下翻周。
///
/// 口径全在 <see cref="WeekPlanLink"/>（W1–W8），取数在 <see cref="WeekPlanSource"/>，
/// **本窗只负责显示与翻页** —— 与原版的分工一致。
///
/// 显示上守的一条：**"判不了"与"0"要分得开**。判不了的格子显示「—」并在"出处"列说明为什么；
/// 底部把逐条口径提示（跨月 / 无月计划 / 日历缺失 / 合计漏了几天）**一条不吞**地列出来。
/// </summary>
internal sealed class WeekPlanWindow : Window
{
    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;
    private DateTime _anchor = DateTime.Today;

    internal sealed class Row
    {
        public string Day { get; set; } = "";
        public string Shifts { get; set; } = "";
        public string PlanLoad { get; set; } = "";
        public string PlanDump { get; set; } = "";
        public string ActualLoad { get; set; } = "";
        public string Attainment { get; set; } = "";
        public string Basis { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };
    private readonly ObservableCollection<Row> _rows = new();
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#B26B00") };
    private readonly TextBlock _sum = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public WeekPlanWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "周计划编制";
        Width = 1020; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        static TextBlock Head(string t) => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };
        _grid.Columns.Add(C("日期", nameof(Row.Day), 120));
        _grid.Columns.Add(C("有效班", nameof(Row.Shifts), 80));
        _grid.Columns.Add(C("计划采装 m³", nameof(Row.PlanLoad), 130));
        _grid.Columns.Add(C("计划排土 m³", nameof(Row.PlanDump), 130));
        _grid.Columns.Add(C("实绩采出 m³", nameof(Row.ActualLoad), 130));
        _grid.Columns.Add(C("达成度", nameof(Row.Attainment), 90));
        _grid.Columns.Add(C("状态", nameof(Row.Status), 120));
        _grid.Columns.Add(new DataGridTextColumn
        { Header = Head("出处"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding(nameof(Row.Basis)) });
        _grid.ItemsSource = _rows;

        var prev = new Button { Content = "上一周", Padding = new Thickness(12, 3) };
        var today = new Button { Content = "本周", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        var next = new Button { Content = "下一周", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        var reload = new Button { Content = "重新载入", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        prev.Click += (_, _) => { _anchor = _anchor.AddDays(-7); Reload(); };
        next.Click += (_, _) => { _anchor = _anchor.AddDays(7); Reload(); };
        today.Click += (_, _) => { _anchor = DateTime.Today; Reload(); };
        reload.Click += (_, _) => Reload();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6),
            Children = { prev, today, next, reload },
        };
        var top = new StackPanel { Children = { bar, _header } };
        var bottom = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _notes, new Border { Height = 26, Background = Brush.Parse("#F3F3F3"), Margin = new Thickness(0, 6, 0, 0), Child = _sum } },
        };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        Reload();
    }

    private static string N(double? v) => v.HasValue ? v.Value.ToString("N0", CultureInfo.InvariantCulture) : "—";
    private static string Pct(double? v) => v.HasValue ? v.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";

    internal void Reload()
    {
        var res = WeekPlanLink.Compose(WeekPlanSource.Load(_conn(), _anchor, DateTime.Today));
        _rows.Clear();
        foreach (var d in res.Days)
            _rows.Add(new Row
            {
                Day = d.DayLabel + (d.IsToday ? "（今天）" : ""),
                Shifts = d.Shifts?.ToString(CultureInfo.InvariantCulture) ?? "—",
                PlanLoad = N(d.PlanLoadM3), PlanDump = N(d.PlanDumpM3),
                ActualLoad = N(d.ActualLoadM3), Attainment = Pct(d.AttainmentPct),
                Basis = d.Basis, Status = d.Status,
            });

        _header.Text = res.Header;
        _notes.Text = res.Notes.Count > 0 ? "◆ " + string.Join("\n◆ ", res.Notes) : "";
        _notes.IsVisible = res.Notes.Count > 0;
        _sum.Text = $"本周计划 采装 {res.PlanLoadSumM3:N0} m³ · 排土 {res.PlanDumpSumM3:N0} m³ · 实绩采出 {res.ActualLoadSumM3:N0} m³"
                  + $"　|　排班 {res.ScheduledDays}/7 天"
                  + (res.UnknownDays > 0 ? $"　|　{res.UnknownDays} 天判不出（合计未计入）" : "")
                  + (res.MonthSharePct is { } p ? $"　|　占本月 {p:0.#}%" : "")
                  + $"　|　{WeekPlanSource.CoalBasisText}";
    }

    // ── 自检钩子用 ──
    internal void SetAnchor(DateTime d) { _anchor = d; Reload(); }
    internal string HeaderText => _header.Text ?? "";
    internal string SumText => _sum.Text ?? "";
    internal string NotesText => _notes.Text ?? "";
    internal int RowCount => _rows.Count;
}
