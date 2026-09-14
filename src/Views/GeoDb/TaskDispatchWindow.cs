using System;
using System.Collections.Generic;
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
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「任务下达」—— 把任务书数字派单至设备，并留下<b>可追溯的单据</b>。
///
/// <para>本窗口是"计划 → 执行"的闸门，三件事必须为真（照搬原版）：</para>
/// <list type="number">
///   <item><b>下达前校验</b>走 <see cref="DispatchEngine.ValidateForIssue"/> —— 有 Error 级校核、
///     缺去向、缺主设备一律不得下达。「这车拉到哪」都没定就发单，现场只能自己找地方倒，
///     采排账当天就散。</item>
///   <item><b>落盘</b>：下达即生成 <see cref="TaskInstance"/>（带稳定键与版本）与
///     <see cref="DispatchReceipt"/> 回执。关窗、重开、改编制参数，"哪条任务下达过没有"都查得到。</item>
///   <item><b>可撤回</b>：撤回也是<b>一条回执</b>，不是把记录抹掉 —— 单据流水只增不改。</item>
/// </list>
///
/// <para>
/// 稳定键（<see cref="TaskKey"/>）解决的是老问题：任务是每次开窗现算的，Id 随编制参数漂移，
/// 拿 Id 当主键则昨天下达的任务今天就对不上号 —— 而对不上时它不报错，只是显示成"待下达"。
/// </para>
///
/// ── 与原版的范围差异（登记）──
/// <list type="bullet">
///   <item>盘子来自 §三五四 的装配层；装不出面时如实说"排不出来"并给补法，<b>不回落样例</b>。</item>
///   <item>「确认(Ack)/开工/完工」三种回执的录入口未移（属班组终端那一侧），
///     但类型与流水已按原版留着 —— 等那一侧接进来时不必回头改单据格式。</item>
///   <item>派车单（车次级 <c>DispatchOrder</c>）未移，另属「派车单」那一项。</item>
/// </list>
/// </summary>
internal sealed class TaskDispatchWindow : Window
{
    private static readonly IBrush OkBrush = Brush.Parse("#16A34A");
    private static readonly IBrush WarnBrush = Brush.Parse("#D97706");
    private static readonly IBrush DimBrush = Brush.Parse("#8A8A8A");
    private static readonly IBrush BadBrush = Brush.Parse("#DC2626");

    internal sealed class Row
    {
        public string Shift { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string Equip { get; set; } = "";
        public string Place { get; set; } = "";
        public string Destination { get; set; } = "";
        public IBrush DestBrush { get; set; } = DimBrush;
        public string Process { get; set; } = "";
        public string Plan { get; set; } = "";
        public string Span { get; set; } = "";
        public string State { get; set; } = "待下达";
        public IBrush StateBrush { get; set; } = DimBrush;

        public ShiftTask Task { get; init; } = new();
        public string Key { get; init; } = "";
        public bool Issued { get; set; }
        /// <summary>阻止下达的原因（空 = 这条过得了闸）。</summary>
        public string Block { get; set; } = "";
    }

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _shift = new() { MinWidth = 110 };
    private readonly TextBox _by = new() { Width = 130, Watermark = "下达人" };
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _check = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };
    private readonly TextBox _receipts = new()
    { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontSize = 11, MinHeight = 92 };

    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
        SelectionMode = DataGridSelectionMode.Extended,
    };
    private readonly ObservableCollection<Row> _rows = new();

    private PlanAssembly _asm = new();
    private ExploderResult _plan = new();
    private Dictionary<string, TaskInstance> _instances = new(StringComparer.OrdinalIgnoreCase);

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal TaskDispatchWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "任务下达";
        Width = 1320; Height = 620;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        _by.Text = Environment.UserName;
        BuildColumns();
        _grid.ItemsSource = _rows;
        _grid.SelectionChanged += (_, _) => ShowReceipts();
        Content = BuildLayout();

        _shift.ItemsSource = new[] { "全部" };
        _shift.SelectedIndex = 0;
        _shift.SelectionChanged += (_, _) => Rebind();
        _date.SelectedDateChanged += (_, _) => Reload();
        Reload();
    }

    private void BuildColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };

        DataGridTemplateColumn Painted(string h, string textPath, string brushPath, DataGridLength w) => new()
        {
            Header = Head(h), Width = w, IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var tb = new TextBlock
                {
                    Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                tb.Bind(TextBlock.TextProperty, new Binding(textPath));
                tb.Bind(TextBlock.ForegroundProperty, new Binding(brushPath));
                tb.Bind(ToolTip.TipProperty, new Binding(nameof(Row.Block)));
                return tb;
            }, supportsRecycling: true),
        };

        _grid.Columns.Add(C("班次", nameof(Row.Shift), 84));
        _grid.Columns.Add(C("任务号", nameof(Row.TaskId), 170));
        _grid.Columns.Add(C("主设备", nameof(Row.Equip), 120));
        _grid.Columns.Add(C("作业地点", nameof(Row.Place), 150));
        _grid.Columns.Add(Painted("卸载地点", nameof(Row.Destination), nameof(Row.DestBrush), new DataGridLength(170)));
        _grid.Columns.Add(C("工序", nameof(Row.Process), 84));
        _grid.Columns.Add(C("计划量", nameof(Row.Plan), 130));
        _grid.Columns.Add(C("时段", nameof(Row.Span), 150));
        _grid.Columns.Add(Painted("状态", nameof(Row.State), nameof(Row.StateBrush),
                                  new DataGridLength(1, DataGridLengthUnitType.Star)));
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
        P(new TextBlock { Text = "下达人", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        P(_by);
        P(new Border { Width = 10 });
        P(B("重新装配", Reload, tip: "台账改过之后按它重装当日盘子"));

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

        var receiptBox = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "回执流水（选中一行看它的单据历史；只增不改）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 6, 12, 2), FontSize = 12 },
                new Border { Margin = new Thickness(12, 0), Child = _receipts },
            },
        };

        var footBar = new Border
        {
            Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(12, 6, 12, 9),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new WrapPanel
            {
                Children =
                {
                    B("全选可下达的", SelectIssuable, tip: "只选过得了闸的那些；被拦的不选中"),
                    B("下达选中", IssueSelected, bold: true),
                    B("撤回选中", WithdrawSelected, tip: "撤回也是一条回执，不抹掉下达那一条"),
                    new Border { Width = 16 },
                    B("关闭", Close),
                },
            },
        };
        var statusBar = new Border { Padding = new Thickness(12, 2), Child = _status };

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(receiptBox, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(receiptBox);
        root.Children.Add(new Border { Margin = new Thickness(12, 6), Child = _grid });
        return root;
    }

    private DateTime CurrentDate => _date.SelectedDate?.Date ?? DateTime.Today;
    private string PlanDate => CurrentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private string? ShiftFilter => _shift.SelectedItem as string is { } s && s != "全部" ? s : null;

    // ═══════════════════ 装配与显示 ═══════════════════

    internal void Reload()
    {
        _asm = ProductionPlanContext.Assemble(_conn(), CurrentDate, DateTime.Now.TimeOfDay.TotalHours);
        _plan = _asm.Usable ? TaskExploder.Explode(_asm.Config) : new ExploderResult();

        var shifts = new List<string> { "全部" };
        shifts.AddRange(_asm.Config.Shifts.Select(s => s.Name));
        string? keep = _shift.SelectedItem as string;
        _shift.ItemsSource = shifts;
        _shift.SelectedIndex = keep != null && shifts.Contains(keep) ? shifts.IndexOf(keep) : 0;

        _instances = DispatchStore.LoadInstances();
        // ★ 读坏与读空是两回事：静默当成"没有单据"，人会以为今天一条都没下达，然后重下一遍
        string storeErr = DispatchStore.LastError;

        _header.Text = $"{PlanDate}　{_asm.SourceLabel}";
        Rebind();
        if (storeErr.Length > 0) _status.Text = "⚠ " + storeErr + " —— 表内「已下达」不作数，先处理该文件。";
    }

    private void Rebind()
    {
        _rows.Clear();
        if (!_asm.Usable)
        {
            _check.Foreground = BadBrush;
            _check.Text = "排不出来 —— " + string.Join(" ", _asm.Notes);
            _status.Text = "没有可下达的任务。";
            _receipts.Text = "";
            return;
        }

        var chk = DispatchEngine.ValidateForIssue(_plan, ShiftFilter);
        var blockBy = chk.Blocks.Where(b => b.TaskId.Length > 0)
            .GroupBy(b => b.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join("；", g.Select(x => x.Message)), StringComparer.OrdinalIgnoreCase);

        var tasks = _plan.Tasks
            .Where(t => t.Process != ProcessType.Idle)
            .Where(t => ShiftFilter == null || string.Equals(t.Shift, ShiftFilter, StringComparison.Ordinal))
            .OrderBy(t => t.Shift, StringComparer.Ordinal).ThenBy(t => t.StartHour);

        foreach (var t in tasks)
        {
            string key = TaskKey.Of(t, PlanDate);
            _instances.TryGetValue(key, out var inst);
            blockBy.TryGetValue(t.Id, out string? block);
            bool issued = inst?.IsIssued == true;

            _rows.Add(new Row
            {
                Task = t, Key = key, Issued = issued, Block = block ?? "",
                Shift = t.Shift,
                TaskId = t.Id,
                Equip = t.Group.MainEquipment.Length > 0 ? t.Group.MainEquipment : "—",
                Place = t.WorkZone,
                Destination = t.HasDestination
                    ? (t.DestinationName.Length > 0 ? t.DestinationName : t.DestinationId)
                    : DispatchEngine.NeedsDestination(t) ? "未指定卸点" : "—",
                DestBrush = t.HasDestination ? Brushes.Black
                          : DispatchEngine.NeedsDestination(t) ? BadBrush : DimBrush,
                Process = t.Process.Label(),
                Plan = t.TargetVolumeM3 > 1e-9 ? $"{t.TargetVolumeM3:N0} m³" : "—",
                Span = $"{Hm(t.StartHour)}–{Hm(t.EndHour)} / {t.PlannedHours:0.#} h",
                State = inst?.IssueCaption ?? (block is { Length: > 0 } ? "不得下达" : "待下达"),
                StateBrush = issued ? OkBrush : block is { Length: > 0 } ? BadBrush : DimBrush,
            });
        }

        _check.Foreground = chk.CanIssue ? OkBrush : BadBrush;
        _check.Text = chk.Summary
                    + (chk.Blocks.Count > 0 ? "\n" + chk.BlockText : "")
                    + (chk.Warnings.Count > 0 ? "\n" + chk.WarnText : "");
        int issuedCount = _rows.Count(r => r.Issued);
        _status.Text = $"共 {_rows.Count} 条任务，已下达 {issuedCount} 条；单据按稳定键存，改编制参数不影响对号。";
        ShowReceipts();
    }

    private void ShowReceipts()
    {
        if (_grid.SelectedItem is not Row row) { _receipts.Text = ""; return; }
        var list = DispatchStore.ReceiptsOf(row.Key);
        _receipts.Text = list.Count == 0
            ? $"{row.TaskId}　稳定键 {row.Key}\n（这条任务还没有任何回执）"
            : $"{row.TaskId}　稳定键 {row.Key}\n" + string.Join("\n", list.Select(r => r.Caption));
    }

    // ═══════════════════ 下达 / 撤回 ═══════════════════

    private void SelectIssuable()
    {
        _grid.SelectedItems.Clear();
        foreach (var r in _rows.Where(r => r.Block.Length == 0 && !r.Issued)) _grid.SelectedItems.Add(r);
        _status.Text = $"已选中 {_grid.SelectedItems.Count} 条可下达的（被拦的不选）。";
    }

    private List<Row> Selected() => _grid.SelectedItems.OfType<Row>().ToList();

    private void IssueSelected()
    {
        var picked = Selected();
        if (picked.Count == 0) { _status.Text = "先选中要下达的行（「全选可下达的」一键选完）。"; return; }

        // ★ 闸门只按选中范围重跑一次：整盘过不了不代表这几条过不了，反之亦然
        var chk = DispatchEngine.ValidateForIssue(_plan, ShiftFilter, picked.Select(r => r.TaskId));
        if (!chk.CanIssue)
        {
            _check.Foreground = BadBrush;
            _check.Text = chk.Summary + "\n" + chk.BlockText;
            _status.Text = "没下达：选中的行里有阻止项（见上方）。";
            return;
        }

        string by = string.IsNullOrWhiteSpace(_by.Text) ? Environment.UserName : _by.Text!.Trim();
        var now = DateTime.Now;
        var receipts = new List<DispatchReceipt>();
        int fresh = 0, again = 0;

        foreach (var r in picked)
        {
            if (!_instances.TryGetValue(r.Key, out var inst))
            {
                inst = TaskInstance.From(r.Task, PlanDate);
                _instances[r.Key] = inst;
                fresh++;
            }
            else
            {
                // 重新下达 = 新版本；快照跟着更新（下达的是此刻这一版的量与时段）
                inst.Version++;
                inst.TaskId = r.Task.Id;
                inst.Snapshot = TaskSnapshot.Of(r.Task);
                inst.WithdrawnBy = ""; inst.WithdrawnAt = null;
                again++;
            }
            inst.IssuedBy = by;
            inst.IssuedAt = now;
            receipts.Add(DispatchReceipt.For(inst, ReceiptKind.Issue, by,
                $"{r.Place} {r.Process} {r.Plan} → {r.Destination}（v{inst.Version}）"));
        }

        string e1 = DispatchStore.SaveInstances(_instances.Values);
        string e2 = DispatchStore.AppendReceipts(receipts);
        if (e1.Length > 0 || e2.Length > 0)
        {
            _status.Text = "写盘出问题：" + string.Join("；", new[] { e1, e2 }.Where(x => x.Length > 0));
            return;
        }

        Reload();
        _status.Text = $"已下达 {picked.Count} 条（新建 {fresh} · 重下 {again}），回执已入流水。";
        _echo(_status.Text);
    }

    private void WithdrawSelected()
    {
        var picked = Selected().Where(r => r.Issued).ToList();
        if (picked.Count == 0) { _status.Text = "选中的行里没有「已下达」的，撤回无从谈起。"; return; }

        string by = string.IsNullOrWhiteSpace(_by.Text) ? Environment.UserName : _by.Text!.Trim();
        var now = DateTime.Now;
        var receipts = new List<DispatchReceipt>();
        foreach (var r in picked)
        {
            if (!_instances.TryGetValue(r.Key, out var inst)) continue;
            inst.Withdraw(by, now);          // 留痕不抹痕：IssuedBy/IssuedAt 原样保留
            receipts.Add(DispatchReceipt.For(inst, ReceiptKind.Withdraw, by, $"{r.Place} {r.Process} 撤回"));
        }

        string e1 = DispatchStore.SaveInstances(_instances.Values);
        string e2 = DispatchStore.AppendReceipts(receipts);
        if (e1.Length > 0 || e2.Length > 0)
        {
            _status.Text = "写盘出问题：" + string.Join("；", new[] { e1, e2 }.Where(x => x.Length > 0));
            return;
        }

        Reload();
        _status.Text = $"已撤回 {picked.Count} 条 —— 下达那一条回执照旧在流水里，没有抹掉。";
        _echo(_status.Text);
    }

    private static string Hm(double hh)
    {
        int h = (int)hh;
        int m = (int)Math.Round((hh - h) * 60);
        if (m == 60) { h++; m = 0; }
        return $"{h:00}:{m:00}";
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal string HeaderText => _header.Text ?? "";
    internal string CheckText => _check.Text ?? "";
    internal string StatusText => _status.Text ?? "";
}
