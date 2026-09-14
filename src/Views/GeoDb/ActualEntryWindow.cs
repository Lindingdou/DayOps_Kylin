using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「实绩录入」—— 把当天实际干了多少灌回台账（<c>daily_mine_summary</c>）。
///
/// <b>为什么要有它</b>：§三三六 的周计划早就在**读**这张表算实绩与达成度，
/// 但一直没有**写**的入口 —— 表空着，那几列就永远显示「—」。本窗把这一头补上。
///
/// ── 口径 ──
/// 「当日出煤合计」= <see cref="WeekPlanSource.CoalColumns"/> 六列相加（**筒仓是库存不是产出，不计**），
/// 折方按 <see cref="WeekPlanSource.CoalDensity"/> —— 与周计划**同一处口径**，不另抄一份。
///
/// ── 排弃回灌（原版那条纪律照搬）──
/// 剥离实绩可回灌进排土场库容，<b>按占容方 V容 = V实 × Kr 扣，且只补增量</b> ——
/// 同一天重复保存不会重复记账。
/// </summary>
internal sealed class ActualEntryWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Func<DbConnection?> _conn;
    private readonly Func<SinkRegistry?> _sinks;
    private readonly Action<string> _echo;

    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly Dictionary<string, TextBox> _tb = new(StringComparer.Ordinal);
    private readonly ComboBox _cmbSink = new() { MinWidth = 220 };
    private readonly CheckBox _chkBackfill = new()
    { Content = "把剥离回灌进排土场库容（按占容方，只补增量）", Margin = new Thickness(0, 4, 0, 0) };

    private readonly TextBlock _total = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _basis = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#666") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    /// <summary>这一天此前已回灌过的占容方 m³（本会话内记，避免同一天重复扣）。</summary>
    private readonly Dictionary<DateTime, double> _dumped = new();

    internal ActualEntryWindow(Func<DbConnection?> conn, Func<SinkRegistry?> sinks, Action<string> echo)
    {
        _conn = conn; _sinks = sinks; _echo = echo;
        Title = "实绩录入";
        Width = 720; Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        Content = BuildLayout();
        _date.SelectedDateChanged += (_, _) => LoadDay();
        LoadDay();
    }

    private TextBox Field(string key)
    {
        var t = new TextBox { Width = 110, Padding = new Thickness(6, 3), HorizontalContentAlignment = HorizontalAlignment.Right };
        t.TextChanged += (_, _) => RefreshTotal();
        _tb[key] = t;
        return t;
    }

    private Control Cell(string label, string key) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 18, 6),
        Children =
        {
            new TextBlock
            {
                Text = label, Width = 116, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
            },
            Field(key),
        },
    };

    private static Border Group(string header, Control body) => new()
    {
        BorderBrush = Brush.Parse("#D0D0D0"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 8),
        Child = new StackPanel
        {
            Children =
            {
                new Border
                {
                    Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(10, 4),
                    Child = new TextBlock { Text = header, FontWeight = FontWeight.SemiBold },
                },
                new Border { Padding = new Thickness(10, 8), Child = body },
            },
        },
    };

    private Control BuildLayout()
    {
        Button B(string t, Action a, bool bold = false)
        {
            var b = new Button { Content = t, Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 8, 0) };
            if (bold) b.FontWeight = FontWeight.SemiBold;
            b.Click += (_, _) => a();
            return b;
        }

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 8),
            Children =
            {
                new WrapPanel
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Children =
                    {
                        new TextBlock { Text = "日期", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                        _date,
                        B("今天", () => { _date.SelectedDate = DateTime.Today; }),
                        B("前一天", () => Shift(-1)),
                        B("后一天", () => Shift(1)),
                    },
                },

                Group("出煤（外运通道，t）", new StackPanel
                {
                    Children =
                    {
                        new WrapPanel { Children = { Cell("大皮带 t", "BigBelt"), Cell("小皮带 t", "SmallBelt"), Cell("龙华 t", "Longhua") } },
                        new WrapPanel { Children = { Cell("汽车外运 t", "TruckExport"), Cell("风选 t", "Winnowed"), Cell("大车堆煤 t", "BigTruckPile") } },
                    },
                }),

                Group("剥离与筒仓", new StackPanel
                {
                    Children =
                    {
                        new WrapPanel { Children = { Cell("剥离 m³实方", "Stripping") } },
                        new WrapPanel { Children = { Cell("1号筒仓 t", "Silo1"), Cell("2号筒仓 t", "Silo2"), Cell("3号筒仓 t", "Silo3") } },
                        new TextBlock
                        {
                            // UI 文案里不要写 Markdown 记号：TextBlock 会原样显示出星号（§三三八 已犯过一次）
                            Text = "筒仓是库存、不是产出，不计入当日出煤合计。",
                            FontSize = 11, Foreground = Brush.Parse("#888"), TextWrapping = TextWrapping.Wrap,
                        },
                    },
                }),

                Group("排弃回灌（可选）", new StackPanel
                {
                    Children =
                    {
                        new WrapPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = "排土场", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                                _cmbSink,
                            },
                        },
                        _chkBackfill,
                    },
                }),

                Group("合计（实时）", new StackPanel { Children = { _total, _basis } }),
            },
        };

        var footBar = new Border
        {
            Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(12, 6, 12, 10),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { B("删除这一天", DeleteDay), B("保存", SaveDay, bold: true), B("关闭", Close) },
            },
        };
        var statusBar = new Border { Padding = new Thickness(12, 2), Child = _status };

        var root = new DockPanel();
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(new ScrollViewer { Content = body });
        return root;
    }

    private void Shift(int days)
    {
        var d = _date.SelectedDate?.DateTime ?? DateTime.Today;
        _date.SelectedDate = new DateTimeOffset(d.AddDays(days));
    }

    private DateTime Day => (_date.SelectedDate?.DateTime ?? DateTime.Today).Date;

    private static double D(TextBox t)
        => double.TryParse(t.Text, NumberStyles.Float, Inv, out double v) ? v : 0;

    private DailyActualRow ReadUi() => new()
    {
        Date = Day,
        BigBelt = D(_tb["BigBelt"]), SmallBelt = D(_tb["SmallBelt"]), Longhua = D(_tb["Longhua"]),
        TruckExport = D(_tb["TruckExport"]), Winnowed = D(_tb["Winnowed"]), BigTruckPile = D(_tb["BigTruckPile"]),
        StrippingM3 = D(_tb["Stripping"]),
        Silo1 = D(_tb["Silo1"]), Silo2 = D(_tb["Silo2"]), Silo3 = D(_tb["Silo3"]),
    };

    private void LoadDay()
    {
        // 排土场下拉每次重取：台账可能刚在「去向台账」里改过
        var sinks = _sinks();
        var dumps = sinks?.All.Where(s => s.IsDumping).OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList()
                    ?? new List<SinkNode>();
        string keep = _cmbSink.SelectedItem as string ?? "";
        var names = dumps.Select(s => s.Name).ToList();
        _cmbSink.ItemsSource = names;
        if (names.Contains(keep)) _cmbSink.SelectedItem = keep;
        else if (names.Count > 0) _cmbSink.SelectedIndex = 0;
        _chkBackfill.IsEnabled = names.Count > 0;

        var row = DailyActuals.Load(_conn(), Day);
        void P(string key, double v) => _tb[key].Text = v == 0 ? "" : v.ToString("0.####", Inv);
        if (row == null)
        {
            foreach (var t in _tb.Values) t.Text = "";
            // 「没录」与「录了 0」要分得开：没录就留空，不预填 0
            SetStatus($"{Day:yyyy-MM-dd} 还没有实绩记录 —— 填好点「保存」。", null);
        }
        else
        {
            P("BigBelt", row.BigBelt); P("SmallBelt", row.SmallBelt); P("Longhua", row.Longhua);
            P("TruckExport", row.TruckExport); P("Winnowed", row.Winnowed); P("BigTruckPile", row.BigTruckPile);
            P("Stripping", row.StrippingM3);
            P("Silo1", row.Silo1); P("Silo2", row.Silo2); P("Silo3", row.Silo3);
            SetStatus($"已载入 {Day:yyyy-MM-dd} 的实绩。", null);
        }
        RefreshTotal();
    }

    private void RefreshTotal()
    {
        var r = ReadUi();
        _total.Text = $"当日出煤 {r.CoalTotalT:N1} t（折 {r.CoalM3:N1} m³实方）"
                    + $"　剥离 {r.StrippingM3:N1} m³实方"
                    + $"　筒仓存量 {r.SiloTotalT:N1} t（不计入出煤）";
        _basis.Text = WeekPlanSource.CoalBasisText
                    + $"；折方按煤密度 {WeekPlanSource.CoalDensity.ToString("0.##", Inv)} t/m³（与周计划同一处口径）";
    }

    private void SetStatus(string text, string? color)
    {
        _status.Text = text;
        _status.Foreground = Brush.Parse(color ?? "#555");
    }

    private void SaveDay()
    {
        var row = ReadUi();
        string err = DailyActuals.Save(_conn(), row);
        if (err.Length > 0) { SetStatus("保存失败：" + err, "#DC2626"); return; }

        string msg = $"已保存 {row.Date:yyyy-MM-dd} 实绩：出煤 {row.CoalTotalT:N1} t · 剥离 {row.StrippingM3:N1} m³";

        if (_chkBackfill.IsChecked == true && _cmbSink.SelectedItem is string sinkName)
        {
            var sinks = _sinks();
            var s = sinks?.All.FirstOrDefault(x => string.Equals(x.Name, sinkName, StringComparison.Ordinal));
            if (s == null) msg += "　⚠ 回灌跳过：找不到该排土场";
            else
            {
                _dumped.TryGetValue(row.Date, out double already);
                double added = DailyActuals.BackfillDump(_conn(), sinks, s.Id, row.StrippingM3, already);
                if (added > 1e-9)
                {
                    _dumped[row.Date] = already + added;
                    msg += $"　已回灌 {s.Name} {added / 1e4:0.##} 万m³占容（充填 {s.FillRate * 100:0.#}%）";
                }
                else msg += "　（本日排弃已回灌过，未重复扣）";
            }
        }

        SetStatus(msg, "#16A34A");
        _echo(msg);
    }

    private void DeleteDay()
    {
        string err = DailyActuals.Delete(_conn(), Day);
        if (err.Length > 0) { SetStatus("删除失败：" + err, "#DC2626"); return; }
        _dumped.Remove(Day);
        SetStatus($"已删除 {Day:yyyy-MM-dd} 的实绩记录。", null);
        _echo($"已删除 {Day:yyyy-MM-dd} 的实绩记录");
        LoadDay();
    }

    // ── 自检钩子用 ──
    internal string TotalText => _total.Text ?? "";
    internal string StatusTextValue => _status.Text ?? "";
    internal void SetDay(DateTime d) { _date.SelectedDate = new DateTimeOffset(d); }
}
