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
/// 「班内工艺·工序推演」—— <b>任务（日 / 班）</b>尺度那一层。
///
/// <para>
/// 一个班里工序怎么互锁（穿孔备孔 → 爆破成堆 → 铲装上车 → 车拉去向 → 推土摊平）、
/// 每条工艺线的能力配比、<b>此刻系统卡在哪</b>（铲等车 / 车等铲 / 排土受限）。
/// 口径全在 <see cref="DayProcessPlan"/>（纯函数）。
/// </para>
/// <para>
/// <b>班内时钟是主控件</b>：拖到哪一刻，表里给的就是那一刻在跑的工艺线与那一刻的瓶颈。
/// </para>
/// <para>
/// <b>与月度那套不共用任何口径</b>：采排体积配对在一个班上必然报不守恒（挖了先堆在采场边、
/// 下一班才拉走），推进反算的分母是月度工作线长 —— 那些判据在班尺度上不成立，这里一条都不放。
/// </para>
/// </summary>
internal sealed class ShiftProcessWindow : Window
{
    private static readonly IBrush BadBrush = Brush.Parse("#DC2626");
    private static readonly IBrush WarnBrush = Brush.Parse("#D97706");
    private static readonly IBrush OkBrush = Brush.Parse("#16A34A");

    internal sealed class LineRow
    {
        public string Zone { get; set; } = "";
        public string Shovel { get; set; } = "";
        public string Trucks { get; set; } = "";
        public string Dest { get; set; } = "";
        public string Span { get; set; } = "";
        public string Target { get; set; } = "";
        public string Cap { get; set; } = "";
        public string Flow { get; set; } = "";
        public string Progress { get; set; } = "";
        public string State { get; set; } = "";
    }

    internal sealed class StepRow
    {
        public string Stage { get; set; } = "";
        public string Zone { get; set; } = "";
        public string Equip { get; set; } = "";
        public string Span { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _shift = new() { MinWidth = 110 };
    private readonly Slider _clock = new() { Minimum = 0, Maximum = 1, Value = 0, Width = 320 };
    private readonly TextBlock _clockText = new() { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _bottleneck = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#B26B00") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    private readonly DataGrid _lineGrid = NewGrid();
    private readonly DataGrid _stepGrid = NewGrid();
    private readonly ObservableCollection<LineRow> _lines = new();
    private readonly ObservableCollection<StepRow> _steps = new();

    private PlanAssembly _asm = new();
    private DayProcessPlan _day = new();

    private static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal ShiftProcessWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "班内工艺·工序推演";
        Width = 1320; Height = 620;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        BuildColumns();
        _lineGrid.ItemsSource = _lines;
        _stepGrid.ItemsSource = _steps;
        Content = BuildLayout();

        _shift.ItemsSource = new[] { "（未装配）" };
        _shift.SelectedIndex = 0;
        _shift.SelectionChanged += (_, _) => Rebind();
        _clock.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Rebind(); };
        _date.SelectedDateChanged += (_, _) => Reload();
        Reload();
    }

    private void BuildColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };

        _lineGrid.Columns.Add(C("作业面", nameof(LineRow.Zone), 132));
        _lineGrid.Columns.Add(C("电铲", nameof(LineRow.Shovel), 108));
        _lineGrid.Columns.Add(C("配车/荐车", nameof(LineRow.Trucks), 112));
        _lineGrid.Columns.Add(C("去向", nameof(LineRow.Dest), 132));
        _lineGrid.Columns.Add(C("时段", nameof(LineRow.Span), 122));
        _lineGrid.Columns.Add(C("本班目标", nameof(LineRow.Target), 116));
        _lineGrid.Columns.Add(C("班产 m³/h", nameof(LineRow.Cap), 112));
        _lineGrid.Columns.Add(C("车流 λ·n", nameof(LineRow.Flow), 150));
        _lineGrid.Columns.Add(C("此刻进度", nameof(LineRow.Progress), 104));
        _lineGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("此刻卡在哪"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(LineRow.State)),
        });

        DataGridTextColumn S(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };
        _stepGrid.Columns.Add(S("工序", nameof(StepRow.Stage), 96));
        _stepGrid.Columns.Add(S("作业地点", nameof(StepRow.Zone), 150));
        _stepGrid.Columns.Add(S("设备", nameof(StepRow.Equip), 120));
        _stepGrid.Columns.Add(S("时段", nameof(StepRow.Span), 130));
        _stepGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("说明"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(StepRow.Note)),
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
        P(new TextBlock { Text = "班内时钟", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        P(_clock);
        P(_clockText);
        P(new Border { Width = 10 });
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
                    Child = new StackPanel { Children = { _header, _bottleneck, _notes } },
                },
            },
        };

        var stepBox = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "工序位（穿孔 / 爆破 / 排土 / 空闲）", FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(12, 6, 12, 2) },
                new Border { Height = 150, Margin = new Thickness(12, 0), Child = _stepGrid },
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
        DockPanel.SetDock(stepBox, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(stepBox);
        root.Children.Add(new Border { Margin = new Thickness(12, 6), Child = _lineGrid });
        return root;
    }

    private DateTime CurrentDate => _date.SelectedDate?.Date ?? DateTime.Today;

    internal void Reload()
    {
        _asm = ProductionPlanContext.Assemble(_conn(), CurrentDate, DateTime.Now.TimeOfDay.TotalHours);
        var plan = _asm.Usable ? TaskExploder.Explode(_asm.Config) : new ExploderResult();
        _day = DayProcessPlan.Build(_asm.Config, plan, CurrentDate);

        var names = _day.Shifts.Select(s => s.ShiftName).ToList();
        string? keep = _shift.SelectedItem as string;
        _shift.ItemsSource = names.Count > 0 ? names : new List<string> { "（未装配）" };
        _shift.SelectedIndex = keep != null && names.Contains(keep) ? names.IndexOf(keep) : 0;

        Rebind();
    }

    private ShiftProcessSystem? Current
    {
        get
        {
            string? name = _shift.SelectedItem as string;
            return _day.Shifts.FirstOrDefault(s => string.Equals(s.ShiftName, name, StringComparison.Ordinal));
        }
    }

    private void Rebind()
    {
        var sys = Current;
        _lines.Clear();
        _steps.Clear();

        if (sys == null)
        {
            _clockText.Text = "—";
            _header.Text = $"{CurrentDate:yyyy-MM-dd}　{_asm.SourceLabel}";
            _bottleneck.Foreground = BadBrush;
            _bottleneck.Text = _asm.Usable ? "盘子里一个班次都没有。" : "排不出来 —— " + string.Join(" ", _asm.Notes);
            _notes.IsVisible = false;
            _status.Text = "没有可推演的班次。";
            return;
        }

        double t = sys.ClockAt(_clock.Value);
        _clockText.Text = DayProcessPlan.Hm(t);

        foreach (var l in sys.Lines)
        {
            bool on = l.ActiveAt(t);
            _lines.Add(new LineRow
            {
                Zone = l.Zone,
                Shovel = l.Shovel.Length > 0 ? l.Shovel : "—",
                Trucks = $"{l.Trucks.Count} / {(l.RecommendedTrucks > 0 ? l.RecommendedTrucks.ToString(CultureInfo.InvariantCulture) : "—")}",
                Dest = l.Destination.Length > 0 ? l.Destination : "未指定卸点",
                Span = $"{DayProcessPlan.Hm(l.StartHour)}–{DayProcessPlan.Hm(l.EndHour)}",
                Target = l.TargetM3 > 1e-9 ? l.TargetM3.ToString("N0", CultureInfo.InvariantCulture) + " m³" : "—",
                Cap = l.GroupCapacityM3PerH > 1e-9 ? l.GroupCapacityM3PerH.ToString("N0", CultureInfo.InvariantCulture) : "—",
                // ★ 在途车数不取整：n<1 时"每隔几分钟才有一台车在途"是真实状态
                Flow = l.TripsPerHour is { } lam && l.TrucksInTransit is { } n
                    ? $"λ {lam:0.##} 车/h · n {n:0.##} 台在途"
                    : "判不了（台账没解出单车载重或循环时间）",
                Progress = on ? (l.ProgressAt(t) * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "—",
                State = on ? l.BottleneckWhy : "此刻不在这条线的时段内",
            });
        }

        foreach (var s in sys.Steps.OrderBy(s => s.StartHour).ThenBy(s => (int)s.Stage))
            _steps.Add(new StepRow
            {
                Stage = s.Stage.Label() + (s.IsIdle ? "·空闲" : ""),
                Zone = s.Zone.Length > 0 ? s.Zone : "—",
                Equip = s.Equipment.Length > 0 ? s.Equipment : "—",
                Span = $"{DayProcessPlan.Hm(s.StartHour)}–{DayProcessPlan.Hm(s.EndHour)}",
                Note = s.IsIdle ? (s.IdleWhy.Length > 0 ? "空闲原因：" + s.IdleWhy : "空闲") : (s.ActiveAt(t) ? "此刻在干" : ""),
            });

        var b = sys.BottleneckAt(t);
        _bottleneck.Foreground = b == Bottleneck.None ? OkBrush : b == Bottleneck.NoFace ? BadBrush : WarnBrush;
        _bottleneck.Text = $"{DayProcessPlan.Hm(t)}　{sys.BottleneckCaption(t)}";

        _header.Text = $"{CurrentDate:yyyy-MM-dd} {sys.ShiftName} "
                     + $"{DayProcessPlan.Hm(sys.StartHour)}–{DayProcessPlan.Hm(sys.EndHour)}　"
                     + $"采装出方 {sys.LoadedM3:N0} m³ · 排土承接 {sys.DumpAcceptM3:N0} m³　|　{_asm.SourceLabel}";

        var notes = new List<string>(sys.Notes);
        notes.AddRange(_day.Notes);
        _notes.IsVisible = notes.Count > 0;
        _notes.Text = notes.Count > 0 ? "◆ " + string.Join("\n◆ ", notes) : "";

        _status.Text = $"工艺线 {sys.Lines.Count} 条 · 工序位 {sys.Steps.Count} 个。"
                     + "本层只留在班尺度上成立的判据 —— 采排配对、推进反算、期内配比都属月度那一层，这里一条不放。";
    }

    // ── 自检钩子用 ──
    internal int LineCount => _lines.Count;
    internal int StepCount => _steps.Count;
    internal string HeaderText => _header.Text ?? "";
    internal string BottleneckText => _bottleneck.Text ?? "";
    internal void SetClock(double phase) { _clock.Value = Math.Clamp(phase, 0, 1); Rebind(); }
}
