using System;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「钻爆计划衔接」—— 逐炮排程 → 停产清场窗口 → 衔接采装面。钻爆设计在专业模块。
///
/// <para>
/// 口径全在 <see cref="BlastPlanLink"/>（B1–B7），本窗口只负责显示、翻页与穿孔计划录入。
/// 下半张表是 <c>drill_plan</c>（V044）的<b>唯一入口</b>：表建好没有入口，等于那张表永远没人填
/// （班次日历吃过这个亏）。工序链的两端本来就要对着看，所以两张表放在一个窗里。
/// </para>
///
/// ── 与原版的差异（登记）──
/// <list type="number">
///   <item>原版穿孔计划是<b>在格子里直接改</b>（可编辑 DataGrid + 保存按钮）；这里照 §三三三 检修档期
///     那一版已经上过屏的做法：上方表单填、下方只读表看、加入/删除两个按钮。Avalonia 的 DataGrid
///     行内编辑另有 TextTrimming 与提交时机两处坑，没必要为这一张表再踩一遍。</item>
///   <item>原版还有「按本期计划生成」（从班组计划分解器批量写穿孔计划）—— 依赖未移植的排产分解器，
///     <b>未移</b>，故 Kylin 侧只有手工录入这一条写入路径（也就没有"两个写入方互相覆盖"那个问题）。</item>
///   <item>「进装箱时窗」一列在 Kylin 侧一律<b>判不了</b>：排产装配层未移植（理由见
///     <see cref="BlastPlanInputs.EngineKnown"/>）。判不了不写成"未进"。</item>
/// </list>
/// </summary>
internal sealed class BlastPlanWindow : Window
{
    private static readonly IBrush OkBrush = Brush.Parse("#16A34A");
    private static readonly IBrush WarnBrush = Brush.Parse("#D97706");
    private static readonly IBrush BadBrush = Brush.Parse("#DC2626");
    private static readonly IBrush DimBrush = Brush.Parse("#8A8A8A");
    private static readonly IBrush TextBrush = Brush.Parse("#303030");

    /// <summary>逐炮一行。</summary>
    internal sealed class Row
    {
        public string Seq { get; set; } = "";
        public string Time { get; set; } = "";
        public string Location { get; set; } = "";
        public string Material { get; set; } = "";
        public string DrillId { get; set; } = "";
        public string DrillPlanText { get; set; } = "";
        public IBrush DrillBrush { get; set; } = DimBrush;
        public string HolesMeters { get; set; } = "";
        public string ExplosiveUnit { get; set; } = "";
        public string Volume { get; set; } = "";
        public string Stop { get; set; } = "";
        public string ShiftText { get; set; } = "";
        public string Face { get; set; } = "";
        public IBrush FaceBrush { get; set; } = DimBrush;
        public string Status { get; set; } = "";
        public IBrush StatusBrush { get; set; } = DimBrush;
        public string NoteShort { get; set; } = "";
        public string Note { get; set; } = "";
        public IBrush NoteBrush { get; set; } = DimBrush;
    }

    /// <summary>穿孔计划一行（只读展示；录入走上方表单）。</summary>
    internal sealed class DrillRow
    {
        public string Equip { get; set; } = "";
        public string Zone { get; set; } = "";
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
        public string Shift { get; set; } = "";
        public string Bench { get; set; } = "";
        public string Holes { get; set; } = "";
        public string Meters { get; set; } = "";
        public string Status { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private DateTime _date = DateTime.Today;

    private readonly TextBlock _dateText = new()
    { FontWeight = FontWeight.SemiBold, MinWidth = 110, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _toolStatus = new() { FontSize = 11, Foreground = Brush.Parse("#555"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#B26B00") };
    private readonly TextBlock _drillStatus = new() { FontSize = 11, Foreground = Brush.Parse("#555"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private readonly DataGrid _grid = NewGrid();
    private readonly DataGrid _drillGrid = NewGrid();
    private readonly ObservableCollection<Row> _rows = new();
    private readonly ObservableCollection<DrillRow> _drillRows = new();

    // 穿孔计划录入表单
    private readonly TextBox _fEquip = new() { Width = 110, Watermark = "钻机编号" };
    private readonly TextBox _fZone = new() { Width = 130, Watermark = "待爆区/平盘" };
    private readonly TextBox _fStart = new() { Width = 78, Text = "08:00" };
    private readonly TextBox _fEnd = new() { Width = 78, Text = "16:00" };
    private readonly TextBox _fBench = new() { Width = 78, Watermark = "标高" };
    private readonly TextBox _fHoles = new() { Width = 66, Watermark = "孔数" };
    private readonly TextBox _fMeters = new() { Width = 84, Watermark = "单孔 m" };
    private readonly ComboBox _fStatus = new() { Width = 92, ItemsSource = DrillPlanStore.Statuses, SelectedIndex = 0 };
    private readonly TextBox _fNote = new() { Width = 140, Watermark = "备注" };

    private BlastPlanResult _res = new();

    private static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal BlastPlanWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "钻爆计划衔接";
        // 十三列一行摆得下才有意义（列宽预算见 BuildColumns）。CenterScreen 同原窗：
        // 这张表比主窗还宽，按 Owner 居中会把右半截推到屏幕外（截图核对时右边三列整段看不见）。
        Width = 1560; Height = 620;
        // ★ Manual 而不是 Center*：十三列的表比主窗还宽，居中（无论对屏还是对宿主）都会把右半截
        //   推到屏幕外，而右边那三列（衔接采装面 / 状态 / 进装箱时窗）恰恰是本窗最有信息量的。
        //   §三四五 记过"在 Opened 里设 Position 不生效"—— 那是 CenterOwner 之后又被摆了一次；
        //   起始位置设成 Manual 之后 WindowFit 的回拉才真正落得下来。
        WindowStartupLocation = WindowStartupLocation.Manual;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        BuildColumns();
        _grid.ItemsSource = _rows;
        _drillGrid.ItemsSource = _drillRows;
        Content = BuildLayout();
        Reload();
    }

    private void BuildColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };

        // 逐行上色只能走模板列（DataGridTextColumn 的 Foreground 是整列一个值）
        DataGridTemplateColumn Painted(string h, string textPath, string brushPath, DataGridLength w, bool bold = false)
            => new()
            {
                Header = Head(h), Width = w, IsReadOnly = true,
                CellTemplate = new FuncDataTemplate<Row>((_, _) =>
                {
                    var tb = new TextBlock
                    {
                        Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    if (bold) tb.FontWeight = FontWeight.SemiBold;
                    tb.Bind(TextBlock.TextProperty, new Binding(textPath));
                    tb.Bind(TextBlock.ForegroundProperty, new Binding(brushPath));
                    tb.Bind(ToolTip.TipProperty, new Binding(nameof(Row.Note)));
                    return tb;
                }, supportsRecycling: true),
            };

        // ★ 列宽按**表头与格子里最长的那句话**给，逐列在实机截图上量过：
        //   56 的「炮次」会被切成「炮」、84 的「爆破时刻」切成「爆破时」、
        //   104 的「停产清场」放不下格子里的「本日无停产窗口」。
        //   表头包了 TextTrimming.None 也照样切 —— 那个只是不加省略号，不会把列撑宽。
        _grid.Columns.Add(C("炮次", nameof(Row.Seq), 70));
        _grid.Columns.Add(C("爆破时刻", nameof(Row.Time), 98));
        _grid.Columns.Add(C("平盘/爆区", nameof(Row.Location), 132));
        _grid.Columns.Add(C("物料", nameof(Row.Material), 112));
        _grid.Columns.Add(C("钻机", nameof(Row.DrillId), 78));
        _grid.Columns.Add(C("孔数/延米", nameof(Row.HolesMeters), 112));
        _grid.Columns.Add(C("装药kg(单耗)", nameof(Row.ExplosiveUnit), 126));
        _grid.Columns.Add(C("方量 m³", nameof(Row.Volume), 98));
        _grid.Columns.Add(Painted("穿孔计划", nameof(Row.DrillPlanText), nameof(Row.DrillBrush), new DataGridLength(130)));
        _grid.Columns.Add(C("停产清场", nameof(Row.Stop), 142));
        _grid.Columns.Add(Painted("衔接采装面", nameof(Row.Face), nameof(Row.FaceBrush),
                                  new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(Painted("状态", nameof(Row.Status), nameof(Row.StatusBrush), new DataGridLength(88)));
        // 短文案进格子、整句进 ToolTip：这一列被切一半就等于没有
        _grid.Columns.Add(Painted("进装箱时窗", nameof(Row.NoteShort), nameof(Row.NoteBrush),
                                  new DataGridLength(1, DataGridLengthUnitType.Star)));

        DataGridTextColumn D(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };
        _drillGrid.Columns.Add(D("钻机", nameof(DrillRow.Equip), 110));
        _drillGrid.Columns.Add(D("待爆区/平盘", nameof(DrillRow.Zone), 140));
        _drillGrid.Columns.Add(D("起", nameof(DrillRow.Start), 74));
        _drillGrid.Columns.Add(D("止", nameof(DrillRow.End), 74));
        _drillGrid.Columns.Add(D("班次", nameof(DrillRow.Shift), 92));
        _drillGrid.Columns.Add(D("台阶 m", nameof(DrillRow.Bench), 84));
        _drillGrid.Columns.Add(D("孔数", nameof(DrillRow.Holes), 68));
        _drillGrid.Columns.Add(D("单孔 m", nameof(DrillRow.Meters), 84));
        _drillGrid.Columns.Add(D("状态", nameof(DrillRow.Status), 84));
        _drillGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("备注"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(DrillRow.Note)),
        });
    }

    private static Button B(string t, Action a, bool bold = false, string? tip = null)
    {
        var b = new Button { Content = t, Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 6, 0) };
        if (bold) b.FontWeight = FontWeight.SemiBold;
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => a();
        return b;
    }

    private Control BuildLayout()
    {
        var bar = new WrapPanel { Margin = new Thickness(12, 8, 12, 4) };
        void P(Control c) => bar.Children.Add(c);
        P(B("‹", () => Shift(-1)));
        P(_dateText);
        P(B("›", () => Shift(+1)));
        P(B("作业日", () => { _date = DateTime.Today; Reload(); }, tip: "回到当前作业日"));
        P(B("刷新", Reload));
        P(_toolStatus);

        var top = new StackPanel
        {
            Children =
            {
                bar,
                new Border
                {
                    Background = Brush.Parse("#F5F7FA"), Padding = new Thickness(12, 6),
                    BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 1),
                    Child = new StackPanel { Children = { _header, _notes } },
                },
            },
        };

        // ── 穿孔作业计划（V044 drill_plan）：全仓唯一入口 ──
        var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void F(Control c) => form.Children.Add(c);
        TextBlock L(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = 12 };
        F(new TextBlock { Text = "穿孔作业计划", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        F(_fEquip); F(L(" 区")); F(_fZone);
        F(L(" 起")); F(_fStart); F(L(" 止")); F(_fEnd);
        F(L(" 台阶")); F(_fBench); F(_fHoles); F(_fMeters); F(_fStatus); F(_fNote);
        var form2 = new WrapPanel();
        form2.Children.Add(B("加入计划", AddDrill, bold: true,
            tip: "待爆区要与爆破台账的平盘编码对得上，否则衔接不上。\n台阶/孔数/单孔延米留空 = 未录（0 是合法值，不拿 0 冒充）。"));
        form2.Children.Add(B("删除选中", DeleteDrill));
        form2.Children.Add(_drillStatus);

        var drillBox = new StackPanel
        {
            Children =
            {
                new Border
                {
                    Background = Brush.Parse("#FAFAFA"), Padding = new Thickness(12, 6),
                    BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
                    Child = new StackPanel { Children = { form, form2 } },
                },
                new Border { Height = 150, Margin = new Thickness(12, 4), Child = _drillGrid },
            },
        };

        var footBar = new Border
        {
            Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(12, 6, 12, 8),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { B("关闭", Close) },
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(drillBox, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(drillBox);
        root.Children.Add(new Border { Margin = new Thickness(12, 6), Child = _grid });
        return root;
    }

    private void Shift(int days) { _date = _date.AddDays(days); Reload(); }

    // ═══════════════════ 取数与显示 ═══════════════════

    internal void Reload()
    {
        _dateText.Text = _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        double nowHour = DateTime.Now.TimeOfDay.TotalHours;
        _res = BlastPlanLink.Build(_conn(), _date, nowHour);

        _rows.Clear();
        foreach (var s in _res.Shots) _rows.Add(ToRow(s));

        _header.Text = _res.Header;
        _notes.Text = _res.Notes.Count > 0
            ? "◆ " + string.Join("\n◆ ", _res.Notes)
            : "采装/排土作业止于爆破前，清场解除后恢复；本表的停产窗口与装箱扣掉的是同一个常数（清场 "
              + $"{BlastPlanLink.ClearanceH * 60:0} 分钟）。";
        _notes.IsVisible = true;

        // ★ 没有炮的时候不报「N 炮未对上作业面」：表里那一行是兜底窗口，不是炮。
        //   原窗这两个计数是无条件显示的（兜底行的 Linked 也是 false，于是恒报 1 炮未对上）——
        //   本日一炮都没有却说"1 炮未对上"，是句会让人去查台账的假话。登记的差异。
        _toolStatus.Text = _res.FromLedger
            ? $"爆破台账 {_res.Shots.Count} 炮"
              + (_res.Unlinked > 0 ? $"　|　{_res.Unlinked} 炮未对上作业面" : "")
              + (_res.EngineUnjudged > 0 ? $"　|　{_res.EngineUnjudged} 炮「进没进计划」判不了" : "")
            : "爆破台账本日无记录（表内为兜底窗口，不是炮次）";

        ReloadDrills();
    }

    private static Row ToRow(BlastShotRow s)
    {
        bool notInWindow = s.EngineJudged && s.Hour.HasValue && !s.DrivesEngine;
        return new Row
        {
            Seq = s.Seq?.ToString(CultureInfo.InvariantCulture) ?? "—",
            Time = s.TimeText,
            Location = s.Location.Length > 0 ? s.Location : "—",
            Material = s.MaterialText.Length > 0 ? s.MaterialText : "—",
            DrillId = s.DrillId.Length > 0 ? s.DrillId : "—",
            DrillPlanText = s.DrillText,
            DrillBrush = s.HasDrill ? TextBrush : WarnBrush,
            HolesMeters = Pair(Num(s.HoleCount), Num(s.HoleMeters, "N0")),
            ExplosiveUnit = s.UnitKgM3 is > 1e-9
                ? $"{Num(s.ExplosiveKg, "N0")}（{s.UnitKgM3.Value:0.###}）"
                : Num(s.ExplosiveKg, "N0"),
            Volume = Num(s.VolumeM3, "N0"),
            Stop = s.StopWindow,
            ShiftText = s.Shift.Length > 0 ? $"落在 {s.Shift}" : "判不出落在哪个班",
            Face = s.LinkedFace,
            FaceBrush = s.Linked ? TextBrush : WarnBrush,
            Status = s.Status,
            StatusBrush = s.Status.StartsWith("待爆", StringComparison.Ordinal) ? WarnBrush
                        : s.Status == "清场中" ? BadBrush
                        : s.Status == "已爆" ? OkBrush
                        : DimBrush,
            NoteShort = !s.EngineJudged ? "判不了（无盘子）"
                      : s.DrivesEngine ? "✓ 已进（按它扣停产）"
                      : notInWindow ? "⚠ 未进（清场没从计划里扣）"
                      : s.Note.Length > 0 ? "⚠ 排不进时窗" : "—",
            Note = s.Note.Length > 0 ? s.Note
                 : s.DrivesEngine ? "装箱按这一炮扣停产：采装/排土作业止于爆破前，清场解除后恢复。" : "—",
            NoteBrush = !s.EngineJudged ? DimBrush
                      : s.DrivesEngine ? OkBrush
                      : notInWindow ? BadBrush : DimBrush,
        };
    }

    // ═══════════════════ 穿孔作业计划读写 ═══════════════════

    private void ReloadDrills()
    {
        _drillRows.Clear();
        var rows = DrillPlanStore.ByDate(_conn(), _date, out string err);
        if (err.Length > 0)
        {
            _drillStatus.Text = $"穿孔计划表读不到（{err}）—— 数据库未就绪时排了也存不进去";
            return;
        }

        var shifts = MaintenanceWindows.ShiftWindowsOf(WorkCalendar.Day(_conn()!, _date, out _));
        foreach (var r in rows)
            _drillRows.Add(new DrillRow
            {
                Equip = r.EquipmentId, Zone = r.Zone, Start = r.StartTime, End = r.EndTime,
                Shift = ShiftNameOf(shifts, r.StartTime),
                Bench = r.BenchElevationM?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—",
                Holes = r.HoleCount?.ToString(CultureInfo.InvariantCulture) ?? "—",
                Meters = r.HoleLengthM?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—",
                Status = r.Status, Note = r.Note ?? "",
            });

        _drillStatus.Text = _drillRows.Count == 0
            ? $"{_date:MM-dd} 未排穿孔 —— 排一条，钻机才会进甘特与工序进度（起止 HH:mm；台阶/孔数留空=未录）"
            : $"{_date:MM-dd} 共 {_drillRows.Count} 条穿孔计划";
    }

    /// <summary>
    /// 这条钻孔排在哪个班（由起始时刻按当日班制推）。穿孔计划本身按时刻排，但下游（甘特、
    /// 工序进度、实绩录入）全按班组织 —— 排的时候看不见班次，很容易把一条活排在交接线上。
    /// 推不出来就写"判不出"，不猜一个班。
    /// </summary>
    private static string ShiftNameOf(System.Collections.Generic.IReadOnlyList<ShiftWindow> shifts, string? hhmm)
    {
        double? h = MaintenanceWindows.Hour(hhmm);
        if (h is null) return "—";
        foreach (var s in shifts)
            if (h.Value >= s.Start && h.Value < s.End) return s.Name;
        return shifts.Count == 0 ? "无日历" : "判不出";
    }

    private void AddDrill()
    {
        var row = new DrillPlanRow
        {
            EquipmentId = (_fEquip.Text ?? "").Trim(),
            PlanDate = DrillPlanStore.D(_date),
            StartTime = (_fStart.Text ?? "").Trim(),
            EndTime = (_fEnd.Text ?? "").Trim(),
            Zone = (_fZone.Text ?? "").Trim(),
            BenchElevationM = Opt(_fBench.Text),
            HoleCount = Opt(_fHoles.Text) is { } hc ? (int)Math.Round(hc) : null,
            HoleLengthM = Opt(_fMeters.Text),
            Status = DrillPlanStore.NormalizeStatus(_fStatus.SelectedItem as string),
            Note = string.IsNullOrWhiteSpace(_fNote.Text) ? null : _fNote.Text!.Trim(),
        };

        string err = DrillPlanStore.Upsert(_conn(), row);
        if (err.Length > 0) { _drillStatus.Text = "没存下：" + err; return; }

        _echo($"穿孔计划已存：{row.EquipmentId} {row.StartTime}–{row.EndTime}"
            + (row.Zone.Length > 0 ? $" @ {row.Zone}" : ""));
        Reload();
        _drillStatus.Text = $"已存 {row.EquipmentId} {row.StartTime}–{row.EndTime}";
    }

    private void DeleteDrill()
    {
        if (_drillGrid.SelectedItem is not DrillRow row) { _drillStatus.Text = "先选中要删的那一行"; return; }
        string err = DrillPlanStore.Delete(_conn(), row.Equip, DrillPlanStore.D(_date), row.Start);
        if (err.Length > 0) { _drillStatus.Text = "删不掉：" + err; return; }
        Reload();
        _drillStatus.Text = $"已删除 {row.Equip} {row.Start}–{row.End}";
    }

    /// <summary>留空 = 未录（null），不拿 0 冒充。</summary>
    private static double? Opt(string? s)
        => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static string Num(int? v) => v.HasValue ? v.Value.ToString("N0", CultureInfo.InvariantCulture) : "—";
    private static string Num(double? v, string fmt) => v is > 1e-9 ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "—";
    /// <summary>"42 / 520"；两侧都没有就一个破折号（不写 "— / —"）。</summary>
    private static string Pair(string a, string b) => a == "—" && b == "—" ? "—" : $"{a} / {b}";

    // ── 自检钩子用 ──
    internal int ShotRowCount => _rows.Count;
    internal int DrillRowCount => _drillRows.Count;
    internal string HeaderText => _header.Text ?? "";
    internal string NotesText => _notes.Text ?? "";
    internal string DrillStatusText => _drillStatus.Text ?? "";
    internal void SetDate(DateTime d) { _date = d; Reload(); }
}
