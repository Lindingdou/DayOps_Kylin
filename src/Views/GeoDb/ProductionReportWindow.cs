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
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「生产报告」—— 日/周/月 × 指标的生产报表（移植原 <c>TaskLib.Reporting</c> 四层架构里的
/// <b>取数 → 指标</b> 两层，落在 Kylin 的实绩事实表上）。
///
/// 口径在 <see cref="ProductionReport"/> / <see cref="IndicatorLibrary"/>（纯函数、可脱 GUI 验收）。
/// <b>算不出来一律显示「—」</b>，绝不编数；实绩录入完整度单列一行 ——
/// 只录三天的周报，合计低不是产量低。
///
/// ── 与原版的范围差异（登记）──
/// 原版 <c>ReportHubWindow</c> 是个报表中心：模板设计器、叙述报告、源×汇交叉表、
/// 存档回溯与两期对比、PDF/Word 渲染与打印（二十余个文件、五千余行）。
/// 本轮只移口径两层 + 日周月汇总 + CSV 导出，其余**未移，如实登记**。
/// </summary>
internal sealed class ProductionReportWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal sealed class Row
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Target { get; set; } = "";
        public string Status { get; set; } = "";
        public string Question { get; set; } = "";
        public IBrush Brush { get; set; } = Brushes.Black;
    }

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private readonly ComboBox _cmbPeriod = new() { MinWidth = 100 };
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly ObservableCollection<Row> _rows = new();
    private readonly ObservableCollection<DailyActualRow> _days = new();
    private readonly DataGrid _grid = NewGrid();
    private readonly DataGrid _dayGrid = NewGrid();
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#B26B00") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    private ProductionReportDoc _doc = new();

    private static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal ProductionReportWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "生产报告";
        Width = 1040; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        BuildColumns();
        _grid.ItemsSource = _rows;
        _dayGrid.ItemsSource = _days;
        Content = BuildLayout();

        _cmbPeriod.ItemsSource = new[] { "日报", "周报", "月报" };
        _cmbPeriod.SelectedIndex = 1;
        _cmbPeriod.SelectionChanged += (_, _) => Reload();
        _date.SelectedDateChanged += (_, _) => Reload();
        Reload();
    }

    private void BuildColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };

        _grid.Columns.Add(C("指标", nameof(Row.Name), 140));
        // 值这一列按评价灯上色 —— 逐行上色只能走模板列
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Head("值"), Width = new DataGridLength(110), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var tb = new TextBlock
                { Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
                tb.Bind(TextBlock.TextProperty, new Binding(nameof(Row.Value)));
                tb.Bind(TextBlock.ForegroundProperty, new Binding(nameof(Row.Brush)));
                return tb;
            }, supportsRecycling: true),
        });
        _grid.Columns.Add(C("单位", nameof(Row.Unit), 90));
        _grid.Columns.Add(C("目标", nameof(Row.Target), 90));
        _grid.Columns.Add(C("评价", nameof(Row.Status), 80));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("这个指标回答什么"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(Row.Question)),
        });

        DataGridTextColumn D(string h, string path, string fmt, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) { StringFormat = fmt } };
        _dayGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("日期"), Width = new DataGridLength(120),
            Binding = new Binding(nameof(DailyActualRow.Date)) { StringFormat = "{0:yyyy-MM-dd}" },
        });
        _dayGrid.Columns.Add(D("出煤 t", nameof(DailyActualRow.CoalTotalT), "{0:N1}", 120));
        _dayGrid.Columns.Add(D("折实方 m³", nameof(DailyActualRow.CoalM3), "{0:N1}", 130));
        _dayGrid.Columns.Add(D("剥离 m³实方", nameof(DailyActualRow.StrippingM3), "{0:N1}", 140));
        _dayGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("筒仓存量 t（不计入出煤）"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(DailyActualRow.SiloTotalT)) { StringFormat = "{0:N1}" },
        });
    }

    private Control BuildLayout()
    {
        Button B(string t, Action a, bool bold = false)
        {
            var b = new Button { Content = t, Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 8, 0) };
            if (bold) b.FontWeight = FontWeight.SemiBold;
            b.Click += (_, _) => a();
            return b;
        }

        var top = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 6),
            Children =
            {
                new WrapPanel
                {
                    Margin = new Thickness(0, 0, 0, 6),
                    Children =
                    {
                        new TextBlock { Text = "周期", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                        _cmbPeriod,
                        new TextBlock { Text = "日期", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) },
                        _date,
                        B("今天", () => { _date.SelectedDate = DateTime.Today; }),
                        B("重新生成", Reload),
                        B("导出 CSV", ExportCsv),
                    },
                },
                _title,
                _notes,
            },
        };

        var footBar = new Border
        {
            Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(12, 6, 12, 10),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { B("关闭", Close) },
            },
        };
        var statusBar = new Border { Padding = new Thickness(12, 2), Child = _status };
        var dayBox = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "逐日明细", FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 6, 12, 2) },
                new Border { Height = 150, Margin = new Thickness(12, 0), Child = _dayGrid },
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(dayBox, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(dayBox);
        root.Children.Add(new Border { Margin = new Thickness(12, 0), Child = _grid });
        return root;
    }

    private PeriodKind Kind => _cmbPeriod.SelectedIndex switch
    { 0 => PeriodKind.Day, 2 => PeriodKind.Month, _ => PeriodKind.Week };

    private static IBrush BrushOf(ReportStatus s) => s switch
    {
        ReportStatus.Ok => Brush.Parse("#16A34A"),
        ReportStatus.Warn => Brush.Parse("#D97706"),
        ReportStatus.Bad => Brush.Parse("#DC2626"),
        _ => Brushes.Black,
    };

    internal void Reload()
    {
        var anchor = (_date.SelectedDate?.DateTime ?? DateTime.Today).Date;
        _doc = ProductionReport.Build(_conn(), Kind, anchor);

        _rows.Clear();
        foreach (var v in _doc.Indicators)
            _rows.Add(new Row
            {
                Name = v.Def.Name,
                Value = v.Text,
                Unit = v.Def.Unit,
                Target = v.Target is { } t ? t.ToString("N2", Inv) : "—",
                Status = ProductionReport.StatusCn(v.Status),
                Question = v.Def.Question,
                Brush = BrushOf(v.Status),
            });

        _days.Clear();
        foreach (var d in _doc.Days) _days.Add(d);

        _title.Text = $"{_doc.Title}　（{_doc.From:yyyy-MM-dd} ~ {_doc.To:yyyy-MM-dd}）"
                    + $"　应录 {_doc.TotalDays} 天 · 实录 {_doc.RecordedDays} 天";
        // 口径与完整度提示一条不吞
        _notes.Text = _doc.Notes.Count > 0 ? "◆ " + string.Join("\n◆ ", _doc.Notes) : "";
        _notes.IsVisible = _doc.Notes.Count > 0;
        _status.Text = $"共 {_rows.Count} 项指标；算不出来的显示「—」，不编数。";
    }

    private async void ExportCsv()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出生产报表",
            SuggestedFileName = $"production_report_{_doc.From:yyyyMMdd}_{_doc.To:yyyyMMdd}.csv",
            DefaultExtension = "csv",
        });
        if (f == null) return;
        try
        {
            System.IO.File.WriteAllText(f.Path.LocalPath, ProductionReport.ToCsv(_doc), new System.Text.UTF8Encoding(true));
            _status.Text = $"已导出 → {f.Path.LocalPath}";
            _echo(_status.Text);
        }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }

    // ── 自检钩子用 ──
    internal int IndicatorRowCount => _rows.Count;
    internal int DayRowCount => _days.Count;
    internal string TitleText => _title.Text ?? "";
    internal string NotesText => _notes.Text ?? "";
}
