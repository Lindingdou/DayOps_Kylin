// 忠实移植自原 PitMine3D Modules/TaskLib/Features/ShiftCalendarWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data.Entities;     // ShiftCalendar
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 班次日历：A/B/C 班 · 爆破班 · 天气 —— 工作历的基础。
///
/// <para><b>本轮从「假窗口」改成真窗口的三件事</b>：</para>
/// <list type="number">
/// <item><b>读的是真源</b>：时段取 <see cref="ProductionPlanContext"/> 装配出来的那一份（引擎实际采用的班次窗口），
///   天气/爆破班/备注取台账行，两者按班次名对上。</item>
/// <item><b>删掉「有效班(h)」列</b>：有效时窗是引擎算的（班起 − 检修 − 爆破清场 − 交接班损失）。「班长」列同理删掉——那是班组派工窗的事。</item>
/// <item><b>补出「本月有效作业日」</b>：月→日裂解在除以它（<c>ShortTermLink.ResolveWorkdays</c>），原先除数是写死的 25。</item>
/// </list>
///
/// <para>
/// <b>保存是真写库</b>：按 日期 × 班次 upsert 回 <c>shift_calendar</c>。
/// 台账里本日没有记录时**一行都不画**：补法是工具条上的「生成整月…」——它按三班倒 + 逐月配置表的作业日数一次排满。
/// </para>
/// </summary>
public sealed class ShiftCalendarWindow : Window
{
    public sealed class Row
    {
        public string Date { get; set; } = "";
        public string Shift { get; set; } = "";
        public string Span { get; set; } = "";
        public bool IsBlast { get; set; }
        public string Weather { get; set; } = "";
        public string Notes { get; set; } = "";
        public string Origin { get; set; } = "";

        /// <summary>是不是工程当前工作日期那一天 —— 整行加粗标出来。
        /// <b>不往日期文字里塞「←今天」</b>：那会把日期列撑爆，日期本身反而被截断。</summary>
        public bool IsToday { get; set; }

        /// <summary>台账里对应的那一行；null = 本日台账没有这个班（保存时新建）。</summary>
        public ShiftCalendar? Entity;

        /// <summary>班次开始时刻（写库用；台账没有时取引擎采用的窗口起点）。</summary>
        public double StartHour;
    }

    private DateTime _date;
    private List<Row> _rows = new();

    private readonly TextBlock monthText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 88, TextAlignment = TextAlignment.Center, FontWeight = FontWeight.Bold };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock workdayLine = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Bold };
    private readonly TextBlock workdayNote = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
    private readonly TextBlock derateLine = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
    private readonly DataGrid grid = TaskUi.Grid(readOnly: false);

    public ShiftCalendarWindow()
    {
        Title = "班次日历 — 日常生产组织";
        TaskUi.Place(this, 1040, 640);

        var header = TaskUi.Header("班次日历", "A/B/C 班 · 爆破班 · 天气 — 工作历的基础（喂编制裂解的有效作业日）");

        // 月份导航：这个窗叫「班次日历」，就该看得见整个月。
        var tool = new DockPanel { LastChildFill = true };
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); tool.Children.Add(c); }
        var prev = TaskUi.Btn("‹", () => ShiftMonth(-1), 30); prev.Margin = new Thickness(0); ToolTip.SetTip(prev, "上一月"); L(prev);
        TaskUi.Theme(monthText, TextBlock.ForegroundProperty, "Theme.Text.Body"); L(monthText);
        var next = TaskUi.Btn("›", () => ShiftMonth(+1), 30); next.Margin = new Thickness(0, 0, 10, 0); ToolTip.SetTip(next, "下一月"); L(next);
        var build = TaskUi.Btn("生成整月…", OnBuildMonth, 96, bold: true); build.Margin = new Thickness(0);
        ToolTip.SetTip(build, "按【三班倒】把整月一次排出来。\n\n· 作业日数取自「逐月配置表」——**不是这里编的**；\n· 只有「哪几天休」是规则（不休 / 周日休 / 均匀摊）；\n· 班次名 A/B/C 整天均分（库上有 CHECK 约束，只认这三档）；\n· **已排过的日子原样保留**，只补缺；\n· 爆破班一律留空 —— 哪一班放炮是逐日定的，猜出来会让那一班凭空少 1.5 小时清场工时。");
        L(build);
        var save = TaskUi.Btn("保存日历", OnSave, 100); save.Margin = new Thickness(0); L(save);
        var reload = TaskUi.Btn("重新载入", OnReload, 90); reload.Margin = new Thickness(10, 0, 0, 0); L(reload);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        tool.Children.Add(toolStatus);

        // 本月有效作业日：月→日裂解的除数。它不是这张表里的一列，而是整张表的汇总量，故单独成条
        var info = new StackPanel();
        TaskUi.Theme(workdayLine, TextBlock.ForegroundProperty, "Theme.Text.Body");
        TaskUi.Theme(workdayNote, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        TaskUi.Theme(derateLine, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        info.Children.Add(workdayLine); info.Children.Add(workdayNote); info.Children.Add(derateLine);
        var infoBox = new Border { Margin = new Thickness(10, 10, 10, 0), Padding = new Thickness(12, 9), BorderThickness = new Thickness(1), Child = info };
        TaskUi.Theme(infoBox, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(infoBox, Border.BorderBrushProperty, "Theme.Surface.Border");

        grid.Columns.Add(TaskUi.TextCol("日期", nameof(Row.Date), 120));
        grid.Columns.Add(TaskUi.TextCol("班次", nameof(Row.Shift), 70));
        // 时段是引擎实际采用的有效时窗起止，只读：改时段要改的是台账里的开班时刻，不是在这里改一个显示值
        grid.Columns.Add(TaskUi.TextCol("时段", nameof(Row.Span), 110));
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = TaskUi.Head("爆破班"), Binding = new Binding(nameof(Row.IsBlast)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(70) });
        grid.Columns.Add(TaskUi.TextCol("天气", nameof(Row.Weather), 90, readOnly: false));
        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("备注"), Binding = new Binding(nameof(Row.Notes)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var origin = TaskUi.TextCol("来源", nameof(Row.Origin), 130); origin.Foreground = TaskUi.Muted; grid.Columns.Add(origin);
        // 今天那一行整行加粗 —— 不往日期文字里塞「←今天」
        grid.LoadingRow += (_, e) =>
        {
            bool today = e.Row.DataContext is Row r && r.IsToday;
            e.Row.FontWeight = today ? FontWeight.Bold : FontWeight.Normal;
            if (today) TaskUi.Theme(e.Row, DataGridRow.BackgroundProperty, "Theme.Surface.Background"); else e.Row.ClearValue(DataGridRow.BackgroundProperty);
        };

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(infoBox, 2); Grid.SetRow(grid, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(infoBox); g.Children.Add(grid);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        Opened += (_, _) => Fill();
    }

    /// <summary>开窗/重新载入：回到工程当前工作日期所在月。</summary>
    private void Fill()
    {
        _date = ProjectScope.WorkDate;
        FillMonth();
    }

    /// <summary>装载 <see cref="_date"/> 所在的<b>整月</b>。翻月只调这一个，不动工作日期。</summary>
    private void FillMonth()
    {
        // ① 引擎实际采用的班次窗口（台账 → 样例三班的兜底已在装配层做过，此处不重做）
        List<ShiftWindow> windows;
        string shiftSource;
        try
        {
            var cfg = ProductionPlanContext.Config();
            windows = cfg.Shifts.ToList();
            shiftSource = ProductionPlanContext.ShiftSourceLabel;
        }
        catch (Exception ex)
        {
            windows = new List<ShiftWindow>();
            shiftSource = $"班次：盘子装配失败（{Short(ex)}）";
        }

        // ② 台账行（天气/爆破班/备注的真源）—— **整月**，不是一天。
        var first = new DateTime(_date.Year, _date.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        string ledgerErr = "";
        List<ShiftCalendar> ledger;
        try
        {
            ledger = PitMine3D.Kylin.Data.EquipmentDataContext.ShiftCalendar
                     .InRange(first, last).Where(r => r != null).ToList();
        }
        catch (Exception ex) { ledger = new List<ShiftCalendar>(); ledgerErr = ex.Message; }

        // ★ 只显示**台账里真有的**班次：没排班就一行不画，并在状态栏说清楚怎么补。
        // ★ 收班时刻**按当日相邻班的开班时刻推**：shift_calendar 只存开班一列，没有收班。
        _rows = ledger
            .OrderBy(r => r.Date.Date)
            .ThenBy(r => ParseHour(r.StartTime))
            .ThenBy(r => (r.Shift ?? "").Trim(), StringComparer.Ordinal)
            .Select(r =>
            {
                double h0 = ParseHour(r.StartTime);
                double h1 = NextStartSameDay(ledger, r.Date.Date, h0);
                return new Row
                {
                    Date = r.Date.ToString("yyyy-MM-dd"),
                    IsToday = r.Date.Date == _date.Date,
                    Shift = WorkCalendar.ShiftName(r.Shift),
                    Span = h0 < 0 ? "（未填开班时刻）" : $"{Hm(h0)}–{Hm(h1)}",
                    StartHour = h0 < 0 ? 0 : h0,
                    IsBlast = r.IsBlastShift,
                    Weather = r.Weather ?? "",
                    Notes = r.Notes ?? "",
                    Origin = "班次日历台账",
                    Entity = r,
                };
            }).ToList();

        grid.ItemsSource = null;
        grid.ItemsSource = _rows;

        monthText.Text = _date.ToString("yyyy-MM");
        int nDays = _rows.Select(r => r.Entity!.Date.Date).Distinct().Count();
        toolStatus.Text = ledgerErr.Length > 0
            ? $"◆ 班次日历读取失败（{ledgerErr}）"
            : _rows.Count > 0
                ? $"{_date:yyyy-MM} 排了 {nDays} 天 · {_rows.Count} 班（全部来自班次日历台账）"
                : $"◆ {_date:yyyy-MM} **一天都没排** —— 点「生成整月…」把这个月一次排出来。"
                  + "这里**不再显示样例三班**：样例行和真行长得一模一样，"
                  + "而保存它等于把一份编出来的班制当成矿上的班制。";
        ToolTip.SetTip(toolStatus, toolStatus.Text + "\n" + shiftSource);

        FillWorkdaySummary();
    }

    /// <summary>"HH:mm" → 小时（解不出来返回 -1，<b>不是 0</b>：0 点是合法开班时刻）。</summary>
    private static double ParseHour(string? t)
    {
        var s = (t ?? "").Trim();
        if (s.Length == 0) return -1;
        if (TimeSpan.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return ts.TotalHours;
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : -1;
    }

    /// <summary>
    /// 同一天里<b>下一个班的开班时刻</b>；没有下一个就收在 24 点。
    /// <para>这就是收班时刻的定义 —— 台账里没有这一列，引擎也是这么推的。</para>
    /// </summary>
    private static double NextStartSameDay(List<ShiftCalendar> all, DateTime day, double h0)
    {
        double best = 24.0;
        foreach (var r in all)
        {
            if (r.Date.Date != day) continue;
            double h = ParseHour(r.StartTime);
            if (h > h0 + 1e-9 && h < best) best = h;
        }
        return best;
    }

    private static string Hm(double h)
    {
        if (h < 0) return "—";
        int hh = (int)Math.Floor(h), mm = (int)Math.Round((h - hh) * 60);
        if (mm >= 60) { hh++; mm -= 60; }
        return $"{hh:00}:{mm:00}";
    }

    /// <summary>本月有效作业日 + 与月计划口径的对照 + 当前天气降效锚点。</summary>
    private void FillWorkdaySummary()
    {
        var info = WorkCalendar.MonthWorkdays(_date);

        if (info.FromLedger)
        {
            workdayLine.Text = $"本月有效作业日：{info.Workdays} 天　（{info.MonthLabel} · 日历口径）";
            workdayNote.Text = $"{info.ShiftRows} 条班次记录"
                             + (info.DaysWithoutTime > 0 ? $" · 其中 {info.DaysWithoutTime} 天未填开班时刻，装箱排不出时窗" : "")
                             + "。月→日裂解按这个数摊：日采出 = 月采出 ÷ 作业日。";
        }
        else
        {
            workdayLine.Text = $"本月有效作业日：未知（{info.MonthLabel}）";
            workdayNote.Text = info.Label
                             + $"。裂解将退回月计划自带的作业日，两者都没有时按兜底 {WorkCalendar.FallbackMonthWorkdays:0} 天——"
                             + "那个数是拍的，请把本月排班补进日历。";
        }

        // 月计划那一侧的口径（有月计划才显示，没有就不提，免得像是这里出了错）
        try
        {
            var mi = ShortTermLink.GetMonthInfo(ProjectScope.DateLabel);
            if (mi.HasPlan && mi.Workdays > 0)
            {
                string cmp = info.FromLedger
                    ? Math.Abs(mi.Workdays - info.Workdays) < 0.5
                        ? "两个口径一致"
                        : $"两个口径差 {Math.Abs(mi.Workdays - info.Workdays):0} 天 —— 日目标按日历口径折算，差额会在「计划校核」里列出"
                    : "日历口径缺失，裂解按月计划口径";
                workdayNote.Text += $"\n月计划「{mi.PlanName}」{mi.MonthLabel} 按 {mi.Workdays:0} 个作业日编制；{cmp}。";
            }
        }
        catch { /* 月计划读不到不影响本窗口主业 */ }

        double derate = CompileOverrides.Current.WeatherDeratePct ?? 0;
        derateLine.Text = derate > 0.01
            ? $"当前天气降效：{derate:0.#}%（在「编制配置」里改）—— 全盘班产 × {1 - derate / 100:0.00}，当日干不完的量报「当日欠产」回摊。"
            : "当前天气降效：0%（在「编制配置」里改）—— 本表的「天气」列只是记录，降效幅度由那个锚点决定，不从天气文字自动推。";
    }

    // ── 保存：按 日期 × 班次 upsert 回 shift_calendar ────────────────────────────

    /// <summary>翻月。<b>只改这个窗看的是哪一月，不动工程的工作日期</b> —— 那是全局状态。</summary>
    private void ShiftMonth(int delta)
    {
        var d = _date == default ? ProjectScope.WorkDate : _date;
        _date = new DateTime(d.Year, d.Month, 1).AddMonths(delta);
        FillMonth();
    }

    /// <summary>
    /// 「生成整月」—— 按三班倒把整月一次排出来。作业日数不在这里编：取自「逐月配置表」。这里只决定"哪几天休"。
    /// </summary>
    private async void OnBuildMonth()
    {
        var when = _date == default ? ProjectScope.WorkDate : _date;

        // 作业日数：逐月配置表 —— 取不到就明说取不到，按全月作业排
        double target = 0; string targetFrom = "取不到（按规则算）";
        try
        {
            var mi = ShortTermLink.GetMonthInfo(when.ToString("yyyy-MM-dd"));
            if (mi != null && mi.HasPlan && mi.Workdays > 0)
            { target = mi.Workdays; targetFrom = $"逐月配置表 {mi.MonthLabel} 的 {mi.Workdays:0} 天"; }
        }
        catch { }

        // 库里已经有的日子 —— 原样保留，只补缺
        var existing = new List<DateTime>();
        try
        {
            var f = new DateTime(when.Year, when.Month, 1);
            existing = PitMine3D.Kylin.Data.EquipmentDataContext.ShiftCalendar
                       .InRange(f, f.AddMonths(1).AddDays(-1))
                       .Where(x => x != null).Select(x => x.Date.Date).Distinct().ToList();
        }
        catch { }

        // 规则：给了作业日数就均匀摊休（对得上配置表），否则不休
        var rule = target > 0 ? RestRule.Even : RestRule.Continuous;
        var built = ShiftCalendarBuilder.Build(when.Year, when.Month, shiftsPerDay: 3,
                                               rule, target, existing);

        string msg = built.Headline
                   + $"\n\n作业日数来源：{targetFrom}"
                   + (built.Notes.Count > 0 ? "\n\n· " + string.Join("\n· ", built.Notes) : "");

        if (!built.Ok)
        { await TaskUi.Info(this, "生成整月", msg); return; }

        if (!await TaskUi.Confirm(this, "生成整月", msg + "\n\n写入库？（已排过的日子不会被覆盖）")) return;

        int ok = 0; var errs = new List<string>();
        foreach (var row in built.Rows)
        {
            string err = WorkCalendar.Upsert(row);
            if (err.Length == 0) ok++;
            else if (errs.Count < 3) errs.Add(err);
        }

        toolStatus.Text = $"已写入 {ok}/{built.Rows.Count} 条"
                        + (errs.Count > 0 ? "　◆ " + string.Join("；", errs) : "")
                        + (built.KeptDays.Count > 0 ? $"　· {built.KeptDays.Count} 天已排过，保留" : "");
        if (errs.Count > 0)
            await TaskUi.Info(this, "生成整月", $"有 {built.Rows.Count - ok} 条没写进去：\n" + string.Join("\n", errs));
        FillMonth();      // 留在刚生成的那一月，不跳回工作日期
    }

    private void OnSave()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        if (_rows.Count == 0) { toolStatus.Text = "没有可保存的班次。"; return; }

        int updated = 0, created = 0;
        var errors = new List<string>();

        foreach (var r in _rows)
        {
            var ent = r.Entity ?? new ShiftCalendar
            {
                Date = _date.Date,
                Shift = WorkCalendar.ShiftCode(r.Shift),
                StartTime = TimeText(r.StartHour),
            };
            bool isNew = r.Entity == null;

            ent.IsBlastShift = r.IsBlast;
            ent.Weather = string.IsNullOrWhiteSpace(r.Weather) ? null : r.Weather.Trim();
            ent.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();

            string err = WorkCalendar.Upsert(ent);
            if (err.Length > 0) { errors.Add($"{r.Shift}：{err}"); continue; }

            if (isNew) { created++; r.Entity = ent; r.Origin = "班次日历台账"; }
            else updated++;
        }

        grid.ItemsSource = null; grid.ItemsSource = _rows;

        toolStatus.Text = errors.Count > 0
            ? $"保存部分失败：更新 {updated} · 新建 {created} · 失败 {errors.Count}（{errors[0]}）"
            : created > 0
                ? $"已保存：更新 {updated} 班 · 新建 {created} 班（新建的班次时刻取自当前引擎窗口，如与实际排班不符请在此基础上核对）"
                : $"已保存：更新 {updated} 班";

        // 作业日/班次窗口都可能因这次保存而变，盘子作废重算
        ProductionPlanContext.Invalidate();
        FillWorkdaySummary();
    }

    private void OnReload()
    {
        ProductionPlanContext.Invalidate();
        FillMonth();      // 重新载入当前查看的那一月
    }

    private static string TimeText(double hour)
    {
        int h = (int)Math.Floor(hour);
        int m = (int)Math.Round((hour - h) * 60);
        if (m >= 60) { h++; m -= 60; }
        if (h >= 24) { h = 23; m = 59; }
        return $"{h:00}:{m:00}";
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
