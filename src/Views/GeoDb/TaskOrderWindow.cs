using System;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「生产任务书」—— 按班次从当日盘子出正式单据。
///
/// <para>
/// 口径全在 <see cref="TaskOrderModel"/>（纯函数、可脱 GUI 验收）。
/// 单据的命门是「从哪采 → 拉到哪」：卸载地点紧跟作业地点，计划量给实方与吨双口径；
/// <b>缺卸点的任务不得下达</b>，本窗把它们单列一块红字。
/// </para>
/// <para>
/// <b>本窗只出单据，不签发</b>。签发唯一入口是「任务下达」—— 落款处只如实回显那边的结果。
/// 原版这里曾有一个只改内存、明写"不落库"的签发按钮，于是"任务书上已签发"与"任务下达里待下达"
/// 可以同时为真。
/// </para>
/// </summary>
internal sealed class TaskOrderWindow : Window
{
    private static readonly IBrush BadBrush = Brush.Parse("#DC2626");
    private static readonly IBrush OkBrush = Brush.Parse("#16A34A");

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _shift = new() { MinWidth = 110 };
    private readonly TextBlock _meta = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _metrics = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };
    private readonly TextBlock _noSink = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = BadBrush };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };
    private readonly ObservableCollection<OrderRow> _rows = new();

    private PlanAssembly _asm = new();
    private ExploderResult _plan = new();
    private OrderDoc _doc = new();

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal TaskOrderWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "生产任务书";
        Width = 1400; Height = 620;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        BuildColumns();
        _grid.ItemsSource = _rows;
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

        // 62 放得下数字，放不下「序号」两个字的表头（实机截图切成了「序」）——
        // 列宽按表头与格子里最长那句话取大者，这条坑 §三五二 记过一次
        _grid.Columns.Add(C("序号", nameof(OrderRow.No), 78));
        _grid.Columns.Add(C("编组", nameof(OrderRow.Group), 168));
        _grid.Columns.Add(C("作业地点", nameof(OrderRow.Place), 168));
        // ★ 卸载地点紧跟作业地点 —— 单据的命门是「从哪采 → 拉到哪」；缺卸点标红
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Head("卸载地点"), Width = new DataGridLength(178), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<OrderRow>((_, _) =>
            {
                var tb = new TextBlock
                {
                    Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                tb.Bind(TextBlock.TextProperty, new Binding(nameof(OrderRow.Destination)));
                tb.Bind(TextBlock.ForegroundProperty, new Binding(nameof(OrderRow.NoDestination))
                { Converter = new FuncValueConverter<bool, IBrush>(b => b ? BadBrush : Brushes.Black) });
                return tb;
            }, supportsRecycling: true),
        });
        _grid.Columns.Add(C("工序", nameof(OrderRow.Process), 84));
        _grid.Columns.Add(C("物料", nameof(OrderRow.Material), 120));
        _grid.Columns.Add(C("计划量", nameof(OrderRow.Plan), 148));
        _grid.Columns.Add(C("量口径", nameof(OrderRow.Basis), 110));
        _grid.Columns.Add(C("运距", nameof(OrderRow.Haul), 92));
        _grid.Columns.Add(C("时段", nameof(OrderRow.Span), 158));
        _grid.Columns.Add(C("作业人员", nameof(OrderRow.Crew), 110));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("下达状态"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(OrderRow.Issue)),
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
        P(new Border { Width = 10 });
        P(B("重新装配", Reload, tip: "台账或编制参数改过之后按它重出单据"));
        P(B("导出 CSV", ExportCsv));

        var top = new StackPanel
        {
            Children =
            {
                bar,
                new Border
                {
                    Background = Brush.Parse("#F5F7FA"), Padding = new Thickness(12, 6),
                    BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 1),
                    Child = new StackPanel { Children = { _meta, _metrics, _summary, _noSink } },
                },
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
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
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

        var shifts = new System.Collections.Generic.List<string> { "全部" };
        shifts.AddRange(_asm.Config.Shifts.Select(s => s.Name));
        string? keep = _shift.SelectedItem as string;
        _shift.ItemsSource = shifts;
        _shift.SelectedIndex = keep != null && shifts.Contains(keep) ? shifts.IndexOf(keep) : 0;

        Rebind();
    }

    private void Rebind()
    {
        var instances = DispatchStore.LoadInstances();
        string storeErr = DispatchStore.LastError;

        // 作业人员接「班组派工」（§三五七）：没派工仍是「—」，不编名字
        var crew = CrewStore.LoadAssignments();
        _doc = TaskOrderModel.Build(_plan.Tasks, PlanDate, ShiftFilter,
                                    DateTime.Now.TimeOfDay.TotalHours,
                                    key => instances.TryGetValue(key, out var i) ? i.IssueCaption : "待下达",
                                    (shift, equip) => CrewAssignModel.CrewTextOf(crew, PlanDate, shift, equip));

        _rows.Clear();
        foreach (var r in _doc.Rows) _rows.Add(r);

        _meta.Text = _doc.MetaText + "　　" + _asm.SourceLabel;
        _metrics.Text = _doc.MetricsText;
        _summary.Text = _asm.Usable
            ? _doc.SummaryText
            : "排不出来 —— " + string.Join(" ", _asm.Notes);
        _summary.Foreground = _asm.Usable ? Brush.Parse("#555") : BadBrush;

        // 缺卸点单列一块：这几条**不得下达**
        _noSink.IsVisible = _doc.HasMissingSinks;
        _noSink.Text = _doc.HasMissingSinks
            ? "⚠ 以下任务缺卸载地点，不得下达：\n· " + string.Join("\n· ", _doc.MissingSinks)
            : "";

        int issued = _rows.Count(r => r.Issue.StartsWith("✓", StringComparison.Ordinal));
        _status.Text = $"共 {_rows.Count} 项任务，已下达 {issued} 项。本窗只出单据不签发 —— 签发在「任务下达」。"
                     + (storeErr.Length > 0 ? "　⚠ " + storeErr : "");
    }

    private async void ExportCsv()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出生产任务书",
            SuggestedFileName = $"task_order_{PlanDate.Replace("-", "")}_{(ShiftFilter.Length > 0 ? ShiftFilter : "全部")}.csv",
            DefaultExtension = "csv",
        });
        if (f == null) return;
        try
        {
            System.IO.File.WriteAllText(f.Path.LocalPath, TaskOrderModel.ToCsv(_doc), new System.Text.UTF8Encoding(true));
            _status.Text = "已导出 → " + f.Path.LocalPath;
            _echo(_status.Text);
        }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal string MetaText => _meta.Text ?? "";
    internal string MetricsText => _metrics.Text ?? "";
    internal string NoSinkText => _noSink.Text ?? "";
}
