// 忠实移植自原 PitMine3D Modules/TaskLib/Features/CrewAssignWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：WPF grid.Items.Refresh() → 重设 ItemsSource；DataTrigger(NeedsCrew=False → 淡显禁编) → LoadingRow 里设 IsEnabled/Foreground。
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 班组派工：把操作手 / 司机配到设备编组，并做持证与出勤校核。
///
/// <para>
/// 原先人员一律显示"（待派）"、保存是个空方法——等于这个窗口从来没有派过工。
/// 现在：花名册来自 %LOCALAPPDATA%/PitMine/crew/roster.json（首次运行自动生成样例），
/// 「自动派工」按【设备类别 ↔ 持证类别】匹配并避开休班与重复占用，保存真写盘、可回读。
/// </para>
/// <para>
/// 持证校核是真校核：填进去的人若不在花名册、或证件类别与设备对不上、或今天休班，
/// 都会在「持证校核 / 出勤」列上直说，而不是一律打勾。
/// </para>
/// </summary>
public sealed class CrewAssignWindow : Window
{
    /// <summary>行的种类 —— 决定它要不要派人、要不要落盘、要不要校持证。</summary>
    public enum RowKind
    {
        /// <summary>主设备编组（电铲/钻机/推土机）：操作手 + 配属车司机。</summary>
        Main,
        /// <summary>辅助设备（推土/平路/洒水）：只要一个操作手。<b>它此前根本不在这张表上</b>。</summary>
        Aux,
        /// <summary>爆破：按已定口径**不指人**（没有爆破队台账）。只列一行说明，不派、不存、不校。</summary>
        Blast,
    }

    public sealed class Row
    {
        public string Group { get; set; } = "";
        public string Main { get; set; } = "";
        public string Operator { get; set; } = "";
        public string Drivers { get; set; } = "";
        public string Cert { get; set; } = "";
        public string Attend { get; set; } = "";

        /// <summary>行的种类。<see cref="RowKind.Blast"/> 的行是**说明**，不是待办。</summary>
        public RowKind Kind { get; init; } = RowKind.Main;
        /// <summary>这一行要不要派人（爆破行为假）。界面据此不给它算进"待处理"。</summary>
        public bool NeedsCrew => Kind != RowKind.Blast;

        /// <summary>设备类别（电铲/钻机/推土机…）——持证匹配的依据。</summary>
        public string Category { get; init; } = "";
        /// <summary>本编组需要的卡车司机人数（＝实配车数）。</summary>
        public int TruckCount { get; init; }

        /// <summary>本编组的配车车号（顺序即司机列的填写顺序）。保存时按位配成 车号↔司机。</summary>
        public IReadOnlyList<string> TruckIds { get; init; } = Array.Empty<string>();

        // ── 校核时解析出的工号（姓名只作显示，对号认工号）──
        public string OperatorId { get; set; } = "";
        public List<string> DriverIds { get; set; } = new();
    }

    private const string Unassigned = "（待派）";
    private static readonly char[] NameSeparators = { '、', ',', '，', '/', ' ' };

    private readonly string _date = SampleTaskBoard.DateLabel;
    private List<CrewMember> _roster = new();
    private List<Row> _rows = new();

    private readonly TextBlock subTitle;
    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBox byBox = new() { Width = 94, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private readonly DataGrid grid = TaskUi.Grid(readOnly: false);
    private readonly TextBlock rosterTitle = new() { Text = "花名册", FontWeight = FontWeight.Bold, Margin = new Thickness(10, 8, 10, 4) };
    private readonly ListBox rosterList = new() { Margin = new Thickness(6), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private bool _loaded;

    public CrewAssignWindow()
    {
        Title = "班组派工 — 日常生产组织";
        TaskUi.Place(this, 1140, 620);

        var header = TaskUi.Header("班组派工", "按花名册把司机 / 操作手配到编组 · 持证与出勤校核 · 真落盘");
        subTitle = (TextBlock)((StackPanel)header.Child!).Children[1];

        var tool = new DockPanel { LastChildFill = false };
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); tool.Children.Add(c); }
        L(Lbl("班次"));
        // 派工一班一份，不给「全部」；取值域按当日班制、默认落当前班
        L(shiftCombo);
        L(Lbl("派工人"));
        byBox.Text = Environment.UserName; L(byBox);
        L(TaskUi.Btn("自动派工", OnAutoAssign, 90, bold: true));
        L(TaskUi.Btn("保存派工", OnSave, 90));
        L(TaskUi.Btn("载入已存", OnLoadSaved, 86));
        L(TaskUi.Btn("校核持证", OnRecheck, 86));
        var rl = TaskUi.Btn("重载花名册", OnReloadRoster, 98); rl.Margin = new Thickness(0); L(rl);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        DockPanel.SetDock(toolStatus, Avalonia.Controls.Dock.Right); tool.Children.Add(toolStatus);

        // 编组：主设备编组 / 辅助设备（推土·平路·洒水，此前完全不在这张表上）/ 爆破说明行
        grid.Margin = new Thickness(0);
        grid.Columns.Add(Star("编组", nameof(Row.Group), 1.8, readOnly: true));
        grid.Columns.Add(TaskUi.TextCol("主设备", nameof(Row.Main), 80));
        grid.Columns.Add(TaskUi.TextCol("操作手", nameof(Row.Operator), 100, readOnly: false));
        grid.Columns.Add(Star("卡车司机（顿号分隔）", nameof(Row.Drivers), 1.6, readOnly: false));
        grid.Columns.Add(Star("持证校核", nameof(Row.Cert), 1.1, readOnly: true));
        grid.Columns.Add(TaskUi.TextCol("出勤", nameof(Row.Attend), 90));
        // 爆破那一行是**说明**不是待办（按已定口径不指人，没有爆破队台账）：
        // 淡显并禁编，免得有人往上面填名字 —— 填了也不会落盘，而那种"填了没保存上"最难查。
        grid.LoadingRow += (_, e) =>
        {
            bool needs = e.Row.DataContext is not Row r || r.NeedsCrew;
            e.Row.IsEnabled = needs;
            if (needs) e.Row.ClearValue(ForegroundProperty); else e.Row.Foreground = TaskUi.Muted;
        };
        grid.CellEditEnded += (_, _) => OnCellEdited();

        var rDock = new DockPanel();
        TaskUi.Theme(rosterTitle, TextBlock.ForegroundProperty, "Theme.Text.Body");
        DockPanel.SetDock(rosterTitle, Avalonia.Controls.Dock.Top); rDock.Children.Add(rosterTitle);
        var rHint = new TextBlock { FontSize = 11, Margin = new Thickness(10, 4, 10, 8), TextWrapping = TextWrapping.Wrap, Text = "花名册存于 %LOCALAPPDATA%/PitMine/crew/roster.json，首次运行自动生成样例，可直接编辑该文件增删人员。" };
        TaskUi.Theme(rHint, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        DockPanel.SetDock(rHint, Avalonia.Controls.Dock.Bottom); rDock.Children.Add(rHint);
        // 原 WPF ListBox 行是紧凑的（每行一位人员）；Fluent 的 ListBoxItem 默认 32px 高 + 12px 内边距，收回去
        var lis = new Style(x => x.OfType<ListBox>().Descendant().OfType<ListBoxItem>());
        lis.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 3)));
        lis.Setters.Add(new Setter(ListBoxItem.MinHeightProperty, 0.0));
        lis.Setters.Add(new Setter(ListBoxItem.FontSizeProperty, 12.5));
        rosterList.Styles.Add(lis);
        rDock.Children.Add(rosterList);
        var rBox = new Border { Margin = new Thickness(10, 0, 0, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = rDock };
        TaskUi.Theme(rBox, Border.BorderBrushProperty, "Theme.Surface.Border");

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,250"), Margin = new Thickness(10) };
        Grid.SetColumn(grid, 0); Grid.SetColumn(rBox, 1);
        body.Children.Add(grid); body.Children.Add(rBox);

        TaskUi.Theme(feedback, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = TaskUi.Bar(new ScrollViewer { Content = feedback, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, top: false, padY: 6);
        foot.MaxHeight = 110;

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(body, 2); Grid.SetRow(foot, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(body); g.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) Build(); };
        Opened += (_, _) =>
        {
            LoadRoster();
            // 派工一班一份，不给「全部」；默认落当前班（原先写死中班）
            ShiftSelector.Bind(shiftCombo, includeAll: false);
            _loaded = true;
            Build();
        };
    }

    private static TextBlock Lbl(string t)
    {
        var l = TaskUi.Lbl(t); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 0);
        return l;
    }

    private static DataGridTextColumn Star(string header, string path, double star, bool readOnly)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay }, Width = new DataGridLength(star, DataGridLengthUnitType.Star), IsReadOnly = readOnly };

    private string Shift
    {
        get
        {
            string s = ShiftSelector.Selected(shiftCombo);
            return s.Length > 0 ? s : ShiftScope.Current;
        }
    }
    private string By => string.IsNullOrWhiteSpace(byBox.Text) ? Environment.UserName : byBox.Text!.Trim();

    private void CommitEdits()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    /// <summary>WPF 的 grid.Items.Refresh()：行对象是普通 POCO，改完字段重设一次源让表重画。</summary>
    private void RefreshGrid()
    {
        var sel = grid.SelectedItem;
        grid.ItemsSource = null; grid.ItemsSource = _rows;
        if (sel != null) grid.SelectedItem = sel;
    }

    // ── 花名册 ───────────────────────────────────────────────────────────────

    private void LoadRoster()
    {
        _roster = TaskPersistence.LoadRoster();
        rosterList.ItemsSource = _roster.Select(m => m.Caption).ToList();
        rosterTitle.Text = $"花名册（{_roster.Count} 人 · 在岗 {_roster.Count(m => m.OnDuty)}）";
    }

    private void OnReloadRoster()
    {
        LoadRoster();
        Recheck();
        feedback.Text = $"已重载花名册：{_roster.Count} 人（操作手 {_roster.Count(m => m.Job == "操作手")} · 司机 {_roster.Count(m => m.Job == "司机")}）。";
    }

    // ── 建行 ─────────────────────────────────────────────────────────────────

    private void Build()
    {
        // 设备类别取自在籍清单（持证匹配要按类别，不能按设备号猜）
        var catOf = SampleTaskBoard.Roster().ToDictionary(r => r.EquipId, r => r.Category, StringComparer.OrdinalIgnoreCase);

        // ⚠ **运输笔不在这里建行**：定车制下它的"设备"是车队（`{铲号} 车队`），
        //    司机本来就配在源采装那台铲的编组里 —— 给车队再建一行，就会出现
        //    "同一批司机被要求派两遍"，而两处派的人可以不一样。
        var mine = SampleTaskBoard.Day()
            .Where(t => t.Shift == Shift)
            .ToList();

        var groups = mine
            .Where(t => t.Process is ProcessType.Load or ProcessType.Dump or ProcessType.Drill)
            .Where(t => !string.IsNullOrWhiteSpace(t.Group.MainEquipment))
            .Select(t => t.Group)
            .GroupBy(g => g.MainEquipment, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        _rows = groups.Select(g => new Row
        {
            Kind = RowKind.Main,
            Group = g.Caption,
            Main = g.MainEquipment,
            Category = catOf.TryGetValue(g.MainEquipment, out var c) ? c : "",
            TruckCount = g.Trucks.Count,
            TruckIds = g.Trucks.ToList(),
            Operator = Unassigned,
            Drivers = g.Trucks.Count > 0 ? string.Join("、", Enumerable.Repeat(Unassigned, g.Trucks.Count)) : "—",
        }).ToList();

        // ── 辅助设备（推土 / 平路 / 洒水）──────────────────────────────
        //
        //  ★ `EquipmentGroup.Aux` 一直有人写（编组求解把辅助机具挂在编组上），
        //    而这张表只收 MainEquipment ⇒ **辅助设备的操作手从来没有人派过**。
        //    它们照样要上人、照样要持证，单据上却一个名字都没有。
        var mainIds = new HashSet<string>(_rows.Select(r => r.Main), StringComparer.OrdinalIgnoreCase);
        var auxOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in mine)
            foreach (var a in t.Group.Aux ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(a) || mainIds.Contains(a)) continue;
                if (!auxOwner.ContainsKey(a)) auxOwner[a] = t.Group.MainEquipment;
            }

        foreach (var kv in auxOwner.OrderBy(x => x.Key, StringComparer.Ordinal))
            _rows.Add(new Row
            {
                Kind = RowKind.Aux,
                Group = $"{kv.Key}（辅助 · 配属 {kv.Value}）",
                Main = kv.Key,
                Category = catOf.TryGetValue(kv.Key, out var ac) ? ac : "",
                TruckCount = 0,
                TruckIds = Array.Empty<string>(),
                Operator = Unassigned,
                Drivers = "—",
            });

        // ── 爆破：列一行说明，不派人 ───────────────────────────────────
        //  按已定口径爆破不指人（爆破队台账还没有）。**不列它更糟**：
        //  班组会以为这个面这一班照常装车，而它其实被一炮占住了。
        var blast = mine.Where(t => t.Process == ProcessType.Blast).ToList();
        if (blast.Count > 0)
            _rows.Add(new Row
            {
                Kind = RowKind.Blast,
                Group = $"爆破 {blast.Count} 炮：{string.Join("、", blast.Select(t => t.WorkZone).Distinct().Take(4))}"
                      + (blast.Select(t => t.WorkZone).Distinct().Count() > 4 ? " 等" : ""),
                Main = "—",
                Category = "",
                TruckCount = 0,
                TruckIds = Array.Empty<string>(),
                Operator = "不派设备（清场）",
                Drivers = "—",
                Cert = "按口径不指人（无爆破队台账）",
                Attend = "—",
            });

        // 已存派工优先回填（同一个班反复开窗不该把昨天派好的人清掉）
        var saved = TaskPersistence.LoadCrew(_date, Shift);
        foreach (var r in _rows)
        {
            if (r.Kind == RowKind.Blast) continue;
            var s = saved.FirstOrDefault(x => string.Equals(x.MainEquipment, r.Main, StringComparison.OrdinalIgnoreCase));
            if (s == null) continue;
            if (!string.IsNullOrWhiteSpace(s.Operator)) r.Operator = s.Operator;
            if (s.Drivers.Count > 0) r.Drivers = string.Join("、", s.Drivers);
        }

        grid.ItemsSource = _rows;
        Recheck();

        subTitle.Text = $"{_date} · {Shift} · 派工落盘 {TaskPersistence.CrewDir()}";
        feedback.Text = saved.Count > 0
            ? $"已回填 {Shift} 的落盘派工 {saved.Count} 条。"
            : $"{Shift} 尚无落盘派工——点「自动派工」按花名册与持证自动匹配，或直接在表里录人名。";
    }

    // ── 自动派工 ─────────────────────────────────────────────────────────────

    private void OnAutoAssign()
    {
        CommitEdits();

        // 已被本班占用的人（含手工填的）不重复派
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows)
        {
            if (r.Kind == RowKind.Blast) continue;
            if (IsName(r.Operator)) used.Add(r.Operator.Trim());
            foreach (var d in SplitNames(r.Drivers)) used.Add(d);
        }

        var ops = _roster.Where(m => m.Job == "操作手" && m.OnDuty).ToList();
        var drivers = _roster.Where(m => m.Job == "司机" && m.OnDuty).ToList();
        int filledOp = 0, filledDr = 0;
        var shortfall = new List<string>();

        foreach (var r in _rows)
        {
            if (r.Kind == RowKind.Blast) continue;   // 按口径不指人，自动派工也不许给它塞一个

            // ① 操作手：先按【设备类别 == 持证类别】匹配，匹配不上再退到任意在岗操作手
            if (!IsName(r.Operator))
            {
                var pick = ops.FirstOrDefault(m => !used.Contains(m.Name) && SameCert(m.CertFor, r.Category))
                        ?? ops.FirstOrDefault(m => !used.Contains(m.Name));
                if (pick != null) { r.Operator = pick.Name; used.Add(pick.Name); filledOp++; }
                else shortfall.Add($"{r.Main} 无可派操作手");
            }

            // ② 卡车司机：按实配车数补齐
            if (r.TruckCount > 0)
            {
                var cur = SplitNames(r.Drivers).ToList();
                while (cur.Count < r.TruckCount)
                {
                    var pick = drivers.FirstOrDefault(m => !used.Contains(m.Name) && SameCert(m.CertFor, "矿卡"))
                            ?? drivers.FirstOrDefault(m => !used.Contains(m.Name));
                    if (pick == null) { shortfall.Add($"{r.Main} 缺 {r.TruckCount - cur.Count} 名司机"); break; }
                    cur.Add(pick.Name); used.Add(pick.Name); filledDr++;
                }
                while (cur.Count < r.TruckCount) cur.Add(Unassigned);
                r.Drivers = string.Join("、", cur);
            }
        }

        Recheck();
        feedback.Text = $"自动派工：补入操作手 {filledOp} 人 / 司机 {filledDr} 人（按设备类别↔持证类别匹配，避开休班与重复占用）。"
                      + (shortfall.Count > 0 ? Environment.NewLine + "⚠ 人手不足：" + string.Join("；", shortfall) + "——请在花名册补录人员或跨班调剂。" : "");
    }

    // ── 校核 ─────────────────────────────────────────────────────────────────

    private void OnRecheck()
    {
        CommitEdits();
        Recheck();
        feedback.Text = "已按花名册重新校核持证与出勤。";
    }

    private void OnCellEdited()
    {
        // 编辑提交后再校核：直接在 Ending 事件里读到的还是旧值，故延后一拍
        Dispatcher.UIThread.Post(Recheck, DispatcherPriority.Background);
    }

    /// <summary>逐行比对花名册，写出持证与出勤结论（真校核，不是一律打勾）。</summary>
    private void Recheck()
    {
        if (_rows.Count == 0) return;

        // ★ 姓名不是主键。花名册里两个"张建国"时，原先 g.First() 会**静默**挑一个去校核持证——
        //   挑错了界面上一点异常也看不出来。现在：同名的一律不认，当场点名要求用工号区分。
        var groups = _roster.GroupBy(m => m.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        var byName = groups.Where(g => g.Count() == 1)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var dupNames = groups.Where(g => g.Count() > 1)
                             .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        int okCount = 0, issues = 0;

        // 认人：同名的返回 null 并写一条"重名"，不猜。
        CrewMember? Resolve(string name, List<string> notes)
        {
            string n = (name ?? "").Trim();
            if (dupNames.TryGetValue(n, out int c))
            {
                notes.Add($"{n} 花名册里有 {c} 个同名，按姓名对不准（请在花名册里去重或用工号区分）");
                return null;
            }
            return byName.TryGetValue(n, out var m) ? m : null;
        }

        foreach (var r in _rows)
        {
            if (r.Kind == RowKind.Blast) continue;   // 说明行：不校持证、不算"待处理"

            var certNotes = new List<string>();
            var absent = new List<string>();

            // 操作手
            r.OperatorId = "";
            if (!IsName(r.Operator)) certNotes.Add("操作手待派");
            else
            {
                int before = certNotes.Count;
                var op = Resolve(r.Operator, certNotes);
                if (op == null)
                {
                    if (certNotes.Count == before) certNotes.Add($"{r.Operator} 不在花名册");
                }
                else
                {
                    r.OperatorId = op.PersonId;          // 工号存下来，下游按它对号
                    if (!op.OnDuty) absent.Add(op.Name);
                    if (op.Job != "操作手") certNotes.Add($"{op.Name} 工种为{op.Job}");
                    else if (!SameCert(op.CertFor, r.Category)) certNotes.Add($"{op.Name} 持{(string.IsNullOrWhiteSpace(op.CertFor) ? "无" : op.CertFor)}证 ≠ {r.Category}");
                }
            }

            // 司机
            var ds = SplitNames(r.Drivers).ToList();
            r.DriverIds = new List<string>();
            if (r.TruckCount > 0)
            {
                int pending = r.TruckCount - ds.Count;
                if (pending > 0) certNotes.Add($"缺 {pending} 名司机");
                foreach (var d in ds)
                {
                    int before = certNotes.Count;
                    var dm = Resolve(d, certNotes);
                    if (dm == null)
                    {
                        if (certNotes.Count == before) certNotes.Add($"{d} 不在花名册");
                        r.DriverIds.Add("");
                        continue;
                    }
                    r.DriverIds.Add(dm.PersonId);
                    if (!dm.OnDuty) absent.Add(dm.Name);
                    if (dm.Job != "司机") certNotes.Add($"{dm.Name} 工种为{dm.Job}");
                }
            }

            r.Cert = certNotes.Count == 0 ? "✓ 持证齐全" : "⚠ " + string.Join("；", certNotes);
            r.Attend = absent.Count == 0 ? "全勤" : "⚠ 休班：" + string.Join("、", absent);
            if (certNotes.Count == 0 && absent.Count == 0) okCount++; else issues++;
        }

        RefreshGrid();

        // 覆盖率按**需要派人的行**算：爆破那一行不是待办，混进分母会让"派满了"永远达不到。
        int need = _rows.Count(r => r.NeedsCrew);
        int aux = _rows.Count(r => r.Kind == RowKind.Aux);
        toolStatus.Text = $"{Shift}：{need} 个编组待派"
                        + (aux > 0 ? $"（含辅助设备 {aux}）" : "")
                        + $" · 校核通过 {okCount} · 待处理 {issues}"
                        + (_rows.Count > need ? " · 爆破 1 行不派设备" : "");
    }

    // ── 保存 / 载入 ──────────────────────────────────────────────────────────

    private void OnSave()
    {
        CommitEdits();
        Recheck();

        var list = _rows.Where(r => r.Kind != RowKind.Blast).Select(r =>
        {
            var names = SplitNames(r.Drivers).ToList();
            // 车号 ↔ 司机在这里配死：这一行是**唯一同时知道车号顺序与司机顺序**的地方。
            // 不在这儿配好，下游（派车单要在每一趟上写司机名）就只能靠下标去猜。
            var pairs = new List<TruckDriver>();
            for (int i = 0; i < r.TruckIds.Count && i < names.Count; i++)
                pairs.Add(new TruckDriver
                {
                    TruckId = r.TruckIds[i],
                    Name = names[i],
                    PersonId = i < r.DriverIds.Count ? r.DriverIds[i] : "",
                });

            return new CrewAssignment
            {
                PlanDate = _date,
                Shift = Shift,
                MainEquipment = r.Main,
                Operator = IsName(r.Operator) ? r.Operator.Trim() : "",
                OperatorId = r.OperatorId,
                Drivers = names,
                TruckDrivers = pairs,
                CertNote = r.Cert,
                AttendNote = r.Attend,
                SavedBy = By,
            };
        }).ToList();

        bool ok = TaskPersistence.SaveCrew(_date, Shift, list);
        CrewLookup.Invalidate();     // 任务书 / 派车单 / 看板下一次读就是新派的工
        int pending = list.Count(x => string.IsNullOrEmpty(x.Operator));
        int paired = list.Sum(x => x.TruckDrivers.Count);

        feedback.Text = (ok ? "✓ 已保存" : "⚠ 保存失败：" + TaskPersistence.LastIoLabel)
            + $" {Shift} 派工 {list.Count} 条（派工人 {By}）→ {TaskPersistence.CrewDir()}"
            + (pending > 0 ? $"；其中 {pending} 个编组的操作手仍待派" : "")
            + $"；已配死 {paired} 组「车号↔司机」，任务书 / 派车单 / 看板即刻可见。";
        toolStatus.Text = ok ? $"{Shift} 派工已保存 {list.Count} 条" : "保存失败";
    }

    private void OnLoadSaved()
    {
        var saved = TaskPersistence.LoadCrew(_date, Shift);
        if (saved.Count == 0) { feedback.Text = $"{Shift} 无落盘派工记录。"; return; }

        foreach (var r in _rows)
        {
            if (r.Kind == RowKind.Blast) continue;
            var s = saved.FirstOrDefault(x => string.Equals(x.MainEquipment, r.Main, StringComparison.OrdinalIgnoreCase));
            if (s == null) continue;
            r.Operator = string.IsNullOrWhiteSpace(s.Operator) ? Unassigned : s.Operator;
            r.Drivers = s.Drivers.Count > 0 ? string.Join("、", s.Drivers) : r.Drivers;
        }
        Recheck();
        feedback.Text = $"已载入 {Shift} 落盘派工 {saved.Count} 条（保存人 {saved[0].SavedBy} · {saved[0].SavedAt:MM-dd HH:mm}）。";
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private static bool IsName(string? s)
        => !string.IsNullOrWhiteSpace(s) && s.Trim() != Unassigned && s.Trim() != "—";

    private static IEnumerable<string> SplitNames(string? s)
        => (s ?? "").Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(IsName);

    /// <summary>持证类别与设备类别是否相容（同名即可；"液压铲/电铲"这类同族称谓互认）。</summary>
    private static bool SameCert(string? cert, string? category)
    {
        string c = (cert ?? "").Trim(), k = (category ?? "").Trim();
        if (c.Length == 0 || k.Length == 0) return false;
        if (string.Equals(c, k, StringComparison.OrdinalIgnoreCase)) return true;
        bool shovelC = c.Contains("铲"), shovelK = k.Contains("铲");
        return shovelC && shovelK;
    }
}
