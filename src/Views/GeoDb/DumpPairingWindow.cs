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
/// 「采排配对」—— 源—汇流向矩阵（行 = 作业面·物料，列 = 去向）+ 各去向库容条 + 本期汇总。
///
/// <para>
/// 口径在 <see cref="DumpPairingModel"/>（纯函数）；汇总直接复用 <see cref="PeriodBalance"/>，
/// <b>不在这里另算一份</b>。
/// </para>
/// <para>
/// <b>本窗不自己做配对</b>，只是把台账里已有的对位关系取过来看 ——
/// 两处各解一次的话，矩阵上显示的与真正排产用的那一份会分叉，而两边各自都自洽。
/// </para>
/// <para>
/// <b>库容按占容方扣</b>（V容 = V实 × Kr）：拿实方去扣会把排土场算得比实际能装得多。
/// </para>
/// </summary>
internal sealed class DumpPairingWindow : Window
{
    private static readonly IBrush BadBrush = Brush.Parse("#DC2626");
    private static readonly IBrush WarnBrush = Brush.Parse("#D97706");

    internal sealed class MatrixRow
    {
        public string Source { get; set; } = "";
        public string Material { get; set; } = "";
        public string Total { get; set; } = "";
        public string Unrouted { get; set; } = "";
        /// <summary>各去向列（按 Sinks 顺序拼成一段文本，列数不定时仍看得全）。</summary>
        public string C0 { get; set; } = "";
        public string C1 { get; set; } = "";
        public string C2 { get; set; } = "";
        public string C3 { get; set; } = "";
        public string Rest { get; set; } = "";
    }

    internal sealed class BarRow
    {
        public string Sink { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Design { get; set; } = "";
        public string Filled { get; set; } = "";
        public string Inbound { get; set; } = "";
        public string Remain { get; set; } = "";
        public string Used { get; set; } = "";
        public string State { get; set; } = "";
    }

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#B26B00") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    private readonly DataGrid _matrix = NewGrid();
    private readonly DataGrid _bars = NewGrid();
    private readonly ObservableCollection<MatrixRow> _rows = new();
    private readonly ObservableCollection<BarRow> _barRows = new();

    private PairingView _view = new();

    private static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal DumpPairingWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "采排配对";
        Width = 1340; Height = 620;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        _matrix.ItemsSource = _rows;
        _bars.ItemsSource = _barRows;
        Content = BuildLayout();
        Reload();
    }

    /// <summary>矩阵的去向列是按数据来的 —— 每次重载都要重建列头。</summary>
    private void BuildMatrixColumns()
    {
        _matrix.Columns.Clear();
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };

        _matrix.Columns.Add(C("作业面", nameof(MatrixRow.Source), 150));
        _matrix.Columns.Add(C("物料", nameof(MatrixRow.Material), 110));
        _matrix.Columns.Add(C("本行合计", nameof(MatrixRow.Total), 124));

        var names = _view.Sinks.Select(s => s.SinkName).Take(4).ToList();
        string[] paths = { nameof(MatrixRow.C0), nameof(MatrixRow.C1), nameof(MatrixRow.C2), nameof(MatrixRow.C3) };
        for (int i = 0; i < names.Count; i++) _matrix.Columns.Add(C(names[i], paths[i], 168));
        if (_view.Sinks.Count > 4)
            _matrix.Columns.Add(C($"其余 {_view.Sinks.Count - 4} 个去向", nameof(MatrixRow.Rest), 220));

        _matrix.Columns.Add(new DataGridTextColumn
        {
            Header = Head("未指派去向"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(MatrixRow.Unrouted)),
        });
    }

    private void BuildBarColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };
        _bars.Columns.Add(C("去向", nameof(BarRow.Sink), 170));
        _bars.Columns.Add(C("种类", nameof(BarRow.Kind), 110));
        _bars.Columns.Add(C("设计 万m³", nameof(BarRow.Design), 124));
        _bars.Columns.Add(C("已填 万m³", nameof(BarRow.Filled), 124));
        _bars.Columns.Add(C("本期入方占容 万m³", nameof(BarRow.Inbound), 178));
        _bars.Columns.Add(C("期末剩余 万m³", nameof(BarRow.Remain), 148));
        _bars.Columns.Add(C("占用率", nameof(BarRow.Used), 96));
        _bars.Columns.Add(new DataGridTextColumn
        {
            Header = Head("结论"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(BarRow.State)),
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
        BuildBarColumns();

        var bar = new WrapPanel { Margin = new Thickness(12, 8, 12, 4) };
        bar.Children.Add(B("重新取数", Reload, tip: "作业面台账或去向台账改过之后按它重取"));
        bar.Children.Add(B("导出 CSV", ExportCsv));

        var top = new StackPanel
        {
            Children =
            {
                bar,
                new Border
                {
                    Background = Brush.Parse("#F5F7FA"), Padding = new Thickness(12, 6),
                    BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 1),
                    Child = new StackPanel { Children = { _summary, _notes } },
                },
            },
        };

        var barBox = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "去向库容（占容方 V容 = V实 × Kr 扣）", FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(12, 6, 12, 2) },
                new Border { Height = 160, Margin = new Thickness(12, 0), Child = _bars },
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
        DockPanel.SetDock(barBox, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(barBox);
        root.Children.Add(new Border { Margin = new Thickness(12, 6), Child = _matrix });
        return root;
    }

    internal void Reload()
    {
        var conn = _conn();
        var flows = SinkInbound.FlowsFromRouting(conn, out string flowErr);
        SinkRegistry? sinks = null;
        try { sinks = SinkRegistryLoader.Load(conn); } catch { sinks = null; }

        _view = DumpPairingModel.Build(flows, sinks, DateTime.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        BuildMatrixColumns();

        _rows.Clear();
        var cols = _view.Sinks.Take(4).ToList();
        foreach (var r in _view.Rows)
        {
            string Cell(int i) => i < cols.Count && r.Cells.TryGetValue(cols[i].SinkId, out var c) ? c.Caption : "—";
            var rest = r.Cells.Where(kv => !cols.Any(c => string.Equals(c.SinkId, kv.Key, StringComparison.OrdinalIgnoreCase)))
                              .Select(kv => $"{kv.Value.SinkName} {kv.Value.InSituM3:N0} m³");
            _rows.Add(new MatrixRow
            {
                Source = r.SourceName,
                Material = r.MaterialName,
                Total = $"{r.InSituM3:N0} m³",
                C0 = Cell(0), C1 = Cell(1), C2 = Cell(2), C3 = Cell(3),
                Rest = string.Join("、", rest),
                Unrouted = r.UnroutedM3 > 1 ? $"⚠ {r.UnroutedM3:N0} m³ 没处去" : "—",
            });
        }

        _barRows.Clear();
        foreach (var b in _view.Sinks)
            _barRows.Add(new BarRow
            {
                Sink = b.SinkName,
                Kind = b.Kind.Label(),
                Design = b.DesignM3 > 1e-6 ? (b.DesignM3 / 1e4).ToString("0.##", CultureInfo.InvariantCulture) : "—",
                Filled = (b.FilledM3 / 1e4).ToString("0.##", CultureInfo.InvariantCulture),
                Inbound = (b.InboundDumpM3 / 1e4).ToString("0.##", CultureInfo.InvariantCulture),
                // ★ 设计库容没录 ⇒ 期末剩余判不了，不写 0（0 会被看成"刚好排满"）
                Remain = b.RemainM3 is { } r ? (r / 1e4).ToString("0.##", CultureInfo.InvariantCulture) : "—",
                Used = b.UsedPct is { } p ? p.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—",
                State = b.Overflow ? "⚠ 本期排不下"
                      : b.RemainM3 == null ? "设计库容未录 ⇒ 判不了（不是「够用」）"
                      : b.Kind.IsDumping() ? "排得下" : "过站，不占排土库容",
            });

        _summary.Text = _view.SummaryCaption;
        var notes = new List<string>(_view.Notes);
        if (flowErr.Length > 0) notes.Insert(0, "作业面台账读不通：" + flowErr);
        _notes.IsVisible = notes.Count > 0;
        _notes.Foreground = _view.Empty || flowErr.Length > 0 ? BadBrush : WarnBrush;
        _notes.Text = notes.Count > 0 ? "◆ " + string.Join("\n◆ ", notes) : "";

        _status.Text = $"源—汇 {_view.Rows.Count} 行 · 去向 {_view.Sinks.Count} 个。"
                     + "本窗只取台账里已有的对位关系，**不自己做配对**；库容按占容方（V容 = V实 × Kr）扣。"
                     + "对位关系取自作业面台账 —— 原版取自采掘单元清单（未移植），两者粒度不同，不能互相冒充。";
    }

    private async void ExportCsv()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出采排配对",
            SuggestedFileName = $"dump_pairing_{_view.Period.Replace("-", "")}.csv",
            DefaultExtension = "csv",
        });
        if (f == null) return;
        try
        {
            System.IO.File.WriteAllText(f.Path.LocalPath, DumpPairingModel.ToCsv(_view), new System.Text.UTF8Encoding(true));
            _status.Text = "已导出 → " + f.Path.LocalPath;
            _echo(_status.Text);
        }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal int SinkCount => _barRows.Count;
    internal string SummaryText => _summary.Text ?? "";
    internal string NotesText => _notes.Text ?? "";
}
