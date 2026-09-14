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
/// 「生产任务动态调整」—— 判原因码 → 选调整动作 → 滚动重排 + 欠量回摊。
///
/// <para>
/// 口径在 <see cref="AdjustModel"/>（纯函数），重排算法是本仓已有的 <see cref="TaskRescheduler"/>
/// —— 它移过来之后<b>一直没有入口</b>，本窗是那条接线。
/// </para>
/// <para>
/// <b>原因码这一步不能跳</b>：同样是"没干够"，故障要顶设备、缺车要补车、缺料要切面、
/// 天气要全盘降效回摊 —— <b>动作选错了，重排出来的计划照样排得满满的，而现场还是干不动</b>。
/// </para>
/// <para>
/// <b>实绩没录 ≠ 实绩为 0</b>：没录时达成度与欠量显示「—」。按 0 算的话，
/// 没录的面会全变成"欠产 100%"，重排就把整天的量又排一遍。
/// </para>
/// </summary>
internal sealed class DynamicAdjustWindow : Window
{
    private static readonly IBrush BadBrush = Brush.Parse("#DC2626");
    private static readonly IBrush OkBrush = Brush.Parse("#16A34A");

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _shift = new() { MinWidth = 110 };
    private readonly TextBox _from = new() { Width = 90, Watermark = "HH:mm" };
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    // 选中行的判定表单
    private readonly TextBox _actual = new() { Width = 110, Watermark = "实绩 m³" };
    private readonly ComboBox _reason = new() { Width = 140 };
    private readonly ComboBox _strategy = new() { Width = 170 };
    private readonly TextBlock _advice = new() { FontSize = 11, Foreground = Brush.Parse("#555"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };
    private readonly ObservableCollection<AdjustRow> _rows = new();

    private PlanAssembly _asm = new();
    private ExploderResult _plan = new();

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal DynamicAdjustWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "生产任务动态调整";
        Width = 1340; Height = 620;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        BuildColumns();
        _grid.ItemsSource = _rows;
        _grid.SelectionChanged += (_, _) => SyncForm();
        Content = BuildLayout();

        _reason.ItemsSource = AdjustModel.Reasons.Select(AdjustModel.ReasonZh).ToList();
        _strategy.ItemsSource = AdjustModel.Strategies.Select(AdjustModel.StrategyZh).ToList();
        _reason.SelectionChanged += (_, _) => OnReasonPicked();
        _shift.ItemsSource = new[] { "全部" };
        _shift.SelectedIndex = 0;
        _shift.SelectionChanged += (_, _) => Rebind();
        _date.SelectedDateChanged += (_, _) => Reload();
        _from.Text = AdjustModel.Hm(DateTime.Now.TimeOfDay.TotalHours);
        Reload();
    }

    private void BuildColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };

        _grid.Columns.Add(C("班次", nameof(AdjustRow.Shift), 84));
        _grid.Columns.Add(C("作业地点", nameof(AdjustRow.Zone), 140));
        _grid.Columns.Add(C("工序", nameof(AdjustRow.Process), 84));
        _grid.Columns.Add(C("主设备", nameof(AdjustRow.Equip), 120));
        _grid.Columns.Add(C("时段", nameof(AdjustRow.Span), 126));
        _grid.Columns.Add(C("计划量", nameof(AdjustRow.Plan), 118));
        _grid.Columns.Add(C("实绩量", nameof(AdjustRow.Actual), 118));
        _grid.Columns.Add(C("达成度", nameof(AdjustRow.Attainment), 96));
        _grid.Columns.Add(C("原因码", nameof(AdjustRow.ReasonText), 110));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("调整动作建议"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(AdjustRow.Advice)),
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
        P(new TextBlock { Text = "重排起点", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        P(_from);
        P(new Border { Width = 10 });
        P(B("执行重排", RunAdjust, bold: true, tip: "只重排起点之后的量；各面按它自己判出来的原因码走对应动作"));
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
                    Child = new StackPanel { Children = { _header, _result } },
                },
            },
        };

        var form = new WrapPanel();
        void F(Control c) => form.Children.Add(c);
        F(new TextBlock { Text = "选中行", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        F(new TextBlock { Text = "实绩", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        F(_actual);
        F(new TextBlock { Text = "原因码", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        F(_reason);
        F(new TextBlock { Text = "动作", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        F(_strategy);
        F(new Border { Width = 10 });
        F(B("应用到选中行", ApplyToRow, bold: true));
        F(_advice);

        var formBox = new Border
        {
            Background = Brush.Parse("#FAFAFA"), Padding = new Thickness(12, 8),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = form,
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
        DockPanel.SetDock(formBox, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(formBox);
        root.Children.Add(new Border { Margin = new Thickness(12, 6), Child = _grid });
        return root;
    }

    private DateTime CurrentDate => _date.SelectedDate?.Date ?? DateTime.Today;
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

        Rebind();
    }

    private void Rebind()
    {
        _rows.Clear();
        foreach (var r in AdjustModel.BuildRows(_plan.Tasks, ShiftFilter)) _rows.Add(r);

        _header.Text = $"{CurrentDate:yyyy-MM-dd}　{_asm.SourceLabel}";
        _result.Foreground = _asm.Usable ? Brush.Parse("#555") : BadBrush;
        _result.Text = _asm.Usable
            ? "还没重排。先逐行判原因码（没判原因码的面按「顺延后续班次」走）。"
            : "排不出来 —— " + string.Join(" ", _asm.Notes);
        int judged = _rows.Count(r => r.Reason != null);
        _status.Text = $"共 {_rows.Count} 项任务，已判原因码 {judged} 项；"
                     + "实绩没录的行达成度显示「—」—— 按 0 算会让它们全成「欠产 100%」，重排就把整天的量又排一遍。";
        SyncForm();
    }

    private void SyncForm()
    {
        if (_grid.SelectedItem is not AdjustRow row)
        {
            _advice.Text = "先在表里选中一行。";
            return;
        }
        _actual.Text = row.ActualM3 is { } a ? a.ToString("0.##", CultureInfo.InvariantCulture) : "";
        int ri = Array.IndexOf(AdjustModel.Reasons, row.Reason ?? SchedReason.Fault);
        _reason.SelectedIndex = row.Reason == null ? -1 : Math.Max(0, ri);
        _strategy.SelectedIndex = Math.Max(0, Array.IndexOf(AdjustModel.Strategies, row.Strategy));
        _advice.Text = row.Advice;
    }

    /// <summary>判了原因码 ⇒ 动作下拉跟着换成对应那一个（人仍可覆盖）。</summary>
    private void OnReasonPicked()
    {
        if (_reason.SelectedIndex < 0 || _reason.SelectedIndex >= AdjustModel.Reasons.Length) return;
        var r = AdjustModel.Reasons[_reason.SelectedIndex];
        _strategy.SelectedIndex = Math.Max(0, Array.IndexOf(AdjustModel.Strategies, TaskRescheduler.StrategyFor(r)));
        _advice.Text = AdjustModel.AdviceOf(r);
    }

    private void ApplyToRow()
    {
        if (_grid.SelectedItem is not AdjustRow row) { _status.Text = "先在表里选中一行。"; return; }

        string t = (_actual.Text ?? "").Trim();
        if (t.Length == 0) row.ActualM3 = null;          // 清空 = 回到"没录"，不是 0
        else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0)
            row.ActualM3 = v;
        else { _status.Text = $"实绩「{t}」认不出（写个非负的数，留空表示没录）。"; return; }

        if (_reason.SelectedIndex >= 0 && _reason.SelectedIndex < AdjustModel.Reasons.Length)
            AdjustModel.ApplyReason(row, AdjustModel.Reasons[_reason.SelectedIndex]);
        if (_strategy.SelectedIndex >= 0 && _strategy.SelectedIndex < AdjustModel.Strategies.Length)
            row.Strategy = AdjustModel.Strategies[_strategy.SelectedIndex];

        // ObservableCollection 不会因为对象内部改了就刷新单元格 —— 换一份出来
        var all = _rows.ToList();
        _rows.Clear();
        foreach (var x in all) _rows.Add(x);
        _grid.SelectedItem = row;

        _status.Text = $"{row.Zone} {row.Process}：原因码 {row.ReasonText} · 动作 {AdjustModel.StrategyZh(row.Strategy)}"
                     + $" · 实绩 {row.Actual}。";
    }

    private void RunAdjust()
    {
        double? from = MaintenanceWindows.Hour(_from.Text);
        if (from == null) { _status.Text = $"重排起点「{_from.Text}」认不出（写 HH:mm）。"; return; }

        var res = AdjustModel.Run(_asm.Usable ? _asm.Config : null, _rows.ToList(), from.Value);
        _result.Foreground = res.Blocked.Length > 0 ? BadBrush : OkBrush;
        _result.Text = res.Summary + (res.Notes.Count > 0 ? "\n◆ " + string.Join("\n◆ ", res.Notes) : "");
        if (res.Ran)
        {
            _echo($"动态调整：{res.Summary}");
            // ★ 重排走的是盘子的一份克隆，原盘子不动 —— 调整后的计划只在 res.Plan 里。
            //   拿原盘子再装一次箱得到的还是调整前那一版，而它算得出来、也不报错。
            _plan = res.Plan ?? _plan;
            var keepReason = _rows.ToDictionary(r => r.TaskId, r => (r.Reason, r.Strategy), StringComparer.Ordinal);
            _rows.Clear();
            foreach (var r in AdjustModel.BuildRows(_plan.Tasks, ShiftFilter))
            {
                if (keepReason.TryGetValue(r.TaskId, out var k)) { r.Reason = k.Reason; r.Strategy = k.Strategy; }
                _rows.Add(r);
            }
            _status.Text = $"重排完成：新计划 {res.TaskCount} 项。表里显示的已是调整后的计划；"
                         + "原盘子未落库 —— 要固化请到「任务下达」重新下达。";
        }
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal string HeaderText => _header.Text ?? "";
    internal string ResultText => _result.Text ?? "";
}
