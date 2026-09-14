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
/// 「派车单」—— 把班次任务下沉到<b>车次</b>，这是任务下达最后一公里的可见交付物。
///
/// <para>
/// 任务说的是"这个班采 2450 m³"，司机要的是"我第 3 趟 09:12 到 WK-10 装车、09:26 卸到破碎站"。
/// 中间那一步是车次展开（口径全在 <see cref="DispatchExpand"/>，纯函数）。
/// </para>
/// <para>
/// <b>三口径同屏</b>：载重给【吨】（守恒量）、实方/松方给 m³（松方是车厢容积口径 ——
/// 卡车拉的是爆破后的松散料）、运距给 km。抬头的总运输功 t·km 是评价这班派车合不合理的主指标。
/// </para>
/// </summary>
internal sealed class DispatchOrderWindow : Window
{
    private static readonly IBrush BadBrush = Brush.Parse("#DC2626");

    internal sealed class Row
    {
        public string OrderId { get; set; } = "";
        public string Truck { get; set; } = "";
        public string Driver { get; set; } = "";
        public string Shovel { get; set; } = "";
        public string Trip { get; set; } = "";
        public string Zone { get; set; } = "";
        public string Material { get; set; } = "";
        public string Sink { get; set; } = "";
        public string LoadAt { get; set; } = "";
        public string DumpAt { get; set; } = "";
        public string Cycle { get; set; } = "";
        public string PayloadT { get; set; } = "";
        public string InSitu { get; set; } = "";
        public string Loose { get; set; } = "";
        public string Haul { get; set; } = "";
    }

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _shift = new() { MinWidth = 110 };
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };
    private readonly ObservableCollection<Row> _rows = new();

    private PlanAssembly _asm = new();
    private ExploderResult _plan = new();
    private DispatchPlan _orders = new();

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal DispatchOrderWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "派车单";
        // 1460 在 150% 缩放下 = 2190 物理，右边会被挤出屏幕（实机截图里「运距」列与「关闭」被切）
        Width = 1360; Height = 620;
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

        _grid.Columns.Add(C("车号", nameof(Row.Truck), 96));
        _grid.Columns.Add(C("司机", nameof(Row.Driver), 96));
        _grid.Columns.Add(C("电铲", nameof(Row.Shovel), 100));
        _grid.Columns.Add(C("趟次", nameof(Row.Trip), 84));
        _grid.Columns.Add(C("作业地点", nameof(Row.Zone), 128));
        _grid.Columns.Add(C("物料", nameof(Row.Material), 92));
        _grid.Columns.Add(C("卸点", nameof(Row.Sink), 136));
        _grid.Columns.Add(C("装车", nameof(Row.LoadAt), 78));
        _grid.Columns.Add(C("卸车", nameof(Row.DumpAt), 78));
        _grid.Columns.Add(C("循环 min", nameof(Row.Cycle), 96));
        _grid.Columns.Add(C("载重 t", nameof(Row.PayloadT), 90));
        _grid.Columns.Add(C("实方 m³", nameof(Row.InSitu), 98));
        _grid.Columns.Add(C("松方 m³", nameof(Row.Loose), 98));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("运距 km"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(Row.Haul)),
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
        P(B("重新装配", Reload, tip: "台账或派工改过之后按它重排车次"));
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
                    Child = new StackPanel { Children = { _header, _notes } },
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

        var shifts = new List<string> { "全部" };
        shifts.AddRange(_asm.Config.Shifts.Select(s => s.Name));
        string? keep = _shift.SelectedItem as string;
        _shift.ItemsSource = shifts;
        _shift.SelectedIndex = keep != null && shifts.Contains(keep) ? shifts.IndexOf(keep) : 0;

        Rebind();
    }

    private void Rebind()
    {
        // 司机来自「班组派工」的车号↔司机显式配对；没派工留空，不编名字
        var crew = CrewStore.LoadAssignments();
        string DriverOf(string shift, string truck)
        {
            foreach (var a in crew.Values)
            {
                if (!string.Equals(a.PlanDate, PlanDate, StringComparison.Ordinal)) continue;
                if (shift.Length > 0 && !string.Equals(a.Shift, shift, StringComparison.Ordinal)) continue;
                var hit = a.TruckDrivers.FirstOrDefault(td => string.Equals(td.TruckId, truck, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit.Name;
            }
            return "";
        }

        _orders = DispatchExpand.Expand(_plan.Tasks, PlanDate, ShiftFilter, DriverOf);

        _rows.Clear();
        foreach (var o in _orders.Orders.OrderBy(o => o.PlannedLoadHour).ThenBy(o => o.TruckId, StringComparer.Ordinal))
            _rows.Add(new Row
            {
                OrderId = o.OrderId,
                Truck = o.TruckId,
                Driver = o.Driver.Length > 0 ? o.Driver : "—",
                Shovel = o.ShovelId,
                Trip = "第 " + o.TripNo + " 趟",
                Zone = o.WorkZone,
                Material = MaterialCatalog.Resolve(o.MaterialCode).Name,
                Sink = o.SinkName.Length > 0 ? o.SinkName : o.SinkId.Length > 0 ? o.SinkId : "未定卸点",
                LoadAt = DispatchExpand.Hm(o.PlannedLoadHour),
                DumpAt = DispatchExpand.Hm(o.PlannedDumpHour),
                Cycle = o.CycleMin.ToString("0.#", CultureInfo.InvariantCulture),
                PayloadT = o.PayloadT.ToString("N1", CultureInfo.InvariantCulture),
                InSitu = o.PayloadInSituM3.ToString("N1", CultureInfo.InvariantCulture),
                Loose = o.PayloadLooseM3.ToString("N1", CultureInfo.InvariantCulture),
                Haul = o.HaulKm > 1e-6 ? o.HaulKm.ToString("0.##", CultureInfo.InvariantCulture) : "—",
            });

        _header.Text = $"{PlanDate}　{_orders.Caption}　|　{_asm.SourceLabel}";
        _notes.Foreground = _asm.Usable ? Brush.Parse("#555") : BadBrush;
        _notes.Text = _asm.Usable
            ? (_orders.Notes.Count > 0 ? "◆ " + string.Join("\n◆ ", _orders.Notes) : "")
            : "排不出来 —— " + string.Join(" ", _asm.Notes);
        _status.Text = $"共 {_rows.Count} 条派车指令。载重按吨（守恒量），实方/松方由它按物料折；"
                     + "一趟算数的条件是能把料卸掉，车次数再按目标量封顶 —— 派车指令是承诺，半趟不是一车料。";
    }

    private async void ExportCsv()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出派车单",
            SuggestedFileName = $"dispatch_orders_{PlanDate.Replace("-", "")}_{(ShiftFilter.Length > 0 ? ShiftFilter : "全部")}.csv",
            DefaultExtension = "csv",
        });
        if (f == null) return;
        try
        {
            System.IO.File.WriteAllText(f.Path.LocalPath, DispatchExpand.ToCsv(_orders), new System.Text.UTF8Encoding(true));
            _status.Text = "已导出 → " + f.Path.LocalPath;
            _echo(_status.Text);
        }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal string HeaderText => _header.Text ?? "";
    internal string NotesText => _notes.Text ?? "";
}
